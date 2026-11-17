using MediaVerifier.UI.Theme;
using MediaVerifier.UI.Kit;

namespace MediaVerifier.UI.Shell;

/// <summary>
/// The window's client area: rail on the left, header on top, page viewport, and the status strip at
/// the bottom while a scan runs (subplan 08, Window and shell). Lays its parts out from logical pixels.
/// </summary>
internal sealed class ShellLayout : Control
{
    public ShellLayout(NavigationRail rail, ShellHeader header, PageViewport viewport, Control? statusStrip = null)
    {
        Rail = rail;
        Header = header;
        Viewport = viewport;
        StatusStrip = statusStrip;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        TabStop = false;
        Controls.Add(viewport);
        Controls.Add(header);
        Controls.Add(rail);
        if (statusStrip is not null)
        {
            statusStrip.Visible = false;
            Controls.Add(statusStrip);
        }

        rail.TabIndex = 0;
        header.TabIndex = 1;
        viewport.TabIndex = 2;
        Drawer = new Drawer { Visible = false, TabIndex = 3 };
        Drawer.ProgressChanged += (_, _) =>
        {
            Drawer.Visible = Drawer.Progress > 0;
            PerformLayout();
        };
        Drawer.WidthChanged += (_, _) => PerformLayout();
        Controls.Add(Drawer);
        Drawer.BringToFront();
    }

    public NavigationRail Rail { get; }

    public ShellHeader Header { get; }

    public PageViewport Viewport { get; }

    public Control? StatusStrip { get; }

    /// <summary>The detail drawer, right of the content, below the header and above the status strip.</summary>
    public Drawer Drawer { get; }

    public bool StatusStripVisible
    {
        get => StatusStrip?.Visible == true;
        set
        {
            if (StatusStrip is not null && StatusStrip.Visible != value)
            {
                StatusStrip.Visible = value;
                Viewport.StripVisible = value;
                PerformLayout();
            }
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Size client = ClientSize;
        if (client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        int dpi = DeviceDpi;
        Rail.Collapsed = client.Width < Dpi.Scale(NavigationRail.CollapseBelowWindowWidth, dpi);
        if (StatusStrip is ScanStatusStrip strip)
        {
            strip.Narrow = Rail.Collapsed;
        }

        int railWidth = Dpi.Scale(Rail.LogicalWidth, dpi);
        int headerHeight = Dpi.Scale(56, dpi);
        int stripHeight = StatusStripVisible ? Dpi.Scale(36, dpi) : 0;
        Rail.Bounds = new Rectangle(0, 0, railWidth, client.Height);
        Header.Bounds = new Rectangle(railWidth, 0, client.Width - railWidth, headerHeight);
        int drawerWidth = Drawer.Progress > 0 ? (int)Math.Round(Dpi.Scale(Drawer.LogicalWidth, dpi) * Drawer.Progress) : 0;
        Viewport.DrawerOpen = drawerWidth > 0;
        Viewport.Bounds = new Rectangle(railWidth, headerHeight, client.Width - railWidth - drawerWidth, client.Height - headerHeight - stripHeight);
        if (drawerWidth > 0)
        {
            Drawer.Bounds = new Rectangle(client.Width - drawerWidth, headerHeight, Dpi.Scale(Drawer.LogicalWidth, dpi), client.Height - headerHeight - stripHeight);
        }

        if (StatusStrip is not null)
        {
            StatusStrip.Bounds = new Rectangle(railWidth, client.Height - stripHeight, client.Width - railWidth, stripHeight);
        }

        Viewport.LayoutPage();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(ThemeService.Tokens.Bg);
    }
}
