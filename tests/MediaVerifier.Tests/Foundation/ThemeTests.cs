using System.Globalization;
using System.Runtime.InteropServices;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.Tests.Foundation;

/// <summary>Tokens, contrast and resource lifetime (tasks UK-01, UK-02, UK-14).</summary>
[Collection(UiCollection.Name)]
public sealed class ThemeTests
{
    /// <summary>Every pair listed in docs/design/tokens.md, checked against the implemented tokens (UK-02).</summary>
    [Fact]
    public void Every_contrast_pair_in_tokens_md_passes_with_the_implemented_colours()
    {
        string[] lines = File.ReadAllLines(RepositoryPaths.Combine("docs", "design", "tokens.md"));
        ThemeTokens? tokens = null;
        double required = 0;
        int checkedPairs = 0;
        List<string> failures = [];
        bool inContrast = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("## Contrast", StringComparison.Ordinal))
            {
                inContrast = true;
                continue;
            }

            if (!inContrast)
            {
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                tokens = line[4..].Trim() == "Dark" ? ThemeTokens.Dark : ThemeTokens.Light;
                continue;
            }

            if (line.StartsWith("#### ", StringComparison.Ordinal))
            {
                int open = line.IndexOf("required ", StringComparison.Ordinal);
                required = double.Parse(line[(open + 9)..].TrimEnd(')'), CultureInfo.InvariantCulture);
                continue;
            }

            string[] cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (tokens is null || cells.Length < 4 || cells[1] is "Foreground" or "---" || cells[1].Length == 0)
            {
                continue;
            }

            double ratio = Contrast(Token(tokens, cells[1]), Token(tokens, cells[2]));
            checkedPairs++;
            if (ratio + 0.005 < required)
            {
                failures.Add($"{tokens.Name}: {cells[1]} on {cells[2]} is {ratio:F2}, needs {required}");
            }
        }

        Assert.True(checkedPairs >= 140, $"only {checkedPairs} pairs parsed");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Token_values_equal_tokens_md()
    {
        foreach (string line in File.ReadAllLines(RepositoryPaths.Combine("docs", "design", "tokens.md")))
        {
            string[] cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 4 || !cells[2].StartsWith("`#", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Equal(Hex(cells[2]), Token(ThemeTokens.Dark, cells[1]).ToArgb() & 0xFFFFFF);
            Assert.Equal(Hex(cells[3]), Token(ThemeTokens.Light, cells[1]).ToArgb() & 0xFFFFFF);
        }
    }

    [Fact]
    public void High_contrast_tokens_map_to_system_colours()
    {
        ThemeTokens hc = ThemeTokens.HighContrast();
        Assert.True(hc.IsHighContrast);
        Assert.Equal(SystemColors.Window.ToArgb(), hc.Surface.ToArgb());
        Assert.Equal(SystemColors.WindowText.ToArgb(), hc.Text3.ToArgb());
        Assert.Equal(SystemColors.Highlight.ToArgb(), hc.Selected.ToArgb());
        Assert.Equal(SystemColors.HighlightText.ToArgb(), hc.OnAccent.ToArgb());
        Assert.Equal(SystemColors.WindowText.ToArgb(), hc.Critical.Fill.ToArgb());
        Assert.Equal(SystemColors.Window.ToArgb(), hc.Ok.Pill.ToArgb());
    }

    [Fact]
    public void Two_hundred_theme_switches_do_not_grow_gdi_handles()
    {
        ThemeTokens original = ThemeService.Tokens;
        using Bitmap canvas = new(400, 200);
        using Graphics g = Graphics.FromImage(canvas);
        void Paint()
        {
            ThemeTokens t = ThemeService.Tokens;
            KitPaint.FillRoundedRect(g, new RectangleF(10, 10, 200, 40), 4, t.Surface);
            KitPaint.StrokeRoundedRect(g, new RectangleF(10, 10, 200, 40), 4, t.Stroke);
            KitPaint.DrawText(g, "Scan folder", TypeRamp.For(96).Body, t.Text, 20, 30, 180);
            IconRenderer.Draw(g, IconName.Play, 220, 20, 16, 96, t.Accent);
            ShadowCache.Draw(g, new Rectangle(20, 60, 120, 40), 8, 96, t.Shadow);
        }

        for (int i = 0; i < 10; i++)
        {
            ThemeService.Set(i % 2 == 0 ? ThemeTokens.Dark : ThemeTokens.Light);
            Paint();
        }

        uint before = GetGuiResources(GetCurrentProcess(), 0);
        int cachedBefore = GdiCache.Count;
        for (int i = 0; i < 200; i++)
        {
            ThemeService.Set(i % 2 == 0 ? ThemeTokens.Dark : ThemeTokens.Light);
            Paint();
        }

        uint after = GetGuiResources(GetCurrentProcess(), 0);
        Assert.True(after <= before + 8, $"GDI objects grew from {before} to {after}");
        Assert.True(GdiCache.Count <= cachedBefore + 4, $"cache grew from {cachedBefore} to {GdiCache.Count}");
        ThemeService.Set(original);
    }

    private static Color Token(ThemeTokens t, string name) => name switch
    {
        "Bg" => t.Bg,
        "Surface" => t.Surface,
        "Raised" => t.Raised,
        "Hover" => t.Hover,
        "Pressed" => t.Pressed,
        "Selected" => t.Selected,
        "Stroke" => t.Stroke,
        "StrokeStrong" => t.StrokeStrong,
        "Text" => t.Text,
        "Text2" => t.Text2,
        "Text3" => t.Text3,
        "TextDisabled" => t.TextDisabled,
        "Accent" => t.Accent,
        "AccentHover" => t.AccentHover,
        "AccentPressed" => t.AccentPressed,
        "OnAccent" => t.OnAccent,
        "DangerFill" => t.DangerFill,
        "DangerHover" => t.DangerHover,
        "DangerPressed" => t.DangerPressed,
        "OnDanger" => t.OnDanger,
        "NeutralPill" => t.NeutralPill,
        "CritText" => t.Critical.Text,
        "CritFill" => t.Critical.Fill,
        "CritPill" => t.Critical.Pill,
        "HighText" => t.High.Text,
        "HighFill" => t.High.Fill,
        "HighPill" => t.High.Pill,
        "MedText" => t.Medium.Text,
        "MedFill" => t.Medium.Fill,
        "MedPill" => t.Medium.Pill,
        "LowText" => t.Low.Text,
        "LowFill" => t.Low.Fill,
        "LowPill" => t.Low.Pill,
        "OkText" => t.Ok.Text,
        "OkFill" => t.Ok.Fill,
        "OkPill" => t.Ok.Pill,
        _ => throw new ArgumentException("Unknown token " + name),
    };

    private static int Hex(string cell) => int.Parse(cell.Trim('`', '#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>WCAG 2.1 contrast ratio.</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(int value)
        {
            double s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
