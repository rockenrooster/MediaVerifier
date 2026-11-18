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

    /// <summary>Off-screen (render harness, tests) the window opens without taking the focus from the user's window.</summary>
    protected override bool ShowWithoutActivation => Diagnostics.ScreenRenderer.Offscreen;

    /// <summary>
    /// Off-screen the window is also never activated (WS_EX_NOACTIVATE): WinForms re-activates a dialog's owner when the
    /// dialog closes, which took the focus from the user's window in the tests.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            if (Diagnostics.ScreenRenderer.Offscreen)
            {
                parameters.ExStyle |= 0x08000000;
            }

            return parameters;
        }
    }

    private readonly AppState _state;
    private readonly LaunchOptions _options;
    private readonly IpcServer? _ipc;
    private readonly NavigationRail _rail = new();
    private readonly ShellHeader _header = new();
    private readonly PageViewport _viewport = new();
    private readonly ScanStatusStrip _statusStrip = new();
    private readonly ShellLayout _shell;
    private readonly Dictionary<PageId, PageBase> _pages = [];
    private readonly Func<UiServices, PageId, PageBase> _pageFactory;
    private readonly UiServices _services;
    private readonly UI.Kit.Popups.ToastHost _toasts;
    private readonly Action<UI.Kit.Popups.ToastContent> _shellToast;
    private readonly System.Collections.Concurrent.ConcurrentQueue<IpcMessage> _ipcMessages = new();
    private ShellBinder? _binder;
    private PageBase? _current;
    private TrayIcon? _tray;
    private bool _startHidden;
    private bool _exiting;
    private bool _firstPaintLogged;
    private FormWindowState _restoreState = FormWindowState.Normal;

    public MainForm(UiServices services, LaunchOptions? options = null, IpcServer? ipc = null, Func<UiServices, PageId, PageBase>? pageFactory = null)
    {
        _services = services;
        services.Navigate = (page, request) =>
        {
            Navigate(page);
            if (request is not null)
            {
                _current?.HandleRequest(request);
            }
        };
        services.ShowDrawer = content => SetDrawerContent(content);
        _toasts = new UI.Kit.Popups.ToastHost(this, () => _shell is { StatusStripVisible: true } ? 36 : 0);

        // Every toast passes through the services, which keep the newest Undo for Ctrl+Z.
        services.ShowToast = content => _toasts.Show(services.TrackUndo(content));
        services.RefreshAll = RefreshAll;
        _shellToast = content => services.ShowToast?.Invoke(content);
        UI.ShellActions.Toast = _shellToast;
        AppState state = services.State;
        _state = state;
        _options = options ?? new LaunchOptions();
        _ipc = ipc;
        if (_ipc is not null)
        {
            // Before anything can create the handle: the pipe starts listening as soon as the handle exists.
            _ipc.MessageReceived += OnIpcMessage;
        }

        _pageFactory = pageFactory ?? PageFactory.Create;

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = AppInfo.WindowTitle;
        BackColor = ThemeService.Tokens.Bg;
        Icon = AppIcon.Load();
        _shell = new ShellLayout(_rail, _header, _viewport, _statusStrip) { Dock = DockStyle.Fill };
        Controls.Add(_shell);
        _rail.ItemInvoked += OnRailItemInvoked;
        ThemeService.Changed += OnThemeChanged;
        RestorePlacement();
        ResumeLayout(performLayout: false);
        ConfigureTray();
        Navigate(PageInfo.TryParse(_state.Settings.WindowPlacement.LastPage, out PageId last) ? last : PageId.Dashboard);
        _binder = new ShellBinder(this, services);
    }

    /// <summary>Puts <paramref name="content"/> into the drawer and opens it; null closes the drawer.</summary>
    internal void SetDrawerContent(Control? content)
    {
        UI.Kit.Drawer drawer = _shell.Drawer;
        if (content is null)
        {
            if (_services.IsDemo)
            {
                drawer.CloseInstantly();
            }
            else
            {
                drawer.Close();
            }

            return;
        }

        if (!drawer.Controls.Contains(content))
        {
            foreach (Control old in drawer.Controls.Cast<Control>().ToArray())
            {
                drawer.Controls.Remove(old);
            }

            drawer.Controls.Add(content);
        }

        content.Bounds = new Rectangle(1, 1, drawer.Width - 1, drawer.Height - 1);
        content.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
        if (!drawer.IsOpen)
        {
            drawer.Visible = true;
            if (_services.IsDemo)
            {
                drawer.OpenInstantly();
            }
            else
            {
                drawer.Open();
            }
        }

        _shell.PerformLayout();
    }

    /// <summary>Reads the scan status now (the render harness).</summary>
    internal void PollScan() => _binder?.PollNow();

    /// <summary>Reloads the shell's badges and the current page (after scans and repairs).</summary>
    internal void RefreshAll()
    {
        _binder?.Refresh();
        _current?.Reload();
    }

    public PageId CurrentPage => _current?.Id ?? PageId.Dashboard;

    /// <summary>The page shown now (the render harness drives demo states through it).</summary>
    internal PageBase? CurrentPageControl => _current;

    /// <summary>The current page's data load (the render harness waits for it).</summary>
    internal Task CurrentPageLoading => _current?.Loading ?? Task.CompletedTask;

    internal NavigationRail Rail => _rail;

    /// <summary>The shell's bindings (the tests drive the Scan menu through it).</summary>
    internal ShellBinder? Binder => _binder;

    internal ShellHeader Header => _header;

    internal PageViewport Viewport => _viewport;

    internal ShellLayout Shell => _shell;

    internal ScanStatusStrip StatusStrip => _statusStrip;

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
        _binder?.PollNow();
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
        if (_startHidden && value)
        {
            // Started with --minimized and the tray on: the window exists (IPC and the tray need its handle) but stays
            // hidden until the user opens it. The handle already exists by now (the constructor's layout creates it),
            // so this must not wait for the handle to be missing: the window showed at every Windows start.
            _startHidden = false;
            if (!IsHandleCreated)
            {
                CreateHandle();
            }

            value = false;
        }

        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
        UpdateMinimumSize();
        if (_options.ActivationUri is string link)
        {
            // Started by a notification or a link: open its view once the window exists.
            BeginInvoke(() => OpenActivation(link));
        }

        // Later launches reach this window from now on: the handler is subscribed, and what it queues runs once the
        // message loop does. (The pipe listened from the start of Main, before anything handled it, so a link or a
        // forwarded scheduled scan that came in while the window was starting was dropped.)
        _ipc?.Start();
        PostIpcDrain();
        UI.UiTask.Run(StartupUpdateCheckAsync());
    }

    /// <summary>
    /// After Windows Forms has set the window's icons: the title bar gets the small image from MediaVerifier.ico
    /// instead of the 32 px icon scaled down.
    /// </summary>
    protected override void CreateHandle()
    {
        base.CreateHandle();
        ApplySmallIcon();
    }

    /// <summary>
    /// Opens the view an activation link names (subplan 13, Activation URIs; task N-02). Links are untrusted: only
    /// the three forms <see cref="ActivationUri.Parse"/> accepts select a view; anything else is logged and ignored.
    /// </summary>
    internal void OpenActivation(string? text)
    {
        ActivationUri? link = ActivationUri.Parse(text);
        if (link is null)
        {
            _state.Log.Warning("Activation link rejected (" + (text?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters)");
            return;
        }

        switch (link.Target)
        {
            case ActivationTarget.Run:
                _services.Navigate?.Invoke(PageId.Scans, new ScanRunRequest(link.Id));
                break;
            case ActivationTarget.Finding:
                _services.Navigate?.Invoke(PageId.Issues, new IssueFindingRequest(link.Id));
                break;
            default:
                _services.Navigate?.Invoke(PageId.Issues, new IssuesFilterRequest(null, null));
                break;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplySmallIcon();
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

        // The rest of the keyboard map (subplan 09, Keyboard); list keys are handled by the lists.
        switch (keyData)
        {
            case Keys.Control | Keys.F:
            case Keys.OemQuestion when ActiveControl is not (UI.Kit.KitTextBox or TextBox) && !IsTextInput(FromChildHandle(msg.HWnd)):
                (_current?.FilterBox ?? _header.Search).Focus();
                return true;
            case Keys.F5:
                // Refresh the view: the page and the shell's badges.
                RefreshAll();
                return true;
            case Keys.Control | Keys.N:
                Navigate(PageId.Library);
                _current?.HandleRequest(new AddFolderRequest(null));
                return true;
            case Keys.Control | Keys.Shift | Keys.S:
                _binder?.ScanAtDefaultDepth();
                return true;
            case Keys.F1:
                UI.ShellActions.OpenUrl(UI.ShellActions.HelpUrl(_current?.HelpRule));
                return true;
            case Keys.Escape when _shell.Drawer.IsOpen && !IsTextInput(FromChildHandle(msg.HWnd)):
                SetDrawerContent(null);
                return true;
            case Keys.Control | Keys.Z when !IsTextInput(FromChildHandle(msg.HWnd)):
                // The newest toast's Undo (an Ignore, a repair of this session). It undid the newest repair in the
                // journal, however old, even right after an Ignore, and said nothing when there was none.
                if (!_services.UndoLast())
                {
                    _services.ShowToast?.Invoke(new UI.Kit.Popups.ToastContent(Core.Model.Severity.None, UI.Strings.Shell.NothingToUndo, string.Empty));
                }

                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool IsTextInput(Control? control) => control is TextBox or UI.Kit.KitTextBox;

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

            if (ReferenceEquals(UI.ShellActions.Toast, _shellToast))
            {
                UI.ShellActions.Toast = null;
            }

            _services.RefreshAll = null;

            _tray?.Dispose();
            _binder?.Dispose();
            _toasts.Dispose();
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
            page = _pageFactory(_services, id);
            page.Visible = false;
            _pages[id] = page;
        }

        return page;
    }

    private void OnRailItemInvoked(object? sender, RailItem item)
    {
        Navigate(item.Page);
        if (item.Kind == RailItemKind.Update)
        {
            _current?.HandleRequest(new SettingsSectionRequest(SettingsPage.UpdatesSection));
        }
    }

    /// <summary>Shows or hides the rail's Update item after a check (subplan 14, Where it shows); the shell's reload decides.</summary>
    internal void RefreshUpdateIndicator() => _binder?.Refresh();

    /// <summary>Closes for good after the installer started (even with Keep running in the notification area on).</summary>
    internal void ExitForUpdate() => ExitApplication();

    /// <summary>
    /// Task U-09: one check per window launch, about 10 seconds after the window is shown, when Check for updates
    /// when MediaVerifier starts is on. Headless runs never get here.
    /// </summary>
    private async Task StartupUpdateCheckAsync()
    {
        if (_services.IsDemo || _services.Updates is not Core.Updates.UpdateService updates || !_state.Settings.CheckForUpdatesOnStartup)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        if (IsDisposed || updates.LastResult is not null || updates.IsChecking)
        {
            return;
        }

        Core.Updates.UpdateCheckResult result = await updates.CheckAsync().ConfigureAwait(true);
        _state.Log.Info("Update check at start-up: " + result.Message);
        if (!IsDisposed)
        {
            RefreshUpdateIndicator();
        }
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

    /// <summary>
    /// The pipe's thread: the message waits in a queue that the UI thread drains, so one that arrives while the handle
    /// is being created or recreated is handled when it exists instead of being dropped.
    /// </summary>
    private void OnIpcMessage(object? sender, IpcMessage message)
    {
        _ipcMessages.Enqueue(message);
        PostIpcDrain();
    }

    private void PostIpcDrain()
    {
        if (IsDisposed || !IsHandleCreated || _ipcMessages.IsEmpty)
        {
            // Drained when the handle exists (OnHandleCreated).
            return;
        }

        try
        {
            BeginInvoke(DrainIpc);
        }
        catch (InvalidOperationException)
        {
            // The window is closing, or its handle is being recreated: OnHandleCreated drains the queue.
        }
    }

    private void DrainIpc()
    {
        while (!IsDisposed && _ipcMessages.TryDequeue(out IpcMessage? message))
        {
            HandleIpc(message);
        }
    }

    private void HandleIpc(IpcMessage message)
    {
        switch (message.Command)
        {
            case IpcMessage.Activate:
                BringToFrontFromAnywhere();
                break;
            case IpcMessage.Open:
                BringToFrontFromAnywhere();
                OpenActivation(message.Uri);
                break;
            case IpcMessage.RunPlan when message.PlanId is long planId:
                _state.Log.Info($"Plan {planId} was forwarded by a scheduled launch");
                UI.UiTask.Run(GuiPlanRunner.RunAsync(_services, planId, () => !IsDisposed && ActiveForm == this, ShowRunNotification));
                break;
        }
    }

    /// <summary>A forwarded plan's notification as an in-app toast (called from a pool thread).</summary>
    private void ShowRunNotification(Core.Scheduling.RunNotification notification)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() => _toasts.Show(new UI.Kit.Popups.ToastContent(notification.Level, notification.Title, notification.Body)));
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    private void ConfigureTray()
    {
        bool trayOn = UpdateTray();
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

    /// <summary>
    /// Shows the tray icon while Keep running or Start with Windows is on, and removes it when both are off (Settings
    /// calls this when either changes, so closing the window behaves at once as the setting says). True when shown.
    /// </summary>
    internal bool UpdateTray()
    {
        bool trayOn = _state.Settings.MinimizeToTray || _state.Settings.StartWithWindows;
        if (!trayOn)
        {
            _tray?.Dispose();
            _tray = null;
            return false;
        }

        if (_tray is null)
        {
            _tray = new TrayIcon(AppIcon.Small(SystemInformation.SmallIconSize.Width)) { Visible = true };
            _tray.OpenRequested += (_, _) => BringToFrontFromAnywhere();
            _tray.CannotScanReason = () => _services.IsDemo ? string.Empty : _services.CannotScanReason(true);
            _tray.ScanRequested += (_, _) =>
            {
                BringToFrontFromAnywhere();
                _binder?.ScanAtDefaultDepth();
            };
            _tray.ExitRequested += (_, _) => ExitApplication();
        }

        return true;
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

    private void ApplySmallIcon()
    {
        if (IsHandleCreated && ShowIcon)
        {
            _ = SendMessageW(Handle, WmSetIcon, IconSmall, AppIcon.Small(Dpi.Scale(16, DeviceDpi)).Handle);
        }
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int WmSetIcon = 0x0080;
    private const nint IconSmall = 0;

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, int message, nint wParam, nint lParam);

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

/// <summary>Creates the page object for a page id.</summary>
internal static class PageFactory
{
    public static PageBase Create(UiServices services, PageId id) => id switch
    {
        PageId.Dashboard => new DashboardPage(services),
        PageId.Issues => new IssuesPage(services),
        PageId.Library => new LibraryPage(services),
        PageId.Scans => new ScansPage(services),
        PageId.Settings => new SettingsPage(services),
        PageId.Tools => new ToolsPage(services),
        PageId.Schedule => new SchedulePage(services),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };
}

/// <summary>The app icon from the executable's resources (task F-09).</summary>
internal static class AppIcon
{
    private static readonly Dictionary<int, Icon> SmallIcons = [];
    private static Icon? s_icon;

    /// <summary>The 32 px icon: the window's large icon (Alt+Tab, the taskbar).</summary>
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

    /// <summary>
    /// The image of MediaVerifier.ico nearest <paramref name="size"/> pixels (16, 20, 24 for the title bar and the
    /// notification area), so they show the small design of plan 08 instead of the 32 px icon scaled down. Kept for the
    /// life of the process, one per size.
    /// </summary>
    public static Icon Small(int size)
    {
        lock (SmallIcons)
        {
            if (SmallIcons.TryGetValue(size, out Icon? icon))
            {
                return icon;
            }

            try
            {
                string? path = Environment.ProcessPath;
                icon = (path is not null ? Icon.ExtractIcon(path, 0, size) : null) ?? Load();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                icon = Load();
            }

            SmallIcons[size] = icon;
            return icon;
        }
    }
}
