using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Storage;

public enum RootRole : byte
{
    Library = 0,
    Backup = 1,
}

public enum StorageClass : byte
{
    Unknown = 0,
    Ssd = 1,
    Hdd = 2,
    Network = 3,
    Removable = 4,
}

public enum FileState : byte
{
    Pending = 0,
    Ok = 1,
    Issues = 2,
    Missing = 3,
    Skipped = 4,
    Error = 5,
}

/// <summary>Why a file was not analysed (subplan 05, Files that are skipped). Not a finding.</summary>
public enum SkipReason : byte
{
    None = 0,
    CloudPlaceholder = 1,
    InProgress = 2,
    Locked = 3,
    AccessDenied = 4,
    TooLarge = 5,
    UnsupportedByFfmpeg = 6,
}

public enum ArtifactKind : byte
{
    Structure = 1,
    Decode = 2,
    PcmStats = 3,
    FrameSamples = 4,
    StreamHash = 5,
    Integrity = 6,
    Probe = 7,
}

public enum FileEventKind : byte
{
    FirstSeen = 1,
    Baselined = 2,
    Verified = 3,
    Modified = 4,
    Rot = 5,
    MetaDrift = 6,
    Moved = 7,
    Missing = 8,
    Returned = 9,
    Repaired = 10,
    Restored = 11,
}

public enum RunTrigger : byte
{
    Manual = 0,
    Scheduled = 1,
    Resume = 2,
}

public enum RunStatus : byte
{
    Running = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
    Interrupted = 4,
    BudgetReached = 5,
}

public enum OperationStatus : byte
{
    Planned = 0,
    Running = 1,
    Done = 2,
    Failed = 3,
    Undone = 4,
    Cancelled = 5,
}

/// <summary>What changed in a committed batch; pages decide whether to refresh.</summary>
[Flags]
public enum ChangeFlags
{
    None = 0,
    Roots = 1 << 0,
    Files = 1 << 1,
    Findings = 1 << 2,
    Runs = 1 << 3,
    Plans = 1 << 4,
    Operations = 1 << 5,
    Notifications = 1 << 6,
    RuleSettings = 1 << 7,
    Artifacts = 1 << 8,
    Ignores = 1 << 9,
    Events = 1 << 10,
    All = (1 << 11) - 1,
}

public sealed class LibraryChangedEventArgs(ChangeFlags changes) : EventArgs
{
    public ChangeFlags Changes { get; } = changes;
}

/// <summary>UTC instants are stored as integer .NET ticks.</summary>
public static class Ticks
{
    public static long From(DateTimeOffset utc) => utc.UtcTicks;

    public static long? From(DateTimeOffset? utc) => utc?.UtcTicks;

    public static DateTimeOffset To(long ticks) => new(ticks, TimeSpan.Zero);

    public static DateTimeOffset? To(long? ticks) => ticks is long t ? new DateTimeOffset(t, TimeSpan.Zero) : null;
}

public sealed record RootRecord
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public string? Label { get; init; }
    public RootRole Role { get; init; } = RootRole.Library;
    public MediaKindMask MediaMask { get; init; } = MediaKindMask.All;
    public ScanDepth DefaultDepth { get; init; } = ScanDepth.Standard;
    public string? IncludePatternsJson { get; init; }
    public string? ExcludePatternsJson { get; init; }
    public string? VolumeId { get; init; }
    public StorageClass StorageClass { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset AddedUtc { get; init; }
    public DateTimeOffset? LastOnlineUtc { get; init; }
}

public sealed record DirRecord(long Id, long RootId, long? ParentId, string RelPath, string Name);

/// <summary>A file row (subplan 03, files). Media summary columns are denormalised for display.</summary>
public sealed record FileRecord
{
    public long Id { get; init; }
    public long RootId { get; init; }
    public long DirId { get; init; }
    public required string Name { get; init; }
    public required string Ext { get; init; }
    public MediaKind MediaKind { get; init; }
    public long Size { get; init; }
    public long MtimeUtc { get; init; }
    public long? NtfsFileId { get; init; }
    public FileState State { get; init; }
    public SkipReason SkipReason { get; init; }
    public Severity WorstSeverity { get; init; }
    public int OpenFindings { get; init; }
    public ScanDepth ScannedDepth { get; init; }
    public int DetectorSet { get; init; }
    public long FirstSeenUtc { get; init; }
    public long LastSeenRun { get; init; }
    public long? LastScannedUtc { get; init; }
    public byte[]? Hash { get; init; }
    public long? HashSize { get; init; }
    public long? HashMtimeUtc { get; init; }
    public long? HashUtc { get; init; }
    public byte[]? StreamHash { get; init; }
    public long? LastVerifiedUtc { get; init; }
    public int VerifyCount { get; init; }
    public MediaSummary Media { get; init; } = MediaSummary.Empty;

    /// <summary>Directory path relative to the root, filled by queries that join it.</summary>
    public string? RelDir { get; init; }
}

public sealed record FindingRecord
{
    public long Id { get; init; }
    public long FileId { get; init; }
    public required string RuleId { get; init; }
    public FindingCategory Category { get; init; }
    public Severity Severity { get; init; }
    public float Confidence { get; init; }
    public required string Summary { get; init; }
    public string? EvidenceJson { get; init; }
    public FindingStatus Status { get; init; }
    public long FirstRun { get; init; }
    public long LastRun { get; init; }
    public long FirstUtc { get; init; }
    public long LastUtc { get; init; }
    public long? ResolvedUtc { get; init; }
    public string? Resolution { get; init; }
}

public sealed record RunRecord
{
    public long Id { get; init; }
    public long? PlanId { get; init; }
    public RunTrigger Trigger { get; init; }
    public ScanDepth Depth { get; init; }
    public string ScopeJson { get; init; } = "{}";
    public RunStatus Status { get; init; }
    public long StartedUtc { get; init; }
    public long? EndedUtc { get; init; }
    public long FilesSeen { get; init; }
    public long FilesAnalysed { get; init; }
    public long FilesVerified { get; init; }
    public long BytesRead { get; init; }
    public long NewFindings { get; init; }
    public long ResolvedFindings { get; init; }
    public long Errors { get; init; }
    public string? AlertsJson { get; init; }
    public string? SummaryJson { get; init; }
}

/// <summary>Counters a running scan checkpoints into its run row.</summary>
public sealed record RunCounters(long FilesSeen, long FilesAnalysed, long FilesVerified, long BytesRead, long NewFindings, long ResolvedFindings, long Errors);

public sealed record PlanRecord
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public required string DefinitionJson { get; init; }
    public string? TaskName { get; init; }
    public long CreatedUtc { get; init; }
    public long UpdatedUtc { get; init; }
    public long? LastRunId { get; init; }
}

public sealed record OperationRecord
{
    public long Id { get; init; }
    public long BatchId { get; init; }
    public long Utc { get; init; }
    public required string ActionId { get; init; }
    public long? FileId { get; init; }
    public OperationStatus Status { get; init; }
    public required string SourcePath { get; init; }
    public string? ResultPath { get; init; }
    public string? BackupPath { get; init; }
    public string? UndoJson { get; init; }
    public string? DetailJson { get; init; }
}

public sealed record NotificationRecord
{
    public long Id { get; init; }
    public long Utc { get; init; }
    public Severity Level { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public string? ActionUri { get; init; }
    public bool IsRead { get; init; }
}

public sealed record IgnoreRecord(long Id, long RootId, long? DirId, string? RuleId, string? Reason, long CreatedUtc);

public sealed record FileEventRecord(long Id, long FileId, long Utc, long? RunId, FileEventKind Kind, string? DetailJson);

/// <summary>Thrown for storage conditions the user must hear about (newer schema, wrong SQLite).</summary>
public sealed class StorageException(string message, Exception? inner = null) : Exception(message, inner);
