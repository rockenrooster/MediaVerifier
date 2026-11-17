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

    /// <summary>Raised on the UI thread when a manual scan has started, so the status strip shows it at once.</summary>
    public event Action? ScanStarted;

    /// <summary>Tells the user why a scan they asked for did not start: a scan button never does nothing.</summary>
    public void ShowScanNotStarted(string reason) =>
        ShowToast?.Invoke(new UI.Kit.Popups.ToastContent(Core.Model.Severity.Medium, UI.Strings.Activity.CouldNotRun, reason));

    /// <summary>True for the render harness: no timers start work, nothing is written.</summary>
    public bool IsDemo { get; init; }

    /// <summary>The only writer of user media (subplan 12); null in the render harness.</summary>
    public Core.Remediation.OperationRunner? Operations { get; init; }

    /// <summary>The signed updater (subplan 14); null in the render harness, which never checks.</summary>
    public Core.Updates.UpdateService? Updates { get; init; }

    /// <summary>The last check found a newer release (the rail's Update item and the Settings dot).</summary>
    public bool UpdateAvailable => Updates?.LastResult?.IsUpdateAvailable == true;

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
        Database database = Database.Open(state.Paths.ResolveDatabaseFile(state.Settings.DatabasePath));
        FfmpegLocation location = mediaEngine ?? FfmpegLocator.Locate(state.Paths);
        FfRunner runner = new(location);
        RuleSettings rules = RuleSettings.Defaults;
        try
        {
            rules = Task.Run(database.RuleSettings.Load).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            state.Log.Warning("Rule settings could not be loaded; defaults apply", ex);
        }

        UiServices services = null!;
        ScanEngine engine = new(EngineFactory.CreateServices(database, runner, state.Paths, () => state.Settings.Scanning, () => services.Rules), engineMutexName ?? Core.Ipc.NamedMutexLock.EngineName);
        EngineScanControl scans = new(engine);
        services = new UiServices
        {
            State = state,
            Database = database,
            Clock = SystemClock.Instance,
            Scans = scans,
            MediaEngine = new MediaEngineInfo(location, BundleIntegrity.Check(location)),
            Data = new UI.Data.LiveUiData(database, () => state.Settings.Scanning, SystemClock.Instance, new Core.Analysis.Audio.EvidenceRenderer(runner, Path.Combine(state.Paths.CacheDirectory, "evidence")), new Core.Analysis.PictureRenderer(runner, Path.Combine(state.Paths.CacheDirectory, "pictures"))),
            Rules = rules,
            Operations = new Core.Remediation.OperationRunner(database, runner, WindowsFileSystem.Instance, SystemClock.Instance),
            Updates = updates ?? new Core.Updates.UpdateService(),
            BackgroundProbe = engineMutexName is null ? PlanHost.BackgroundRunActive : static () => false,
        };
        services._owned.Add(services.Updates);

        // A crash during an operation leaves at most its journaled temporary file: clean it up now.
        _ = !startupMaintenance ? Task.CompletedTask : Task.Run(async () =>
        {
            try
            {
                _ = ToastRegistration.Ensure(state);
                new StartupRegistration().Reconcile(state.Settings.StartWithWindows);
                await services.Operations.RecoverAsync().ConfigureAwait(false);
                await services.Operations.PurgeAsync(state.Settings.Scanning.EmptyQuarantineAfterDays).ConfigureAwait(false);
                await PlanHost.ReconcileAsync(database).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
            {
                state.Log.Warning("Start-up maintenance failed", ex);
            }
        });
        services._owned.Add(scans);
        services._owned.Add(runner);
        services._owned.Add(database);
        return services;
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
    /// Starts a manual scan of <paramref name="scopes"/> unless one cannot run (demo, no engine, a scan
    /// already running); returns false when nothing started.
    /// </summary>
    public async Task<bool> StartScanAsync(IReadOnlyList<ScanScope> scopes, ScanDepth depth, string scopeText, ScanOptions? options = null)
    {
        if (IsDemo || scopes.Count == 0)
        {
            return false;
        }

        if (CannotScanReason(true) is string reason)
        {
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
