using MediaVerifier.Core;
using MediaVerifier.Core.Ipc;

namespace MediaVerifier.App;

/// <summary>
/// <c>--run-plan &lt;id&gt;</c> (subplans 01 and 13): forwards to a running GUI when there is one,
/// otherwise takes the engine mutex and runs the plan without any UI. Never shows a message box.
/// </summary>
internal static class HeadlessLauncher
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitForwarded = 3;

    public static int Run(long planId) => Run(planId, AppPaths.Default, IpcNames.PipeNameForCurrentSession());

    internal static int Run(long planId, AppPaths paths, string pipeName)
    {
        using AppState state = AppState.Load(paths);
        if (NamedMutexLock.IsHeldElsewhere(NamedMutexLock.SingleInstanceName)
            && IpcClient.TrySend(pipeName, IpcMessage.ForRunPlan(planId), TimeSpan.FromSeconds(5)))
        {
            state.Log.Info($"Plan {planId} forwarded to the running window");
            return ExitForwarded;
        }

        return RunHere(state, planId, NamedMutexLock.EngineName, PlanHost.Run);
    }

    /// <summary>
    /// Subplan 13, Headless run, steps 2 to 6: holds the engine mutex for the whole run (conditions, scan, notification),
    /// so a window opened meanwhile cannot start a scan of its own; <paramref name="run"/> runs the plan. When another
    /// run holds the mutex this one leaves it alone (exit 3), and the bell says the plan was skipped: two plans due
    /// at the same minute lost one of them without a trace.
    /// </summary>
    internal static int RunHere(AppState state, long planId, string engineMutexName, Func<AppState, long, int> run)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(run);
        using NamedMutexLock? engine = NamedMutexLock.TryAcquire(engineMutexName, TimeSpan.Zero);
        if (engine is null)
        {
            state.Log.Warning($"Plan {planId} not started: another MediaVerifier process is scanning");
            PlanHost.RecordSkipped(state, CoreStrings.Notify.AnotherScan);
            return ExitForwarded;
        }

        return run(state, planId);
    }
}

/// <summary><c>--cleanup</c>: removes per-user registrations; called by the uninstaller.</summary>
internal static class CleanupCommand
{
    public static int Run()
    {
        bool ok = true;
        try
        {
            new StartupRegistration().Remove();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ok = false;
            Core.Logging.ErrorLog.Write("Cleanup startup entry", ex);
        }

        // The scheduled tasks and the notification registration (subplan 13); each step runs even when one fails.
        foreach (Action step in new Action[] { PlanHost.RemoveScheduledTasks, PlanHost.RemoveToastRegistration })
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                ok = false;
                Core.Logging.ErrorLog.Write("Cleanup " + step.Method.Name, ex);
            }
        }

        return ok ? 0 : 1;
    }
}
