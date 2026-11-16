using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Kit;

/// <summary>
/// Base of every custom-painted control: double buffered, repaints on theme change, scales logical
/// pixels to the control's DPI, and knows whether focus came from the keyboard (focus rings are shown
/// for keyboard focus only, subplan 08).
/// </summary>
internal class KitControl : Control
{
    private bool _themeHooked;

    protected KitControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);
        HookTheme();
    }

    protected static ThemeTokens Tokens => Theme.Theme.Tokens;

    protected TypeRamp Type => TypeRamp.For(DeviceDpi);

    /// <summary>True when the control has focus and Windows is showing keyboard cues.</summary>
    protected bool KeyboardFocused => Focused && ShowFocusCues;

    protected int S(int logical) => Dpi.Scale(logical, DeviceDpi);

    protected float SF(float logical) => Dpi.ScaleF(logical, DeviceDpi);

    protected virtual void OnThemeChanged()
    {
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _themeHooked)
        {
            Theme.Theme.Changed -= HandleThemeChanged;
            _themeHooked = false;
        }

        base.Dispose(disposing);
    }

    private void HookTheme()
    {
        Theme.Theme.Changed += HandleThemeChanged;
        _themeHooked = true;
    }

    private void HandleThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            OnThemeChanged();
        }
    }
}
