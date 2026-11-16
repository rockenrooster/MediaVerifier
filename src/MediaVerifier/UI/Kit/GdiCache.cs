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

    private static Pen GetPen(Color color, float width, bool round)
    {
        (int, float, bool) key = (color.ToArgb(), MathF.Round(width, 3), round);
        if (!Pens.TryGetValue(key, out Pen? pen))
        {
            pen = new Pen(color, width);
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
