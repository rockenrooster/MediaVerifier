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
        TempDirectory,
        runId.ToString(CultureInfo.InvariantCulture),
        jobId.ToString(CultureInfo.InvariantCulture));

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

    /// <summary>
    /// Deletes leftover scratch folders from earlier runs (called at startup by the process that
    /// owns the engine). Folders still in use are skipped.
    /// </summary>
    public int CleanTemp()
    {
        if (!Directory.Exists(TempDirectory))
        {
            return 0;
        }

        int removed = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(TempDirectory))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }

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
}
