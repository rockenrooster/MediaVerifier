using System.Globalization;
using System.Text.RegularExpressions;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Model;

public sealed class RuleCatalogTests
{
    private static readonly string[] ExpectedIds =
    [
        "GEN-UNREADABLE", "GEN-EMPTY", "GEN-ZEROFILL", "GEN-UNRECOGNIZED", "GEN-EXT-MISMATCH", "GEN-PATH",
        "INT-ROT", "INT-META-DRIFT", "INT-SIDECAR",
        "COR-CONTAINER", "COR-TRUNCATED", "COR-DECODE", "COR-CONCEALED", "COR-CHECKSUM", "COR-HANG",
        "COR-PARTIAL-IMAGE", "COR-TIMESTAMPS", "COR-INDEX", "COR-STREAM-LENGTH", "COR-TRAILING", "COR-DROPOUT",
        "COR-CLICKS", "COR-VISUAL",
        "AUD-FAKE-LOSSLESS", "AUD-UPSAMPLED", "AUD-PADDED-BITS", "AUD-BITRATE-INFLATED", "AUD-LOW-BITRATE",
        "AUD-CLIPPING", "AUD-OVERS", "AUD-DC", "AUD-DEAD-CHANNEL", "AUD-PHASE", "AUD-SILENT", "AUD-LONG-SILENCE",
        "AUD-DUAL-MONO", "AUD-DYNAMICS",
        "VID-UPSCALED", "VID-STARVED", "VID-BLOCKY", "VID-INFLATED", "VID-INTERLACE", "VID-FROZEN", "VID-BLACK",
        "VID-AUDIO-SILENT", "VID-HDR",
        "IMG-UPSCALED", "IMG-HEAVY-COMPRESSION", "IMG-LOW-RES",
    ];

    [Fact]
    public void ContainsExactlyTheRulesOfSubplan02()
    {
        Assert.Equal(ExpectedIds, RuleCatalog.All.Select(r => r.Id));
        Assert.Equal(["RUN-MASS-MISSING", "RUN-ROOT-OFFLINE", "RUN-READ-ERRORS"], RuleCatalog.RunAlerts.Select(a => a.Id));
    }

    [Fact]
    public void IdsAreUniqueAndWellFormed()
    {
        Regex pattern = new("^[A-Z]{3}-[A-Z0-9-]+$");
        Assert.Equal(RuleCatalog.All.Count, RuleCatalog.All.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(RuleCatalog.All, r => Assert.Matches(pattern, r.Id));
        Assert.All(RuleCatalog.RunAlerts, a => Assert.Matches(pattern, a.Id));
    }

    [Fact]
    public void EveryRuleHasATitleAndThreeExplanationParts()
    {
        foreach (RuleDefinition rule in RuleCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Title), rule.Id);
            Assert.False(string.IsNullOrWhiteSpace(rule.WhatItMeans), rule.Id);
            Assert.False(string.IsNullOrWhiteSpace(rule.WhyItMatters), rule.Id);
            Assert.False(string.IsNullOrWhiteSpace(rule.WhatYouCanDo), rule.Id);
            foreach (string part in new[] { rule.WhatItMeans, rule.WhyItMatters, rule.WhatYouCanDo })
            {
                Assert.EndsWith(".", part, StringComparison.Ordinal);
                Assert.All(RuleText.Placeholders(part), key => Assert.True(RuleText.HasDefault(key), $"{rule.Id}: no default for {{{key}}}"));
            }
        }
    }

    [Fact]
    public void QualityAndAuthenticityRulesAreNeverCritical()
    {
        foreach (RuleDefinition rule in RuleCatalog.All.Where(r => r.Category is FindingCategory.Quality or FindingCategory.Authenticity))
        {
            Assert.True(rule.MaxSeverity < Severity.Critical, rule.Id);
        }
    }

    [Fact]
    public void SeverityRangesAreOrderedAndActionsAreKnown()
    {
        foreach (RuleDefinition rule in RuleCatalog.All)
        {
            Assert.True(rule.MinSeverity is >= Severity.Low and <= Severity.Critical, rule.Id);
            Assert.True(rule.MinSeverity <= rule.MaxSeverity, rule.Id);
            Assert.NotEmpty(rule.ActionIds);
            Assert.All(rule.ActionIds, a => Assert.Contains(a, ActionIds.All));
            Assert.All(ActionIds.Common, a => Assert.Contains(a, rule.ActionIds));
        }
    }

    [Fact]
    public void DisabledByDefaultMatchesSubplan02()
    {
        string[] off = [.. RuleCatalog.All.Where(r => !r.EnabledByDefault).Select(r => r.Id)];
        Assert.Equal(["AUD-DUAL-MONO", "AUD-DYNAMICS", "VID-BLACK", "IMG-LOW-RES"], off);
    }

    [Fact]
    public void FakeLosslessTextMatchesTheReferenceMockup()
    {
        RuleDefinition rule = RuleCatalog.Get("AUD-FAKE-LOSSLESS");
        Dictionary<string, object> evidence = new() { ["sourceName"] = "MP3", ["sourcePhrase"] = "an MP3", ["formatName"] = "FLAC" };

        Assert.Equal(
            "The audio has the hard frequency cutoff that MP3 encoders leave, but the file is stored as FLAC. It was probably converted from an MP3.",
            RuleText.Render(rule.WhatItMeans, evidence));
        Assert.Equal(
            "Replace it from a genuine lossless source. Nothing can restore what the MP3 encoder removed.",
            RuleText.Render(rule.WhatYouCanDo, evidence));
        Assert.DoesNotContain("{", RuleText.Render(rule.WhatItMeans, null), StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedRulesDocumentIsCurrent()
    {
        string path = RepositoryPaths.Combine("docs", "rules.md");
        string generated = RuleCatalogMarkdown.Generate();
        if (RepositoryPaths.UpdateDocs || !File.Exists(path))
        {
            File.WriteAllText(path, generated);
        }

        string committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(committed == generated, "docs/rules.md is stale: run the tests with MEDIAVERIFIER_UPDATE_DOCS=1.");
    }

    [Fact]
    public void ParameterKeysAreUniqueAndDefaultsInRange()
    {
        Assert.Equal(RuleDefaults.All.Count, RuleDefaults.All.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(RuleDefaults.All, p => Assert.InRange(p.Default, p.Min, p.Max));
        Assert.All(RuleDefaults.All, p => Assert.True(RuleCatalog.TryGet(p.RuleId, out _) || p.RuleId.StartsWith("RUN-", StringComparison.Ordinal), p.Key));
        _ = CultureInfo.InvariantCulture;
    }
}
