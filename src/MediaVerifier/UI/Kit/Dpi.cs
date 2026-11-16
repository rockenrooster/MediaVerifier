namespace MediaVerifier.UI.Kit;

/// <summary>Logical pixels (96 DPI) to device pixels, rounded to whole pixels (subplan 08).</summary>
internal static class Dpi
{
    public static int Scale(int logical, int dpi) =>
        (int)Math.Round(logical * (dpi <= 0 ? 96 : dpi) / 96.0, MidpointRounding.AwayFromZero);

    public static float ScaleF(float logical, int dpi) => logical * (dpi <= 0 ? 96 : dpi) / 96f;

    public static Rectangle Scale(Rectangle logical, int dpi) => Rectangle.FromLTRB(
        Scale(logical.Left, dpi),
        Scale(logical.Top, dpi),
        Scale(logical.Right, dpi),
        Scale(logical.Bottom, dpi));

    public static Size Scale(Size logical, int dpi) => new(Scale(logical.Width, dpi), Scale(logical.Height, dpi));
}
