using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.UI.Data;

namespace MediaVerifier.Diagnostics;

/// <summary>The schedule mockup's data (docs/design/schedule-dark.svg).</summary>
internal sealed partial class DemoUiData
{
    public ScheduleData LoadSchedule()
    {
        PlanDefinition nightly = PlanDefinition.Templates[0].Definition;
        PlanDefinition weekly = PlanDefinition.Templates[1].Definition;
        PlanDefinition monthly = PlanDefinition.Templates[2].Definition with { Roots = [3] };
        Dictionary<long, string> roots = new() { [3] = "Videos" };
        DateOnly created = new(2026, 11, 1);
        return new ScheduleData(
            [
                new PlanRow(1, UI.Strings.Schedule.Nightly, true, ScheduleText.Summary(nightly, roots), string.Format(System.Globalization.CultureInfo.CurrentCulture, UI.Strings.Schedule.TomorrowAt, "03:00"), UI.Strings.Scans.Completed, Severity.None, nightly, created),
                new PlanRow(2, UI.Strings.Schedule.Weekly, true, ScheduleText.Summary(weekly, roots), "Sun 03:00", UI.Strings.Scans.Completed, Severity.None, weekly, created),
                new PlanRow(3, UI.Strings.Schedule.MonthlyDeep, false, ScheduleText.Summary(monthly, roots), UI.Strings.Schedule.Off, UI.Strings.Schedule.NeverRun, null, monthly, created),
            ],
            0.82,
            2251,
            12_507,

            // 41 nights at the mockup's 2 h and the assumed read rate ("a full pass takes 41 days").
            29_000_000_000_000,
            ScheduleData.AssumedReadRate);
    }
}
