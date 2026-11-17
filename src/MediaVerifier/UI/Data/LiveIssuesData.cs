using System.Globalization;
using System.Text;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Issues and drawer snapshots from the library database (off the UI thread).</summary>
internal sealed partial class LiveUiData
{
    public IssuesData LoadIssues(IssuesQuery query)
    {
        // The list and its counts are independent reads; at a million findings each takes up to a few
        // hundred milliseconds, so the counts run beside the list on the reader pool.
        Task<SeverityCounts> tiers = Task.Run(() => database.Queries.SeverityCounts(query.Filter));
        Task<Dictionary<string, int>> rules = Task.Run(() => database.Queries.CountsByRule(query.Filter));
        // Without filters beyond the tiers, the tier counts are already the totals.
        FindingFilter filter = query.Filter;
        bool narrowed = filter.Kinds is not null || filter.Categories is not null || filter.RuleIds is not null || filter.RootId is not null
            || filter.DirId is not null || !string.IsNullOrWhiteSpace(filter.Text) || filter.FirstRun is not null || filter.Status != FindingStatus.Open;
        Task<SeverityCounts> all = narrowed ? Task.Run(() => database.Queries.SeverityCounts()) : tiers;
        Task<List<FindingGroupCount>>? groupCounts = query.Group == FindingGroup.Severity ? null : Task.Run(() => database.Queries.FindingGroupCounts(query.Filter, query.Group));
        long[] ids = database.Queries.FindingIds(query.Filter, query.Sort, query.Descending, query.Group);
        List<RootRecord> roots = database.Roots.List();
        RunRecord? last = database.Runs.List(5).FirstOrDefault(r => r.Status != RunStatus.Running);

        // The number of scanned files only fills the empty states, so it is read only for an empty list.
        long scanned = ids.Length > 0 ? -1 : database.Queries.Health(TimeSpan.FromDays(settings().ScrubPeriodDays), clock.UtcNow).FilesScanned;
        Task.WaitAll(tiers, rules, all);

        // Grouped by tier, the groups are the tier counts within the tier filter, in list order.
        List<FindingGroupCount> groups = groupCounts?.Result ?? TierGroups(tiers.Result, query.Filter.Severities);
        return new IssuesData(
            query,
            ids,
            [.. groups.Select(g => new IssueGroup(g.Key, g.Tier is Severity tier ? UiFormat.TierName(tier) : g.Label, g.Tier, g.Count, g.Count))],
            tiers.Result,
            rules.Result,
            [.. roots.Select(r => (r.Id, r.Label ?? Path.GetFileName(r.Path.TrimEnd('\\'))))],
            all.Result.Total,
            roots.Count > 0,
            scanned != 0,
            Math.Max(0, scanned),
            last is null ? null : Ticks(last.EndedUtc ?? last.StartedUtc));
    }

    private static List<FindingGroupCount> TierGroups(SeverityCounts counts, IReadOnlyCollection<Severity>? filter)
    {
        List<FindingGroupCount> groups = [];
        foreach (Severity tier in new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low })
        {
            int count = counts[tier];
            if (count > 0 && (filter is not { Count: > 0 } || filter.Contains(tier)))
            {
                groups.Add(new FindingGroupCount(tier.ToString(), tier.ToString(), tier, count));
            }
        }

        return groups;
    }

    public IReadOnlyList<IssueRow> LoadIssueRows(IReadOnlyList<long> ids)
    {
        List<FindingRow> rows = database.Queries.FindingRows(ids);
        List<IssueRow> result = new(rows.Count);
        foreach (FindingRow row in rows)
        {
            string folder = row.RelDir.Length == 0 ? row.RootPath : Path.Combine(row.RootPath, row.RelDir);
            result.Add(new IssueRow(row.Id, row.FileId, row.RuleId, row.Severity, row.Confidence, row.FileName, Line2(row.RuleId, row.Confidence, row.Summary), folder, row.Size, row.Kind, Path.Combine(folder, row.FileName)));
        }

        return result;
    }

    public FileDetail? LoadFileDetail(long fileId)
    {
        FileRecord? file = database.Files.Get(fileId);
        if (file is null)
        {
            return null;
        }

        RootRecord? root = database.Roots.Get(file.RootId);
        string rootPath = root?.Path ?? string.Empty;
        string folder = string.IsNullOrEmpty(file.RelDir) ? rootPath : Path.Combine(rootPath, file.RelDir);
        List<string> crumbs = [.. folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)];
        if (crumbs.Count > 0 && crumbs[0].EndsWith(':'))
        {
            crumbs[0] += string.Empty;
        }

        List<FindingDetail> findings = [];
        long[] zeroRuns = [];
        foreach (FindingRecord finding in database.Findings.ForFile(fileId).Where(f => f.Status == FindingStatus.Open).OrderByDescending(f => f.Severity).ThenByDescending(f => f.Confidence))
        {
            IReadOnlyDictionary<string, object> evidence = EvidenceJson.Deserialize(finding.EvidenceJson);
            RuleDefinition rule = RuleCatalog.TryGet(finding.RuleId, out RuleDefinition found) ? found : RuleCatalog.All[0];
            EvidenceView view = ViewFor(finding.RuleId, evidence);
            if (evidence.TryGetValue("runOffsetsBytes", out object? runs) && runs is object[] values)
            {
                zeroRuns = [.. values.Select(v => Convert.ToInt64(v, CultureInfo.InvariantCulture))];
            }

            findings.Add(new FindingDetail(
                finding.Id,
                finding.RuleId,
                finding.Severity,
                finding.Confidence,
                rule.Title,
                finding.Summary,
                RuleText.Render(rule.WhatItMeans, evidence),
                RuleText.Render(rule.WhyItMatters, evidence),
                RuleText.Render(rule.WhatYouCanDo, evidence),
                view,
                evidence,
                view == EvidenceView.Spectrogram ? SpectralCaption(evidence) : finding.Summary));
        }

        List<(string, DateTimeOffset)> history = [.. database.Events.ForFile(fileId).OrderByDescending(e => e.Utc).Select(e => (EventText(e), Ticks(e.Utc)))];
        string? recommended = null;
        if (findings.Count > 0 && root is not null && file.State != FileState.Missing)
        {
            bool healthy = MediaVerifier.Core.Remediation.ActionCatalog.HasHealthyCopy(database, MediaVerifier.Core.Engine.WindowsFileSystem.Instance, file);
            bool clean = !findings.Any(f => f.RuleId is "COR-DECODE" or "COR-TRUNCATED" or "COR-CONCEALED");
            recommended = MediaVerifier.Core.Remediation.ActionCatalog.Recommended(MediaVerifier.Core.Remediation.ActionCatalog.ForFinding(findings[0].RuleId, file, root, healthy, clean));
        }

        FingerprintState fingerprint = file.Hash is null ? FingerprintState.None
            : findings.Any(f => f.RuleId == "INT-ROT") ? FingerprintState.Changed
            : file.VerifyCount > 0 ? FingerprintState.Unchanged : FingerprintState.NeverVerified;
        return new FileDetail(
            file.Id,
            file.Name,
            Path.Combine(folder, file.Name),
            crumbs,
            file.MediaKind,
            FormatLine(file),
            fingerprint,
            file.HashUtc is long hashed ? Ticks(hashed) : null,
            file.VerifyCount,
            file.LastVerifiedUtc is long verified ? Ticks(verified) : null,
            findings,
            history,
            TechnicalText(file),
            file.Size,
            zeroRuns,
            recommended is null ? string.Empty : ActionName(recommended),
            recommended ?? string.Empty);
    }

    public AudioEvidence? LoadAudioEvidence(FileDetail detail, CancellationToken token)
    {
        if (evidenceRenderer is null || detail.Kind != MediaKind.Audio || database.Files.Get(detail.FileId) is not FileRecord file)
        {
            return null;
        }

        string key = EvidenceRenderer.CacheKey(file.Id, file.Hash, file.Size, file.MtimeUtc);
        try
        {
            return evidenceRenderer.RenderAsync(detail.FullPath, key, EvidenceMarks(detail), token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An offline drive, a file in use or a missing media engine: the drawer shows the frame without a picture.
            return null;
        }
    }

    /// <summary>The positions marked on the waveform: clipped runs and dropouts, in time order.</summary>
    internal static double[] EvidenceMarks(FileDetail detail) =>
        [.. detail.Findings.Where(f => f.View == EvidenceView.Waveform).SelectMany(f => Positions(f.Evidence)).Distinct().Order().Take(MarkWindowCollector.MaxMarks)];

    internal static IEnumerable<double> Positions(IReadOnlyDictionary<string, object> evidence) =>
        evidence.TryGetValue("positionsSeconds", out object? value) && value is object[] list
            ? list.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture))
            : [];

    /// <summary>Row line 2: the rule title with its confidence wording, then the evidence phrase.</summary>
    internal static string Line2(string ruleId, float confidence, string summary)
    {
        string title = RuleCatalog.TryGet(ruleId, out RuleDefinition rule) ? rule.Title : ruleId;
        string phrase = summary.Length > 1 && char.IsUpper(summary[0]) && char.IsLower(summary[1]) ? char.ToLowerInvariant(summary[0]) + summary[1..] : summary;
        return SeverityPolicy.Hedge(title, confidence) + Strings.Separator + phrase;
    }

    /// <summary>The format line of the drawer header (subplan 09, Detail drawer).</summary>
    internal static string FormatLine(FileRecord file)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        MediaSummary m = file.Media;
        string size = UiFormat.Size(file.Size);
        string? duration = m.DurationMs is long ms ? Core.Analysis.Detectors.EvidenceText.MediaTime(ms / 1000.0) : null;
        switch (file.MediaKind)
        {
            case MediaKind.Audio:
                string? channels = m.Channels switch { 1 => Strings.Drawer.Mono, 2 => Strings.Drawer.Stereo, 6 => Strings.Drawer.Surround, int n => n.ToString(culture), _ => null };
                return UiFormat.Facts(
                    m.AudioCodec?.ToUpperInvariant(),
                    m.SampleRate is int rate ? string.Format(culture, Strings.Drawer.Kilohertz, (rate / 1000.0).ToString("0.#", culture)) : null,
                    m.BitDepth is int bits ? string.Format(culture, Strings.Drawer.Bits, bits) : null,
                    channels,
                    duration,
                    size);
            case MediaKind.Video:
                return UiFormat.Facts(
                    m.Container?.ToUpperInvariant(),
                    m.VideoCodec?.ToUpperInvariant(),
                    m.Height is int height ? string.Format(culture, Strings.Drawer.Progressive, height) : null,
                    m.FpsMilli is int fps ? string.Format(culture, Strings.Drawer.Fps, (fps / 1000.0).ToString("0.##", culture)) : null,
                    duration,
                    size);
            default:
                return UiFormat.Facts(
                    (m.VideoCodec ?? file.Ext).ToUpperInvariant(),
                    m.Width is int w && m.Height is int h ? string.Format(culture, Strings.Drawer.Dimensions, w, h) : null,
                    size);
        }
    }

    /// <summary>The caption under a spectrogram: <c>Cutoff 16.6 kHz · drop 67 dB · only rounding noise above</c>.</summary>
    private static string SpectralCaption(IReadOnlyDictionary<string, object> evidence)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        double knee = Convert.ToDouble(evidence["kneeHz"], CultureInfo.InvariantCulture);
        string cutoff = string.Format(culture, Strings.Drawer.CutoffLine, (knee / 1000).ToString("0.0", culture));
        string? drop = evidence.TryGetValue("dropDb", out object? d) ? string.Format(culture, Strings.Drawer.DropLine, Math.Round(Convert.ToDouble(d, CultureInfo.InvariantCulture)).ToString("0", culture)) : null;
        string? noise = evidence.TryGetValue("aboveVsFloorDb", out object? f) && Convert.ToDouble(f, CultureInfo.InvariantCulture) <= 6 ? Strings.Drawer.OnlyRoundingNoise : null;
        return UiFormat.Facts(cutoff, drop, noise);
    }

    private static EvidenceView ViewFor(string ruleId, IReadOnlyDictionary<string, object> evidence) => ruleId switch
    {
        "GEN-ZEROFILL" or "INT-ROT" => EvidenceView.ByteMap,
        "AUD-FAKE-LOSSLESS" or "AUD-UPSAMPLED" or "AUD-BITRATE-INFLATED" when evidence.ContainsKey("kneeHz") => EvidenceView.Spectrogram,
        "AUD-CLIPPING" or "AUD-OVERS" or "COR-DROPOUT" or "COR-CLICKS" or "AUD-SILENT" or "AUD-LONG-SILENCE" => EvidenceView.Waveform,
        _ when evidence.ContainsKey("messages") => EvidenceView.Messages,
        _ => EvidenceView.None,
    };

    private static string EventText(FileEventRecord e) => e.Kind switch
    {
        FileEventKind.FirstSeen => Strings.Events.FirstSeen,
        FileEventKind.Baselined => Strings.Events.Baselined,
        FileEventKind.Verified => Strings.Events.Verified,
        FileEventKind.Modified => Strings.Events.Modified,
        FileEventKind.Rot => Strings.Events.Rot,
        FileEventKind.MetaDrift => Strings.Events.MetaDrift,
        FileEventKind.Moved => string.Format(CultureInfo.CurrentCulture, Strings.Events.Moved, e.DetailJson ?? string.Empty),
        FileEventKind.Missing => Strings.Events.Missing,
        FileEventKind.Returned => Strings.Events.CameBack,
        FileEventKind.Repaired => string.Format(CultureInfo.CurrentCulture, Strings.Events.Repaired, e.DetailJson ?? string.Empty),
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Events.Restored, e.DetailJson ?? string.Empty),
    };

    private static string TechnicalText(FileRecord file)
    {
        StringBuilder text = new();
        if (file.Hash is not null)
        {
            text.Append(Strings.Drawer.FileFingerprint).AppendLine(Convert.ToHexStringLower(file.Hash));
        }

        if (file.StreamHash is not null)
        {
            text.Append(Strings.Drawer.StreamFingerprint).AppendLine(Convert.ToHexStringLower(file.StreamHash));
        }

        return text.ToString();
    }
}
