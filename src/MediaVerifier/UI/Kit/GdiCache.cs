using System.Drawing.Drawing2D;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// Pens and brushes reused across paints, so painting and scrolling allocate nothing. Keyed by colour
/// and width; the set of colours is the token set, so the cache stays small. UI thread only.
/// </summary>
internal static class GdiCache
{
    private static readonly Dictionary<int, SolidBrush> Brushes = [];
    private static readonly Dictionary<(int Argb, float Width, bool Round), Pen> Pens = [];
    private static readonly Dictionary<(int Argb, float Width, float Dash, float Gap), Pen> DashedPens = [];

    public static SolidBrush Brush(Color color)
    {
        int key = color.ToArgb();
        if (!Brushes.TryGetValue(key, out SolidBrush? brush))
        {
            brush = new SolidBrush(color);
            Brushes[key] = brush;
        }

        return brush;
    }

    /// <summary>A pen with round caps and joins (icons, rings, focus rings use round geometry).</summary>
    public static Pen RoundPen(Color color, float width) => GetPen(color, width, round: true);

    /// <summary>A plain pen with flat caps and miter joins (borders and dividers).</summary>
    public static Pen Pen(Color color, float width) => GetPen(color, width, round: false);

    /// <summary>
    /// A round-joined pen with flat-capped dashes of <paramref name="dash"/> and <paramref name="gap"/> pen widths (dashed
    /// icons such as the Pending state, drawn on every never-scanned row).
    /// </summary>
    public static Pen DashedRoundPen(Color color, float width, float dash, float gap)
    {
        (int, float, float, float) key = (color.ToArgb(), MathF.Round(width, 3), MathF.Round(dash, 3), MathF.Round(gap, 3));
        if (!DashedPens.TryGetValue(key, out Pen? pen))
        {
            pen = new Pen(color, StrokeWidth(width))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
                DashCap = DashCap.Flat,
                DashPattern = [dash, gap],
            };
            DashedPens[key] = pen;
        }

        return pen;
    }

    /// <summary>Disposes every cached pen and brush (theme change). Painting re-creates what it needs.</summary>
    public static void Clear()
    {
        foreach (SolidBrush brush in Brushes.Values)
        {
            brush.Dispose();
        }

        foreach (Pen pen in Pens.Values)
        {
            pen.Dispose();
        }

        foreach (Pen pen in DashedPens.Values)
        {
            pen.Dispose();
        }

        Brushes.Clear();
        Pens.Clear();
        DashedPens.Clear();
    }

    /// <summary>
    /// The pen width to ask GDI+ for so a stroke keeps its weight: antialiased pens of 1.5 device pixels
    /// or less are drawn with only 1 px of coverage, so widths from 1.25 to 1.5 are nudged past that
    /// threshold (1.5 px strokes otherwise look half as heavy as in the references).
    /// </summary>
    public static float StrokeWidth(float deviceWidth) => deviceWidth is > 1.25f and <= 1.5f ? 1.501f : deviceWidth;

    /// <summary>Number of cached GDI+ objects (tests check that theme switches do not grow it).</summary>
    public static int Count => Brushes.Count + Pens.Count + DashedPens.Count;

    private static Pen GetPen(Color color, float width, bool round)
    {
        (int, float, bool) key = (color.ToArgb(), MathF.Round(width, 3), round);
        if (!Pens.TryGetValue(key, out Pen? pen))
        {
            pen = new Pen(color, StrokeWidth(width));
            if (round)
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
            }

            Pens[key] = pen;
        }

        return pen;
    }
}
