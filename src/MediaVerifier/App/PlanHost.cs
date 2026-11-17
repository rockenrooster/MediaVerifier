using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Ipc;
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

    /// <summary>Wires the runner, the cleanup steps and the self-check of the toast channel; called once at start-up.</summary>
    public static void Install()
    {
        HeadlessLauncher.PlanRunner = Run;
        CleanupCommand.Steps.Add(RemoveScheduledTasks);
        CleanupCommand.Steps.Add(RemoveToastRegistration);
        Diagnostics.SelfCheck.ExtraChecks.Add(ToastActivationFactory);
    }

    /// <summary>True while a headless run of this user is alive: its stop event exists (subplan 13, task SCH-06).</summary>
    public static bool BackgroundRunActive() => BackgroundRunActive(StopEventName);

    internal static bool BackgroundRunActive(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? handle))
        {
            return false;
        }

        handle.Dispose();
        return true;
    }

    /// <summary>Asks the headless run to stop (the GUI's Stop); false when none is running.</summary>
    public static bool StopBackgroundRun() => StopBackgroundRun(StopEventName);

    internal static bool StopBackgroundRun(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    /// <summary>Whether quiet hours apply now (Settings → Notifications).</summary>
    public static bool InQuietHours(Core.Settings.ScanSettings settings) =>
        NotificationPolicy.InQuietHours(settings.QuietHours, settings.QuietFrom, settings.QuietTo, TimeOnly.FromDateTime(DateTime.Now));

    /// <summary>Makes the scheduled tasks match the plans with this executable's path (app start, plan changes).</summary>
    public static Task ReconcileAsync(Database database) =>
        PlanReconciler.ReconcileAsync(database, new TaskSchedulerBackend(), Environment.ProcessPath ?? Application.ExecutablePath, DateOnly.FromDateTime(DateTime.Now));

    private static void RemoveScheduledTasks() => PlanReconciler.RemoveAll(new TaskSchedulerBackend());

    private static void RemoveToastRegistration() => ToastRegistration.Remove(new CurrentUserRegistry());

    private static string? ToastActivationFactory() =>
        ToastInterop.CanActivate() ? null : "The Windows notification manager could not be activated; notifications use the tray balloon";

    private static int Run(AppState state, long planId)
    {
        using Process self = Process.GetCurrentProcess();
        self.PriorityClass = ProcessPriorityClass.BelowNormal;

        // The stop event exists for the whole run, so an open window sees it and its Stop works while conditions wait.
        using EventWaitHandle stopEvent = new(false, EventResetMode.ManualReset, StopEventName);
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

        string? skip = SkipReason(definition.Conditions, state, stopEvent);
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
        RegisteredWaitHandle stopWait = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => stop.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);
        ScanRequest request = new([.. roots.Select(id => new ScanScope(id))], definition.Depth, RunTrigger.Scheduled, planId, definition.BudgetMinutes is int minutes ? TimeSpan.FromMinutes(minutes) : null);
        (int exit, RunSummary? summary) = HeadlessScan.RunAsync(engine, request, stop.Token).GetAwaiter().GetResult();
        stopWait.Unregister(null);
        if (summary is not null)
        {
            _ = database.Plans.UpdateAsync(plan with { LastRunId = summary.RunId }).Wait(TimeSpan.FromSeconds(10));
            string finished = string.Format(CultureInfo.CurrentCulture, CoreStrings.Notify.FinishedAt, DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture));
            Core.Settings.ScanSettings settings = state.Settings.Scanning;
            PlanNotify notify = NotificationPolicy.WithThreshold(definition.Notify, settings.NotifyAbout);
            if (NotificationPolicy.Decide(summary, notify, new Dictionary<long, DateTimeOffset>(), DateTimeOffset.UtcNow, InQuietHours(settings), finished) is RunNotification notification)
            {
                Record(database, notification);
                if (notification.Toast && settings.ShowWindowsNotifications && ToastRegistration.Ensure(state))
                {
                    _ = SystemToast.Show(notification.Title, notification.Body, new ActivationUri(ActivationTarget.Run, summary.RunId), notification.Silent);
                }
            }

            // After the run and its notification: a slow or failing webhook delays only the process exit.
            WebhookDispatch.SendAsync(state, database, summary).GetAwaiter().GetResult();
        }

        return exit;
    }

    /// <summary>
    /// Why the plan should not run now, or null. Full screen is re-checked every 5 minutes for up to an hour; the
    /// stop event ends the wait.
    /// </summary>
    private static string? SkipReason(PlanConditions conditions, AppState state, EventWaitHandle stopEvent)
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
            if (attempt < 12 && stopEvent.WaitOne(TimeSpan.FromMinutes(5)))
            {
                return CoreStrings.Notify.Stopped;
            }
        }

        return CoreStrings.Notify.FullScreen;
    }

    /// <summary>The conditions checked once, without waiting (a plan forwarded to the open window).</summary>
    internal static string? ImmediateSkipReason(PlanConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.OnlyOnAcPower && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
        {
            return CoreStrings.Notify.OnBattery;
        }

        return conditions.SkipWhenFullScreen && Busy() ? CoreStrings.Notify.FullScreen : null;
    }

    private static bool Busy() => SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;

    private static void Record(Database database, RunNotification notification) =>
        database.Notifications.AddAsync(new NotificationRecord { Utc = DateTime.UtcNow.Ticks, Level = notification.Level, Title = notification.Title, Body = notification.Body }).Wait(TimeSpan.FromSeconds(10));

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}

/// <summary>
/// A plan forwarded by a scheduled launch while the window is open (subplan 13, Headless run, step 1): it runs in
/// the window's own engine, which shows its progress. Afterwards the notification goes to the bell and, unless the
/// window is focused, to the system channel; a focused window shows it as an in-app toast.
/// </summary>
internal static class GuiPlanRunner
{
    public static async Task RunAsync(UiServices services, long planId, Func<bool> windowFocused, Action<RunNotification> showInApp)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.IsDemo)
        {
            return;
        }

        Core.Logging.AppLog log = services.State.Log;
        (PlanRecord? plan, PlanDefinition? definition, List<long> roots) = await Task.Run(() =>
        {
            PlanRecord? record = services.Database.Plans.Get(planId);
            PlanDefinition? parsed = record is { Enabled: true } ? PlanDefinition.FromJson(record.DefinitionJson) : null;
            List<long> ids = parsed?.Roots is { Length: > 0 } chosen ? [.. chosen] : [.. services.Database.Roots.List().Where(r => r.Enabled).Select(r => r.Id)];
            return (record, parsed is not null && parsed.Validate().Count == 0 ? parsed : null, ids);
        }).ConfigureAwait(true);
        if (plan is null || definition is null || roots.Count == 0)
        {
            log.Warning($"Forwarded plan {planId} is missing, disabled or invalid");
            return;
        }

        string? skip = PlanHost.ImmediateSkipReason(definition.Conditions);
        if (skip is null && services.CannotScanReason(true) is string reason)
        {
            skip = reason;
        }

        if (skip is not null)
        {
            log.Info($"Forwarded plan {planId} skipped: {skip}");
            await services.Database.Notifications.AddAsync(new NotificationRecord { Utc = DateTime.UtcNow.Ticks, Level = Severity.Low, Title = CoreStrings.Notify.Skipped, Body = skip }).ConfigureAwait(true);
            return;
        }

        void Completed(RunSummary summary)
        {
            if (summary.Trigger != RunTrigger.Scheduled || summary.PlanId != planId)
            {
                return;
            }

            services.Scans.RunCompleted -= Completed;
            _ = Task.Run(() => FinishAsync(services, plan, definition, summary, windowFocused, showInApp));
        }

        services.Scans.RunCompleted += Completed;
        try
        {
            ScanRequest request = new([.. roots.Select(id => new ScanScope(id))], definition.Depth, RunTrigger.Scheduled, planId, definition.BudgetMinutes is int minutes ? TimeSpan.FromMinutes(minutes) : null);
            _ = await services.Scans.StartAsync(request, plan.Name).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            services.Scans.RunCompleted -= Completed;
            log.Warning($"Forwarded plan {planId} not started", ex);
        }
    }

    private static async Task FinishAsync(UiServices services, PlanRecord plan, PlanDefinition definition, RunSummary summary, Func<bool> windowFocused, Action<RunNotification> showInApp)
    {
        try
        {
            await services.Database.Plans.UpdateAsync(plan with { LastRunId = summary.RunId }).ConfigureAwait(false);
            _ = Task.Run(() => WebhookDispatch.SendAsync(services.State, services.Database, summary));
            Core.Settings.ScanSettings settings = services.State.Settings.Scanning;
            string finished = string.Format(CultureInfo.CurrentCulture, CoreStrings.Notify.FinishedAt, DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture));
            PlanNotify notify = NotificationPolicy.WithThreshold(definition.Notify, settings.NotifyAbout);
            if (NotificationPolicy.Decide(summary, notify, new Dictionary<long, DateTimeOffset>(), DateTimeOffset.UtcNow, PlanHost.InQuietHours(settings), finished) is not RunNotification notification)
            {
                return;
            }

            await services.Database.Notifications.AddAsync(new NotificationRecord { Utc = DateTime.UtcNow.Ticks, Level = notification.Level, Title = notification.Title, Body = notification.Body }).ConfigureAwait(false);
            if (!notification.Toast)
            {
                return;
            }

            if (!windowFocused() && settings.ShowWindowsNotifications
                && SystemToast.Show(notification.Title, notification.Body, new ActivationUri(ActivationTarget.Run, summary.RunId), notification.Silent, allowBalloon: false))
            {
                return;
            }

            if (!notification.Silent)
            {
                showInApp(notification);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            services.State.Log.Warning("The notification of a forwarded plan run failed", ex);
        }
    }
}

/// <summary>Posts a scheduled run's summary to the webhook in Settings, when one is set (task N-06). Never throws.</summary>
internal static class WebhookDispatch
{
    public static async Task SendAsync(AppState state, Database database, RunSummary summary)
    {
        Core.Settings.ScanSettings settings = state.Settings.Scanning;
        if (settings.WebhookUrl is null)
        {
            return;
        }

        WebhookAddressVerdict verdict = WebhookAddress.Check(settings.WebhookUrl, out Uri? address);
        if (address is null || (verdict == WebhookAddressVerdict.NeedsConfirmation && !settings.WebhookAllowHttp))
        {
            state.Log.Warning("The webhook address cannot be used; nothing was sent");
            return;
        }

        try
        {
            Dictionary<string, long> byKind = await Task.Run(() => WebhookPayload.CountByKind(database, summary.RunId)).ConfigureAwait(false);
            bool sent = await new WebhookSender().SendAsync(address, WebhookPayload.From(summary, byKind, DateTimeOffset.UtcNow), message => state.Log.Warning(message)).ConfigureAwait(false);
            state.Log.Info(sent ? $"Webhook received run {summary.RunId}" : $"Webhook did not receive run {summary.RunId}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            state.Log.Warning("The webhook could not be sent", ex);
        }
    }
}
