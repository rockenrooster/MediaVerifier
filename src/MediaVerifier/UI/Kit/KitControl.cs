using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>The interaction states of subplan 08 that painters distinguish; several can hold at once.</summary>
[Flags]
internal enum VisualState
{
    Rest = 0,
    Hover = 1,
    Pressed = 2,

    /// <summary>Keyboard focus (focus rings are shown for keyboard focus only).</summary>
    Focused = 4,
    Disabled = 8,

    /// <summary>Selected, checked or on.</summary>
    Selected = 16,
}

/// <summary>
/// The paint step every kit control shares: the control paints itself, then the focus ring of the keyboard-focused kit
/// control inside it (<see cref="KitPaint.PaintFocusRings"/>). A ring lies 1 px outside its control (subplan 08), where
/// the control cannot paint, so the controls around it paint it, as in Disk Monitor 2.0. This layer seals
/// Control.OnPaint so the ring comes after whatever a subclass paints; subclasses override
/// <see cref="KitControl.OnPaint"/> as usual.
/// </summary>
internal abstract class KitPaintLayer : Control
{
    protected sealed override void OnPaint(PaintEventArgs e) => PaintWithFocusRing(e);

    private protected abstract void PaintWithFocusRing(PaintEventArgs e);

    /// <summary>Control.OnPaint, which raises the Paint event.</summary>
    private protected void RaisePaint(PaintEventArgs e) => base.OnPaint(e);
}

/// <summary>
/// Base of every custom-painted control (subplan 08, Rendering rules): double buffered, repaints on theme
/// and DPI change, scales logical pixels, knows whether focus came from the keyboard, tracks hover and
/// press for interactive controls (cleared on mouse leave and when the window deactivates), and exposes
/// an accessible name, role and state (task UK-12). Only the left button clicks a kit control; Enter acts once
/// per press and Space on release (<see cref="ActsOnEnter"/>, <see cref="ActsOnSpace"/>); a control is selectable
/// only while it is a tab stop, so arrow keys never move the focus onto cards, pages or other passive controls.
/// </summary>
internal class KitControl : KitPaintLayer
{
    private const int WmKeyDown = 0x0100;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonUp = 0x020C;

    /// <summary>The kit control that has the keyboard focus (its ring shows while <see cref="ShowsFocusRing"/>).</summary>
    private static KitControl? s_focusOwner;

    private bool _themeHooked;
    private bool _hovered;
    private bool _pressed;
    private bool _keyboardFocus;
    private bool _spaceDown;
    private bool _keyRepeat;
    private Form? _hookedForm;

    protected KitControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);

        // Passive until a control says otherwise: interactive controls set TabStop, which makes them selectable.
        TabStop = false;
        ThemeService.Changed += HandleThemeChanged;
        _themeHooked = true;
    }

    /// <summary>The kit control whose keyboard focus ring shows now, or null.</summary>
    internal static KitControl? FocusRingOwner => s_focusOwner is { IsDisposed: false } owner && owner.ShowsFocusRing ? owner : null;

    /// <summary>
    /// The shape the keyboard focus ring goes around, in client coordinates; empty for a control that shows focus
    /// another way (a text box's border, a list's focused row). The ring is drawn 1 px outside it, by the control for
    /// the part inside its bounds and by the containers around it for the rest (<see cref="KitPaint.PaintFocusRings"/>).
    /// </summary>
    protected internal virtual Rectangle FocusRingBounds => Rectangle.Empty;

    /// <summary>The corner radius of <see cref="FocusRingBounds"/> in device pixels (the ring's is 3 more).</summary>
    protected internal virtual float FocusRingRadius => SF(4);

    protected static ThemeTokens Tokens => ThemeService.Tokens;

    protected TypeRamp Type => TypeRamp.For(DeviceDpi);

    /// <summary>True when the control has focus that arrived from the keyboard (Tab, arrows, access keys).</summary>
    protected bool KeyboardFocused => Focused && _keyboardFocus && ShowFocusCues;

    /// <summary>Hover and press are tracked only for controls that react to them.</summary>
    protected virtual bool Interactive => false;

    /// <summary>Space presses the control while held and acts when released, as Windows buttons do.</summary>
    protected virtual bool ActsOnSpace => false;

    /// <summary>Enter acts at once, once per press (holding the key does not repeat the action).</summary>
    protected virtual bool ActsOnEnter => false;

    /// <summary>True while a key-down that auto-repeats a held key is handled (a held key acts once).</summary>
    protected bool IsKeyRepeat => _keyRepeat;

    protected bool IsHovered => _hovered;

    protected bool IsPressed => _pressed;

    /// <summary>The state to paint with: hover, pressed, keyboard focus and disabled.</summary>
    protected VisualState PaintState
    {
        get
        {
            if (!Enabled)
            {
                return VisualState.Disabled;
            }

            VisualState state = VisualState.Rest;
            if (_hovered)
            {
                state |= VisualState.Hover;
            }

            if (_pressed)
            {
                state |= VisualState.Pressed;
            }

            if (KeyboardFocused)
            {
                state |= VisualState.Focused;
            }

            return state;
        }
    }

    protected int S(int logical) => Dpi.Scale(logical, DeviceDpi);

    protected float SF(float logical) => Dpi.ScaleF(logical, DeviceDpi);

    /// <summary>Extra accessible states (checked, selected, expanded) for the accessibility object.</summary>
    protected virtual AccessibleStates ExtraAccessibleStates => AccessibleStates.None;

    /// <summary>The name read out when no AccessibleName is set (the control's text by default).</summary>
    protected virtual string? FallbackAccessibleName => string.IsNullOrEmpty(Text) ? null : Text;

    /// <summary>The description read out when no AccessibleDescription is set (an info bar's action).</summary>
    protected virtual string? FallbackAccessibleDescription => null;

    /// <summary>The value read out (a drop-down's choice, a box's text); null when the control has none.</summary>
    protected virtual string? AccessibleValue => null;

    private bool ShowsFocusRing => KeyboardFocused && Enabled && Visible && !FocusRingBounds.IsEmpty;

    protected virtual void OnThemeChanged() => Invalidate();

    /// <summary>The action of Enter and Space (<see cref="ActsOnEnter"/>, <see cref="ActsOnSpace"/>): a click by default.</summary>
    protected virtual void OnKeyboardAction() => OnClick(EventArgs.Empty);

    /// <summary>Paints the control (by default raises Paint). The focus ring of a focused kit control inside it follows.</summary>
    protected new virtual void OnPaint(PaintEventArgs e) => RaisePaint(e);

    /// <summary>Clears hover and press, for example when the window loses activation.</summary>
    public void ResetInteraction()
    {
        _spaceDown = false;
        if (_hovered || _pressed)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
        }
    }

    /// <summary>Sets the hover state directly (a text box stays hovered while the pointer is over its edit).</summary>
    protected void SetHover(bool hovered)
    {
        hovered &= Interactive && Enabled;
        if (_hovered != hovered)
        {
            _hovered = hovered;
            Invalidate();
        }
    }

    private protected sealed override void PaintWithFocusRing(PaintEventArgs e)
    {
        OnPaint(e);
        KitPaint.PaintFocusRings(e.Graphics, this);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new KitAccessibleObject(this);

    /// <summary>
    /// WinForms raises Click for whichever button is released over a control: a right-click pressed buttons, flipped
    /// switches and confirmed dialogs. Only the left button clicks a kit control; MouseUp still reports every button.
    /// Auto-repeated key-downs are marked, so Enter and Space act once per press.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmKeyDown)
        {
            // Bit 30: the key was already down. A held Enter that opened a dialog must not press the dialog's button
            // too, nor the invoker again when the dialog closes.
            _keyRepeat = (m.LParam.ToInt64() & 0x40000000) != 0;
            try
            {
                base.WndProc(ref m);
            }
            finally
            {
                _keyRepeat = false;
            }

            return;
        }

        if (m.Msg is WmRButtonUp or WmMButtonUp or WmXButtonUp && GetStyle(ControlStyles.StandardClick))
        {
            SetStyle(ControlStyles.StandardClick, false);
            try
            {
                base.WndProc(ref m);
            }
            finally
            {
                SetStyle(ControlStyles.StandardClick, true);
            }

            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>A kit control is selectable only while it is a tab stop: arrow keys skip passive controls.</summary>
    protected override void OnTabStopChanged(EventArgs e)
    {
        base.OnTabStopChanged(e);
        SetStyle(ControlStyles.Selectable, TabStop);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData == Keys.Space && ActsOnSpace) || (keyData == Keys.Enter && ActsOnEnter) || base.IsInputKey(keyData);

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (Interactive && Enabled && !_hovered)
        {
            _hovered = true;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        // A Space press held on the focused control survives the pointer leaving it.
        bool pressed = _pressed && _spaceDown;
        if (_hovered || _pressed != pressed)
        {
            _hovered = false;
            _pressed = pressed;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_keyboardFocus)
        {
            _keyboardFocus = false;
            InvalidateFocusRing();
        }

        if (Interactive && Enabled && e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed && !_spaceDown)
        {
            _pressed = false;
            Invalidate();
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ResetInteraction();
        InvalidateFocusRing();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (s_focusOwner == this)
        {
            InvalidateFocusRing();
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        // Focus that arrives while a mouse button is down came from a click: no focus ring.
        _keyboardFocus = MouseButtons == MouseButtons.None;
        s_focusOwner = this;
        base.OnGotFocus(e);
        InvalidateFocusRing();
        if (_keyboardFocus)
        {
            KitScrolling.RevealFocused(this);
        }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _keyboardFocus = false;
        if (_spaceDown)
        {
            _spaceDown = false;
            _pressed = false;
        }

        base.OnLostFocus(e);
        InvalidateFocusRing();
        if (s_focusOwner == this)
        {
            s_focusOwner = null;
        }
    }

    protected override void OnChangeUICues(UICuesEventArgs e)
    {
        base.OnChangeUICues(e);
        if (Focused)
        {
            InvalidateFocusRing();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_keyboardFocus && Focused)
        {
            _keyboardFocus = true;
            InvalidateFocusRing();
        }

        if (e.Handled || !Enabled || e.Modifiers != Keys.None)
        {
            return;
        }

        if (e.KeyCode == Keys.Space && ActsOnSpace)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (!_spaceDown && !_keyRepeat)
            {
                _spaceDown = true;
                _pressed = true;
                Invalidate();
            }
        }
        else if (e.KeyCode == Keys.Enter && ActsOnEnter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (!_keyRepeat)
            {
                OnKeyboardAction();
            }
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space && _spaceDown)
        {
            _spaceDown = false;
            _pressed = false;
            Invalidate();
            e.Handled = true;
            if (Enabled && ActsOnSpace)
            {
                OnKeyboardAction();
            }
        }
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        // A focused control that moves takes its ring along: clear it where it was, then paint it where it is.
        bool ring = s_focusOwner == this && ShowsFocusRing;
        if (ring)
        {
            InvalidateFocusRing();
        }

        base.SetBoundsCore(x, y, width, height, specified);
        if (ring)
        {
            InvalidateFocusRing();
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        HookForm();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        HookForm();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (s_focusOwner == this)
            {
                s_focusOwner = null;
            }

            if (_themeHooked)
            {
                ThemeService.Changed -= HandleThemeChanged;
                _themeHooked = false;
            }

            if (_hookedForm is not null)
            {
                _hookedForm.Deactivate -= HandleFormDeactivate;
                _hookedForm = null;
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>Repaints the ring's area in this control and in every container around it.</summary>
    private void InvalidateFocusRing()
    {
        Invalidate();
        Rectangle ring = FocusRingBounds;
        if (ring.IsEmpty)
        {
            return;
        }

        int pad = S(4);
        ring.Inflate(pad, pad);
        for (Control child = this; child.Parent is Control parent; child = parent)
        {
            ring.Offset(child.Left, child.Top);
            parent.Invalidate(ring);
            if (parent is Form)
            {
                break;
            }
        }
    }

    private void HookForm()
    {
        if (!Interactive)
        {
            return;
        }

        Form? form = FindForm();
        if (ReferenceEquals(form, _hookedForm))
        {
            return;
        }

        if (_hookedForm is not null)
        {
            _hookedForm.Deactivate -= HandleFormDeactivate;
        }

        _hookedForm = form;
        if (form is not null)
        {
            form.Deactivate += HandleFormDeactivate;
        }
    }

    private void HandleFormDeactivate(object? sender, EventArgs e) => ResetInteraction();

    private void HandleThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            OnThemeChanged();
        }
    }

    /// <summary>Name and role from the control's Accessible* properties (or its Text); state from the control.</summary>
    private sealed class KitAccessibleObject(KitControl owner) : ControlAccessibleObject(owner)
    {
        public override string? Name => owner.AccessibleName ?? owner.FallbackAccessibleName ?? base.Name;

        public override string? Description => owner.AccessibleDescription ?? owner.FallbackAccessibleDescription ?? base.Description;

        public override string? Value
        {
            get => owner.AccessibleValue ?? base.Value;
            set => base.Value = value;
        }

        public override AccessibleRole Role => owner.AccessibleRole != AccessibleRole.Default ? owner.AccessibleRole : base.Role;

        public override AccessibleStates State
        {
            get
            {
                AccessibleStates state = base.State | owner.ExtraAccessibleStates;
                if (!owner.Enabled)
                {
                    state |= AccessibleStates.Unavailable;
                }

                if (owner.TabStop)
                {
                    state |= AccessibleStates.Focusable;
                }

                if (owner.Focused)
                {
                    state |= AccessibleStates.Focused;
                }

                return state;
            }
        }
    }
}
