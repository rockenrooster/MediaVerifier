using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>One plan row of the Schedule page (subplan 09, Schedule).</summary>
internal sealed record PlanRow(long Id, string Name, bool Enabled, string Summary, string NextRun, string LastResult, Severity ResultTier, PlanDefinition Definition);

/// <summary>The Schedule page: plans and the verification card.</summary>
internal sealed record ScheduleData(IReadOnlyList<PlanRow> Plans, double VerifiedShare, long OverdueFiles, int FullPassDays);

internal interface IScheduleData
{
    ScheduleData LoadSchedule();
}

/// <summary>Wording shared by the live and demo schedule data.</summary>
internal static class ScheduleText
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary><c>Every day at 03:00 · Quick · all folders · up to 2 h</c>.</summary>
    public static string Summary(PlanDefinition plan, IReadOnlyDictionary<long, string> rootNames)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PlanTrigger t = plan.Trigger;
        string time = t.TimeOfDay.ToString("HH:mm", Culture);
        string when = t.Kind switch
        {
            TriggerKind.Daily => string.Format(Culture, Strings.Schedule.EveryDay, time),
            TriggerKind.Weekly => string.Format(Culture, Strings.Schedule.EveryWeekday, string.Join(", ", t.DaysOfWeek.Select(d => Culture.DateTimeFormat.GetDayName(d))), time),
            TriggerKind.Monthly when t.WeekOfMonth is int week => string.Format(Culture, week == 0 ? Strings.Schedule.LastWeekdayOfMonth : Strings.Schedule.NthWeekdayOfMonth, Ordinal(week), t.DaysOfWeek.Length == 1 ? Culture.DateTimeFormat.GetDayName(t.DaysOfWeek[0]) : string.Empty, time),
            TriggerKind.Monthly when t.DayOfMonth is 0 => string.Format(Culture, Strings.Schedule.LastDayOfMonth, time),
            TriggerKind.Monthly => string.Format(Culture, Strings.Schedule.DayOfMonth, t.DayOfMonth ?? 1, time),
            _ => string.Format(Culture, Strings.Schedule.EveryNDays, t.EveryDays ?? 1, time),
        };
        string scope = plan.Roots is { Length: > 0 } roots
            ? string.Join(", ", roots.Select(id => rootNames.TryGetValue(id, out string? name) ? name : string.Empty).Where(n => n.Length > 0))
            : Strings.Schedule.AllFoldersLower;
        string limit = plan.BudgetMinutes is int minutes
            ? minutes % 60 == 0 ? string.Format(Culture, Strings.Schedule.UpToHours, minutes / 60) : string.Format(Culture, Strings.Schedule.UpToMinutes, minutes)
            : Strings.Schedule.NoTimeLimit;
        return UiFormat.Facts(when, UiFormat.DepthName(plan.Depth), scope, limit);
    }

    /// <summary><c>Tomorrow 03:00</c>, <c>Sun 03:00</c>, <c>12 Oct 03:00</c>; <c>Off</c> for a disabled plan.</summary>
    public static string NextRun(PlanDefinition plan, bool enabled, DateTime localNow, DateOnly anchor)
    {
        if (!enabled || ScheduleCalculator.Next(plan.Trigger, localNow, anchor) is not DateTime next)
        {
            return Strings.Schedule.Off;
        }

        string time = next.ToString("HH:mm", Culture);
        int days = DateOnly.FromDateTime(next).DayNumber - DateOnly.FromDateTime(localNow).DayNumber;
        return days switch
        {
            0 => string.Format(Culture, Strings.Schedule.TodayAt, time),
            1 => string.Format(Culture, Strings.Schedule.TomorrowAt, time),
            < 7 => next.ToString("ddd", Culture) + " " + time,
            _ => next.ToString("d MMM", Culture) + " " + time,
        };
    }

    public static (string Text, Severity Tier) Result(RunStatus? status) => status switch
    {
        null => (Strings.Schedule.NeverRun, Severity.None),
        RunStatus.Completed or RunStatus.BudgetReached => (Strings.Scans.Completed, Severity.None),
        RunStatus.Running => (Strings.Schedule.Running, Severity.Low),
        RunStatus.Cancelled => (Strings.Scans.Stopped, Severity.Medium),
        _ => (Strings.Schedule.Failed, Severity.High),
    };

    private static string Ordinal(int week) => week switch
    {
        1 => Strings.Schedule.First,
        2 => Strings.Schedule.Second,
        3 => Strings.Schedule.Third,
        4 => Strings.Schedule.Fourth,
        _ => Strings.Schedule.Last,
    };
}

internal sealed partial class LiveUiData : IScheduleData
{
    public ScheduleData LoadSchedule()
    {
        Dictionary<long, string> roots = database.Roots.List().ToDictionary(r => r.Id, r => r.Label ?? Path.GetFileName(r.Path.TrimEnd('\\')));
        DateTime now = clock.UtcNow.LocalDateTime;
        List<PlanRow> rows = [];
        foreach (PlanRecord plan in database.Plans.List().OrderBy(p => p.Id))
        {
            if (PlanDefinition.FromJson(plan.DefinitionJson) is not PlanDefinition definition)
            {
                continue;
            }

            RunStatus? status = plan.LastRunId is long runId ? database.Runs.Get(runId)?.Status : null;
            (string result, Severity tier) = ScheduleText.Result(status);
            DateOnly anchor = DateOnly.FromDateTime(new DateTime(plan.CreatedUtc, DateTimeKind.Utc).ToLocalTime());
            rows.Add(new PlanRow(plan.Id, plan.Name, plan.Enabled, ScheduleText.Summary(definition, roots), ScheduleText.NextRun(definition, plan.Enabled, now, anchor), result, tier, definition));
        }

        Core.Settings.ScanSettings scan = settings();
        HealthSnapshot health = database.Queries.Health(TimeSpan.FromDays(scan.ScrubPeriodDays), clock.UtcNow);
        double share = health.FilesWithBaseline == 0 ? 0 : (double)health.FilesVerifiedInPeriod / health.FilesWithBaseline;
        long overdue = Math.Max(0, health.FilesWithBaseline - health.FilesVerifiedInPeriod);
        return new ScheduleData(rows, share, overdue, FullPassDays(health.FilesWithBaseline, scan.ScrubBudgetMinutes));
    }

    /// <summary>Days for one full verification pass at the nightly budget, from the stored throughput (about 100 MB/s when unknown).</summary>
    private int FullPassDays(long files, int budgetMinutes)
    {
        long bytes = database.Queries.KindSummaries().Sum(k => k.Bytes);
        double perNight = budgetMinutes * 60.0 * 100_000_000;
        return files == 0 || perNight <= 0 ? 0 : (int)Math.Ceiling(bytes / perNight);
    }
}
