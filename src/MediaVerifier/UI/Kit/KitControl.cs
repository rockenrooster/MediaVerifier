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
/// Base of every custom-painted control (subplan 08, Rendering rules): double buffered, repaints on theme
/// and DPI change, scales logical pixels, knows whether focus came from the keyboard, tracks hover and
/// press for interactive controls (cleared on mouse leave and when the window deactivates), and exposes
/// an accessible name, role and state (task UK-12).
/// </summary>
internal class KitControl : Control
{
    private bool _themeHooked;
    private bool _hovered;
    private bool _pressed;
    private bool _keyboardFocus;
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
        ThemeService.Changed += HandleThemeChanged;
        _themeHooked = true;
    }

    protected static ThemeTokens Tokens => ThemeService.Tokens;

    protected TypeRamp Type => TypeRamp.For(DeviceDpi);

    /// <summary>True when the control has focus that arrived from the keyboard (Tab, arrows, access keys).</summary>
    protected bool KeyboardFocused => Focused && _keyboardFocus && ShowFocusCues;

    /// <summary>Hover and press are tracked only for controls that react to them.</summary>
    protected virtual bool Interactive => false;

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

    protected virtual void OnThemeChanged() => Invalidate();

    /// <summary>Clears hover and press, for example when the window loses activation.</summary>
    public void ResetInteraction()
    {
        if (_hovered || _pressed)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new KitAccessibleObject(this);

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
        ResetInteraction();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _keyboardFocus = false;
        if (Interactive && Enabled && e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ResetInteraction();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        // Focus that arrives while a mouse button is down came from a click: no focus ring.
        _keyboardFocus = MouseButtons == MouseButtons.None;
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _keyboardFocus = false;
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_keyboardFocus && Focused)
        {
            _keyboardFocus = true;
            Invalidate();
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
        public override string? Name => owner.AccessibleName ?? (string.IsNullOrEmpty(owner.Text) ? base.Name : owner.Text);

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
