using MediaVerifier.Core.Model;

namespace MediaVerifier.UI.Theme;

/// <summary>The three colours of one severity tier (subplan 08): label, fill and pill background.</summary>
internal readonly record struct TierColors(Color Text, Color Fill, Color Pill);

/// <summary>
/// Every colour the UI paints with (subplan 08, docs/design/tokens.md). Nothing else in the app holds
/// a literal colour.
/// </summary>
internal sealed class ThemeTokens
{
    public required string Name { get; init; }
    public required bool IsDark { get; init; }
    public bool IsHighContrast { get; init; }

    public required Color Bg { get; init; }
    public required Color Surface { get; init; }
    public required Color Raised { get; init; }
    public required Color Hover { get; init; }
    public required Color Pressed { get; init; }
    public required Color Selected { get; init; }
    public required Color Stroke { get; init; }
    public required Color StrokeStrong { get; init; }
    public required Color Text { get; init; }
    public required Color Text2 { get; init; }
    public required Color Text3 { get; init; }
    public required Color TextDisabled { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentPressed { get; init; }
    public required Color OnAccent { get; init; }
    public required Color DangerFill { get; init; }
    public required Color DangerHover { get; init; }
    public required Color DangerPressed { get; init; }
    public required Color OnDanger { get; init; }
    public required Color NeutralPill { get; init; }
    public required Color Scrim { get; init; }
    public required Color Shadow { get; init; }

    public required TierColors Critical { get; init; }
    public required TierColors High { get; init; }
    public required TierColors Medium { get; init; }
    public required TierColors Low { get; init; }
    public required TierColors Ok { get; init; }

    /// <summary>Colours for a tier; <see cref="Severity.None"/> maps to OK.</summary>
    public TierColors ForSeverity(Severity severity) => severity switch
    {
        Severity.Critical => Critical,
        Severity.High => High,
        Severity.Medium => Medium,
        Severity.Low => Low,
        _ => Ok,
    };

    public static ThemeTokens Dark { get; } = new()
    {
        Name = "Dark",
        IsDark = true,
        Bg = Hex(0x1F1F1F),
        Surface = Hex(0x2A2A2A),
        Raised = Hex(0x333333),
        Hover = Hex(0x3A3A3A),
        Pressed = Hex(0x474747),
        Selected = Hex(0x243C4D),
        Stroke = Hex(0x454545),
        StrokeStrong = Hex(0x8F8F8F),
        Text = Hex(0xF5F5F5),
        Text2 = Hex(0xCECECE),
        Text3 = Hex(0xB0B0B0),
        TextDisabled = Hex(0x7A7A7A),
        Accent = Hex(0x60CDFF),
        AccentHover = Hex(0x8AD9FF),
        AccentPressed = Hex(0x3FB4E8),
        OnAccent = Hex(0x00131F),
        DangerFill = Hex(0xC93237),
        DangerHover = Hex(0xD13C41),
        DangerPressed = Hex(0xA8262B),
        OnDanger = Hex(0xFFFFFF),
        NeutralPill = Hex(0x3A3A3A),
        Scrim = Color.FromArgb(128, 0, 0, 0),
        Shadow = Color.FromArgb(128, 0, 0, 0),
        Critical = new(Hex(0xFF8585), Hex(0xE5484D), Hex(0x4C2F30)),
        High = new(Hex(0xFFA657), Hex(0xF76B15), Hex(0x4F3626)),
        Medium = new(Hex(0xFFD23F), Hex(0xFFB224), Hex(0x504229)),
        Low = new(Hex(0x85BDFF), Hex(0x3E8BFF), Hex(0x2E3B50)),
        Ok = new(Hex(0x5AD694), Hex(0x30A46C), Hex(0x2B4036)),
    };

    public static ThemeTokens Light { get; } = new()
    {
        Name = "Light",
        IsDark = false,
        Bg = Hex(0xF6F6F6),
        Surface = Hex(0xFFFFFF),
        Raised = Hex(0xFFFFFF),
        Hover = Hex(0xEBEBEB),
        Pressed = Hex(0xDCDCDC),
        Selected = Hex(0xD6E8F8),
        Stroke = Hex(0xD0D0D0),
        StrokeStrong = Hex(0x6E6E6E),
        Text = Hex(0x1A1A1A),
        Text2 = Hex(0x404040),
        Text3 = Hex(0x595959),
        TextDisabled = Hex(0x9A9A9A),
        Accent = Hex(0x005FB8),
        AccentHover = Hex(0x0A70CF),
        AccentPressed = Hex(0x004A91),
        OnAccent = Hex(0xFFFFFF),
        DangerFill = Hex(0xC4252B),
        DangerHover = Hex(0xD13438),
        DangerPressed = Hex(0xA61D22),
        OnDanger = Hex(0xFFFFFF),
        NeutralPill = Hex(0xEBEBEB),
        Scrim = Color.FromArgb(77, 0, 0, 0),
        Shadow = Color.FromArgb(56, 0, 0, 0),
        Critical = new(Hex(0xB91C22), Hex(0xD13438), Hex(0xF9E7E7)),
        High = new(Hex(0xA54300), Hex(0xD45A00), Hex(0xFAEBE0)),
        Medium = new(Hex(0x735400), Hex(0xA37A00), Hex(0xF6F1E0)),
        Low = new(Hex(0x1A56B0), Hex(0x2B6FD6), Hex(0xE6EEFA)),
        Ok = new(Hex(0x146C3E), Hex(0x1F8A52), Hex(0xE4F1EA)),
    };

    /// <summary>
    /// The contrast-theme set (subplan 08, task UK-14), built from the current system colours: surfaces
    /// are Window, every text and line WindowText, selection and accent Highlight, severity WindowText,
    /// pills Window (controls add a 1 px WindowText border and show hover as a 2 px Highlight border).
    /// </summary>
    public static ThemeTokens HighContrast()
    {
        Color window = SystemColors.Window;
        Color text = SystemColors.WindowText;
        Color highlight = SystemColors.Highlight;
        Color onHighlight = SystemColors.HighlightText;
        TierColors tier = new(text, text, window);
        return new ThemeTokens
        {
            Name = "HighContrast",
            IsDark = window.GetBrightness() < 0.5f,
            IsHighContrast = true,
            Bg = window,
            Surface = window,
            Raised = window,
            Hover = window,
            Pressed = window,
            Selected = highlight,
            Stroke = text,
            StrokeStrong = text,
            Text = text,
            Text2 = text,
            Text3 = text,
            TextDisabled = SystemColors.GrayText,
            Accent = highlight,
            AccentHover = highlight,
            AccentPressed = highlight,
            OnAccent = onHighlight,
            DangerFill = highlight,
            DangerHover = highlight,
            DangerPressed = highlight,
            OnDanger = onHighlight,
            NeutralPill = window,
            Scrim = Color.FromArgb(128, 0, 0, 0),
            Shadow = Color.FromArgb(0, 0, 0, 0),
            Critical = tier,
            High = tier,
            Medium = tier,
            Low = tier,
            Ok = tier,
        };
    }

    /// <summary>Solid colour from 0xRRGGBB.</summary>
    public static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
