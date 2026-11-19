using System.Drawing;

namespace ScalpFeedIndicators;

/// <summary>
/// The rolling candle's shading, row by row, apart from the view so the tests can draw it and read the pixels back.
/// Each pixel row's colour is worked out here and filled solid, rather than left to a GDI+ gradient brush: drawn onto
/// a bitmap on 2026-11-19, the brush blended a price's row with the next one's (a red price a pixel tall came out
/// half green, and zoomed in a price's row was off by up to 23 of 255), under either pixel offset mode. Here a
/// price's row is exactly its stop's colour, and the rows between two prices blend in a straight line from one to
/// the other, so it looks as smooth as before. Nothing here needs Quantower.
/// </summary>
public static class CandleShade
{
    public static readonly Color Up = Color.FromArgb(38, 166, 154);
    public static readonly Color Down = Color.FromArgb(239, 83, 80);

    /// <summary>
    /// A stop's colour at its alpha: green as far as its shares were bought and red as far as sold (the candle's hue
    /// where that isn't known); in a burst, stops near the candle's brightest lifted toward white by up to half the
    /// heat.
    /// </summary>
    public static Color Colour(ShadeStop stop, Color hue, float heat, int brightest)
    {
        var colour = double.IsNaN(stop.Buying) ? hue : Mix(Down, Up, stop.Buying);
        if (heat <= 0)
            return Color.FromArgb(stop.Alpha, colour);
        int floor = VolumeBrightness.Alpha(0);
        float hot = 0.5f * heat * Math.Clamp((stop.Alpha - floor) / (float)Math.Max(1, brightest - floor), 0f, 1f);
        int Lift(int c) => (int)Math.Round(c + ((255 - c) * hot));
        return Color.FromArgb(stop.Alpha, Lift(colour.R), Lift(colour.G), Lift(colour.B));
    }

    /// <summary>
    /// The colour of each pixel row from top to bottom (bottom not included), taken at the row's middle: a stop's own
    /// colour on its row, a straight blend between two stops, and the end stops' colours past them.
    /// </summary>
    public static Color[] Rows(List<ShadeStop> stops, int top, int bottom, Color hue, float heat, int brightest)
    {
        var rows = new Color[Math.Max(0, bottom - top)];
        if (rows.Length == 0)
            return rows;
        if (stops.Count == 0)
        {
            Array.Fill(rows, Colour(new ShadeStop(top, VolumeBrightness.Alpha(0), double.NaN), hue, heat, brightest));
            return rows;
        }

        var colours = stops.Select(s => Colour(s, hue, heat, brightest)).ToArray();
        int i = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            double y = top + r + 0.5;
            while (i < stops.Count - 1 && stops[i + 1].Y <= y)
                i++;

            if (y <= stops[0].Y || i == stops.Count - 1)
            {
                rows[r] = y <= stops[0].Y ? colours[0] : colours[^1];
                continue;
            }

            double span = stops[i + 1].Y - stops[i].Y;
            rows[r] = span > 0 ? Blend(colours[i], colours[i + 1], (y - stops[i].Y) / span) : colours[i + 1];
        }

        return rows;
    }

    /// <summary>
    /// Fills the rows from fromRow to toRow (not included) of a column x to x + width with their colours from rows,
    /// whose first is row top. Rows of one colour in a run are filled as one rectangle.
    /// </summary>
    public static void Fill(Graphics graphics, Color[] rows, int top, int x, int width, int fromRow, int toRow)
    {
        fromRow = Math.Max(fromRow, top);
        toRow = Math.Min(toRow, top + rows.Length);
        int start = fromRow;
        while (start < toRow)
        {
            var colour = rows[start - top];
            int end = start + 1;
            while (end < toRow && rows[end - top] == colour)
                end++;

            using (var brush = new SolidBrush(colour))
                graphics.FillRectangle(brush, x, start, width, end - start);
            start = end;
        }
    }

    /// <summary>From a (at 0) to b (at 1), channel by channel.</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        int Channel(int from, int to) => (int)Math.Round(from + ((to - from) * t));
        return Color.FromArgb(Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    /// <summary>From a (at 0) to b (at 1), alpha included.</summary>
    private static Color Blend(Color a, Color b, double t)
    {
        var mixed = Mix(a, b, t);
        return Color.FromArgb((int)Math.Round(a.A + ((b.A - a.A) * Math.Clamp(t, 0, 1))), mixed);
    }
}
