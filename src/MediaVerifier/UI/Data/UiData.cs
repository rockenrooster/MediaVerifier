using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>
/// A run as the Dashboard and Scans pages show it. <paramref name="Folders"/> is its scope as it reads inside a
/// sentence (<c>all folders</c>, folder names, <c>{n} files</c>).
/// </summary>
internal sealed record RunInfo(
    long Id,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    RunTrigger Trigger,
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
internal sealed record RecommendationItem(Severity Tier, string Title, string Detail, string Button, string ActionId, IReadOnlyList<long>? FileIds = null);

/// <summary>A Recent activity entry.</summary>
internal sealed record ActivityItem(string Text, DateTimeOffset When);

/// <summary>
/// The Dashboard's snapshot. <paramref name="OpenAtLastScan"/> is what was open when the last completed scan began
/// (the tiles' change), <paramref name="OpenProblemsHistory"/> the open total after each of the last 30 scans, and
/// <paramref name="FirstScanEstimate"/> how long a first Standard scan of the folders not scanned yet takes (null
/// when their size is not known).
/// </summary>
internal sealed record DashboardData(
    bool HasFolders,
    bool AnyScanned,
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
    IReadOnlyList<double> OpenProblemsHistory,
    TimeSpan? FirstScanEstimate = null)
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
internal interface IUiData : IIssuesData, ILibraryData, IScansData, IToolsData, IScheduleData
{
    ShellCounts LoadShell();

    DashboardData LoadDashboard();
}
