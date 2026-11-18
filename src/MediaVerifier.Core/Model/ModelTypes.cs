namespace MediaVerifier.Core.Model;

public enum MediaKind : byte
{
    Unknown = 0,
    Photo = 1,
    Video = 2,
    Audio = 3,
}

[Flags]
public enum MediaKindMask : byte
{
    None = 0,
    Photo = 1 << 0,
    Video = 1 << 1,
    Audio = 1 << 2,
    All = Photo | Video | Audio,
}

/// <summary>How much work a run does per file (master plan section 7).</summary>
public enum ScanDepth : byte
{
    None = 0,
    Quick = 1,
    Standard = 2,
    Deep = 3,
}

public enum FindingCategory : byte
{
    /// <summary>Rot, unreadable, zero-filled.</summary>
    Integrity = 1,

    /// <summary>Container and decode damage.</summary>
    Corruption = 2,

    /// <summary>Fakes: lossless-from-lossy, upscaled, upsampled, padded.</summary>
    Authenticity = 3,

    /// <summary>Clipping, starved bitrate, artifacts.</summary>
    Quality = 4,

    /// <summary>Index, timestamps, extension, trailing data.</summary>
    Structure = 5,
}

public enum FindingStatus : byte
{
    Open = 0,
    Ignored = 1,
    Resolved = 2,
}

/// <summary>The milestone that implements a rule (subplan 02, column Ms).</summary>
public enum Milestone : byte
{
    M1 = 1,
    M2 = 2,
    M3 = 3,
    Backlog = 9,
}

/// <summary>How a heuristic's confidence is worded (subplan 02, Wording rules).</summary>
public enum ConfidenceWording : byte
{
    /// <summary>Confidence 1.0: a plain statement.</summary>
    Fact = 0,

    /// <summary>0.8 to 0.99: "Likely ...".</summary>
    Likely = 1,

    /// <summary>0.5 to 0.79: "Possibly ...".</summary>
    Possibly = 2,

    /// <summary>Below 0.5: "Suspicious: ..." and never above Low.</summary>
    Suspicious = 3,
}

public static class MediaKindExtensions
{
    public static MediaKindMask ToMask(this MediaKind kind) => kind switch
    {
        MediaKind.Photo => MediaKindMask.Photo,
        MediaKind.Video => MediaKindMask.Video,
        MediaKind.Audio => MediaKindMask.Audio,
        _ => MediaKindMask.None,
    };

    public static bool Includes(this MediaKindMask mask, MediaKind kind) => (mask & kind.ToMask()) != 0;
}

/// <summary>
/// One rule violation on one file (subplan 02). Evidence holds numbers, strings or small arrays under
/// camelCase keys with a unit suffix, enough for the UI to draw its evidence view without re-running
/// analysis.
/// </summary>
public sealed record Finding(
    string RuleId,
    Severity Severity,
    float Confidence,
    string Summary,
    IReadOnlyDictionary<string, object> Evidence)
{
    public static IReadOnlyDictionary<string, object> NoEvidence { get; } = new Dictionary<string, object>(StringComparer.Ordinal);
}

/// <summary>A run-level alert (not a per-file finding), shown in run summaries and notifications.</summary>
public sealed record RunAlertDefinition(string Id, string Title, Severity Severity, string Trigger);

/// <summary>Media facts kept with each file row for display and sorting (from the probe).</summary>
public sealed record MediaSummary(
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    int? Width,
    int? Height,
    long? DurationMs,
    long? Bitrate,
    int? SampleRate,
    int? Channels,
    int? BitDepth,
    int? FpsMilli)
{
    public static MediaSummary Empty { get; } = new(null, null, null, null, null, null, null, null, null, null, null);
}
