using System.Globalization;
using System.Text;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Issues and drawer snapshots from the library database (off the UI thread).</summary>
internal sealed partial class LiveUiData
{
    /// <summary>The rules whose finding means the file's content is no longer what was fingerprinted: only then can a copy with the old fingerprint be healthier.</summary>
    private static readonly string[] ChangedContentRules = ["INT-ROT", "GEN-UNREADABLE"];

    /// <summary>The lowest tier those findings take (Critical): a file whose worst open finding is below it has none of them.</summary>
    private static readonly Severity ChangedContentTier = ChangedContentRules.Min(r => RuleCatalog.Get(r).MinSeverity);

    public IssuesData LoadIssues(IssuesQuery query)
    {
        // The list and its counts are separate reads, each in its own snapshot. A scan that commits between them
        // made the group sizes disagree with the ids: rows under the wrong header, or an index past the end of the
        // list while painting. The reads are taken again when they disagree; a last disagreement is reconciled.
        IssuesData data = LoadIssuesOnce(query);
        for (int attempt = 0; attempt < 2 && data.Groups.Sum(g => g.Rows) != data.Ids.Length; attempt++)
        {
            data = LoadIssuesOnce(query);
        }

        return data with { Groups = Reconcile(data.Groups, data.Ids.Length) };
    }

    /// <summary>Group sizes that add up to the number of ids: the counts as read, cut or extended at the end.</summary>
    internal static IReadOnlyList<IssueGroup> Reconcile(IReadOnlyList<IssueGroup> groups, int ids)
    {
        if (groups.Sum(g => g.Rows) == ids)
        {
            return groups;
        }

        List<IssueGroup> fitted = new(groups.Count);
        int left = ids;
        for (int i = 0; i < groups.Count; i++)
        {
            int rows = i == groups.Count - 1 ? left : Math.Min(groups[i].Rows, left);
            if (rows > 0)
            {
                fitted.Add(groups[i] with { Rows = rows });
            }

            left -= rows;
        }

        return fitted;
    }

    private IssuesData LoadIssuesOnce(IssuesQuery query)
    {
        // The list and its counts are independent reads; at a million findings each takes up to a few
        // hundred milliseconds, so the counts run beside the list on the reader pool.
        Task<SeverityCounts> tiers = Task.Run(() => database.Queries.SeverityCounts(query.Filter));
        // Every problem the other filters leave, so the Rule list keeps its choices while one is chosen.
        Task<Dictionary<string, int>> rules = Task.Run(() => database.Queries.CountsByRule(query.Filter with { RuleIds = null }));
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

        // GetResult rethrows a failed read as itself (a SqliteException the page logs), not wrapped in an
        // AggregateException that escaped the page's handler and left the list stale without a word.
        SeverityCounts tierCounts = tiers.GetAwaiter().GetResult();
        Dictionary<string, int> ruleCounts = rules.GetAwaiter().GetResult();
        SeverityCounts allCounts = all.GetAwaiter().GetResult();

        // Grouped by tier, the groups are the tier counts within the tier filter, in list order.
        List<FindingGroupCount> groups = groupCounts?.GetAwaiter().GetResult() ?? TierGroups(tierCounts, query.Filter.Severities);
        return new IssuesData(
            query,
            ids,
            [.. groups.Select(g => new IssueGroup(g.Key, g.Tier is Severity tier ? UiFormat.TierName(tier) : g.Label, g.Tier, g.Count, g.Count))],
            tierCounts,
            ruleCounts,
            [.. roots.Select(r => (r.Id, r.Label ?? Path.GetFileName(r.Path.TrimEnd('\\'))))],
            allCounts.Total,
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

    public SeverityCounts CountIssues(FindingFilter filter) => database.Queries.SeverityCounts(filter);

    public IReadOnlyList<IssueRow> LoadIssueRows(IReadOnlyList<long> ids, bool withFixes = true)
    {
        List<FindingRow> rows = database.Queries.FindingRows(ids);
        Dictionary<long, RootRecord> roots = withFixes ? database.Roots.List().ToDictionary(r => r.Id) : [];
        Dictionary<long, (bool HasCopy, bool Clean)> facts = withFixes ? FileFacts(rows) : [];
        List<IssueRow> result = new(rows.Count);
        foreach (FindingRow row in rows)
        {
            string folder = row.RelDir.Length == 0 ? row.RootPath : Path.Combine(row.RootPath, row.RelDir);
            RootRecord root = roots.GetValueOrDefault(row.RootId) ?? new RootRecord { Id = row.RootId, Path = row.RootPath };
            (bool hasCopy, bool clean) = facts.GetValueOrDefault(row.FileId);
            result.Add(new IssueRow(row.Id, row.FileId, row.RuleId, row.Severity, row.FileName, row.Summary, folder, row.Size, row.Kind, Path.Combine(folder, row.FileName), Title(row.RuleId, row.Confidence))
            {
                Fixes = withFixes ? ListFixes(row.RuleId, row.Kind, row.FileName, root, hasCopy, clean) : [],
            });
        }

        return result;
    }

    /// <summary>
    /// Per file, what its fixes depend on beyond the finding: Restore needs changed content (rot, unreadable) and a copy
    /// in the index with the old fingerprint (database only; the copy is checked again before it is used); Rewrite FLAC
    /// and Store as 16-bit need audio that nothing says is damaged. Both are judged on all the file's open findings, as
    /// the drawer judges them: a rotted file's decode row offered Restore only when its rot row was read in the same
    /// window. Only files that can take those fixes are looked at, and their other findings are read only when the rows
    /// at hand are not all of them.
    /// </summary>
    private Dictionary<long, (bool HasCopy, bool Clean)> FileFacts(List<FindingRow> rows)
    {
        Dictionary<long, (bool HasCopy, bool Clean)> facts = [];
        ILookup<long, FindingRow> byFile = rows.ToLookup(r => r.FileId);
        foreach (FileRecord file in database.Files.GetMany([.. byFile.Select(g => g.Key)]))
        {
            bool mayHaveChanged = file.Hash is not null && file.WorstSeverity >= ChangedContentTier;
            bool rewritable = (file.Ext.Equals("flac", StringComparison.OrdinalIgnoreCase) || file.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase))
                && byFile[file.Id].Any(r => RuleCatalog.TryGet(r.RuleId, out RuleDefinition rule) && rule.ActionIds.Any(a => a is ActionIds.ReencodeFlac or ActionIds.ShrinkBitDepth));
            if (!mayHaveChanged && !rewritable)
            {
                continue;
            }

            List<FindingRow> open = [.. byFile[file.Id].Where(r => r.Status == FindingStatus.Open)];
            List<(string RuleId, IReadOnlyDictionary<string, object> Evidence)> findings = open.Count >= file.OpenFindings
                ? [.. open.Select(r => (r.RuleId, (IReadOnlyDictionary<string, object>)EvidenceJson.Deserialize(r.EvidenceJson)))]
                : [.. database.Findings.ForFile(file.Id).Where(f => f.Status == FindingStatus.Open).Select(f => (f.RuleId, (IReadOnlyDictionary<string, object>)EvidenceJson.Deserialize(f.EvidenceJson)))];
            bool changed = mayHaveChanged && findings.Any(f => ChangedContentRules.Contains(f.RuleId));
            facts[file.Id] = (changed && HasIndexedCopy(file), DecodesCleanly(findings));
        }

        return facts;
    }

    /// <summary>
    /// The fixes a list offers for a finding (subplan 12, Action catalogue): what applies to the file's kind, format
    /// and folder, and runnable. Quarantine has its own button and menu item; the Recycle Bin and accepting a change are
    /// asked for in the drawer, one file at a time.
    /// </summary>
    internal static IReadOnlyList<string> ListFixes(string ruleId, MediaKind kind, string fileName, RootRecord root, bool hasHealthyCopy, bool decodesCleanly)
    {
        FileRecord file = new() { Name = fileName, Ext = MediaClassifier.Extension(fileName), MediaKind = kind };
        return [.. ActionsFor(ruleId, file, root, hasHealthyCopy, decodesCleanly)
            .Where(a => ActionCatalog.Runnable.Contains(a) && a is not (ActionIds.Quarantine or ActionIds.Recycle or ActionIds.AcceptBaseline))];
    }

    /// <summary>
    /// The catalogue's actions for a finding, with Store as 16-bit held to what Rewrite FLAC needs: both decode and
    /// encode the audio again, so on a file that does not decode cleanly (frames failing their checksums) either one
    /// stores the damage under fresh, valid checksums and closes the finding that reported it.
    /// </summary>
    private static IEnumerable<string> ActionsFor(string ruleId, FileRecord file, RootRecord root, bool hasHealthyCopy, bool decodesCleanly) =>
        ActionCatalog.ForFinding(ruleId, file, root, hasHealthyCopy, decodesCleanly).Where(a => decodesCleanly || a != ActionIds.ShrinkBitDepth);

    /// <summary>
    /// True when nothing says the file's sound or picture is damaged: no decode, truncation or concealment finding, and
    /// no frame checksum errors. A FLAC whose frames fail their checksums decodes without a decoder error, and
    /// rewriting it would store the damaged audio under a fresh, valid checksum.
    /// </summary>
    internal static bool DecodesCleanly(IEnumerable<(string RuleId, IReadOnlyDictionary<string, object> Evidence)> findings) =>
        !findings.Any(f => f.RuleId is "COR-DECODE" or "COR-TRUNCATED" or "COR-CONCEALED"
            || (f.RuleId == "COR-CHECKSUM" && f.Evidence.TryGetValue("checksumErrors", out object? errors) && Convert.ToInt64(errors, CultureInfo.InvariantCulture) > 0));

    /// <summary>Another, present file in the index has the file's fingerprint (database only, no disk access).</summary>
    private bool HasIndexedCopy(FileRecord file) =>
        file.Hash is { Length: > 0 } hash && file.HashSize is > 0
        && database.Files.FindByHash(hash).Any(copy => copy.Id != file.Id && copy.State != FileState.Missing && copy.HashSize is > 0);

    public long? FileOfFinding(long findingId) => database.Findings.Get(findingId)?.FileId;

    public FileDetail? LoadFileDetail(long fileId) => LoadFileDetail(fileId, FindingStatus.Open, findCopies: true);

    public FileDetail? LoadFileDetail(long fileId, FindingStatus status, bool findCopies)
    {
        FileRecord? file = database.Files.Get(fileId);
        if (file is null)
        {
            return null;
        }

        RootRecord? root = database.Roots.Get(file.RootId);
        string rootPath = root?.Path ?? string.Empty;
        string folder = string.IsNullOrEmpty(file.RelDir) ? rootPath : Path.Combine(rootPath, file.RelDir);
        string[] rootCrumbs = rootPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        List<string> crumbs = [.. rootCrumbs, .. (file.RelDir ?? string.Empty).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)];

        // The open findings first, then (in the Ignored or Resolved view) the ones the list shows: details of an ignored
        // finding used to open a drawer without it.
        List<FindingDetail> findings = [];
        long[] zeroRuns = [];
        List<FindingRecord> shown = [.. database.Findings.ForFile(fileId).Where(f => f.Status == FindingStatus.Open || f.Status == status)];
        foreach (FindingRecord finding in shown.OrderBy(f => f.Status == FindingStatus.Open ? 0 : 1).ThenByDescending(f => f.Severity).ThenByDescending(f => f.Confidence))
        {
            IReadOnlyDictionary<string, object> evidence = EvidenceJson.Deserialize(finding.EvidenceJson);
            RuleDefinition rule = RuleCatalog.TryGet(finding.RuleId, out RuleDefinition found) ? found : RuleCatalog.All[0];
            EvidenceView view = ViewFor(finding.RuleId, evidence);
            long[]? runs = view == EvidenceView.ByteMap ? ZeroRuns(evidence) : null;
            if (zeroRuns.Length == 0 && Offsets(evidence) is { Length: > 0 } offsets)
            {
                zeroRuns = offsets;
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
                view switch
                {
                    EvidenceView.Spectrogram => SpectralCaption(evidence),
                    EvidenceView.Bitrate => string.Empty,
                    _ => finding.Summary,
                },
                finding.Status,
                runs));
        }

        List<(string, DateTimeOffset)> history = [.. database.Events.ForFile(fileId).OrderByDescending(e => e.Utc).Select(e => (EventText(e), Ticks(e.Utc)))];

        // The primary button: the first open finding, most serious first, that the app can fix; the More menu: every fix.
        // A healthy copy is looked for only when the content changed (rot, unreadable): for any other finding a copy
        // with the same fingerprint holds the same damage. The search can wait on an offline backup drive, so the
        // drawer first opens without it.
        string? recommended = null;
        List<string> fixes = [];
        List<FindingDetail> open = [.. findings.Where(f => f.Status == FindingStatus.Open)];
        bool changedContent = open.Any(f => ChangedContentRules.Contains(f.RuleId));
        bool copiesPending = false;
        if (open.Count > 0 && root is not null && file.State != FileState.Missing)
        {
            bool healthy = false;
            if (changedContent && file.Hash is not null)
            {
                healthy = findCopies && ActionCatalog.HasHealthyCopy(database, WindowsFileSystem.Instance, file);
                copiesPending = !findCopies;
            }

            bool clean = DecodesCleanly(open.Select(f => (f.RuleId, f.Evidence)));
            foreach (FindingDetail finding in open)
            {
                IReadOnlyList<string> actions = [.. ActionsFor(finding.RuleId, file, root, healthy, clean)];
                recommended ??= ActionCatalog.Recommended(actions);
                foreach (string action in actions)
                {
                    if (ActionCatalog.Runnable.Contains(action)
                        && action is not (ActionIds.Quarantine or ActionIds.Recycle)
                        && !fixes.Contains(action))
                    {
                        fixes.Add(action);
                    }
                }
            }
        }

        FingerprintState fingerprint = file.Hash is null ? FingerprintState.None
            : open.Any(f => f.RuleId == "INT-ROT") ? FingerprintState.Changed
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
            TechnicalText(file, findings),
            file.Size,
            zeroRuns,
            recommended is null ? string.Empty : ActionName(recommended),
            recommended ?? string.Empty,
            fixes,
            file.RootId,
            file.DirId,
            file.RelDir ?? string.Empty,
            rootCrumbs.Length,
            copiesPending);
    }

    /// <summary>The start offsets of zero-filled runs a finding records (GEN-ZEROFILL as <c>runOffsetsBytes</c>, INT-ROT and damage as <c>zeroRunOffsetsBytes</c>).</summary>
    private static long[]? Offsets(IReadOnlyDictionary<string, object> evidence)
    {
        foreach (string key in new[] { "runOffsetsBytes", "zeroRunOffsetsBytes" })
        {
            if (evidence.TryGetValue(key, out object? value) && value is object[] values && values.Length > 0)
            {
                return [.. values.Select(v => Convert.ToInt64(v, CultureInfo.InvariantCulture))];
            }
        }

        return null;
    }

    /// <summary>
    /// The byte map's zero-filled regions as offset and length pairs: the longest run over its whole length, then a
    /// region at the start of every other recorded run (their lengths are not recorded). Null without any.
    /// </summary>
    internal static long[]? ZeroRuns(IReadOnlyDictionary<string, object> evidence)
    {
        List<long> runs = [];
        if (evidence.TryGetValue("longestRunOffsetBytes", out object? offset) && evidence.TryGetValue("longestRunBytes", out object? length))
        {
            long bytes = Convert.ToInt64(length, CultureInfo.InvariantCulture);
            if (bytes > 0)
            {
                runs.Add(Convert.ToInt64(offset, CultureInfo.InvariantCulture));
                runs.Add(bytes);
            }
        }

        foreach (long start in Offsets(evidence) ?? [])
        {
            runs.Add(start);
            runs.Add(1);
        }

        return runs.Count == 0 ? null : [.. runs];
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

    public byte[]? LoadThumbnail(FileDetail detail, CancellationToken token)
    {
        if (pictures is null || database.Files.Get(detail.FileId) is not FileRecord file)
        {
            return null;
        }

        try
        {
            string key = EvidenceRenderer.CacheKey(file.Id, file.Hash, file.Size, file.MtimeUtc);
            return pictures.ThumbnailAsync(detail.FullPath, key, file.MediaKind, (file.Media.DurationMs ?? 0) / 1000.0, token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public byte[]? LoadFrameStrip(FileDetail detail, FindingDetail finding, CancellationToken token)
    {
        if (pictures is null || database.Files.Get(detail.FileId) is not FileRecord file)
        {
            return null;
        }

        double[] times = FrameTimes(finding.Evidence);
        try
        {
            string key = EvidenceRenderer.CacheKey(file.Id, file.Hash, file.Size, file.MtimeUtc);
            return pictures.FrameStripAsync(detail.FullPath, key, file.MediaKind == MediaKind.Photo ? [0] : times, token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public byte[]? LoadCompare(FileDetail detail, FindingDetail finding, double time, CancellationToken token)
    {
        if (pictures is null || database.Files.Get(detail.FileId) is not FileRecord file)
        {
            return null;
        }

        IReadOnlyDictionary<string, object> e = finding.Evidence;
        int width = e.TryGetValue("containerWidth", out object? w) ? Convert.ToInt32(w, CultureInfo.InvariantCulture) : file.Media.Width ?? 0;
        int height = e.TryGetValue("containerHeight", out object? h) ? Convert.ToInt32(h, CultureInfo.InvariantCulture) : file.Media.Height ?? 0;
        int effective = e.TryGetValue("effectiveHeight", out object? eh) ? Convert.ToInt32(eh, CultureInfo.InvariantCulture) : 0;
        if (width <= 0 || height <= 0 || effective <= 0)
        {
            return null;
        }

        try
        {
            string key = EvidenceRenderer.CacheKey(file.Id, file.Hash, file.Size, file.MtimeUtc);
            return pictures.CompareAsync(detail.FullPath, key, file.MediaKind, time, width, height, effective, token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The sampled frame times of an upscale finding.</summary>
    internal static double[] FrameTimes(IReadOnlyDictionary<string, object> evidence) =>
        evidence.TryGetValue("frameTimes", out object? value) && value is object[] list ? [.. list.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture))] : [0];

    /// <summary>The positions marked on the waveform: clipped runs and dropouts, in time order.</summary>
    internal static double[] EvidenceMarks(FileDetail detail) =>
        [.. detail.Findings.Where(f => f.View == EvidenceView.Waveform).SelectMany(f => Positions(f.Evidence)).Distinct().Order().Take(MarkWindowCollector.MaxMarks)];

    internal static IEnumerable<double> Positions(IReadOnlyDictionary<string, object> evidence) =>
        evidence.TryGetValue("positionsSeconds", out object? value) && value is object[] list
            ? list.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture))
            : [];

    /// <summary>The rule's title, hedged by the finding's confidence (the Issues column, Browse's Problem).</summary>
    internal static string Title(string ruleId, float confidence) =>
        SeverityPolicy.Hedge(RuleCatalog.TryGet(ruleId, out RuleDefinition rule) ? rule.Title : ruleId, confidence);

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
        "VID-UPSCALED" when evidence.ContainsKey("frameTimes") => EvidenceView.FrameStrip,
        "IMG-UPSCALED" when evidence.ContainsKey("containerWidth") => EvidenceView.FrameStrip,
        "VID-STARVED" or "VID-INFLATED" when evidence.ContainsKey("equivalentBpp") => EvidenceView.Bitrate,
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

    /// <summary>
    /// Technical details (subplan 09, Detail drawer): the streams, the decoder messages with their counts, and the
    /// fingerprints; Copy details copies it as shown, for a bug report or a forum post.
    /// </summary>
    internal static string TechnicalText(FileRecord file, IReadOnlyList<FindingDetail> findings)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        MediaSummary m = file.Media;
        StringBuilder text = new();
        if (m.Container is { Length: > 0 } container)
        {
            text.Append(Strings.Drawer.TechContainer).AppendLine(container);
        }

        if (m.VideoCodec is { Length: > 0 } video)
        {
            text.Append(Strings.Drawer.TechVideo).AppendLine(UiFormat.Facts(
                video,
                m.Width is int w && m.Height is int h ? string.Format(culture, Strings.Drawer.Dimensions, w, h) : null,
                m.FpsMilli is int fps ? string.Format(culture, Strings.Drawer.Fps, (fps / 1000.0).ToString("0.##", culture)) : null));
        }

        if (m.AudioCodec is { Length: > 0 } audio)
        {
            text.Append(Strings.Drawer.TechAudio).AppendLine(UiFormat.Facts(
                audio,
                m.SampleRate is int rate ? string.Format(culture, Strings.Drawer.Kilohertz, (rate / 1000.0).ToString("0.#", culture)) : null,
                m.BitDepth is int bits ? string.Format(culture, Strings.Drawer.Bits, bits) : null,
                m.Channels switch { 1 => Strings.Drawer.Mono, 2 => Strings.Drawer.Stereo, 6 => Strings.Drawer.Surround, int n => n.ToString(culture), _ => null }));
        }

        // The decoder's messages with how often each was logged; findings of one decode share them, so each is listed once.
        HashSet<string> listed = new(StringComparer.Ordinal);
        foreach (FindingDetail finding in findings)
        {
            object[] messages = finding.Evidence.TryGetValue("messages", out object? m1) && m1 is object[] a ? a : [];
            object[] counts = finding.Evidence.TryGetValue("messageCounts", out object? c1) && c1 is object[] b ? b : [];
            for (int i = 0; i < messages.Length; i++)
            {
                string message = Convert.ToString(messages[i], CultureInfo.InvariantCulture) ?? string.Empty;
                long count = i < counts.Length ? Convert.ToInt64(counts[i], CultureInfo.InvariantCulture) : 1;
                if (message.Length > 0 && listed.Add(message))
                {
                    text.AppendLine(string.Format(culture, Strings.Drawer.TechMessage, UiFormat.Count(count), message));
                }
            }
        }

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
