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
            int clamped = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
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

    protected override AccessibleStates ExtraAccessibleStates => IsOpen ? AccessibleStates.Expanded : AccessibleStates.Collapsed;

    public string ShownText => SelectedItem is string item ? (DisplayFormat is null ? item : string.Format(System.Globalization.CultureInfo.CurrentCulture, DisplayFormat, item)) : string.Empty;

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

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
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
                OpenList();
                e.Handled = true;
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

    public static void Draw(Graphics g, Rectangle bounds, IReadOnlyList<string> segments, int selected, int hover, bool inset, VisualState state, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float radius = Dpi.ScaleF(4, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, inset ? t.Bg : t.Surface);
        KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        float x = bounds.X;
        int centreY = bounds.Y + (bounds.Height / 2);
        for (int i = 0; i < segments.Count; i++)
        {
            float width = SegmentWidth(segments[i], i == selected, dpi);
            RectangleF segment = new(x, bounds.Y, width, bounds.Height);
            bool isSelected = i == selected;
            if (isSelected)
            {
                float inner = Dpi.ScaleF(2, dpi);
                KitPaint.FillRoundedRect(g, RectangleF.Inflate(segment, -inner, -inner), Dpi.ScaleF(3, dpi), t.Selected);
                float sideInset = Dpi.ScaleF(10, dpi);
                float underline = Dpi.ScaleF(2, dpi);
                KitPaint.FillRoundedRect(g, new RectangleF(segment.X + sideInset, segment.Bottom - Dpi.ScaleF(3, dpi) - underline, segment.Width - (2 * sideInset), underline), underline / 2f, t.Accent);
            }
            else if (i == hover && !state.HasFlag(VisualState.Disabled))
            {
                float inner = Dpi.ScaleF(2, dpi);
                KitPaint.FillRoundedRect(g, RectangleF.Inflate(segment, -inner, -inner), Dpi.ScaleF(3, dpi), t.Hover);
            }

            if (i > 0 && i - 1 != selected && !isSelected)
            {
                float inset8 = Dpi.ScaleF(8, dpi);
                KitPaint.VerticalLine(g, t.Stroke, (float)Math.Round(x), bounds.Y + inset8, bounds.Bottom - inset8, Dpi.ScaleF(1, dpi));
            }

            Font font = isSelected ? type.BodyStrong : type.Body;
            Color color = state.HasFlag(VisualState.Disabled) ? t.TextDisabled : isSelected ? t.Text : t.Text2;
            KitPaint.DrawTextCentred(g, segments[i], font, color, (int)Math.Round(segment.X + (segment.Width / 2f)), centreY - (isSelected ? Dpi.Scale(1, dpi) : 0));
            x += width;
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(S(PreferredLogicalWidth), S(_compact ? 28 : 32));

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientRectangle, _segments, _selected, _hover, _inset, PaintState, DeviceDpi);

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

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = IndexAt(e.X);
        if (index >= 0 && e.Button == MouseButtons.Left)
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

/// <summary>Base for the on/off controls (switch, check box, radio button): a mark, then an optional Body label 10 px to the right.</summary>
internal abstract class ToggleControl : KitControl
{
    private bool _checked;

    protected ToggleControl()
    {
        TabStop = true;
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

    protected override AccessibleStates ExtraAccessibleStates => _checked ? AccessibleStates.Checked : AccessibleStates.None;

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

    protected abstract void PaintMark(Graphics g, Rectangle mark, VisualState state);

    protected override void OnPaint(PaintEventArgs e)
    {
        Size logical = MarkSize;
        Size mark = new(S(logical.Width), S(logical.Height));
        Rectangle markRect = new(0, (Height - mark.Height) / 2, mark.Width, mark.Height);
        VisualState state = PaintState;
        PaintMark(e.Graphics, markRect, state & ~VisualState.Focused);
        if (!string.IsNullOrEmpty(Text))
        {
            int x = markRect.Right + S(10);
            KitPaint.DrawText(e.Graphics, Text, Type.Body, Enabled ? Tokens.Text : Tokens.TextDisabled, x, Height / 2, Width - x);
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(e.Graphics, markRect, markRect.Height / 2f, DeviceDpi);
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
            KitPaint.FillRoundedRect(g, bounds, radius, hover ? t.Hover : t.Surface);
            KitPaint.StrokeRoundedRect(g, bounds, radius, disabled ? t.Stroke : t.StrokeStrong, Dpi.ScaleF(1, dpi));
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
            PointF[] check =
            [
                new(bounds.X + (4.5f * scale), bounds.Y + (9.5f * scale)),
                new(bounds.X + (7.75f * scale), bounds.Y + (12.5f * scale)),
                new(bounds.X + (13.5f * scale), bounds.Y + (5.75f * scale)),
            ];
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLines(GdiCache.RoundPen(disabled ? t.TextDisabled : t.OnAccent, 2 * scale), check);
            g.SmoothingMode = previous;
        }
        else
        {
            KitPaint.FillRoundedRect(g, bounds, radius, hover ? t.Hover : t.Surface);
            KitPaint.StrokeRoundedRect(g, bounds, radius, disabled ? t.Stroke : t.StrokeStrong, Dpi.ScaleF(1, dpi));
        }

        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, radius, dpi);
        }
    }

    protected override void PaintMark(Graphics g, Rectangle mark, VisualState state) => Draw(g, mark, Checked, state, DeviceDpi);
}

/// <summary>Radio button (subplan 08): 18 px; off a ring r 8.5; on an Accent disc r 9 with an OnAccent dot r 4.</summary>
internal sealed class KitRadioButton : ToggleControl
{
    public KitRadioButton()
    {
        AccessibleRole = AccessibleRole.RadioButton;
    }

    /// <summary>Radio buttons with the same group name under the same parent exclude each other.</summary>
    public string GroupName { get; set; } = string.Empty;

    protected override Size MarkSize => new(18, 18);

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
            float r = Dpi.ScaleF(8.5f, dpi);
            g.FillEllipse(GdiCache.Brush(hover ? t.Hover : t.Surface), cx - r, cy - r, 2 * r, 2 * r);
            g.DrawEllipse(GdiCache.Pen(disabled ? t.Stroke : t.StrokeStrong, Dpi.ScaleF(1, dpi)), cx - r, cy - r, 2 * r, 2 * r);
        }

        g.SmoothingMode = previous;
        if (state.HasFlag(VisualState.Focused))
        {
            KitPaint.DrawFocusRing(g, bounds, bounds.Height / 2f, dpi);
        }
    }

    protected override void PaintMark(Graphics g, Rectangle mark, VisualState state) => Draw(g, mark, Checked, state, DeviceDpi);

    protected override void Toggle() => Checked = true;

    protected override void OnCheckedChanged()
    {
        if (!Checked || Parent is null)
        {
            return;
        }

        foreach (Control sibling in Parent.Controls)
        {
            if (sibling is KitRadioButton radio && !ReferenceEquals(radio, this) && radio.GroupName == GroupName)
            {
                radio.Checked = false;
            }
            else if (sibling is OptionRow row && row.GroupName == GroupName)
            {
                row.Checked = false;
            }
        }
    }
}

/// <summary>
/// An option row (subplan 08): a choice with a description. 52–56 px, radius 4; off Bg + Stroke, on
/// Selected + Accent border; radio at +14, title BodyStrong at +46, description Caption Text2 below,
/// optional value Body right-aligned 14 px from the right.
/// </summary>
internal sealed class OptionRow : ToggleControl
{
    public OptionRow()
    {
        AccessibleRole = AccessibleRole.RadioButton;
    }

    public string Description { get; set; } = string.Empty;

    public string? Value { get; set; }

    public string GroupName { get; set; } = string.Empty;

    protected override Size MarkSize => new(18, 18);

    public static void Draw(Graphics g, Rectangle bounds, string title, string description, string? value, bool on, VisualState state, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(dpi);
        float radius = Dpi.ScaleF(4, dpi);
        bool hover = state.HasFlag(VisualState.Hover);
        KitPaint.FillRoundedRect(g, bounds, radius, on ? t.Selected : hover ? t.Hover : t.Bg);
        KitPaint.StrokeRoundedRect(g, bounds, radius, on ? t.Accent : hover ? t.StrokeStrong : t.Stroke, Dpi.ScaleF(1, dpi));
        int centreY = bounds.Y + (bounds.Height / 2);
        int radio = Dpi.Scale(18, dpi);
        KitRadioButton.Draw(g, new Rectangle(bounds.X + Dpi.Scale(14, dpi), centreY - (radio / 2), radio, radio), on, state & ~(VisualState.Focused | VisualState.Hover), dpi);
        int x = bounds.X + Dpi.Scale(46, dpi);
        int valueWidth = value is null ? 0 : KitPaint.MeasureWidth(value, type.Body);
        int textWidth = bounds.Right - x - valueWidth - Dpi.Scale(28, dpi);
        bool disabled = state.HasFlag(VisualState.Disabled);
        KitPaint.DrawText(g, title, type.BodyStrong, disabled ? t.TextDisabled : t.Text, x, centreY - Dpi.Scale(8, dpi), textWidth);
        KitPaint.DrawText(g, description, type.Caption, disabled ? t.TextDisabled : t.Text2, x, centreY + Dpi.Scale(10, dpi), textWidth);
        if (value is not null)
        {
            KitPaint.DrawText(g, value, type.Body, disabled ? t.TextDisabled : t.Text, bounds.Right - Dpi.Scale(14, dpi) - valueWidth, centreY, valueWidth + 2);
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
            List<OptionRow> rows = [.. Parent.Controls.OfType<OptionRow>().Where(r => r.GroupName == GroupName && r.Enabled).OrderBy(r => r.Top)];
            int index = rows.IndexOf(this) + (e.KeyCode == Keys.Down ? 1 : -1);
            if (index >= 0 && index < rows.Count)
            {
                rows[index].Focus();
                rows[index].Checked = true;
                e.Handled = true;
            }
        }
    }

    protected override void OnCheckedChanged()
    {
        if (!Checked || Parent is null)
        {
            return;
        }

        foreach (Control sibling in Parent.Controls)
        {
            if (sibling is OptionRow row && !ReferenceEquals(row, this) && row.GroupName == GroupName)
            {
                row.Checked = false;
            }
        }
    }
}
