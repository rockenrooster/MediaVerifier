using System.Globalization;
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
/// A single-line text input (subplan 08, Text box). The frame, text and placeholder are custom-painted;
/// while the box has focus a borderless native edit control inside it takes the typing, caret and
/// selection, so editing behaves exactly like Windows.
/// </summary>
internal class KitTextBox : KitControl
{
    private readonly TextBox _edit;
    private string _placeholder = string.Empty;
    private bool _inset;

    public KitTextBox()
    {
        TabStop = true;
        Cursor = Cursors.IBeam;
        AccessibleRole = AccessibleRole.Text;
        _edit = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Visible = false,
            TabStop = false,
        };
        _edit.TextChanged += (_, _) =>
        {
            if (!string.Equals(base.Text, _edit.Text, StringComparison.Ordinal))
            {
                base.Text = _edit.Text;
            }
        };
        _edit.LostFocus += (_, _) => EndEdit();
        _edit.KeyDown += (_, e) => OnEditKeyDown(e);
        _edit.KeyPress += (_, e) => OnEditKeyPress(e);
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
            AccessibleDescription = _placeholder;
            Invalidate();
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

    protected bool Editing => _edit.Visible;

    /// <summary>Left text inset in logical pixels (the search box moves the text past its icon).</summary>
    protected virtual int TextLeft => InputPainter.TextInset;

    /// <summary>Right text inset in logical pixels.</summary>
    protected virtual int TextRight => InputPainter.TextInset;

    protected virtual bool RightAligned => false;

    protected override bool Interactive => true;

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
        if (!Editing)
        {
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

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        BeginEdit();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Focused && !_edit.Focused)
        {
            Focus();
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

    protected override void OnThemeChanged()
    {
        ApplyColours();
        base.OnThemeChanged();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _edit.Enabled = Enabled;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _commitTimer?.Dispose();
        }

        base.Dispose(disposing);
    }

    private System.Windows.Forms.Timer? _commitTimer;

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

    private void BeginEdit()
    {
        if (!Enabled)
        {
            return;
        }

        PerformLayout();
        _edit.Visible = true;
        _edit.Focus();
        _edit.SelectionStart = _edit.TextLength;
        Invalidate();
    }

    private void EndEdit()
    {
        if (ContainsFocus && _edit.Focused)
        {
            return;
        }

        _edit.Visible = false;
        Invalidate();
    }

    private void ApplyColours()
    {
        _edit.BackColor = _inset ? Tokens.Bg : Tokens.Surface;
        _edit.ForeColor = Tokens.Text;
    }
}

/// <summary>
/// The search box (subplan 08): a 16 px search icon at +10, text at +34, and while it has text a 16 px
/// close icon 10 px from the right that clears it. Filters 200 ms after the last key (<see cref="KitTextBox.TextCommitted"/>).
/// </summary>
internal sealed class SearchBox : KitTextBox
{
    public SearchBox()
    {
        AccessibleRole = AccessibleRole.Text;
    }

    protected override int TextLeft => 34;

    protected override int TextRight => string.IsNullOrEmpty(Text) ? InputPainter.TextInset : 36;

    public static void Draw(Graphics g, Rectangle bounds, string text, string placeholder, VisualState state, bool focused, int dpi)
    {
        InputPainter.PaintTextBox(g, bounds, text, placeholder, state, focused, false, dpi, 34);
        PaintIcons(g, bounds, text, dpi);
    }

    protected override void PaintAdornments(Graphics g) => PaintIcons(g, ClientRectangle, Text, DeviceDpi);

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!string.IsNullOrEmpty(Text) && e.X >= Width - S(36))
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

    private static void PaintIcons(Graphics g, Rectangle bounds, string text, int dpi)
    {
        ThemeTokens t = ThemeService.Tokens;
        float iconY = bounds.Y + ((bounds.Height - Dpi.ScaleF(16, dpi)) / 2f);
        IconRenderer.Draw(g, IconName.Search, bounds.X + Dpi.ScaleF(10, dpi), iconY, 16, dpi, t.Text3);
        if (!string.IsNullOrEmpty(text))
        {
            IconRenderer.Draw(g, IconName.Close, bounds.Right - Dpi.ScaleF(26, dpi), iconY, 16, dpi, t.Text2);
        }
    }
}

/// <summary>A number box (subplan 08): right-aligned digits, Up/Down change by one step, non-digits rejected.</summary>
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

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (TryParse(out double parsed) && parsed >= Minimum && parsed <= Maximum && Differs(parsed))
        {
            _value = parsed;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Number = TryParse(out double parsed) ? parsed : _value;
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

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
        string separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        bool decimalPoint = Decimals > 0 && separator.Length == 1 && e.KeyChar == separator[0];
        if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && !decimalPoint)
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
        _browse.Click += (_, _) => Browse();
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

    private void Browse()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = DialogTitle,
            UseDescriptionForTitle = DialogTitle.Length > 0,
            SelectedPath = Directory.Exists(_box.Text) ? _box.Text : string.Empty,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _box.Text = dialog.SelectedPath;
        }
    }
}
