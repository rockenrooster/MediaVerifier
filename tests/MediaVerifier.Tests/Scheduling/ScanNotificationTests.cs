using System.Collections.Concurrent;
using System.Diagnostics;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Scheduling;

/// <summary>
/// What a finished scan tells the user, a scan started in the window included (subplan 13, Notification policy), and
/// the plan details a scheduled run follows. In the UI collection: the window's services run on a test thread.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ScanNotificationTests
{
    /// <summary>A scan the user started is not called a scheduled one: "Scan could not run", "Scan finished".</summary>
    [Fact]
    public void A_scan_the_user_started_is_not_called_scheduled()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Dictionary<long, DateTimeOffset> none = [];
        RunSummary manual = Summary(RunTrigger.Manual, SeverityCounts.Zero) with { Status = RunStatus.Failed, Error = "The database is locked" };
        RunSummary resumed = Summary(RunTrigger.Resume, SeverityCounts.Zero);
        RunSummary scheduled = Summary(RunTrigger.Scheduled, SeverityCounts.Zero) with { Status = RunStatus.Failed };

        Assert.Equal(CoreStrings.Notify.ScanCouldNotRun, NotificationPolicy.Decide(manual, new PlanNotify(), none, now, false, string.Empty)!.Title);
        Assert.Equal(CoreStrings.Notify.ScanFinished, NotificationPolicy.Decide(resumed, new PlanNotify { When = NotifyWhen.Always }, none, now, false, string.Empty)!.Title);
        Assert.Equal(CoreStrings.Notify.CouldNotRun, NotificationPolicy.Decide(scheduled, new PlanNotify(), none, now, false, string.Empty)!.Title);
    }

    /// <summary>A manual scan that found a new problem goes to the bell and, with the window focused, to an in-app toast.</summary>
    [Fact]
    public void A_manual_scan_with_a_new_problem_reaches_the_bell_and_the_window()
    {
        List<NotificationRecord> bell = [];
        ConcurrentQueue<RunNotification> shown = new();
        Sta.Run(() =>
        {
            using TempDirectory temp = new("manual-notify");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.Scanning.ShowWindowsNotifications = false;
            using UiServices services = Live(state, temp);
            Task.Run(() => GuiPlanRunner.NotifyAsync(services, new PlanNotify(), Summary(RunTrigger.Manual, new SeverityCounts(1, 0, 0, 0)), () => true, shown.Enqueue)).GetAwaiter().GetResult();
            bell = Task.Run(() => services.Database.Notifications.List()).GetAwaiter().GetResult();
        });

        NotificationRecord note = Assert.Single(bell);
        Assert.Equal("1 new problem found", note.Title);
        Assert.Equal(Severity.Critical, note.Level);
        Assert.Equal("page:Issues", note.ActionUri);
        Assert.Equal("1 new problem found", Assert.Single(shown).Title);
    }

    /// <summary>
    /// The window's own scan is told when it ends (a folder that is not connected, here): only scheduled runs were, so a
    /// long scan ended in a hidden window without a word. After the subscription ends, nothing more is told.
    /// </summary>
    [Fact]
    public void The_windows_own_scan_is_told_when_it_ends()
    {
        List<NotificationRecord> bell = [];
        ConcurrentQueue<RunNotification> shown = new();
        Sta.Run(() =>
        {
            using TempDirectory temp = new("manual-watch");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.Scanning.ShowWindowsNotifications = false;
            using UiServices services = Live(state, temp);
            long root = Task.Run(() => services.Database.Roots.AddAsync(new RootRecord { Path = temp.Combine("Unplugged"), Label = "Unplugged", Role = RootRole.Library, MediaMask = MediaKindMask.All })).GetAwaiter().GetResult();
            using (GuiPlanRunner.WatchManualRuns(services, () => true, shown.Enqueue))
            {
                _ = services.Scans.StartAsync(new ScanRequest([new ScanScope(root)], ScanDepth.Quick, RunTrigger.Manual), "Unplugged");
                Settle(() => !shown.IsEmpty, TimeSpan.FromSeconds(30));
                Settle(() => !services.Scans.Status.IsActive, TimeSpan.FromSeconds(10));
            }

            bell = Task.Run(() => services.Database.Notifications.List()).GetAwaiter().GetResult();
        });

        RunNotification told = Assert.Single(shown);
        Assert.Equal(Severity.Medium, told.Level);
        Assert.Contains(bell, n => n.Title == told.Title);
    }

    /// <summary>A plan without verification gets no time for it (<c>includeVerification</c> was stored and never read).</summary>
    [Fact]
    public void A_plan_without_verification_gets_no_time_for_it()
    {
        PlanRecord record = new() { Id = 5, Name = "Nightly", Enabled = true, DefinitionJson = PlanDefinition.Templates[0].Definition.ToJson() };
        PlanDefinition nightly = PlanDefinition.Templates[0].Definition;
        Assert.Null(new PlanJob(record, nightly, []).Request.Options);
        Assert.Equal(TimeSpan.Zero, new PlanJob(record, nightly with { IncludeVerification = false }, []).Request.Options?.ScrubBudget);
        Assert.Equal(TimeSpan.FromMinutes(120), new PlanJob(record, nightly, []).Request.Budget);
    }

    /// <summary>Two plans at the same minute meet on a day both fire: daily and Sunday do, Monday and Sunday never do.</summary>
    [Fact]
    public void Plans_at_the_same_minute_fire_together_on_a_shared_day()
    {
        DateOnly from = new(2026, 11, 18);
        PlanTrigger daily = new() { Kind = TriggerKind.Daily, Time = "03:00" };
        PlanTrigger sunday = new() { Kind = TriggerKind.Weekly, Time = "03:00", DaysOfWeek = [DayOfWeek.Sunday] };
        PlanTrigger monday = sunday with { DaysOfWeek = [DayOfWeek.Monday] };
        PlanTrigger fifth = new() { Kind = TriggerKind.Monthly, Time = "03:00", DayOfMonth = 5 };
        PlanTrigger everyOther = new() { Kind = TriggerKind.EveryDays, Time = "03:00", EveryDays = 2 };

        Assert.True(ScheduleCalculator.FireTogether(daily, from, sunday, from, from));
        Assert.False(ScheduleCalculator.FireTogether(daily, from, sunday with { Time = "05:00" }, from, from));
        Assert.False(ScheduleCalculator.FireTogether(monday, from, sunday, from, from));
        Assert.True(ScheduleCalculator.FireTogether(fifth, from, sunday, from, from));
        Assert.False(ScheduleCalculator.FireTogether(everyOther, from, everyOther, from.AddDays(1), from));
    }

    private static RunSummary Summary(RunTrigger trigger, SeverityCounts counts) =>
        new(1, RunStatus.Completed, trigger, null, new RunCounters(0, 0, 0, 0, 0, 0, 0), counts, [], TimeSpan.FromMinutes(5), null);

    private static UiServices Live(AppState state, TempDirectory temp) =>
        UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N"));

    private static void Settle(Func<bool> done, TimeSpan limit)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < limit)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
