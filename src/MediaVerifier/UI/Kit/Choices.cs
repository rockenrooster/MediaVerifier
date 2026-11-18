using System.Drawing.Drawing2D;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// A drop-down (subplan 08): coloured as a secondary button, label at +12, chevron-down with its left edge
/// 26 px from the right. Opens a drop-down list 4 px below; Alt+Down, F4, Enter and Space open it; Up and
/// Down change the value while it is closed.
/// </summary>
internal sealed class DropDown : KitControl
{
    private readonly List<string> _items = [];
    private int _selected = -1;
    private bool _compact;
    private PopupList? _open;
    private bool _closedByPress;
    private bool _ignoreRelease;
    private string? _shownFor;
    private string? _shownFormat;
    private string _shown = string.Empty;

    public DropDown()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
    }

    public event EventHandler? SelectedIndexChanged;

    public IList<string> Items => _items;

    /// <summary>Text shown instead of the selected item (the narrow Issues layout shows "Group: Severity").</summary>
    public string? DisplayFormat { get; set; }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            // -1 shows nothing chosen (a schedule whose folder is no longer watched must be given one).
            int clamped = _items.Count == 0 || value < 0 ? -1 : Math.Min(value, _items.Count - 1);
            if (clamped != _selected)
            {
                _selected = clamped;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    /// <summary>Unnamed drop-downs (the Issues filters) are read as what they show, for example "Status: Open".</summary>
    protected override string? FallbackAccessibleName => ShownText.Length > 0 ? ShownText : null;

    protected override string? AccessibleValue => SelectedItem;

    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            Invalidate();
        }
    }

    public bool IsOpen => _open is { IsDisposed: false, Visible: true };

    /// <summary>A choice other than the default is set: an Accent border (subplan 09, Issues drop-downs).</summary>
    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted != value)
            {
                _highlighted = value;
                Invalidate();
            }
        }
    }

    private bool _highlighted;

    protected override bool Interactive => true;

    protected internal override Rectangle FocusRingBounds => ClientRectangle;

    protected override AccessibleStates ExtraAccessibleStates => IsOpen ? AccessibleStates.Expanded : AccessibleStates.Collapsed;

    /// <summary>The text shown: the selected item, or <see cref="DisplayFormat"/> around it (formatted once per change, not per paint).</summary>
    public string ShownText
    {
        get
        {
            string? item = SelectedItem;
            if (!ReferenceEquals(item, _shownFor) || !ReferenceEquals(DisplayFormat, _shownFormat))
            {
                _shownFor = item;
                _shownFormat = DisplayFormat;
                _shown = item is null ? string.Empty
                    : DisplayFormat is null ? item
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, DisplayFormat, item);
            }

            return _shown;
        }
    }

    public static void Draw(Graphics g, Rectangle bounds, string text, VisualState state, int dpi, bool highlighted = false)
    {
        ThemeTokens t = ThemeService.Tokens;
        (Color fill, Color border, Color content, _) = ButtonPainter.Colors(ButtonKind.Secondary, state, t);
        if (highlighted && !state.HasFlag(VisualState.Disabled))
        {
            border = t.Accent;
        }
        float radius = Dpi.ScaleF(4, dpi);
        ButtonPainter.PaintSurface(g, bounds, ButtonKind.Secondary, state, fill, border, false, content, radius, dpi);
        int centreY = bounds.Y + (bounds.Height / 2);
        int chevronX = bounds.Right - Dpi.Scale(26, dpi);
        KitPaint.DrawText(g, text, TypeRamp.For(dpi).Body, content, bounds.X + Dpi.Scale(12, dpi), centreY, chevronX - bounds.X - Dpi.Scale(16, dpi));
        IconRenderer.Draw(g, IconName.ChevronDown, chevronX, centreY - Dpi.ScaleF(8, dpi), 16, dpi, state.HasFlag(VisualState.Disabled) ? t.TextDisabled : t.Text2);
        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Math.Max(Width, S(160)), S(_compact ? 28 : 32));

    public void OpenList()
    {
        if (IsOpen || _items.Count == 0 || !Enabled)
        {
            return;
        }

        PopupItem[] items = [.. _items.Select(i => new PopupItem(i))];

        // At least as wide as the control, and wide enough for the longest item (the Issues filters are narrow).
        int width = Math.Max((int)Math.Round(Width / (DeviceDpi / 96f)), PopupList.DropDownWidth(items, DeviceDpi));
        _open = new PopupList(items, PopupStyle.DropDown, width, _selected);
        _open.Chosen += (_, index) => SelectedIndex = index;
        _open.Dismissed += (_, _) =>
        {
            _open = null;
            Invalidate();

            // The list closes on any mouse press outside it, this drop-down's own included, before the press reaches
            // this control: that press must not open the list again. The flag lasts until the press is dispatched.
            if (IsHandleCreated && !IsDisposed)
            {
                _closedByPress = true;
                BeginInvoke(() => _closedByPress = false);
            }
        };
        _open.Open(this, PointToScreen(new Point(0, Height + S(4))));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        VisualState state = PaintState;
        if (IsOpen)
        {
            state |= VisualState.Pressed;
        }

        Draw(e.Graphics, ClientRectangle, ShownText, state, DeviceDpi, _highlighted);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // This press closed the open list: clicking an open drop-down closes it.
        _ignoreRelease = _closedByPress && e.Button == MouseButtons.Left;
        _closedByPress = false;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_ignoreRelease)
        {
            _ignoreRelease = false;
            return;
        }

        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            OpenList();
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space or Keys.F4 or (Keys.Alt | Keys.Down) || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsOpen)
        {
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Down when e.Alt:
            case Keys.F4:
            case Keys.Enter:
            case Keys.Space:
                // A held key opens the list once: the list takes Enter and Space as a choice, so a repeat would choose,
                // close and open it again for as long as the key is held.
                if (!IsKeyRepeat)
                {
                    OpenList();
                }

                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Up:
                SelectedIndex = Math.Max(0, _selected - 1);
                e.Handled = true;
                break;
            case Keys.Down:
                SelectedIndex = Math.Min(_items.Count - 1, _selected + 1);
                e.Handled = true;
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _open?.Dismiss();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// A segmented control (subplan 08): segments label + 28 px wide; the selected one has a Selected fill
/// inset 2 px and a 2 px Accent underline; separators between two unselected neighbours. Left and Right
/// move the selection.
/// </summary>
internal sealed class SegmentedControl : KitControl
{
    private readonly List<string> _segments = [];
    private int _selected;
    private int _hover = -1;
    private int _pressed = -1;
    private bool _inset;
    private bool _compact;

    public SegmentedControl()
    {
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
    }

    public event EventHandler? SelectedIndexChanged;

    public IList<string> Segments => _segments;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int clamped = _segments.Count == 0 ? 0 : Math.Clamp(value, 0, _segments.Count - 1);
            if (clamped != _selected)
            {
                _selected = clamped;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool Inset
    {
        get => _inset;
        set
        {
            _inset = value;
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

    public int PreferredLogicalWidth => MeasureWidth(_segments, _selected, DeviceDpi);

    protected override bool Interactive => true;

    protected internal override Rectangle FocusRingBounds => ClientRectangle;

    /// <summary>Read out as the chosen segment.</summary>
    protected override string? AccessibleValue => _selected >= 0 && _selected < _segments.Count ? _segments[_selected] : null;

    public static int MeasureWidth(IReadOnlyList<string> segments, int selected, int dpi)
    {
        float scale = dpi / 96f;
        float width = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            width += SegmentWidth(segments[i], i == selected, dpi) / scale;
        }

        return (int)Math.Ceiling(width);
    }

    /// <summary>
    /// Paints the control. <paramref name="pressed"/> is the segment under a held mouse button (Pressed fill, Accent
    /// border, label 1 px lower); in a contrast theme hover is a 2 px Highlight border and the selected label and
    /// underline are HighlightText (subplan 08).
    /// </summary>
    public static void Draw(Graphics g, Rectangle bounds, IReadOnlyList<string> segments, int selected, int hover, bool inset, VisualState state, int dpi, int pressed = -1)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float radius = Dpi.ScaleF(4, dpi);
        bool disabled = state.HasFlag(VisualState.Disabled);
        KitPaint.FillRoundedRect(g, bounds, radius, inset ? t.Bg : t.Surface);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        float x = bounds.X;
        int centreY = bounds.Y + (bounds.Height / 2);
        float innerRadius = Dpi.ScaleF(3, dpi);
        for (int i = 0; i < segments.Count; i++)
        {
            float width = SegmentWidth(segments[i], i == selected, dpi);
            RectangleF segment = new(x, bounds.Y, width, bounds.Height);
            RectangleF inner = RectangleF.Inflate(segment, -Dpi.ScaleF(2, dpi), -Dpi.ScaleF(2, dpi));
            bool isSelected = i == selected;
            bool isPressed = i == pressed && !isSelected && !disabled;
            if (isSelected)
            {
                KitPaint.FillRoundedRect(g, inner, innerRadius, t.Selected);
                float sideInset = Dpi.ScaleF(10, dpi);
                float underline = Dpi.ScaleF(2, dpi);
                KitPaint.FillRoundedRect(g, new RectangleF(segment.X + sideInset, segment.Bottom - Dpi.ScaleF(3, dpi) - underline, segment.Width - (2 * sideInset), underline), underline / 2f, t.IsHighContrast ? t.OnAccent : t.Accent);
            }
            else if (isPressed)
            {
                KitPaint.FillRoundedRect(g, inner, innerRadius, t.Pressed);
                KitPaint.StrokeRoundedRect(g, inner, innerRadius, t.Accent, Dpi.ScaleF(t.IsHighContrast ? 2 : 1, dpi));
            }
            else if (i == hover && !disabled)
            {
                if (t.IsHighContrast)
                {
                    KitPaint.StrokeRoundedRect(g, inner, innerRadius, t.Accent, Dpi.ScaleF(2, dpi));
                }
                else
                {
                    KitPaint.FillRoundedRect(g, inner, innerRadius, t.Hover);
                }
            }

            if (i > 0 && i - 1 != selected && !isSelected)
            {
                float inset8 = Dpi.ScaleF(8, dpi);
                KitPaint.VerticalLine(g, t.Stroke, (float)Math.Round(x), bounds.Y + inset8, bounds.Bottom - inset8, Dpi.ScaleF(1, dpi));
            }

            Font font = isSelected ? type.BodyStrong : type.Body;
            Color color = disabled ? t.TextDisabled : isSelected ? t.TextOnSelected : t.Text2;
            int shift = isSelected ? -Dpi.Scale(1, dpi) : isPressed ? Dpi.Scale(1, dpi) : 0;
            KitPaint.DrawTextCentred(g, segments[i], font, color, (int)Math.Round(segment.X + (segment.Width / 2f)), centreY + shift);
            x += width;
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(S(PreferredLogicalWidth), S(_compact ? 28 : 32));

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, _segments, _selected, _hover, _inset, PaintState, DeviceDpi, IsPressed ? _pressed : -1);

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.X);
        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = IndexAt(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int pressed = _pressed;
        _pressed = -1;
        int index = IndexAt(e.X);
        if (index >= 0 && index == pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            SelectedIndex = index;
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left:
                SelectedIndex = _selected - 1;
                e.Handled = true;
                break;
            case Keys.Right:
                SelectedIndex = _selected + 1;
                e.Handled = true;
                break;
            case Keys.Home:
                SelectedIndex = 0;
                e.Handled = true;
                break;
            case Keys.End:
                SelectedIndex = _segments.Count - 1;
                e.Handled = true;
                break;
        }
    }

    private static float SegmentWidth(string text, bool selected, int dpi)
    {
        TypeRamp type = TypeRamp.For(dpi);
        return KitPaint.MeasureWidth(text, selected ? type.BodyStrong : type.Body) + Dpi.ScaleF(28, dpi);
    }

    private int IndexAt(int x)
    {
        float position = 0;
        for (int i = 0; i < _segments.Count; i++)
        {
            float width = SegmentWidth(_segments[i], i == _selected, DeviceDpi);
            if (x >= position && x < position + width)
            {
                return i;
            }

            position += width;
        }

        return -1;
    }
}

/// <summary>
/// Base for the on/off controls (switch, check box, option row): a mark, then an optional Body label 10 px to the right.
/// A click or Space (on release) toggles; a quick second click toggles again rather than counting as a double-click.
/// </summary>
internal abstract class ToggleControl : KitControl
{
    private bool _checked;

    protected ToggleControl()
    {
        TabStop = true;
        SetStyle(ControlStyles.StandardDoubleClick, false);
    }

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked != value)
            {
                _checked = value;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                OnCheckedChanged();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Logical size of the mark.</summary>
    protected abstract Size MarkSize { get; }

    protected override bool Interactive => true;

    protected override bool ActsOnSpace => true;

    protected override AccessibleStates ExtraAccessibleStates => _checked ? AccessibleStates.Checked : AccessibleStates.None;

    /// <summary>The ring goes around the mark.</summary>
    protected internal override Rectangle FocusRingBounds => MarkBounds;

    /// <summary>The mark's corner radius: half its height (switch), 3 px for a check box.</summary>
    protected internal override float FocusRingRadius => MarkBounds.Height / 2f;

    /// <summary>Where the mark is painted: at the left, centred vertically.</summary>
    protected Rectangle MarkBounds
    {
        get
        {
            Size logical = MarkSize;
            Size mark = new(S(logical.Width), S(logical.Height));
            return new Rectangle(0, (Height - mark.Height) / 2, mark.Width, mark.Height);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size mark = MarkSize;
        int width = mark.Width + (string.IsNullOrEmpty(Text) ? 0 : 10 + (int)Math.Ceiling(KitPaint.MeasureWidth(Text, TypeRamp.For(DeviceDpi).Body) / (DeviceDpi / 96f)));
        return new Size(S(width), S(Math.Max(mark.Height, 20)));
    }

    protected virtual void OnCheckedChanged()
    {
    }

    protected virtual void Toggle() => Checked = !_checked;

    /// <summary>Paints the mark in <paramref name="mark"/>, its focus ring included when the state says focused.</summary>
    protected abstract void PaintMark(Graphics g, Rectangle mark, VisualState state);

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle markRect = MarkBounds;
        PaintMark(e.Graphics, markRect, PaintState);
        if (!string.IsNullOrEmpty(Text))
        {
            int x = markRect.Right + S(10);
            KitPaint.DrawText(e.Graphics, Text, Type.Body, Enabled ? Tokens.Text : Tokens.TextDisabled, x, Height / 2, Width - x);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Enabled)
        {
            Toggle();
        }
    }

    protected override void OnKeyboardAction()
    {
        if (Enabled)
        {
            Toggle();
        }
    }
}

/// <summary>Switch (subplan 08): 40 × 20; knob r 6 (r 7 on hover) at x + 10 off, x + 30 on.</summary>
internal sealed class ToggleSwitch : ToggleControl
{
    public ToggleSwitch()
    {
        AccessibleRole = AccessibleRole.CheckButton;
    }

    protected override Size MarkSize => new(40, 20);

    public static void Draw(Graphics g, Rectangle bounds, bool on, VisualState state, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        bool hover = state.HasFlag(VisualState.Hover);
        bool disabled = state.HasFlag(VisualState.Disabled);
        float radius = bounds.Height / 2f;
        if (on)
        {
            KitPaint.FillRoundedRect(g, bounds, radius, disabled ? t.Hover : hover ? t.AccentHover : t.Accent);
        }
        else
        {
            // A contrast theme shows hover as a 2 px Highlight border instead of a fill (subplan 08).
            bool contrastHover = t.IsHighContrast && hover && !disabled;
            KitPaint.FillRoundedRect(g, bounds, radius, hover && !contrastHover ? t.Hover : t.Surface);
            KitPaint.StrokeRoundedRect(g, bounds, radius, contrastHover ? t.Accent : disabled ? t.Stroke : t.StrokeStrong, Dpi.ScaleF(contrastHover ? 2 : 1, dpi));
        }

        float knob = Dpi.ScaleF(hover && !disabled ? 7 : 6, dpi);
        float cx = bounds.X + Dpi.ScaleF(on ? 30 : 10, dpi);
        float cy = bounds.Y + (bounds.Height / 2f);
        Color knobColor = disabled ? t.TextDisabled : on ? t.OnAccent : t.Text2;
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillEllipse(GdiCache.Brush(knobColor), cx - knob, cy - knob, 2 * knob, 2 * knob);
        g.SmoothingMode = previous;
        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    protected override void PaintMark(Graphics g, Rectangle mark, VisualState state) => Draw(g, mark, Checked, state, DeviceDpi);
}

/// <summary>Check box (subplan 08): 18 × 18, radius 3; on: Accent with a 2 px OnAccent check.</summary>
internal sealed class KitCheckBox : ToggleControl
{
    public KitCheckBox()
    {
        AccessibleRole = AccessibleRole.CheckButton;
    }

    protected override Size MarkSize => new(18, 18);

    protected internal override float FocusRingRadius => SF(3);

    public static void Draw(Graphics g, Rectangle bounds, bool on, VisualState state, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        bool hover = state.HasFlag(VisualState.Hover);
        bool disabled = state.HasFlag(VisualState.Disabled);
        float radius = Dpi.ScaleF(3, dpi);
        if (on)
        {
            KitPaint.FillRoundedRect(g, bounds, radius, disabled ? t.Hover : hover ? t.AccentHover : t.Accent);
            float scale = dpi / 96f;
            PointF[] check = CheckPoints;
            check[0] = new(bounds.X + (4.5f * scale), bounds.Y + (9.5f * scale));
            check[1] = new(bounds.X + (7.75f * scale), bounds.Y + (12.5f * scale));
            check[2] = new(bounds.X + (13.5f * scale), bounds.Y + (5.75f * scale));
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLines(GdiCache.RoundPen(disabled ? t.TextDisabled : t.OnAccent, 2 * scale), check);
            g.SmoothingMode = previous;
        }
        else
        {
            bool contrastHover = t.IsHighContrast && hover && !disabled;
            KitPaint.FillRoundedRect(g, bounds, radius, hover && !contrastHover ? t.Hover : t.Surface);
            KitPaint.StrokeRoundedRect(g, bounds, radius, contrastHover ? t.Accent : disabled ? t.Stroke : t.StrokeStrong, Dpi.ScaleF(contrastHover ? 2 : 1, dpi));
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    protected override void PaintMark(Graphics g, Rectangle mark, VisualState state) => Draw(g, mark, Checked, state, DeviceDpi);

    /// <summary>The check mark's three points, reused by every paint (UI thread only).</summary>
    private static readonly PointF[] CheckPoints = new PointF[3];
}

/// <summary>
/// Radio button painter (subplan 08): 18 px; off a ring r 8.5; on an Accent disc r 9 with an OnAccent dot r 4. Option
/// rows draw it; the app has no stand-alone radio buttons.
/// </summary>
internal static class KitRadioButton
{
    public static void Draw(Graphics g, Rectangle bounds, bool on, VisualState state, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        bool hover = state.HasFlag(VisualState.Hover);
        bool disabled = state.HasFlag(VisualState.Disabled);
        float cx = bounds.X + (bounds.Width / 2f);
        float cy = bounds.Y + (bounds.Height / 2f);
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (on)
        {
            float r = Dpi.ScaleF(9, dpi);
            g.FillEllipse(GdiCache.Brush(disabled ? t.Hover : hover ? t.AccentHover : t.Accent), cx - r, cy - r, 2 * r, 2 * r);
            float dot = Dpi.ScaleF(4, dpi);
            g.FillEllipse(GdiCache.Brush(disabled ? t.TextDisabled : t.OnAccent), cx - dot, cy - dot, 2 * dot, 2 * dot);
        }
        else
        {
            bool contrastHover = t.IsHighContrast && hover && !disabled;
            float r = Dpi.ScaleF(8.5f, dpi);
            g.FillEllipse(GdiCache.Brush(hover && !contrastHover ? t.Hover : t.Surface), cx - r, cy - r, 2 * r, 2 * r);
            g.DrawEllipse(GdiCache.Pen(contrastHover ? t.Accent : disabled ? t.Stroke : t.StrokeStrong, Dpi.ScaleF(contrastHover ? 2 : 1, dpi)), cx - r, cy - r, 2 * r, 2 * r);
        }

        g.SmoothingMode = previous;
        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, bounds.Height / 2f, dpi);
        }
    }
}

/// <summary>
/// An option row (subplan 08): a choice with a description. 52–56 px, radius 4; off Bg + Stroke, on
/// Selected + Accent border; radio at +14, title BodyStrong at +46, description Caption Text2 below,
/// optional value Body right-aligned 14 px from the right. Rows with the same group name under one parent
/// exclude each other; only the checked row is a tab stop, and Up and Down move the choice (Tab leaves the group).
/// </summary>
internal sealed class OptionRow : ToggleControl
{
    private string _groupName = string.Empty;

    public OptionRow()
    {
        AccessibleRole = AccessibleRole.RadioButton;
    }

    public string Description { get; set; } = string.Empty;

    public string? Value { get; set; }

    public string GroupName
    {
        get => _groupName;
        set
        {
            _groupName = value ?? string.Empty;
            UpdateGroupTabStops();
        }
    }

    protected override Size MarkSize => new(18, 18);

    protected internal override Rectangle FocusRingBounds => ClientRectangle;

    protected internal override float FocusRingRadius => SF(4);

    public static void Draw(Graphics g, Rectangle bounds, string title, string description, string? value, bool on, VisualState state, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float radius = Dpi.ScaleF(4, dpi);
        bool disabled = state.HasFlag(VisualState.Disabled);
        bool hover = state.HasFlag(VisualState.Hover) && !disabled;

        // A contrast theme shows hover as a 2 px Highlight border instead of a fill (subplan 08).
        bool contrastHover = t.IsHighContrast && hover && !on;
        KitPaint.FillRoundedRect(g, bounds, radius, on ? t.Selected : hover && !contrastHover ? t.Hover : t.Bg);
        KitPaint.StrokeRoundedRect(g, bounds, radius, on || contrastHover ? t.Accent : hover ? t.StrokeStrong : t.Stroke, Dpi.ScaleF(contrastHover ? 2 : 1, dpi));
        int centreY = bounds.Y + (bounds.Height / 2);
        int radio = Dpi.Scale(18, dpi);
        KitRadioButton.Draw(g, new Rectangle(bounds.X + Dpi.Scale(14, dpi), centreY - (radio / 2), radio, radio), on, state & ~(VisualState.Focused | VisualState.Hover), dpi);
        int x = bounds.X + Dpi.Scale(46, dpi);
        int valueWidth = value is null ? 0 : KitPaint.MeasureWidth(value, type.Body);
        int textWidth = bounds.Right - x - valueWidth - Dpi.Scale(28, dpi);
        Color text = disabled ? t.TextDisabled : on ? t.TextOnSelected : t.Text;
        Color secondary = disabled ? t.TextDisabled : on && t.IsHighContrast ? t.OnAccent : t.Text2;
        KitPaint.DrawText(g, title, type.BodyStrong, text, x, centreY - Dpi.Scale(8, dpi), textWidth);
        KitPaint.DrawText(g, description, type.Caption, secondary, x, centreY + Dpi.Scale(10, dpi), textWidth);
        if (value is not null)
        {
            KitPaint.DrawText(g, value, type.Body, text, bounds.Right - Dpi.Scale(14, dpi) - valueWidth, centreY, valueWidth + 2);
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Math.Max(Width, S(400)), S(56));

    protected override void PaintMark(Graphics g, Rectangle mark, VisualState state)
    {
    }

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, Text, Description, Value, Checked, PaintState, DeviceDpi);

    protected override void Toggle() => Checked = true;

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Up or Keys.Down && Parent is not null)
        {
            List<OptionRow> rows = [.. Group().Where(r => r.Enabled && r.Visible).OrderBy(r => r.Top)];
            int index = rows.IndexOf(this) + (e.KeyCode == Keys.Down ? 1 : -1);
            if (index >= 0 && index < rows.Count)
            {
                rows[index].Checked = true;
                rows[index].Focus();
                e.Handled = true;
            }
        }
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        UpdateGroupTabStops();
    }

    protected override void OnCheckedChanged()
    {
        if (Checked && Parent is not null)
        {
            foreach (OptionRow row in Group())
            {
                if (!ReferenceEquals(row, this))
                {
                    row.Checked = false;
                }
            }
        }

        UpdateGroupTabStops();
    }

    private IEnumerable<OptionRow> Group() =>
        Parent is null ? [this] : Parent.Controls.OfType<OptionRow>().Where(r => r.GroupName == GroupName);

    /// <summary>Only the checked row of a group is a tab stop (every row while none is checked).</summary>
    private void UpdateGroupTabStops()
    {
        List<OptionRow> rows = [.. Group()];
        bool anyChecked = rows.Any(r => r.Checked);
        foreach (OptionRow row in rows)
        {
            row.TabStop = row.Checked || !anyChecked;
        }
    }
}
