using MediaVerifier.App;
using MediaVerifier.UI.Kit;

namespace MediaVerifier.UI.Pages;

internal enum PageId
{
    Dashboard,
    Issues,
    Library,
    Scans,
    Schedule,
    Tools,
    Settings,
}

/// <summary>
/// One page of the shell (ported from FastTaskMgr's PageBase). Pages are created lazily on first
/// visit, told when they are shown and hidden, and must stop every timer while hidden so the idle app
/// uses no CPU.
/// </summary>
internal abstract class PageBase : KitControl
{
    private readonly List<System.Windows.Forms.Timer> _timers = [];

    protected PageBase(UiServices services)
    {
        Services = services;
        State = services.State;
        TabStop = false;
    }

    protected UiServices Services { get; }

    protected AppState State { get; }

    /// <summary>The current load, so the render harness can wait for a page's data.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Reloads the page's data off the UI thread, then applies it on the UI thread.</summary>
    public void Reload() => Loading = ReloadAsync();

    /// <summary>The page's own filter box, focused by Ctrl+F or / (subplan 09, Keyboard); null uses the header search.</summary>
    public virtual Control? FilterBox => null;

    /// <summary>The rule F1 opens help for (the focused finding's), or null for the page's help.</summary>
    public virtual string? HelpRule => null;

    /// <summary>A request from another page (a filter to apply, a dialog to open).</summary>
    public virtual void HandleRequest(object request)
    {
    }

    /// <summary>Loads the snapshot (runs on a pool thread); return null to keep what is shown.</summary>
    protected virtual object? LoadData() => null;

    /// <summary>Applies a loaded snapshot on the UI thread.</summary>
    protected virtual void ApplyData(object data)
    {
    }

    private async Task ReloadAsync()
    {
        object? data;
        try
        {
            data = await Task.Run(LoadData).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            State.Log.Warning($"Could not load {Id}", ex);
            return;
        }

        if (data is not null && !IsDisposed)
        {
            ApplyData(data);
            if (Parent is Shell.PageViewport viewport)
            {
                viewport.LayoutPage();
            }

            PerformLayout();
            Invalidate(true);
        }
    }

    public abstract PageId Id { get; }

    public string Title => PageInfo.Title(Id);

    /// <summary>True while this is the page shown in the main window.</summary>
    public bool IsActive { get; private set; }

    /// <summary>True while the window is minimised or hidden in the tray.</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>
    /// Height the page needs at <paramref name="width"/> device pixels; 0 means "fill the content
    /// area". A taller page scrolls (subplan 08, Scrolling).
    /// </summary>
    public virtual int MeasureContentHeight(int width) => 0;

    public void Activate()
    {
        IsActive = true;
        UpdateTimers();
        OnActivated();
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdateTimers();
        OnDeactivated();
    }

    public void SetSuspended(bool suspended)
    {
        if (IsSuspended == suspended)
        {
            return;
        }

        IsSuspended = suspended;
        UpdateTimers();
    }

    protected virtual void OnActivated()
    {
    }

    protected virtual void OnDeactivated()
    {
    }

    /// <summary>A timer that runs only while the page is visible and the window is not suspended.</summary>
    protected System.Windows.Forms.Timer CreatePageTimer(int intervalMs, EventHandler tick)
    {
        System.Windows.Forms.Timer timer = new() { Interval = intervalMs };
        timer.Tick += tick;
        _timers.Add(timer);
        UpdateTimers();
        return timer;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Tokens.Bg);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (System.Windows.Forms.Timer timer in _timers)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }

        base.Dispose(disposing);
    }

    private void UpdateTimers()
    {
        bool run = IsActive && !IsSuspended;
        foreach (System.Windows.Forms.Timer timer in _timers)
        {
            timer.Enabled = run;
        }
    }
}

internal static class PageInfo
{
    public static IReadOnlyList<PageId> All { get; } = Enum.GetValues<PageId>();

    public static string Title(PageId id) => id switch
    {
        PageId.Dashboard => Strings.Pages.Dashboard,
        PageId.Issues => Strings.Pages.Issues,
        PageId.Library => Strings.Pages.Library,
        PageId.Scans => Strings.Pages.Scans,
        PageId.Schedule => Strings.Pages.Schedule,
        PageId.Tools => Strings.Pages.Tools,
        _ => Strings.Pages.Settings,
    };

    public static IconName Icon(PageId id) => id switch
    {
        PageId.Dashboard => IconName.Dashboard,
        PageId.Issues => IconName.Issues,
        PageId.Library => IconName.Library,
        PageId.Scans => IconName.Scans,
        PageId.Schedule => IconName.Schedule,
        PageId.Tools => IconName.Tools,
        _ => IconName.Settings,
    };

    public static bool TryParse(string? text, out PageId page) =>
        Enum.TryParse(text, ignoreCase: true, out page) && Enum.IsDefined(page);
}

/// <summary>A page with nothing on it yet (phase 1 placeholders).</summary>
internal sealed class PlaceholderPage : PageBase
{
    private readonly PageId _id;

    public PlaceholderPage(UiServices services, PageId id)
        : base(services)
    {
        _id = id;
    }

    public override PageId Id => _id;
}
