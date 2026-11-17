using System.Diagnostics;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Ipc;
using MediaVerifier.Core.Logging;
using MediaVerifier.Diagnostics;
using MediaVerifier.UI;
using MediaVerifier.UI.Theme;

namespace MediaVerifier;

internal static class Program
{
    /// <summary>
    /// Milliseconds since the process started (runtime start-up included), logged by the main window
    /// at first paint for the 500 ms start-up budget.
    /// </summary>
    internal static long MillisecondsSinceProcessStart()
    {
        using Process self = Process.GetCurrentProcess();
        return (long)(DateTime.Now - self.StartTime).TotalMilliseconds;
    }

    private static int s_errorReported;

    [STAThread]
    private static int Main(string[] args)
    {
        // Subplan 13, Registration: before any window or toast exists.
        ToastRegistration.SetProcessId();
        if (!CommandLineParser.TryParse(args, out LaunchOptions options, out string error))
        {
            return ReportBadArguments(error);
        }

        PlanHost.Install();
        return options.Mode switch
        {
            LaunchMode.SelfCheck => SelfCheck.Run(),
            LaunchMode.Cleanup => CleanupCommand.Run(),
            LaunchMode.RunPlan => HeadlessLauncher.Run(options.PlanId!.Value),
            LaunchMode.RenderScreens => ScreenRenderer.Run(options.RenderDirectory!),
            _ => RunGui(options),
        };
    }

    private static int RunGui(LaunchOptions options)
    {
        using NamedMutexLock? instance = NamedMutexLock.TryAcquire(NamedMutexLock.SingleInstanceName, TimeSpan.Zero);
        string pipeName = IpcNames.PipeNameForCurrentSession();
        if (instance is null)
        {
            // Another window owns this session: hand the request over and leave.
            if (options.ActivationUri is not null)
            {
                _ = IpcClient.TrySend(pipeName, IpcMessage.ForOpen(options.ActivationUri), TimeSpan.FromSeconds(3));
            }
            else if (!options.Minimized)
            {
                _ = IpcClient.TrySend(pipeName, IpcMessage.ForActivate(), TimeSpan.FromSeconds(3));
            }

            return 0;
        }

        using AppState state = AppState.Load(AppPaths.Default);
        InstallExceptionHandlers();
        ApplicationConfiguration.Initialize();
        ThemeService.Apply(state.Settings.Theme);
        state.Log.Info($"{AppInfo.WindowTitle} starting");

        using IpcServer ipc = new(pipeName);
        ipc.Start();
        Core.Storage.Database.UiThreadId = Environment.CurrentManagedThreadId;
        using UiServices services = UiServices.CreateLive(state);
        using MainForm form = new(services, options, ipc);
        Application.Run(form);
        ThemeService.StopListening();
        state.Log.Info("Exiting");
        return 0;
    }

    private static int ReportBadArguments(string error)
    {
        try
        {
            AppPaths.Default.EnsureCreated();
            using AppLog log = new(AppPaths.Default.LogsDirectory);
            log.Error("Command line rejected: " + error + Environment.NewLine + CommandLineParser.Usage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return 1;
    }

    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                ErrorLog.Write("Unhandled", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("Unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Log, keep running, tell the user once per session (subplan 01).</summary>
    private static void ReportUiException(Exception exception)
    {
        ErrorLog.Write("UI thread", exception);
        if (Interlocked.Exchange(ref s_errorReported, 1) != 0)
        {
            return;
        }

        TaskDialogButton openLog = new(Strings.UnhandledError.OpenLog);
        TaskDialogButton close = new(Strings.UnhandledError.Close);
        TaskDialogPage page = new()
        {
            Caption = Strings.ProductName,
            Heading = Strings.UnhandledError.Title,
            Text = Strings.UnhandledError.Body,
            Icon = TaskDialogIcon.Warning,
            Buttons = { openLog, close },
            DefaultButton = close,
        };
        Form? owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        TaskDialogButton result = owner is not null ? TaskDialog.ShowDialog(owner, page) : TaskDialog.ShowDialog(page);
        if (result == openLog)
        {
            OpenInShell(ErrorLog.FilePath);
        }
    }

    internal static void OpenInShell(string path)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ErrorLog.Write("Open " + path, ex);
        }
    }
}
