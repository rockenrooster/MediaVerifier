using System.Runtime.InteropServices;
using MediaVerifier.Core;
using MediaVerifier.Core.Ipc;
using MediaVerifier.Core.Settings;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;
using MediaVerifier.UI.Shell;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.App;

/// <summary>
/// The shell window (task F-08): navigation rail, header, page viewport and status strip, with pages
/// created on first visit. Restores its placement, follows the theme in its title bar, and receives
/// commands from later launches over IPC.
/// </summary>
internal sealed class MainForm : Form
{
    public static readonly Size DefaultClientSize = new(1280, 800);
    public static readonly Size MinimumClientSize = new(960, 600);

    private readonly AppState _state;
    private readonly LaunchOptions _options;
    private readonly IpcServer? _ipc;
    private readonly NavigationRail _rail = new();
    private readonly ShellHeader _header = new();
    private readonly PageViewport _viewport = new();
    private readonly ShellLayout _shell;
    private readonly Dictionary<PageId, PageBase> _pages = [];
    private readonly Func<AppState, PageId, PageBase> _pageFactory;
    private PageBase? _current;
    private TrayIcon? _tray;
    private bool _startHidden;
    private bool _exiting;
    private bool _firstPaintLogged;
    private FormWindowState _restoreState = FormWindowState.Normal;

    public MainForm(AppState state, LaunchOptions? options = null, IpcServer? ipc = null, Func<AppState, PageId, PageBase>? pageFactory = null)
    {
        _state = state;
        _options = options ?? new LaunchOptions();
        _ipc = ipc;
        _pageFactory = pageFactory ?? PageFactory.Create;

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = AppInfo.WindowTitle;
        KeyPreview = true;
        BackColor = ThemeService.Tokens.Bg;
        Icon = AppIcon.Load();
        _shell = new ShellLayout(_rail, _header, _viewport) { Dock = DockStyle.Fill };
        Controls.Add(_shell);
        _rail.ItemInvoked += OnRailItemInvoked;
        ThemeService.Changed += OnThemeChanged;
        RestorePlacement();
        ResumeLayout(performLayout: false);

        if (_ipc is not null)
        {
            _ipc.MessageReceived += OnIpcMessage;
        }

        ConfigureTray();
        Navigate(PageInfo.TryParse(_state.Settings.WindowPlacement.LastPage, out PageId last) ? last : PageId.Dashboard);
    }

    public PageId CurrentPage => _current?.Id ?? PageId.Dashboard;

    internal NavigationRail Rail => _rail;

    internal ShellHeader Header => _header;

    internal PageViewport Viewport => _viewport;

    internal ShellLayout Shell => _shell;

    public void Navigate(PageId id)
    {
        PageBase page = GetOrCreatePage(id);
        if (ReferenceEquals(page, _current))
        {
            return;
        }

        _current?.Deactivate();
        _viewport.Show(page);
        _current = page;
        page.SetSuspended(WindowState == FormWindowState.Minimized || !Visible);
        page.Activate();
        _rail.SelectedPage = id;
        _header.PageTitle = page.Title;
        _state.Settings.WindowPlacement.LastPage = id.ToString();
    }

    /// <summary>Shows the window from the tray or the taskbar and brings it to the front.</summary>
    public void BringToFrontFromAnywhere()
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = _restoreState;
        }

        Activate();
        _ = SetForegroundWindow(Handle);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && value && !IsHandleCreated)
        {
            // Started with --minimized and the tray on: create the window (IPC and the tray need it)
            // but keep it hidden until the user opens it.
            _startHidden = false;
            CreateHandle();
            value = false;
        }

        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
        UpdateMinimumSize();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateMinimumSize();
        _shell.PerformLayout();
        Invalidate(invalidateChildren: true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized)
        {
            _restoreState = WindowState;
        }

        bool suspended = WindowState == FormWindowState.Minimized || !Visible;
        _current?.SetSuspended(suspended);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _current?.SetSuspended(WindowState == FormWindowState.Minimized || !Visible);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_firstPaintLogged)
        {
            _firstPaintLogged = true;
            _state.Log.Info($"First paint {Program.MillisecondsSinceProcessStart()} ms after process start");
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(ThemeService.Tokens.Bg);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        PageId? target = keyData switch
        {
            Keys.Control | Keys.D1 => PageId.Dashboard,
            Keys.Control | Keys.D2 => PageId.Issues,
            Keys.Control | Keys.D3 => PageId.Library,
            Keys.Control | Keys.D4 => PageId.Scans,
            Keys.Control | Keys.D5 => PageId.Schedule,
            Keys.Control | Keys.D6 => PageId.Tools,
            Keys.Control | Keys.D7 => PageId.Settings,
            Keys.Control | Keys.Oemcomma => PageId.Settings,
            _ => null,
        };
        if (target is PageId page)
        {
            Navigate(page);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _state.Settings.MinimizeToTray && _tray is not null)
        {
            // "Keep running in the notification area when closed".
            e.Cancel = true;
            SavePlacement();
            Hide();
            return;
        }

        SavePlacement();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeService.Changed -= OnThemeChanged;
            if (_ipc is not null)
            {
                _ipc.MessageReceived -= OnIpcMessage;
            }

            _tray?.Dispose();
            foreach (PageBase page in _pages.Values)
            {
                page.Dispose();
            }

            _pages.Clear();
        }

        base.Dispose(disposing);
    }

    private PageBase GetOrCreatePage(PageId id)
    {
        if (!_pages.TryGetValue(id, out PageBase? page))
        {
            page = _pageFactory(_state, id);
            page.Visible = false;
            _pages[id] = page;
        }

        return page;
    }

    private void OnRailItemInvoked(object? sender, RailItem item)
    {
        Navigate(item.Page);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        BackColor = ThemeService.Tokens.Bg;
        if (IsHandleCreated)
        {
            ApplyTitleBarTheme();
        }

        Invalidate(invalidateChildren: true);
    }

    private void OnIpcMessage(object? sender, IpcMessage message)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() => HandleIpc(message));
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    private void HandleIpc(IpcMessage message)
    {
        switch (message.Command)
        {
            case IpcMessage.Activate:
            case IpcMessage.Open:
                BringToFrontFromAnywhere();
                break;
            case IpcMessage.RunPlan:
                _state.Log.Info($"Plan {message.PlanId} was forwarded by a scheduled launch");
                break;
        }
    }

    private void ConfigureTray()
    {
        bool trayOn = _state.Settings.MinimizeToTray || _state.Settings.StartWithWindows;
        if (trayOn)
        {
            _tray = new TrayIcon(Icon ?? SystemIcons.Application) { Visible = true };
            _tray.OpenRequested += (_, _) => BringToFrontFromAnywhere();
            _tray.ExitRequested += (_, _) => ExitApplication();
        }

        if (_options.Minimized)
        {
            if (trayOn)
            {
                _startHidden = true;
            }
            else
            {
                WindowState = FormWindowState.Minimized;
            }
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private void RestorePlacement()
    {
        int dpi = DeviceDpi;
        WindowPlacement placement = _state.Settings.WindowPlacement;
        Size defaultClient = Dpi.Scale(DefaultClientSize, dpi);
        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        if (placement.HasBounds)
        {
            Rectangle saved = new(placement.Left, placement.Top, placement.Width, placement.Height);
            if (WindowPlacementRules.IsReachable(saved, Screen.AllScreens.Select(s => s.WorkingArea)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = saved;
            }
            else
            {
                // The monitor it was on is gone: fall back to the primary screen.
                StartPosition = FormStartPosition.Manual;
                ClientSize = WindowPlacementRules.Fit(defaultClient, work.Size);
                Location = WindowPlacementRules.CentreIn(Size, work);
            }

            if (placement.Maximized)
            {
                WindowState = FormWindowState.Maximized;
                _restoreState = FormWindowState.Maximized;
            }
        }
        else
        {
            StartPosition = FormStartPosition.Manual;
            ClientSize = WindowPlacementRules.Fit(defaultClient, work.Size);
            Location = WindowPlacementRules.CentreIn(Size, work);
        }
    }

    private void SavePlacement()
    {
        WindowPlacement placement = _state.Settings.WindowPlacement;
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            placement.Left = bounds.Left;
            placement.Top = bounds.Top;
            placement.Width = bounds.Width;
            placement.Height = bounds.Height;
        }

        placement.Maximized = WindowState == FormWindowState.Maximized
            || (WindowState == FormWindowState.Minimized && _restoreState == FormWindowState.Maximized);
        placement.LastPage = CurrentPage.ToString();
        _ = _state.SaveSettings();
    }

    private void UpdateMinimumSize()
    {
        if (IsHandleCreated)
        {
            MinimumSize = SizeFromClientSize(Dpi.Scale(MinimumClientSize, DeviceDpi));
        }
    }

    private void ApplyTitleBarTheme()
    {
        int dark = ThemeService.Tokens.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

/// <summary>Pure placement arithmetic, testable without a window.</summary>
internal static class WindowPlacementRules
{
    /// <summary>
    /// True when enough of the window (100 x 32 px of its top edge, where the title bar is) lies on
    /// some screen's working area for the user to grab it.
    /// </summary>
    public static bool IsReachable(Rectangle bounds, IEnumerable<Rectangle> workingAreas)
    {
        Rectangle titleStrip = new(bounds.Left, bounds.Top, bounds.Width, Math.Min(32, bounds.Height));
        foreach (Rectangle area in workingAreas)
        {
            Rectangle overlap = Rectangle.Intersect(titleStrip, area);
            if (overlap.Width >= 100 && overlap.Height >= Math.Min(16, titleStrip.Height))
            {
                return true;
            }
        }

        return false;
    }

    public static Size Fit(Size wanted, Size available) =>
        new(Math.Min(wanted.Width, available.Width), Math.Min(wanted.Height, available.Height));

    public static Point CentreIn(Size size, Rectangle area) =>
        new(area.Left + Math.Max(0, (area.Width - size.Width) / 2), area.Top + Math.Max(0, (area.Height - size.Height) / 2));
}

/// <summary>Creates the page object for a page id. Real pages replace the placeholders phase by phase.</summary>
internal static class PageFactory
{
    public static PageBase Create(AppState state, PageId id) => new PlaceholderPage(state, id);
}

/// <summary>The app icon from the executable's resources (task F-09).</summary>
internal static class AppIcon
{
    private static Icon? s_icon;

    public static Icon Load()
    {
        if (s_icon is not null)
        {
            return s_icon;
        }

        try
        {
            string? path = Environment.ProcessPath;
            s_icon = (path is not null ? Icon.ExtractAssociatedIcon(path) : null) ?? SystemIcons.Application;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            s_icon = SystemIcons.Application;
        }

        return s_icon;
    }
}
