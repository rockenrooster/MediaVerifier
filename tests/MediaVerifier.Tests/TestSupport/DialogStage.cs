using MediaVerifier.UI.Kit;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Shows a dialog without blocking, over a plain window far off-screen, so a test can paint and inspect it. A dialog
/// covers its owner's client area, so the owner is as large as the main window by default.
/// </summary>
internal static class DialogStage
{
    /// <summary>Places and shows <paramref name="dialog"/>; dispose the returned owner after closing the dialog.</summary>
    public static Form Show(KitDialog dialog)
    {
        Form owner = new()
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
        };
        owner.Show();
        owner.ClientSize = Dpi.Scale(new Size(1280, 800), owner.DeviceDpi);
        owner.Location = new Point(-32000, -32000);
        dialog.PlaceOver(owner);
        dialog.Show(owner);
        Application.DoEvents();
        return owner;
    }
}
