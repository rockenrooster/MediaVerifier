using System.Xml.Linq;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Ipc;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Scheduling;

/// <summary>
/// A plan run without the window, and the scheduled tasks it is started by (subplan 13). Everything uses names of the
/// test's own (engine mutex, stop event) and the in-memory Task Scheduler, so an open MediaVerifier is never disturbed
/// and nothing is registered with Windows. In the UI collection, with the Schedule page tests that share that scheduler.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PlanRunTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// The launcher holds the engine mutex for the whole run, and the scan still runs: the engine took the same mutex
    /// again from this process, was refused, and no scheduled scan ever ran while the window was closed. The run is
    /// handed the launcher's own mutex, so an engine that took it again fails here. The run row is linked to the plan,
    /// and the plan record is not written back (that undid edits made during the run).
    /// </summary>
    [Fact]
    public async Task A_plan_runs_while_the_launcher_holds_the_engine_mutex()
    {
        using TempDirectory temp = new("headless-plan");
        string library = Directory.CreateDirectory(temp.Combine("Library")).FullName;
        File.WriteAllText(Path.Combine(library, "notes.txt"), "not a media file");
        AppPaths paths = temp.CreateAppPaths();
        using AppState state = AppState.Load(paths, installSharedLog: false);
        state.Settings.Scanning.ShowWindowsNotifications = false;
        string databaseFile = paths.ResolveDatabaseFile(state.Settings.DatabasePath);
        long planId;
        long updatedBefore;
        using (Database database = Database.Open(databaseFile))
        {
            _ = await database.Roots.AddAsync(new RootRecord { Path = library, Label = "Library", Role = RootRole.Library, MediaMask = MediaKindMask.All });
            planId = await database.Plans.AddAsync(new PlanRecord { Name = "Nightly", Enabled = true, DefinitionJson = Quiet().ToJson() });
            updatedBefore = database.Plans.Get(planId)!.UpdatedUtc;
        }

        string engine = TestName("Engine");
        int exit = HeadlessLauncher.RunHere(state, planId, engine, (s, id) =>
        {
            // The launcher's lock is held here, as in a real run.
            Assert.True(NamedMutexLock.IsHeldElsewhere(engine));
            return PlanHost.RunPlan(s, id, TestName("StopRun"), engine, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false));
        });

        using Database check = Database.Open(databaseFile);
        RunRecord run = Assert.Single(check.Runs.List(5));
        PlanRecord plan = check.Plans.Get(planId)!;
        Assert.Equal(HeadlessLauncher.ExitSuccess, exit);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(planId, run.PlanId);
        Assert.Equal(run.Id, plan.LastRunId);
        Assert.Equal(updatedBefore, plan.UpdatedUtc);
    }

    /// <summary>Another run holds the engine (two plans due at the same minute): this one exits 3 and the bell says it was skipped.</summary>
    [Fact]
    public void A_plan_that_finds_the_engine_busy_is_recorded_as_skipped()
    {
        using TempDirectory temp = new("headless-busy");
        AppPaths paths = temp.CreateAppPaths();
        using AppState state = AppState.Load(paths, installSharedLog: false);
        string engine = TestName("Engine");
        bool ran = false;
        int exit;
        using (NamedMutexLock? other = NamedMutexLock.TryAcquire(engine, TimeSpan.Zero))
        {
            Assert.NotNull(other);
            exit = HeadlessLauncher.RunHere(state, 1, engine, (_, _) =>
            {
                ran = true;
                return HeadlessLauncher.ExitSuccess;
            });
        }

        using Database check = Database.Open(paths.ResolveDatabaseFile(state.Settings.DatabasePath));
        NotificationRecord note = Assert.Single(check.Notifications.List());
        Assert.Equal(HeadlessLauncher.ExitForwarded, exit);
        Assert.False(ran);
        Assert.Equal(CoreStrings.Notify.Skipped, note.Title);
        Assert.Equal(CoreStrings.Notify.AnotherScan, note.Body);
        Assert.Equal(Severity.Low, note.Level);
    }

    /// <summary>
    /// An every-3-days plan starts on the day it was created, whenever the app reconciles: anchored on the day of each
    /// reconcile (every app start), its next run kept moving and, with a daily start, it never fired.
    /// </summary>
    [Fact]
    public async Task Every_n_days_tasks_start_on_the_day_the_plan_was_created()
    {
        using TestDatabase db = new();
        PlanDefinition everyThree = new() { Trigger = new PlanTrigger { Kind = TriggerKind.EveryDays, EveryDays = 3, Time = "03:00" } };
        long id = await db.Database.Plans.AddAsync(new PlanRecord { Name = "Every 3 days", DefinitionJson = everyThree.ToJson() });
        DateOnly created = DateOnly.FromDateTime(db.Clock.UtcNow.UtcDateTime.ToLocalTime());
        RecordingBackend backend = new();

        // The app starts five days later.
        await PlanReconciler.ReconcileAsync(db.Database, backend, "x.exe", created.AddDays(5));
        XDocument xml = XDocument.Parse(backend.Tasks.Single().Value);
        Assert.Equal(created.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "T03:00:00", xml.Descendants(Ns + "StartBoundary").Single().Value);
        Assert.Equal(created, PlanReconciler.AnchorOf(db.Database.Plans.Get(id)!, created.AddDays(5)));
    }

    /// <summary>An edit saved while the reconciler registers a task survives: the task name went onto the snapshot, which switched the plan back on.</summary>
    [Fact]
    public async Task The_task_name_is_written_onto_the_row_as_it_is_now()
    {
        using TestDatabase db = new();
        long id = await db.Database.Plans.AddAsync(new PlanRecord { Name = "Weekly check", DefinitionJson = PlanDefinition.Templates[1].Definition.ToJson() });
        RecordingBackend backend = new()
        {
            // The user switches the plan off while Windows registers its task.
            OnRegister = () =>
            {
                PlanRecord now = db.Database.Plans.Get(id)!;
                db.Database.Plans.UpdateAsync(now with { Enabled = false }).GetAwaiter().GetResult();
            },
        };

        await PlanReconciler.ReconcileAsync(db.Database, backend, "x.exe", new DateOnly(2026, 11, 1));
        PlanRecord plan = db.Database.Plans.Get(id)!;
        Assert.False(plan.Enabled, "The reconciler switched the plan back on.");
        Assert.Equal("MediaVerifier\\Plan " + id, plan.TaskName);
    }

    /// <summary>A plan that cannot be run removes its orphan task through the scheduler in use (here the fake).</summary>
    [Fact]
    public void A_missing_plan_removes_its_task()
    {
        using TempDirectory temp = new("headless-missing");
        AppPaths paths = temp.CreateAppPaths();
        using AppState state = AppState.Load(paths, installSharedLog: false);
        TestScheduler.Backend.Tasks["MediaVerifier\\Plan 4242"] = "<old/>";
        int exit = PlanHost.RunPlan(state, 4242, TestName("StopRun"), TestName("Engine"), new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false));
        Assert.Equal(HeadlessLauncher.ExitFailure, exit);
        Assert.False(TestScheduler.Backend.Tasks.ContainsKey("MediaVerifier\\Plan 4242"));
    }

    /// <summary>A plan that runs at once and tells nobody: no power or full-screen condition, no notification.</summary>
    private static PlanDefinition Quiet() => new()
    {
        Depth = ScanDepth.Quick,
        Trigger = new PlanTrigger { Kind = TriggerKind.Daily, Time = "03:00" },
        Conditions = new PlanConditions { OnlyOnAcPower = false, SkipWhenFullScreen = false },
        Notify = new PlanNotify { When = NotifyWhen.Never },
    };

    private static string TestName(string what) => @"Local\MediaVerifier." + what + ".Test." + Guid.NewGuid().ToString("N");

    private sealed class RecordingBackend : IScheduleBackend
    {
        public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Action? OnRegister { get; init; }

        public string Register(long planId, string xml)
        {
            OnRegister?.Invoke();
            string name = "MediaVerifier\\" + TaskSchedulerBackend.TaskName(planId);
            Tasks[name] = xml;
            return name;
        }

        public void Delete(string taskName) => Tasks.Remove(taskName);

        public IReadOnlyList<string> List() => [.. Tasks.Keys];
    }
}
