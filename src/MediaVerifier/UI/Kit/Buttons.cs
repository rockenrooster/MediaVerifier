using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>The four button looks of subplan 08.</summary>
internal enum ButtonKind
{
    Primary,
    Secondary,
    Subtle,
    Danger,
}

/// <summary>
/// Paints buttons in every state (subplan 08, Buttons and Interaction states). Controls and the
/// render-screens harness share these painters.
/// </summary>
internal static class ButtonPainter
{
    public const int Height = 32;
    public const int CompactHeight = 28;
    public const int Radius = 4;
    private const int Padding = 12;
    private const int IconSize = 16;
    private const int IconGap = 8;
    private const int ChevronGap = 4;

    /// <summary>Logical width of a button: content plus 12 px each side, at least 64 with a label (32 without).</summary>
    public static int MeasureWidth(string? text, IconName? icon, bool chevron, ButtonKind kind, int dpi)
    {
        float content = ContentWidth(text, icon, chevron, kind, dpi) / (dpi / 96f);
        int width = (int)Math.Round(content + (2 * Padding), MidpointRounding.AwayFromZero);
        return Math.Max(string.IsNullOrEmpty(text) ? 32 : 64, width);
    }

    /// <summary>Paints a whole button into <paramref name="bounds"/> (device pixels).</summary>
    public static void Paint(Graphics g, Rectangle bounds, ButtonKind kind, VisualState state, string? text, IconName? icon, bool chevron, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        (Color fill, Color border, Color content, bool outline) = Colors(kind, state, t);
        float radius = Dpi.ScaleF(Radius, dpi);
        PaintSurface(g, bounds, kind, state, fill, border, outline, content, radius, dpi);

        bool pressed = state.HasFlag(VisualState.Pressed) && !state.HasFlag(VisualState.Disabled);
        int shift = pressed ? Dpi.Scale(1, dpi) : 0;
        float group = ContentWidth(text, icon, chevron, kind, dpi);
        float x = bounds.X + ((bounds.Width - group) / 2f);
        int centreY = bounds.Y + (bounds.Height / 2) + shift;
        DrawContent(g, x, centreY, text, icon, chevron, kind, content, dpi);

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    /// <summary>Fill, border and outline for a button state (used by the split button parts too).</summary>
    public static (Color Fill, Color Border, Color Content, bool InnerOutline) Colors(ButtonKind kind, VisualState state, ThemeTokens t)
    {
        bool disabled = state.HasFlag(VisualState.Disabled);
        bool pressed = state.HasFlag(VisualState.Pressed);
        bool hover = state.HasFlag(VisualState.Hover);
        switch (kind)
        {
            case ButtonKind.Primary:
            case ButtonKind.Danger:
                {
                    bool danger = kind == ButtonKind.Danger;
                    if (disabled)
                    {
                        return (t.Hover, Color.Empty, t.TextDisabled, false);
                    }

                    Color on = danger ? t.OnDanger : t.OnAccent;
                    Color fill = pressed ? (danger ? t.DangerPressed : t.AccentPressed)
                        : hover ? (danger ? t.DangerHover : t.AccentHover)
                        : (danger ? t.DangerFill : t.Accent);
                    return (fill, Color.Empty, on, pressed || hover);
                }

            case ButtonKind.Secondary:
                if (disabled)
                {
                    return (t.Bg, t.Stroke, t.TextDisabled, false);
                }

                return pressed ? (t.Pressed, t.Accent, t.Text, false)
                    : hover ? (t.Hover, t.StrokeStrong, t.Text, false)
                    : (t.Surface, t.Stroke, t.Text, false);
            default:
                if (disabled)
                {
                    return (Color.Empty, Color.Empty, t.TextDisabled, false);
                }

                return pressed ? (t.Pressed, t.Accent, t.Text, false)
                    : hover ? (t.Hover, t.StrokeStrong, t.Text, false)
                    : (Color.Empty, Color.Empty, t.Text, false);
        }
    }

    /// <summary>Background, border and the filled buttons' inner outline; contrast themes show hover as a 2 px Highlight border.</summary>
    public static void PaintSurface(Graphics g, RectangleF bounds, ButtonKind kind, VisualState state, Color fill, Color border, bool outline, Color content, float radius, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        if (!fill.IsEmpty)
        {
            KitPaint.FillRoundedRect(g, bounds, radius, fill);
        }

        if (t.IsHighContrast)
        {
            bool active = state.HasFlag(VisualState.Hover) || state.HasFlag(VisualState.Pressed);
            KitPaint.StrokeRoundedRect(g, bounds, radius, active ? t.Accent : t.Stroke, active ? Dpi.ScaleF(2, dpi) : Dpi.ScaleF(1, dpi));
            return;
        }

        if (!border.IsEmpty)
        {
            KitPaint.StrokeRoundedRect(g, bounds, radius, border, Dpi.ScaleF(1, dpi));
        }

        if (outline && kind is ButtonKind.Primary or ButtonKind.Danger)
        {
            KitPaint.StrokeRoundedRect(g, bounds, radius, content, Dpi.ScaleF(1, dpi));
        }
    }

    /// <summary>Width of icon, gap, label, gap and chevron in device pixels.</summary>
    public static float ContentWidth(string? text, IconName? icon, bool chevron, ButtonKind kind, int dpi)
    {
        float width = 0;
        if (icon is not null)
        {
            width += Dpi.ScaleF(IconSize, dpi);
        }

        if (!string.IsNullOrEmpty(text))
        {
            if (icon is not null)
            {
                width += Dpi.ScaleF(IconGap, dpi);
            }

            width += KitPaint.MeasureWidth(text, LabelFont(kind, dpi));
        }

        if (chevron)
        {
            width += Dpi.ScaleF(ChevronGap + IconSize, dpi);
        }

        return width;
    }

    public static Font LabelFont(ButtonKind kind, int dpi) =>
        kind is ButtonKind.Primary or ButtonKind.Danger ? TypeRamp.For(dpi).BodyStrong : TypeRamp.For(dpi).Body;

    /// <summary>Icon, label and chevron as one group starting at <paramref name="x"/>.</summary>
    public static void DrawContent(Graphics g, float x, int centreY, string? text, IconName? icon, bool chevron, ButtonKind kind, Color color, int dpi)
    {
        float iconSize = Dpi.ScaleF(IconSize, dpi);
        if (icon is IconName name)
        {
            IconRenderer.Draw(g, name, x, centreY - (iconSize / 2f), IconSize, dpi, color);
            x += iconSize;
            if (!string.IsNullOrEmpty(text))
            {
                x += Dpi.ScaleF(IconGap, dpi);
            }
        }

        if (!string.IsNullOrEmpty(text))
        {
            Font font = LabelFont(kind, dpi);
            int width = KitPaint.MeasureWidth(text, font);
            KitPaint.DrawText(g, text, font, color, (int)Math.Round(x), centreY, width + 2);
            x += width;
        }

        if (chevron)
        {
            x += Dpi.ScaleF(ChevronGap, dpi);
            IconRenderer.Draw(g, IconName.ChevronDown, x, centreY - (iconSize / 2f), IconSize, dpi, color);
        }
    }
}

/// <summary>A push button (subplan 08, Buttons). Width follows the content unless docked or anchored.</summary>
internal class KitButton : KitControl
{
    private ButtonKind _kind = ButtonKind.Secondary;
    private IconName? _icon;
    private bool _chevron;
    private bool _compact;

    public KitButton()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        Cursor = Cursors.Default;
    }

    public ButtonKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Relayout();
        }
    }

    public IconName? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Relayout();
        }
    }

    /// <summary>Shows a chevron-down after the label (buttons that open a menu).</summary>
    public bool ShowChevron
    {
        get => _chevron;
        set
        {
            _chevron = value;
            Relayout();
        }
    }

    /// <summary>28 px instead of 32 (inside rows, cards and bars).</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            Relayout();
        }
    }

    /// <summary>Fixed logical width instead of the content width (0 = content width).</summary>
    public int FixedWidth { get; set; }

    /// <summary>Logical width this button wants.</summary>
    public int PreferredLogicalWidth => FixedWidth > 0 ? FixedWidth : ButtonPainter.MeasureWidth(Text, _icon, _chevron, _kind, DeviceDpi);

    public int LogicalHeight => _compact ? ButtonPainter.CompactHeight : ButtonPainter.Height;

    protected override bool Interactive => true;

    public void PerformClick()
    {
        if (Enabled && Visible)
        {
            OnClick(EventArgs.Empty);
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(S(PreferredLogicalWidth), S(LogicalHeight));

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Relayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ButtonPainter.Paint(e.Graphics, ClientRectangle, _kind, PaintState, Text, _icon, _chevron, DeviceDpi);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            PerformClick();
        }
    }

    protected virtual void Relayout()
    {
        if (AutoSize)
        {
            Size = GetPreferredSize(Size.Empty);
        }

        Invalidate();
    }
}

/// <summary>
/// The Scan split button (subplan 08): primary colours, a label part and a 29 px chevron part, each
/// with its own hover and press, divided by a 35 % OnAccent line.
/// </summary>
internal sealed class SplitButton : KitControl
{
    private const int ChevronPart = 29;
    private int _hoverPart = -1;
    private int _pressedPart = -1;
    private IconName? _icon = IconName.Play;

    public SplitButton()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.SplitButton;
    }

    /// <summary>Raised when the menu part is clicked or Alt+Down is pressed.</summary>
    public event EventHandler? DropDownClick;

    public IconName? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Invalidate();
        }
    }

    public int PreferredLogicalWidth => LabelPartWidth(DeviceDpi) + ChevronPart;

    protected override bool Interactive => true;

    public override Size GetPreferredSize(Size proposedSize) => new(S(PreferredLogicalWidth), S(ButtonPainter.Height));

    /// <summary>Paints the split button with separate states for its two parts.</summary>
    public static void Draw(Graphics g, Rectangle bounds, VisualState left, VisualState right, string text, IconName? icon, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = Dpi.ScaleF(ButtonPainter.Radius, dpi);
        bool disabled = left.HasFlag(VisualState.Disabled);
        int split = bounds.Right - Dpi.Scale(ChevronPart, dpi);
        (Color baseFill, _, Color content, _) = ButtonPainter.Colors(ButtonKind.Primary, disabled ? VisualState.Disabled : VisualState.Rest, t);
        KitPaint.FillRoundedRect(g, bounds, radius, baseFill);
        if (!disabled)
        {
            PaintPart(g, bounds, Rectangle.FromLTRB(bounds.Left, bounds.Top, split, bounds.Bottom), left, t, radius);
            PaintPart(g, bounds, Rectangle.FromLTRB(split, bounds.Top, bounds.Right, bounds.Bottom), right, t, radius);
        }

        int inset = Dpi.Scale(6, dpi);
        g.FillRectangle(GdiCache.Brush(Color.FromArgb(89, content)), split, bounds.Top + inset, Dpi.ScaleF(1, dpi), bounds.Height - (2 * inset));

        int leftShift = left.HasFlag(VisualState.Pressed) && !disabled ? Dpi.Scale(1, dpi) : 0;
        int rightShift = right.HasFlag(VisualState.Pressed) && !disabled ? Dpi.Scale(1, dpi) : 0;
        int centreY = bounds.Y + (bounds.Height / 2);
        ButtonPainter.DrawContent(g, bounds.X + Dpi.ScaleF(12, dpi), centreY + leftShift, text, icon, false, ButtonKind.Primary, content, dpi);
        float chevronX = split + ((Dpi.ScaleF(ChevronPart, dpi) - Dpi.ScaleF(16, dpi)) / 2f);
        IconRenderer.Draw(g, IconName.ChevronDown, (float)Math.Floor(chevronX), centreY + rightShift - Dpi.ScaleF(8, dpi), 16, dpi, content);
        if (left.HasFlag(VisualState.Focused) || right.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        VisualState state = PaintState;
        VisualState focus = state & VisualState.Focused;
        VisualState disabled = state & VisualState.Disabled;
        VisualState left = disabled | focus | (_hoverPart == 0 ? VisualState.Hover : 0) | (_pressedPart == 0 ? VisualState.Pressed : 0);
        VisualState right = disabled | (_hoverPart == 1 ? VisualState.Hover : 0) | (_pressedPart == 1 ? VisualState.Pressed : 0);
        Draw(e.Graphics, ClientRectangle, left, right, Text, _icon, DeviceDpi);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Tokens.Bg);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int part = PartAt(e.X);
        if (part != _hoverPart)
        {
            _hoverPart = part;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPart = -1;
        _pressedPart = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && Enabled)
        {
            _pressedPart = PartAt(e.X);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int pressed = _pressedPart;
        _pressedPart = -1;
        Invalidate();
        if (e.Button == MouseButtons.Left && Enabled && pressed >= 0 && pressed == PartAt(e.X) && ClientRectangle.Contains(e.Location))
        {
            if (pressed == 0)
            {
                OnClick(EventArgs.Empty);
            }
            else
            {
                DropDownClick?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    protected override void OnClick(EventArgs e)
    {
        // Clicks are raised from OnMouseUp per part; ignore the framework's whole-control click.
        if (e is not MouseEventArgs)
        {
            base.OnClick(e);
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space or (Keys.Alt | Keys.Down) || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled)
        {
            return;
        }

        if (e.KeyCode == Keys.Down && e.Alt)
        {
            e.Handled = true;
            DropDownClick?.Invoke(this, EventArgs.Empty);
        }
        else if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
    }

    private static void PaintPart(Graphics g, Rectangle whole, Rectangle part, VisualState state, ThemeTokens t, float radius)
    {
        Color? fill = state.HasFlag(VisualState.Pressed) ? t.AccentPressed : state.HasFlag(VisualState.Hover) ? t.AccentHover : null;
        if (fill is not Color color)
        {
            return;
        }

        System.Drawing.Drawing2D.GraphicsState saved = g.Save();
        g.SetClip(part, System.Drawing.Drawing2D.CombineMode.Intersect);
        KitPaint.FillRoundedRect(g, whole, radius, color);
        g.Restore(saved);
    }

    private int LabelPartWidth(int dpi)
    {
        float label = ButtonPainter.ContentWidth(Text, _icon, false, ButtonKind.Primary, dpi) / (dpi / 96f);
        return (int)Math.Round(label + 24, MidpointRounding.AwayFromZero);
    }

    private int PartAt(int x) => x >= Width - S(ChevronPart) ? 1 : 0;
}

/// <summary>A 32 px (compact 28) icon-only button with a mandatory tooltip and an optional count badge.</summary>
internal sealed class IconButton : KitControl
{
    private IconName _icon = IconName.More;
    private int _badge;
    private bool _compact;

    public IconButton()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    public IconName Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Invalidate();
        }
    }

    /// <summary>The tooltip (and accessible name). Required by subplan 08.</summary>
    public string ToolTipText
    {
        get => AccessibleName ?? string.Empty;
        set
        {
            AccessibleName = value;
            KitToolTip.Set(this, value);
        }
    }

    /// <summary>Count shown in the badge; 0 hides it.</summary>
    public int Badge
    {
        get => _badge;
        set
        {
            _badge = Math.Max(0, value);
            Invalidate();
        }
    }

    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            Invalidate();
        }
    }

    public int LogicalSize => _compact ? 28 : 32;

    protected override bool Interactive => true;

    public override Size GetPreferredSize(Size proposedSize) => new(S(LogicalSize), S(LogicalSize));

    public static void Draw(Graphics g, Rectangle bounds, IconName icon, VisualState state, int badge, int dpi, Color? iconColor = null)
    {
        ThemeTokens t = ThemeService.Tokens;
        (Color fill, Color border, Color content, _) = ButtonPainter.Colors(ButtonKind.Subtle, state, t);
        float radius = Dpi.ScaleF(ButtonPainter.Radius, dpi);
        ButtonPainter.PaintSurface(g, bounds, ButtonKind.Subtle, state, fill, border, false, content, radius, dpi);
        int shift = state.HasFlag(VisualState.Pressed) && !state.HasFlag(VisualState.Disabled) ? Dpi.Scale(1, dpi) : 0;
        float size = Dpi.ScaleF(16, dpi);
        Color color = state.HasFlag(VisualState.Disabled) ? t.TextDisabled : iconColor ?? content;
        IconRenderer.Draw(g, icon, bounds.X + ((bounds.Width - size) / 2f), bounds.Y + ((bounds.Height - size) / 2f) + shift, 16, dpi, color);
        if (badge > 0)
        {
            PaintBadge(g, bounds, badge, dpi);
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    /// <summary>Circle r 7 centred 8 px in from the right and 9 px down, Accent with a 2 px Bg outline.</summary>
    public static void PaintBadge(Graphics g, Rectangle bounds, int count, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float cx = bounds.Right - Dpi.ScaleF(8, dpi);
        float cy = bounds.Top + Dpi.ScaleF(9, dpi);
        float r = Dpi.ScaleF(7, dpi);
        float outline = Dpi.ScaleF(2, dpi);
        System.Drawing.Drawing2D.SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillEllipse(GdiCache.Brush(t.Bg), cx - r - outline, cy - r - outline, 2 * (r + outline), 2 * (r + outline));
        g.FillEllipse(GdiCache.Brush(t.Accent), cx - r, cy - r, 2 * r, 2 * r);
        g.SmoothingMode = previous;
        string text = count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        KitPaint.DrawTextCentred(g, text, TypeRamp.For(dpi).CaptionStrong, t.OnAccent, (int)Math.Round(cx), (int)Math.Round(cy));
    }

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, _icon, PaintState, _badge, DeviceDpi);

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
    }
}

/// <summary>
/// An inline link (subplan 08: links use Accent; the hand cursor is for inline links): BodyStrong in Accent with a
/// 1 px underline, AccentHover on hover, the focus ring around the text. Enter or Space activates it.
/// </summary>
internal sealed class KitLink : KitControl
{
    public KitLink()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.Link;
        Cursor = Cursors.Hand;
    }

    /// <summary>Logical width of the text plus 2 px for the focus ring on each side.</summary>
    public int PreferredLogicalWidth => (int)Math.Ceiling(KitPaint.MeasureWidth(Text, Type.BodyStrong) * 96f / DeviceDpi) + 4;

    protected override bool Interactive => true;

    public override Size GetPreferredSize(Size proposedSize) => new(S(PreferredLogicalWidth), S(32));

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemeTokens t = Tokens;
        Graphics g = e.Graphics;
        VisualState state = PaintState;
        Color colour = !Enabled ? t.TextDisabled : state.HasFlag(VisualState.Hover) || state.HasFlag(VisualState.Pressed) ? t.AccentHover : t.Accent;
        int width = KitPaint.MeasureWidth(Text, Type.BodyStrong);
        int x = S(2);
        int centreY = Height / 2;
        KitPaint.DrawText(g, Text, Type.BodyStrong, colour, x, centreY, width + 2);
        KitPaint.HorizontalLine(g, colour, x, x + width, centreY + SF(8.5f), SF(1));
        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, new Rectangle(0, centreY - S(11), width + S(4), S(22)), SF(2), DeviceDpi);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space && Enabled)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
    }
}
