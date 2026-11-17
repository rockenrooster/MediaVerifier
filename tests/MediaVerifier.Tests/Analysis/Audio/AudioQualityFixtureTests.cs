using System.Globalization;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Analysis.Audio;

/// <summary>
/// The verified prototype results of subplan 10 as regression targets (tasks A-02 to A-07): the generated
/// fixtures scanned at Standard depth through the real engine.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AudioQualityFixtureTests(ITestOutputHelper output)
{
    /// <summary>Fixture, expected knee in Hz (0: no hard cutoff), effective bits (-1: float), rules expected.</summary>
    private static readonly (string Name, double Knee, int Bits, string Rules)[] Table =
    [
        ("src16.flac", 0, 16, ""),
        ("pad24.flac", 0, 16, "AUD-PADDED-BITS"),
        ("true96.flac", 0, 24, ""),
        ("up96.flac", 23_200, 24, "AUD-UPSAMPLED"),
        ("fake128.flac", 16_550, 16, "AUD-FAKE-LOSSLESS"),
        ("fake320.flac", 20_000, 16, "AUD-FAKE-LOSSLESS"),
        ("fakeAac.flac", 17_200, 16, "AUD-FAKE-LOSSLESS"),
        ("fakeOpus.flac", 20_000, 16, "AUD-FAKE-LOSSLESS"),
        ("fakeV0.flac", 0, 16, ""),
        ("infl320.mp3", 16_550, -1, "AUD-BITRATE-INFLATED"),
        ("m320.mp3", 20_000, -1, ""),
        ("natural.flac", 0, 16, ""),
        ("dull.flac", 0, 16, ""),
        ("clip.flac", 0, 16, "AUD-CLIPPING"),
        ("clipatt.flac", 0, 16, "AUD-CLIPPING"),
        ("clip320.mp3", -1, -1, "AUD-OVERS"),
    ];

    [FixtureFact]
    public async Task Fixtures_match_the_verified_prototype_results()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        foreach ((string name, _, _, _) in Table)
        {
            FixtureFactory.CopyTo(name, harness.Library);
        }

        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Standard);
        AudioDecodeProvider provider = new();
        List<string> failures = [];
        foreach ((string name, double knee, int bits, string rules) in Table)
        {
            FileRecord file = harness.AllFiles().Single(f => f.Name == name);
            Dictionary<ArtifactKind, (int ProviderVersion, string Data)> stored = harness.Database.Artifacts.ForFile(file.Id);
            if (!stored.TryGetValue(ArtifactKind.AudioDecode, out (int ProviderVersion, string Data) audio))
            {
                failures.Add($"{name}: no audio decode artifact; stored {string.Join(", ", stored.Keys)}; state {file.State}; findings {string.Join(", ", harness.Database.Findings.ForFile(file.Id).Select(f => f.RuleId + " " + f.Summary))}");
                continue;
            }

            AudioDecodeArtifact decode = (AudioDecodeArtifact)provider.Deserialize(audio.Data)!;
            PcmStats stats = decode.Stats!;
            SpectrumStats s = stats.Spectrum;
            List<FindingRecord> findings = [.. harness.Database.Findings.ForFile(file.Id).Where(f => f.Status == FindingStatus.Open)];
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{name,-14} knee {s.KneeHz,7:0} drop {s.DropDb,5:0.0} above {s.AboveDb,7:0.0} floor {stats.AboveVsFloorDb,6:0.0} bits {stats.EffectiveBits,3} runs {stats.Clipping.Runs,4} clip% {100.0 * stats.Clipping.ClippedSamples / (stats.Seconds * stats.SampleRate),6:0.000} longest {stats.Clipping.LongestRun,3} overs {stats.Levels.Sum(l => l.Overs),5} | {string.Join(", ", findings.Select(f => $"{f.RuleId} {f.Severity} {f.Confidence:0.00} \"{f.Summary}\""))}"));
            if (knee > 0 && (!s.Brickwall || Math.Abs(s.KneeHz - knee) > 300))
            {
                failures.Add($"{name}: knee {s.KneeHz:0} drop {s.DropDb:0.0}, expected about {knee:0}");
            }

            if (knee == 0 && s.Brickwall)
            {
                failures.Add($"{name}: unexpected cutoff at {s.KneeHz:0}");
            }

            if (bits != stats.EffectiveBits)
            {
                failures.Add($"{name}: {stats.EffectiveBits} effective bits, expected {bits}");
            }

            HashSet<string> expected = [.. rules.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
            HashSet<string> found = [.. findings.Select(f => f.RuleId)];
            if (!expected.SetEquals(found))
            {
                failures.Add($"{name}: findings {string.Join(", ", found)}, expected {string.Join(", ", expected)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [FixtureFact]
    public async Task Changing_a_clipping_threshold_re_judges_without_decoding()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        int processes = 0;
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        FixtureFactory.CopyTo("clip.flac", harness.Library);
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Standard);
        FileRecord file = harness.AllFiles().Single();
        Assert.Equal(Severity.High, harness.Database.Findings.ForFile(file.Id).Single(f => f.RuleId == "AUD-CLIPPING").Severity);

        // Raise every High limit out of reach: the same stored features now give Medium.
        runner.ObserveLine = _ => Interlocked.Increment(ref processes);
        harness.Rules.SetParameter("AUD-CLIPPING", "highPerMinute", 100000);
        harness.Rules.SetParameter("AUD-CLIPPING", "highPercent", 100);
        harness.Rules.SetParameter("AUD-CLIPPING", "highRunSamples", 100000);
        ReevaluationResult result = await harness.Engine.ReevaluateAsync(["AUD-CLIPPING"]);
        Assert.Equal(1, result.ChangedTier);
        Assert.Equal(Severity.Medium, harness.Database.Findings.ForFile(file.Id).Single(f => f.RuleId == "AUD-CLIPPING").Severity);
        Assert.Equal(0, processes);
    }
}
