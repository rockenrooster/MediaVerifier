using System.Globalization;
using System.Text.Json;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Saving is atomic (temp file, flush, replace); loading is
/// tolerant: unknown properties are ignored, missing ones defaulted, and an unreadable file is renamed
/// to <c>settings.corrupt-&lt;timestamp&gt;.json</c> before defaults are used. A file that cannot be opened at all
/// (another program holds it, or access is denied) is left alone: defaults are used and nothing is saved over it.
/// </summary>
public sealed class SettingsStore
{
    private const int ReadAttempts = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);
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

    /// <summary>
    /// Set by <see cref="Load"/> when the file exists but could not be opened: the settings in use are defaults, and
    /// saving them would replace the user's own, so this session writes nothing.
    /// </summary>
    public bool SavingDisabled { get; private set; }

    /// <summary>Test hook: runs after the temp file is flushed and before it replaces the real file.</summary>
    internal Action? BeforeReplace { get; set; }

    public AppSettings Load()
    {
        LastCorruptCopy = null;
        SavingDisabled = false;
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        if (!TryRead(out byte[] bytes))
        {
            return new AppSettings();
        }

        try
        {
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
    /// The bytes <see cref="Write"/> stores for <paramref name="settings"/>. Taken on the thread that changes the
    /// settings, so the file gets one consistent set of values even when another thread writes it.
    /// </summary>
    public static byte[] Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return JsonSerializer.SerializeToUtf8Bytes(settings, AppSettingsJsonContext.Default.AppSettings);
    }

    /// <summary>
    /// Writes <c>settings.json.tmp</c>, flushes it to disk, then swaps it in. A crash at any point
    /// leaves either the old file or the new one, never a truncated file.
    /// </summary>
    public void Save(AppSettings settings) => Write(Serialize(settings));

    /// <inheritdoc cref="Save"/>
    public void Write(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (SavingDisabled)
        {
            throw new IOException("The settings file could not be read when MediaVerifier started, so it is not overwritten in this session.");
        }

        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (FileStream stream = new(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
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

    public bool TrySave(AppSettings settings) => TryWrite(Serialize(settings));

    /// <summary><see cref="Write"/>, logging a failure instead of throwing it. False when the file was not written.</summary>
    public bool TryWrite(byte[] content)
    {
        try
        {
            Write(content);
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

    /// <summary>
    /// Reads the file, trying again twice 100 ms apart while another program holds it (a sync client, a virus scanner,
    /// a scheduled run's save). False when it still cannot be opened: <see cref="SavingDisabled"/> is then set, because
    /// the file is there and holds the user's settings. A file that vanished in the meantime reads as no file.
    /// </summary>
    private bool TryRead(out byte[] bytes)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                bytes = File.ReadAllBytes(FilePath);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                bytes = [];
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < ReadAttempts)
                {
                    Thread.Sleep(ReadRetryDelay);
                    continue;
                }

                SavingDisabled = true;
                AppLog.Shared?.Error("Settings file could not be opened; defaults are used and settings are not saved until MediaVerifier starts again", ex);
                bytes = [];
                return false;
            }
        }
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
