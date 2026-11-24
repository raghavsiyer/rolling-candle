using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;

namespace ScalpFeedIndicators;

/// <summary>
/// Puts a RollingCandle on its indicator's price chart: one candle just right of the latest bar, half as wide again
/// as the chart's own candles by default (a setting), green with the close at or above the open and red below, its
/// rate of change over it ("+2.4%", green up and red down; your request, 2026-11-24, in place of "10s"), an exponential
/// average with the window as its time constant (RollingCandle.Roc), and its window's volume under it. Its brightness,
/// glow and shading go by volume that fades the same way (FadingVolume; your choice, 2026-11-24), its shape and the
/// volume under it by the window's trades. Its colour is as strong as its volume against the
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

    // A burst's blotch: brighter than the candle's own colours, so it stands out on a candle of the same side.
    private static readonly Color BurstUp = Color.FromArgb(0, 255, 150);
    private static readonly Color BurstDown = Color.FromArgb(255, 60, 60);

    private readonly Indicator owner;
    private readonly Func<DateTime?> latestBarTime;
    private readonly RollingCandle candle = new(TimeSpan.FromSeconds(10));
    private readonly FadingVolume fading = new(TimeSpan.FromSeconds(10));
    private readonly VolumeBrightness brightness = new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
    private readonly TapeBursts bursts = new();
    private readonly object gate = new();
    private Symbol symbol;
    private Timer timer;
    private Look painted = new(RollingBar.None, 0, 0);
    private int seconds = 10;
    private double fullMultiple = 2;
    private double glowMultiple = 4;
    private double minShares = VolumeAtPrice.DefaultMinShares;
    private float widthScale = 1.5f;
    private bool shade = true;
    private bool showBursts = true;
    private double burstMultiple = 3;
    private double burstMinShares = 1000;

    // What the chart's Symbol.NewLast delivered in the current minute, logged once a minute to Quantower's log, to
    // check against the prints the connection sent for the symbol (your report, 2026-11-18: highs and lows jumped
    // more than a full tape's window would; replaying captures says a full tape steps little).
    private long printsSeen;
    private int generation;
    private double sharesSeen;
    private long nextReportTicks;
    /// <summary>
    /// The candle as drawn: its bar, colour alpha, glow heat and how hot its busiest prices run, both in twentieths
    /// (so a redraw waits for a visible change); while a burst's blotch is fading, the redraw tick, so it's redrawn; and
    /// the rate of change as it reads, which fades with no trades, as do the prices with enough shares to show (Lit).
    /// </summary>
    private readonly record struct Look(RollingBar Bar, int Alpha, int Heat, int Hot = 0, long Frame = 0, string Roc = "", int Lit = 0);

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
    /// under minShares at the lowest intensity, and widthScale times as wide as the chart's own candles. Without
    /// shade the candle is one plain colour. With showBursts, the tape's bursts (TapeBursts) are blotches on it: a
    /// side's prints each within burstGapMs of the last, burstMultiple times the typical rate bought or sold and at
    /// least burstMinShares.
    /// </summary>
    public void Start(Symbol chartSymbol, int windowSeconds, int typicalMinutes, double fullMultiple, double glowMultiple, double minShares, double widthScale,
        bool shade = true, bool showBursts = true, int burstGapMs = 1000, double burstMultiple = 3, int burstMinShares = 1000)
    {
        this.Stop();
        this.seconds = Math.Max(1, windowSeconds);
        this.candle.Window = TimeSpan.FromSeconds(this.seconds);
        this.candle.Clear();
        this.fading.Window = TimeSpan.FromSeconds(this.seconds);
        this.fading.Clear();
        this.brightness.Window = TimeSpan.FromSeconds(this.seconds);
        this.brightness.Lookback = TimeSpan.FromMinutes(Math.Max(1, typicalMinutes));
        this.bursts.Gap = TimeSpan.FromMilliseconds(Math.Max(10, burstGapMs));
        this.bursts.Keep = TimeSpan.FromSeconds(this.seconds);
        this.bursts.Clear();
        this.shade = shade;
        this.showBursts = showBursts;
        this.burstMultiple = Math.Max(0, burstMultiple);
        this.burstMinShares = Math.Max(0, burstMinShares);
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
                var seed = new List<(long EndTicks, double Shares)>();
                var barLength = TimeSpan.FromSeconds(10);
                using (var history = chartSymbol.GetHistory(new Period(BasePeriod.Second, 10), chartSymbol.HistoryType, from))
                {
                    for (int i = 0; i < history.Count; i++)
                    {
                        if (history[i, SeekOriginHistory.Begin] is HistoryItemBar bar && bar.TimeLeft >= from && bar.TimeLeft.Ticks < started && bar.Volume > 0)
                        {
                            seed.Add(((bar.TimeLeft + barLength).Ticks, bar.Volume));
                            shares += bar.Volume;
                            bars++;
                        }
                    }
                }

                lock (this.gate)
                {
                    if (generation != this.generation)
                        return;
                    this.brightness.Seed(seed);
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
        this.fading.Clear();
        this.brightness.Clear();
        this.bursts.Clear();
    }

    private void OnNewLast(Symbol from, Last last)
    {
        long now = DateTime.UtcNow.Ticks;
        // Who took it, as the tape colours it: ScalpFeed marks a print Buy at or above the ask, Sell at or below the bid.
        var taker = last.AggressorFlag == AggressorFlag.Buy ? PrintTaker.Buyer
            : last.AggressorFlag == AggressorFlag.Sell ? PrintTaker.Seller
            : PrintTaker.Between;
        this.candle.Add(now, last.Price, last.Size, taker);
        this.fading.Add(now, last.Price, last.Size, taker);
        this.bursts.Add(now, last.Price, last.Size, taker);
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

    /// <summary>The candle now, its colour's alpha (its brightest), and its glow; into levels, when given, the fading shares at each of its prices.</summary>
    private Look TakeLook(List<PriceLevel> levels = null, List<TapeBurst> burstsNow = null)
    {
        long now = DateTime.UtcNow.Ticks;
        var bar = this.candle.At(now);
        long frame = 0;
        if (this.showBursts)
        {
            double perSecond = this.brightness.Typical(now) / this.seconds;
            var found = this.bursts.At(now, perSecond, this.burstMultiple, this.burstMinShares);
            burstsNow?.AddRange(found);
            if (found.Count > 0)
                frame = now / (RedrawEveryMs * TimeSpan.TicksPerMillisecond);
        }

        if (bar.IsEmpty)
            return new Look(bar, 0, 0, 0, frame);

        string roc = RollingCandle.FormatRoc(this.candle.Roc(now));
        levels ??= [];
        this.fading.Levels(now, bar.Low, bar.High, levels);
        int lit = levels.Count(l => l.Shares >= this.minShares);
        double taken = this.fading.Taken(now);
        if (!(taken >= this.minShares))
            return new Look(bar, VolumeBrightness.Alpha(0), 0, 0, frame, roc, lit);

        double ratio = this.brightness.Ratio(now, taken);
        int alpha = VolumeBrightness.Alpha(VolumeBrightness.Strength(ratio, this.fullMultiple));
        int heat = (int)Math.Round(VolumeBrightness.Heat(ratio, this.glowMultiple) * 20);
        int hot = (int)Math.Round(VolumeBrightness.Hot(ratio, this.fullMultiple, this.glowMultiple) * 20);
        return new Look(bar, alpha, heat, hot, frame, roc, lit);
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
        var burstsNow = new List<TapeBurst>();
        var look = this.TakeLook(levels, burstsNow);
        if (!this.shade && !look.Bar.IsEmpty)
            look = look with { Alpha = 255, Heat = 0, Hot = 0 };
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
        var stops = this.shade
            ? VolumeAtPrice.Stops(levels, price => converter.GetChartY(price), rowHigh, rowLow + 1, this.minShares, look.Alpha)
            : [new ShadeStop(rowHigh, look.Alpha, double.NaN)];

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

            // The tape's bursts: a soft round blotch each at its average price, green bought and red sold, bigger the
            // more it beat the bar, fading over the window after its last print.
            if (burstsNow.Count > 0)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                long nowTicks = DateTime.UtcNow.Ticks;
                double keep = this.bursts.Keep.Ticks;
                float baseRadius = Math.Max(3f, body * 0.6f);
                foreach (var burst in burstsNow)
                {
                    double y = converter.GetChartY(burst.Price);
                    float fade = (float)Math.Clamp(1 - ((nowTicks - burst.LastTicks) / keep), 0, 1);
                    if (double.IsNaN(y) || fade <= 0)
                        continue;

                    float radius = baseRadius * (float)Math.Clamp(Math.Sqrt(burst.Strength), 1, 3);
                    var colour = burst.Side == PrintTaker.Buyer ? BurstUp : BurstDown;
                    using var path = new GraphicsPath();
                    path.AddEllipse(centre + 0.5f - radius, (float)y - radius, radius * 2, radius * 2);
                    using var blotch = new PathGradientBrush(path)
                    {
                        CenterColor = Color.FromArgb((int)(235 * fade), colour),
                        SurroundColors = [Color.FromArgb(0, colour)],
                    };
                    graphics.FillPath(blotch, path);
                }
            }

            // In a burst the volume reads in the candle's colour, in bold.
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI", 8f, heat > 0 ? FontStyle.Bold : FontStyle.Regular);
            using var text = new SolidBrush(heat > 0 ? hue : Label);
            string over = look.Roc;
            using var rocText = new SolidBrush(over.StartsWith('+') ? Up : over.StartsWith('-') ? Down : Label);
            var size = graphics.MeasureString(over, font);

            // Kept inside the chart: it's wider than the candle, which can be at the right edge.
            float overLeft = Math.Max(area.Left, Math.Min(centre + 0.5f - (size.Width / 2), area.Right - size.Width));
            graphics.DrawString(over, font, rocText, overLeft, rowHigh - size.Height - 1);
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
