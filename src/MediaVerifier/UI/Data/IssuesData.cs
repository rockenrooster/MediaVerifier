using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>What the Issues list shows: filter, order and grouping.</summary>
internal sealed record IssuesQuery(FindingFilter Filter, FindingSort Sort = FindingSort.Severity, bool Descending = false, FindingGroup Group = FindingGroup.Severity)
{
    /// <summary>True when anything but the defaults is set (the summary row and the empty state use it).</summary>
    public bool Filtered =>
        Filter.Severities is { Count: > 0 } || Filter.Kinds is { Count: > 0 } || Filter.Categories is { Count: > 0 } || Filter.RuleIds is { Count: > 0 }
        || Filter.RootId is not null || Filter.DirId is not null || !string.IsNullOrEmpty(Filter.Text) || Filter.Status != FindingStatus.Open;
}

/// <summary>A group of the list: header label, the count shown, and how many ids of the list belong to it.</summary>
internal sealed record IssueGroup(string Key, string Label, Severity? Tier, int Count, int Rows);

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
    DateTimeOffset? LastScan);

/// <summary>One row of the Issues list.</summary>
internal sealed record IssueRow(
    long Id,
    long FileId,
    string RuleId,
    Severity Severity,
    float Confidence,
    string Name,
    string Line2,
    string Folder,
    long Size,
    MediaKind Kind,
    string FullPath);

/// <summary>Which evidence view a finding gets in the drawer (subplan 09, task UI-14).</summary>
internal enum EvidenceView
{
    None,
    Messages,
    ByteMap,
    Spectrogram,
    FrameStrip,
    Waveform,
}

/// <summary>A finding in the drawer, with the rule's three paragraphs.</summary>
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
    string EvidenceCaption);

/// <summary>The fingerprint line of the drawer.</summary>
internal enum FingerprintState
{
    None,
    NeverVerified,
    Unchanged,
    Changed,
}

/// <summary>Everything the detail drawer shows about one file.</summary>
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
    string RecommendedAction = "");

/// <summary>Issues and drawer data (separate interface so pages depend only on what they read).</summary>
internal interface IIssuesData
{
    IssuesData LoadIssues(IssuesQuery query);

    IReadOnlyList<IssueRow> LoadIssueRows(IReadOnlyList<long> ids);

    FileDetail? LoadFileDetail(long fileId);

    /// <summary>
    /// The spectrogram and waveform behind a file's audio findings (subplan 10, Evidence views); null when
    /// they cannot be rendered. Decodes the file on a cache miss, so it never runs on the UI thread.
    /// </summary>
    AudioEvidence? LoadAudioEvidence(FileDetail detail, CancellationToken token);
}
