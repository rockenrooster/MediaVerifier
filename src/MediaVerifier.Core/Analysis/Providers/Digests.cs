using System.Text.Json.Serialization;
using MediaVerifier.Core.Ffmpeg;

namespace MediaVerifier.Core.Analysis.Providers;

/// <summary>Outcome of an FFmpeg run as stored in artifacts.</summary>
public enum RunOutcome : byte
{
    Completed = 0,
    TimedOut = 1,
    FailedToStart = 2,
    Cancelled = 3,
}

public sealed record MessageCount(string Message, int Count);

/// <summary>Counts per signal kind: what detectors need from a log, a few dozen bytes stored.</summary>
public sealed record SignalCounts(
    int OpenFailure = 0,
    int Truncation = 0,
    int DecodeError = 0,
    int MinorDecode = 0,
    int Concealed = 0,
    int Checksum = 0,
    int Timestamp = 0,
    int Unsupported = 0,
    int Unknown = 0)
{
    public static SignalCounts Zero { get; } = new();

    /// <summary>Unknown error lines count as decode errors at half weight (subplan 04).</summary>
    [JsonIgnore]
    public double WeightedDecodeErrors => DecodeError + (0.5 * Unknown);

    [JsonIgnore]
    public bool AnyDamage => OpenFailure + Truncation + DecodeError + Concealed + Checksum + Unknown > 0;

    public static SignalCounts From(LogSummary log) => new(
        log.Count(LogSignalKind.OpenFailure),
        log.Count(LogSignalKind.Truncation),
        log.Count(LogSignalKind.DecodeError),
        log.Count(LogSignalKind.MinorDecode),
        log.Count(LogSignalKind.ConcealedFrame),
        log.Count(LogSignalKind.Checksum),
        log.Count(LogSignalKind.Timestamp),
        log.Count(LogSignalKind.Unsupported),
        log.Count(LogSignalKind.Unknown));

    public SignalCounts Add(LogSignalKind kind) => kind switch
    {
        LogSignalKind.OpenFailure => this with { OpenFailure = OpenFailure + 1 },
        LogSignalKind.Truncation => this with { Truncation = Truncation + 1 },
        LogSignalKind.DecodeError => this with { DecodeError = DecodeError + 1 },
        LogSignalKind.MinorDecode => this with { MinorDecode = MinorDecode + 1 },
        LogSignalKind.ConcealedFrame => this with { Concealed = Concealed + 1 },
        LogSignalKind.Checksum => this with { Checksum = Checksum + 1 },
        LogSignalKind.Timestamp => this with { Timestamp = Timestamp + 1 },
        LogSignalKind.Unsupported => this with { Unsupported = Unsupported + 1 },
        LogSignalKind.Unknown => this with { Unknown = Unknown + 1 },
        _ => this,
    };
}

/// <summary>A log reduced to counts, the three most frequent damage messages and the first raw line.</summary>
/// <summary>
/// Where in the file the decoder reported errors (owner request 2026-11-17): the position decoding had reached
/// when each error line arrived, so an approximation, merged into spots at least 2 s apart.
/// </summary>
public static class ErrorSpots
{
    public const int Max = 50;

    /// <summary>Seconds rounded to tenths, sorted; an error within 2 s of the last kept spot belongs to it.</summary>
    public static double[] Merge(IEnumerable<double> seconds)
    {
        List<double> spots = [];
        foreach (double s in seconds.Where(double.IsFinite).Select(s => Math.Round(Math.Max(0, s), 1)).Order())
        {
            if (spots.Count == 0 || s - spots[^1] >= 2)
            {
                spots.Add(s);
                if (spots.Count == Max)
                {
                    break;
                }
            }
        }

        return [.. spots];
    }

    /// <summary>
    /// The media position of each error from when it was reported (wall-clock seconds since the decode started),
    /// linear between the progress reports around it; before the first report from the start, after the last one
    /// its position. Both lists are in time order.
    /// </summary>
    public static List<double> Interpolate(IReadOnlyList<double> errorWall, IReadOnlyList<(double Wall, double Media)> marks)
    {
        ArgumentNullException.ThrowIfNull(errorWall);
        ArgumentNullException.ThrowIfNull(marks);
        List<double> positions = new(errorWall.Count);
        int next = 0;
        foreach (double wall in errorWall)
        {
            while (next < marks.Count && marks[next].Wall < wall)
            {
                next++;
            }

            (double Wall, double Media) before = next > 0 ? marks[next - 1] : (0, 0);
            if (next == marks.Count)
            {
                positions.Add(before.Media);
                continue;
            }

            (double Wall, double Media) after = marks[next];
            double span = after.Wall - before.Wall;
            positions.Add(span <= 0 ? after.Media : before.Media + ((after.Media - before.Media) * (wall - before.Wall) / span));
        }

        return positions;
    }

    /// <summary>True for the log signals the decode findings count (where they happened is worth keeping).</summary>
    public static bool Counts(LogSignalKind kind) => kind is LogSignalKind.DecodeError or LogSignalKind.MinorDecode or LogSignalKind.ConcealedFrame;

    /// <summary>True when the counts hold errors whose spots are worth knowing.</summary>
    public static bool HasErrors(SignalCounts counts) => counts.DecodeError + counts.MinorDecode + counts.Concealed > 0;

    /// <summary>The finding's evidence with the spots in seconds (<c>positionsSeconds</c>) when the decode kept any.</summary>
    public static Dictionary<string, object> AddTo(Dictionary<string, object> evidence, double[]? spots)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (spots is { Length: > 0 })
        {
            evidence["positionsSeconds"] = spots;
        }

        return evidence;
    }
}

public sealed record LogDigest(SignalCounts Counts, MessageCount[] Top, string? FirstLine)
{
    public static LogDigest Empty { get; } = new(SignalCounts.Zero, [], null);

    public static LogDigest From(LogSummary log)
    {
        MessageCount[] top = [.. log.DamageMessages().Take(3).Select(m => new MessageCount(m.Message, m.Count))];
        string? first = log.Kinds.Values
            .Where(k => k.Kind is not (LogSignalKind.Noise or LogSignalKind.Benign))
            .Select(k => k.FirstLine)
            .FirstOrDefault(l => l is not null);
        return new LogDigest(SignalCounts.From(log), top, first);
    }

    /// <summary>The evidence table rows (Message, Count) for the detail view.</summary>
    public IReadOnlyList<string> EvidenceMessages() => [.. Top.Select(m => m.Message)];

    public IReadOnlyList<int> EvidenceCounts() => [.. Top.Select(m => m.Count)];
}

public static class OutcomeMapping
{
    public static RunOutcome From(ProcessOutcome outcome) => outcome switch
    {
        ProcessOutcome.TimedOut => RunOutcome.TimedOut,
        ProcessOutcome.FailedToStart => RunOutcome.FailedToStart,
        ProcessOutcome.Cancelled => RunOutcome.Cancelled,
        _ => RunOutcome.Completed,
    };

    /// <summary>Parses <c>0,a,MD5=...</c> lines from <c>-f streamhash</c> into one 16-byte hash of all lines.</summary>
    public static byte[]? StreamHash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] lines = [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Contains("MD5=", StringComparison.Ordinal))];
        if (lines.Length == 0)
        {
            return null;
        }

        byte[] all = System.Text.Encoding.ASCII.GetBytes(string.Join('\n', lines));
        return System.Security.Cryptography.MD5.HashData(all);
    }
}
