using System.Globalization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.UI.Data;

/// <summary>
/// One plan row of the Schedule page (subplan 09, Schedule). <paramref name="ResultTier"/> is the pill's tier, or null
/// for a neutral pill; <paramref name="Anchor"/> is the first day of an every-n-days rhythm (the day it was created).
/// </summary>
internal sealed record PlanRow(long Id, string Name, bool Enabled, string Summary, string NextRun, string LastResult, Severity? ResultTier, PlanDefinition Definition, DateOnly Anchor);

/// <summary>
/// The Schedule page: plans and the verification card. The full-pass estimate is worked out on the page from
/// <paramref name="PassBytes"/> and <paramref name="ReadRate"/> (bytes a second), so it follows the budget as it is
/// typed; <paramref name="BaselineFiles"/> is 0 before any file has a fingerprint, when there is nothing to estimate.
/// </summary>
internal sealed record ScheduleData(IReadOnlyList<PlanRow> Plans, double VerifiedShare, long OverdueFiles, long BaselineFiles, long PassBytes, double ReadRate)
{
    /// <summary>The read rate assumed until a scan has measured one.</summary>
    public const double AssumedReadRate = 100_000_000;

    /// <summary>Days for one full verification pass at a nightly budget.</summary>
    public int FullPassDays(int budgetMinutes)
    {
        double perNight = budgetMinutes * 60.0 * (ReadRate > 0 ? ReadRate : AssumedReadRate);
        return BaselineFiles == 0 || perNight <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(PassBytes / perNight));
    }
}

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
        string scope = plan.Roots is { Length: > 0 } roots
            ? string.Join(", ", roots.Select(id => rootNames.TryGetValue(id, out string? name) ? name : string.Empty).Where(n => n.Length > 0))
            : Strings.Schedule.AllFoldersLower;
        string limit = plan.BudgetMinutes is int minutes
            ? minutes % 60 == 0 ? string.Format(Culture, Strings.Schedule.UpToHours, minutes / 60) : string.Format(Culture, Strings.Schedule.UpToMinutes, minutes)
            : Strings.Schedule.NoTimeLimit;
        return UiFormat.Facts(When(plan.Trigger), UiFormat.DepthName(plan.Depth), scope, limit);
    }

    /// <summary>
    /// The when phrase (subplan 09): <c>Every day at 03:00</c>, <c>Every Sunday at 03:00</c>, several weekdays as
    /// <c>Every Mon and Thu at 03:00</c>.
    /// </summary>
    public static string When(PlanTrigger t)
    {
        ArgumentNullException.ThrowIfNull(t);
        string time = t.TimeOfDay.ToString("HH:mm", Culture);
        return t.Kind switch
        {
            TriggerKind.Daily => string.Format(Culture, Strings.Schedule.EveryDay, time),
            TriggerKind.Weekly => string.Format(Culture, Strings.Schedule.EveryWeekday, Weekdays(t.DaysOfWeek), time),
            TriggerKind.Monthly when t.WeekOfMonth is int week => string.Format(Culture, week == 0 ? Strings.Schedule.LastWeekdayOfMonth : Strings.Schedule.NthWeekdayOfMonth, Ordinal(week), t.DaysOfWeek.Length == 1 ? Culture.DateTimeFormat.GetDayName(t.DaysOfWeek[0]) : string.Empty, time),
            TriggerKind.Monthly when t.DayOfMonth is 0 => string.Format(Culture, Strings.Schedule.LastDayOfMonth, time),
            TriggerKind.Monthly => string.Format(Culture, Strings.Schedule.DayOfMonth, t.DayOfMonth ?? 1, time),
            _ => string.Format(Culture, Strings.Schedule.EveryNDays, t.EveryDays ?? 1, time),
        };
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

    /// <summary>
    /// The Last result pill: <c>Never run</c>, <c>Running</c>, or the result pill Scans shows for the same run (an
    /// interrupted run read Failed in High colours here and Interrupted on Scans). A null tier is a neutral pill.
    /// </summary>
    public static (string Text, Severity? Tier) Result(RunStatus? status) => status switch
    {
        null => (Strings.Schedule.NeverRun, null),
        RunStatus.Running => (Strings.Schedule.Running, null),
        RunStatus s => (ScansPage.ResultText(s), ScansPage.ResultTier(s)),
    };

    /// <summary>One weekday by its name; several by their short names, joined as a sentence.</summary>
    private static string Weekdays(DayOfWeek[] days) => days.Length == 1
        ? Culture.DateTimeFormat.GetDayName(days[0])
        : UiFormat.JoinNames([.. days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => Culture.DateTimeFormat.GetAbbreviatedDayName(d))]);

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
            (string result, Severity? tier) = ScheduleText.Result(status);
            DateOnly anchor = PlanReconciler.AnchorOf(plan, DateOnly.FromDateTime(now));
            rows.Add(new PlanRow(plan.Id, plan.Name, plan.Enabled, ScheduleText.Summary(definition, roots), ScheduleText.NextRun(definition, plan.Enabled, now, anchor), result, tier, definition, anchor));
        }

        Core.Settings.ScanSettings scan = settings();
        HealthSnapshot health = database.Queries.Health(TimeSpan.FromDays(scan.ScrubPeriodDays), clock.UtcNow);
        double share = health.FilesWithBaseline == 0 ? 0 : (double)health.FilesVerifiedInPeriod / health.FilesWithBaseline;
        long overdue = Math.Max(0, health.FilesWithBaseline - health.FilesVerifiedInPeriod);
        long bytes = database.Queries.KindSummaries().Sum(k => k.Bytes);

        // The read rate the engine measured (it keeps a smoothed average between scans); 100 MB/s until it has one.
        double rate = Estimator.Deserialize(database.Meta.Get(ScanEngine.EstimatesKey)).Values.Select(a => a.BytesPerSecond).FirstOrDefault(r => r > 0);
        return new ScheduleData(rows, share, overdue, health.FilesWithBaseline, bytes, rate);
    }
}
