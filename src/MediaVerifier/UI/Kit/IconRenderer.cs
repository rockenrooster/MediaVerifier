using System.Drawing.Drawing2D;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>Draws icons, severity shapes and the app icon from geometry (subplan 08, Icons).</summary>
internal static class IconRenderer
{
    private static readonly GraphicsPath CriticalShape = Polygon(5, 1.5f, 11, 1.5f, 14.5f, 5, 14.5f, 11, 11, 14.5f, 5, 14.5f, 1.5f, 11, 1.5f, 5);
    private static readonly GraphicsPath HighShape = Polygon(8, 2.25f, 14.5f, 13.5f, 1.5f, 13.5f);
    private static readonly GraphicsPath MediumShape = Polygon(8, 1.5f, 14.5f, 8, 8, 14.5f, 1.5f, 8);
    private static readonly GraphicsPath CircleShape = Ellipse(8, 8, 6.75f);
    private static readonly GraphicsPath AppShield = SvgPath("M128 18 L226 54 V124 C226 182 185 224 128 242 C71 224 30 182 30 124 V54 Z");
    private static readonly GraphicsPath AppTriangleStandard = Polygon(104, 86, 180, 128, 104, 170);
    private static readonly GraphicsPath AppTriangleSmall = Polygon(98, 80, 186, 128, 98, 176);

    /// <summary>Stroke width in logical pixels for an icon drawn at <paramref name="logicalSize"/>.</summary>
    public static float StrokeForSize(float logicalSize) => logicalSize switch
    {
        <= 16 => 1.5f,
        <= 24 => 1.75f,
        <= 32 => 2.2f,
        _ => 2.5f,
    };

    /// <summary>
    /// Draws <paramref name="icon"/> in a square at (x, y) of <paramref name="logicalSize"/> logical
    /// pixels, scaled to <paramref name="dpi"/>.
    /// </summary>
    public static void Draw(Graphics g, IconName icon, float x, float y, float logicalSize, int dpi, Color color)
    {
        IconShape shape = IconGeometry.Get(icon);
        float scale = logicalSize * dpi / 96f / 16f;
        float strokeGrid = StrokeForSize(logicalSize) * 16f / logicalSize;
        GraphicsState state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(x, y);
            g.ScaleTransform(scale, scale);
            if (shape.Stroked.PointCount > 0)
            {
                Pen pen = GdiCache.RoundPen(color, strokeGrid);
                if (shape.DashPattern is null)
                {
                    g.DrawPath(pen, shape.Stroked);
                }
                else
                {
                    using Pen dashed = (Pen)pen.Clone();
                    dashed.DashPattern = [shape.DashPattern[0] / strokeGrid, shape.DashPattern[1] / strokeGrid];
                    dashed.DashCap = DashCap.Flat;
                    g.DrawPath(dashed, shape.Stroked);
                }
            }

            if (shape.Filled.PointCount > 0)
            {
                g.FillPath(GdiCache.Brush(color), shape.Filled);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    /// <summary>Draws the tier's shape at (x, y) with its glyph knocked out in <paramref name="knockout"/>.</summary>
    public static void DrawSeverity(Graphics g, Severity severity, float x, float y, float logicalSize, int dpi, Color shapeColor, Color knockout)
    {
        float scale = logicalSize * dpi / 96f / 16f;
        GraphicsState state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(x, y);
            g.ScaleTransform(scale, scale);
            SolidBrush fill = GdiCache.Brush(shapeColor);
            Pen glyph = GdiCache.RoundPen(knockout, 1.7f);
            switch (severity)
            {
                case Severity.Critical:
                    g.FillPath(fill, CriticalShape);
                    g.DrawPath(GdiCache.RoundPen(shapeColor, 1f), CriticalShape);
                    g.DrawLine(glyph, 5.75f, 5.75f, 10.25f, 10.25f);
                    g.DrawLine(glyph, 10.25f, 5.75f, 5.75f, 10.25f);
                    break;
                case Severity.High:
                    g.FillPath(fill, HighShape);
                    g.DrawPath(GdiCache.RoundPen(shapeColor, 1.5f), HighShape);
                    g.DrawLine(glyph, 8, 6.25f, 8, 9.5f);
                    FillDot(g, knockout, 8, 11.6f, 0.95f);
                    break;
                case Severity.Medium:
                    g.FillPath(fill, MediumShape);
                    g.DrawPath(GdiCache.RoundPen(shapeColor, 1.2f), MediumShape);
                    g.DrawLine(glyph, 8, 4.9f, 8, 8.6f);
                    FillDot(g, knockout, 8, 10.9f, 0.95f);
                    break;
                case Severity.Low:
                    g.FillPath(fill, CircleShape);
                    FillDot(g, knockout, 8, 5, 0.95f);
                    g.DrawLine(glyph, 8, 7.4f, 8, 11.2f);
                    break;
                default:
                    g.FillPath(fill, CircleShape);
                    g.DrawLines(glyph, [new PointF(4.9f, 8.2f), new PointF(7.2f, 10.4f), new PointF(11.1f, 5.9f)]);
                    break;
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    /// <summary>
    /// The app icon: a shield holding a play triangle on a 256 grid. <paramref name="small"/> selects
    /// the flat version used at 24 px and below.
    /// </summary>
    public static void DrawAppIcon(Graphics g, RectangleF bounds, bool small, bool smallTriangleOnGradient = false)
    {
        GraphicsState state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(bounds.X, bounds.Y);
            g.ScaleTransform(bounds.Width / 256f, bounds.Height / 256f);
            if (small)
            {
                g.FillPath(GdiCache.Brush(AppIconColors.Flat), AppShield);
            }
            else
            {
                using LinearGradientBrush gradient = new(new PointF(0, 18), new PointF(0, 242), AppIconColors.GradientTop, AppIconColors.GradientBottom);
                g.FillPath(gradient, AppShield);
            }

            GraphicsPath triangle = small || smallTriangleOnGradient ? AppTriangleSmall : AppTriangleStandard;
            g.FillPath(GdiCache.Brush(AppIconColors.Mark), triangle);
            g.DrawPath(GdiCache.RoundPen(AppIconColors.Mark, 14f), triangle);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static void FillDot(Graphics g, Color color, float cx, float cy, float r) =>
        g.FillEllipse(GdiCache.Brush(color), cx - r, cy - r, r * 2, r * 2);

    private static GraphicsPath Polygon(params float[] coordinates)
    {
        PointF[] points = new PointF[coordinates.Length / 2];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new PointF(coordinates[i * 2], coordinates[(i * 2) + 1]);
        }

        GraphicsPath path = new();
        path.AddPolygon(points);
        return path;
    }

    private static GraphicsPath Ellipse(float cx, float cy, float r)
    {
        GraphicsPath path = new();
        path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
        return path;
    }

    private static GraphicsPath SvgPath(string data)
    {
        GraphicsPath path = new();
        IconGeometry.AddSvgPath(path, data);
        return path;
    }
}

/// <summary>App icon colours (subplan 08, App icon). Part of the icon's specification, not the theme.</summary>
internal static class AppIconColors
{
    public static readonly Color GradientTop = ThemeTokens.Hex(0x6ED3FF);
    public static readonly Color GradientBottom = ThemeTokens.Hex(0x1F6FE0);
    public static readonly Color Flat = ThemeTokens.Hex(0x2B8CEB);
    public static readonly Color Mark = ThemeTokens.Hex(0xFFFFFF);
}
