namespace MediaVerifier.UI.Theme;

/// <summary>The eight type styles of subplan 08 at one DPI. No other sizes or weights exist.</summary>
internal sealed class TypeRamp
{
    private const string UiFamily = "Segoe UI";
    private const string UiSemiboldFamily = "Segoe UI Semibold";
    private const string MonoFamily = "Consolas";

    private static readonly Dictionary<int, TypeRamp> Cache = [];
    private static readonly Lock Gate = new();

    private TypeRamp(int dpi)
    {
        Dpi = dpi;
        Caption = Create(UiFamily, 11, semibold: false);
        CaptionStrong = Create(UiFamily, 11, semibold: true);
        Body = Create(UiFamily, 13, semibold: false);
        BodyStrong = Create(UiFamily, 13, semibold: true);
        Subtitle = Create(UiFamily, 16, semibold: true);
        Title = Create(UiFamily, 20, semibold: true);
        Metric = Create(UiFamily, 28, semibold: true);
        Mono = Create(MonoFamily, 12, semibold: false);
    }

    public int Dpi { get; }

    /// <summary>11 px, 400. Secondary facts, timestamps, helper text. Line height 16.</summary>
    public Font Caption { get; }

    /// <summary>11 px, 600. Card titles, column headers, badge text. Line height 16.</summary>
    public Font CaptionStrong { get; }

    /// <summary>13 px, 400. Default. Line height 20.</summary>
    public Font Body { get; }

    /// <summary>13 px, 600. File names, selected items, primary-button labels. Line height 20.</summary>
    public Font BodyStrong { get; }

    /// <summary>16 px, 600. Dialog and card headings. Line height 24.</summary>
    public Font Subtitle { get; }

    /// <summary>20 px, 600. Page title. Line height 28.</summary>
    public Font Title { get; }

    /// <summary>28 px, 600. Large numbers. Line height 36.</summary>
    public Font Metric { get; }

    /// <summary>Consolas 12 px. Hashes, raw paths, logs. Line height 16.</summary>
    public Font Mono { get; }

    public static TypeRamp For(int dpi)
    {
        if (dpi <= 0)
        {
            dpi = 96;
        }

        lock (Gate)
        {
            if (!Cache.TryGetValue(dpi, out TypeRamp? ramp))
            {
                ramp = new TypeRamp(dpi);
                Cache[dpi] = ramp;
            }

            return ramp;
        }
    }

    private Font Create(string family, float pixels, bool semibold)
    {
        float size = pixels * Dpi / 96f;
        if (semibold)
        {
            Font semi = new(UiSemiboldFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
            if (string.Equals(semi.Name, UiSemiboldFamily, StringComparison.OrdinalIgnoreCase))
            {
                return semi;
            }

            semi.Dispose();
            return new Font(family, size, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        return new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
    }
}
