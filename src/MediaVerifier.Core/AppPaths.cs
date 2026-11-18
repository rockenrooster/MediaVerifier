using System.Globalization;

namespace MediaVerifier.Core;

/// <summary>
/// Every location the app reads or writes (subplan 01, Locations). The roaming and local roots are
/// overridable so tests never touch the real profile.
/// </summary>
public sealed class AppPaths
{
    public const string FolderName = AppInfo.ProductName;

    public AppPaths(string roamingDirectory, string localDirectory, string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        RoamingDirectory = Path.GetFullPath(roamingDirectory);
        LocalDirectory = Path.GetFullPath(localDirectory);
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
    }

    /// <summary>The real per-user locations under %APPDATA% and %LOCALAPPDATA%.</summary>
    public static AppPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName),
        AppContext.BaseDirectory);

    /// <summary>All per-user folders under one base directory, for tests and diagnostics.</summary>
    public static AppPaths UnderBase(string baseDirectory, string? applicationDirectory = null) => new(
        Path.Combine(baseDirectory, "Roaming", FolderName),
        Path.Combine(baseDirectory, "Local", FolderName),
        applicationDirectory ?? AppContext.BaseDirectory);

    /// <summary>%APPDATA%\MediaVerifier: settings.</summary>
    public string RoamingDirectory { get; }

    /// <summary>%LOCALAPPDATA%\MediaVerifier: database, logs, caches, updates, scratch.</summary>
    public string LocalDirectory { get; }

    /// <summary>The folder holding MediaVerifier.exe.</summary>
    public string ApplicationDirectory { get; }

    public string SettingsFile => Path.Combine(RoamingDirectory, "settings.json");
    public string DefaultDatabaseFile => Path.Combine(LocalDirectory, "library.db");
    public string LogsDirectory => Path.Combine(LocalDirectory, "logs");
    public string CacheDirectory => Path.Combine(LocalDirectory, "cache");
    public string UpdatesDirectory => Path.Combine(LocalDirectory, "Updates");
    public string TempDirectory => Path.Combine(LocalDirectory, "temp");

    /// <summary>The bundled FFmpeg folder; read-only, part of the install.</summary>
    public string BundledFfmpegDirectory => Path.Combine(ApplicationDirectory, "ffmpeg");

    /// <summary>The database file: the settings override when one is set, otherwise the default.</summary>
    public string ResolveDatabaseFile(string? overridePath) =>
        string.IsNullOrWhiteSpace(overridePath) ? DefaultDatabaseFile : Path.GetFullPath(overridePath);

    /// <summary>Scratch folder for one child-process job; created on demand, deleted after the job.</summary>
    public string JobTempDirectory(long runId, long jobId) => Path.Combine(
        RunTempDirectory(runId),
        jobId.ToString(CultureInfo.InvariantCulture));

    /// <summary>The scratch folder of one scan run; its jobs' folders are below it.</summary>
    public string RunTempDirectory(long runId) => Path.Combine(TempDirectory, runId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Deletes the scratch folders of scan runs (the numbered folders under temp) except <paramref name="keepRunId"/>'s:
    /// at the start of a run for what crashed or killed runs left behind, and at its end for its own. Called only by
    /// the process that holds the engine mutex, so no other run is using one. Folders still in use are skipped.
    /// </summary>
    public int CleanRunTemp(long? keepRunId = null)
    {
        if (!Directory.Exists(TempDirectory))
        {
            return 0;
        }

        int removed = 0;
        foreach (string folder in Directory.EnumerateDirectories(TempDirectory))
        {
            if (!long.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out long runId) || runId == keepRunId)
            {
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>Creates every writable folder. The FFmpeg folder is not created: it ships with the app.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RoamingDirectory);
        Directory.CreateDirectory(LocalDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
