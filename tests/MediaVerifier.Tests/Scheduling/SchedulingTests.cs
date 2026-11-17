using System.Xml.Linq;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Scheduling;

/// <summary>Plans, next-run times, task XML, reconciliation and the notification policy (subplan 13).</summary>
public sealed class SchedulingTests
{
    private static readonly DateOnly Anchor = new(2026, 11, 1);

    [Fact]
    public void Plans_round_trip_and_validate()
    {
        PlanDefinition plan = PlanDefinition.Templates[1].Definition with { Roots = [1, 4], BudgetMinutes = 90 };
        PlanDefinition back = PlanDefinition.FromJson(plan.ToJson())!;
        Assert.Equal(plan.ToJson(), back.ToJson());
        Assert.Equal([1L, 4L], back.Roots!);
        Assert.Contains("\"kind\":\"Weekly\"", plan.ToJson(), StringComparison.Ordinal);
        Assert.Empty(plan.Validate());
        Assert.All(PlanDefinition.Templates, t => Assert.Empty(t.Definition.Validate()));

        Assert.NotEmpty((plan with { Trigger = plan.Trigger with { Time = "25:00" } }).Validate());
        Assert.NotEmpty((plan with { Roots = [] }).Validate());
        Assert.NotEmpty((plan with { Trigger = plan.Trigger with { DaysOfWeek = [] } }).Validate());
        Assert.NotEmpty((plan with { Trigger = new PlanTrigger { Kind = TriggerKind.Monthly, DayOfMonth = 31 } }).Validate());
        Assert.Empty((plan with { Trigger = new PlanTrigger { Kind = TriggerKind.Monthly, DayOfMonth = 0 } }).Validate());
        Assert.NotEmpty((plan with { Trigger = new PlanTrigger { Kind = TriggerKind.EveryDays, EveryDays = 0 } }).Validate());
        Assert.Null(PlanDefinition.FromJson("{not json"));
    }

    [Fact]
    public void Next_runs_follow_each_trigger_kind()
    {
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        DateTime now = new(2026, 11, 16, 12, 0, 0); // a Monday
        Assert.Equal(new DateTime(2026, 11, 17, 3, 0, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Daily, Time = "03:00" }, now, Anchor, utc));
        Assert.Equal(new DateTime(2026, 11, 16, 13, 0, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Daily, Time = "13:00" }, now, Anchor, utc));
        Assert.Equal(new DateTime(2026, 11, 22, 3, 0, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Weekly, DaysOfWeek = [DayOfWeek.Sunday] }, now, Anchor, utc));
        Assert.Equal(new DateTime(2026, 11, 18, 3, 0, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Weekly, DaysOfWeek = [DayOfWeek.Sunday, DayOfWeek.Wednesday] }, now, Anchor, utc));
        Assert.Equal(new DateTime(2026, 11, 30, 3, 0, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Monthly, DayOfMonth = 0 }, now, Anchor, utc));
        Assert.Equal(new DateTime(2026, 12, 1, 3, 0, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Monthly, DayOfMonth = 1 }, now, Anchor, utc));
        Assert.Equal(new DateTime(2026, 12, 5, 1, 0, 0), ScheduleCalculator.Next(PlanDefinition.Templates[2].Definition.Trigger, now, Anchor, utc)); // first Saturday
        Assert.Equal(new DateTime(2026, 11, 28, 1, 0, 0), ScheduleCalculator.Next(PlanDefinition.Templates[2].Definition.Trigger with { WeekOfMonth = 0 }, now, Anchor, utc)); // last Saturday
        Assert.Equal(new DateTime(2026, 11, 19, 3, 0, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.EveryDays, EveryDays = 3 }, now, Anchor, utc));
        Assert.Equal(new DateTime(2027, 2, 28, 3, 0, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Monthly, DayOfMonth = 0 }, new DateTime(2027, 2, 1), Anchor, utc));
    }

    [Fact]
    public void A_time_skipped_by_daylight_saving_moves_to_the_first_valid_minute()
    {
        TimeZoneInfo europe = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        DateTime before = new(2026, 3, 28, 12, 0, 0); // the clocks go forward at 02:00 on 29 March 2026
        DateTime? next = ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Daily, Time = "02:30" }, before, Anchor, europe);
        Assert.Equal(new DateTime(2026, 3, 29, 3, 0, 0), next);
        Assert.Equal(new DateTime(2026, 3, 30, 2, 30, 0), ScheduleCalculator.Next(new PlanTrigger { Kind = TriggerKind.Daily, Time = "02:30" }, next!.Value, Anchor, europe));
    }

    [Fact]
    public void Task_xml_follows_the_schema_and_escapes_text()
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string weekly = ScheduleXmlBuilder.Build("Weekly <check> & \"more\"", PlanDefinition.Templates[1].Definition, 3, @"C:\Apps\Media & Co\MediaVerifier.exe", new DateOnly(2026, 1, 4));
        XDocument doc = XDocument.Parse(weekly);
        Assert.Equal("1.2", doc.Root!.Attribute("version")!.Value);
        Assert.Equal("MediaVerifier scheduled scan: Weekly <check> & \"more\"", doc.Descendants(ns + "Description").Single().Value);
        Assert.Equal("2026-01-04T03:00:00", doc.Descendants(ns + "StartBoundary").Single().Value);
        Assert.NotNull(doc.Descendants(ns + "ScheduleByWeek").Single().Element(ns + "DaysOfWeek")!.Element(ns + "Sunday"));
        Assert.Equal(@"C:\Apps\Media & Co\MediaVerifier.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--run-plan 3", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("PT8H", doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Contains("&amp;", weekly, StringComparison.Ordinal);
        Assert.Contains("&lt;check&gt;", weekly, StringComparison.Ordinal);

        XDocument daily = XDocument.Parse(ScheduleXmlBuilder.Build("n", PlanDefinition.Templates[0].Definition, 1, "x.exe", Anchor));
        Assert.Equal("1", daily.Descendants(ns + "DaysInterval").Single().Value);
        XDocument monthly = XDocument.Parse(ScheduleXmlBuilder.Build("n", PlanDefinition.Templates[2].Definition, 1, "x.exe", Anchor));
        Assert.Equal("1", monthly.Descendants(ns + "Week").Single().Value);
        Assert.Equal(12, monthly.Descendants(ns + "Months").Single().Elements().Count());
        XDocument last = XDocument.Parse(ScheduleXmlBuilder.Build("n", new PlanDefinition { Trigger = new PlanTrigger { Kind = TriggerKind.Monthly, DayOfMonth = 0 } }, 1, "x.exe", Anchor));
        Assert.Equal("Last", last.Descendants(ns + "Day").Single().Value);
    }

    [Fact]
    public async Task Reconciliation_registers_enabled_plans_and_removes_the_rest()
    {
        using TestDatabase db = new();
        long weekly = await db.Database.Plans.AddAsync(new PlanRecord { Name = "Weekly check", DefinitionJson = PlanDefinition.Templates[1].Definition.ToJson() });
        await db.Database.Plans.AddAsync(new PlanRecord { Name = "Off", Enabled = false, DefinitionJson = PlanDefinition.Templates[0].Definition.ToJson() });
        FakeBackend backend = new();
        backend.Tasks["MediaVerifier\\Plan 99"] = "<old/>";
        await PlanReconciler.ReconcileAsync(db.Database, backend, @"D:\New place\MediaVerifier.exe", Anchor);
        Assert.Equal(["MediaVerifier\\Plan " + weekly], backend.Tasks.Keys.Order());
        Assert.Contains(@"D:\New place\MediaVerifier.exe", backend.Tasks.Single().Value, StringComparison.Ordinal);
        Assert.Equal("MediaVerifier\\Plan " + weekly, db.Database.Plans.Get(weekly)!.TaskName);

        PlanReconciler.RemoveAll(backend);
        Assert.Empty(backend.Tasks);
    }

    [Theory]
    [InlineData(1, 0, 0, NotifyWhen.NewFindings, Severity.Medium, true, Severity.Critical)]
    [InlineData(0, 2, 0, NotifyWhen.NewFindings, Severity.Medium, true, Severity.High)]
    [InlineData(0, 0, 3, NotifyWhen.NewFindings, Severity.High, false, Severity.None)]
    [InlineData(1, 0, 0, NotifyWhen.Never, Severity.Medium, true, Severity.Critical)]
    [InlineData(0, 0, 0, NotifyWhen.Always, Severity.Medium, true, Severity.None)]
    [InlineData(0, 0, 0, NotifyWhen.NewFindings, Severity.Medium, false, Severity.None)]
    public void The_policy_decides_one_notification(int critical, int high, int medium, NotifyWhen when, Severity min, bool expected, Severity level)
    {
        RunSummary run = Run(new SeverityCounts(critical, high, medium, 0), []);
        RunNotification? result = NotificationPolicy.Decide(run, new PlanNotify { When = when, MinSeverity = min }, new Dictionary<long, DateTimeOffset>(), DateTimeOffset.UtcNow, false, "Scan finished 03:12.");
        Assert.Equal(expected, result is not null);
        if (result is not null)
        {
            Assert.Equal(level, result.Level);
            Assert.Equal(when != NotifyWhen.Never, result.Toast);
        }
    }

    [Fact]
    public void Failures_alerts_offline_roots_and_quiet_hours_follow_the_table()
    {
        PlanNotify notify = new();
        DateTimeOffset now = new(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);
        RunSummary failed = Run(SeverityCounts.Zero, []) with { Status = RunStatus.Failed, Error = "Database locked" };
        Assert.Equal(CoreStrings.Notify.CouldNotRun, NotificationPolicy.Decide(failed, notify, new Dictionary<long, DateTimeOffset>(), now, false, string.Empty)!.Title);

        RunSummary errors = Run(SeverityCounts.Zero, [new RunAlert("RUN-READ-ERRORS", Severity.High, 1, "Drive D: returned read errors on 4 files")]);
        Assert.True(NotificationPolicy.Decide(errors, notify, new Dictionary<long, DateTimeOffset>(), now, false, string.Empty)!.Toast);

        RunSummary offline = Run(SeverityCounts.Zero, [new RunAlert("RUN-ROOT-OFFLINE", Severity.Medium, 7, "Music is offline")]);
        Assert.True(NotificationPolicy.Decide(offline, notify, new Dictionary<long, DateTimeOffset>(), now, false, string.Empty)!.Toast);
        Assert.False(NotificationPolicy.Decide(offline, notify, new Dictionary<long, DateTimeOffset> { [7] = now.AddDays(-3) }, now, false, string.Empty)!.Toast);
        Assert.True(NotificationPolicy.Decide(offline, notify, new Dictionary<long, DateTimeOffset> { [7] = now.AddDays(-8) }, now, false, string.Empty)!.Toast);

        RunSummary findings = Run(new SeverityCounts(0, 1, 2, 0), []);
        RunNotification quiet = NotificationPolicy.Decide(findings, notify, new Dictionary<long, DateTimeOffset>(), now, quietHours: true, "Scan finished 03:12.")!;
        Assert.True(quiet.Toast);
        Assert.True(quiet.Silent);
        Assert.Equal("3 new problems found", quiet.Title);
        Assert.Equal("1 high, 2 medium. Scan finished 03:12.", quiet.Body);
        Assert.False(NotificationPolicy.Decide(findings, notify, new Dictionary<long, DateTimeOffset>(), now, quietHours: false, string.Empty)!.Silent);
        Assert.False(NotificationPolicy.Decide(findings, new PlanNotify { When = NotifyWhen.Never }, new Dictionary<long, DateTimeOffset>(), now, quietHours: true, string.Empty)?.Silent ?? false);
    }

    [Theory]
    [InlineData(true, "22:00", "07:00", "23:30", true)]
    [InlineData(true, "22:00", "07:00", "06:59", true)]
    [InlineData(true, "22:00", "07:00", "07:00", false)]
    [InlineData(true, "22:00", "07:00", "12:00", false)]
    [InlineData(true, "9:00", "17:30", "12:00", true)]
    [InlineData(true, "09:00", "17:30", "17:30", false)]
    [InlineData(false, "22:00", "07:00", "23:30", false)]
    [InlineData(true, "25:00", "07:00", "23:30", false)]
    [InlineData(true, "22:00", "22:00", "22:00", false)]
    [InlineData(true, null, "07:00", "03:00", false)]
    public void Quiet_hours_wrap_midnight_and_ignore_unreadable_times(bool enabled, string? from, string? to, string now, bool expected) =>
        Assert.Equal(expected, NotificationPolicy.InQuietHours(enabled, from, to, TimeOnly.ParseExact(now, "HH:mm", System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(Severity.Medium, MediaVerifier.Core.Settings.NotifyThreshold.CriticalOnly, Severity.Critical)]
    [InlineData(Severity.Medium, MediaVerifier.Core.Settings.NotifyThreshold.Everything, Severity.Medium)]
    [InlineData(Severity.Low, MediaVerifier.Core.Settings.NotifyThreshold.HighAndAbove, Severity.High)]
    [InlineData(Severity.Critical, MediaVerifier.Core.Settings.NotifyThreshold.MediumAndAbove, Severity.Critical)]
    public void The_global_threshold_only_makes_a_plan_stricter(Severity plan, MediaVerifier.Core.Settings.NotifyThreshold global, Severity expected) =>
        Assert.Equal(expected, NotificationPolicy.WithThreshold(new PlanNotify { MinSeverity = plan }, global).MinSeverity);

    private static RunSummary Run(SeverityCounts counts, IReadOnlyList<RunAlert> alerts) =>
        new(1, RunStatus.Completed, RunTrigger.Scheduled, 1, new RunCounters(0, 0, 0, 0, 0, 0, 0), counts, alerts, TimeSpan.FromMinutes(5), null);

    private sealed class FakeBackend : IScheduleBackend
    {
        public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Register(long planId, string xml)
        {
            string name = "MediaVerifier\\" + TaskSchedulerBackend.TaskName(planId);
            Tasks[name] = xml;
            return name;
        }

        public void Delete(string taskName) => Tasks.Remove(taskName);

        public IReadOnlyList<string> List() => [.. Tasks.Keys];
    }
}
