using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.App;

/// <summary>
/// The headless run of a plan (subplan 13, Headless run; task SCH-05): conditions, the engine at below-normal
/// priority, the stop event, and the notification. Never shows a window or a message box.
/// </summary>
internal static class PlanHost
{
    public const string StopEventName = @"Local\MediaVerifier.StopRun";

    /// <summary>Wires the runner and the cleanup step; called once at start-up.</summary>
    public static void Install()
    {
        HeadlessLauncher.PlanRunner = Run;
        CleanupCommand.Steps.Add(RemoveScheduledTasks);
    }

    /// <summary>Makes the scheduled tasks match the plans with this executable's path (app start, plan changes).</summary>
    public static Task ReconcileAsync(Database database) =>
        PlanReconciler.ReconcileAsync(database, new TaskSchedulerBackend(), Environment.ProcessPath ?? Application.ExecutablePath, DateOnly.FromDateTime(DateTime.Now));

    private static void RemoveScheduledTasks() => PlanReconciler.RemoveAll(new TaskSchedulerBackend());

    private static int Run(AppState state, long planId)
    {
        using Process self = Process.GetCurrentProcess();
        self.PriorityClass = ProcessPriorityClass.BelowNormal;
        using Database database = Database.Open(state.Paths.ResolveDatabaseFile(state.Settings.DatabasePath));
        PlanRecord? plan = database.Plans.Get(planId);
        if (plan is not { Enabled: true } || PlanDefinition.FromJson(plan.DefinitionJson) is not PlanDefinition definition || definition.Validate().Count > 0)
        {
            state.Log.Warning($"Plan {planId} is missing, disabled or invalid; removing its task");
            try
            {
                new TaskSchedulerBackend().Delete(plan?.TaskName ?? "MediaVerifier\\" + TaskSchedulerBackend.TaskName(planId));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                state.Log.Warning("Could not remove the orphan task", ex);
            }

            return HeadlessScan.ExitFailure;
        }

        string? skip = SkipReason(definition.Conditions, state);
        if (skip is not null)
        {
            Record(database, new RunNotification(Severity.Low, CoreStrings.Notify.Skipped, skip, Toast: false));
            state.Log.Info($"Plan {planId} skipped: {skip}");
            return HeadlessScan.ExitSuccess;
        }

        List<long> roots = definition.Roots is { Length: > 0 } chosen ? [.. chosen] : [.. database.Roots.List().Select(r => r.Id)];
        using FfRunner runner = new(FfmpegLocator.Locate(state.Paths));
        RuleSettings rules = database.RuleSettings.Load();
        ScanEngine engine = new(EngineFactory.CreateServices(database, runner, state.Paths, () => state.Settings.Scanning, () => rules));
        using CancellationTokenSource stop = new(TimeSpan.FromMinutes(definition.Conditions.StopAfterMinutes));
        using EventWaitHandle stopEvent = new(false, EventResetMode.ManualReset, StopEventName);
        RegisteredWaitHandle stopWait = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => stop.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);
        ScanRequest request = new([.. roots.Select(id => new ScanScope(id))], definition.Depth, RunTrigger.Scheduled, planId, definition.BudgetMinutes is int minutes ? TimeSpan.FromMinutes(minutes) : null);
        (int exit, RunSummary? summary) = HeadlessScan.RunAsync(engine, request, stop.Token).GetAwaiter().GetResult();
        stopWait.Unregister(null);
        if (summary is not null)
        {
            _ = database.Plans.UpdateAsync(plan with { LastRunId = summary.RunId }).Wait(TimeSpan.FromSeconds(10));
            string finished = string.Format(CultureInfo.CurrentCulture, CoreStrings.Notify.FinishedAt, DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture));
            if (NotificationPolicy.Decide(summary, definition.Notify, new Dictionary<long, DateTimeOffset>(), DateTimeOffset.UtcNow, quietHours: false, finished) is RunNotification notification)
            {
                Record(database, notification);
                if (notification.Toast)
                {
                    SystemToast.Show(notification.Title, notification.Body);
                }
            }
        }

        return exit;
    }

    /// <summary>Why the plan should not run now, or null. Full screen is re-checked every 5 minutes for up to an hour.</summary>
    private static string? SkipReason(PlanConditions conditions, AppState state)
    {
        if (conditions.OnlyOnAcPower && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
        {
            return CoreStrings.Notify.OnBattery;
        }

        if (!conditions.SkipWhenFullScreen)
        {
            return null;
        }

        for (int attempt = 0; attempt <= 12; attempt++)
        {
            if (!Busy())
            {
                return null;
            }

            state.Log.Info("A full-screen program is running; waiting 5 minutes");
            if (attempt < 12)
            {
                Thread.Sleep(TimeSpan.FromMinutes(5));
            }
        }

        return CoreStrings.Notify.FullScreen;
    }

    private static bool Busy() => SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;

    private static void Record(Database database, RunNotification notification) =>
        database.Notifications.AddAsync(new NotificationRecord { Utc = DateTime.UtcNow.Ticks, Level = notification.Level, Title = notification.Title, Body = notification.Body }).Wait(TimeSpan.FromSeconds(10));

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}

/// <summary>
/// The system notification of a headless run: the tray balloon, which Windows shows as a toast (fallback
/// channel of subplan 13). The in-app bell keeps every notification.
/// </summary>
internal static class SystemToast
{
    public static void Show(string title, string body)
    {
        try
        {
            using NotifyIcon icon = new() { Icon = SystemIcons.Information, Text = Core.AppInfo.ProductName, Visible = true };
            icon.ShowBalloonTip(10_000, title, body.Length == 0 ? title : body, ToolTipIcon.Info);
            Thread.Sleep(TimeSpan.FromSeconds(8));
            icon.Visible = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException)
        {
            Core.Logging.ErrorLog.Write("System notification", ex);
        }
    }
}
