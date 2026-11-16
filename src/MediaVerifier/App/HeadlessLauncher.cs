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

    /// <summary>Runs the plan in this process; replaced by the scheduling subplan's runner.</summary>
    internal static Func<AppState, long, int>? PlanRunner { get; set; }

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

        using NamedMutexLock? engine = NamedMutexLock.TryAcquire(NamedMutexLock.EngineName, TimeSpan.Zero);
        if (engine is null)
        {
            state.Log.Warning($"Plan {planId} not started: another MediaVerifier process is scanning");
            return ExitFailure;
        }

        if (PlanRunner is null)
        {
            state.Log.Warning($"Plan {planId} not started: this build cannot run plans");
            return ExitFailure;
        }

        return PlanRunner(state, planId);
    }
}

/// <summary><c>--cleanup</c>: removes per-user registrations; called by the uninstaller.</summary>
internal static class CleanupCommand
{
    /// <summary>Further cleanup steps registered by later phases (scheduled tasks, toast registration).</summary>
    internal static List<Action> Steps { get; } = [];

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

        foreach (Action step in Steps)
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
