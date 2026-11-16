using MediaVerifier.Core;

namespace MediaVerifier.App;

internal sealed class MainForm : Form
{
    public MainForm()
    {
        Text = AppInfo.WindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 800);
    }
}
