// Checks on the rolling candle's maths, without Quantower: the candle, its brightness against typical volume, and
// its shading by price and by who took each price.
//   dotnet run --project tests

using ScalpFeedIndicators;

int failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok)
        failures++;
}

long T(double s) => new DateTime(2026, 11, 18, 15, 0, 0, DateTimeKind.Utc).Ticks + (long)(s * TimeSpan.TicksPerSecond);
var c = new RollingCandle(TimeSpan.FromSeconds(10));
Check(c.At(T(0)).IsEmpty, "nothing traded yet: no candle");
c.Add(T(0), 3.40, 100);
c.Add(T(2), 3.45, 300);
c.Add(T(4), 3.38, 200);
c.Add(T(6), 3.42, 500);
Check(c.At(T(6.5)) == new RollingBar(3.40, 3.45, 3.38, 3.42, 1100, 4), $"open the first trade, high and low the extremes, close the latest, volume the sum ({c.At(T(6.5))})");
Check(c.At(T(10.5)) == new RollingBar(3.45, 3.45, 3.38, 3.42, 1000, 3), "10 s after it, the first trade has rolled out and the open is the next one");
Check(c.At(T(14.5)) == new RollingBar(3.42, 3.42, 3.42, 3.42, 500, 1), "by 14.5 s only the last trade is left");
Check(c.At(T(16.5)) == new RollingBar(3.42, 3.42, 3.42, 3.42, 0, 0), "with nothing in the window: flat at the last trade, no volume");
c.Add(T(17), 3.50, 1000);
Check(c.At(T(17)) == new RollingBar(3.50, 3.50, 3.50, 3.50, 1000, 1), "the next trade starts it again");

var edge = new RollingCandle(TimeSpan.FromSeconds(10));
edge.Add(T(0), 1.00, 100);
Check(edge.At(T(9.99)).Volume == 100 && edge.At(T(10)).Volume == 0, "a trade stays in until exactly the window's length has passed");

var five = new RollingCandle(TimeSpan.FromSeconds(5));
five.Add(T(0), 2.00, 100);
five.Add(T(4), 2.10, 100);
Check(five.At(T(6)) == new RollingBar(2.10, 2.10, 2.10, 2.10, 100, 1), "the window is settable: 5 s drops a trade 6 s old");

var junk = new RollingCandle(TimeSpan.FromSeconds(10));
junk.Add(T(0), 0, 100);
junk.Add(T(0), -1, 100);
junk.Add(T(0), 2.00, -5);
Check(junk.At(T(1)).IsEmpty, "a price of 0 or less, or a negative size, isn't a trade");
junk.Add(T(1), 2.00, 0);
Check(junk.At(T(1)) == new RollingBar(2.00, 2.00, 2.00, 2.00, 0, 1), "a trade of 0 shares still moves the price");

var roc = new RollingCandle(TimeSpan.FromSeconds(10));
bool Near(double a, double b, double within = 1e-9) => Math.Abs(a - b) < within;
double Fade(double change, double seconds) => Math.Exp(Math.Log(1 + change) * Math.Exp(-seconds / 10)) - 1;
Check(double.IsNaN(roc.Roc(T(0))), "nothing traded yet: no rate of change");
roc.Add(T(0), 4.00, 100);
Check(Near(roc.Roc(T(5)), 0), "one trade: none yet");
roc.Add(T(20), 4.20, 100);
Check(Near(roc.Roc(T(20)), 0.05), "a 5% jump counts in full when it comes");
Check(Near(roc.Roc(T(25)), Fade(0.05, 5)) && Near(roc.Roc(T(30)), Fade(0.05, 10)) && Near(roc.Roc(T(40)), Fade(0.05, 20)),
    $"and fades: {roc.Roc(T(25)):P2} after 5 s, {roc.Roc(T(30)):P2} after 10, {roc.Roc(T(40)):P2} after 20");
Check(Near(roc.Roc(T(29.99)), roc.Roc(T(30.01)), 0.0001) && roc.At(T(30.01)).Trades == 0, "with no drop as the jump leaves the candle's window");
Check(Near(roc.Roc(T(20)), 0.05), "asking for it changes nothing");
roc.Add(T(30), 4.20, 100);
Check(Near(roc.Roc(T(30)), Fade(0.05, 10)), "a trade at the same price changes nothing either");
roc.Clear();
Check(double.IsNaN(roc.Roc(T(42))), "starting over forgets it");
roc.Add(T(43), 2.00, 100);
Check(Near(roc.Roc(T(43)), 0), "so a new stock isn't measured against the last one's price");
roc.Add(T(44), 2.10, 100);
roc.Add(T(44), 2.00, 100);
Check(Near(roc.Roc(T(44)), 0), "up and straight back is none");
roc.Add(T(43.5), 2.02, 100);
Check(Near(roc.Roc(T(44)), 0.01), "a trade timed before the last one doesn't grow what's there");

var climb = new RollingCandle(TimeSpan.FromSeconds(10));
for (int i = 0; i <= 1200; i++)
    climb.Add(T(i / 10.0), 3 * Math.Pow(1.001, i / 10.0), 100);
Check(Near(climb.Roc(T(120)), Math.Pow(1.001, 10) - 1, 0.0001), $"a steady 0.1% a second reads what 10 seconds of it is ({climb.Roc(T(120)):P3})");

// The same thing the long way: the price against its own average over time, faded the same way.
var wander = new RollingCandle(TimeSpan.FromSeconds(10));
var rng = new Random(7);
double price = 5, average = Math.Log(5), step = 0.001;
wander.Add(T(0), price, 100);
for (int i = 1; i <= 60_000; i++)
{
    average += (Math.Log(price) - average) * (1 - Math.Exp(-step / 10));
    if (i % 250 == 0)
    {
        price *= 1 + ((rng.NextDouble() - 0.48) * 0.01);
        wander.Add(T(i * step), price, 100);
    }
}
Check(Near(wander.Roc(T(60)), (price / Math.Exp(average)) - 1, 1e-6), $"it's how far the price is from its exponential average ({wander.Roc(T(60)):P3})");

var fiveSeconds = new RollingCandle(TimeSpan.FromSeconds(5));
fiveSeconds.Add(T(0), 4.00, 100);
fiveSeconds.Add(T(1), 4.20, 100);
Check(Near(fiveSeconds.Roc(T(6)), Math.Exp(Math.Log(1.05) * Math.Exp(-1)) - 1), "it fades with the window's length, so a 5 second one fades twice as fast");
var fading = new FadingVolume(TimeSpan.FromSeconds(10));
Check(fading.Taken(T(0)) == 0, "nothing bought or sold yet: no fading volume");
fading.Add(T(0), 4.00, 10_000, PrintTaker.Buyer);
Check(Near(fading.Taken(T(0)), 10_000) && Near(fading.Taken(T(5)), 10_000 * Math.Exp(-0.5), 1e-6) && Near(fading.Taken(T(10)), 10_000 * Math.Exp(-1), 1e-6),
    $"a print counts in full and fades: {fading.Taken(T(5)):0} after 5 s, {fading.Taken(T(10)):0} after 10, {fading.Taken(T(20)):0} after 20");
Check(Near(fading.Taken(T(9.99)), fading.Taken(T(10.01)), 10), "with no drop as it leaves the candle's window");
fading.Add(T(0), 4.00, 5_000, PrintTaker.Between);
fading.Add(T(0), 0, 5_000, PrintTaker.Buyer);
fading.Add(T(0), 4.00, 0, PrintTaker.Buyer);
Check(Near(fading.Taken(T(0)), 10_000), "prints between the bid and ask, and ones with no price or shares, count for nothing");

var atPrices = new List<PriceLevel>();
fading.Clear();
fading.Add(T(0), 4.004, 3_000, PrintTaker.Buyer);
fading.Add(T(0), 4.00, 1_000, PrintTaker.Seller);
fading.Add(T(0), 4.05, 2_000, PrintTaker.Seller);
fading.Add(T(0), 3.90, 9_000, PrintTaker.Buyer);
fading.Levels(T(0), 4.00, 4.05, atPrices);
Check(atPrices.SequenceEqual(new[] { new PriceLevel(4.05, 2_000, 0, 2_000), new PriceLevel(4.00, 4_000, 3_000, 1_000) }),
    "its shares at each price by who took them, rounded to the tick, highest first, and none from prices the candle is off");
fading.Levels(T(10), 4.00, 4.05, atPrices);
Check(atPrices.Count == 2 && Near(atPrices[1].Shares, 4_000 * Math.Exp(-1), 1e-6) && Near(atPrices[1].Bought / atPrices[1].Shares, 0.75) && Near(atPrices[0].Shares / atPrices[1].Shares, 0.5),
    "they fade together, so which price is busiest and who took it stay as they were");
fading.Add(T(10), 4.05, 2_000, PrintTaker.Buyer);
fading.Levels(T(10), 4.00, 4.05, atPrices);
Check(Near(atPrices[0].Shares, 2_000 + (2_000 * Math.Exp(-1)), 1e-6) && atPrices[0].Shares > atPrices[1].Shares, "a fresh print counts for more than one as big that's faded");
fading.Levels(T(120), 4.00, 4.05, atPrices);
Check(atPrices.Count == 0 && fading.Taken(T(120)) < 1, "after a couple of minutes there's nothing left");

var flow = new FadingVolume(TimeSpan.FromSeconds(10));
var flowTypical = new VolumeBrightness(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
for (int i = 0; i < 3_000; i++)
{
    flow.Add(T(i / 10.0), 4.00 + (i % 3 * 0.01), 100, PrintTaker.Buyer);
    flowTypical.Add(T(i / 10.0), 100);
}
Check(Near(flow.Taken(T(299.95)), 10_000, 100), $"at a steady rate it's what the window holds, 10,000 ({flow.Taken(T(299.95)):0})");
Check(Near(flowTypical.Ratio(T(299.95), flow.Taken(T(299.95))), 1, 0.03), $"so a steady stock is at typical ({flowTypical.Ratio(T(299.95), flow.Taken(T(299.95))):0.000})");
flow.Levels(T(299.95), 4.00, 4.02, atPrices);
Check(atPrices.Count == 3 && Near(atPrices.Sum(l => l.Shares), flow.Taken(T(299.95)), 1), "and its prices add up to it");
var quick = new FadingVolume(TimeSpan.FromSeconds(5));
quick.Add(T(0), 4.00, 10_000, PrintTaker.Seller);
Check(Near(quick.Taken(T(5)), 10_000 * Math.Exp(-1), 1e-6), "it fades with the window's length");

Check(RollingCandle.FormatRoc(0.02361) == "+2.4%" && RollingCandle.FormatRoc(-0.004) == "-0.4%" && RollingCandle.FormatRoc(0.1249) == "+12.5%" && RollingCandle.FormatRoc(1.2341) == "+123.4%"
      && RollingCandle.FormatRoc(0) == "0.0%" && RollingCandle.FormatRoc(0.0004) == "0.0%" && RollingCandle.FormatRoc(-0.0004) == "0.0%" && RollingCandle.FormatRoc(0.0006) == "+0.1%" && RollingCandle.FormatRoc(double.NaN) == "",
    "it reads +2.4%, -0.4%, +12.5%, +123.4%, and 0.0% with no sign when it rounds to none");

Check(RollingCandle.FormatVolume(850) == "850" && RollingCandle.FormatVolume(1234) == "1K" && RollingCandle.FormatVolume(12_345) == "12K" && RollingCandle.FormatVolume(12_700) == "13K"
      && RollingCandle.FormatVolume(1_250_000) == "1M" && RollingCandle.FormatVolume(0.4) == "0" && RollingCandle.FormatVolume(0) == "0", "the volume label reads 850, 1K, 12K, 13K, 1M, with no decimals");
Check(RollingCandle.FormatVolume(999.4) == "999" && RollingCandle.FormatVolume(999.6) == "1K" && RollingCandle.FormatVolume(999_400) == "999K" && RollingCandle.FormatVolume(999_600) == "1M", "and changes unit where it rounds over, never 1000K");
Check(RollingBar.None == RollingBar.None, "an empty candle equals itself, so the redraw timer can tell nothing changed");

// The colour against the stock's typical 10 s volume over the last 5 minutes: half at typical, full at 2x,
// and beyond 4x a glow that's strongest at 16x.
var vb = new VolumeBrightness(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
Check(double.IsNaN(vb.Ratio(T(0), 100)), "nothing seen yet: typical isn't known");
for (int i = 0; i < 300; i++)
    vb.Add(T(i), 60);
double steady = vb.Typical(T(299.5));
Check(Math.Abs(steady - 600) < 6, $"60 shares a second for 5 minutes is a typical 10 s window of about 600 ({steady:0.#}), measured from the first trade");
Check(Math.Abs(vb.Ratio(T(299.5), 1200) - 2) < 0.02 && Math.Abs(vb.Ratio(T(299.5), 600) - 1) < 0.01 && vb.Ratio(T(299.5), 0) == 0,
    "a window of 1,200 is about twice typical, 600 about typical, none none");

var young = new VolumeBrightness(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
young.Add(T(0), 100);
Check(double.IsNaN(young.Ratio(T(19.9), 100)) && young.Ratio(T(20), 100) == 2,
    "typical needs two windows of time first; then 100 shares in 20 s is 50 a window");
var dead = new VolumeBrightness(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
dead.Add(T(5), 600);
Check(double.IsPositiveInfinity(dead.Ratio(T(400), 100)) && VolumeBrightness.Heat(dead.Ratio(T(400), 100), 4) == 1,
    "after 5 minutes with nothing, any trade is a burst at its hottest");

Check(VolumeBrightness.Strength(1, 2) == 0.5 && VolumeBrightness.Strength(2, 2) == 1 && VolumeBrightness.Strength(0.5, 2) == 0,
    "strength: typical is half, twice typical full, half typical none");
Check(VolumeBrightness.Strength(10, 2) == 1 && VolumeBrightness.Strength(0, 2) == 0 && VolumeBrightness.Strength(double.NaN, 2) == 0.5,
    "past twice typical stays full; no volume none; unknown typical half");
Check(VolumeBrightness.Heat(3, 4) == 0 && VolumeBrightness.Heat(4, 4) == 0 && VolumeBrightness.Heat(8, 4) == 0.5 && VolumeBrightness.Heat(16, 4) == 1,
    "glow: none up to 4x typical, half at 8x, most at 16x");
Check(VolumeBrightness.Heat(100, 4) == 1 && VolumeBrightness.Heat(double.NaN, 4) == 0, "and no more past 16x; none while typical isn't known");
Check(VolumeBrightness.Hot(2, 2, 4) == 0 && Math.Abs(VolumeBrightness.Hot(4, 2, 4) - (1.0 / 3)) < 1e-9 && Math.Abs(VolumeBrightness.Hot(8, 2, 4) - (2.0 / 3)) < 1e-9 && VolumeBrightness.Hot(16, 2, 4) == 1,
    $"past full colour it runs hotter right away: 2x none, 4x a third, 8x two thirds, 16x all ({VolumeBrightness.Hot(4, 2, 4)}, {VolumeBrightness.Hot(8, 2, 4)})");
Check(VolumeBrightness.Hot(1.5, 2, 4) == 0 && VolumeBrightness.Hot(100, 2, 4) == 1 && VolumeBrightness.Hot(double.NaN, 2, 4) == 0, "none under full colour or while typical isn't known; no more past 16x");
var hotCore = CandleShade.Colour(new ShadeStop(0, 255, 1), CandleShade.Up, 1, 255);
var coolEdge = CandleShade.Colour(new ShadeStop(0, 64, 1), CandleShade.Up, 1, 255);
Check(hotCore.R == 168 && hotCore.G == 219 && hotCore.B == 215 && coolEdge == System.Drawing.Color.FromArgb(64, CandleShade.Up), $"at its hottest the busiest price runs 60% of the way to white; the quietest keep their colour ({hotCore}, {coolEdge})");
Check(VolumeBrightness.Alpha(0) == 64 && VolumeBrightness.Alpha(0.5) == 159 && VolumeBrightness.Alpha(1) == 255 && VolumeBrightness.Alpha(2) == 255,
    $"opacity runs from a quarter (64) through half strength (159) to full (255) ({VolumeBrightness.Alpha(0)}, {VolumeBrightness.Alpha(0.5)}, {VolumeBrightness.Alpha(1)})");

// Seeded from history: thirty 10-second bars of 3,000 shares each, the last ending as the candle started (90,000
// in the 5 minutes, 3,000 a window).
var seeded = new VolumeBrightness(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
seeded.Begin(T(0));
seeded.Seed(Enumerable.Range(0, 30).Select(k => (T(-290 + (10 * k)), 3000.0)));
Check(seeded.Typical(T(0)) == 1500 && seeded.Ratio(T(0), 1500) == 1,
    $"from the start: the bars' 3,000 a window, half taken to be bought or sold until trades say otherwise ({seeded.Typical(T(0))})");
seeded.Add(T(10), 30_000, taken: true);
seeded.Add(T(10), 10_000, taken: false);
Check(Math.Abs(seeded.TakenShare() - 0.7) < 1e-9, $"30,000 of 40,000 shares bought or sold moves the share from a half to 0.7 ({seeded.TakenShare()})");
Check(Math.Abs(seeded.Typical(T(150)) - 2050) < 1e-6,
    $"150 s in: the live 30,000 bought or sold, and the 15 bars that ended in the last 5 minutes (45,000 x 0.7), over 5 minutes ({seeded.Typical(T(150))})");
Check(Math.Abs(seeded.Typical(T(300)) - 1000) < 1e-6, $"5 minutes in, every bar has aged out ({seeded.Typical(T(300))})");
var bunched = new VolumeBrightness(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
bunched.Begin(T(0));
bunched.Seed(Enumerable.Range(0, 6).Select(k => (T(-290 + (10 * k)), 15_000.0)));
double atStart = bunched.Typical(T(0)), at55 = bunched.Typical(T(55)), at61 = bunched.Typical(T(61));
Check(Math.Abs(atStart - 1500) < 1e-6 && Math.Abs(at55 - 250) < 1e-6 && at61 == 0,
    $"90,000 all in the first minute of the 5: each bar drops as it ages past 5 minutes, so it's gone 61 s in, not spread over 5 minutes ({atStart}, {at55}, {at61})");
seeded.Clear();
Check(double.IsNaN(seeded.Typical(T(301))), "starting over drops the seed");
var unseeded = new VolumeBrightness(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
unseeded.Add(T(0), 500, taken: false);
Check(unseeded.Typical(T(25)) == 0, "prints between the bid and ask don't count toward typical");

// Shading by price: where in the candle the shares traded, with under 1,000 at a price at the lowest intensity.
Check(VolumeAtPrice.TickAt(1) == 0.01 && VolumeAtPrice.TickAt(0.99) == 0.0001, "ticks: a cent from $1, a hundredth of a cent below");
Check(VolumeAtPrice.Level(5.005) == 5.01 && VolumeAtPrice.Level(4.999) == 5.00 && VolumeAtPrice.Level(3.4499) == 3.45 && VolumeAtPrice.Level(0.51234) == 0.5123,
    $"a trade counts at its tick, halves up: 5.005 at 5.01, 4.999 at 5.00, 0.51234 at 0.5123 ({VolumeAtPrice.Level(5.005)}, {VolumeAtPrice.Level(0.51234)})");

var p = new RollingCandle(TimeSpan.FromSeconds(10));
p.Add(T(0), 3.40, 100, PrintTaker.Buyer);
p.Add(T(1), 3.45, 3000, PrintTaker.Buyer);
p.Add(T(2), 3.405, 500, PrintTaker.Buyer);
p.Add(T(3), 3.4499, 2000, PrintTaker.Buyer);
var levels = new List<PriceLevel>();
var pb = p.At(T(3.5), levels);
Check(levels.SequenceEqual([new PriceLevel(3.45, 5000, 5000), new PriceLevel(3.41, 500, 500), new PriceLevel(3.40, 100, 100)]) && pb.Volume == 5600,
    $"the candle's shares at each price, highest first, a midpoint print with its tick ({string.Join(", ", levels)})");
p.At(T(11.5), levels);
Check(levels.SequenceEqual([new PriceLevel(3.45, 2000, 2000), new PriceLevel(3.41, 500, 500)]), "and they roll out with their trades");

Check(VolumeAtPrice.Alpha(5000, 5000, 1000, 255) == 255 && VolumeAtPrice.Alpha(1250, 5000, 1000, 255) == 160 && VolumeAtPrice.Alpha(999, 5000, 1000, 255) == 64,
    $"the busiest price gets the candle's colour, a quarter of its shares half way, under 1,000 the lowest ({VolumeAtPrice.Alpha(1250, 5000, 1000, 255)})");
Check(VolumeAtPrice.Alpha(5000, 5000, 1000, 160) == 160 && VolumeAtPrice.Alpha(0, 5000, 0, 255) == 64 && VolumeAtPrice.Alpha(5000, 5000, 1000, 10) == 64,
    "the busiest is only as bright as the candle; nothing traded is the lowest; never under the lowest");

// The gradient's stops. 1 cent = 10 pixels: the high (3.45) at y 50, the low (3.40) at y 100, the candle down to 101.
string Alphas(List<ShadeStop> stops) => string.Join(",", stops.Select(st => st.Alpha));
string Ys(List<ShadeStop> stops) => string.Join(",", stops.Select(st => st.Y));
var spread = new List<PriceLevel> { new(3.45, 4000), new(3.44, 1000), new(3.40, 2000) };
// (Rounded, so 3.43 lands on y 70 rather than binary's 69.99999.)
var stops = VolumeAtPrice.Stops(spread, price => Math.Round((3.50 - price) * 1000, 6), 50, 101, 1000, 255);
Check(Alphas(stops) == "255,255,160,64,64,199,199" && Ys(stops) == "50,50.5,60.5,70.5,90.5,100.5,101",
    $"zoomed in: each traded price a stop as bright as its shares (4,000 full, 1,000 half way, 2,000 between), the lowest a tick past them where nothing traded (3.43, 3.41) so they fade over a tick, from the candle's top to its bottom ({Alphas(stops)} at {Ys(stops)})");
stops = VolumeAtPrice.Stops(spread, price => (3.50 - price) * 10, 0, 2, 1000, 255);
Check(Alphas(stops) == "255,255,199,199" && Ys(stops) == "0,0.5,1.5,2",
    $"zoomed out, 1 cent = 0.1 pixel: one stop a row, the brightest on it ({Alphas(stops)} at {Ys(stops)})");
var quiet = new List<PriceLevel> { new(3.45, 600), new(3.44, 300) };
stops = VolumeAtPrice.Stops(quiet, price => (3.50 - price) * 1000, 50, 61, 1000, 255);
Check(stops.All(st => st.Alpha == 64), "a window under 1,000 shares is all at the lowest intensity");
stops = VolumeAtPrice.Stops([new PriceLevel(3.45, 5000)], price => (3.50 - price) * 1000, 50, 51, 1000, 200);
Check(stops.All(st => st.Alpha == 200) && stops[0].Y == 50 && stops[^1].Y == 51, "a candle that never moved is one colour, the candle's");
stops = VolumeAtPrice.Stops([], price => (3.50 - price) * 1000, 50, 51, 1000, 200);
Check(stops.All(st => st.Alpha == 64) && stops[0].Y == 50 && stops[^1].Y == 51, "with nothing to go on, the lowest intensity throughout");

// Who took each price, as the tape colours it: green where bought, red where sold.
var sides = new RollingCandle(TimeSpan.FromSeconds(10));
sides.Add(T(0), 3.45, 3000, PrintTaker.Buyer);
sides.Add(T(1), 3.45, 1000, PrintTaker.Seller);
sides.Add(T(2), 3.44, 2000, PrintTaker.Seller);
sides.Add(T(3), 3.44, 500);
sides.Add(T(4), 3.43, 800, PrintTaker.Seller);
sides.At(T(4.5), levels);
var sidesBar = sides.At(T(4.5), levels);
Check(levels.SequenceEqual([new PriceLevel(3.45, 4000, 3000, 1000), new PriceLevel(3.44, 2000, 0, 2000), new PriceLevel(3.43, 800, 0, 800)]),
    $"each price's shares bought or sold; a print between the bid and ask counts in neither, nor in its shares ({string.Join(", ", levels)})");
Check(sidesBar.Volume == 7300 && sidesBar.Taken == 6800, $"the candle's volume has every print, what its brightness goes by only the bought and sold ({sidesBar})");
var grey = new RollingCandle(TimeSpan.FromSeconds(10));
grey.Add(T(0), 3.45, 50_000);
grey.Add(T(1), 3.44, 50_000);
grey.Add(T(2), 3.43, 2000, PrintTaker.Seller);
var greyLevels = new List<PriceLevel>();
var greyBar = grey.At(T(2.5), greyLevels);
var greyStops = VolumeAtPrice.Stops(greyLevels, price => Math.Round((3.50 - price) * 1000, 6), 50, 81, 1000, 255);
Check(greyBar.High == 3.45 && greyBar.Taken == 2000 && Alphas(greyStops).StartsWith("64,64,64,") && greyStops.Max(st => st.Alpha) == 255,
    $"100,000 shares printed between the bid and ask stay at the lowest intensity, though the candle reaches up to them; the 2,000 sold is the brightest ({Alphas(greyStops)})");
string Buying(List<ShadeStop> st) => string.Join(",", st.Select(x => double.IsNaN(x.Buying) ? "-" : x.Buying.ToString("0.##")));
stops = VolumeAtPrice.Stops(levels, price => Math.Round((3.50 - price) * 1000, 6), 50, 71, 1000, 255);
Check(Buying(stops) == "0.75,0.75,0,-,-",
    $"3.45 three-quarters bought, 3.44 all sold (its 500 between aside); 3.43 under 1,000 and the quiet tick past it keep the candle's colour ({Buying(stops)} at {Ys(stops)})");
stops = VolumeAtPrice.Stops(levels, price => Math.Round((3.50 - price) * 10, 6), 0, 1, 1000, 255);
Check(Buying(stops) == "0.5,0.5,0.5", $"zoomed out, prices sharing a row pool their buying and selling: 3,000 bought, 3,000 sold ({Buying(stops)})");

// The drawn gradient, read back from a bitmap: each price's row must be its own stop's colour and alpha, never
// blended with the row next to it. Drawn as the candle draws it (no smoothing), under each pixel offset mode, since
// the chart's Graphics comes from Quantower.
System.Drawing.Color Expected(ShadeStop st, System.Drawing.Color hue) =>
    System.Drawing.Color.FromArgb(st.Alpha, double.IsNaN(st.Buying) ? hue : CandleShade.Mix(CandleShade.Down, CandleShade.Up, st.Buying));
int WorstRow(List<ShadeStop> st, int top, int bottom, System.Drawing.Drawing2D.PixelOffsetMode mode, System.Drawing.Color hue, Func<int, bool> priceRow, out string where)
{
    using var bitmap = new System.Drawing.Bitmap(12, bottom + 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var g = System.Drawing.Graphics.FromImage(bitmap))
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        g.PixelOffsetMode = mode;
        CandleShade.Fill(g, CandleShade.Rows(st, top, bottom, hue, 0, 255), top, 0, 12, top, bottom);
    }

    int worst = 0;
    where = "";
    foreach (var stop in st.Where(x => x.Y > top && x.Y < bottom && priceRow((int)Math.Floor(x.Y))))
    {
        int row = (int)Math.Floor(stop.Y);
        var got = bitmap.GetPixel(6, row);
        var want = Expected(stop, hue);
        int off = new[] { Math.Abs(got.A - want.A), Math.Abs(got.R - want.R), Math.Abs(got.G - want.G), Math.Abs(got.B - want.B) }.Max();
        if (off > worst)
            (worst, where) = (off, $"row {row}: drawn {got.A},{got.R},{got.G},{got.B}, its price's {want.A},{want.R},{want.G},{want.B}");
    }

    return worst;
}

// Zoomed out to a pixel a tick: ten prices on ten rows, each brighter or dimmer and greener or redder than the next.
var striped = new List<PriceLevel>();
for (int i = 0; i < 10; i++)
    striped.Add(new PriceLevel(3.50 - (i * 0.01), i % 2 == 0 ? 8000 : 1500, i % 2 == 0 ? 8000 : 0, i % 2 == 0 ? 0 : 1500));
var stripedStops = VolumeAtPrice.Stops(striped, price => Math.Round((3.50 - price) * 100, 6) + 20, 20, 30, 1000, 255);
// Zoomed in, ten pixels a tick: a green price, a red one a tick below, a quiet tick, then a busy mixed one.
var spaced = new List<PriceLevel> { new(3.45, 6000, 6000, 0), new(3.44, 3000, 0, 3000), new(3.42, 5000, 2500, 2500) };
var spacedStops = VolumeAtPrice.Stops(spaced, price => Math.Round((3.50 - price) * 1000, 6), 50, 81, 1000, 255);
foreach (var mode in new[] { System.Drawing.Drawing2D.PixelOffsetMode.Default, System.Drawing.Drawing2D.PixelOffsetMode.Half })
{
    int a = WorstRow(stripedStops, 20, 30, mode, CandleShade.Up, _ => true, out string at);
    Check(a <= 1, $"pixel offset {mode}, a tick a pixel: every price's row in its own colour, off by {a} at most {at}");
    int b = WorstRow(spacedStops, 50, 81, mode, CandleShade.Up, row => row is 50 or 60 or 70 or 80, out at);
    Check(b <= 1, $"pixel offset {mode}, ten pixels a tick: each traded price's row, and the quiet tick between (3.43), in its own colour, off by {b} at most {at}");
}

// Tape bursts: a side's prints within a second of each other, judged against the side's typical rate.
var tb = new TapeBursts { Gap = TimeSpan.FromSeconds(1), Keep = TimeSpan.FromSeconds(10) };
// Typical: 200 shares a second bought or sold, so over a second the bar is 3 x 200 = 600, but at least 1,000.
tb.Add(T(0), 3.40, 300, PrintTaker.Buyer);
tb.Add(T(0.4), 3.41, 400, PrintTaker.Buyer);
Check(tb.At(T(0.5), 200, 3, 1000).Count == 0, "700 bought in under a second: under the least shares, no burst yet");
tb.Add(T(0.6), 3.42, 5000, PrintTaker.Between);
tb.Add(T(0.7), 3.40, 2000, PrintTaker.Seller);
tb.Add(T(0.9), 3.43, 600, PrintTaker.Buyer);
var found = tb.At(T(1.0), 200, 3, 1000);
Check(found.Count == 2 && found.Any(b => b.Side == PrintTaker.Buyer && b.Shares == 1300) && found.Any(b => b.Side == PrintTaker.Seller && b.Shares == 2000),
    $"the buyers' run, through a sell and a print between the bid and ask, is 1,300 shares; the 2,000 sold is its own; the 5,000 between is in neither ({string.Join(", ", found)})");
var buying = found.First(b => b.Side == PrintTaker.Buyer);
Check(Math.Abs(buying.Price - ((3.40 * 300 + 3.41 * 400 + 3.43 * 600) / 1300)) < 1e-9 && Math.Abs(buying.Strength - 1.3) < 1e-9,
    $"a burst sits at its average price by shares, and its strength is how far past the bar it is ({buying.Price:0.0000}, {buying.Strength})");
tb.Add(T(2.5), 3.45, 900, PrintTaker.Buyer);
Check(tb.At(T(2.6), 200, 3, 1000).Count(b => b.Side == PrintTaker.Buyer) == 1, "a print 1.6 s after the last starts a new run, which isn't a burst on its own");
Check(tb.At(T(2.6), 2000, 3, 1000).Count == 0, "on a stock that typically trades 2,000 a second, none of these is a burst");
Check(tb.At(T(2.6), double.NaN, 3, 1000).Count == 2, "with typical not known yet, only the least shares counts");
Check(tb.At(T(10.6), 200, 3, 1000).Count == 2 && tb.At(T(10.8), 200, 3, 1000).Count == 1 && tb.At(T(11.0), 200, 3, 1000).Count == 0, "each burst stays until a window has passed since its own last print: the sell (last 0.7 s) goes at 10.7 s, the buy (0.9 s) at 10.9 s");
var slow = new TapeBursts { Gap = TimeSpan.FromSeconds(1), Keep = TimeSpan.FromSeconds(10) };
for (int i = 0; i < 6; i++)
    slow.Add(T(i * 0.9), 3.40, 400, PrintTaker.Buyer);
Check(slow.At(T(5), 200, 3, 1000).Count == 0, "a steady 400 every 0.9 s for 4.5 s is 2,400 against a bar of 2,700: steady buying isn't a burst");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
