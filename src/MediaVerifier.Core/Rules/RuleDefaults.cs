using System.Globalization;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Core.Rules;

/// <summary>A tunable threshold: its default, allowed range and how Settings → Rules labels it.</summary>
public sealed record RuleParameter(
    string RuleId,
    string Name,
    double Default,
    double Min,
    double Max,
    string Label,
    string Unit,
    int Decimals = 0)
{
    public string Key => RuleId + "." + Name;

    public double Clamp(double value) => Math.Clamp(value, Min, Max);

    public string Format(double value) => value.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
}

/// <summary>
/// Every numeric threshold in one place (D-13), keyed <c>ruleId.paramName</c>. Detectors read values
/// through <see cref="RuleSettings"/>, which applies the user's overrides clamped to these ranges.
/// Values marked *calibrate* in the subplans start here and are updated by calibration (subplan 15).
/// </summary>
public static class RuleDefaults
{
    private static readonly Dictionary<string, RuleParameter> ByKey;

    static RuleDefaults()
    {
        All =
        [
            // General and integrity (subplans 02, 05, 06).
            new("GEN-ZEROFILL", "criticalPercent", 50, 1, 100, "Critical when this share is zeros", "%"),
            new("GEN-PATH", "maxPathLength", 259, 100, 32767, "Longest path without a warning", "characters"),
            new("RUN-MASS-MISSING", "percent", 20, 1, 100, "Alert when this share of a folder disappears", "%"),
            new("RUN-MASS-MISSING", "minFiles", 50, 1, 100000, "and at least this many files", "files"),

            // Corruption (subplan 07).
            new("COR-TRUNCATED", "criticalPercent", 50, 5, 100, "Critical when this share is missing", "%"),
            new("COR-TRUNCATED", "audioSeconds", 0.5, 0.1, 60, "Audio: allowed shortfall", "seconds", 1),
            new("COR-TRUNCATED", "audioPercent", 1, 0.1, 50, "or share of the length", "%", 1),
            new("COR-TRUNCATED", "mp3EstimatePercent", 3, 0.5, 50, "MP3 without a length header: allowed shortfall", "%", 1),
            new("COR-TRUNCATED", "videoSeconds", 2, 0.5, 600, "Video: allowed shortfall", "seconds", 1),
            new("COR-TRUNCATED", "videoPercent", 2, 0.1, 50, "or share of the length", "%", 1),
            new("COR-TRUNCATED", "keyFrameSeconds", 12, 1, 600, "Video without sound at Standard depth: allowed shortfall", "seconds", 1),
            new("COR-TRUNCATED", "secondsPerError", 1.5, 0.1, 60, "Audio lost mid-file that each decoder error explains", "seconds", 1),
            new("COR-DECODE", "highErrors", 5, 1, 100000, "High from this many errors", "errors"),
            new("COR-DECODE", "audioHighSeconds", 0.5, 0.1, 600, "Audio: High when this much is lost", "seconds", 1),
            new("COR-DECODE", "videoHighSeconds", 2, 0.1, 600, "Video: High when this much is lost", "seconds", 1),
            new("COR-DECODE", "frameShortfall", 2, 1, 100000, "Deep: count missing frames above", "frames"),
            new("COR-DECODE", "frameShortfallPercent", 0.1, 0.01, 50, "and above this share of the frames", "%", 2),
            new("COR-CONCEALED", "highFrames", 10, 1, 100000, "High from this many damaged frames", "frames"),

            // Structure (subplans 02, 07).
            new("COR-TIMESTAMPS", "minCount", 10, 1, 100000, "Report from this many timestamp errors", "errors"),
            new("COR-TIMESTAMPS", "mediumCount", 100, 1, 1000000, "Medium from this many", "errors"),
            new("COR-STREAM-LENGTH", "seconds", 2, 0.5, 600, "Allowed length difference", "seconds", 1),
            new("COR-STREAM-LENGTH", "percent", 2, 0.1, 50, "or share of the length", "%", 1),
            new("COR-STREAM-LENGTH", "mediumPercent", 10, 1, 100, "Medium above this difference", "%", 1),
        ];
        ByKey = All.ToDictionary(p => p.Key, StringComparer.Ordinal);
    }

    public static IReadOnlyList<RuleParameter> All { get; private set; }

    public static IEnumerable<RuleParameter> ForRule(string ruleId) => All.Where(p => p.RuleId == ruleId);

    public static RuleParameter Get(string ruleId, string name) =>
        ByKey.TryGetValue(ruleId + "." + name, out RuleParameter? parameter)
            ? parameter
            : throw new KeyNotFoundException($"Unknown rule parameter '{ruleId}.{name}'.");

    public static bool TryGet(string key, out RuleParameter parameter) => ByKey.TryGetValue(key, out parameter!);

    /// <summary>
    /// Adds parameters owned by a detector subplan. Called from static constructors of the detector
    /// registries, so every threshold still has exactly one home and range.
    /// </summary>
    internal static void Register(IEnumerable<RuleParameter> parameters)
    {
        lock (ByKey)
        {
            foreach (RuleParameter parameter in parameters)
            {
                if (!ByKey.TryAdd(parameter.Key, parameter))
                {
                    if (ByKey[parameter.Key] != parameter)
                    {
                        throw new InvalidOperationException($"Rule parameter '{parameter.Key}' is defined twice.");
                    }

                    continue;
                }
            }

            All = [.. ByKey.Values.OrderBy(p => p.RuleId, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal)];
        }
    }

    /// <summary>The value to use: the override when given (clamped and logged if out of range), else the default.</summary>
    public static double Resolve(RuleParameter parameter, double? overrideValue)
    {
        if (overrideValue is not double value || double.IsNaN(value))
        {
            return parameter.Default;
        }

        double clamped = parameter.Clamp(value);
        if (clamped != value)
        {
            AppLog.Shared?.Warning(string.Create(CultureInfo.InvariantCulture, $"Rule setting {parameter.Key} = {value} is outside {parameter.Min}..{parameter.Max}; using {clamped}"));
        }

        return clamped;
    }
}
