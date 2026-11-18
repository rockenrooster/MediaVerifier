using System.Diagnostics;
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
    /// <summary>How long the exit waits for a settings write that is still running (a stuck network profile).</summary>
    private static readonly TimeSpan FlushLimit = TimeSpan.FromSeconds(10);

    private readonly List<IDisposable> _owned = [];
    private readonly Lock _gate = new();
    private readonly Lock _saveGate = new();
    private byte[]? _pendingSettings;
    private TaskCompletionSource<bool>? _pendingSaved;
    private Task _writer = Task.CompletedTask;
    private bool _writing;
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

    /// <summary>
    /// Saves the settings without waiting for the disk, so no click or key in Settings waits for a flush (subplan 09:
    /// no file access on the UI thread). The values are serialized here, one consistent set, and a single background
    /// writer writes, flushes and swaps the file in. Changes made while a write runs are written together afterwards as
    /// the newest values, so typing a number writes at most twice. The task is true once the file holds this change,
    /// false when it could not be written (the log has the reason).
    /// </summary>
    public Task<bool> SaveSettings()
    {
        byte[] content = SettingsStore.Serialize(Settings);
        lock (_saveGate)
        {
            _pendingSettings = content;
            _pendingSaved ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> saved = _pendingSaved.Task;
            if (!_writing)
            {
                _writing = true;
                _writer = Task.Run(WritePendingSettings);
            }

            return saved;
        }
    }

    /// <summary>Waits until the settings queued so far are written (the app is exiting; at most 10 s).</summary>
    public void FlushSettings()
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            Task writer;
            lock (_saveGate)
            {
                if (!_writing)
                {
                    return;
                }

                writer = _writer;
            }

            TimeSpan left = FlushLimit - waited.Elapsed;
            if (left <= TimeSpan.Zero || !writer.Wait(left))
            {
                Log.Warning("Settings were still being written when MediaVerifier closed");
                return;
            }
        }
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

        // The window's last save (its placement) is queued as it closes: write it before the log goes.
        FlushSettings();
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

    /// <summary>The background writer: takes the newest values until none are left. Never throws.</summary>
    private void WritePendingSettings()
    {
        while (true)
        {
            byte[] content;
            TaskCompletionSource<bool> saved;
            lock (_saveGate)
            {
                if (_pendingSettings is null || _pendingSaved is null)
                {
                    _writing = false;
                    return;
                }

                content = _pendingSettings;
                saved = _pendingSaved;
                _pendingSettings = null;
                _pendingSaved = null;
            }

            bool written;
            try
            {
                written = SettingsStore.TryWrite(content);
            }
            catch (Exception ex)
            {
                // An unexpected failure (a malformed path, a security policy): reported like any other failed save.
                ErrorLog.Write("Settings save", ex);
                written = false;
            }

            saved.TrySetResult(written);
        }
    }
}
