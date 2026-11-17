using System.Drawing.Drawing2D;
using MediaVerifier.Core.Model;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// A card (subplan 08): Surface, 1 px Stroke, radius 8, 16 px padding; optional title in CaptionStrong Text3,
/// upper case, at +16, centred 22 px below the top. A card that acts as a button gets StrokeStrong on hover
/// (stat tiles also the Hover fill). Children are laid out by the page inside <see cref="ContentBounds"/>.
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

    public static void Draw(Graphics g, Rectangle bounds, string? title, VisualState state, bool tile, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = Dpi.ScaleF(8, dpi);
        bool hover = state.HasFlag(VisualState.Hover);
        KitPaint.FillRoundedRect(g, bounds, radius, hover && tile ? t.Hover : t.Surface);
        KitPaint.StrokeRoundedRect(g, bounds, radius, hover ? (t.IsHighContrast ? t.Accent : t.StrokeStrong) : t.Stroke, Dpi.ScaleF(hover && t.IsHighContrast ? 2 : 1, dpi));
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

    protected override bool IsInputKey(Keys keyData) => (_clickable && keyData is Keys.Enter or Keys.Space) || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_clickable && e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
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

    protected override bool IsInputKey(Keys keyData) => (keyData is Keys.Enter or Keys.Space && _action is not null) || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_action is not null && e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
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

    public EmptyState()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    public IconName Icon { get; set; } = IconName.Check;

    /// <summary>32 px icon instead of 48 (small empty states inside cards).</summary>
    public bool Small { get; set; }

    public string Headline { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string? Note { get; set; }

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
        return _button;
    }

    /// <summary>Paints the column centred in <paramref name="bounds"/>; returns the y where a button goes.</summary>
    public static int Draw(Graphics g, Rectangle bounds, IconName icon, bool small, string headline, string body, string? note, bool hasButton, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        // Icon, 6 px, headline (24 px line), body centred 24 px below the headline (docs/design components sheet).
        int iconSize = small ? 32 : 48;
        int height = iconSize + 6 + 24 + 4 + 20 + (hasButton ? 16 + 32 : 0) + (note is null ? 0 : 12 + 16);
        int y = bounds.Y + ((bounds.Height - Dpi.Scale(height, dpi)) / 2);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        int buttonY = Draw(e.Graphics, ClientRectangle, Icon, Small, Headline, Body, Note, _button is not null, DeviceDpi);
        if (_button is not null)
        {
            int width = S(_button.PreferredLogicalWidth);
            Rectangle target = new((Width - width) / 2, buttonY, width, S(32));
            if (_button.Bounds != target)
            {
                _button.Bounds = target;
            }
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);
}

/// <summary>
/// The floating selection bar (subplan 08): 560 × 48, radius 8, Raised, StrokeStrong border, shadow,
/// centred in the list 12 px above its bottom edge. Count in BodyStrong at +16, buttons from +104, 8 px
/// apart, a close icon button at the right.
/// </summary>
internal sealed class SelectionBar : KitControl
{
    public const int LogicalWidth = 560;
    public const int LogicalHeight = 48;
    private readonly IconButton _close = new() { Icon = IconName.Close, Compact = true };

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

    public string CountText { get; set; } = string.Empty;

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

        Buttons = [.. buttons];
        foreach (KitButton button in Buttons)
        {
            button.Compact = true;
            Controls.Add(button);
        }

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
        KitPaint.DrawText(g, count, TypeRamp.For(dpi).BodyStrong, t.Text, bounds.X + Dpi.Scale(16, dpi), bounds.Y + (bounds.Height / 2), Dpi.Scale(84, dpi));
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
        int x = S(104);
        int buttonHeight = S(28);
        int y = (Height - buttonHeight) / 2;
        foreach (KitButton button in Buttons)
        {
            int width = S(button.PreferredLogicalWidth);
            button.Bounds = new Rectangle(x, y, width, buttonHeight);
            x += width + S(8);
        }

        _close.Bounds = new Rectangle(Width - S(10) - buttonHeight, y, buttonHeight, buttonHeight);
    }

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, CountText, DeviceDpi);

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // The list paints the shadow under the bar; outside the rounded corners show the list.
        pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Surface);
    }

    protected override void OnThemeChanged()
    {
        BackColor = Tokens.Raised;
        base.OnThemeChanged();
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

    public void Close() => Animate(false);

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
