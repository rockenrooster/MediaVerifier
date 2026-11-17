using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Scheduling;

public enum TriggerKind
{
    Daily,
    Weekly,
    Monthly,
    EveryDays,
}

public enum NotifyWhen
{
    Always,
    NewFindings,
    Never,
}

/// <summary>When a plan fires (subplan 13, Plans): one local time of day per plan.</summary>
public sealed record PlanTrigger
{
    public TriggerKind Kind { get; init; } = TriggerKind.Weekly;

    /// <summary>Local time of day, <c>HH:mm</c>.</summary>
    public string Time { get; init; } = "03:00";

    /// <summary>Weekly: the days; Monthly with <see cref="WeekOfMonth"/>: the one weekday.</summary>
    public DayOfWeek[] DaysOfWeek { get; init; } = [];

    /// <summary>Monthly: day 1–28, or 0 for the last day of the month.</summary>
    public int? DayOfMonth { get; init; }

    /// <summary>Monthly by weekday: 1–4, or 0 for the last such weekday ("first Saturday").</summary>
    public int? WeekOfMonth { get; init; }

    /// <summary>EveryDays: the interval in days.</summary>
    public int? EveryDays { get; init; }

    [JsonIgnore]
    public TimeOnly TimeOfDay => TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly t) ? t : new TimeOnly(3, 0);
}

public sealed record PlanConditions
{
    public bool OnlyOnAcPower { get; init; } = true;

    public bool SkipWhenFullScreen { get; init; } = true;

    public bool RunMissedAsSoonAsPossible { get; init; } = true;

    public bool WakeComputer { get; init; }

    public int StopAfterMinutes { get; init; } = 480;
}

public sealed record PlanNotify
{
    public NotifyWhen When { get; init; } = NotifyWhen.NewFindings;

    public Severity MinSeverity { get; init; } = Severity.Medium;
}

/// <summary>A saved scan definition with its trigger (subplan 13, Plans; task SCH-01). Stored as JSON in <c>plans.definition_json</c>.</summary>
public sealed record PlanDefinition
{
    /// <summary>Root ids; null means all folders.</summary>
    public long[]? Roots { get; init; }

    public ScanDepth Depth { get; init; } = ScanDepth.Standard;

    public PlanTrigger Trigger { get; init; } = new();

    /// <summary>Null means no time limit.</summary>
    public int? BudgetMinutes { get; init; }

    public bool IncludeVerification { get; init; } = true;

    public PlanConditions Conditions { get; init; } = new();

    public PlanNotify Notify { get; init; } = new();

    /// <summary>The problems with this definition, in plain words; empty when it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];
        if (!TimeOnly.TryParseExact(Trigger.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            problems.Add("The time must be HH:mm.");
        }

        if (Roots is { Length: 0 })
        {
            problems.Add("Choose at least one folder.");
        }

        switch (Trigger.Kind)
        {
            case TriggerKind.Weekly when Trigger.DaysOfWeek.Length == 0:
                problems.Add("Choose at least one day.");
                break;
            case TriggerKind.Monthly when Trigger.WeekOfMonth is null && Trigger.DayOfMonth is not (>= 0 and <= 28):
                problems.Add("The day of the month must be 1 to 28, or the last day.");
                break;
            case TriggerKind.Monthly when Trigger.WeekOfMonth is not null && (Trigger.WeekOfMonth is not (>= 0 and <= 4) || Trigger.DaysOfWeek.Length != 1):
                problems.Add("Choose one weekday and its week in the month.");
                break;
            case TriggerKind.EveryDays when Trigger.EveryDays is not (>= 1 and <= 365):
                problems.Add("The interval must be 1 to 365 days.");
                break;
        }

        if (BudgetMinutes is <= 0)
        {
            problems.Add("The time limit must be positive.");
        }

        if (Conditions.StopAfterMinutes is <= 0 or > 72 * 60)
        {
            problems.Add("The stop limit must be 1 minute to 72 hours.");
        }

        return problems;
    }

    public string ToJson() => JsonSerializer.Serialize(this, SchedulingJsonContext.Default.PlanDefinition);

    public static PlanDefinition? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, SchedulingJsonContext.Default.PlanDefinition);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The templates of subplan 13: name and definition.</summary>
    public static IReadOnlyList<(string Name, PlanDefinition Definition)> Templates { get; } =
    [
        ("Nightly verification", new PlanDefinition { Depth = ScanDepth.Quick, Trigger = new PlanTrigger { Kind = TriggerKind.Daily, Time = "03:00" }, BudgetMinutes = 120 }),
        ("Weekly check", new PlanDefinition { Depth = ScanDepth.Standard, Trigger = new PlanTrigger { Kind = TriggerKind.Weekly, Time = "03:00", DaysOfWeek = [DayOfWeek.Sunday] } }),
        ("Monthly deep scan", new PlanDefinition { Depth = ScanDepth.Deep, Trigger = new PlanTrigger { Kind = TriggerKind.Monthly, Time = "01:00", WeekOfMonth = 1, DaysOfWeek = [DayOfWeek.Saturday] } }),
    ];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlanDefinition))]
internal sealed partial class SchedulingJsonContext : JsonSerializerContext;
