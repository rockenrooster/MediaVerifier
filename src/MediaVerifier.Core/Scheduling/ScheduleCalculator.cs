using System.Globalization;
using System.Xml.Linq;

namespace MediaVerifier.Core.Scheduling;

/// <summary>
/// Next run times shown in the UI (subplan 13, task SCH-02); pure. Times are local wall-clock times: a time that
/// does not exist on a spring-forward day moves to the first valid minute after it, as Task Scheduler does.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>The first run strictly after <paramref name="localNow"/>; <paramref name="anchor"/> is the first day of an every-n-days plan.</summary>
    public static DateTime? Next(PlanTrigger trigger, DateTime localNow, DateOnly anchor, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        zone ??= TimeZoneInfo.Local;
        DateOnly day = DateOnly.FromDateTime(localNow);
        for (int i = 0; i < 800; i++, day = day.AddDays(1))
        {
            if (!Fires(trigger, day, anchor))
            {
                continue;
            }

            DateTime at = Valid(day.ToDateTime(trigger.TimeOfDay), zone);
            if (at > localNow)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>Whether the plan fires on a day.</summary>
    public static bool Fires(PlanTrigger trigger, DateOnly day, DateOnly anchor)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return trigger.Kind switch
        {
            TriggerKind.Daily => true,
            TriggerKind.Weekly => trigger.DaysOfWeek.Contains(day.DayOfWeek),
            TriggerKind.EveryDays => trigger.EveryDays is int n && n > 0 && day >= anchor && (day.DayNumber - anchor.DayNumber) % n == 0,
            TriggerKind.Monthly when trigger.WeekOfMonth is int week => trigger.DaysOfWeek.Length == 1 && day.DayOfWeek == trigger.DaysOfWeek[0]
                && (week == 0 ? day.AddDays(7).Month != day.Month : (day.Day - 1) / 7 == week - 1),
            TriggerKind.Monthly => trigger.DayOfMonth is 0 ? day.AddDays(1).Month != day.Month : day.Day == trigger.DayOfMonth,
            _ => false,
        };
    }

    private static DateTime Valid(DateTime local, TimeZoneInfo zone)
    {
        DateTime at = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(at))
        {
            at = at.AddMinutes(1);
        }

        return at;
    }
}

/// <summary>The Task Scheduler XML for a plan (subplan 13, task SCH-03); pure. Text is escaped by the XML writer.</summary>
public static class ScheduleXmlBuilder
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(string planName, PlanDefinition definition, long planId, string exePath, DateOnly startDay)
    {
        ArgumentNullException.ThrowIfNull(definition);
        PlanTrigger t = definition.Trigger;
        string start = startDay.ToDateTime(t.TimeOfDay).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        XElement schedule = t.Kind switch
        {
            TriggerKind.Daily => new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", 1)),
            TriggerKind.EveryDays => new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", t.EveryDays ?? 1)),
            TriggerKind.Weekly => new XElement(Ns + "ScheduleByWeek", Days(t.DaysOfWeek), new XElement(Ns + "WeeksInterval", 1)),
            TriggerKind.Monthly when t.WeekOfMonth is int week => new XElement(
                Ns + "ScheduleByMonthDayOfWeek",
                new XElement(Ns + "Weeks", new XElement(Ns + "Week", week == 0 ? "Last" : week.ToString(CultureInfo.InvariantCulture))),
                Days(t.DaysOfWeek),
                Months()),
            _ => new XElement(
                Ns + "ScheduleByMonth",
                new XElement(Ns + "DaysOfMonth", new XElement(Ns + "Day", t.DayOfMonth is 0 ? "Last" : (t.DayOfMonth ?? 1).ToString(CultureInfo.InvariantCulture))),
                Months()),
        };
        PlanConditions c = definition.Conditions;
        XElement task = new(
            Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", "MediaVerifier scheduled scan: " + planName)),
            new XElement(Ns + "Triggers", new XElement(Ns + "CalendarTrigger", new XElement(Ns + "StartBoundary", start), new XElement(Ns + "Enabled", "true"), schedule)),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"), new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(
                Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", Bool(c.OnlyOnAcPower)),
                new XElement(Ns + "StopIfGoingOnBatteries", Bool(c.OnlyOnAcPower)),
                new XElement(Ns + "StartWhenAvailable", Bool(c.RunMissedAsSoonAsPossible)),
                new XElement(Ns + "WakeToRun", Bool(c.WakeComputer)),
                new XElement(Ns + "ExecutionTimeLimit", "PT" + (c.StopAfterMinutes / 60).ToString(CultureInfo.InvariantCulture) + "H" + (c.StopAfterMinutes % 60 > 0 ? (c.StopAfterMinutes % 60).ToString(CultureInfo.InvariantCulture) + "M" : string.Empty)),
                new XElement(Ns + "Priority", 7),
                new XElement(Ns + "Enabled", "true")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), new XElement(Ns + "Exec", new XElement(Ns + "Command", exePath), new XElement(Ns + "Arguments", "--run-plan " + planId.ToString(CultureInfo.InvariantCulture)))));
        return new XDocument(task).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Days(IEnumerable<DayOfWeek> days) =>
        new(Ns + "DaysOfWeek", days.Distinct().Order().Select(d => new XElement(Ns + d.ToString())));

    private static XElement Months() =>
        new(Ns + "Months", CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12).Select(m => new XElement(Ns + m)));

    private static string Bool(bool value) => value ? "true" : "false";
}
