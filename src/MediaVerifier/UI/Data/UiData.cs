using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>A run as the Dashboard and Scans pages show it.</summary>
internal sealed record RunInfo(
    long Id,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    RunTrigger Trigger,
    string? PlanName,
    ScanDepth Depth,
    string Folders,
    long Files,
    long NewFindings,
    long Resolved,
    RunStatus Status);

/// <summary>The next scheduled run.</summary>
internal sealed record NextRunInfo(DateTimeOffset When, string PlanName);

/// <summary>One media-kind row of the Dashboard.</summary>
internal sealed record KindRow(MediaKind Kind, long Files, long Bytes, SeverityCounts Tiers, long OkFiles)
{
    public int Problems => Tiers.Total;
}

/// <summary>A recommended next step (subplan 12 fills these).</summary>
internal sealed record RecommendationItem(Severity Tier, string Title, string Detail, string Button, string ActionId);

/// <summary>A Recent activity entry.</summary>
internal sealed record ActivityItem(string Text, DateTimeOffset When);

internal sealed record DashboardData(
    bool HasFolders,
    bool AnyScanned,
    bool EngineMissing,
    long FilesScanned,
    long FilesHealthy,
    long FilesNeedingAttention,
    long FilesWithBaseline,
    long FilesVerified,
    int ScrubDays,
    SeverityCounts Open,
    SeverityCounts? OpenAtLastScan,
    RunInfo? LastRun,
    NextRunInfo? NextRun,
    IReadOnlyList<KindRow> Kinds,
    IReadOnlyList<RecommendationItem> Recommendations,
    IReadOnlyList<ActivityItem> Activity,
    IReadOnlyList<double> OpenProblemsHistory)
{
    public double HealthyPercent => FilesScanned == 0 ? 0 : 100.0 * FilesHealthy / FilesScanned;

    public double ProtectedPercent => FilesWithBaseline == 0 ? 0 : 100.0 * FilesVerified / FilesWithBaseline;

    public long Overdue => Math.Max(0, FilesWithBaseline - FilesVerified);
}

/// <summary>Badges in the shell: the Issues count and tier, unread notifications.</summary>
internal sealed record ShellCounts(int OpenMediumOrAbove, Severity Worst, int UnreadNotifications, bool HasFolders, bool UpdateAvailable = false);

/// <summary>
/// Everything the pages read, as immutable snapshots loaded off the UI thread (subplan 09). The live
/// implementation queries the library database; the render harness returns the mockups' data.
/// </summary>
internal interface IUiData
{
    ShellCounts LoadShell();

    DashboardData LoadDashboard();
}
