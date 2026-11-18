using System.Text.Json;
using MediaVerifier.Core;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Page snapshots from the library database. Every method runs off the UI thread.</summary>
internal sealed partial class LiveUiData(Database database, Func<ScanSettings> settings, IClock clock, EvidenceRenderer? evidenceRenderer = null, Core.Analysis.PictureRenderer? pictures = null) : IUiData
{
    /// <summary>Runs the Dashboard reads: Recent activity, the last completed scan and the sparkline's 30 scans.</summary>
    private const int DashboardRuns = 100;

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
        List<RunRecord> runs = database.Runs.List(DashboardRuns);

        // "Last" is the last completed run (subplan 09), as on Scans: a stopped or failed run was shown as the last scan.
        RunRecord? last = LastCompleted(runs);
        List<ActivityItem> activity = Activity(runs, roots);
        List<KindSummary> summaries = database.Queries.KindSummaries();
        List<KindRow> kinds = [.. summaries.Select(k => new KindRow(k.Kind, k.Files, k.Bytes, k.Tiers, k.HealthyFiles))];
        int passDays = VerificationPassDays(summaries.Sum(k => k.Bytes), health.FilesWithBaseline, scan.ScrubBudgetMinutes);
        IReadOnlyList<RecommendationItem> recommendations = [.. Core.Remediation.RecommendationEngine.Build(database, scan, WindowsFileSystem.Instance, clock.UtcNow).Select(r => Word(r, scan, passDays))];
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

        (SeverityCounts? before, List<double> history) = OpenHistory(runs, last, complete: runs.Count < DashboardRuns);
        bool anyScanned = health.FilesScanned > 0;
        return new DashboardData(
            roots.Count > 0,
            anyScanned,
            health.FilesScanned,
            health.FilesHealthy,
            health.FilesNeedingAttention,
            health.FilesWithBaseline,
            health.FilesVerifiedInPeriod,
            scan.ScrubPeriodDays,
            health.Open,
            before,
            last is null ? null : ToRunInfo(last, roots),
            next,
            kinds,
            recommendations,
            activity,
            history,
            anyScanned ? null : FirstScanEstimate(roots, scan));
    }

    /// <summary>Meta key of the last folder that stopped being watched, as <c>{utc ticks}|{name}</c> (Recent activity).</summary>
    public const string RemovedFolderKey = "activity.folderRemoved";

    /// <summary>Meta key of the last update, as <c>{utc ticks}|{version}</c> (Recent activity).</summary>
    public const string UpdatedKey = "activity.updated";

    /// <summary>
    /// Days one full verification pass takes at the nightly budget: the library's bytes re-read at about 100 MB/s. The
    /// Dashboard's recommendation and the Schedule page's verification card both say it, so they share this. 0 while
    /// no file has a fingerprint.
    /// </summary>
    internal static int VerificationPassDays(long libraryBytes, long filesWithBaseline, int budgetMinutes)
    {
        double perNight = budgetMinutes * 60.0 * 100_000_000;
        return filesWithBaseline == 0 || perNight <= 0 ? 0 : (int)Math.Ceiling(libraryBytes / perNight);
    }

    /// <summary>The nightly budget as the Schedule page writes it: whole hours as <c>2 h</c>, otherwise minutes.</summary>
    internal static string BudgetText(int minutes) => minutes % 60 == 0
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Units.Hours, minutes / 60)
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Units.Minutes, minutes);

    /// <summary>
    /// The tiles' change and the sparkline (subplan 09, Tiles and Activity) from the open counts recorded at the end of
    /// each run (summary_json <c>openCritical</c>, <c>openHigh</c>, <c>openMedium</c>, <c>openLow</c>): what was open when
    /// the last completed scan began (the run before it ended there; nothing before the first scan of all), and the open
    /// total after each of the last 30 scans, oldest first. Runs that recorded no counts are left out.
    /// </summary>
    /// <param name="complete">The list holds every run, so a run with none before it was the first.</param>
    internal static (SeverityCounts? Before, List<double> History) OpenHistory(IReadOnlyList<RunRecord> runs, RunRecord? lastCompleted, bool complete)
    {
        List<(long Id, SeverityCounts Open)> recorded = [];
        foreach (RunRecord run in runs)
        {
            if (run.Status != RunStatus.Running && OpenAtEnd(run.SummaryJson) is SeverityCounts open)
            {
                recorded.Add((run.Id, open));
            }
        }

        SeverityCounts? before = null;
        if (lastCompleted is not null)
        {
            RunRecord? previous = runs.FirstOrDefault(r => r.Id < lastCompleted.Id && r.Status != RunStatus.Running);
            before = previous is null
                ? complete ? SeverityCounts.Zero : null
                : recorded.FirstOrDefault(r => r.Id == previous.Id).Open;
        }

        List<double> history = [.. recorded.Take(30).Select(r => (double)r.Open.Total)];
        history.Reverse();
        return (before, history);
    }

    /// <summary>The open counts a run recorded at its end; null when it recorded none.</summary>
    internal static SeverityCounts? OpenAtEnd(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(summaryJson);
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Count(root, "openCritical") is int critical && Count(root, "openHigh") is int high
                && Count(root, "openMedium") is int medium && Count(root, "openLow") is int low
                ? new SeverityCounts(critical, high, medium, low)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        static int? Count(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) && n >= 0 ? n : null;
    }

    /// <summary>
    /// "A first Standard scan takes about {time}" (subplan 09, not yet scanned): the Add folder counts of the enabled
    /// Library folders, timed with this machine's averages or the starting values before any scan. Null when a
    /// folder's size is not known.
    /// </summary>
    private TimeSpan? FirstScanEstimate(IReadOnlyList<RootRecord> roots, ScanSettings scan)
    {
        List<RootRecord> library = [.. roots.Where(r => r.Role == RootRole.Library && r.Enabled)];
        if (library.Count == 0)
        {
            return null;
        }

        long photos = 0, videos = 0, music = 0, bytes = 0;
        foreach (RootRecord root in library)
        {
            if (FolderCount.Parse(database.Meta.Get(FolderCount.Key(root.Id))) is not FolderCount count)
            {
                return null;
            }

            photos += count.Photos;
            videos += count.Videos;
            music += count.Music;
            bytes += count.Bytes;
        }

        Estimator estimator = new(Estimator.Deserialize(database.Meta.Get(ScanEngine.EstimatesKey)).Values.FirstOrDefault());
        Dictionary<MediaKind, long> files = new() { [MediaKind.Photo] = photos, [MediaKind.Video] = videos, [MediaKind.Audio] = music };
        return estimator.Estimate(bytes, files, ScanDepth.Standard, CpuGate.CapacityFor(scan.Speed, Environment.ProcessorCount));
    }

    /// <summary>
    /// Recent activity (subplan 09, Dashboard): scans, folders added and removed, restores and repairs from the journal,
    /// and updates, newest first, four at most. Only scans were listed.
    /// </summary>
    private List<ActivityItem> Activity(List<RunRecord> runs, List<RootRecord> roots)
    {
        System.Globalization.CultureInfo c = System.Globalization.CultureInfo.CurrentCulture;
        List<ActivityItem> items = [];
        foreach (RunRecord run in runs.Where(r => r.Status != RunStatus.Running).Take(4))
        {
            string text = run.Status switch
            {
                RunStatus.Completed or RunStatus.BudgetReached => run.Trigger == RunTrigger.Scheduled ? Strings.Activity.ScheduledFinished : Strings.Activity.ScanFinished,
                RunStatus.Cancelled => Strings.Activity.StoppedByYou,
                _ => Strings.Activity.CouldNotRun,
            };
            items.Add(new ActivityItem(text, Ticks(run.EndedUtc ?? run.StartedUtc)));
        }

        foreach (RootRecord root in roots.Where(r => r.AddedUtc.Year > 2000))
        {
            items.Add(new ActivityItem(string.Format(c, Strings.Activity.FolderAdded, root.Label ?? Path.GetFileName(root.Path.TrimEnd('\\'))), root.AddedUtc));
        }

        // A batch of one action is one entry; undo records and actions that change no file (quarantine, accept) are left out.
        foreach (IGrouping<long, OperationRecord> batch in database.Journal.List(200, status: OperationStatus.Done)
            .Where(o => o.DetailJson?.StartsWith("undo:", StringComparison.Ordinal) != true)
            .GroupBy(o => o.BatchId))
        {
            int n = batch.Count();
            string? text = batch.First().ActionId switch
            {
                Core.Rules.ActionIds.RestoreCopy => n == 1 ? Strings.Activity.RestoredOne : string.Format(c, Strings.Activity.Restored, UiFormat.Count(n)),
                Core.Rules.ActionIds.Remux or Core.Rules.ActionIds.FixExtension or Core.Rules.ActionIds.ShrinkBitDepth or Core.Rules.ActionIds.ReencodeFlac
                    or Core.Rules.ActionIds.Salvage or Core.Rules.ActionIds.ExtractPreview or Core.Rules.ActionIds.DeclipCopy
                    => n == 1 ? Strings.Activity.RepairedOne : string.Format(c, Strings.Activity.Repaired, UiFormat.Count(n)),
                _ => null,
            };
            if (text is not null)
            {
                items.Add(new ActivityItem(text, Ticks(batch.Max(o => o.Utc))));
            }
        }

        if (Marked(RemovedFolderKey) is (DateTimeOffset removed, string name))
        {
            items.Add(new ActivityItem(string.Format(c, Strings.Activity.FolderRemoved, name), removed));
        }

        if (Marked(UpdatedKey) is (DateTimeOffset updated, string version))
        {
            items.Add(new ActivityItem(string.Format(c, Strings.Activity.Updated, version), updated));
        }

        return [.. items.OrderByDescending(i => i.When).Take(4)];
    }

    /// <summary>A <c>{utc ticks}|{text}</c> meta value.</summary>
    private (DateTimeOffset When, string Text)? Marked(string key) =>
        database.Meta.Get(key) is string value && value.IndexOf('|', StringComparison.Ordinal) is int bar and > 0
        && long.TryParse(value.AsSpan(0, bar), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long ticks)
            ? (Ticks(ticks), value[(bar + 1)..])
            : null;

    /// <summary>
    /// The wording of subplan 09's recommendation table for one recommendation; <paramref name="passDays"/> is
    /// <see cref="VerificationPassDays"/> (it was made up from the overdue share, whatever the library's size).
    /// </summary>
    internal static RecommendationItem Word(Core.Remediation.Recommendation r, ScanSettings scan, int passDays)
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
            Core.Remediation.RecommendationKind.MissingIndex => new(r.Severity, UiFormat.Plural(r.FileCount, Strings.Recommendations.IndexTitleOne, Strings.Recommendations.IndexTitle), string.Format(c, Strings.Recommendations.IndexDetail, UiFormat.Duration(TimeSpan.FromSeconds(Math.Max(1, r.TotalBytes / 200_000_000.0)))), Strings.Recommendations.FixAll, "fix:" + r.ActionId, r.FileIds),
            Core.Remediation.RecommendationKind.WrongExtension => new(r.Severity, UiFormat.Plural(r.FileCount, Strings.Recommendations.ExtensionTitleOne, Strings.Recommendations.ExtensionTitle), Strings.Recommendations.ExtensionDetail, Strings.Recommendations.FixAll, "fix:" + r.ActionId, r.FileIds),
            Core.Remediation.RecommendationKind.FakeLossless => new(r.Severity, UiFormat.Plural(r.FileCount, Strings.Recommendations.FakeLosslessTitleOne, Strings.Recommendations.FakeLosslessTitle), Strings.Recommendations.CannotRepair, Strings.Recommendations.Review, "review:AUD-FAKE-LOSSLESS", r.FileIds),
            Core.Remediation.RecommendationKind.UpscaledVideo => new(r.Severity, UiFormat.Plural(r.FileCount, Strings.Recommendations.UpscaledTitleOne, Strings.Recommendations.UpscaledTitle), Strings.Recommendations.CannotRepair, Strings.Recommendations.Review, "review:VID-UPSCALED", r.FileIds),
            Core.Remediation.RecommendationKind.VerificationOverdue => new(
                r.Severity,
                string.Format(c, Strings.Recommendations.OverdueTitle, (r.Percent * 100).ToString("0", c) + "%", scan.ScrubPeriodDays),
                string.Format(c, Strings.Recommendations.OverdueDetail, BudgetText(scan.ScrubBudgetMinutes), UiFormat.Count(Math.Max(1, passDays))),
                Strings.Recommendations.VerifyNow,
                "verify"),
            Core.Remediation.RecommendationKind.NoBackupFolder => new(r.Severity, Strings.Recommendations.NoBackupTitle, Strings.Recommendations.NoBackupDetail, Strings.Recommendations.AddFolder, "add-folder"),
            Core.Remediation.RecommendationKind.NoSchedule => new(r.Severity, Strings.Recommendations.NoScheduleTitle, Strings.Recommendations.NoScheduleDetail, Strings.Recommendations.CreateSchedule, "schedule"),
            Core.Remediation.RecommendationKind.ReadErrors => new(r.Severity, string.Format(c, r.FileCount == 1 ? Strings.Recommendations.ReadErrorsTitleOne : Strings.Recommendations.ReadErrorsTitle, r.Place, n), Strings.Recommendations.ReadErrorsDetail, Strings.Recommendations.WhatToDo, "read-errors"),
            _ => new(r.Severity, Strings.Recommendations.InterruptedTitle, string.Format(c, Strings.Recommendations.InterruptedDetail, UiFormat.Count(r.Done), UiFormat.Count(r.Total)), Strings.Recommendations.Continue, "continue"),
        };
    }

    /// <summary>Stored UTC ticks to a timestamp.</summary>
    internal static DateTimeOffset Ticks(long utcTicks) => new(utcTicks, TimeSpan.Zero);
}
