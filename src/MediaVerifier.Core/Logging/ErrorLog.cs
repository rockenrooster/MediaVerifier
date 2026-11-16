using System.Globalization;

namespace MediaVerifier.Core.Logging;

/// <summary>
/// Best-effort record of unhandled exceptions in <c>logs\error.log</c> (ported from FastTaskMgr).
/// Synchronous on purpose: it runs when the process may be about to die. Never throws; a failure that
/// repeats within a minute is written once.
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();
    private static string s_directory = AppPaths.Default.LogsDirectory;
    private static string s_lastEntryKey = "";
    private static long s_lastEntryAt;

    public static string FilePath
    {
        get
        {
            lock (Gate)
            {
                return Path.Combine(s_directory, "error.log");
            }
        }
    }

    /// <summary>Points the log at a logs folder (the app's, or a test's temp folder).</summary>
    public static void Configure(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        lock (Gate)
        {
            s_directory = Path.GetFullPath(logsDirectory);
            s_lastEntryKey = "";
        }
    }

    public static void Write(string context, Exception exception)
    {
        try
        {
            AppLog.Shared?.Error(context, exception);
            string key = $"{context}|{exception.GetType().FullName}|{exception.Message}";
            lock (Gate)
            {
                long now = Environment.TickCount64;
                if (key == s_lastEntryKey && now - s_lastEntryAt < 60_000)
                {
                    return;
                }

                s_lastEntryKey = key;
                s_lastEntryAt = now;
                Directory.CreateDirectory(s_directory);
                string path = Path.Combine(s_directory, "error.log");
                FileInfo file = new(path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                File.AppendAllText(path, $"{stamp} [{context}] {exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Last-resort logging must never throw.
        }
    }
}
