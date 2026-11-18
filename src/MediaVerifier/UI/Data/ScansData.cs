using System.Globalization;
using System.Text.Json;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Scans page: finished runs newest first, the interrupted run to continue, the last completed scan.</summary>
internal sealed record ScansData(IReadOnlyList<RunInfo> History, RunInfo? Interrupted, DateTimeOffset? LastScan, string? RunningPlan);

/// <summary>A run in the drawer: its row, data read, alerts and the number of files and folders that could not be read.</summary>
internal sealed record RunDetail(RunInfo Run, long BytesRead, IReadOnlyList<(Severity Tier, string Text)> Alerts, long Errors);

/// <summary>What a scan of some folders covers, and how long each depth would take (null when unknown).</summary>
internal sealed record ScanEstimate(long Files, long Bytes, string Folders, TimeSpan? Quick, TimeSpan? Standard, TimeSpan? Deep)
{
    /// <summary>More than 10,000 files or 100 GB asks first (subplan 09, Before a large scan).</summary>
    public bool IsLarge => Files > 10_000 || Bytes > 100L * 1024 * 1024 * 1024;
}

/// <summary>
/// A folder's media as the Add folder dialog counted it, kept in the meta table until a scan indexes the folder: the
/// large-scan question and the first-scan estimate need its size before anything is in the files table.
/// </summary>
internal sealed record FolderCount(long Photos, long Videos, long Music, long Bytes)
{
    public long Files => Photos + Videos + Music;

    /// <summary>The meta key of a folder's count.</summary>
    public static string Key(long rootId) => "root." + rootId.ToString(CultureInfo.InvariantCulture) + ".counted";

    /// <summary>The meta value: <c>{photos}|{videos}|{music}|{bytes}</c>, invariant culture.</summary>
    public string ToMeta() => string.Create(CultureInfo.InvariantCulture, $"{Photos}|{Videos}|{Music}|{Bytes}");

    public static FolderCount? Parse(string? value)
    {
        string[] parts = value?.Split('|') ?? [];
        long[] numbers = new long[4];
        if (parts.Length != 4)
        {
            return null;
        }

        for (int i = 0; i < 4; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
            {
                return null;
            }
        }

        return new FolderCount(numbers[0], numbers[1], numbers[2], numbers[3]);
    }
}

internal partial interface IUiData
{
    ScansData LoadScans();

    RunDetail? LoadRunDetail(long runId);

    ScanEstimate LoadScanEstimate(IReadOnlyList<long> rootIds);
}

/// <summary>Scans snapshots from the library database (off the UI thread).</summary>
internal sealed partial class LiveUiData
{
    /// <summary>The engine writes alerts with lower-case names ("id", "severity", "rootId", "detail").</summary>
    private static readonly JsonSerializerOptions AlertJson = new(JsonSerializerDefaults.Web);

    public ScansData LoadScans()
    {
        List<RootRecord> roots = database.Roots.List();
        List<RunRecord> runs = database.Runs.List(50);
        List<RunInfo> history = [.. runs.Where(r => r.Status != RunStatus.Running).Select(r => ToRunInfo(r, roots))];
        RunInfo? interrupted = history.FirstOrDefault() is { Status: RunStatus.Interrupted } last ? last : null;
        DateTimeOffset? lastScan = LastCompleted(runs) is RunRecord completed ? Ticks(completed.EndedUtc ?? completed.StartedUtc) : null;
        string? runningPlan = runs.FirstOrDefault(r => r.Status == RunStatus.Running) is { PlanId: long planId }
            ? database.Plans.List().FirstOrDefault(p => p.Id == planId)?.Name
            : null;
        return new ScansData(history, interrupted, lastScan, runningPlan);
    }

    public RunDetail? LoadRunDetail(long runId)
    {
        if (database.Runs.Get(runId) is not RunRecord run)
        {
            return null;
        }

        return new RunDetail(ToRunInfo(run, database.Roots.List()), run.BytesRead, RunAlerts(run.AlertsJson), run.Errors);
    }

    /// <summary>
    /// A run's alerts as the drawer's info bars: the alert's title from the rule catalogue, then the engine's detail (a
    /// path, a count). The engine stores them with lower-case names, which the record's default binding did not match,
    /// so every alert came back empty and was drawn as a blank OK bar.
    /// </summary>
    internal static List<(Severity Tier, string Text)> RunAlerts(string? alertsJson)
    {
        List<(Severity, string)> alerts = [];
        if (string.IsNullOrWhiteSpace(alertsJson))
        {
            return alerts;
        }

        try
        {
            foreach (RunAlert? alert in JsonSerializer.Deserialize<List<RunAlert?>>(alertsJson, AlertJson) ?? [])
            {
                if (alert is null)
                {
                    continue;
                }

                RunAlertDefinition? definition = RuleCatalog.RunAlerts.FirstOrDefault(a => a.Id == alert.Id);
                Severity tier = alert.Severity != Severity.None ? alert.Severity : definition?.Severity ?? Severity.Medium;
                alerts.Add((tier, UiFormat.Facts(definition?.Title, alert.Detail)));
            }
        }
        catch (JsonException)
        {
            // An unreadable alert list shows no alerts.
        }

        return alerts;
    }

    public ScanEstimate LoadScanEstimate(IReadOnlyList<long> rootIds)
    {
        List<RootRecord> roots = database.Roots.List();
        Dictionary<long, RootSummary> summaries = database.Queries.RootSummaries().ToDictionary(s => s.RootId);
        Dictionary<MediaKind, long> perKind = database.Queries.KindSummaries().ToDictionary(k => k.Kind, k => k.Files);

        // Indexed folders by their rows; a folder no scan has indexed yet by the Add folder dialog's count (it used to
        // count as empty, so the first scan, the longest of all, never asked).
        long indexed = 0;
        long bytes = 0;
        FolderCount counted = new(0, 0, 0, 0);
        foreach (long id in rootIds)
        {
            if (summaries.TryGetValue(id, out RootSummary? summary) && summary.Files > 0)
            {
                indexed += summary.Files;
                bytes += summary.Bytes;
            }
            else if (FolderCount.Parse(database.Meta.Get(FolderCount.Key(id))) is FolderCount count)
            {
                counted = new FolderCount(counted.Photos + count.Photos, counted.Videos + count.Videos, counted.Music + count.Music, counted.Bytes + count.Bytes);
            }
        }

        long allFiles = Math.Max(1, perKind.Values.Sum());
        Dictionary<MediaKind, long> scoped = new()
        {
            [MediaKind.Photo] = (perKind.GetValueOrDefault(MediaKind.Photo) * indexed / allFiles) + counted.Photos,
            [MediaKind.Video] = (perKind.GetValueOrDefault(MediaKind.Video) * indexed / allFiles) + counted.Videos,
            [MediaKind.Audio] = (perKind.GetValueOrDefault(MediaKind.Audio) * indexed / allFiles) + counted.Music,
        };
        long files = indexed + counted.Files;
        bytes += counted.Bytes;
        string folders = ScopeText([.. rootIds.Select(id => new ScopeEntry(id, null, 0))], roots);
        Dictionary<StorageClass, EstimateAverages> averages = Estimator.Deserialize(database.Meta.Get(ScanEngine.EstimatesKey));
        if (averages.Count == 0)
        {
            // With no history yet the estimates read "Not known yet" (subplan 09).
            return new ScanEstimate(files, bytes, folders, null, null, null);
        }

        Estimator estimator = new(averages.Values.First());
        int parallel = CpuGate.CapacityFor(settings().Speed, Environment.ProcessorCount);
        return new ScanEstimate(
            files,
            bytes,
            folders,
            estimator.Estimate(bytes, scoped, ScanDepth.Quick, parallel),
            estimator.Estimate(bytes, scoped, ScanDepth.Standard, parallel),
            estimator.Estimate(bytes, scoped, ScanDepth.Deep, parallel));
    }

    /// <summary>The last scan that ran to its end (Completed or Time limit reached): "last scan" on the Dashboard and Scans.</summary>
    internal static RunRecord? LastCompleted(IEnumerable<RunRecord> runs) => runs.FirstOrDefault(r => r.Status is RunStatus.Completed or RunStatus.BudgetReached);

    internal static RunInfo ToRunInfo(RunRecord run, IReadOnlyList<RootRecord> roots) => new(
        run.Id,
        Ticks(run.StartedUtc),
        run.EndedUtc is long ended ? Ticks(ended) : null,
        run.Trigger,
        run.Depth,
        ScopeText(run.ScopeJson, roots),
        run.FilesSeen,
        run.NewFindings,
        run.ResolvedFindings,
        run.Status);

    /// <summary>
    /// A run's scope as it reads in a sentence: <c>all folders</c> when every Library folder was scanned whole, the
    /// folder names (a subfolder by its own name), or <c>{n} files</c> for a file list. Empty when unreadable.
    /// </summary>
    internal static string ScopeText(string scopeJson, IReadOnlyList<RootRecord> roots) =>
        ParseScope(scopeJson) is List<ScopeEntry> entries ? ScopeText(entries, roots) : string.Empty;

    /// <summary>The text of scopes about to be scanned (Continue names what it scans, not what the old run did).</summary>
    internal static string ScopeText(IReadOnlyList<ScanScope> scopes, IReadOnlyList<RootRecord> roots) =>
        ScopeText([.. scopes.Select(s => new ScopeEntry(s.RootId, s.RelativeDirectory, s.FileIds?.Count ?? 0))], roots);

    /// <summary>
    /// The scopes Continue scans again (subplan 05: a new run with the same scope): the run's folders and subfolders that
    /// are still watched and enabled. A file list continues as its folder, or its whole root, because the run kept only
    /// the number of files. Empty when none of them is left; null when the stored scope names no folder (unreadable).
    /// </summary>
    internal static List<ScanScope>? ScopesOf(string scopeJson, IReadOnlyList<RootRecord> roots)
    {
        if (ParseScope(scopeJson) is not { Count: > 0 } entries)
        {
            return null;
        }

        List<ScanScope> scopes = [];
        foreach (ScopeEntry entry in entries)
        {
            if (roots.FirstOrDefault(r => r.Id == entry.RootId) is not { Enabled: true } root)
            {
                continue;
            }

            ScanScope scope = new(root.Id, string.IsNullOrEmpty(entry.Dir) ? null : entry.Dir);
            if (!scopes.Contains(scope))
            {
                scopes.Add(scope);
            }
        }

        // A whole folder already covers its subfolders.
        return [.. scopes.Where(s => s.RelativeDirectory is null || !scopes.Any(o => o.RootId == s.RootId && o.RelativeDirectory is null))];
    }

    private static string ScopeText(IReadOnlyList<ScopeEntry> entries, IReadOnlyList<RootRecord> roots)
    {
        long files = entries.Sum(e => e.Files);
        if (files > 0)
        {
            return UiFormat.Plural(files, Strings.Repair.OneFile, Strings.Scans.FilesScope);
        }

        bool partial = entries.Any(e => !string.IsNullOrEmpty(e.Dir));
        List<RootRecord> library = [.. roots.Where(r => r.Role == RootRole.Library)];
        if (!partial && library.Count > 0 && library.All(r => entries.Any(e => e.RootId == r.Id)))
        {
            return Strings.Scans.AllFolders;
        }

        List<string> names = [];
        foreach (ScopeEntry entry in entries)
        {
            string? name = !string.IsNullOrEmpty(entry.Dir)
                ? Path.GetFileName(entry.Dir.TrimEnd('\\', '/'))
                : roots.FirstOrDefault(r => r.Id == entry.RootId) is RootRecord root ? root.Label ?? Path.GetFileName(root.Path.TrimEnd('\\')) : null;
            if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return UiFormat.JoinNames(names);
    }

    /// <summary>The engine's scope JSON (<c>[{"rootId","dir","files"}]</c>); null when it cannot be read.</summary>
    private static List<ScopeEntry>? ParseScope(string scopeJson)
    {
        List<ScopeEntry> entries = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(scopeJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }

            foreach (JsonElement scope in document.RootElement.EnumerateArray())
            {
                if (scope.ValueKind != JsonValueKind.Object || !scope.TryGetProperty("rootId", out JsonElement root) || root.ValueKind != JsonValueKind.Number || !root.TryGetInt64(out long id))
                {
                    continue;
                }

                string? dir = scope.TryGetProperty("dir", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                long files = scope.TryGetProperty("files", out JsonElement count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out long n) ? n : 0;
                entries.Add(new ScopeEntry(id, dir, files));
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return entries;
    }

    /// <summary>One entry of a run's scope: a root, a folder in it, or the number of chosen files.</summary>
    private sealed record ScopeEntry(long RootId, string? Dir, long Files);
}
