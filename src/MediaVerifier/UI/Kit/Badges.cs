using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>Severity badges and pills (subplan 08, Badges, pills, chips).</summary>
internal static class BadgePainter
{
    public const int Height = 20;

    /// <summary>The tier word shown in badges, chips and read by screen readers.</summary>
    public static string TierWord(Severity severity) => severity switch
    {
        Severity.Critical => Strings.TierCritical,
        Severity.High => Strings.TierHigh,
        Severity.Medium => Strings.TierMedium,
        Severity.Low => Strings.TierLow,
        _ => Strings.TierOk,
    };

    /// <summary>Logical width of a severity badge: 6, 12 px shape, 4, word, 8.</summary>
    public static int SeverityBadgeWidth(Severity severity, int dpi) =>
        (int)Math.Round(18 + 4 + 8 + (KitPaint.MeasureWidth(TierWord(severity), TypeRamp.For(dpi).CaptionStrong) / (dpi / 96f)), MidpointRounding.AwayFromZero);

    /// <summary>Paints a severity badge with its top-left corner at (x, y), device pixels. Returns its width.</summary>
    public static int PaintSeverityBadge(Graphics g, int x, int y, Severity severity, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TierColors tier = t.ForSeverity(severity);
        int width = Dpi.Scale(SeverityBadgeWidth(severity, dpi), dpi);
        int height = Dpi.Scale(Height, dpi);
        RectangleF rect = new(x, y, width, height);
        KitPaint.FillRoundedRect(g, rect, height / 2f, tier.Pill);
        if (t.IsHighContrast)
        {
            KitPaint.StrokeRoundedRect(g, rect, height / 2f, t.Text, Dpi.ScaleF(1, dpi));
        }

        IconRenderer.DrawSeverity(g, severity, x + Dpi.ScaleF(6, dpi), y + Dpi.ScaleF(4, dpi), 12, dpi, tier.Text, tier.Pill);
        Font font = TypeRamp.For(dpi).CaptionStrong;
        KitPaint.DrawText(g, TierWord(severity), font, tier.Text, x + Dpi.Scale(22, dpi), y + (height / 2), width - Dpi.Scale(22, dpi));
        return width;
    }

    /// <summary>Logical width of a pill: 8 px padding each side of CaptionStrong text.</summary>
    public static int PillWidth(string text, int dpi) =>
        (int)Math.Round(16 + (KitPaint.MeasureWidth(text, TypeRamp.For(dpi).CaptionStrong) / (dpi / 96f)), MidpointRounding.AwayFromZero);

    /// <summary>A neutral pill (Text2 on NeutralPill). Returns its width in device pixels.</summary>
    public static int PaintPill(Graphics g, int x, int y, string text, int dpi) =>
        PaintPill(g, x, y, text, ThemeService.Tokens.Text2, ThemeService.Tokens.NeutralPill, dpi);

    /// <summary>A status pill in a tier's Text on its Pill colour.</summary>
    public static int PaintStatusPill(Graphics g, int x, int y, string text, Severity tier, int dpi)
    {
        TierColors colors = ThemeService.Tokens.ForSeverity(tier);
        return PaintPill(g, x, y, text, colors.Text, colors.Pill, dpi);
    }

    public static int PaintPill(Graphics g, int x, int y, string text, Color foreground, Color background, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        int width = Dpi.Scale(PillWidth(text, dpi), dpi);
        int height = Dpi.Scale(Height, dpi);
        RectangleF rect = new(x, y, width, height);
        KitPaint.FillRoundedRect(g, rect, height / 2f, background);
        if (t.IsHighContrast)
        {
            KitPaint.StrokeRoundedRect(g, rect, height / 2f, t.Text, Dpi.ScaleF(1, dpi));
        }

        KitPaint.DrawText(g, text, TypeRamp.For(dpi).CaptionStrong, foreground, x + Dpi.Scale(8, dpi), y + (height / 2), width - Dpi.Scale(8, dpi));
        return width;
    }
}

/// <summary>
/// A filter chip (subplan 08): 28 px, radius 14, optional severity shape or icon, label, optional count.
/// Toggles <see cref="On"/> when clicked or with Space.
/// </summary>
internal sealed class Chip : KitControl
{
    public const int LogicalHeight = 28;
    private bool _on;
    private int? _count;
    private Severity? _severity;
    private IconName? _icon;

    public Chip()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    /// <summary>Raised after <see cref="On"/> changes from a click or key.</summary>
    public event EventHandler? Toggled;

    public bool On
    {
        get => _on;
        set
        {
            if (_on != value)
            {
                _on = value;
                Invalidate();
            }
        }
    }

    public int? Count
    {
        get => _count;
        set
        {
            _count = value;
            Invalidate();
        }
    }

    public Severity? Severity
    {
        get => _severity;
        set
        {
            _severity = value;
            Invalidate();
        }
    }

    public IconName? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Invalidate();
        }
    }

    public int PreferredLogicalWidth => MeasureWidth(Text, _severity, _icon, _count, _on, DeviceDpi);

    protected override bool Interactive => true;

    protected override AccessibleStates ExtraAccessibleStates => _on ? AccessibleStates.Checked : AccessibleStates.None;

    public static int MeasureWidth(string text, Severity? severity, IconName? icon, int? count, bool on, int dpi)
    {
        TypeRamp type = TypeRamp.For(dpi);
        float scale = dpi / 96f;
        float width = 12 + 12;
        if (severity is not null)
        {
            width += 14 + 6;
        }
        else if (icon is not null)
        {
            width += 16 + 5;
        }

        width += KitPaint.MeasureWidth(text, on ? type.BodyStrong : type.Body) / scale;
        if (count is int n)
        {
            width += 6 + (KitPaint.MeasureWidth(n.ToString("N0", CultureInfo.CurrentCulture), type.Body) / scale);
        }

        return (int)Math.Round(width, MidpointRounding.AwayFromZero);
    }

    public static void Draw(Graphics g, Rectangle bounds, string text, Severity? severity, IconName? icon, int? count, bool on, VisualState state, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float radius = bounds.Height / 2f;
        bool hover = state.HasFlag(VisualState.Hover);
        Color fill = on ? t.Selected : hover ? t.Hover : t.Surface;
        Color border = on ? t.Accent : hover ? t.StrokeStrong : t.Stroke;
        if (state.HasFlag(VisualState.Disabled))
        {
            fill = t.Bg;
            border = t.Stroke;
        }

        KitPaint.FillRoundedRect(g, bounds, radius, fill);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.IsHighContrast && hover ? t.Accent : border, Dpi.ScaleF(t.IsHighContrast && hover ? 2 : 1, dpi));
        int centreY = bounds.Y + (bounds.Height / 2);
        float x = bounds.X + Dpi.ScaleF(12, dpi);
        if (severity is Severity tier)
        {
            float size = Dpi.ScaleF(14, dpi);
            IconRenderer.DrawSeverity(g, tier, x, centreY - (size / 2f), 14, dpi, t.ForSeverity(tier).Text, fill);
            x += size + Dpi.ScaleF(6, dpi);
        }
        else if (icon is IconName name)
        {
            float size = Dpi.ScaleF(16, dpi);
            IconRenderer.Draw(g, name, x - Dpi.ScaleF(1, dpi), centreY - (size / 2f), 16, dpi, state.HasFlag(VisualState.Disabled) ? t.TextDisabled : t.Text2);
            x += size + Dpi.ScaleF(5, dpi);
        }

        Font font = on ? type.BodyStrong : type.Body;
        Color color = state.HasFlag(VisualState.Disabled) ? t.TextDisabled : t.Text;
        int labelWidth = KitPaint.MeasureWidth(text, font);
        KitPaint.DrawText(g, text, font, color, (int)Math.Round(x), centreY, labelWidth + 2);
        x += labelWidth;
        if (count is int n)
        {
            x += Dpi.ScaleF(6, dpi);
            string digits = n.ToString("N0", CultureInfo.CurrentCulture);
            KitPaint.DrawText(g, digits, type.Body, state.HasFlag(VisualState.Disabled) ? t.TextDisabled : t.Text3, (int)Math.Round(x), centreY, KitPaint.MeasureWidth(digits, type.Body) + 2);
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(S(PreferredLogicalWidth), S(LogicalHeight));

    protected override void OnPaint(PaintEventArgs e) =>
        Draw(e.Graphics, ClientRectangle, Text, _severity, _icon, _count, _on, PaintState, DeviceDpi);

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Toggle();
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            Toggle();
        }
    }

    private void Toggle()
    {
        On = !_on;
        Toggled?.Invoke(this, EventArgs.Empty);
    }
}
