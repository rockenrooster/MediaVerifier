using MediaVerifier.UI;

namespace MediaVerifier.App;

/// <summary>
/// The notification-area icon (task F-12), present while the tray option is on. Menu: Open, Scan now
/// (disabled until scanning exists), Exit. Double-click opens the window.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _scanNow;
    private readonly Font _boldFont;

    public TrayIcon(Icon icon)
    {
        _menu = new ContextMenuStrip();
        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        ToolStripMenuItem open = new(Strings.Tray.Open) { Font = _boldFont };
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _scanNow = new ToolStripMenuItem(Strings.Tray.ScanNow) { Enabled = false };
        _scanNow.Click += (_, _) => ScanRequested?.Invoke(this, EventArgs.Empty);
        ToolStripMenuItem exit = new(Strings.Tray.Exit);
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.AddRange([open, _scanNow, new ToolStripSeparator(), exit]);
        _menu.Opening += (_, _) => _scanNow.Enabled = CanScan?.Invoke() ?? false;

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = Strings.ProductName,
            ContextMenuStrip = _menu,
            Visible = false,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke(this, EventArgs.Empty);
        _icon.BalloonTipClosed += (_, _) => BalloonClosed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ScanRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? BalloonClicked;

    public event EventHandler? BalloonClosed;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public bool ScanNowEnabled
    {
        get => _scanNow.Enabled;
        set => _scanNow.Enabled = value;
    }

    /// <summary>Asked each time the menu opens: whether Scan now can start a scan.</summary>
    public Func<bool>? CanScan { get; set; }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _icon.Visible = true;
        _icon.ShowBalloonTip(10_000, title, text, icon);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _boldFont.Dispose();
    }
}
