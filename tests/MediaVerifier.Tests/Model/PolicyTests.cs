using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;

namespace MediaVerifier.Tests.Model;

public sealed class PolicyTests
{
    [Theory]
    [InlineData(1.0f, ConfidenceWording.Fact, null)]
    [InlineData(0.99f, ConfidenceWording.Likely, "Likely")]
    [InlineData(0.8f, ConfidenceWording.Likely, "Likely")]
    [InlineData(0.79f, ConfidenceWording.Possibly, "Possibly")]
    [InlineData(0.5f, ConfidenceWording.Possibly, "Possibly")]
    [InlineData(0.49f, ConfidenceWording.Suspicious, "Suspicious")]
    [InlineData(0f, ConfidenceWording.Suspicious, "Suspicious")]
    public void ConfidenceMapsToWording(float confidence, ConfidenceWording wording, string? pill)
    {
        Assert.Equal(wording, SeverityPolicy.Wording(confidence));
        Assert.Equal(pill, SeverityPolicy.PillText(confidence));
    }

    [Theory]
    [InlineData(1.0f, "Decoder reported 14 errors", "Decoder reported 14 errors")]
    [InlineData(0.9f, "Made from an MP3", "Likely made from an MP3")]
    [InlineData(0.6f, "Upscaled from 720p", "Possibly upscaled from 720p")]
    [InlineData(0.6f, "MP3 source", "Possibly MP3 source")]
    [InlineData(0.3f, "Clicks", "Suspicious: Clicks")]
    public void HedgingFollowsTheWordingTable(float confidence, string text, string expected)
    {
        Assert.Equal(expected, SeverityPolicy.Hedge(text, confidence));
    }

    [Fact]
    public void HeuristicsBelowHalfConfidenceAreNeverAboveLow()
    {
        Assert.Equal(Severity.Low, SeverityPolicy.CapForConfidence(Severity.High, 0.49f));
        Assert.Equal(Severity.High, SeverityPolicy.CapForConfidence(Severity.High, 0.5f));
        Assert.Equal(Severity.Low, SeverityPolicy.FromConfidence(RuleCatalog.Get("AUD-FAKE-LOSSLESS"), 0.3f));
        Assert.Equal(Severity.High, SeverityPolicy.FromConfidence(RuleCatalog.Get("AUD-FAKE-LOSSLESS"), 0.9f));
        Assert.Equal(Severity.Medium, SeverityPolicy.FromConfidence(RuleCatalog.Get("AUD-FAKE-LOSSLESS"), 0.6f));
    }

    [Fact]
    public void FileStatusIsTheWorstOpenSeverity()
    {
        Assert.Equal(Severity.None, SeverityPolicy.Aggregate([]));
        Assert.Equal(Severity.High, SeverityPolicy.Aggregate([Severity.Low, Severity.High, Severity.Medium]));
        Assert.True(Severity.Critical > Severity.High && Severity.High > Severity.Medium && Severity.Medium > Severity.Low && Severity.Low > Severity.None);
    }

    [Fact]
    public void DisabledRuleYieldsNoFinding()
    {
        RuleSettings settings = new();
        Finding raw = new("AUD-CLIPPING", Severity.Medium, 1f, "Clipping", Finding.NoEvidence);
        Assert.NotNull(settings.Apply(raw));

        settings.SetEnabled("AUD-CLIPPING", false);

        Assert.Null(settings.Apply(raw));
        Assert.Null(new RuleSettings().Apply(new Finding("VID-BLACK", Severity.Low, 1f, "Black", Finding.NoEvidence)));
    }

    [Fact]
    public void CapLowersSeverityButNeverBelowTheRuleMinimum()
    {
        RuleSettings settings = new();
        settings.SetSeverityCap("COR-DECODE", Severity.Medium);

        Finding? capped = settings.Apply(new Finding("COR-DECODE", Severity.Critical, 1f, "Errors", Finding.NoEvidence));
        Assert.Equal(Severity.Medium, capped!.Severity);

        settings.SetSeverityCap("COR-TRUNCATED", Severity.Low);
        Assert.Equal(Severity.High, settings.Cap(RuleCatalog.Get("COR-TRUNCATED")));
    }

    [Fact]
    public void OverridesSurviveAJsonRoundTripAndUnknownEntriesAreDropped()
    {
        RuleSettings settings = new();
        settings.SetEnabled("AUD-DUAL-MONO", true);
        settings.SetSeverityCap("AUD-CLIPPING", Severity.Medium);
        settings.SetParameter("GEN-ZEROFILL", "criticalPercent", 60);
        string stored = settings.ToJson();
        string json = "{\"NOPE-RULE\":{\"enabled\":false}," + stored[1..];

        RuleSettings restored = RuleSettings.FromJson(json);

        Assert.True(restored.IsEnabled(RuleCatalog.Get("AUD-DUAL-MONO")));
        Assert.Equal(Severity.Medium, restored.Cap(RuleCatalog.Get("AUD-CLIPPING")));
        Assert.Equal(60, restored.Value("GEN-ZEROFILL", "criticalPercent"));
        Assert.False(restored.Overrides.ContainsKey("NOPE-RULE"));
    }

    [Fact]
    public void OutOfRangeOverrideIsClamped()
    {
        RuleParameter parameter = RuleDefaults.Get("GEN-ZEROFILL", "criticalPercent");
        Assert.Equal(100, RuleDefaults.Resolve(parameter, 400));
        Assert.Equal(1, RuleDefaults.Resolve(parameter, -5));
        Assert.Equal(50, RuleDefaults.Resolve(parameter, null));

        RuleSettings settings = RuleSettings.FromJson("""{"GEN-ZEROFILL":{"parameters":{"criticalPercent":1000}}}""");
        Assert.Equal(100, settings.Value("GEN-ZEROFILL", "criticalPercent"));
    }

    [Fact]
    public void SettingADefaultValueRemovesTheOverride()
    {
        RuleSettings settings = new();
        settings.SetParameter("GEN-ZEROFILL", "criticalPercent", 70);
        Assert.True(settings.IsOverridden("GEN-ZEROFILL", "criticalPercent"));
        settings.SetParameter("GEN-ZEROFILL", "criticalPercent", 50);
        Assert.False(settings.IsOverridden("GEN-ZEROFILL", "criticalPercent"));
        Assert.Empty(settings.Overrides);
    }

    [Fact]
    public void EvidenceRoundTripsUnderANonEnglishCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Dictionary<string, object> evidence = new()
            {
                ["zeta"] = 1.5,
                ["cutoffHz"] = 16_600.25,
                ["count"] = 14,
                ["offsetBytes"] = 9_876_543_210L,
                ["codec"] = "flac",
                ["clean"] = false,
                ["peaksDb"] = new[] { -0.1, -3.25 },
                ["streams"] = new[] { "audio", "video" },
                ["ratio"] = 0.1f,
            };

            string json = EvidenceJson.Serialize(evidence);

            Assert.Equal("""{"clean":false,"codec":"flac","count":14,"cutoffHz":16600.25,"offsetBytes":9876543210,"peaksDb":[-0.1,-3.25],"ratio":0.1,"streams":["audio","video"],"zeta":1.5}""", json);
            Dictionary<string, object> back = EvidenceJson.Deserialize(json);
            Assert.Equal(16_600.25, back["cutoffHz"]);
            Assert.Equal(14L, back["count"]);
            Assert.Equal(9_876_543_210L, back["offsetBytes"]);
            Assert.Equal("flac", back["codec"]);
            Assert.Equal(false, back["clean"]);
            Assert.Equal(new object[] { -0.1, -3.25 }, (object[])back["peaksDb"]);
            Assert.True(EvidenceJson.TryGetDouble(back, "ratio", out double ratio) && Math.Abs(ratio - 0.1) < 1e-9);
            Assert.Equal(json, EvidenceJson.Serialize(back));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EvidenceRejectsNonFiniteNumbersAndNestedArrays()
    {
        Assert.Throws<ArgumentException>(() => EvidenceJson.Serialize(new Dictionary<string, object> { ["x"] = double.NaN }));
        Assert.Throws<ArgumentException>(() => EvidenceJson.Serialize(new Dictionary<string, object> { ["x"] = new[] { new[] { 1 } } }));
        Assert.Empty(EvidenceJson.Deserialize("not json"));
        Assert.Equal("{}", EvidenceJson.Serialize(null));
    }
}
