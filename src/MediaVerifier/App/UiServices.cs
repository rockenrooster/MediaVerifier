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

    public Task<ReevaluationResult> ReevaluateAsync(IReadOnlyCollection<string> ruleIds) => engine.ReevaluateAsync(ruleIds);

    public event Action<RunSummary>? RunCompleted
    {
        add => engine.RunCompleted += value;
        remove => engine.RunCompleted -= value;
    }

    public ScanEngine Engine => engine;

    public async Task<long> StartAsync(ScanRequest request, string scopeText)
    {
        _scope = scopeText;
        return await engine.StartAsync(request).ConfigureAwait(false);
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

    /// <summary>True for the render harness: no timers start work, nothing is written.</summary>
    public bool IsDemo { get; init; }

    /// <summary>Opens the user's library, the bundled media engine and the scan engine.</summary>
    public static UiServices CreateLive(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Database database = Database.Open(state.Paths.ResolveDatabaseFile(state.Settings.DatabasePath));
        FfmpegLocation location = FfmpegLocator.Locate(state.Paths);
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
        ScanEngine engine = new(EngineFactory.CreateServices(database, runner, state.Paths, () => state.Settings.Scanning, () => services.Rules));
        EngineScanControl scans = new(engine);
        services = new UiServices
        {
            State = state,
            Database = database,
            Clock = SystemClock.Instance,
            Scans = scans,
            MediaEngine = new MediaEngineInfo(location, BundleIntegrity.Check(location)),
            Data = new UI.Data.LiveUiData(database, () => state.Settings.Scanning, SystemClock.Instance),
            Rules = rules,
        };
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

        return null;
    }

    /// <summary>
    /// Starts a manual scan of <paramref name="scopes"/> unless one cannot run (demo, no engine, a scan
    /// already running); returns false when nothing started.
    /// </summary>
    public async Task<bool> StartScanAsync(IReadOnlyList<ScanScope> scopes, ScanDepth depth, string scopeText, ScanOptions? options = null)
    {
        if (IsDemo || scopes.Count == 0 || CannotScanReason(true) is not null)
        {
            return false;
        }

        try
        {
            await Scans.StartAsync(new ScanRequest(scopes, depth, RunTrigger.Manual, Options: options), scopeText).ConfigureAwait(true);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            State.Log.Warning("Scan not started: " + ex.Message);
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
