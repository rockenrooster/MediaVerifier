using MediaVerifier.Core;
using MediaVerifier.Core.Logging;
using MediaVerifier.Core.Settings;

namespace MediaVerifier.App;

/// <summary>
/// Composition root (subplan 01): owns the paths, settings and log, constructs services lazily on
/// first use and disposes them in reverse order of creation. No dependency-injection container.
/// </summary>
internal sealed class AppState : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    private AppState(AppPaths paths, SettingsStore store, AppSettings settings, AppLog log, bool ownsSharedLog)
    {
        Paths = paths;
        SettingsStore = store;
        Settings = settings;
        Log = log;
        OwnsSharedLog = ownsSharedLog;
    }

    public AppPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public AppLog Log { get; }

    private bool OwnsSharedLog { get; }

    /// <summary>Raised on the caller's thread after the settings were saved.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    /// Creates the folders, points the error log at them, starts the app log (installed as the shared
    /// log when <paramref name="installSharedLog"/> is set) and loads the settings.
    /// </summary>
    public static AppState Load(AppPaths paths, bool installSharedLog = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();
        ErrorLog.Configure(paths.LogsDirectory);
        AppLog log = new(paths.LogsDirectory);
        if (installSharedLog)
        {
            AppLog.SetShared(log);
        }

        try
        {
            SettingsStore store = new(paths.SettingsFile);
            AppSettings settings = store.Load();
            AppState state = new(paths, store, settings, log, installSharedLog);

            // The log is created first, so it is disposed last.
            state._owned.Add(log);
            return state;
        }
        catch
        {
            if (installSharedLog)
            {
                AppLog.SetShared(null);
            }

            log.Dispose();
            throw;
        }
    }

    /// <summary>Persists the settings and notifies listeners. False when the file could not be written.</summary>
    public bool SaveSettings()
    {
        bool saved = SettingsStore.TrySave(Settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    /// <summary>
    /// Returns the service in <paramref name="slot"/>, creating it on first use. Disposable services are
    /// disposed in reverse creation order when the state is disposed.
    /// </summary>
    public T GetOrCreate<T>(ref T? slot, Func<T> factory)
        where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (slot is null)
            {
                T created = factory();
                if (created is IDisposable disposable)
                {
                    _owned.Add(disposable);
                }

                slot = created;
            }

            return slot;
        }
    }

    /// <summary>Number of services currently owned (for the self-check and tests).</summary>
    internal int OwnedCount
    {
        get
        {
            lock (_gate)
            {
                return _owned.Count;
            }
        }
    }

    public void Dispose()
    {
        IDisposable[] owned;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owned = [.. _owned];
            _owned.Clear();
        }

        for (int i = owned.Length - 1; i >= 0; i--)
        {
            try
            {
                if (ReferenceEquals(owned[i], Log) && OwnsSharedLog)
                {
                    AppLog.SetShared(null);
                }

                owned[i].Dispose();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Dispose " + owned[i].GetType().Name, ex);
            }
        }
    }
}
