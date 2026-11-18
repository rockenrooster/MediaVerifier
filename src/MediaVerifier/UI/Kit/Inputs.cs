using System.Globalization;
using System.Runtime.InteropServices;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>Frames of text inputs (subplan 08, Inputs).</summary>
internal static class InputPainter
{
    public const int Height = 32;
    public const int TextInset = 10;

    /// <summary>
    /// Text box frame: fill Surface on Bg (Bg when the box sits on Surface or Raised), border Stroke, hover
    /// StrokeStrong, focused a 2 px Accent border.
    /// </summary>
    public static void PaintFrame(Graphics g, Rectangle bounds, VisualState state, bool focused, bool inset, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float radius = Dpi.ScaleF(4, dpi);
        KitPaint.FillRoundedRect(g, bounds, radius, inset ? t.Bg : t.Surface);
        if (focused)
        {
            KitPaint.StrokeRoundedRect(g, bounds, radius, t.Accent, Dpi.ScaleF(2, dpi));
        }
        else if (state.HasFlag(VisualState.Hover) && !state.HasFlag(VisualState.Disabled))
        {
            KitPaint.StrokeRoundedRect(g, bounds, radius, t.IsHighContrast ? t.Accent : t.StrokeStrong, Dpi.ScaleF(t.IsHighContrast ? 2 : 1, dpi));
        }
        else
        {
            KitPaint.StrokeRoundedRect(g, bounds, radius, t.Stroke, Dpi.ScaleF(1, dpi));
        }
    }

    /// <summary>Paints a whole text box as the harness and an unfocused box show it.</summary>
    public static void PaintTextBox(Graphics g, Rectangle bounds, string text, string? placeholder, VisualState state, bool focused, bool inset, int dpi, int textX = TextInset, bool rightAlign = false)
    {
        ThemeTokens t = ThemeService.Tokens;
        PaintFrame(g, bounds, state, focused, inset, dpi);
        Font font = TypeRamp.For(dpi).Body;
        int x = bounds.X + Dpi.Scale(textX, dpi);
        int width = bounds.Right - x - Dpi.Scale(TextInset, dpi);
        int centreY = bounds.Y + (bounds.Height / 2);
        bool empty = string.IsNullOrEmpty(text);
        string shown = empty ? placeholder ?? string.Empty : text;
        Color color = state.HasFlag(VisualState.Disabled) ? t.TextDisabled : empty ? t.Text3 : t.Text;
        KitPaint.DrawText(g, shown, font, color, x, centreY, width, rightAlign ? TextFormatFlags.Right : TextFormatFlags.Default);
        if (focused)
        {
            // The caret: 1 px Text after the text (the live box shows the native caret instead).
            int caretX = rightAlign ? bounds.Right - Dpi.Scale(TextInset, dpi) : x + (empty ? 0 : KitPaint.MeasureWidth(text, font)) + Dpi.Scale(1, dpi);
            KitPaint.VerticalLine(g, t.Text, caretX, centreY - Dpi.ScaleF(8, dpi), centreY + Dpi.ScaleF(8, dpi), Dpi.ScaleF(1, dpi));
        }
    }
}

/// <summary>
/// A single-line text input (subplan 08, Text box): a painted frame around a borderless native edit control, which
/// stays visible and is the tab stop (as in Disk Monitor 2.0), so clicks place the caret where they land, Shift+Tab
/// leaves the box, and the focus survives switching to another window and back. The frame shows hover and focus; the
/// edit paints the placeholder while it is empty and unfocused. A disabled box hides the edit and paints its text.
/// </summary>
internal class KitTextBox : KitControl
{
    private readonly EditBox _edit;
    private string _placeholder = string.Empty;
    private bool _inset;

    public KitTextBox()
    {
        TabStop = false;
        Cursor = Cursors.IBeam;
        AccessibleRole = AccessibleRole.Text;
        _edit = new EditBox(this)
        {
            BorderStyle = BorderStyle.None,
            TabStop = true,
        };
        _edit.TextChanged += (_, _) =>
        {
            if (!string.Equals(base.Text, _edit.Text, StringComparison.Ordinal))
            {
                base.Text = _edit.Text;
            }
        };
        _edit.GotFocus += (_, _) => OnEditGotFocus();
        _edit.LostFocus += (_, _) => OnEditLostFocus();
        _edit.MouseEnter += (_, _) => SetHover(true);
        _edit.MouseLeave += (_, _) => SetHover(ClientRectangle.Contains(PointToClient(MousePosition)));
        _edit.KeyDown += (_, e) => OnEditKeyDown(e);
        _edit.KeyPress += (_, e) =>
        {
            if (!_edit.Multiline && e.KeyChar is '\r' or '\u001b')
            {
                // Enter and Esc act through the dialog or page; a single-line box beeped at their characters.
                e.Handled = true;
                return;
            }

            OnEditKeyPress(e);
        };
        Controls.Add(_edit);
        ApplyColours();
    }

    /// <summary>Raised after the text changed and no key was typed for 200 ms (search boxes filter on it).</summary>
    public event EventHandler? TextCommitted;

    public string Placeholder
    {
        get => _placeholder;
        set
        {
            _placeholder = value ?? string.Empty;
            AccessibleDescription = _placeholder.Length > 0 ? _placeholder : null;
            _edit.AccessibleDescription = AccessibleDescription;
            NameEdit();
            _edit.Invalidate();
            Invalidate();
        }
    }

    /// <summary>The name Narrator reads when the edit has focus; the edit carries it, since the edit is what is focused.</summary>
    public new string? AccessibleName
    {
        get => base.AccessibleName;
        set
        {
            base.AccessibleName = value;
            NameEdit();
        }
    }

    /// <summary>The box sits on Surface or Raised, so its fill is Bg.</summary>
    public bool Inset
    {
        get => _inset;
        set
        {
            _inset = value;
            ApplyColours();
            Invalidate();
        }
    }

    public int MaxLength
    {
        get => _edit.MaxLength;
        set => _edit.MaxLength = value;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            value ??= string.Empty;
            if (!string.Equals(_edit.Text, value, StringComparison.Ordinal))
            {
                _edit.Text = value;
            }

            if (!string.Equals(base.Text, value, StringComparison.Ordinal))
            {
                base.Text = value;
            }
        }
    }

    /// <summary>True while the edit has the keyboard focus.</summary>
    protected bool Editing => _edit.Focused;

    /// <summary>Caret position in the edit (the number box accepts a minus sign only at the start).</summary>
    protected int SelectionStart => _edit.SelectionStart;

    protected int SelectionLength => _edit.SelectionLength;

    /// <summary>Left text inset in logical pixels (the search box moves the text past its icon).</summary>
    protected virtual int TextLeft => InputPainter.TextInset;

    /// <summary>Right text inset in logical pixels.</summary>
    protected virtual int TextRight => InputPainter.TextInset;

    protected virtual bool RightAligned => false;

    protected override bool Interactive => true;

    /// <summary>A box's text is its value, not its name.</summary>
    protected override string? FallbackAccessibleName => null;

    /// <summary>The edit's name when no AccessibleName is set (a search box's placeholder).</summary>
    protected virtual string? EditFallbackName => null;

    public void SelectAll() => _edit.SelectAll();

    public override Size GetPreferredSize(Size proposedSize) => new(Math.Max(Width, S(200)), S(InputPainter.Height));

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
        RestartCommitTimer();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        VisualState state = PaintState & ~VisualState.Focused;
        InputPainter.PaintFrame(e.Graphics, ClientRectangle, state, Editing, _inset, DeviceDpi);
        if (!_edit.Visible)
        {
            // Disabled (or not shown yet): the edit is hidden and the frame paints the text (or the placeholder).
            ThemeTokens t = Tokens;
            bool empty = string.IsNullOrEmpty(Text);
            Color color = !Enabled ? t.TextDisabled : empty ? t.Text3 : t.Text;
            int x = S(TextLeft);
            KitPaint.DrawText(e.Graphics, empty ? _placeholder : Text, Type.Body, color, x, Height / 2, Width - x - S(TextRight), RightAligned ? TextFormatFlags.Right : TextFormatFlags.Default);
        }

        PaintAdornments(e.Graphics);
    }

    /// <summary>Icons drawn on top of the frame (search and clear icons).</summary>
    protected virtual void PaintAdornments(Graphics g)
    {
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : Tokens.Bg);

    /// <summary>Focus given to the box (a page's Ctrl+F, a dialog's first field) goes to the edit.</summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_edit.CanFocus)
        {
            _edit.Focus();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !_edit.Visible || _edit.Focused || !InTextArea(e.X))
        {
            return;
        }

        // A click in the padding around the edit: the caret goes to the nearer end of the text.
        _edit.Focus();
        _edit.Select(e.X >= _edit.Right ? _edit.TextLength : 0, 0);
    }

    /// <summary>Whether a click at <paramref name="x"/> belongs to the text (not to an adornment such as the clear icon).</summary>
    protected virtual bool InTextArea(int x) => true;

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        // Moving onto the edit leaves the frame but stays inside the box.
        if (ClientRectangle.Contains(PointToClient(MousePosition)))
        {
            SetHover(true);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int x = S(TextLeft);
        int lineHeight = Type.Body.Height;
        _edit.Font = Type.Body;
        _edit.TextAlign = RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _edit.Bounds = new Rectangle(x, (Height - lineHeight) / 2, Math.Max(1, Width - x - S(TextRight)), lineHeight);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NameEdit();
    }

    protected override void OnThemeChanged()
    {
        ApplyColours();
        base.OnThemeChanged();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _edit.Visible = Enabled;
        Invalidate();
    }

    /// <summary>Lets derived boxes handle Up/Down or Escape inside the edit control.</summary>
    protected virtual void OnEditKeyDown(KeyEventArgs e)
    {
        OnKeyDown(e);
    }

    /// <summary>Lets derived boxes reject characters (the number box accepts digits only).</summary>
    protected virtual void OnEditKeyPress(KeyPressEventArgs e)
    {
    }

    /// <summary>The edit lost the focus: focus moved on, or the window was left (the number box applies its limits).</summary>
    protected virtual void OnEditLostFocus()
    {
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _commitTimer?.Dispose();
        }

        base.Dispose(disposing);
    }

    private System.Windows.Forms.Timer? _commitTimer;

    private void OnEditGotFocus()
    {
        Invalidate();
        if (MouseButtons == MouseButtons.None)
        {
            // Tab or a shortcut brought the focus: scroll the box into view, as kit controls do.
            KitScrolling.RevealFocused(this);
        }
    }

    private void RestartCommitTimer()
    {
        if (TextCommitted is null)
        {
            return;
        }

        if (_commitTimer is null)
        {
            _commitTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _commitTimer.Tick += (_, _) =>
            {
                _commitTimer.Stop();
                TextCommitted?.Invoke(this, EventArgs.Empty);
            };
        }

        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void NameEdit()
    {
        string? name = base.AccessibleName ?? EditFallbackName;
        if (!string.Equals(_edit.AccessibleName, name, StringComparison.Ordinal))
        {
            _edit.AccessibleName = name;
        }
    }

    private void ApplyColours()
    {
        _edit.BackColor = _inset ? Tokens.Bg : Tokens.Surface;
        _edit.ForeColor = Tokens.Text;
        _edit.Invalidate();
    }

    /// <summary>
    /// The native edit: no margins (the text starts at the box's text inset, as the reference shows), and it paints the
    /// placeholder in Text3 while it is empty and unfocused, as Windows' own cue banner does, in the theme's colour.
    /// </summary>
    private sealed class EditBox(KitTextBox owner) : TextBox
    {
        private const int WmPaint = 0x000F;
        private const int WmPrintClient = 0x0318;
        private const int EmSetMargins = 0x00D3;
        private const int EcLeftAndRightMargin = 0x0003;
        private bool _showsPlaceholder;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ClearMargins();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            ClearMargins();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RepaintPlaceholder();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            RepaintPlaceholder();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            RepaintPlaceholder();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WmPaint)
            {
                _showsPlaceholder = ShouldShowPlaceholder();
                if (_showsPlaceholder)
                {
                    IntPtr dc = GetDC(Handle);
                    try
                    {
                        DrawPlaceholder(dc);
                    }
                    finally
                    {
                        _ = ReleaseDC(Handle, dc);
                    }
                }
            }
            else if (m.Msg == WmPrintClient && ShouldShowPlaceholder())
            {
                // Printed (the render harness, a dialog's backdrop): the placeholder belongs in the picture too.
                DrawPlaceholder(m.WParam);
            }
        }

        private void DrawPlaceholder(IntPtr dc)
        {
            TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | (TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            NativeText.Draw(dc, owner._placeholder, Font, ThemeService.Tokens.Text3, ClientRectangle, flags);
        }

        private bool ShouldShowPlaceholder() => owner._placeholder.Length > 0 && TextLength == 0 && !Focused;

        /// <summary>The edit erases only what it redraws: show or clear the placeholder by repainting it whole.</summary>
        private void RepaintPlaceholder()
        {
            if (IsHandleCreated && _showsPlaceholder != ShouldShowPlaceholder())
            {
                Invalidate();
            }
        }

        private void ClearMargins()
        {
            if (IsHandleCreated)
            {
                _ = SendMessage(Handle, EmSetMargins, (IntPtr)EcLeftAndRightMargin, IntPtr.Zero);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}

/// <summary>
/// The search box (subplan 08): a 16 px search icon at +10, text at +34, and while it has text a 16 px
/// close icon 10 px from the right that clears it (its 36 px are always kept free of the edit, so the icon is never
/// covered while typing). Filters 200 ms after the last key (<see cref="KitTextBox.TextCommitted"/>).
/// </summary>
internal sealed class SearchBox : KitTextBox
{
    private bool _clearHover;

    public SearchBox()
    {
        AccessibleRole = AccessibleRole.Text;
    }

    /// <summary>A search box is named by its placeholder ("Filter rules", "Search files") unless named otherwise.</summary>
    protected override string? FallbackAccessibleName => string.IsNullOrEmpty(Placeholder) ? null : Placeholder;

    protected override string? EditFallbackName => FallbackAccessibleName;

    protected override string? AccessibleValue => Text;

    protected override int TextLeft => 34;

    protected override int TextRight => 36;

    public static void Draw(Graphics g, Rectangle bounds, string text, string placeholder, VisualState state, bool focused, int dpi)
    {
        InputPainter.PaintTextBox(g, bounds, text, placeholder, state, focused, false, dpi, 34);
        PaintIcons(g, bounds, text, false, dpi);
    }

    protected override void PaintAdornments(Graphics g) => PaintIcons(g, ClientRectangle, Text, _clearHover && Enabled, DeviceDpi);

    protected override bool InTextArea(int x) => string.IsNullOrEmpty(Text) || !InClearZone(x);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (IsHandleCreated)
        {
            UpdateClearHover(PointToClient(MousePosition));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateClearHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateClearHover(new Point(-1, -1));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && !string.IsNullOrEmpty(Text) && InClearZone(e.X) && ClientRectangle.Contains(e.Location))
        {
            Text = string.Empty;
            Focus();
        }
    }

    protected override void OnEditKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && !string.IsNullOrEmpty(Text))
        {
            Text = string.Empty;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnEditKeyDown(e);
    }

    /// <summary>The search icon, and while there is text the clear icon (on a Hover square while the pointer is on it).</summary>
    private static void PaintIcons(Graphics g, Rectangle bounds, string text, bool clearHover, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float iconY = bounds.Y + ((bounds.Height - Dpi.ScaleF(16, dpi)) / 2f);
        IconRenderer.Draw(g, IconName.Search, bounds.X + Dpi.ScaleF(10, dpi), iconY, 16, dpi, t.Text3);
        if (!string.IsNullOrEmpty(text))
        {
            float iconX = bounds.Right - Dpi.ScaleF(26, dpi);
            if (clearHover)
            {
                // The subtle icon button's hover: a Hover square, or a 2 px Highlight border in a contrast theme.
                float size = Dpi.ScaleF(24, dpi);
                RectangleF square = new(iconX + Dpi.ScaleF(8, dpi) - (size / 2f), iconY + Dpi.ScaleF(8, dpi) - (size / 2f), size, size);
                if (t.IsHighContrast)
                {
                    KitPaint.StrokeRoundedRect(g, square, Dpi.ScaleF(4, dpi), t.Accent, Dpi.ScaleF(2, dpi));
                }
                else
                {
                    KitPaint.FillRoundedRect(g, square, Dpi.ScaleF(4, dpi), t.Hover);
                }
            }

            IconRenderer.Draw(g, IconName.Close, iconX, iconY, 16, dpi, clearHover ? t.Text : t.Text2);
        }
    }

    private bool InClearZone(int x) => x >= Width - S(36);

    /// <summary>Over the clear icon the pointer is the arrow and the icon shows its hover.</summary>
    private void UpdateClearHover(Point point)
    {
        bool over = !string.IsNullOrEmpty(Text) && InClearZone(point.X) && ClientRectangle.Contains(point);
        Cursor = over ? Cursors.Default : Cursors.IBeam;
        if (over != _clearHover)
        {
            _clearHover = over;
            Invalidate();
        }
    }
}

/// <summary>
/// A number box (subplan 08): right-aligned digits, Up/Down change by one step, other characters rejected (a minus sign
/// is accepted at the start when the range allows negative values). A value in range applies as it is typed; when the
/// edit loses the focus the text is brought within the limits, so what the box shows is always the value in use.
/// </summary>
internal sealed class NumberBox : KitTextBox
{
    private double _value;

    public NumberBox()
    {
        AccessibleRole = AccessibleRole.SpinButton;
        Text = "0";
    }

    public event EventHandler? ValueChanged;

    public double Minimum { get; set; }

    public double Maximum { get; set; } = int.MaxValue;

    /// <summary>Digits after the decimal separator; 0 accepts whole numbers only.</summary>
    public int Decimals { get; set; }

    public double Step { get; set; } = 1;

    /// <summary>The value rounded to a whole number.</summary>
    public int Value
    {
        get => (int)Math.Round(_value);
        set => Number = value;
    }

    /// <summary>The value with <see cref="Decimals"/> digits after the separator, kept within the limits.</summary>
    public double Number
    {
        get => _value;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum)), Decimals);
            string text = clamped.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
            if (Text != text)
            {
                Text = text;
            }

            if (Differs(clamped))
            {
                _value = clamped;
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    protected override bool RightAligned => true;

    /// <summary>Applies the limits to what was typed (an empty or out-of-range entry shows the value in use again).</summary>
    public void Normalize() => Number = TryParse(out double parsed) ? parsed : _value;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (TryParse(out double parsed) && parsed >= Minimum && parsed <= Maximum && Differs(parsed))
        {
            _value = parsed;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnEditLostFocus()
    {
        base.OnEditLostFocus();
        Normalize();
    }

    protected override void OnEditKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            Number = _value + (e.KeyCode == Keys.Up ? Step : -Step);
            SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnEditKeyDown(e);
    }

    protected override void OnEditKeyPress(KeyPressEventArgs e)
    {
        NumberFormatInfo format = CultureInfo.CurrentCulture.NumberFormat;
        string separator = format.NumberDecimalSeparator;
        string minus = format.NegativeSign;
        bool decimalPoint = Decimals > 0 && separator.Length == 1 && e.KeyChar == separator[0];

        // A minus sign only where it can stand: first, when the range has negative values and no sign is typed yet
        // outside the text it replaces (selecting -60 and typing -70 works).
        bool sign = Minimum < 0 && minus.Length == 1 && e.KeyChar == minus[0] && SelectionStart == 0
            && !Text.AsSpan(SelectionLength).Contains(minus[0]);
        if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && !decimalPoint && !sign)
        {
            e.Handled = true;
        }
    }

    private bool Differs(double value) => Math.Abs(value - _value) > 0.5 * Math.Pow(10, -Decimals - 1);

    private bool TryParse(out double value)
    {
        bool ok = double.TryParse(Text, Decimals > 0 ? NumberStyles.Float : NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
        value = ok ? Math.Round(value, Decimals) : 0;
        return ok;
    }
}

/// <summary>A path box (subplan 08): a text box, an 8 px gap and a secondary Browse… button that picks a folder.</summary>
internal sealed class PathBox : Control
{
    private readonly KitTextBox _box = new();
    private readonly KitButton _browse = new() { Kind = ButtonKind.Secondary, Text = Strings.Kit.Browse };

    public PathBox()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
        _box.TabIndex = 0;
        _browse.TabIndex = 1;
        _box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _browse.Click += (_, _) => UiTask.Run(BrowseAsync());
        Controls.Add(_box);
        Controls.Add(_browse);
    }

    /// <summary>Title of the folder picker.</summary>
    public string DialogTitle { get; set; } = string.Empty;

    public bool Inset
    {
        get => _box.Inset;
        set => _box.Inset = value;
    }

    /// <summary>The name of the path field (the focused edit carries it).</summary>
    public new string? AccessibleName
    {
        get => _box.AccessibleName;
        set
        {
            base.AccessibleName = value;
            _box.AccessibleName = value;
        }
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int buttonWidth = Dpi.Scale(_browse.PreferredLogicalWidth, DeviceDpi);
        int gap = Dpi.Scale(8, DeviceDpi);
        _browse.Bounds = new Rectangle(Width - buttonWidth, 0, buttonWidth, Height);
        _box.Bounds = new Rectangle(0, 0, Math.Max(0, Width - buttonWidth - gap), Height);
    }

    /// <summary>Focus given to the path box (Add folder opens with it) goes to the text field.</summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _box.Focus();
    }

    /// <summary>The Browse button's focus ring falls partly between the field and the button, on this control.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        KitPaint.PaintFocusRings(e.Graphics, this);
    }

    /// <summary>
    /// The picker starts in the typed folder only when the folder answers within 2 seconds. Checked on the UI thread, a
    /// network folder that is offline held the window for over two minutes (measured: 136 s), and the picker itself checks
    /// the folder it is given; otherwise it opens where Windows chooses.
    /// </summary>
    private async Task BrowseAsync()
    {
        string start;
        _browse.Enabled = false;
        try
        {
            start = await StartFolderAsync(_box.Text.Trim(), TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        }
        finally
        {
            _browse.Enabled = true;
        }

        using FolderBrowserDialog dialog = new()
        {
            Description = DialogTitle,
            UseDescriptionForTitle = DialogTitle.Length > 0,
            SelectedPath = start,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _box.Text = dialog.SelectedPath;
        }
    }

    /// <summary><paramref name="typed"/> when it is a folder that answers within <paramref name="wait"/>; otherwise empty.</summary>
    internal static async Task<string> StartFolderAsync(string typed, TimeSpan wait)
    {
        if (typed.Length == 0)
        {
            return string.Empty;
        }

        Task<bool> exists = Task.Run(() => Directory.Exists(typed));
        return await Task.WhenAny(exists, Task.Delay(wait)).ConfigureAwait(false) == exists && exists.Result ? typed : string.Empty;
    }
}
