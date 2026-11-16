using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Rules;

/// <summary>
/// One catalog entry (subplan 02): what kind of finding it is, which files and depth it applies to, how
/// severe it can be, and the three plain-language parts shown in the detail view. Explanation text may
/// contain <c>{key}</c> placeholders filled from the finding's evidence (see <see cref="RuleText"/>).
/// </summary>
public sealed record RuleDefinition(
    string Id,
    string Title,
    FindingCategory Category,
    MediaKindMask AppliesTo,
    ScanDepth MinDepth,
    Severity MinSeverity,
    Severity MaxSeverity,
    int Version,
    bool EnabledByDefault,
    Milestone Milestone,
    bool IsHeuristic,
    string WhatItMeans,
    string WhyItMatters,
    string WhatYouCanDo,
    IReadOnlyList<string> ActionIds)
{
    /// <summary>Rules evaluated when a fingerprint is verified, at any depth (INT-ROT, INT-META-DRIFT).</summary>
    public bool OnVerification { get; init; }

    /// <summary>Settings → Rules kind label: Damage covers integrity and corruption.</summary>
    public string KindLabel => Category switch
    {
        FindingCategory.Integrity or FindingCategory.Corruption => "Damage",
        FindingCategory.Authenticity => "Authenticity",
        FindingCategory.Quality => "Quality",
        _ => "Structure",
    };

    public bool CanReach(Severity severity) => severity >= MinSeverity && severity <= MaxSeverity;
}
