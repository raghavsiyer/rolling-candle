namespace ScalpFeedIndicators;

/// <summary>
/// One burst on the tape: prints on one side (bought at or above the ask, or sold at or below the bid) close together.
/// Price is their average, by shares; Strength how many times the bar to count as a burst its shares are.
/// </summary>
public readonly record struct TapeBurst(PrintTaker Side, long StartTicks, long LastTicks, double Shares, double Price, double Strength);

/// <summary>
/// The tape's bursts (your idea, 2026-11-19: the order flow feel of blotches, rather than the whole candle brightening
/// with the window's volume): a run of prints on one side, each within the gap (a second) of the one before on that
/// side, whatever the other side does meanwhile. Prints between the bid and ask aren't in any run. A run is a burst
/// when its shares are at least the multiple (3) of what the stock typically has bought and sold, both sides
/// together, over the run's time (a single print counts as a gap's time), and at least the least shares (1,000). Its strength is how many
/// times that bar it is, which sizes its blotch. A run is kept until it's been a window (10 seconds) since its last
/// print, so a blotch fades over the window as the candle's trades roll out. Thread-safe: prints come in on the feed's
/// thread and bursts are read on the chart's. This depends on nothing in Quantower, so the tests can drive it.
/// </summary>
public sealed class TapeBursts
{
    private sealed class Run
    {
        public PrintTaker Side;
        public long Start;
        public long Last;
        public double Shares;
        public double Notional;
    }

    private readonly object gate = new();
    private readonly List<Run> runs = [];
    private Run openBuy;
    private Run openSell;
    private long gap = TimeSpan.TicksPerSecond;
    private long keep = 10 * TimeSpan.TicksPerSecond;

    /// <summary>How close together a side's prints must be to count as one run.</summary>
    public TimeSpan Gap
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref this.gap));
        set => Volatile.Write(ref this.gap, Math.Max(TimeSpan.TicksPerMillisecond, value.Ticks));
    }

    /// <summary>How long after its last print a run is kept: the candle's window.</summary>
    public TimeSpan Keep
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref this.keep));
        set => Volatile.Write(ref this.keep, Math.Max(TimeSpan.TicksPerMillisecond, value.Ticks));
    }

    /// <summary>A print as it arrives, at utcTicks; ones between the bid and ask, of no shares, or no price are skipped.</summary>
    public void Add(long utcTicks, double price, double size, PrintTaker taker)
    {
        if (taker == PrintTaker.Between || !(size > 0) || !(price > 0))
            return;

        lock (this.gate)
        {
            var open = taker == PrintTaker.Buyer ? this.openBuy : this.openSell;
            if (open == null || utcTicks - open.Last > Volatile.Read(ref this.gap))
            {
                open = new Run { Side = taker, Start = utcTicks, Last = utcTicks };
                this.runs.Add(open);
                if (taker == PrintTaker.Buyer)
                    this.openBuy = open;
                else
                    this.openSell = open;
            }

            open.Last = Math.Max(open.Last, utcTicks);
            open.Shares += size;
            open.Notional += size * price;
        }
    }

    /// <summary>
    /// The bursts at utcTicks, oldest first, judged against typicalPerSecond: the shares typically bought or sold a
    /// second (NaN when not known yet, when only the least shares counts).
    /// </summary>
    public List<TapeBurst> At(long utcTicks, double typicalPerSecond, double multiple, double minShares)
    {
        var bursts = new List<TapeBurst>();
        lock (this.gate)
        {
            long keepTicks = Volatile.Read(ref this.keep);
            long gapTicks = Volatile.Read(ref this.gap);
            this.runs.RemoveAll(r => r.Last <= utcTicks - keepTicks);
            if (this.openBuy != null && !this.runs.Contains(this.openBuy))
                this.openBuy = null;
            if (this.openSell != null && !this.runs.Contains(this.openSell))
                this.openSell = null;

            foreach (var run in this.runs)
            {
                double seconds = Math.Max(run.Last - run.Start, gapTicks) / (double)TimeSpan.TicksPerSecond;
                double bar = Math.Max(Math.Max(1, minShares), double.IsNaN(typicalPerSecond) ? 0 : multiple * typicalPerSecond * seconds);
                if (run.Shares >= bar)
                    bursts.Add(new TapeBurst(run.Side, run.Start, run.Last, run.Shares, run.Notional / run.Shares, run.Shares / bar));
            }
        }

        return bursts;
    }

    public void Clear()
    {
        lock (this.gate)
        {
            this.runs.Clear();
            this.openBuy = null;
            this.openSell = null;
        }
    }
}
