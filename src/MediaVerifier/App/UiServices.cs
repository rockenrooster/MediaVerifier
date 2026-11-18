using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.App;

/// <summary>What the pages may do with scans: status polling and control (the engine or the harness's stand-in).</summary>
internal interface IScanControl
{
    /// <summary>Immutable snapshot; poll at up to 10 Hz (subplan 05).</summary>
    EngineStatus Status { get; }

    /// <summary>A short description of the running scan's scope ("Music and Photos").</summary>
    string ScopeText { get; }

    /// <summary>Raised on a pool thread when a run ends.</summary>
    event Action<RunSummary>? RunCompleted;

    Task<long> StartAsync(ScanRequest request, string scopeText);

    void Pause();

    void Resume();

    void Cancel();

    /// <summary>Re-judges stored analysis after a rule setting changed (no file is read again).</summary>
    Task<ReevaluationResult> ReevaluateAsync(IReadOnlyCollection<string> ruleIds);
}

/// <summary>The real engine behind <see cref="IScanControl"/>.</summary>
internal sealed class EngineScanControl(ScanEngine engine) : IScanControl, IDisposable
{
    private string _scope = string.Empty;

    public EngineStatus Status => engine.Status;

    public string ScopeText => _scope;

    /// <summary>
    /// On a pool thread: the engine's synchronous first part reads every file of the rule's kinds, which froze the
    /// window at each change in Settings → Rules (and throws in a debug build, where the UI thread may not read).
    /// </summary>
    public Task<ReevaluationResult> ReevaluateAsync(IReadOnlyCollection<string> ruleIds) => Task.Run(() => engine.ReevaluateAsync(ruleIds));

    public event Action<RunSummary>? RunCompleted
    {
        add => engine.RunCompleted += value;
        remove => engine.RunCompleted -= value;
    }

    public ScanEngine Engine => engine;

    public async Task<long> StartAsync(ScanRequest request, string scopeText)
    {
        _scope = scopeText;

        // On a pool thread: starting reads the database before its first wait (the engine lock is held on a thread of
        // its own, so any thread may take it).
        return await Task.Run(() => engine.StartAsync(request)).ConfigureAwait(false);
    }

    public void Pause() => engine.Pause();

    public void Resume() => engine.Resume();

    public void Cancel() => engine.Cancel();

    public void Dispose() => engine.Dispose();
}

/// <summary>The bundled FFmpeg as the Tools page and the scan buttons see it (subplans 04 and 17).</summary>
internal sealed record MediaEngineInfo(FfmpegLocation Location, BundleCheck Check)
{
    public bool ScanningAllowed => Check.ScanningAllowed;

    public string Version => Check.Info?.FfmpegVersion ?? FfmpegVersions.Bundled;

    public string Build => Check.Info?.ExtraVersion ?? "mediaverifier-1";

    public string Licence => Check.Info?.Licence ?? "LGPL 2.1 or later";
}

/// <summary>
/// Everything a page reads or controls: settings, the library database, the clock, scans and the media
/// engine (subplan 09). The live app opens the user's library; the render harness passes demo data.
/// </summary>
internal sealed class UiServices : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly Queue<QueuedScan> _queuedScans = new();
    private Action? _lastUndo;

    public required AppState State { get; init; }

    public required Database Database { get; init; }

    public required IClock Clock { get; init; }

    public required IScanControl Scans { get; init; }

    public required MediaEngineInfo MediaEngine { get; init; }

    /// <summary>Page snapshots (the database, or the mockups' data in the render harness).</summary>
    public required UI.Data.IUiData Data { get; init; }

    /// <summary>The rule overlay (enabled, caps, limits) as stored in the database.</summary>
    public RuleSettings Rules { get; set; } = RuleSettings.Defaults;

    /// <summary>Opens a page, optionally with a request for it (a filter, a dialog to open); set by the main window.</summary>
    public Action<UI.Pages.PageId, object?>? Navigate { get; set; }

    /// <summary>Shows content in the detail drawer (null closes it); set by the main window.</summary>
    public Action<Control?>? ShowDrawer { get; set; }

    /// <summary>Shows a toast at the bottom right of the main window (set by the main window).</summary>
    public Action<UI.Kit.Popups.ToastContent>? ShowToast { get; set; }

    /// <summary>
    /// Reloads the shell's badges and the page on show (set by the main window). Call it after a change the page did
    /// not make itself: a repair, an undo.
    /// </summary>
    public Action? RefreshAll { get; set; }

    /// <summary>Raised on the UI thread when a manual scan has started, so the status strip shows it at once.</summary>
    public event Action? ScanStarted;

    /// <summary>Tells the user why a scan they asked for did not start: a scan button never does nothing.</summary>
    public void ShowScanNotStarted(string reason) =>
        ShowToast?.Invoke(new UI.Kit.Popups.ToastContent(Core.Model.Severity.Medium, UI.Strings.Activity.CouldNotRun, reason));

    /// <summary>
    /// Remembers the Undo of a toast for Ctrl+Z (subplan 09, Keyboard: "Undo the last operation that allows it") and
    /// returns the toast to show, its Undo now running at most once, from the button or from Ctrl+Z. The newest toast
    /// with Undo wins; a repair from an earlier session is undone from Tools → Operations, never by Ctrl+Z.
    /// </summary>
    public UI.Kit.Popups.ToastContent TrackUndo(UI.Kit.Popups.ToastContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Action is not Action undo || content.ActionText != UI.Strings.Dialogs.Undo)
        {
            return content;
        }

        bool done = false;
        Action? once = null;
        once = () =>
        {
            if (done)
            {
                return;
            }

            done = true;
            if (ReferenceEquals(_lastUndo, once))
            {
                _lastUndo = null;
            }

            undo();
        };
        _lastUndo = once;
        return content with { Action = once };
    }

    /// <summary>True while a toast's Undo is waiting for Ctrl+Z.</summary>
    public bool CanUndo => _lastUndo is not null;

    /// <summary>Ctrl+Z: runs the newest toast's Undo once; false when there is nothing to undo.</summary>
    public bool UndoLast()
    {
        if (_lastUndo is not Action undo)
        {
            return false;
        }

        undo();
        return true;
    }

    /// <summary>True for the render harness: no timers start work, nothing is written.</summary>
    public bool IsDemo { get; init; }

    /// <summary>The only writer of user media (subplan 12); null in the render harness.</summary>
    public Core.Remediation.OperationRunner? Operations { get; init; }

    /// <summary>The signed updater (subplan 14); null in the render harness, which never checks.</summary>
    public Core.Updates.UpdateService? Updates { get; init; }

    /// <summary>
    /// The last check found a newer release that can be installed (the rail's Update item and the Settings dot). A
    /// release whose signed files are missing or invalid is not offered: its card says it could not be verified.
    /// </summary>
    public bool UpdateAvailable => Updates?.LastResult?.CanDownload == true;

    /// <summary>
    /// Opens the user's library, the bundled media engine and the scan engine. Tests pass the engine and an update
    /// service, and skip the start-up maintenance, which registers notifications and reconciles the Task Scheduler.
    /// </summary>
    /// <param name="engineMutexName">
    /// Tests pass their own, so a MediaVerifier that is scanning on the same machine neither blocks their scans nor
    /// counts as a scheduled scan.
    /// </param>
    public static UiServices CreateLive(AppState state, FfmpegLocation? mediaEngine = null, Core.Updates.UpdateService? updates = null, bool startupMaintenance = true, string? engineMutexName = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        FfmpegLocation location = mediaEngine ?? FfmpegLocator.Locate(state.Paths);

        // The media engine's hashes (27 MB) are checked while the database opens, instead of after it on the UI
        // thread before the window can show.
        Task<BundleCheck> bundle = Task.Run(() => CheckBundle(location, state.Log));
        Database database = Database.Open(state.Paths.ResolveDatabaseFile(state.Settings.DatabasePath));
        Task<RuleSettings> rules = Task.Run(() => LoadRules(database, state.Log));
        FfRunner runner = new(location);
        UiServices services = null!;
        ScanEngine engine = new(EngineFactory.CreateServices(database, runner, state.Paths, () => state.Settings.Scanning, () => services.Rules), engineMutexName ?? Core.Ipc.NamedMutexLock.EngineName);
        EngineScanControl scans = new(engine);
        services = new UiServices
        {
            State = state,
            Database = database,
            Clock = SystemClock.Instance,
            Scans = scans,
            MediaEngine = new MediaEngineInfo(location, bundle.GetAwaiter().GetResult()),
            Data = new UI.Data.LiveUiData(database, () => state.Settings.Scanning, SystemClock.Instance, new Core.Analysis.Audio.EvidenceRenderer(runner, Path.Combine(state.Paths.CacheDirectory, "evidence")), new Core.Analysis.PictureRenderer(runner, Path.Combine(state.Paths.CacheDirectory, "pictures"))),
            Rules = rules.GetAwaiter().GetResult(),
            Operations = new Core.Remediation.OperationRunner(database, runner, WindowsFileSystem.Instance, SystemClock.Instance),
            Updates = updates ?? new Core.Updates.UpdateService(),
            BackgroundProbe = engineMutexName is null ? PlanHost.BackgroundRunActive : static () => false,
        };

        // Owned in the order they were created and disposed in reverse (subplan 01): the update service, then the scan
        // engine, which stops a running scan and records it as stopped while the database can still write, then the
        // FFmpeg runner, and the database last. (The database went first, so a scan running at exit was recorded as
        // interrupted, with the next start as its end.)
        services._owned.AddRange([database, runner, scans, services.Updates]);
        if (startupMaintenance)
        {
            _ = Task.Run(() => RunStartupMaintenanceAsync(state, services, database));
        }

        return services;
    }

    /// <summary>The bundle check; a file it cannot read (held by an antivirus) leaves scanning on and says why in Tools.</summary>
    private static BundleCheck CheckBundle(FfmpegLocation location, Core.Logging.AppLog log)
    {
        try
        {
            return BundleIntegrity.Check(location);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning("The media engine files could not be checked", ex);
            return new BundleCheck(BundleState.Changed, null, [], [], ex.Message);
        }
    }

    private static RuleSettings LoadRules(Database database, Core.Logging.AppLog log)
    {
        try
        {
            return database.RuleSettings.Load();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.Warning("Rule settings could not be loaded; defaults apply", ex);
            return RuleSettings.Defaults;
        }
    }

    /// <summary>
    /// Start-up maintenance, off the UI thread. Crash recovery of an interrupted repair runs first, and every step runs
    /// on its own: a step that fails (a blocked registry write, an unavailable Task Scheduler) is logged and the rest
    /// still run. One failure used to skip every later step, crash recovery included.
    /// </summary>
    private static Task RunStartupMaintenanceAsync(AppState state, UiServices services, Database database)
    {
        Core.Remediation.OperationRunner operations = services.Operations!;
        return RunStepsAsync(state.Log,
        [
            // A crash during an operation leaves at most its journaled temporary file and an original in quarantine.
            ("crash recovery", () => operations.RecoverAsync()),
            ("notification registration", () => Task.FromResult(ToastRegistration.Ensure(state))),
            ("Start with Windows", () =>
            {
                new StartupRegistration().Reconcile(state.Settings.StartWithWindows);
                return Task.CompletedTask;
            }),
            ("quarantine purge", () => operations.PurgeAsync(state.Settings.Scanning.EmptyQuarantineAfterDays)),
            ("scheduled tasks", () => PlanHost.ReconcileAsync(database)),
            ("version record", () => RecordUpdateAsync(database)),
        ]);
    }

    /// <summary>Runs each step in turn; a step that throws is logged and the next one still runs.</summary>
    internal static async Task RunStepsAsync(Core.Logging.AppLog log, IReadOnlyList<(string Name, Func<Task> Run)> steps)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(steps);
        foreach ((string name, Func<Task> run) in steps)
        {
            try
            {
                await run().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Nothing awaits start-up maintenance: a failure that is not logged here is lost.
                log.Warning("Start-up maintenance: " + name + " failed", ex);
            }
        }
    }

    /// <summary>Meta key of the version that last ran against this database.</summary>
    internal const string VersionKey = "app.version";

    /// <summary>
    /// After an update, the first start notes it for the Dashboard's Recent activity ("Updated to version {v}"). The
    /// first start of all only records the version.
    /// </summary>
    internal static async Task RecordUpdateAsync(Database database)
    {
        string? previous = database.Meta.Get(VersionKey);
        if (previous == AppInfo.VersionText)
        {
            return;
        }

        if (previous is not null)
        {
            await database.Meta.SetAsync(UI.Data.LiveUiData.UpdatedKey, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow.UtcTicks}|{AppInfo.VersionText}")).ConfigureAwait(false);
        }

        await database.Meta.SetAsync(VersionKey, AppInfo.VersionText).ConfigureAwait(false);
    }

    /// <summary>Takes ownership of something to dispose with the services (demo databases, temp folders).</summary>
    public void Own(IDisposable disposable) => _owned.Add(disposable);

    /// <summary>The reason the Scan button is disabled, or null when a scan can start (subplan 09, Shell).</summary>
    public string? CannotScanReason(bool hasFolders)
    {
        if (!MediaEngine.ScanningAllowed)
        {
            return UI.Strings.Shell.EngineMissing;
        }

        if (!hasFolders)
        {
            return UI.Strings.Shell.AddFolderFirst;
        }

        EngineStatus status = Scans.Status;
        if (status.IsActive)
        {
            return status.Trigger == RunTrigger.Scheduled ? UI.Strings.Shell.ScheduledRunning : UI.Strings.Shell.AlreadyRunning;
        }

        return BackgroundRunActive() ? UI.Strings.Shell.ScheduledRunning : null;
    }

    /// <summary>
    /// True while a scheduled scan runs in another MediaVerifier process (subplan 13, GUI opened during a headless
    /// run; task SCH-06). Never in the render harness.
    /// </summary>
    public bool BackgroundRunActive() => !IsDemo && !Scans.Status.IsActive && BackgroundProbe();

    /// <summary>How a background run is detected; tests replace it.</summary>
    public Func<bool> BackgroundProbe { get; init; } = PlanHost.BackgroundRunActive;

    /// <summary>
    /// Starts a manual scan of <paramref name="scopes"/> unless one cannot run (demo, no engine, a scheduled scan in
    /// another process); returns false when the request was refused, after saying why. While this window's scan runs,
    /// the request waits for it (subplan 05: a second manual request is queued) and starts when it ends.
    /// </summary>
    public async Task<bool> StartScanAsync(IReadOnlyList<ScanScope> scopes, ScanDepth depth, string scopeText, ScanOptions? options = null)
    {
        if (IsDemo || scopes.Count == 0)
        {
            return false;
        }

        if (CannotScanReason(true) is string reason)
        {
            if (MediaEngine.ScanningAllowed && Scans.Status.IsActive)
            {
                _queuedScans.Enqueue(new QueuedScan(scopes, depth, scopeText, options));
                ShowToast?.Invoke(new UI.Kit.Popups.ToastContent(Core.Model.Severity.None, UI.Strings.Shell.ScanQueued, UI.Strings.Shell.ScanQueuedText));
                return true;
            }

            ShowScanNotStarted(reason);
            return false;
        }

        try
        {
            await Scans.StartAsync(new ScanRequest(scopes, depth, RunTrigger.Manual, Options: options), scopeText).ConfigureAwait(true);
            ScanStarted?.Invoke();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // The engine is busy: a scan here, or one in another MediaVerifier process (a scheduled scan).
            State.Log.Warning("Scan not started: " + ex.Message);
            ShowScanNotStarted(CannotScanReason(true) ?? UI.Strings.Shell.ScheduledRunning);
            return false;
        }
    }

    /// <summary>Requests waiting for this window's scan to end.</summary>
    public int QueuedScanCount => _queuedScans.Count;

    /// <summary>Starts the oldest queued request once no scan runs here (the shell calls it when a scan ends).</summary>
    public async Task StartQueuedScanAsync()
    {
        if (_queuedScans.Count == 0 || Scans.Status.IsActive)
        {
            return;
        }

        QueuedScan next = _queuedScans.Dequeue();
        _ = await StartScanAsync(next.Scopes, next.Depth, next.ScopeText, next.Options).ConfigureAwait(true);
    }

    private sealed record QueuedScan(IReadOnlyList<ScanScope> Scopes, ScanDepth Depth, string ScopeText, ScanOptions? Options);

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                _owned[i].Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                State.Log.Warning("Could not dispose a service", ex);
            }
        }

        _owned.Clear();
    }
}
