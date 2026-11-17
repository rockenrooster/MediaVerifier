using System.Globalization;
using System.Text.Json;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Scans page: finished runs newest first, the interrupted run to continue, the last scan.</summary>
internal sealed record ScansData(IReadOnlyList<RunInfo> History, RunInfo? Interrupted, DateTimeOffset? LastScan, bool HasFolders, string? RunningPlan);

/// <summary>A run in the drawer: its row, data read, alerts and errors.</summary>
internal sealed record RunDetail(RunInfo Run, long BytesRead, long Analysed, long Verified, IReadOnlyList<(Severity Tier, string Text)> Alerts, long Errors);

/// <summary>What a scan of some folders covers, and how long each depth would take (null when unknown).</summary>
internal sealed record ScanEstimate(long Files, long Bytes, string Folders, TimeSpan? Quick, TimeSpan? Standard, TimeSpan? Deep)
{
    /// <summary>More than 10,000 files or 100 GB asks first (subplan 09, Before a large scan).</summary>
    public bool IsLarge => Files > 10_000 || Bytes > 100L * 1024 * 1024 * 1024;
}

internal interface IScansData
{
    ScansData LoadScans();

    RunDetail? LoadRunDetail(long runId);

    ScanEstimate LoadScanEstimate(IReadOnlyList<long> rootIds);
}

/// <summary>Scans snapshots from the library database (off the UI thread).</summary>
internal sealed partial class LiveUiData
{
    public ScansData LoadScans()
    {
        List<RootRecord> roots = database.Roots.List();
        List<RunRecord> runs = database.Runs.List(50);
        Dictionary<long, string> plans = database.Plans.List().ToDictionary(p => p.Id, p => p.Name);
        List<RunInfo> history = [.. runs.Where(r => r.Status != RunStatus.Running).Select(r => ToRunInfo(r, roots, plans))];
        RunInfo? interrupted = history.FirstOrDefault() is { Status: RunStatus.Interrupted } last ? last : null;
        DateTimeOffset? lastScan = history.FirstOrDefault(r => r.Status is RunStatus.Completed or RunStatus.BudgetReached)?.Ended;
        string? runningPlan = runs.FirstOrDefault(r => r.Status == RunStatus.Running) is { PlanId: long planId } && plans.TryGetValue(planId, out string? planName) ? planName : null;
        return new ScansData(history, interrupted, lastScan, roots.Count > 0, runningPlan);
    }

    public RunDetail? LoadRunDetail(long runId)
    {
        List<RootRecord> roots = database.Roots.List();
        Dictionary<long, string> plans = database.Plans.List().ToDictionary(p => p.Id, p => p.Name);
        RunRecord? run = database.Runs.List(200).FirstOrDefault(r => r.Id == runId);
        if (run is null)
        {
            return null;
        }

        List<(Severity, string)> alerts = [];
        if (!string.IsNullOrWhiteSpace(run.AlertsJson))
        {
            try
            {
                foreach (RunAlert alert in JsonSerializer.Deserialize<List<RunAlert>>(run.AlertsJson) ?? [])
                {
                    alerts.Add((alert.Severity, alert.Detail));
                }
            }
            catch (JsonException)
            {
                // An unreadable alert list shows no alerts.
            }
        }

        return new RunDetail(ToRunInfo(run, roots, plans), run.BytesRead, run.FilesAnalysed, run.FilesVerified, alerts, run.Errors);
    }

    public ScanEstimate LoadScanEstimate(IReadOnlyList<long> rootIds)
    {
        List<RootRecord> roots = database.Roots.List();
        Dictionary<long, RootSummary> summaries = database.Queries.RootSummaries().ToDictionary(s => s.RootId);
        Dictionary<MediaKind, long> perKind = database.Queries.KindSummaries().ToDictionary(k => k.Kind, k => (long)k.Files);
        long files = rootIds.Sum(id => summaries.GetValueOrDefault(id)?.Files ?? 0);
        long bytes = rootIds.Sum(id => summaries.GetValueOrDefault(id)?.Bytes ?? 0);
        long allFiles = Math.Max(1, perKind.Values.Sum());
        Dictionary<MediaKind, long> scoped = perKind.ToDictionary(p => p.Key, p => p.Value * files / allFiles);
        Dictionary<StorageClass, EstimateAverages> averages = Estimator.Deserialize(database.Meta.Get(ScanEngine.EstimatesKey));
        string folders = FoldersText(roots, rootIds);
        if (averages.Count == 0)
        {
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

    internal static RunInfo ToRunInfo(RunRecord run, IReadOnlyList<RootRecord> roots, IReadOnlyDictionary<long, string> plans) => new(
        run.Id,
        Ticks(run.StartedUtc),
        run.EndedUtc is long ended ? Ticks(ended) : null,
        run.Trigger,
        run.PlanId is long plan && plans.TryGetValue(plan, out string? name) ? name : null,
        run.Depth,
        ScopeText(run.ScopeJson, roots),
        run.FilesSeen,
        run.NewFindings,
        run.ResolvedFindings,
        run.Status);

    /// <summary>The Folders column: <c>All folders</c>, folder names, or <c>{n} files</c> for a file list.</summary>
    internal static string ScopeText(string scopeJson, IReadOnlyList<RootRecord> roots)
    {
        List<long> rootIds = [];
        long files = 0;
        bool partial = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(scopeJson);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement scope in document.RootElement.EnumerateArray())
                {
                    if (scope.TryGetProperty("rootId", out JsonElement root) && root.TryGetInt64(out long id))
                    {
                        rootIds.Add(id);
                    }

                    if (scope.TryGetProperty("files", out JsonElement count) && count.ValueKind == JsonValueKind.Number)
                    {
                        files += count.GetInt64();
                    }

                    partial |= scope.TryGetProperty("dir", out JsonElement dir) && dir.ValueKind == JsonValueKind.String && dir.GetString()!.Length > 0;
                }
            }
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        if (files > 0)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.Scans.FilesScope, UiFormat.Count(files));
        }

        return partial ? FoldersText(roots, rootIds, sentence: false) : FoldersText(roots, rootIds);
    }

    private static string FoldersText(IReadOnlyList<RootRecord> roots, IReadOnlyList<long> rootIds, bool sentence = false)
    {
        List<RootRecord> library = [.. roots.Where(r => r.Role == RootRole.Library)];
        if (library.Count > 0 && library.All(r => rootIds.Contains(r.Id)))
        {
            string all = Strings.Scans.AllFolders;
            return sentence ? all : char.ToUpper(all[0], CultureInfo.CurrentCulture) + all[1..];
        }

        return UiFormat.JoinNames([.. roots.Where(r => rootIds.Contains(r.Id)).Select(r => r.Label ?? Path.GetFileName(r.Path.TrimEnd('\\')))]);
    }
}
