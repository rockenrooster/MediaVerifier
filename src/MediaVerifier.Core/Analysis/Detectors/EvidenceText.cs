using System.Globalization;
using MediaVerifier.Core.Analysis.Providers;

namespace MediaVerifier.Core.Analysis.Detectors;

/// <summary>
/// Formatting for finding summaries (the evidence phrase after the rule title) and evidence values
/// (subplan 02, Evidence conventions; subplan 09, Formats used in strings).
/// </summary>
public static class EvidenceText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A position or length inside media: <c>0:07</c>, <c>4:05</c>, <c>1:02:10</c>.</summary>
    /// <summary>
    /// Where decoder errors were: ", around 1:23, 4:05 and 12:40", ", around 0:11", or with more spots ", around 1:23,
    /// 4:05, 12:40 and 11 more places"; empty without spots. The positions are approximate, hence "around".
    /// </summary>
    public static string Around(IReadOnlyList<double>? spots)
    {
        if (spots is null || spots.Count == 0)
        {
            return string.Empty;
        }

        string[] shown = [.. spots.Take(3).Select(MediaTime)];
        int more = spots.Count - shown.Length;
        string list = more > 0 ? string.Join(", ", shown) + " and " + Plural(more, "more place", "more places")
            : shown.Length == 1 ? shown[0]
            : string.Join(", ", shown[..^1]) + " and " + shown[^1];
        return ", around " + list;
    }

    public static string MediaTime(double seconds)
    {
        long total = (long)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero);
        long hours = total / 3600;
        long minutes = total / 60 % 60;
        long secs = total % 60;
        return hours > 0
            ? string.Create(Invariant, $"{hours}:{minutes:00}:{secs:00}")
            : string.Create(Invariant, $"{minutes}:{secs:00}");
    }

    /// <summary>A short span: <c>0.4 s</c>, <c>12 s</c>, then media time.</summary>
    public static string Span(double seconds) => seconds switch
    {
        < 10 => string.Create(Invariant, $"{seconds:0.0} s"),
        < 60 => string.Create(Invariant, $"{seconds:0} s"),
        _ => MediaTime(seconds),
    };

    /// <summary>Binary units, one decimal, none for bytes and KB (<c>412 KB</c>, <c>31.2 MB</c>).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(Invariant, $"{bytes} bytes");
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes / 1024.0;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(Invariant, $"{Math.Round(value):0} {units[0]}")
            : string.Create(Invariant, $"{value:0.0} {units[unit]}");
    }

    public static string Count(long n) => n.ToString("N0", Invariant);

    /// <summary><c>1 error</c> / <c>14 errors</c>.</summary>
    public static string Plural(long n, string one, string many) => n == 1 ? "1 " + one : Count(n) + " " + many;

    /// <summary>Rounds for evidence: three decimals is plenty for seconds and keeps the JSON short.</summary>
    public static double Round(double value) => Math.Round(value, 3);

    /// <summary>The decoder evidence shared by every decode rule: counts per kind, the top messages, the first raw line.</summary>
    public static Dictionary<string, object> Decoder(LogDigest log, double? declaredSeconds, double? decodedSeconds)
    {
        Dictionary<string, object> evidence = [];
        if (declaredSeconds is double declared)
        {
            evidence["declaredSeconds"] = Round(declared);
        }

        if (decodedSeconds is double decoded)
        {
            evidence["decodedSeconds"] = Round(decoded);
        }

        AddCounts(evidence, log.Counts, string.Empty);
        if (log.Top.Length > 0)
        {
            evidence["messages"] = log.EvidenceMessages();
            evidence["messageCounts"] = log.EvidenceCounts();
        }

        if (log.FirstLine is not null)
        {
            evidence["firstLine"] = log.FirstLine;
        }

        return evidence;
    }

    /// <summary>Adds the non-zero counts of <paramref name="counts"/>, keys prefixed (<c>video</c>, <c>audio</c>) when given.</summary>
    public static void AddCounts(Dictionary<string, object> evidence, SignalCounts counts, string prefix)
    {
        Add("decodeErrors", counts.DecodeError);
        Add("minorErrors", counts.MinorDecode);
        Add("unknownErrors", counts.Unknown);
        Add("checksumErrors", counts.Checksum);
        Add("truncationErrors", counts.Truncation);
        Add("concealedFrames", counts.Concealed);
        Add("timestampErrors", counts.Timestamp);
        Add("openErrors", counts.OpenFailure);

        void Add(string key, int value)
        {
            if (value > 0)
            {
                evidence[prefix.Length == 0 ? key : prefix + char.ToUpperInvariant(key[0]) + key[1..]] = value;
            }
        }
    }
}
