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

    /// <summary>The Task Scheduler. Tests put a fake in its place, so they never register a task with Windows.</summary>
    internal static Func<IScheduleBackend> Scheduler { get; set; } = static () => new TaskSchedulerBackend();

    /// <summary>Wires the self-check of the toast channel; called once at start-up.</summary>
    public static void Install() => Diagnostics.SelfCheck.ExtraChecks.Add(ToastActivationFactory);

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
        PlanReconciler.ReconcileAsync(database, Scheduler(), Environment.ProcessPath ?? Application.ExecutablePath, DateOnly.FromDateTime(DateTime.Now));

    /// <summary><c>--cleanup</c>: removes every MediaVerifier task and the folder they lived in.</summary>
    internal static void RemoveScheduledTasks()
    {
        PlanReconciler.RemoveAll(Scheduler());
        TaskSchedulerBackend.RemoveFolderIfEmpty();
    }

    /// <summary><c>--cleanup</c>: removes the notification registration.</summary>
    internal static void RemoveToastRegistration() => ToastRegistration.Remove(new CurrentUserRegistry());

    private static string? ToastActivationFactory() =>
        ToastInterop.CanActivate() ? null : "The Windows notification manager could not be activated; notifications use the tray balloon";

    /// <summary>
    /// <c>--run-plan</c> in this process (subplan 13, Headless run, steps 3 to 6), at below-normal priority. The
    /// launcher holds the engine mutex for the whole run.
    /// </summary>
    internal static int Run(AppState state, long planId)
    {
        using (Process self = Process.GetCurrentProcess())
        {
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }

        return RunPlan(state, planId, StopEventName, NamedMutexLock.EngineName, FfmpegLocator.Locate(state.Paths));
    }

    /// <summary>
    /// Loads the plan, checks its conditions, scans, records the notification and posts the webhook. Tests pass a stop
    /// event of their own, so an open MediaVerifier never takes their run for a scheduled scan. <paramref name="launcherLock"/>
    /// is the engine mutex the launcher holds for the whole run.
    /// </summary>
    internal static int RunPlan(AppState state, long planId, string stopEventName, string launcherLock, FfmpegLocation mediaEngine)
    {
        // The stop event exists for the whole run, so an open window sees it and its Stop works while conditions wait.
        using EventWaitHandle stopEvent = new(false, EventResetMode.ManualReset, stopEventName);
        using Database database = Database.Open(state.Paths.ResolveDatabaseFile(state.Settings.DatabasePath));
        PlanRecord? record = database.Plans.Get(planId);
        if (PlanJob.Load(database, planId) is not PlanJob job)
        {
            state.Log.Warning($"Plan {planId} is missing, disabled or invalid; removing its task");
            try
            {
                Scheduler().Delete(record?.TaskName ?? "MediaVerifier\\" + TaskSchedulerBackend.TaskName(planId));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException)
            {
                state.Log.Warning("Could not remove the orphan task", ex);
            }

            return HeadlessScan.ExitFailure;
        }

        if (job.Roots.Count == 0)
        {
            state.Log.Info($"Plan {planId} has no folders to scan");
            return HeadlessScan.ExitSuccess;
        }

        string? skip = SkipReason(job.Definition.Conditions, state, stopEvent);
        if (skip is not null)
        {
            RecordSkipped(database, skip);
            state.Log.Info($"Plan {planId} skipped: {skip}");
            return HeadlessScan.ExitSuccess;
        }

        using FfRunner runner = new(mediaEngine);
        RuleSettings rules = database.RuleSettings.Load();

        // The launcher holds its engine mutex from before the conditions until the process exits (subplan 13, step 2).
        // NamedMutexLock owns a mutex on a thread of its own, so the engine taking the same mutex again from this
        // process was refused, and no scheduled scan ever ran while the window was closed: the engine's lock is one of
        // this process's own.
        string engineLock = launcherLock + ".Run." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        using ScanEngine engine = new(EngineFactory.CreateServices(database, runner, state.Paths, () => state.Settings.Scanning, () => rules), engineLock);
        using CancellationTokenSource stop = new(TimeSpan.FromMinutes(job.Definition.Conditions.StopAfterMinutes));
        RegisteredWaitHandle stopWait = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => stop.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);
        (int exit, RunSummary? summary) = HeadlessScan.RunAsync(engine, job.Request, stop.Token).GetAwaiter().GetResult();
        stopWait.Unregister(null);

        // The run row links itself to the plan (RunStore.BeginAsync). Writing the plan record read at the start back
        // here undid whatever the user changed in it during the run, a switch-off included.
        Core.Settings.ScanSettings settings = state.Settings.Scanning;
        RunNotification? notification = summary is null
            ? new RunNotification(Severity.High, CoreStrings.Notify.CouldNotRun, string.Empty, job.Definition.Notify.When != NotifyWhen.Never)
            : NotificationPolicy.Decide(summary, NotificationPolicy.WithThreshold(job.Definition.Notify, settings.NotifyAbout), new Dictionary<long, DateTimeOffset>(), DateTimeOffset.UtcNow, InQuietHours(settings), FinishedText());
        if (notification is not null)
        {
            Record(database, notification);
            if (notification.Toast && settings.ShowWindowsNotifications && ToastRegistration.Ensure(state))
            {
                _ = SystemToast.Show(notification.Title, notification.Body, summary is null ? new ActivationUri(ActivationTarget.Issues, 0) : new ActivationUri(ActivationTarget.Run, summary.RunId), notification.Silent);
            }
        }

        if (summary is not null)
        {
            // After the run and its notification: a slow or failing webhook delays only the process exit.
            WebhookDispatch.SendAsync(state, database, summary).GetAwaiter().GetResult();
        }

        return exit;
    }

    /// <summary>
    /// A scheduled scan that did not run (on battery, a full-screen program, another scan): written to the bell, not
    /// toasted (subplan 13, step 4). Opens its own connection; the launcher calls it before any other work.
    /// </summary>
    internal static void RecordSkipped(AppState state, string reason)
    {
        try
        {
            using Database database = Database.Open(state.Paths.ResolveDatabaseFile(state.Settings.DatabasePath));
            RecordSkipped(database, reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            state.Log.Warning("The skipped scheduled scan could not be recorded", ex);
        }
    }

    internal static void RecordSkipped(Database database, string reason) =>
        Record(database, new RunNotification(Severity.Low, CoreStrings.Notify.Skipped, reason, Toast: false), "page:Scans");

    /// <summary>The same, from the window (a forwarded plan).</summary>
    internal static Task RecordSkippedAsync(Database database, string reason) =>
        database.Notifications.AddAsync(new NotificationRecord { Utc = DateTime.UtcNow.Ticks, Level = Severity.Low, Title = CoreStrings.Notify.Skipped, Body = reason, ActionUri = "page:Scans" });

    internal static string FinishedText() =>
        string.Format(CultureInfo.CurrentCulture, CoreStrings.Notify.FinishedAt, DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture));

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

    private static void Record(Database database, RunNotification notification, string? page = null) =>
        database.Notifications.AddAsync(new NotificationRecord { Utc = DateTime.UtcNow.Ticks, Level = notification.Level, Title = notification.Title, Body = notification.Body, ActionUri = page ?? PageOf(notification) }).Wait(TimeSpan.FromSeconds(10));

    /// <summary>Where the bell's item leads: Issues when the run found problems, Scans otherwise.</summary>
    internal static string PageOf(RunNotification notification) => notification.Level > Severity.None ? "page:Issues" : "page:Scans";

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}

/// <summary>
/// A plan ready to run, read the same way by the headless run and the window: the plan when it exists, is switched
/// on and valid, and the folders it scans (its own, or every folder that is switched on). The two copies had drifted.
/// </summary>
internal sealed record PlanJob(PlanRecord Plan, PlanDefinition Definition, IReadOnlyList<RootRecord> Roots)
{
    /// <summary>
    /// The scan request: the plan's folders at its depth, linked to the plan, within its time limit. A plan without
    /// verification (<c>includeVerification</c> false, subplan 13) gets no time for it, so no file is re-read only to
    /// compare its fingerprint; the setting was stored and never read.
    /// </summary>
    public ScanRequest Request => new(
        [.. Roots.Select(r => new ScanScope(r.Id))],
        Definition.Depth,
        RunTrigger.Scheduled,
        Plan.Id,
        Definition.BudgetMinutes is int minutes ? TimeSpan.FromMinutes(minutes) : null,
        Definition.IncludeVerification ? null : new ScanOptions { ScrubBudget = TimeSpan.Zero });

    /// <summary>The folders for the Scans heading: all folders, or the plan's folders by name.</summary>
    public string FoldersText => Definition.Roots is { Length: > 0 }
        ? UI.UiFormat.JoinNames([.. Roots.Select(r => r.Label ?? Path.GetFileName(r.Path.TrimEnd('\\')))])
        : UI.Strings.Scans.AllFolders;

    /// <summary>Null when the plan is missing, switched off or invalid. Reads the database: never on the UI thread.</summary>
    public static PlanJob? Load(Database database, long planId)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Plans.Get(planId) is not { Enabled: true } plan || PlanDefinition.FromJson(plan.DefinitionJson) is not PlanDefinition definition || definition.Validate().Count > 0)
        {
            return null;
        }

        List<RootRecord> roots = database.Roots.List();
        List<RootRecord> scanned = definition.Roots is { Length: > 0 } chosen ? [.. roots.Where(r => chosen.Contains(r.Id))] : [.. roots.Where(r => r.Enabled)];
        return new PlanJob(plan, definition, scanned);
    }
}

/// <summary>
/// A plan forwarded by a scheduled launch while the window is open (subplan 13, Headless run, step 1): it runs in
/// the window's own engine, which shows its progress. Afterwards the notification goes to the bell and, unless the
/// window is focused, to the system channel; a focused window shows it as an in-app toast. Manual runs of the window
/// are told the same way (<see cref="WatchManualRuns"/>).
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
        PlanJob? job = await Task.Run(() => PlanJob.Load(services.Database, planId)).ConfigureAwait(true);
        if (job is null || job.Roots.Count == 0)
        {
            log.Warning($"Forwarded plan {planId} is missing, disabled, invalid or has no folders");
            return;
        }

        string? skip = PlanHost.ImmediateSkipReason(job.Definition.Conditions);
        if (skip is null && services.CannotScanReason(true) is string reason)
        {
            skip = reason;
        }

        if (skip is not null)
        {
            log.Info($"Forwarded plan {planId} skipped: {skip}");
            await PlanHost.RecordSkippedAsync(services.Database, skip).ConfigureAwait(true);
            return;
        }

        void Completed(RunSummary summary)
        {
            if (summary.Trigger != RunTrigger.Scheduled || summary.PlanId != planId)
            {
                return;
            }

            services.Scans.RunCompleted -= Completed;
            _ = Task.Run(() => FinishAsync(services, job.Definition, summary, windowFocused, showInApp));
        }

        services.Scans.RunCompleted += Completed;
        try
        {
            // The Scans heading names the folders, as for any scan (it read "Scanning Nightly verification").
            _ = await services.Scans.StartAsync(job.Request, job.FoldersText).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            // Another scan took the engine first (two plans at the same minute): the bell says so (subplan 13).
            services.Scans.RunCompleted -= Completed;
            log.Warning($"Forwarded plan {planId} not started", ex);
            await PlanHost.RecordSkippedAsync(services.Database, CoreStrings.Notify.AnotherScan).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The notifications of the window's own scans (subplan 13: every notification goes to the bell; a manual run with
    /// the window focused shows an in-app toast instead of a system one). Only scheduled runs notified, so a long scan
    /// ended in a hidden window without a word whatever Settings → Notifications said. Disposing the result stops it.
    /// </summary>
    public static IDisposable WatchManualRuns(UiServices services, Func<bool> windowFocused, Action<RunNotification> showInApp)
    {
        ArgumentNullException.ThrowIfNull(services);
        void Completed(RunSummary summary)
        {
            // Started here by the user: a scan, or the Continue of an interrupted one. A forwarded plan tells its own.
            if (summary.Trigger != RunTrigger.Scheduled && !services.IsDemo)
            {
                _ = Task.Run(() => NotifyAsync(services, new PlanNotify(), summary, windowFocused, showInApp));
            }
        }

        services.Scans.RunCompleted += Completed;
        return new Subscription(() => services.Scans.RunCompleted -= Completed);
    }

    private static Task FinishAsync(UiServices services, PlanDefinition definition, RunSummary summary, Func<bool> windowFocused, Action<RunNotification> showInApp)
    {
        // The run row links itself to the plan (RunStore.BeginAsync); the plan record is not written back, which
        // undid the user's changes made during the run. Only scheduled runs post to the webhook (task N-06).
        _ = Task.Run(() => WebhookDispatch.SendAsync(services.State, services.Database, summary));
        return NotifyAsync(services, definition.Notify, summary, windowFocused, showInApp);
    }

    /// <summary>
    /// A run's notification, decided by the plan's (or a manual run's default) rules and Settings → Notifications: to
    /// the bell, and as a system toast when the window is not focused, otherwise as an in-app toast. Never throws.
    /// </summary>
    internal static async Task NotifyAsync(UiServices services, PlanNotify plan, RunSummary summary, Func<bool> windowFocused, Action<RunNotification> showInApp)
    {
        try
        {
            Core.Settings.ScanSettings settings = services.State.Settings.Scanning;
            PlanNotify notify = NotificationPolicy.WithThreshold(plan, settings.NotifyAbout);
            if (NotificationPolicy.Decide(summary, notify, new Dictionary<long, DateTimeOffset>(), DateTimeOffset.UtcNow, PlanHost.InQuietHours(settings), PlanHost.FinishedText()) is not RunNotification notification)
            {
                return;
            }

            await services.Database.Notifications.AddAsync(new NotificationRecord { Utc = DateTime.UtcNow.Ticks, Level = notification.Level, Title = notification.Title, Body = notification.Body, ActionUri = PlanHost.PageOf(notification) }).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            services.State.Log.Warning("The notification of a scan failed", ex);
        }
    }

    private sealed class Subscription(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
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
