using System.Drawing.Drawing2D;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// Shared painting helpers. One reusable path keeps rounded shapes allocation-free while painting
/// (UI thread only).
/// </summary>
internal static class KitPaint
{
    private const TextFormatFlags BaseFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    private static readonly GraphicsPath Scratch = new();

    /// <summary>
    /// Draws one line of text whose vertical centre sits on <paramref name="centreY"/> (the mockups
    /// position text by its centre). Text that does not fit ends with an ellipsis.
    /// </summary>
    public static void DrawText(Graphics g, string text, Font font, Color color, int x, int centreY, int maxWidth, TextFormatFlags extra = TextFormatFlags.Default)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return;
        }

        int lineHeight = NativeText.LineHeight(font);
        NativeText.Draw(g, text, font, color, new Rectangle(x, centreY - (lineHeight / 2), maxWidth, lineHeight), BaseFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | extra);
    }

    /// <summary>Text centred horizontally on <paramref name="centreX"/>.</summary>
    public static void DrawTextCentred(Graphics g, string text, Font font, Color color, int centreX, int centreY)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int width = NativeText.Measure(text, font);
        int lineHeight = NativeText.LineHeight(font);
        NativeText.Draw(g, text, font, color, new Rectangle(centreX - (width / 2) - 1, centreY - (lineHeight / 2), width + 2, lineHeight), BaseFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }

    public static int MeasureWidth(string text, Font font) => NativeText.Measure(text, font);

    public static void FillRoundedRect(Graphics g, RectangleF rect, float radius, Color color)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        if (radius <= 0)
        {
            g.FillRectangle(GdiCache.Brush(color), rect);
            return;
        }

        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Scratch.Reset();
        IconGeometry.AddRoundedRect(Scratch, rect, radius);
        g.FillPath(GdiCache.Brush(color), Scratch);
        g.SmoothingMode = previous;
    }

    /// <summary>A border drawn inside the element's bounds (subplan 08: borders are inside).</summary>
    public static void StrokeRoundedRect(Graphics g, RectangleF rect, float radius, Color color, float width = 1f)
    {
        if (rect.Width <= width || rect.Height <= width)
        {
            return;
        }

        float half = width / 2f;
        RectangleF inner = RectangleF.FromLTRB(rect.Left + half, rect.Top + half, rect.Right - half, rect.Bottom - half);
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = radius > 0 ? SmoothingMode.AntiAlias : SmoothingMode.None;
        Scratch.Reset();
        IconGeometry.AddRoundedRect(Scratch, inner, Math.Max(0, radius - half));
        g.DrawPath(GdiCache.Pen(color, width), Scratch);
        g.SmoothingMode = previous;
    }

    /// <summary>
    /// Focus ring: 2 px Accent, 1 px away from the control, so its outer edge is the control inflated
    /// by 3 px with radius + 3 (subplan 08).
    /// </summary>
    public static void DrawFocusRing(Graphics g, RectangleF control, float radius, int dpi)
    {
        float gap = Dpi.ScaleF(1, dpi);
        float width = Dpi.ScaleF(2, dpi);
        RectangleF outer = RectangleF.Inflate(control, gap + width, gap + width);
        StrokeRoundedRect(g, outer, radius + gap + width, ThemeService.Tokens.Accent, width);
    }

    /// <summary>
    /// Paints the part of the keyboard focus ring that falls on <paramref name="host"/>, when the focused kit control is
    /// inside it. A ring lies 1 px outside its control, where the control cannot paint; every kit control calls this
    /// after painting itself, and other containers (dialogs, panels) call it at the end of their paint.
    /// </summary>
    public static void PaintFocusRings(Graphics g, Control host)
    {
        if (KitControl.FocusRingOwner is { } owner)
        {
            PaintFocusRing(g, host, owner);
        }
    }

    /// <summary>Paints <paramref name="owner"/>'s focus ring on <paramref name="host"/> when the host contains it.</summary>
    internal static void PaintFocusRing(Graphics g, Control host, KitControl owner)
    {
        if (!host.Contains(owner) || owner.FocusRingBounds.IsEmpty)
        {
            return;
        }

        Rectangle ring = owner.FocusRingBounds;
        for (Control child = owner; child != host && child.Parent is Control parent; child = parent)
        {
            ring.Offset(child.Left, child.Top);
        }

        // A clip the host's own painting left behind (lists clip their rows) must not cut the ring. Only the containers of
        // the focused control get here, so saving the state costs nothing elsewhere.
        System.Drawing.Drawing2D.GraphicsState saved = g.Save();
        g.ResetClip();
        DrawFocusRing(g, ring, owner.FocusRingRadius, owner.DeviceDpi);
        g.Restore(saved);
    }

    public static void HorizontalLine(Graphics g, Color color, float x1, float x2, float y, float width = 1f) =>
        g.FillRectangle(GdiCache.Brush(color), x1, y, x2 - x1, width);

    public static void VerticalLine(Graphics g, Color color, float x, float y1, float y2, float width = 1f) =>
        g.FillRectangle(GdiCache.Brush(color), x, y1, width, y2 - y1);

    public static ThemeTokens Tokens => ThemeService.Tokens;
}
