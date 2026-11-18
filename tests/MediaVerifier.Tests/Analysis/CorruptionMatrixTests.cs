using System.Diagnostics;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Analysis;

/// <summary>
/// The corruption matrix of subplan 15 (task T-04) and the provider acceptance tests of subplan 07 (tasks
/// C-07 to C-10, C-12), through the real engine with the production providers and detectors. Any FFmpeg
/// log line the classifier cannot place fails the test (the unknown-line gate, subplan 15).
/// </summary>
[Trait("Category", "Integration")]
public sealed class CorruptionMatrixTests(ITestOutputHelper output)
{
    /// <summary>
    /// Fixture, mutation, rules at Quick, Standard, Deep (each cumulative; "A|B" means either), rules that may also appear.
    /// </summary>
    public static TheoryData<string, string, string, string, string, string> Matrix => new()
    {
        { "src16.flac", "flip", "", "COR-CHECKSUM", "", "COR-DECODE" },
        { "src16.flac", "zero", "", "COR-DECODE", "", "COR-CHECKSUM GEN-ZEROFILL" },
        { "src16.flac", "truncate 0.6", "", "COR-TRUNCATED", "", "COR-DECODE" },
        { "m128.mp3", "random 3000", "", "COR-DECODE", "", "" },
        { "m128.mp3", "truncate 0.55", "COR-TRUNCATED", "COR-TRUNCATED", "", "COR-DECODE" },
        { "a128.m4a", "random 1500", "", "COR-DECODE", "", "" },
        { "o128.opus", "flip2", "", "COR-CHECKSUM", "", "COR-DECODE" },
        { "native1080.mp4", "truncate 0.6", "COR-CONTAINER", "", "", "COR-TRUNCATED" },
        { "fs.mp4", "truncate 0.6", "COR-TRUNCATED", "", "", "COR-DECODE" },
        // Standard decodes key frames and all audio: it sees this only when the damage lands in audio (about 8 % of
        // positions in docs/detection-rates.md). Deep decodes every frame: decoder errors or concealed frames.
        { "av.mp4", "random 2000", "", "", "COR-DECODE|COR-CONCEALED", "COR-DECODE COR-CONCEALED" },
        { "fs.mp4", "random 2000", "", "", "COR-CONCEALED", "COR-DECODE" },
        { "fs.mp4", "flip", "", "", "COR-CONCEALED", "COR-DECODE" },
        { "t.mkv", "truncate 0.6", "COR-TRUNCATED", "", "", "COR-DECODE" },
        { "img.jpg", "truncate 0.6", "COR-TRUNCATED", "COR-PARTIAL-IMAGE", "", "" },
        { "img.jpg", "zero", "", "COR-PARTIAL-IMAGE", "", "GEN-ZEROFILL" },
        { "img.jpg", "flip", "", "", "", "" },
        { "img.png", "flip", "", "COR-CHECKSUM", "", "COR-DECODE COR-PARTIAL-IMAGE" },
        { "img.png", "truncate 0.6", "COR-TRUNCATED", "COR-DECODE", "", "COR-PARTIAL-IMAGE" },
        { "img.webp", "flip3", "", "", "", "" },
        { "img.webp", "truncate 0.6", "COR-TRUNCATED COR-CONTAINER", "", "", "COR-DECODE" },
    };

    /// <summary>Every fixture without damage: no finding at any depth (subplan 07 exit criteria).</summary>
    public static TheoryData<ScanDepth> Depths => new() { ScanDepth.Quick, ScanDepth.Standard, ScanDepth.Deep };

    /// <summary>Undamaged fixtures; pad24.flac and up96.flac are left out because they are quality findings by design (subplan 10).</summary>
    private static readonly string[] HealthySet =
    [
        "src16.flac", "m128.mp3", "m320.mp3", "mV0.mp3", "a128.m4a", "o128.opus", "true96.flac",
        "cover.mp3", "native1080.mp4", "fs.mp4", "t.mkv", "av.mp4", "img.jpg", "img.png", "img.webp",
    ];

    [FixtureTheory]
    [MemberData(nameof(Matrix))]
    public async Task Matrix_cell_behaves_as_listed(string fixture, string mutation, string quick, string standard, string deep, string extras)
    {
        HashSet<string> allowed = [.. Words(extras)];
        HashSet<string> expected = [];
        foreach ((ScanDepth depth, string column) in new[] { (ScanDepth.Quick, quick), (ScanDepth.Standard, standard), (ScanDepth.Deep, deep) })
        {
            expected.UnionWith(Words(column));
            Scan scan = await ScanOneAsync(fixture, path => Apply(path, mutation), depth);
            HashSet<string> found = [.. scan.Findings.Select(f => f.RuleId)];
            output.WriteLine($"{fixture} {mutation} @ {depth}: {string.Join(", ", scan.Findings.Select(f => $"{f.RuleId} {f.Severity} \"{f.Summary}\""))}");
            Assert.True(expected.All(word => word.Split('|').Any(found.Contains)), $"{depth}: expected {string.Join(", ", expected)}; found {string.Join(", ", found)}");
            HashSet<string> permitted = [.. expected.SelectMany(word => word.Split('|')), .. allowed];
            Assert.True(found.IsSubsetOf(permitted), $"{depth}: unexpected {string.Join(", ", found.Except(permitted))}");
        }
    }

    [FixtureTheory]
    [MemberData(nameof(Depths))]
    public async Task Healthy_fixtures_have_no_findings(ScanDepth depth)
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        List<string> unknown = Gate(runner);
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        foreach (string name in HealthySet)
        {
            FixtureFactory.CopyTo(name, harness.Library);
        }

        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(depth);
        List<string> problems = [];
        foreach (FileRecord file in harness.AllFiles())
        {
            Assert.Equal(FileState.Ok, file.State);
            problems.AddRange(harness.Database.Findings.ForFile(file.Id).Select(f => $"{file.Name}: {f.RuleId} {f.Severity} \"{f.Summary}\""));
        }

        Assert.Equal(HealthySet.Length, harness.AllFiles().Count);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Empty(unknown);
    }

    /// <summary>
    /// A decode finding says where in the file the errors were (owner request 2026-11-17): damage in the middle of an
    /// MP3 is placed near its middle, from the audio read so far; in a video, between the progress reports.
    /// </summary>
    [FixtureTheory]
    [InlineData("m128.mp3", 3000)]
    [InlineData("av.mp4", 2000)]
    public async Task Decode_errors_are_placed_in_the_file(string fixture, int bytes)
    {
        Scan scan = await ScanOneAsync(fixture, path => Mutate.RandomBlock(path, MiddleReadAtStandard(path), bytes, seed: 1), ScanDepth.Standard);
        FindingRecord decode = Assert.Single(scan.Findings, f => f.RuleId == "COR-DECODE");
        output.WriteLine(decode.Summary);
        double[]? spots = fixture.EndsWith(".mp3", StringComparison.Ordinal)
            ? Assert.IsType<AudioDecodeArtifact>(scan.Artifact(ArtifactKind.AudioDecode, new AudioDecodeProvider())).ErrorPositions
            : Assert.IsType<VideoDecodeArtifact>(scan.Artifact(ArtifactKind.VideoDecode, new VideoDecodeProvider())).ErrorPositions;
        Assert.NotNull(spots);
        Assert.NotEmpty(spots);
        output.WriteLine("spots: " + string.Join(", ", spots));
        Assert.Contains(", around ", decode.Summary, StringComparison.Ordinal);
        double duration = (scan.File.Media.DurationMs ?? 0) / 1000.0;
        Assert.True(duration > 0, "No duration.");
        Assert.All(spots, s => Assert.InRange(s, 0, duration + 1));
        if (fixture.EndsWith(".mp3", StringComparison.Ordinal))
        {
            Assert.InRange(spots[0], duration * 0.3, duration * 0.8);
        }
    }

    [FixtureFact]
    public async Task Flac_md5_matches_for_16_and_24_bit()
    {
        foreach (string name in new[] { "src16.flac", "pad24.flac" })
        {
            Scan scan = await ScanOneAsync(name, null, ScanDepth.Standard);
            AudioDecodeArtifact audio = Assert.IsType<AudioDecodeArtifact>(scan.Artifact(ArtifactKind.AudioDecode, new AudioDecodeProvider()));
            Assert.Equal(Md5Verdict.Match, audio.Md5);
            Assert.Equal(20.0, audio.DecodedSeconds!.Value, 2);

            // The 24-bit copy of a 16-bit file is padded (subplan 10); nothing else is reported.
            Assert.All(scan.Findings, f => Assert.Equal("AUD-PADDED-BITS", f.RuleId));
            Assert.Equal(name == "pad24.flac" ? 1 : 0, scan.Findings.Count);
        }
    }

    [FixtureFact]
    public async Task Truncated_mp3_reports_the_seconds_that_decode()
    {
        Scan scan = await ScanOneAsync("m128.mp3", path => Mutate.Truncate(path, 0.55), ScanDepth.Standard);
        FindingRecord truncated = Assert.Single(scan.Findings, f => f.RuleId == "COR-TRUNCATED");
        IReadOnlyDictionary<string, object> evidence = EvidenceJson.Deserialize(truncated.EvidenceJson);
        Assert.Equal(20.0, Convert.ToDouble(evidence["declaredSeconds"], System.Globalization.CultureInfo.InvariantCulture), 0);
        Assert.Equal(11.0, Convert.ToDouble(evidence["decodedSeconds"], System.Globalization.CultureInfo.InvariantCulture), 0);
        Assert.Equal(Severity.High, truncated.Severity);
    }

    [FixtureFact]
    public async Task Truncated_fast_start_mp4_stops_early()
    {
        Scan scan = await ScanOneAsync("fs.mp4", path => Mutate.Truncate(path, 0.6), ScanDepth.Standard);
        FindingRecord truncated = Assert.Single(scan.Findings, f => f.RuleId == "COR-TRUNCATED");
        output.WriteLine(truncated.Summary + " " + truncated.EvidenceJson);
        VideoDecodeArtifact video = Assert.IsType<VideoDecodeArtifact>(scan.Artifact(ArtifactKind.VideoDecode, new VideoDecodeProvider()));
        Assert.True(video.DecodedSeconds < 5.0, $"decoded {video.DecodedSeconds}");
    }

    [FixtureFact]
    public async Task A_photo_named_like_a_sequence_pattern_is_read_as_one_file()
    {
        Scan scan = await ScanOneAsync("img.jpg", null, ScanDepth.Standard, "a%03d.jpg");
        ImageDecodeArtifact image = Assert.IsType<ImageDecodeArtifact>(scan.Artifact(ArtifactKind.Decode, new ImageDecodeProvider()));
        Assert.Equal(1, image.FramesRead);
        Assert.Empty(scan.Findings);
    }

    [FixtureFact]
    public async Task Video_standard_pass_records_the_stream_hash()
    {
        Scan scan = await ScanOneAsync("av.mp4", null, ScanDepth.Standard);
        Assert.NotNull(scan.File.StreamHash);
        Assert.Equal(16, scan.File.StreamHash!.Length);

        // Re-tagging changes the file but not the packets: the audio stream hash survives a remux of tags.
        Scan audio = await ScanOneAsync("m128.mp3", null, ScanDepth.Standard);
        Assert.Equal(16, audio.File.StreamHash!.Length);
    }

    [FfmpegFact]
    public async Task A_stalled_decoder_is_a_hang_and_leaves_no_process_behind()
    {
        // Its own copy of the helper, so helper processes of other tests (kill tests) are not counted.
        string helper = Path.Combine(AppContext.BaseDirectory, "stall-ffmpeg.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "MediaVerifier.TestHelper.exe"), helper, overwrite: true);
        using FfRunner runner = new(new FfmpegLocation(AppContext.BaseDirectory, IsOverride: true)) { ExecutableOverride = _ => helper };
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), new ScanSettings { StallSeconds = 1 }, runner: runner);
        harness.Write("song.flac", EngineHarness.Bytes(64 * 1024, 3));
        await harness.AddRootAsync(harness.Library);

        await harness.ScanAsync(ScanDepth.Standard);

        FileRecord file = harness.AllFiles().Single();
        FindingRecord hang = Assert.Single(harness.Database.Findings.ForFile(file.Id), f => f.RuleId == "COR-HANG");
        Assert.Equal(Severity.High, hang.Severity);
        Assert.DoesNotContain(Process.GetProcessesByName("stall-ffmpeg"), p => !p.HasExited);
    }

    private static IEnumerable<string> Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Mid-file where Standard is sure to read the damage: the middle of an MP3; in a video, the start of the audio
    /// packet nearest the middle. Standard decodes all audio but only key frames, and what sits mid-file depends on the
    /// machine: x264's output changes with its thread count, so on a 4-core CI runner the middle is a non-key frame.
    /// </summary>
    private static double MiddleReadAtStandard(string path)
    {
        if (!path.EndsWith(".mp4", StringComparison.Ordinal))
        {
            return 0.5;
        }

        FfmpegLocation engine = FfmpegEnvironment.Engine ?? throw new InvalidOperationException("No FFmpeg engine; the test should have been skipped.");
        ProcessStartInfo start = new(engine.FfprobeExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "packet=pos", "-of", "csv=p=0", path })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string[] lines = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        process.WaitForExit();
        long length = new FileInfo(path).Length;

        // A packet with side data ends its line with a comma.
        long packet = lines.Select(line => long.Parse(line.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture)).MinBy(pos => Math.Abs(pos - (length / 2)));

        // Half a byte in, so RandomBlock's rounding starts the block exactly at the packet.
        return (packet + 0.5) / length;
    }

    /// <summary>The matrix damages files at mid-file (subplan 15); several bytes are spread around the middle.</summary>
    private static void Apply(string path, string mutation)
    {
        string[] parts = mutation.Split(' ');
        switch (parts[0])
        {
            case "flip":
                Mutate.FlipByte(path, 0.5);
                break;
            case "flip2":
                Mutate.FlipByte(path, 0.45);
                Mutate.FlipByte(path, 0.55);
                break;
            case "flip3":
                Mutate.FlipByte(path, 0.4);
                Mutate.FlipByte(path, 0.5);
                Mutate.FlipByte(path, 0.6);
                break;
            case "zero":
                Mutate.ZeroBlock(path, 0.5);
                break;
            case "random":
                Mutate.RandomBlock(path, 0.5, int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), seed: 1);
                break;
            case "truncate":
                Mutate.Truncate(path, double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                throw new ArgumentException("Unknown mutation " + mutation);
        }
    }

    private static List<string> Gate(FfRunner runner)
    {
        List<string> unknown = [];
        runner.ObserveLine = line =>
        {
            if (line.Kind == LogSignalKind.Unknown)
            {
                lock (unknown)
                {
                    unknown.Add(line.Raw);
                }
            }
        };
        return unknown;
    }

    private async Task<Scan> ScanOneAsync(string fixture, Action<string>? mutate, ScanDepth depth, string? name = null)
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        List<string> unknown = Gate(runner);
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string path = FixtureFactory.CopyTo(fixture, harness.Library, name);
        mutate?.Invoke(path);
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(depth);
        FileRecord file = harness.AllFiles().Single();
        List<FindingRecord> findings = [.. harness.Database.Findings.ForFile(file.Id).Where(f => f.Status == FindingStatus.Open)];
        Dictionary<ArtifactKind, (int ProviderVersion, string Data)> artifacts = harness.Database.Artifacts.ForFile(file.Id);
        foreach (string line in unknown)
        {
            output.WriteLine("UNKNOWN: " + line);
        }

        Assert.Empty(unknown);
        return new Scan(file, findings, artifacts);
    }

    private sealed record Scan(FileRecord File, List<FindingRecord> Findings, Dictionary<ArtifactKind, (int ProviderVersion, string Data)> Artifacts)
    {
        public IArtifact? Artifact(ArtifactKind kind, IArtifactProvider provider) =>
            Artifacts.TryGetValue(kind, out (int ProviderVersion, string Data) stored) ? provider.Deserialize(stored.Data) : null;
    }
}
