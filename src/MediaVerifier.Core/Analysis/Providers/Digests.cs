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
