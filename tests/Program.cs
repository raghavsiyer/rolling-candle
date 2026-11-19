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

Check(RollingCandle.FormatVolume(850) == "850" && RollingCandle.FormatVolume(1234) == "1.23K" && RollingCandle.FormatVolume(12_345) == "12.3K"
      && RollingCandle.FormatVolume(1_250_000) == "1.25M", "the volume label reads 850, 1.23K, 12.3K, 1.25M");
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
Check(VolumeBrightness.Alpha(0) == 64 && VolumeBrightness.Alpha(0.5) == 159 && VolumeBrightness.Alpha(1) == 255 && VolumeBrightness.Alpha(2) == 255,
    $"opacity runs from a quarter (64) through half strength (159) to full (255) ({VolumeBrightness.Alpha(0)}, {VolumeBrightness.Alpha(0.5)}, {VolumeBrightness.Alpha(1)})");

// Seeded from history: 90,000 shares in the 5 minutes of 10-second bars before the candle started, 3,000 a window.
var seeded = new VolumeBrightness(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
seeded.Begin(T(0));
seeded.Seed(90_000, 300);
Check(seeded.Typical(T(0)) == 1500 && seeded.Ratio(T(0), 1500) == 1,
    $"from the start: the bars' 3,000 a window, half taken to be bought or sold until trades say otherwise ({seeded.Typical(T(0))})");
seeded.Add(T(10), 30_000, taken: true);
seeded.Add(T(10), 10_000, taken: false);
Check(Math.Abs(seeded.TakenShare() - 0.7) < 1e-9, $"30,000 of 40,000 shares bought or sold moves the share from a half to 0.7 ({seeded.TakenShare()})");
Check(Math.Abs(seeded.Typical(T(150)) - 2050) < 1e-6,
    $"half way through the lookback: the live 30,000 bought or sold over 5 minutes (1,000 a window) and the bars for the half not seen live (3,000 x 0.7 x a half) ({seeded.Typical(T(150))})");
Check(Math.Abs(seeded.Typical(T(300)) - 1000) < 1e-6, $"once the whole lookback has been seen live, the bars count for nothing ({seeded.Typical(T(300))})");
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

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
