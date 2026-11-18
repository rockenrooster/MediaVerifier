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
    private int _reloadGeneration;

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

    /// <summary>
    /// Reloads the page's data off the UI thread, then applies it on the UI thread. Only the newest reload is applied:
    /// loads run side by side on the database's reader pool, and an older, slower one (the folder before the one just
    /// chosen, the filter before the last click) overwrote the newer one. A failure the load does not handle reaches
    /// the app's error handler instead of being lost in an unobserved task.
    /// </summary>
    public void Reload()
    {
        Loading = ReloadAsync();
        UiTask.Run(Loading);
    }

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
        int generation = ++_reloadGeneration;
        object? data;
        try
        {
            data = await Task.Run(LoadData).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            State.Log.Warning($"Could not load {Id}", ex);
            return;
        }

        if (generation != _reloadGeneration)
        {
            // A newer reload started while this one ran: its result is the one to show.
            return;
        }

        if (data is not null && !IsDisposed)
        {
            Diagnostics.UiWatchdog.Mark("Apply " + Id);
            ApplyData(data);
            if (Parent is Shell.PageViewport viewport)
            {
                viewport.LayoutPage();
            }

            PerformLayout();
            Invalidate(true);
        }
    }

    /// <summary>
    /// A load that failed for a reason the page survives (the database busy or closing, a disk error), also when it
    /// comes wrapped from parallel queries.
    /// </summary>
    private static bool IsLoadFailure(Exception exception) => exception switch
    {
        InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsLoadFailure),
        _ => false,
    };

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
        Diagnostics.UiWatchdog.Mark("Show " + Id);
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
