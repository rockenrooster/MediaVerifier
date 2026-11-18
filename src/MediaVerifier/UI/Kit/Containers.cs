using System.Drawing.Drawing2D;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// A card (subplan 08): Surface, 1 px Stroke, radius 8, 16 px padding; optional title in CaptionStrong Text3,
/// upper case, at +16, centred 22 px below the top. A card that acts as a button gets StrokeStrong on hover
/// (stat tiles also the Hover fill), the Pressed fill and an Accent border while pressed, and acts on Enter and
/// Space. Children are laid out by the page inside <see cref="ContentBounds"/>.
/// </summary>
internal class Card : KitControl
{
    private string? _title;
    private bool _clickable;
    private bool _tile;

    public Card()
    {
        TabStop = false;
        ApplySurface();
    }

    /// <summary>Upper-case title (the string table holds it in upper case already).</summary>
    public string? Title
    {
        get => _title;
        set
        {
            _title = value;
            Invalidate();
        }
    }

    /// <summary>The whole card acts as a button.</summary>
    public bool Clickable
    {
        get => _clickable;
        set
        {
            _clickable = value;
            TabStop = value;
            AccessibleRole = value ? AccessibleRole.PushButton : AccessibleRole.Grouping;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    /// <summary>Stat tiles also fill with Hover on hover.</summary>
    public bool Tile
    {
        get => _tile;
        set
        {
            _tile = value;
            Invalidate();
        }
    }

    /// <summary>The inside of the card after the 16 px padding (and below the title when there is one).</summary>
    public Rectangle ContentBounds
    {
        get
        {
            int pad = S(16);
            int top = _title is null ? pad : S(40);
            return Rectangle.FromLTRB(pad, top, Math.Max(pad, Width - pad), Math.Max(top, Height - pad));
        }
    }

    protected override bool Interactive => _clickable;

    protected override bool ActsOnEnter => _clickable;

    protected override bool ActsOnSpace => _clickable;

    protected internal override Rectangle FocusRingBounds => _clickable ? ClientRectangle : Rectangle.Empty;

    protected internal override float FocusRingRadius => SF(8);

    public static void Draw(Graphics g, Rectangle bounds, string? title, VisualState state, bool tile, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = Dpi.ScaleF(8, dpi);
        bool hover = state.HasFlag(VisualState.Hover);
        bool pressed = state.HasFlag(VisualState.Pressed) && !state.HasFlag(VisualState.Disabled);
        bool active = hover || pressed;
        Color border = pressed || (active && t.IsHighContrast) ? t.Accent : hover ? t.StrokeStrong : t.Stroke;
        KitPaint.FillRoundedRect(g, bounds, radius, pressed ? t.Pressed : hover && tile ? t.Hover : t.Surface);
        KitPaint.StrokeRoundedRect(g, bounds, radius, border, Dpi.ScaleF(active && t.IsHighContrast ? 2 : 1, dpi));
        if (title is not null)
        {
            KitPaint.DrawText(g, title, TypeRamp.For(dpi).CaptionStrong, t.Text3, bounds.X + Dpi.Scale(16, dpi), bounds.Y + Dpi.Scale(22, dpi), bounds.Width - Dpi.Scale(32, dpi));
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw(e.Graphics, ClientRectangle, _title, PaintState, _tile, DeviceDpi);
        PaintContent(e.Graphics);
    }

    /// <summary>Card contents drawn directly (text, graphics) rather than as child controls.</summary>
    protected virtual void PaintContent(Graphics g)
    {
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnThemeChanged()
    {
        ApplySurface();
        base.OnThemeChanged();
    }

    private void ApplySurface() => BackColor = Tokens.Surface;
}

/// <summary>
/// An info bar (subplan 08): 40 px (36 or 32 in tight places), radius 4, the tier's Pill fill, a 3 px tier
/// Fill bar on the left edge, a 16 px shape icon at +14, Body text at +40, and an optional action in
/// BodyStrong Accent with a 1 px underline, 16 px from the right.
/// </summary>
internal sealed class InfoBar : KitControl
{
    private Severity _tier = Severity.Low;
    private string? _action;
    private bool _actionHover;

    public InfoBar()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.Alert;
    }

    /// <summary>Raised when the action link is clicked or Enter is pressed on it.</summary>
    public event EventHandler? ActionClicked;

    /// <summary>The tier sets the colours and the icon; <see cref="Severity.None"/> is OK (success).</summary>
    public Severity Tier
    {
        get => _tier;
        set
        {
            _tier = value;
            Invalidate();
        }
    }

    public string? Action
    {
        get => _action;
        set
        {
            _action = value;
            TabStop = value is not null;
            Invalidate();
        }
    }

    protected override bool Interactive => _action is not null;

    protected override bool ActsOnEnter => _action is not null;

    protected override bool ActsOnSpace => _action is not null;

    /// <summary>The action is read out with the message ("Show"), since Enter runs it.</summary>
    protected override string? FallbackAccessibleDescription => _action;

    /// <summary>The ring goes around the action link: 20 px high, 2 px either side of it.</summary>
    protected internal override Rectangle FocusRingBounds
    {
        get
        {
            if (_action is null)
            {
                return Rectangle.Empty;
            }

            int width = KitPaint.MeasureWidth(_action, Type.BodyStrong);
            int x = Width - S(16) - width;
            return new Rectangle(x - S(2), (Height / 2) - S(10), width + S(4), S(20));
        }
    }

    protected internal override float FocusRingRadius => SF(2);

    public static void Draw(Graphics g, Rectangle bounds, Severity tier, string text, string? action, bool actionHover, VisualState state, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TierColors colors = t.ForSeverity(tier);
        TypeRamp type = TypeRamp.For(dpi);
        float radius = Dpi.ScaleF(4, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, colors.Pill);
        if (t.IsHighContrast)
        {
            KitPaint.StrokeRoundedRect(g, bounds, radius, t.Text, Dpi.ScaleF(1, dpi));
        }

        KitPaint.FillRoundedRect(g, new RectangleF(bounds.X, bounds.Y, Dpi.ScaleF(3, dpi), bounds.Height), Dpi.ScaleF(1.5f, dpi), colors.Fill);

        int centreY = bounds.Y + (bounds.Height / 2);
        IconRenderer.DrawSeverity(g, tier, bounds.X + Dpi.ScaleF(14, dpi), centreY - Dpi.ScaleF(8, dpi), 16, dpi, colors.Text, colors.Pill);
        int textX = bounds.X + Dpi.Scale(40, dpi);
        int actionWidth = action is null ? 0 : KitPaint.MeasureWidth(action, type.BodyStrong);
        int right = bounds.Right - Dpi.Scale(16, dpi);
        KitPaint.DrawText(g, text, type.Body, t.Text, textX, centreY, right - textX - (actionWidth > 0 ? actionWidth + Dpi.Scale(16, dpi) : 0));
        if (action is not null)
        {
            int ax = right - actionWidth;
            Color link = actionHover ? t.AccentHover : t.Accent;
            KitPaint.DrawText(g, action, type.BodyStrong, link, ax, centreY, actionWidth + 2);
            KitPaint.HorizontalLine(g, link, ax, ax + actionWidth, centreY + Dpi.ScaleF(8.5f, dpi), Dpi.ScaleF(1, dpi));
            if (state.HasFlag(VisualState.Focused))
            {
                KitPaint.DrawFocusRing(g, new Rectangle(ax - Dpi.Scale(2, dpi), centreY - Dpi.Scale(10, dpi), actionWidth + Dpi.Scale(4, dpi), Dpi.Scale(20, dpi)), Dpi.ScaleF(2, dpi), dpi);
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, _tier, Text, _action, _actionHover, PaintState, DeviceDpi);

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over = ActionBounds().Contains(e.Location);
        Cursor = over ? Cursors.Hand : Cursors.Default;
        if (over != _actionHover)
        {
            _actionHover = over;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _actionHover = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_action is not null && e.Button == MouseButtons.Left && ActionBounds().Contains(e.Location))
        {
            ActionClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnKeyboardAction()
    {
        if (_action is not null)
        {
            ActionClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private Rectangle ActionBounds()
    {
        if (_action is null)
        {
            return Rectangle.Empty;
        }

        int width = KitPaint.MeasureWidth(_action, Type.BodyStrong);
        int right = Width - S(16);
        return new Rectangle(right - width - S(4), 0, width + S(8), Height);
    }
}

/// <summary>
/// An empty state (subplan 08): a centred column with a 48 px (or 32 px) icon in Text3, a headline, one or
/// two sentences in Body Text2, an optional primary button and an optional Caption Text3 line.
/// </summary>
internal sealed class EmptyState : KitControl
{
    private KitButton? _button;
    private IconName _icon = IconName.Check;
    private bool _small;
    private string _headline = string.Empty;
    private string _body = string.Empty;
    private string? _note;

    public EmptyState()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
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

    /// <summary>32 px icon instead of 48 (small empty states inside cards).</summary>
    public bool Small
    {
        get => _small;
        set
        {
            _small = value;
            PerformLayout();
            Invalidate();
        }
    }

    public string Headline
    {
        get => _headline;
        set
        {
            _headline = value ?? string.Empty;
            Invalidate();
        }
    }

    public string Body
    {
        get => _body;
        set
        {
            _body = value ?? string.Empty;
            Invalidate();
        }
    }

    public string? Note
    {
        get => _note;
        set
        {
            _note = value;
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>Removes the button, and with it every Click handler a page attached.</summary>
    public void ClearButton()
    {
        if (_button is not null)
        {
            Controls.Remove(_button);
            _button.Dispose();
            _button = null;
            Invalidate();
        }
    }

    /// <summary>Adds the optional primary button; returns it so the page can handle Click.</summary>
    public KitButton SetButton(string text)
    {
        if (_button is null)
        {
            _button = new KitButton { Kind = ButtonKind.Primary };
            Controls.Add(_button);
        }

        _button.Text = text;
        PerformLayout();
        Invalidate();
        return _button;
    }

    /// <summary>Paints the column centred in <paramref name="bounds"/>; returns the y where a button goes.</summary>
    public static int Draw(Graphics g, Rectangle bounds, IconName icon, bool small, string headline, string body, string? note, bool hasButton, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        int iconSize = small ? 32 : 48;
        int y = ColumnTop(bounds, small, note, hasButton, dpi);
        int centreX = bounds.X + (bounds.Width / 2);
        IconRenderer.Draw(g, icon, centreX - Dpi.ScaleF(iconSize / 2f, dpi), y, iconSize, dpi, t.Text3);
        y += Dpi.Scale(iconSize + 6, dpi);
        KitPaint.DrawTextCentred(g, headline, type.Subtitle, t.Text, centreX, y + Dpi.Scale(12, dpi));
        y += Dpi.Scale(26, dpi);
        KitPaint.DrawTextCentred(g, body, type.Body, t.Text2, centreX, y + Dpi.Scale(10, dpi));
        y += Dpi.Scale(20, dpi);
        int buttonY = y + Dpi.Scale(16, dpi);
        if (hasButton)
        {
            y += Dpi.Scale(48, dpi);
        }

        if (note is not null)
        {
            KitPaint.DrawTextCentred(g, note, type.Caption, t.Text3, centreX, y + Dpi.Scale(12 + 8, dpi));
        }

        return buttonY;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_button is not null)
        {
            // The same arithmetic as Draw: icon, 6, headline 26, body 20, then the button 16 px below.
            int iconSize = _small ? 32 : 48;
            int buttonY = ColumnTop(ClientRectangle, _small, _note, hasButton: true, DeviceDpi) + S(iconSize + 6) + S(26) + S(20) + S(16);
            int width = S(_button.PreferredLogicalWidth);
            _button.Bounds = new Rectangle((Width - width) / 2, buttonY, width, S(32));
        }
    }

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, _icon, _small, _headline, _body, _note, _button is not null, DeviceDpi);

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    /// <summary>Top of the centred column: icon, 6 px, headline (24 px line), body centred 24 px below (docs/design components sheet).</summary>
    private static int ColumnTop(Rectangle bounds, bool small, string? note, bool hasButton, int dpi)
    {
        int iconSize = small ? 32 : 48;
        int height = iconSize + 6 + 24 + 4 + 20 + (hasButton ? 16 + 32 : 0) + (note is null ? 0 : 12 + 16);
        return bounds.Y + ((bounds.Height - Dpi.Scale(height, dpi)) / 2);
    }
}

/// <summary>
/// The floating selection bar (subplan 08): 560 × 48, radius 8, Raised, StrokeStrong border, shadow,
/// centred in the list 12 px above its bottom edge. Count in BodyStrong at +16, buttons from +104 (or 16 px after a
/// longer count), 8 px apart, a close icon button at the right. Buttons that do not fit before the close button show
/// only their icons, with the label as tooltip and name. Tab reaches the buttons first and the close button last.
/// </summary>
internal sealed class SelectionBar : KitControl
{
    public const int LogicalWidth = 560;
    public const int LogicalHeight = 48;
    private readonly IconButton _close = new() { Icon = IconName.Close, Compact = true };
    private readonly List<KitButton> _labelTips = [];
    private string _countText = string.Empty;

    public SelectionBar()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.ToolBar;
        BackColor = Tokens.Raised;
        _close.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        Controls.Add(_close);
    }

    /// <summary>The close button was used: the page clears the selection.</summary>
    public event EventHandler? Closed;

    /// <summary>"3 selected": a longer count moves the buttons right.</summary>
    public string CountText
    {
        get => _countText;
        set
        {
            value ??= string.Empty;
            if (!string.Equals(_countText, value, StringComparison.Ordinal))
            {
                _countText = value;
                PerformLayout();
                Invalidate();
            }
        }
    }

    public IReadOnlyList<KitButton> Buttons { get; private set; } = [];

    public string CloseToolTip
    {
        set => _close.ToolTipText = value;
    }

    public void SetButtons(IEnumerable<KitButton> buttons)
    {
        foreach (KitButton old in Buttons)
        {
            Controls.Remove(old);
        }

        _labelTips.Clear();
        Buttons = [.. buttons];
        for (int i = 0; i < Buttons.Count; i++)
        {
            KitButton button = Buttons[i];
            button.Compact = true;
            button.TabIndex = i;
            Controls.Add(button);
        }

        // The close button is drawn last (rightmost), so it comes last in the tab order.
        _close.TabIndex = Buttons.Count;
        PerformLayout();
        Invalidate();
    }

    /// <summary>Paints the bar surface and count; the shadow is drawn by the host beneath it.</summary>
    public static void Draw(Graphics g, Rectangle bounds, string count, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = Dpi.ScaleF(8, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, t.Raised);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.StrokeStrong, Dpi.ScaleF(1, dpi));
        Font font = TypeRamp.For(dpi).BodyStrong;
        int left = Dpi.Scale(16, dpi);
        KitPaint.DrawText(g, count, font, t.Text, bounds.X + left, bounds.Y + (bounds.Height / 2), Math.Min(KitPaint.MeasureWidth(count, font) + 2, bounds.Width - (2 * left)));
    }

    /// <summary>Where the bar goes inside a list of <paramref name="list"/> bounds (device pixels).</summary>
    public static Rectangle PlaceIn(Rectangle list, int dpi)
    {
        int width = Math.Min(list.Width - Dpi.Scale(16, dpi), Dpi.Scale(LogicalWidth, dpi));
        int height = Dpi.Scale(LogicalHeight, dpi);
        return new Rectangle(list.X + ((list.Width - width) / 2), list.Bottom - Dpi.Scale(12, dpi) - height, width, height);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int buttonHeight = S(28);
        int y = (Height - buttonHeight) / 2;
        _close.Bounds = new Rectangle(Width - S(10) - buttonHeight, y, buttonHeight, buttonHeight);

        int start = Math.Max(S(104), S(16) + KitPaint.MeasureWidth(_countText, Type.BodyStrong) + S(16));
        int limit = _close.Left - S(8);
        int full = 0;
        foreach (KitButton button in Buttons)
        {
            full += S(button.LabelledLogicalWidth) + S(8);
        }

        bool iconOnly = start + full - S(8) > limit;
        int x = start;
        foreach (KitButton button in Buttons)
        {
            SetIconOnly(button, iconOnly && button.Icon is not null);
            int width = S(button.PreferredLogicalWidth);
            button.Bounds = new Rectangle(x, y, width, buttonHeight);
            x += width + S(8);
        }
    }

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, _countText, DeviceDpi);

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // The bar floats over the list: outside its rounded corners the list's Surface shows.
        pevent.Graphics.Clear(Tokens.Surface);
    }

    protected override void OnThemeChanged()
    {
        BackColor = Tokens.Raised;
        base.OnThemeChanged();
    }

    /// <summary>An icon-only button shows its label as tooltip, unless the page gave it a tooltip of its own.</summary>
    private void SetIconOnly(KitButton button, bool iconOnly)
    {
        button.IconOnly = iconOnly;
        bool labelTip = _labelTips.Contains(button);
        if (iconOnly && !labelTip && KitToolTip.Get(button).Length == 0)
        {
            KitToolTip.Set(button, button.Text);
            _labelTips.Add(button);
        }
        else if (iconOnly && labelTip)
        {
            KitToolTip.Set(button, button.Text);
        }
        else if (!iconOnly && labelTip)
        {
            KitToolTip.Set(button, string.Empty);
            _labelTips.Remove(button);
        }
    }
}

/// <summary>
/// The detail drawer (subplan 08): 440 px wide (drag the left edge between 360 and 640; the width is
/// remembered), from below the header to above the status strip, Surface with a 1 px Stroke on its left
/// and top edges, 20 px padding, scrolling vertically. Slides in over 120 ms (ease-out cubic) and out over
/// 100 ms, or at once when Windows' animation effects are off.
/// </summary>
internal sealed class Drawer : KitControl
{
    public const int DefaultWidth = 440;
    public const int MinWidth = 360;
    public const int MaxWidth = 640;
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };
    private long _animationStart;
    private bool _opening;
    private bool _dragging;
    private int _dragStartX;
    private int _dragStartWidth;
    private int _logicalWidth = DefaultWidth;

    public Drawer()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.Pane;
        BackColor = Tokens.Surface;
        _animation.Tick += (_, _) => Step();
    }

    /// <summary>Raised when the open fraction changes (the host lays out the drawer at <see cref="Progress"/>).</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>Raised when the user drags the left edge; the host stores <see cref="LogicalWidth"/>.</summary>
    public event EventHandler? WidthChanged;

    public int LogicalWidth
    {
        get => _logicalWidth;
        set => _logicalWidth = Math.Clamp(value, MinWidth, MaxWidth);
    }

    /// <summary>0 = closed, 1 = fully open.</summary>
    public float Progress { get; private set; }

    public bool IsOpen => _opening;

    /// <summary>The 20 px-padded content area.</summary>
    public Rectangle ContentBounds => Rectangle.FromLTRB(S(21), S(21), Math.Max(S(21), Width - S(20)), Math.Max(S(21), Height - S(20)));

    public void Open() => Animate(true);

    /// <summary>Opens without the slide (the render harness).</summary>
    public void OpenInstantly()
    {
        _opening = true;
        _animation.Stop();
        Progress = 1;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Close() => Animate(false);

    /// <summary>Closes without the slide (the render harness).</summary>
    public void CloseInstantly()
    {
        _opening = false;
        _animation.Stop();
        Progress = 0;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemeTokens t = Tokens;
        e.Graphics.Clear(t.Surface);
        KitPaint.VerticalLine(e.Graphics, t.Stroke, 0, 0, Height, SF(1));
        KitPaint.HorizontalLine(e.Graphics, t.Stroke, 0, Width, 0, SF(1));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            int delta = _dragStartX - Cursor.Position.X;
            LogicalWidth = _dragStartWidth + (int)Math.Round(delta / (DeviceDpi / 96f));
            ProgressChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Cursor = e.X <= S(4) ? Cursors.SizeWE : Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.X <= S(4))
        {
            _dragging = true;
            _dragStartX = Cursor.Position.X;
            _dragStartWidth = _logicalWidth;
            Capture = true;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            WidthChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnThemeChanged()
    {
        BackColor = Tokens.Surface;
        base.OnThemeChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Animate(bool open)
    {
        if (open == _opening && Progress == (open ? 1f : 0f))
        {
            // Already there: a closed drawer asked to close again does not slide out once more.
            return;
        }

        _opening = open;
        if (!SystemInformation.UIEffectsEnabled)
        {
            Progress = open ? 1 : 0;
            ProgressChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _animationStart = Environment.TickCount64;
        _animation.Start();
        Step();
    }

    private void Step()
    {
        double duration = _opening ? 120 : 100;
        double elapsed = Math.Clamp((Environment.TickCount64 - _animationStart) / duration, 0, 1);
        double eased = 1 - Math.Pow(1 - elapsed, 3);
        Progress = (float)(_opening ? eased : 1 - eased);
        if (elapsed >= 1)
        {
            _animation.Stop();
            Progress = _opening ? 1 : 0;
        }

        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }
}
