namespace MediaVerifier.UI.Kit;

/// <summary>Focus the app moves on its own (a dialog's first button, the first-run Add folder button).</summary>
internal static class KitFocus
{
    /// <summary>
    /// Gives <paramref name="control"/> the keyboard focus, except off-screen (the tests and the render harness): there
    /// Focus also activates the hidden window and takes the focus from the user's own window, which made the screen
    /// flicker at every test dialog.
    /// </summary>
    public static void Move(Control? control)
    {
        if (control is not null && !Diagnostics.ScreenRenderer.Offscreen)
        {
            control.Focus();
        }
    }
}
