using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// Puts a RollingCandle on its indicator's price chart: one candle just right of the latest bar, half as wide again
/// as the chart's own candles by default (a setting), green with the close at or above the open and red below, its
/// window over it ("10s") and the window's volume under it. Its colour is as strong as its volume against the
/// stock's typical volume for a window that long (VolumeBrightness): half at typical, full at twice typical, a
/// quarter's opacity when quiet. That's the candle's brightest; down it each price is shaded by the shares traded
/// there (VolumeAtPrice), in one smooth gradient: the busiest price brightest, prices under the least shares (1,000)
/// or with none at a quarter's opacity, so it shows where volume was and wasn't traded. Each price is green where it
/// was bought and red where it was sold, by the tape's own colouring (the print's AggressorFlag), so big selling
/// inside a green candle shows red there. The wick is a 1-pixel line, as on the chart's own candles. Past full
/// colour its busiest prices run hotter and hotter, toward white (VolumeBrightness.Hot; your feedback, 2026-11-19:
/// abnormal volume didn't stand out enough), and in a burst (over 4 times typical) a glow grows around the body only
/// (your feedback, 2026-11-18: never over the wick). A window under the least shares is all at a
/// quarter, with no glow. It takes the chart symbol's prints as they arrive (Symbol.NewLast) and, four times a
/// second, asks the chart to redraw when the candle has changed, so trades roll out of the window even when nothing
/// trades. Used by the Rolling Candle indicator and by Key Moving Averages, so a Free licence's two indicators a
/// chart can still have it.
/// </summary>
internal sealed class RollingCandleView
{
    private const int RedrawEveryMs = 250;
    private static readonly Color Up = CandleShade.Up;
    private static readonly Color Down = CandleShade.Down;
    private static readonly Color Label = Color.FromArgb(200, 200, 200);

    private readonly Indicator owner;
    private readonly Func<DateTime?> latestBarTime;
    private readonly RollingCandle candle = new(TimeSpan.FromSeconds(10));
    private readonly VolumeBrightness brightness = new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
    private readonly object gate = new();
    private Symbol symbol;
    private Timer timer;
    private Look painted = new(RollingBar.None, 0, 0);
    private int seconds = 10;
    private double fullMultiple = 2;
    private double glowMultiple = 4;
    private double minShares = VolumeAtPrice.DefaultMinShares;
    private float widthScale = 1.5f;

    // What the chart's Symbol.NewLast delivered in the current minute, logged once a minute to Quantower's log, to
    // check against the prints the connection sent for the symbol (your report, 2026-11-18: highs and lows jumped
    // more than a full tape's window would; replaying captures says a full tape steps little).
    private long printsSeen;
    private int generation;
    private double sharesSeen;
    private long nextReportTicks;
    /// <summary>
    /// The candle as drawn: its bar, colour alpha, glow heat and how hot its busiest prices run, both in twentieths
    /// (so a redraw waits for a visible change).
    /// </summary>
    private readonly record struct Look(RollingBar Bar, int Alpha, int Heat, int Hot = 0);

    /// <param name="owner">The indicator whose chart it draws on.</param>
    /// <param name="latestBarTime">The open time of the chart's latest bar, or null with no bars yet.</param>
    public RollingCandleView(Indicator owner, Func<DateTime?> latestBarTime)
    {
        this.owner = owner;
        this.latestBarTime = latestBarTime;
    }

    /// <summary>
    /// Starts over on this symbol with a window of this many seconds, judged against the typical volume of the last
    /// typicalMinutes: full colour at fullMultiple times typical, glowing beyond glowMultiple, prices (or a window)
    /// under minShares at the lowest intensity, and widthScale times as wide as the chart's own candles.
    /// </summary>
    public void Start(Symbol chartSymbol, int windowSeconds, int typicalMinutes, double fullMultiple, double glowMultiple, double minShares, double widthScale)
    {
        this.Stop();
        this.seconds = Math.Max(1, windowSeconds);
        this.candle.Window = TimeSpan.FromSeconds(this.seconds);
        this.candle.Clear();
        this.brightness.Window = TimeSpan.FromSeconds(this.seconds);
        this.brightness.Lookback = TimeSpan.FromMinutes(Math.Max(1, typicalMinutes));
        this.brightness.Clear();
        this.fullMultiple = fullMultiple;
        this.glowMultiple = glowMultiple;
        this.minShares = Math.Max(0, minShares);
        this.widthScale = (float)Math.Clamp(widthScale, 0.2, 10);
        if (chartSymbol == null)
            return;

        long started = DateTime.UtcNow.Ticks;
        int generation;
        lock (this.gate)
        {
            this.symbol = chartSymbol;
            generation = ++this.generation;
            this.brightness.Begin(started);
            chartSymbol.NewLast += this.OnNewLast;
            this.timer = new Timer(_ => this.OnTimer(), null, RedrawEveryMs, RedrawEveryMs);
        }

        this.SeedFromHistory(chartSymbol, started, generation);
    }

    /// <summary>
    /// Off Quantower's thread: typical volume to start from, the shares in the 10-second bars of the lookback before
    /// started, so the candle's brightness means something from its first trade instead of after 20 seconds and a few
    /// jumpy minutes. Dropped if the candle has started over since, or the bars can't be had (it then starts as before).
    /// </summary>
    private void SeedFromHistory(Symbol chartSymbol, long started, int generation)
    {
        var lookback = this.brightness.Lookback;
        _ = Task.Run(() =>
        {
            try
            {
                var from = new DateTime(started, DateTimeKind.Utc) - lookback;
                double shares = 0;
                int bars = 0;
                using (var history = chartSymbol.GetHistory(new Period(BasePeriod.Second, 10), chartSymbol.HistoryType, from))
                {
                    for (int i = 0; i < history.Count; i++)
                    {
                        if (history[i, SeekOriginHistory.Begin] is HistoryItemBar bar && bar.TimeLeft >= from && bar.TimeLeft.Ticks < started && bar.Volume > 0)
                        {
                            shares += bar.Volume;
                            bars++;
                        }
                    }
                }

                lock (this.gate)
                {
                    if (generation != this.generation)
                        return;
                    this.brightness.Seed(shares, lookback.TotalSeconds);
                }

                Core.Instance.Loggers.Log($"Rolling candle: {chartSymbol.Name} typical volume seeded from {bars} 10-second bars, {shares:#,0} shares over {lookback.TotalMinutes:0} minutes", LoggingLevel.System);
            }
            catch (Exception)
            {
                // No bars to be had: typical builds up from live trades alone.
            }
        });
    }

    public void Stop()
    {
        lock (this.gate)
        {
            if (this.symbol != null)
                this.symbol.NewLast -= this.OnNewLast;
            this.symbol = null;
            this.timer?.Dispose();
            this.timer = null;
        }

        this.candle.Clear();
        this.brightness.Clear();
    }

    private void OnNewLast(Symbol from, Last last)
    {
        long now = DateTime.UtcNow.Ticks;
        // Who took it, as the tape colours it: ScalpFeed marks a print Buy at or above the ask, Sell at or below the bid.
        var taker = last.AggressorFlag == AggressorFlag.Buy ? PrintTaker.Buyer
            : last.AggressorFlag == AggressorFlag.Sell ? PrintTaker.Seller
            : PrintTaker.Between;
        this.candle.Add(now, last.Price, last.Size, taker);
        // Typical volume, like the candle's brightness, counts only what was bought or sold, not prints between;
        // every print counts toward the share that's bought or sold, which scales the seed from history.
        if (last.Price > 0)
            this.brightness.Add(now, last.Size, taker != PrintTaker.Between);

        lock (this.gate)
        {
            this.printsSeen++;
            this.sharesSeen += last.Size;
        }
    }

    /// <summary>Once a minute: how many prints and shares Symbol.NewLast brought, into Quantower's log.</summary>
    private void ReportPrints(long nowTicks)
    {
        long prints;
        double shares;
        string name;
        lock (this.gate)
        {
            if (this.nextReportTicks == 0)
                this.nextReportTicks = nowTicks + TimeSpan.TicksPerMinute;
            if (nowTicks < this.nextReportTicks)
                return;

            this.nextReportTicks = nowTicks + TimeSpan.TicksPerMinute;
            prints = this.printsSeen;
            shares = this.sharesSeen;
            this.printsSeen = 0;
            this.sharesSeen = 0;
            name = this.symbol?.Name;
        }

        try
        {
            Core.Instance.Loggers.Log($"Rolling candle {name}: {prints} prints, {shares:0} shares from NewLast in the last minute", LoggingLevel.System);
        }
        catch
        {
            // The count must never stop the candle.
        }
    }

    /// <summary>The candle now, its colour's alpha (its brightest), and its glow; into levels, when given, its shares at each price.</summary>
    private Look TakeLook(List<PriceLevel> levels = null)
    {
        long now = DateTime.UtcNow.Ticks;
        var bar = this.candle.At(now, levels);
        if (bar.IsEmpty)
            return new Look(bar, 0, 0);
        if (!(bar.Taken >= this.minShares))
            return new Look(bar, VolumeBrightness.Alpha(0), 0);

        double ratio = this.brightness.Ratio(now, bar.Taken);
        int alpha = VolumeBrightness.Alpha(VolumeBrightness.Strength(ratio, this.fullMultiple));
        int heat = (int)Math.Round(VolumeBrightness.Heat(ratio, this.glowMultiple) * 20);
        int hot = (int)Math.Round(VolumeBrightness.Hot(ratio, this.fullMultiple, this.glowMultiple) * 20);
        return new Look(bar, alpha, heat, hot);
    }

    private void OnTimer()
    {
        this.ReportPrints(DateTime.UtcNow.Ticks);
        var now = this.TakeLook();
        Look shown;
        lock (this.gate)
            shown = this.painted;
        if (now == shown)
            return;

        try
        {
            this.owner.CurrentChart?.RedrawBuffer();
        }
        catch (Exception)
        {
            // A chart closing as the timer fires; the next tick, or none, will do.
        }
    }

    public void Paint(PaintChartEventArgs args)
    {
        var chart = this.owner.CurrentChart;
        var window = chart?.MainWindow;
        var graphics = args?.Graphics;
        if (window == null || graphics == null)
            return;

        var levels = new List<PriceLevel>();
        var look = this.TakeLook(levels);
        var bar = look.Bar;
        lock (this.gate)
            this.painted = look;
        if (bar.IsEmpty)
            return;

        var converter = window.CoordinatesConverter;
        Rectangle area = window.ClientRectangle;
        float barWidth = Math.Max(1f, (float)chart.BarsWidth);

        // The chart's own candles are about 0.7 of a bar wide; this one's widthScale times that, an odd width so the
        // wick sits in the body's middle.
        int body = Math.Max(5, (int)MathF.Round(barWidth * 0.7f * this.widthScale)) | 1;

        // Two bar widths right of the latest bar, kept inside the chart when it's scrolled back or tight on the right.
        float x = this.latestBarTime() is DateTime latest ? (float)converter.GetChartX(latest) + barWidth * 2 : area.Right - body * 2;
        float left = area.Left + body;
        float right = area.Right - body - 2;
        int centre = (int)MathF.Round(right > left ? Math.Clamp(x, left, right) : area.Right - body);
        int bodyLeft = centre - (body / 2);

        // Whole pixel rows, from the one the high is on to the one the low is on.
        int Row(double price) => (int)Math.Clamp(Math.Floor(converter.GetChartY(price)), area.Top - 1, area.Bottom + 1);
        int rowHigh = Row(bar.High);
        int rowLow = Math.Max(rowHigh, Row(bar.Low));
        int bodyTop = Math.Clamp(Math.Min(Row(bar.Open), Row(bar.Close)), rowHigh, rowLow);
        int bodyBottom = Math.Clamp(Math.Max(Row(bar.Open), Row(bar.Close)), bodyTop, rowLow);
        var stops = VolumeAtPrice.Stops(levels, price => converter.GetChartY(price), rowHigh, rowLow + 1, this.minShares, look.Alpha);

        var state = graphics.Save();
        try
        {
            graphics.SetClip(area);
            var hue = bar.Close >= bar.Open ? Up : Down;
            float heat = look.Heat / 20f;
            float hot = look.Hot / 20f;

            // A burst: a soft glow around the body only, wider and stronger the bigger the burst, faintest and
            // widest layer first.
            if (heat > 0)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                const int Layers = 5;
                for (int layer = Layers; layer >= 1; layer--)
                {
                    float spread = (2.5f + (7f * heat)) * layer / Layers;
                    using var glow = new SolidBrush(Color.FromArgb((int)((30 + (60 * heat)) * (Layers + 1 - layer) / Layers), hue));
                    using var halo = Rounded(bodyLeft - spread, bodyTop - spread, body + (2 * spread), bodyBottom + 1 - bodyTop + (2 * spread), spread + 1);
                    graphics.FillPath(glow, halo);
                }
            }

            // The candle: shaded row by row top to bottom (CandleShade), a 1-pixel wick outside the body and the
            // body between the open and the close. In a burst the busiest prices run hot, toward white.
            graphics.SmoothingMode = SmoothingMode.None;
            var shade = CandleShade.Rows(stops, rowHigh, rowLow + 1, hue, hot, look.Alpha);
            CandleShade.Fill(graphics, shade, rowHigh, centre, 1, rowHigh, bodyTop);
            CandleShade.Fill(graphics, shade, rowHigh, centre, 1, bodyBottom + 1, rowLow + 1);
            CandleShade.Fill(graphics, shade, rowHigh, bodyLeft, body, bodyTop, bodyBottom + 1);

            // In a burst the volume reads in the candle's colour, in bold.
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 8f, heat > 0 ? FontStyle.Bold : FontStyle.Regular);
            using var text = new SolidBrush(heat > 0 ? hue : Label);
            string over = $"{this.seconds}s";
            var size = graphics.MeasureString(over, font);
            graphics.DrawString(over, font, text, centre + 0.5f - size.Width / 2, rowHigh - size.Height - 1);
            string under = RollingCandle.FormatVolume(bar.Volume);
            size = graphics.MeasureString(under, font);
            graphics.DrawString(under, font, text, centre + 0.5f - size.Width / 2, rowLow + 2);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    /// <summary>A rectangle with corners rounded by radius, for a glow that hugs the body rather than boxing it.</summary>
    private static GraphicsPath Rounded(float left, float top, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(Math.Min(width, height), radius * 2);
        if (d < 1)
        {
            path.AddRectangle(new RectangleF(left, top, width, height));
            return path;
        }

        path.AddArc(left, top, d, d, 180, 90);
        path.AddArc(left + width - d, top, d, d, 270, 90);
        path.AddArc(left + width - d, top + height - d, d, d, 0, 90);
        path.AddArc(left, top + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
