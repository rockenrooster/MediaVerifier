using System.Globalization;

namespace MediaVerifier.Core.Logging;

/// <summary>
/// Errors, each written to one place (ported from FastTaskMgr, subplan 01): unhandled exceptions, which may come as the
/// process dies, go synchronously to <c>logs\error.log</c> with a one-line pointer in app.log; every other error goes to
/// app.log only, or to error.log when no app.log is open yet. Never throws. The same error (type and message) repeated
/// within a minute is written once, whatever its context, so a failure that hits every file of a scan (a full disk, a
/// closed database) leaves one stack trace and a count instead of burying the first entry.
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);
    private static readonly Lock Gate = new();
    private static string s_directory = AppPaths.Default.LogsDirectory;
    private static string s_lastEntryKey = "";
    private static long s_lastEntryAt;
    private static int s_repeats;

    /// <summary>The contexts the process's last-chance handlers use (Program): these go to error.log.</summary>
    private static readonly HashSet<string> CrashContexts = new(StringComparer.Ordinal) { "Unhandled", "UI thread", "Unobserved task" };

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
            s_repeats = 0;
        }
    }

    public static void Write(string context, Exception exception)
    {
        try
        {
            AppLog? app = AppLog.Shared;
            bool crash = CrashContexts.Contains(context) || app is null;
            string key = $"{exception.GetType().FullName}|{exception.Message}";
            int repeats;
            lock (Gate)
            {
                long now = Environment.TickCount64;
                if (key == s_lastEntryKey && now - s_lastEntryAt < (long)RepeatWindow.TotalMilliseconds)
                {
                    s_repeats++;
                    return;
                }

                repeats = s_repeats;
                s_repeats = 0;
                s_lastEntryKey = key;
                s_lastEntryAt = now;
                if (crash)
                {
                    AppendToFile(context, exception, repeats);
                }
            }

            string earlier = repeats > 0 ? string.Create(CultureInfo.InvariantCulture, $" (the error before this one repeated {repeats} more times)") : "";
            if (crash)
            {
                app?.Error($"{context}: {exception.GetType().Name}: {exception.Message}{earlier}; details in error.log");
            }
            else
            {
                app!.Error(context + earlier, exception);
            }
        }
        catch
        {
            // Last-resort logging must never throw.
        }
    }

    private static void AppendToFile(string context, Exception exception, int repeats)
    {
        Directory.CreateDirectory(s_directory);
        string path = Path.Combine(s_directory, "error.log");
        FileInfo file = new(path);
        if (file.Exists && file.Length > MaxBytes)
        {
            File.Move(path, path + ".old", overwrite: true);
        }

        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string earlier = repeats > 0 ? string.Create(CultureInfo.InvariantCulture, $"{stamp} (the error before this one repeated {repeats} more times){Environment.NewLine}") : "";
        File.AppendAllText(path, $"{earlier}{stamp} [{context}] {exception}{Environment.NewLine}{Environment.NewLine}");
    }
}
