using MediaVerifier.Core;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Page snapshots from the library database. Every method runs off the UI thread.</summary>
internal sealed partial class LiveUiData(Database database, Func<ScanSettings> settings, IClock clock, EvidenceRenderer? evidenceRenderer = null) : IUiData
{
    public ShellCounts LoadShell()
    {
        SeverityCounts open = database.Queries.SeverityCounts();
        return new ShellCounts(open.MediumOrAbove, open.Worst, database.Notifications.UnreadCount(), database.Roots.List().Count > 0);
    }

    public DashboardData LoadDashboard()
    {
        ScanSettings scan = settings();
        List<RootRecord> roots = database.Roots.List();
        HealthSnapshot health = database.Queries.Health(TimeSpan.FromDays(scan.ScrubPeriodDays), clock.UtcNow);
        List<RunRecord> runs = database.Runs.List(30);
        RunRecord? last = runs.FirstOrDefault(r => r.Status != RunStatus.Running);
        List<ActivityItem> activity = [];
        foreach (RunRecord run in runs.Where(r => r.Status != RunStatus.Running).Take(4))
        {
            string text = run.Status switch
            {
                RunStatus.Completed or RunStatus.BudgetReached => run.Trigger == RunTrigger.Scheduled ? Strings.Activity.ScheduledFinished : Strings.Activity.ScanFinished,
                RunStatus.Cancelled => Strings.Activity.StoppedByYou,
                _ => Strings.Activity.CouldNotRun,
            };
            activity.Add(new ActivityItem(text, Ticks(run.EndedUtc ?? run.StartedUtc)));
        }

        List<KindRow> kinds = [.. database.Queries.KindSummaries().Select(k => new KindRow(k.Kind, k.Files, k.Bytes, k.Tiers, k.HealthyFiles))];
        return new DashboardData(
            roots.Count > 0,
            health.FilesScanned > 0,
            false,
            health.FilesScanned,
            health.FilesHealthy,
            health.FilesNeedingAttention,
            health.FilesWithBaseline,
            health.FilesVerifiedInPeriod,
            scan.ScrubPeriodDays,
            health.Open,
            null,
            last is null ? null : ToRunInfo(last, roots),
            null,
            kinds,
            [],
            activity,
            []);
    }

    /// <summary>Stored UTC ticks to a timestamp.</summary>
    internal static DateTimeOffset Ticks(long utcTicks) => new(utcTicks, TimeSpan.Zero);

    internal static RunInfo ToRunInfo(RunRecord run, IReadOnlyList<RootRecord> roots) => ToRunInfo(run, roots, new Dictionary<long, string>());
}
