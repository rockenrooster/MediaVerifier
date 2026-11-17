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
}

/// <summary>A progress bar control (8 px, or 4 px in rows).</summary>
internal sealed class KitProgressBar : KitControl
{
    private double _value;
    private bool _indeterminate;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private long _start;

    public KitProgressBar()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        _timer.Tick += (_, _) => Invalidate();
    }

    /// <summary>0..1.</summary>
    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            AccessibleName = Math.Round(_value * 100).ToString(CultureInfo.CurrentCulture) + " %";
            Invalidate();
        }
    }

    /// <summary>Shows the travelling segment instead of a value (no animation when Windows' effects are off).</summary>
    public bool Indeterminate
    {
        get => _indeterminate;
        set
        {
            _indeterminate = value;
            _start = Environment.TickCount64;
            UpdateTimer();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        RectangleF bounds = new(0, (Height - Math.Min(Height, S(8))) / 2f, Width, Math.Min(Height, S(8)));
        if (_indeterminate)
        {
            double phase = SystemInformation.UIEffectsEnabled ? (Environment.TickCount64 - _start) % 1500 / 1500.0 : 0.5;
            DataPaint.IndeterminateBar(e.Graphics, bounds, phase);
        }
        else
        {
            DataPaint.ProgressBar(e.Graphics, bounds, _value);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateTimer();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateTimer() => _timer.Enabled = _indeterminate && Visible && SystemInformation.UIEffectsEnabled;
}

/// <summary>A progress ring (radius 8, or 10 beside a Subtitle) rotating clockwise once per second while visible.</summary>
internal sealed class ProgressRing : KitControl
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly long _start = Environment.TickCount64;
    private int _logicalRadius = 8;

    public ProgressRing()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.Animation;
        _timer.Tick += (_, _) => Invalidate();
    }

    public int LogicalRadius
    {
        get => _logicalRadius;
        set
        {
            _logicalRadius = value;
            Invalidate();
        }
    }

    /// <summary>Colour override (the Busy state of filled buttons draws the ring in their On… colour).</summary>
    public Color? Color { get; set; }

    public override Size GetPreferredSize(Size proposedSize) => new(S(2 * _logicalRadius), S(2 * _logicalRadius));

    protected override void OnPaint(PaintEventArgs e)
    {
        float angle = SystemInformation.UIEffectsEnabled ? (Environment.TickCount64 - _start) % 1000 / 1000f * 360f : 0;
        DataPaint.ProgressRing(e.Graphics, new PointF(Width / 2f, Height / 2f), SF(_logicalRadius), angle - 90, DeviceDpi, Color);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _timer.Enabled = Visible && SystemInformation.UIEffectsEnabled;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _timer.Enabled = Visible && SystemInformation.UIEffectsEnabled;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>The severity bar: clickable segments with tooltips "<tier>: <count>" (subplan 08).</summary>
internal sealed class SeverityBar : KitControl
{
    private readonly long[] _counts = new long[5];
    private readonly RectangleF[] _segments = new RectangleF[5];
    private int _hover = -1;

    public SeverityBar()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.Chart;
    }

    /// <summary>Raised when a tier segment is clicked; the value is the tier.</summary>
    public event EventHandler<Severity>? SegmentClicked;

    public void SetCounts(long critical, long high, long medium, long low, long ok)
    {
        _counts[0] = critical;
        _counts[1] = high;
        _counts[2] = medium;
        _counts[3] = low;
        _counts[4] = ok;
        AccessibleName = string.Join(", ", Enumerable.Range(0, 5).Where(i => _counts[i] > 0).Select(i => $"{BadgePainter.TierWord(DataPaint.TierSeverity(i))}: {_counts[i].ToString("N0", CultureInfo.CurrentCulture)}"));
        Invalidate();
    }

    /// <summary>The tier under a point, or null.</summary>
    public Severity? TierAt(Point point)
    {
        for (int i = 0; i < 5; i++)
        {
            RectangleF segment = _segments[i];
            if (!segment.IsEmpty && point.X >= segment.Left && point.X < segment.Right + SF(2) && point.Y >= 0 && point.Y < Height)
            {
                return DataPaint.TierSeverity(i);
            }
        }

        return null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float height = SF(8);
        DataPaint.SeverityBar(e.Graphics, new RectangleF(0, (Height - height) / 2f, Width, height), _counts, _segments, DeviceDpi);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Severity? tier = TierAt(e.Location);
        int index = tier is Severity s ? Array.IndexOf([Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.None], s) : -1;
        if (index != _hover)
        {
            _hover = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            KitToolTip.Hide();
            KitToolTip.Set(this, index >= 0 ? $"{BadgePainter.TierWord(DataPaint.TierSeverity(index))}: {_counts[index].ToString("N0", CultureInfo.CurrentCulture)}" : string.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && TierAt(e.Location) is Severity tier)
        {
            SegmentClicked?.Invoke(this, tier);
        }
    }
}
