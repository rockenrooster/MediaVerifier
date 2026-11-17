using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Analysis;

/// <summary>Interlacing, frozen and black pictures, silent soundtracks (subplan 11, tasks V-06 and V-07).</summary>
public sealed class VideoSignalParserTests
{
    [Fact]
    public void Idet_summary_and_graph()
    {
        Assert.Equal((12, 0, 588, 0), InterlaceProvider.Parse("[Parsed_idet_4 @ 000001] Multi frame detection: TFF:   12 BFF:    0 Progressive:  588 Undetermined:    0"));
        Assert.Null(InterlaceProvider.Parse("[Parsed_idet_4 @ 000001] Single frame detection: TFF: 1 BFF: 0 Progressive: 2 Undetermined: 3"));
        // Flat (black) frames give inf and nan from blurdetect and the SSIM dB value: kept finite.
        List<(double SpatialInfo, double Block, double Blur)> flat = FrameSampling.ParseFrameLog("frame:0 pts:0\nlavfi.siti.si=0.00\nlavfi.block=nan\nlavfi.blur=inf\n");
        Assert.Equal((0.0, 0.0, 0.0), flat[0]);
        Assert.Equal([1.0], FrameSampling.ParseSsim("n:1 Y:inf U:1.000000 V:1.000000 All:1.000000 (inf)\n"));
        Assert.Equal("[0:v]trim=end_frame=200,setpts=PTS-STARTPTS[s0];[1:v]trim=end_frame=200,setpts=PTS-STARTPTS[s1];[s0][s1]concat=n=2:v=1:a=0,idet[v]", InterlaceProvider.Graph(2));
    }
}

[Trait("Category", "Integration")]
public sealed class VideoSignalFixtureTests(ITestOutputHelper output)
{
    [FixtureFact]
    public async Task Interlacing_and_a_silent_soundtrack_are_found_at_standard_depth()
    {
        Dictionary<string, string[]> found = await ScanAsync(ScanDepth.Standard, ["interlaced.mp4", "silent.mp4", "native1080.mp4", "av.mp4"]);
        Assert.Contains("VID-INTERLACE", found["interlaced.mp4"]);
        Assert.Contains("VID-AUDIO-SILENT", found["silent.mp4"]);
        Assert.DoesNotContain(found["native1080.mp4"].Concat(found["av.mp4"]), r => r is "VID-INTERLACE" or "VID-AUDIO-SILENT");
        Assert.DoesNotContain("VID-AUDIO-SILENT", found["interlaced.mp4"]);
    }

    [FixtureFact]
    public async Task A_frozen_and_a_black_section_are_found_at_deep_depth()
    {
        Dictionary<string, string[]> found = await ScanAsync(ScanDepth.Deep, ["frozen.mp4", "black.mp4", "native1080.mp4"], enable: "VID-BLACK");
        Assert.Contains("VID-FROZEN", found["frozen.mp4"]);
        Assert.DoesNotContain("VID-BLACK", found["frozen.mp4"]);
        Assert.Contains("VID-BLACK", found["black.mp4"]);
        Assert.DoesNotContain("VID-FROZEN", found["black.mp4"]);
        Assert.DoesNotContain(found["native1080.mp4"], r => r is "VID-FROZEN" or "VID-BLACK");
    }

    private async Task<Dictionary<string, string[]>> ScanAsync(ScanDepth depth, string[] names, string? enable = null)
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        foreach (string name in names)
        {
            FixtureFactory.CopyTo(name, harness.Library);
        }

        if (enable is not null)
        {
            harness.Rules.SetEnabled(enable, true);
        }

        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(depth);
        Dictionary<string, string[]> found = [];
        foreach (string name in names)
        {
            FileRecord file = harness.AllFiles().Single(f => f.Name == name);
            List<FindingRecord> findings = harness.Database.Findings.ForFile(file.Id);
            output.WriteLine($"{name}: {string.Join("; ", findings.Select(f => $"{f.RuleId} {f.Severity} \"{f.Summary}\""))}");
            found[name] = [.. findings.Select(f => f.RuleId)];
        }

        return found;
    }
}
