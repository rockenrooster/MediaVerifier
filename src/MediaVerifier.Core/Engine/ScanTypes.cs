using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Engine;

/// <summary>Part of the library a run covers: a root, a folder in it, or chosen files.</summary>
public sealed record ScanScope(long RootId, string? RelativeDirectory = null, IReadOnlyList<long>? FileIds = null);

/// <summary>Per-run switches.</summary>
public sealed record ScanOptions
{
    /// <summary>"Verify fingerprints only": read and compare, no analysis.</summary>
    public bool VerifyOnly { get; init; }

    /// <summary>Manual "Verify now": ignore the scrub period and budget for the scope.</summary>
    public bool VerifyEverything { get; init; }

    /// <summary>Time allowed for verification in this run; null is unlimited.</summary>
    public TimeSpan? ScrubBudget { get; init; }
}

/// <summary>A request to scan (subplan 05, Engine API).</summary>
public sealed record ScanRequest(
    IReadOnlyList<ScanScope> Scopes,
    ScanDepth Depth,
    RunTrigger Trigger,
    long? PlanId = null,
    TimeSpan? Budget = null,
    ScanOptions? Options = null);

public enum EnginePhase : byte
{
    Idle = 0,
    Enumerating = 1,
    Scanning = 2,
    Pausing = 3,
    Paused = 4,
    Finishing = 5,
}

/// <summary>One file being worked on, for the Scans page.</summary>
public sealed record InFlightItem(string Path, string Stage, double Fraction, MediaKind Kind);

/// <summary>
/// Immutable progress snapshot (subplan 05, Progress and estimates). The UI polls it at up to 10 Hz; the
/// engine never raises per-file UI events.
/// </summary>
public sealed record EngineStatus(
    EnginePhase Phase,
    long? RunId,
    ScanDepth Depth,
    long FilesSeen,
    long FilesQueued,
    long FilesDone,
    long FilesSkipped,
    long FilesFailed,
    long BytesRead,
    double BytesPerSecond,
    IReadOnlyDictionary<MediaKind, long> DonePerKind,
    SeverityCounts NewFindings,
    IReadOnlyList<InFlightItem> InFlight,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    bool Estimating,
    int PausingFiles,
    RunTrigger Trigger)
{
    public static EngineStatus Idle { get; } = new(
        EnginePhase.Idle, null, ScanDepth.None, 0, 0, 0, 0, 0, 0, 0, new Dictionary<MediaKind, long>(), SeverityCounts.Zero, [], TimeSpan.Zero, null, false, 0, RunTrigger.Manual);

    public bool IsActive => Phase != EnginePhase.Idle;

    /// <summary>Files the run expects in total (seen so far; grows while enumerating).</summary>
    public long Total => Math.Max(FilesSeen, FilesDone + FilesQueued);

    public double Fraction => Total == 0 ? 0 : Math.Clamp((double)FilesDone / Total, 0, 1);
}

/// <summary>A run-level alert (subplan 02): mass disappearance, offline folder, read errors.</summary>
public sealed record RunAlert(string Id, Severity Severity, long? RootId, string Detail);

/// <summary>What a finished run reports (RunCompleted, notifications, Scans history).</summary>
public sealed record RunSummary(
    long RunId,
    RunStatus Status,
    RunTrigger Trigger,
    long? PlanId,
    RunCounters Counters,
    SeverityCounts NewFindings,
    IReadOnlyList<RunAlert> Alerts,
    TimeSpan Elapsed,
    string? Error);

/// <summary>Why a file needs attention in this run, in the priority order of subplan 05.</summary>
public enum WorkPriority : byte
{
    NewOrModified = 0,
    VerificationDue = 1,
    Upgrade = 2,
}

/// <summary>One file's work in a run.</summary>
public sealed record WorkItem
{
    public required long FileId { get; init; }
    public required RootRecord Root { get; init; }
    public required VolumeInfo Volume { get; init; }
    public required string RelDir { get; init; }
    public required string Name { get; init; }
    public required MediaKind Kind { get; init; }
    public required FileStat Stat { get; init; }
    public FileRecord? Previous { get; init; }
    public WorkPriority Priority { get; init; }

    /// <summary>Read the content for a fingerprint (new, modified, verification due, or verify-on-analysis).</summary>
    public bool ReadContent { get; init; }

    /// <summary>Run providers and detectors at <see cref="Depth"/>.</summary>
    public bool Analyze { get; init; }

    /// <summary>Re-run detectors over stored artifacts only (detector set behind).</summary>
    public bool ReevaluateOnly { get; init; }

    public bool IsNew { get; init; }

    public bool Modified { get; init; }

    public bool Returned { get; init; }

    public ScanDepth Depth { get; init; }

    public string FullPath => RelDir.Length == 0 ? System.IO.Path.Combine(Root.Path, Name) : System.IO.Path.Combine(Root.Path, RelDir, Name);
}
