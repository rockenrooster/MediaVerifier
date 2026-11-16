using MediaVerifier.UI.Kit;

namespace MediaVerifier.UI.Theme;

/// <summary>
/// GDI resources cached per theme and DPI (subplan 08): pens and brushes in <see cref="GdiCache"/>,
/// fonts in <see cref="TypeRamp"/>, shadows in <see cref="ShadowCache"/>. Painting only reads from these
/// caches, so it allocates nothing; a theme change disposes the colour-keyed objects and the shadows.
/// UI thread only.
/// </summary>
internal static class ThemeResources
{
    public static TypeRamp Fonts(int dpi) => TypeRamp.For(dpi);

    public static SolidBrush Brush(Color color) => GdiCache.Brush(color);

    public static Pen Pen(Color color, float width) => GdiCache.Pen(color, width);

    public static Pen RoundPen(Color color, float width) => GdiCache.RoundPen(color, width);

    /// <summary>Disposes everything keyed by colour; called by <see cref="ThemeService.Set"/>.</summary>
    public static void Reset()
    {
        GdiCache.Clear();
        ShadowCache.Clear();
    }
}
