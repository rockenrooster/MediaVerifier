using System.Drawing.Drawing2D;
using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>Painters for the data graphics of subplan 08 (task UK-10). Nothing here allocates per paint.</summary>
internal static class DataPaint
{
    private static readonly GraphicsPath Scratch = new();
    private static PointF[] s_points = new PointF[64];

    /// <summary>Progress bar: track Stroke, fill Accent; a non-zero value is never narrower than the height.</summary>
    public static void ProgressBar(Graphics g, RectangleF bounds, double fraction)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = bounds.Height / 2f;
        KitPaint.FillRoundedRect(g, bounds, radius, t.Stroke);
        if (fraction > 0)
        {
            float width = Math.Max(bounds.Height, (float)(bounds.Width * Math.Clamp(fraction, 0, 1)));
            KitPaint.FillRoundedRect(g, new RectangleF(bounds.X, bounds.Y, width, bounds.Height), radius, t.Accent);
        }
    }

    /// <summary>Indeterminate bar: a segment 40 % of the track at <paramref name="phase"/> (0..1 of a 1.5 s cycle, eased).</summary>
    public static void IndeterminateBar(Graphics g, RectangleF bounds, double phase)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = bounds.Height / 2f;
        KitPaint.FillRoundedRect(g, bounds, radius, t.Stroke);
        double eased = phase < 0.5 ? 4 * phase * phase * phase : 1 - (Math.Pow((-2 * phase) + 2, 3) / 2);
        float segment = bounds.Width * 0.4f;
        float x = bounds.X - segment + (float)(eased * (bounds.Width + segment));
        RectangleF bar = RectangleF.Intersect(new RectangleF(x, bounds.Y, segment, bounds.Height), bounds);
        if (bar.Width > 0)
        {
            KitPaint.FillRoundedRect(g, bar, radius, t.Accent);
        }
    }

    /// <summary>Progress ring: a 259° arc, 2 px Accent, starting at <paramref name="angle"/> degrees.</summary>
    public static void ProgressRing(Graphics g, PointF centre, float radius, float angle, int dpi, Color? color = null)
    {
        float width = Dpi.ScaleF(2, dpi);
        float r = radius - (width / 2f);
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawArc(GdiCache.RoundPen(color ?? ThemeService.Tokens.Accent, width), centre.X - r, centre.Y - r, 2 * r, 2 * r, angle, 259.2f);
        g.SmoothingMode = previous;
    }

    /// <summary>
    /// Severity bar: one segment per tier with a count, in the order Critical, High, Medium, Low, OK;
    /// widths proportional with a 4 px minimum, 2 px gaps, radius 2. Writes each segment's bounds into
    /// <paramref name="segments"/> (index by tier: 0 Critical .. 4 OK; empty when absent) for hit testing.
    /// </summary>
    public static void SeverityBar(Graphics g, RectangleF bounds, ReadOnlySpan<long> counts, Span<RectangleF> segments, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        long total = 0;
        int present = 0;
        for (int i = 0; i < 5; i++)
        {
            total += Math.Max(0, counts[i]);
            present += counts[i] > 0 ? 1 : 0;
            segments[i] = RectangleF.Empty;
        }

        if (total == 0 || present == 0)
        {
            KitPaint.FillRoundedRect(g, bounds, Dpi.ScaleF(2, dpi), t.Stroke);
            return;
        }

        float gap = Dpi.ScaleF(2, dpi);
        float minimum = Dpi.ScaleF(4, dpi);
        float available = bounds.Width - (gap * (present - 1));

        // Proportional widths with a minimum: segments below it take the minimum and the rest share what is left.
        float reserved = 0;
        long large = 0;
        for (int i = 0; i < 5; i++)
        {
            if (counts[i] > 0 && available * counts[i] / total < minimum)
            {
                reserved += minimum;
            }
            else if (counts[i] > 0)
            {
                large += counts[i];
            }
        }

        float x = bounds.X;
        for (int i = 0; i < 5; i++)
        {
            if (counts[i] <= 0)
            {
                continue;
            }

            float width = available * counts[i] / total < minimum || large == 0 ? minimum : (available - reserved) * counts[i] / large;
            RectangleF segment = new(x, bounds.Y, width, bounds.Height);
            segments[i] = segment;
            KitPaint.FillRoundedRect(g, segment, Dpi.ScaleF(2, dpi), Tier(i, t).Fill);
            x += width + gap;
        }
    }

    /// <summary>Donut: track Stroke, value arc from 12 o'clock clockwise with round ends.</summary>
    public static void Donut(Graphics g, PointF centre, float outerRadius, float thickness, double fraction, Color value)
    {
        ThemeTokens t = ThemeService.Tokens;
        float r = outerRadius - (thickness / 2f);
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawEllipse(GdiCache.Pen(t.Stroke, thickness), centre.X - r, centre.Y - r, 2 * r, 2 * r);
        double clamped = Math.Clamp(fraction, 0, 1);
        if (clamped > 0)
        {
            Pen pen = GdiCache.RoundPen(value, thickness);
            if (clamped >= 0.999)
            {
                g.DrawEllipse(pen, centre.X - r, centre.Y - r, 2 * r, 2 * r);
            }
            else
            {
                g.DrawArc(pen, centre.X - r, centre.Y - r, 2 * r, 2 * r, -90, (float)(360 * clamped));
            }
        }

        g.SmoothingMode = previous;
    }

    /// <summary>Sparkline: 1.5 px Accent line with round joins, the area below at 14 % opacity, a dot r 2.5 on the last point.</summary>
    public static void Sparkline(Graphics g, RectangleF bounds, ReadOnlySpan<double> values, int dpi)
    {
        if (values.Length < 2)
        {
            return;
        }

        ThemeTokens t = ThemeService.Tokens;
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (double v in values)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        double range = max - min <= 0 ? 1 : max - min;
        if (s_points.Length < values.Length + 2)
        {
            s_points = new PointF[values.Length + 2];
        }

        float inset = Dpi.ScaleF(3, dpi);
        float top = bounds.Y + inset;
        float height = bounds.Height - (2 * inset);
        for (int i = 0; i < values.Length; i++)
        {
            float x = bounds.X + (bounds.Width * i / (values.Length - 1));
            float y = top + height - (float)((values[i] - min) / range * height);
            s_points[i] = new PointF(x, y);
        }

        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Scratch.Reset();
        Scratch.AddLines(new ReadOnlySpan<PointF>(s_points, 0, values.Length));
        Scratch.AddLine(s_points[values.Length - 1].X, s_points[values.Length - 1].Y, bounds.Right, bounds.Bottom);
        Scratch.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        Scratch.CloseFigure();
        g.FillPath(GdiCache.Brush(Color.FromArgb(36, t.Accent)), Scratch);
        Scratch.Reset();
        Scratch.AddLines(new ReadOnlySpan<PointF>(s_points, 0, values.Length));
        g.DrawPath(GdiCache.RoundPen(t.Accent, Dpi.ScaleF(1.5f, dpi)), Scratch);
        float dot = Dpi.ScaleF(2.5f, dpi);
        PointF last = s_points[values.Length - 1];
        g.FillEllipse(GdiCache.Brush(t.Accent), last.X - dot, last.Y - dot, 2 * dot, 2 * dot);
        g.SmoothingMode = previous;
    }

    /// <summary>
    /// Timeline: a 1 px Stroke vertical line; each event a circle r 4 (Surface fill, 1.5 px StrokeStrong
    /// outline), text in Body, time in Caption Text3 right-aligned. Rows are <paramref name="pitch"/> apart.
    /// </summary>
    public static void Timeline(Graphics g, Rectangle bounds, IReadOnlyList<(string Text, string Time)> events, int pitch, int dpi)
    {
        if (events.Count == 0)
        {
            return;
        }

        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float lineX = bounds.X + Dpi.ScaleF(4, dpi);
        int first = bounds.Y + (pitch / 2);
        int last = first + ((events.Count - 1) * pitch);
        KitPaint.VerticalLine(g, t.Stroke, lineX, first, last, Dpi.ScaleF(1, dpi));
        float r = Dpi.ScaleF(4, dpi);
        SmoothingMode previous = g.SmoothingMode;
        for (int i = 0; i < events.Count; i++)
        {
            int y = first + (i * pitch);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(GdiCache.Brush(t.Surface), lineX + 0.5f - r, y - r, 2 * r, 2 * r);
            g.DrawEllipse(GdiCache.Pen(t.StrokeStrong, Dpi.ScaleF(1.5f, dpi)), lineX + 0.5f - r, y - r, 2 * r, 2 * r);
            g.SmoothingMode = previous;
            (string text, string time) = events[i];
            int timeWidth = KitPaint.MeasureWidth(time, type.Caption);
            int textX = bounds.X + Dpi.Scale(20, dpi);
            KitPaint.DrawText(g, text, type.Body, t.Text, textX, y, bounds.Right - textX - timeWidth - Dpi.Scale(12, dpi));
            KitPaint.DrawText(g, time, type.Caption, t.Text3, bounds.Right - timeWidth, y, timeWidth + 2);
        }
    }

    public static TierColors Tier(int index, ThemeTokens t) => index switch
    {
        0 => t.Critical,
        1 => t.High,
        2 => t.Medium,
        3 => t.Low,
        _ => t.Ok,
    };

    public static Severity TierSeverity(int index) => index switch
    {
        0 => Severity.Critical,
        1 => Severity.High,
        2 => Severity.Medium,
        3 => Severity.Low,
        _ => Severity.None,
    };

    /// <summary>
    /// The severity bar segment under <paramref name="x"/> (0 Critical .. 4 OK), or -1: each segment takes the 2 px gap
    /// after it, so the pointer is never between two. <paramref name="segments"/> is what <see cref="SeverityBar"/> wrote.
    /// </summary>
    public static int SegmentAt(ReadOnlySpan<RectangleF> segments, float x, int dpi)
    {
        float gap = Dpi.ScaleF(2, dpi);
        for (int i = 0; i < segments.Length; i++)
        {
            RectangleF segment = segments[i];
            if (!segment.IsEmpty && x >= segment.Left && x < segment.Right + gap)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A segment's tooltip, "<tier>: <count>" (subplan 08, Severity bar).</summary>
    public static string SegmentToolTip(int index, long count) =>
        BadgePainter.TierWord(TierSeverity(index)) + ": " + count.ToString("N0", CultureInfo.CurrentCulture);
}
