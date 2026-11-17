using MediaVerifier.Core;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Page snapshots from the library database. Every method runs off the UI thread.</summary>
internal sealed partial class LiveUiData(Database database, Func<ScanSettings> settings, IClock clock, EvidenceRenderer? evidenceRenderer = null, Core.Analysis.PictureRenderer? pictures = null) : IUiData
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
        IReadOnlyList<RecommendationItem> recommendations = [.. Core.Remediation.RecommendationEngine.Build(database, scan, Core.Engine.WindowsFileSystem.Instance, clock.UtcNow).Select(r => Word(r, scan))];
        NextRunInfo? next = null;
        DateTime local = clock.UtcNow.LocalDateTime;
        foreach (PlanRecord plan in database.Plans.List().Where(p => p.Enabled))
        {
            if (Core.Scheduling.PlanDefinition.FromJson(plan.DefinitionJson) is { } definition
                && Core.Scheduling.ScheduleCalculator.Next(definition.Trigger, local, DateOnly.FromDateTime(new DateTime(plan.CreatedUtc, DateTimeKind.Utc).ToLocalTime())) is DateTime when
                && (next is null || when < next.When.LocalDateTime))
            {
                next = new NextRunInfo(new DateTimeOffset(when), plan.Name);
            }
        }
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
            next,
            kinds,
            recommendations,
            activity,
            []);
    }

    /// <summary>The wording of subplan 09's recommendation table for one recommendation.</summary>
    internal static RecommendationItem Word(Core.Remediation.Recommendation r, ScanSettings scan)
    {
        System.Globalization.CultureInfo c = System.Globalization.CultureInfo.CurrentCulture;
        string n = UiFormat.Count(r.FileCount);
        return r.Kind switch
        {
            Core.Remediation.RecommendationKind.RotWithCopies => new(
                r.Severity,
                r.FileCount == 1 ? Strings.Recommendations.RotTitleOne : string.Format(c, Strings.Recommendations.RotTitle, n),
                r.Covered == r.FileCount ? string.Format(c, Strings.Recommendations.RotCopyAll, r.Place, n) : string.Format(c, Strings.Recommendations.RotCopySome, r.Place, UiFormat.Count(r.Covered), n),
                Strings.Recommendations.Review,
                "review:INT-ROT",
                r.FileIds),
            Core.Remediation.RecommendationKind.RotWithoutCopies => new(r.Severity, r.FileCount == 1 ? Strings.Recommendations.RotTitleOne : string.Format(c, Strings.Recommendations.RotTitle, n), Strings.Recommendations.RotNoCopy, Strings.Recommendations.Review, "review:INT-ROT", r.FileIds),
            Core.Remediation.RecommendationKind.MissingIndex => new(r.Severity, string.Format(c, Strings.Recommendations.IndexTitle, n), string.Format(c, Strings.Recommendations.IndexDetail, UiFormat.Duration(TimeSpan.FromSeconds(Math.Max(1, r.TotalBytes / 200_000_000.0)))), Strings.Recommendations.FixAll, "fix:" + r.ActionId, r.FileIds),
            Core.Remediation.RecommendationKind.WrongExtension => new(r.Severity, string.Format(c, Strings.Recommendations.ExtensionTitle, n), Strings.Recommendations.ExtensionDetail, Strings.Recommendations.FixAll, "fix:" + r.ActionId, r.FileIds),
            Core.Remediation.RecommendationKind.FakeLossless => new(r.Severity, string.Format(c, Strings.Recommendations.FakeLosslessTitle, n), Strings.Recommendations.CannotRepair, Strings.Recommendations.Review, "review:AUD-FAKE-LOSSLESS", r.FileIds),
            Core.Remediation.RecommendationKind.UpscaledVideo => new(r.Severity, string.Format(c, Strings.Recommendations.UpscaledTitle, n), Strings.Recommendations.CannotRepair, Strings.Recommendations.Review, "review:VID-UPSCALED", r.FileIds),
            Core.Remediation.RecommendationKind.VerificationOverdue => new(
                r.Severity,
                string.Format(c, Strings.Recommendations.OverdueTitle, (r.Percent * 100).ToString("0", c) + "%", scan.ScrubPeriodDays),
                string.Format(c, Strings.Recommendations.OverdueDetail, UiFormat.Duration(TimeSpan.FromMinutes(scan.ScrubBudgetMinutes)), UiFormat.Count(Math.Max(1, (int)Math.Ceiling(1 / Math.Max(0.01, 1 - r.Percent))))),
                Strings.Recommendations.VerifyNow,
                "verify"),
            Core.Remediation.RecommendationKind.NoBackupFolder => new(r.Severity, Strings.Recommendations.NoBackupTitle, Strings.Recommendations.NoBackupDetail, Strings.Recommendations.AddFolder, "add-folder"),
            Core.Remediation.RecommendationKind.NoSchedule => new(r.Severity, Strings.Recommendations.NoScheduleTitle, Strings.Recommendations.NoScheduleDetail, Strings.Recommendations.CreateSchedule, "schedule"),
            Core.Remediation.RecommendationKind.ReadErrors => new(r.Severity, string.Format(c, Strings.Recommendations.ReadErrorsTitle, r.Place, n), Strings.Recommendations.ReadErrorsDetail, Strings.Recommendations.WhatToDo, "read-errors"),
            _ => new(r.Severity, Strings.Recommendations.InterruptedTitle, string.Format(c, Strings.Recommendations.InterruptedDetail, UiFormat.Count(r.Done), UiFormat.Count(r.Total)), Strings.Recommendations.Continue, "continue"),
        };
    }

    /// <summary>Stored UTC ticks to a timestamp.</summary>
    internal static DateTimeOffset Ticks(long utcTicks) => new(utcTicks, TimeSpan.Zero);

    internal static RunInfo ToRunInfo(RunRecord run, IReadOnlyList<RootRecord> roots) => ToRunInfo(run, roots, new Dictionary<long, string>());
}
