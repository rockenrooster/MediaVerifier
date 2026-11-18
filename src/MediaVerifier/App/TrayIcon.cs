using MediaVerifier.UI;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.App;

/// <summary>
/// The notification-area icon (task F-12), present while the tray option is on. Menu: Open, Scan now (disabled, with
/// the reason as its tooltip, when a scan cannot start), Exit. Double-click opens the window. The menu takes the
/// theme's colours, so it is dark in dark mode.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _scanNow;
    private readonly Font _boldFont;

    /// <param name="icon">The small app icon; the caller keeps ownership.</param>
    public TrayIcon(Icon icon)
    {
        _menu = new ContextMenuStrip { ShowItemToolTips = true, Renderer = new ToolStripProfessionalRenderer(new ThemeColorTable()) { RoundedEdges = false } };
        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        ToolStripMenuItem open = new(Strings.Tray.Open) { Font = _boldFont };
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _scanNow = new ToolStripMenuItem(Strings.Tray.ScanNow) { Enabled = false };
        _scanNow.Click += (_, _) => ScanRequested?.Invoke(this, EventArgs.Empty);
        ToolStripMenuItem exit = new(Strings.Tray.Exit);
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.AddRange([open, _scanNow, new ToolStripSeparator(), exit]);
        _menu.Opening += (_, _) => OnOpening();

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
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ScanRequested;

    public event EventHandler? ExitRequested;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>
    /// Asked each time the menu opens: why Scan now cannot start a scan (shown as its tooltip), or null when it can. An
    /// empty reason disables it without a tooltip.
    /// </summary>
    public Func<string?>? CannotScanReason { get; set; }

    /// <summary>The menu as it opens (the tests read it).</summary>
    internal ToolStripMenuItem ScanNowItem => _scanNow;

    /// <summary>Updates Scan now and the theme colours, as when the menu opens.</summary>
    internal void OnOpening()
    {
        string? reason = CannotScanReason is null ? string.Empty : CannotScanReason();
        _scanNow.Enabled = reason is null;
        _scanNow.ToolTipText = reason ?? string.Empty;
        ThemeTokens t = ThemeService.Tokens;
        _menu.BackColor = t.Raised;
        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = item.Enabled ? t.Text : t.TextDisabled;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _boldFont.Dispose();
    }

    /// <summary>The system menu renderer's colours from the theme tokens (read at paint time, so a theme change applies).</summary>
    private sealed class ThemeColorTable : ProfessionalColorTable
    {
        private static ThemeTokens T => ThemeService.Tokens;

        public override Color ToolStripDropDownBackground => T.Raised;

        public override Color ImageMarginGradientBegin => T.Raised;

        public override Color ImageMarginGradientMiddle => T.Raised;

        public override Color ImageMarginGradientEnd => T.Raised;

        public override Color MenuBorder => T.Stroke;

        public override Color MenuItemBorder => T.StrokeStrong;

        public override Color MenuItemSelected => T.Hover;

        public override Color MenuItemSelectedGradientBegin => T.Hover;

        public override Color MenuItemSelectedGradientEnd => T.Hover;

        public override Color MenuItemPressedGradientBegin => T.Pressed;

        public override Color MenuItemPressedGradientMiddle => T.Pressed;

        public override Color MenuItemPressedGradientEnd => T.Pressed;

        public override Color SeparatorDark => T.Stroke;

        public override Color SeparatorLight => T.Stroke;
    }
}
