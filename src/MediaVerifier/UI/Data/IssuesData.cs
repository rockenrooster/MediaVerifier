using System.Globalization;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>What the Issues list shows: filter, order and grouping.</summary>
internal sealed record IssuesQuery(FindingFilter Filter, FindingSort Sort = FindingSort.Severity, bool Descending = false, FindingGroup Group = FindingGroup.Severity)
{
    /// <summary>
    /// True when anything but the defaults is set (the summary row and the empty state use it). "Problems found in this
    /// scan" counts: without it the list could say "No problems found" while thousands were open, with no way to clear it.
    /// </summary>
    public bool Filtered =>
        Filter.Severities is { Count: > 0 } || Filter.Kinds is { Count: > 0 } || Filter.Categories is { Count: > 0 } || Filter.RuleIds is { Count: > 0 }
        || Filter.RootId is not null || Filter.DirId is not null || !string.IsNullOrEmpty(Filter.Text) || Filter.Status != FindingStatus.Open
        || Filter.FirstRun is not null;
}

/// <summary>A group of the list: header label, the count shown, and how many ids of the list belong to it.</summary>
internal sealed record IssueGroup(string Key, string Label, Severity? Tier, int Count, int Rows)
{
    /// <summary>The header's count, formatted once with the data (the list paints without allocating).</summary>
    public string CountText { get; } = UiFormat.Count(Count);
}

/// <summary>The Issues snapshot: ids in list order, groups, chip and drop-down counts.</summary>
internal sealed record IssuesData(
    IssuesQuery Query,
    long[] Ids,
    IReadOnlyList<IssueGroup> Groups,
    SeverityCounts TierCounts,
    IReadOnlyDictionary<string, int> RuleCounts,
    IReadOnlyList<(long RootId, string Name)> Roots,
    long TotalOpen,
    bool HasFolders,
    bool AnyScanned,
    long FilesScanned,
    DateTimeOffset? LastScan)
{
    /// <summary>The filter summary row's text, <c>Showing {n} of {total}</c>, formatted once with the data.</summary>
    public string ShowingText { get; } = string.Format(CultureInfo.CurrentCulture, Strings.Issues.Showing, UiFormat.Count(Ids.Length), UiFormat.Count(TotalOpen));
}

/// <summary>One row of the Issues list.</summary>
internal sealed record IssueRow(
    long Id,
    long FileId,
    string RuleId,
    Severity Severity,
    string Name,
    string Line2,
    string Folder,
    long Size,
    MediaKind Kind,
    string FullPath,
    string? Problem = null)
{
    /// <summary>Line 2 with the problem in front, for a list too narrow for the Problem column (built once, not per paint).</summary>
    public string Line2WithProblem { get; } = Problem is null ? Line2 : Problem + Strings.Separator + Line2;

    /// <summary>The size as the row shows it, formatted once (the list paints without allocating).</summary>
    public string SizeText { get; } = UiFormat.Size(Size);

    /// <summary>
    /// The fixes the list can offer for this finding, most recommended first: runnable, and valid for the file's kind,
    /// format and folder (subplan 12, Action catalogue). Quarantine has its own button and is not among them.
    /// </summary>
    public IReadOnlyList<string> Fixes { get; init; } = [];

    /// <summary>The recommended fix (the row menu's and Ctrl+Enter's), null when the app has none to run.</summary>
    public string? RecommendedFix => Core.Remediation.ActionCatalog.Recommended(Fixes);
}

/// <summary>Which evidence view a finding gets in the drawer (subplan 09, task UI-14).</summary>
internal enum EvidenceView
{
    None,
    Messages,
    ByteMap,
    Spectrogram,
    FrameStrip,
    Waveform,
    Bitrate,
}

/// <summary>A finding in the drawer, with the rule's three paragraphs.</summary>
/// <param name="Status">Open, or the status the list shows (an ignored finding offers Stop ignoring).</param>
/// <param name="ZeroRuns">For the byte map: zero-filled regions as offset and length pairs, in bytes.</param>
internal sealed record FindingDetail(
    long Id,
    string RuleId,
    Severity Severity,
    float Confidence,
    string Title,
    string Summary,
    string WhatItMeans,
    string WhyItMatters,
    string WhatYouCanDo,
    EvidenceView View,
    IReadOnlyDictionary<string, object> Evidence,
    string EvidenceCaption,
    FindingStatus Status = FindingStatus.Open,
    IReadOnlyList<long>? ZeroRuns = null);

/// <summary>The fingerprint line of the drawer.</summary>
internal enum FingerprintState
{
    None,
    NeverVerified,
    Unchanged,
    Changed,
}

/// <summary>Everything the detail drawer shows about one file.</summary>
/// <param name="RootSegments">How many breadcrumb segments the watched folder's own path has (the rest are its subfolders).</param>
/// <param name="CopiesPending">The fixes were worked out without looking for a healthy copy; a second load looks for one.</param>
internal sealed record FileDetail(
    long FileId,
    string Name,
    string FullPath,
    IReadOnlyList<string> Breadcrumb,
    MediaKind Kind,
    string FormatLine,
    FingerprintState Fingerprint,
    DateTimeOffset? BaselineUtc,
    int VerifyCount,
    DateTimeOffset? LastVerifiedUtc,
    IReadOnlyList<FindingDetail> Findings,
    IReadOnlyList<(string Text, DateTimeOffset When)> History,
    string TechnicalText,
    long SizeBytes,
    IReadOnlyList<long> ZeroRunOffsets,
    string RecommendedAction = "",
    string RecommendedActionId = "",
    IReadOnlyList<string>? FixActionIds = null,
    long RootId = 0,
    long DirId = 0,
    string RelDir = "",
    int RootSegments = 0,
    bool CopiesPending = false)
{
    /// <summary><see cref="ZeroRunOffsets"/> as one-byte regions (offset and length pairs) for the byte map, built once.</summary>
    public IReadOnlyList<long> ZeroRunStarts { get; } = [.. ZeroRunOffsets.SelectMany(offset => new[] { offset, 1L })];
}

internal partial interface IUiData
{
    IssuesData LoadIssues(IssuesQuery query);

    /// <summary>The tier counts <see cref="LoadIssues"/> gives for <paramref name="filter"/>, without the list (the new-problems pill).</summary>
    SeverityCounts CountIssues(FindingFilter filter);

    /// <summary>
    /// The rows of the given findings, in the given order; findings that are gone are left out. Without
    /// <paramref name="withFixes"/> the rows carry no fixes and the file facts behind them are not read (Scan again, Copy
    /// path, Quarantine and a fix already chosen need only the files).
    /// </summary>
    IReadOnlyList<IssueRow> LoadIssueRows(IReadOnlyList<long> ids, bool withFixes = true);

    /// <summary>Everything the drawer shows about a file and its open findings, healthy copies looked for.</summary>
    FileDetail? LoadFileDetail(long fileId);

    /// <summary>
    /// The drawer's data with the file's open findings and those with <paramref name="status"/> (the list's Status
    /// view). Without <paramref name="findCopies"/> no healthy copy is looked for, which can wait on an offline backup
    /// drive: the drawer opens at once and <see cref="FileDetail.CopiesPending"/> says a second load is worthwhile.
    /// </summary>
    FileDetail? LoadFileDetail(long fileId, FindingStatus status, bool findCopies);

    /// <summary>The file a finding belongs to; null when there is no such finding.</summary>
    long? FileOfFinding(long findingId);

    /// <summary>
    /// The spectrogram and waveform behind a file's audio findings (subplan 10, Evidence views); null when
    /// they cannot be rendered. Decodes the file on a cache miss, so it never runs on the UI thread.
    /// </summary>
    AudioEvidence? LoadAudioEvidence(FileDetail detail, CancellationToken token);

    /// <summary>The drawer header's thumbnail as PNG (cover art, the photo, a video frame); null when there is none.</summary>
    byte[]? LoadThumbnail(FileDetail detail, CancellationToken token);

    /// <summary>The sampled frames of an upscale finding side by side, as PNG (subplan 11, Evidence views).</summary>
    byte[]? LoadFrameStrip(FileDetail detail, FindingDetail finding, CancellationToken token);

    /// <summary>One frame's original and rebuilt crops side by side, as PNG.</summary>
    byte[]? LoadCompare(FileDetail detail, FindingDetail finding, double time, CancellationToken token);
}
