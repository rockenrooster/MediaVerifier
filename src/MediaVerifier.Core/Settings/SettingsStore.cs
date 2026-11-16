using System.Globalization;
using System.Text.Json;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Saving is atomic (temp file, flush, replace); loading is
/// tolerant: unknown properties are ignored, missing ones defaulted, and an unreadable file is renamed
/// to <c>settings.corrupt-&lt;timestamp&gt;.json</c> before defaults are used.
/// </summary>
public sealed class SettingsStore
{
    private readonly IClock _clock;

    public SettingsStore(string filePath, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        _clock = clock ?? SystemClock.Instance;
    }

    public string FilePath { get; }

    public string TempPath => FilePath + ".tmp";

    /// <summary>Set by <see cref="Load"/> when the previous file could not be parsed and was renamed.</summary>
    public string? LastCorruptCopy { get; private set; }

    /// <summary>Test hook: runs after the temp file is flushed and before it replaces the real file.</summary>
    internal Action? BeforeReplace { get; set; }

    public AppSettings Load()
    {
        LastCorruptCopy = null;
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(FilePath);
            AppSettings? settings = JsonSerializer.Deserialize(bytes, AppSettingsJsonContext.Default.AppSettings);
            if (settings is null)
            {
                throw new JsonException("The settings file is empty or null.");
            }

            return Upgrade(settings).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            LastCorruptCopy = QuarantineCorruptFile();
            AppLog.Shared?.Warning($"Settings file could not be read and was set aside as {LastCorruptCopy ?? "(rename failed)"}: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes <c>settings.json.tmp</c>, flushes it to disk, then swaps it in. A crash at any point
    /// leaves either the old file or the new one, never a truncated file.
    /// </summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        using (FileStream stream = new(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, AppSettingsJsonContext.Default.AppSettings);
            stream.Flush(flushToDisk: true);
        }

        BeforeReplace?.Invoke();

        if (File.Exists(FilePath))
        {
            File.Replace(TempPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            // File.Replace needs an existing destination: the first save is a plain move.
            File.Move(TempPath, FilePath);
        }
    }

    public bool TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Shared?.Error("Settings could not be saved", ex);
            return false;
        }
    }

    /// <summary>Upgrade hook for files written by older versions. Version 1 is the first schema.</summary>
    private static AppSettings Upgrade(AppSettings settings)
    {
        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            // Written by a newer version: keep the values this version understands.
            AppLog.Shared?.Warning($"Settings schema {settings.SchemaVersion} is newer than {AppSettings.CurrentSchemaVersion}; unknown values are ignored.");
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return settings;
    }

    private string? QuarantineCorruptFile()
    {
        string stamp = _clock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string directory = Path.GetDirectoryName(FilePath) ?? ".";
        string target = Path.Combine(directory, $"settings.corrupt-{stamp}.json");
        for (int attempt = 2; File.Exists(target); attempt++)
        {
            target = Path.Combine(directory, $"settings.corrupt-{stamp}-{attempt.ToString(CultureInfo.InvariantCulture)}.json");
        }

        try
        {
            File.Move(FilePath, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
