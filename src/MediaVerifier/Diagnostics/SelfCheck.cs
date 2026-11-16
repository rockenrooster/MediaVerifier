using System.Globalization;
using System.Text;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Ipc;
using MediaVerifier.Core.Logging;
using MediaVerifier.Core.Settings;

namespace MediaVerifier.Diagnostics;

/// <summary>
/// <c>--self-check</c>: diagnostics without a window (task F-11). Exit 0 when every check passes,
/// 1 otherwise. Never shows UI. Results go to the app log when it can be written.
/// </summary>
internal static class SelfCheck
{
    public static int Run() => Run(AppPaths.Default);

    public static int Run(AppPaths paths) => Run(paths, checkMediaEngine: true);

    /// <summary>Tests that are about the profile switch the media-engine check off; it depends on MEDIAVERIFIER_FFMPEG_DIR.</summary>
    internal static int Run(AppPaths paths, bool checkMediaEngine)
    {
        List<CheckResult> results = [];
        results.Add(Check("Paths writable", () => PathsWritable(paths)));
        results.Add(Check("Settings round trip", () => SettingsRoundTrip(paths)));
        results.Add(Check("Log write", () => LogWrite(paths)));
        results.Add(Check("IPC loopback", IpcLoopback));
        results.Add(Check("App state lifecycle", () => AppStateLifecycle(paths)));
        if (checkMediaEngine)
        {
            results.Add(Check("Media engine", () => MediaEngine(paths)));
        }
        foreach (Func<string?> extra in ExtraChecks)
        {
            results.Add(Check(extra.Method.Name, extra));
        }

        bool passed = results.TrueForAll(r => r.Passed);
        Report(paths, results, passed);
        return passed ? 0 : 1;
    }

    /// <summary>Checks added by later phases (bundle integrity, update trust, storage, ...).</summary>
    internal static List<Func<string?>> ExtraChecks { get; } = [];

    private static CheckResult Check(string name, Func<string?> check)
    {
        try
        {
            string? failure = check();
            return new CheckResult(name, failure is null, failure ?? "OK");
        }
        catch (Exception ex)
        {
            return new CheckResult(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string? PathsWritable(AppPaths paths)
    {
        paths.EnsureCreated();
        string[] directories = [paths.RoamingDirectory, paths.LocalDirectory, paths.LogsDirectory, paths.CacheDirectory, paths.UpdatesDirectory, paths.TempDirectory];
        foreach (string directory in directories)
        {
            string probe = Path.Combine(directory, ".selfcheck-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }

        return null;
    }

    private static string? SettingsRoundTrip(AppPaths paths)
    {
        string folder = Path.Combine(paths.TempDirectory, "selfcheck-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        try
        {
            SettingsStore store = new(Path.Combine(folder, "settings.json"));
            AppSettings written = new()
            {
                Theme = ThemeMode.Dark,
                MinimizeToTray = true,
                DatabasePath = @"C:\Self check\library.db",
                WindowPlacement = new WindowPlacement { Left = 1, Top = 2, Width = 3, Height = 4, LastPage = "Issues" },
            };
            store.Save(written);
            AppSettings read = store.Load();
            bool same = read.Theme == written.Theme
                && read.MinimizeToTray
                && read.DatabasePath == written.DatabasePath
                && read.WindowPlacement.Width == 3
                && read.WindowPlacement.LastPage == "Issues";
            return same ? null : "Values changed in the round trip";
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static string? LogWrite(AppPaths paths)
    {
        string folder = Path.Combine(paths.TempDirectory, "selfcheck-log-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try
        {
            AppLog log = new(folder);
            log.Info("self-check");
            log.Dispose();
            string text = File.ReadAllText(log.FilePath);
            return text.Contains("[Info] self-check", StringComparison.Ordinal) ? null : "Log line missing";
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static string? IpcLoopback()
    {
        string pipe = "MediaVerifier.SelfCheck." + Guid.NewGuid().ToString("N");
        using IpcServer server = new(pipe);
        using ManualResetEventSlim received = new(false);
        server.MessageReceived += (_, message) =>
        {
            if (message.Command == IpcMessage.Activate)
            {
                received.Set();
            }
        };
        server.Start();
        if (!IpcClient.TrySend(pipe, IpcMessage.ForActivate(), TimeSpan.FromSeconds(5)))
        {
            return server.StartFailure?.Message ?? "Could not connect";
        }

        return received.Wait(TimeSpan.FromSeconds(5)) ? null : "Message not received";
    }

    private static string? AppStateLifecycle(AppPaths paths)
    {
        AppState state = AppState.Load(paths, installSharedLog: false);
        object? slot = null;
        _ = state.GetOrCreate(ref slot, () => new object());
        state.Dispose();
        return null;
    }

    /// <summary>
    /// The bundled FFmpeg is intact, is version 9.0.2 and has only the file and pipe protocols (task
    /// FF-04). A development build without a bundle skips the check; the shipped single-file build and
    /// any developer override are checked, so a full FFmpeg build through the override fails.
    /// </summary>
    private static string? MediaEngine(AppPaths paths)
    {
        FfmpegLocation location = FfmpegLocator.Locate(paths);
        // A development build has MediaVerifier.dll beside the exe; the single-file build embeds it.
        bool shipped = !File.Exists(Path.Combine(AppContext.BaseDirectory, "MediaVerifier.dll"));
        if (!location.IsOverride && !File.Exists(location.FfmpegExe) && !shipped)
        {
            return null;
        }

        BundleCheck check = BundleIntegrity.Check(location);
        if (check.State == BundleState.Missing)
        {
            return "The media engine is missing or damaged (" + string.Join(", ", check.MissingFiles) + (check.Detail is null ? "" : "; " + check.Detail) + ")";
        }

        using FfRunner runner = new(location);
        return BundleIntegrity.VerifyEngineAsync(runner, FfmpegVersions.Bundled).GetAwaiter().GetResult();
    }

    private static void Report(AppPaths paths, List<CheckResult> results, bool passed)
    {
        try
        {
            StringBuilder text = new();
            text.Append("Self-check ").Append(passed ? "passed" : "FAILED").Append(" (").Append(AppInfo.VersionText).Append(')');
            foreach (CheckResult result in results)
            {
                text.Append(Environment.NewLine).Append("  ").Append(result.Passed ? "pass " : "FAIL ").Append(result.Name).Append(": ").Append(result.Detail);
            }

            using AppLog log = new(paths.LogsDirectory);
            log.Write(passed ? LogLevel.Info : LogLevel.Error, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only data folder is exactly what failed; there is nowhere to write.
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct CheckResult(string Name, bool Passed, string Detail);
}
