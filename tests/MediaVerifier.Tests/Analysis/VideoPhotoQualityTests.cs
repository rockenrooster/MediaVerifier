using System.Drawing.Imaging;
using System.Globalization;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Analysis;

/// <summary>Frame sampling, the down-up decision, bitrate and JPEG quality (subplan 11, tasks V-01 to V-04, P-01 to P-03).</summary>
public sealed class VideoPhotoQualityTests
{
    [Fact]
    public void The_graph_matches_the_verified_command()
    {
        string graph = FrameSampling.Graph(3, 1152, 648, [720, 540], [(768, 432), (576, 324)], deinterlace: false);
        string expected =
            "[0:v]trim=end_frame=1,setpts=PTS-STARTPTS[s0];" +
            "[1:v]trim=end_frame=1,setpts=PTS-STARTPTS[s1];" +
            "[2:v]trim=end_frame=1,setpts=PTS-STARTPTS[s2];" +
            "[s0][s1][s2]concat=n=3:v=1:a=0,crop=1152:648,split=4[o][a][b][m];" +
            "[a]split[a1][a2];[a2]scale=768:432:flags=bicubic,scale=1152:648:flags=bicubic[a3];" +
            "[a1][a3]ssim=stats_file=c720.log[ao];" +
            "[b]split[b1][b2];[b2]scale=576:324:flags=bicubic,scale=1152:648:flags=bicubic[b3];" +
            "[b1][b3]ssim=stats_file=c540.log[bo];" +
            "[m]siti=print_summary=0,blockdetect,blurdetect,metadata=mode=print:file=frame.log[mo]";
        Assert.Equal(expected, graph);
        Assert.Equal(["-map", "[o]", "-f", "null", "-", "-map", "[ao]", "-f", "null", "-", "-map", "[bo]", "-f", "null", "-", "-map", "[mo]", "-f", "null", "-"], FrameSampling.Outputs(2));
        Assert.Contains("[0:v]yadif=deint=all,trim=end_frame=1", FrameSampling.Graph(1, 100, 100, [50], [(50, 50)], deinterlace: true), StringComparison.Ordinal);

        // The measures run on the centre quarter, its corner on the frame's 16-pixel grid: 1080p crop 1152 x 648 at (384, 216).
        MetricsWindow window = FrameSampling.Window(1920, 1080, 1152, 648, spatialOnly: false);
        Assert.Equal(new MetricsWindow(288, 152, 576, 324, false), window);
        Assert.Equal(0, (384 + window.X) % 16);
        Assert.Equal(0, (216 + window.Y) % 16);
        Assert.EndsWith("[m]crop=576:324:288:152,siti=print_summary=0,blockdetect,blurdetect,metadata=mode=print:file=frame.log[mo]", FrameSampling.Graph(3, 1152, 648, [720, 540], [(768, 432), (576, 324)], false, window), StringComparison.Ordinal);
        Assert.EndsWith("[m]crop=1536:864:768:424,siti=print_summary=0,metadata=mode=print:file=frame.log[mo]", FrameSampling.Graph(1, 3072, 1728, [1440], [(2048, 1152)], false, FrameSampling.Window(3840, 2160, 3072, 1728, spatialOnly: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void Sizes_samples_and_candidates_follow_the_tables()
    {
        Assert.Equal((1152, 648), FrameSampling.Crop(1920, 1080, 0.6));
        Assert.Equal((768, 432), FrameSampling.Scaled(1152, 648, 720, 1080));
        Assert.Equal([720, 540, 360], FrameSampling.VideoCandidates(1080));
        Assert.Equal([1440, 1080, 720], FrameSampling.VideoCandidates(2160));
        Assert.Equal([360, 240], FrameSampling.VideoCandidates(576));
        Assert.Equal([534, 400, 266], FrameSampling.VideoCandidates(800));
        Assert.Equal([1440, 1080, 720, 540], FrameSampling.PhotoCandidates(2160));

        double[] short30 = FrameSampling.SampleTimes(30);
        Assert.Equal(6, short30.Length);
        Assert.Equal(1.5, short30[0]);
        Assert.Equal(28.5, short30[^1]);
        Assert.Equal(10, FrameSampling.SampleTimes(600).Length);
        Assert.Equal(8, FrameSampling.SampleTimes(90).Length);
        Assert.Equal(24, FrameSampling.SampleTimes(7200).Length);
    }

    [Fact]
    public void Parsers_read_the_verified_log_formats()
    {
        Assert.Equal([0.971018, 0.956629], FrameSampling.ParseSsim("n:1 Y:0.971018 U:0.981134 V:0.980853 All:0.974343 (15.907929)\nn:2 Y:0.956629 U:0.977363 V:0.976493 All:0.963396 (14.364658)\n"));
        List<(double SpatialInfo, double Block, double Blur)> frames = FrameSampling.ParseFrameLog(
            "frame:0    pts:0       pts_time:0\nlavfi.siti.si=54.77\nlavfi.siti.ti=0.00\nlavfi.block=1.488698\nlavfi.blur=5.933234\n" +
            "frame:1    pts:0       pts_time:0\nlavfi.siti.si=59.09\nlavfi.siti.ti=46.58\nlavfi.block=1.253489\nlavfi.blur=5.774832\n");
        Assert.Equal(2, frames.Count);
        Assert.Equal((54.77, 1.488698, 5.933234), frames[0]);
        Assert.Equal(59.09, frames[1].SpatialInfo);
    }

    [Fact]
    public void The_decision_takes_the_smallest_passing_candidate_before_the_fall_off()
    {
        // The verified table: upscaled from 540p passes 720 and 540 and falls away at 360.
        FrameSamplesArtifact up540 = Samples(1080, [720, 540, 360], 8, [0.994, 0.991, 0.980]);
        UpscaleVerdict verdict = UpscaleDecision.Decide(up540, 25, 0.985, 4, starved: false)!;
        Assert.Equal(540, verdict.EffectiveHeight);
        Assert.True(verdict.FallOff);
        Assert.Equal(0.85f, verdict.Confidence);

        // Native: nothing passes.
        Assert.Null(UpscaleDecision.Decide(Samples(1080, [720, 540, 360], 8, [0.965, 0.948, 0.963]), 25, 0.985, 4, false));

        // Too few usable frames (flat frames below the spatial-information minimum).
        Assert.Null(UpscaleDecision.Decide(Samples(1080, [720, 540, 360], 8, [0.994, 0.991, 0.980], spatialInfo: 10), 25, 0.985, 4, false));

        // A small margin, few frames and starvation lower the confidence.
        UpscaleVerdict weak = UpscaleDecision.Decide(Samples(1080, [720, 540, 360], 5, [0.987, 0.975, 0.970]), 25, 0.985, 4, starved: true)!;
        Assert.Equal(720, weak.EffectiveHeight);
        Assert.Equal(0.65f - 0.20f - 0.15f, weak.Confidence, 3);
    }

    [Fact]
    public void Bitrate_comes_from_the_stream_the_container_or_the_size()
    {
        StreamInfo video = Video("h264", 1920, 1080, 24, 1_750_000);
        StreamInfo audio = new(1, "audio", "aac", null, null, null, null, null, null, null, 48_000, 2, null, null, 60, 128_000, null, null, null, false, true);
        ProbeArtifact withStream = Probe([video, audio], 9_000_000, 60, 10_000_000);
        Assert.Equal((1_750_000L, "stream"), VideoBitrate.VideoBitRate(withStream, video));
        StreamInfo bare = video with { BitRate = null };
        Assert.Equal((8_872_000L, "container"), VideoBitrate.VideoBitRate(Probe([bare, audio], 9_000_000, 60, 10_000_000), bare));
        Assert.Equal((10_000_000L * 8 / 60 - 128_000, "size"), VideoBitrate.VideoBitRate(Probe([bare, audio], null, 60, 10_000_000), bare));

        double eq = VideoBitrate.EquivalentBpp(withStream, video, out _, out double bpp)!.Value;
        Assert.Equal(0.035, bpp, 3);
        Assert.Equal(bpp, eq, 6);
        StreamInfo hevc = video with { Codec = "hevc" };
        Assert.Equal(bpp / 0.6, VideoBitrate.EquivalentBpp(Probe([hevc], null, 60, null), hevc, out _, out _)!.Value, 6);
        Assert.Null(VideoBitrate.CodecFactor("prores"));
        Assert.Equal(2.2, VideoBitrate.CodecFactor("mpeg2video"));
    }

    [Fact]
    public void Starved_and_hdr_rules_read_the_probe()
    {
        StreamInfo starved = Video("h264", 1920, 1080, 24, 300_000);
        List<Finding> findings = EvaluateVideo(Probe([starved], null, 60, null));
        Assert.Equal(Severity.Medium, Assert.Single(findings, f => f.RuleId == "VID-STARVED").Severity);

        StreamInfo hdr = Video("hevc", 3840, 2160, 24, 40_000_000) with { ColorTransfer = "smpte2084", PixFmt = "yuv420p", ColorPrimaries = "bt2020" };
        Assert.Equal(Severity.Medium, Assert.Single(EvaluateVideo(Probe([hdr], null, 60, null)), f => f.RuleId == "VID-HDR").Severity);
        StreamInfo hdr10 = hdr with { PixFmt = "yuv420p10le" };
        Assert.DoesNotContain(EvaluateVideo(Probe([hdr10], null, 60, null)), f => f.RuleId == "VID-HDR");
    }

    [Fact]
    public void Jpeg_quality_is_estimated_from_standard_tables()
    {
        for (int quality = 20; quality <= 95; quality += 5)
        {
            int scale = quality < 50 ? 5000 / quality : 200 - (2 * quality);
            int[] zigzag = new int[64];
            for (int i = 0; i < 64; i++)
            {
                zigzag[i] = Math.Clamp(((JpegQuality.StandardLuminance[JpegQuality.ZigZag[i]] * scale) + 50) / 100, 1, 255);
            }

            (double estimate, bool standard) = JpegQuality.Estimate(zigzag);
            Assert.True(Math.Abs(estimate - quality) <= 3, $"quality {quality}: estimated {estimate:0.0}");
            Assert.True(standard, $"quality {quality} not recognised as a standard table");
        }
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(90)]
    public void Jpeg_quality_is_estimated_for_files_written_by_windows(int quality)
    {
        using Bitmap bitmap = new(64, 64);
        using MemoryStream stream = new();
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using EncoderParameters parameters = new(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bitmap.Save(stream, codec, parameters);
        byte[] bytes = stream.ToArray();
        int dqt = Enumerable.Range(0, bytes.Length - 1).First(i => bytes[i] == 0xFF && bytes[i + 1] == 0xDB);
        int[] table = [.. bytes.Skip(dqt + 5).Take(64).Select(b => (int)b)];
        (double estimate, _) = JpegQuality.Estimate(table);
        Assert.True(Math.Abs(estimate - quality) <= 3, $"quality {quality}: estimated {estimate:0.0}");
    }

    // Each frame a different picture (its spatial information differs): repeated pictures count once.
    private static FrameSamplesArtifact Samples(int height, int[] candidates, int frames, double[] ssim, double spatialInfo = 50) =>
        new(RunOutcome.Completed, LogDigest.Empty, height * 16 / 9, height, 0, 0, candidates,
            [.. Enumerable.Range(0, frames).Select(i => new SampledFrame(i, spatialInfo + (i * 0.01), 1.5, 6, [.. ssim]))]);

    private static StreamInfo Video(string codec, int width, int height, double fps, long? bitRate) =>
        new(0, "video", codec, null, width, height, "yuv420p", "progressive", fps, null, null, null, null, null, 60, bitRate, null, null, null, false, true);

    private static ProbeArtifact Probe(StreamInfo[] streams, long? bitRate, double duration, long? size) =>
        new(true, false, "mov,mp4", 100, duration, bitRate, size, streams, LogDigest.Empty, RunOutcome.Completed);

    private static List<Finding> EvaluateVideo(ProbeArtifact probe)
    {
        Dictionary<Type, IArtifact> artifacts = new() { [typeof(ProbeArtifact)] = probe };
        DetectorInput input = new(@"D:\v.mp4", ".mp4", MediaKind.Video, ScanDepth.Quick, RootRole.Library, null, artifacts, new RuleSettings());
        FindingSink sink = new();
        new VideoQualityDetector().Evaluate(input, sink);
        return [.. sink.Findings];
    }
}

/// <summary>The verified fixtures of subplan 11 scanned at Standard depth.</summary>
[Trait("Category", "Integration")]
public sealed class VideoPhotoQualityFixtureTests(ITestOutputHelper output)
{
    [FixtureFact]
    public async Task Upscaled_and_starved_fixtures_are_found_and_native_ones_are_not()
    {
        (string Name, string Expected)[] table =
        [
            ("native1080k.mp4", ""),
            ("up720k.mp4", "VID-UPSCALED@720"),
            ("up540k.mp4", "VID-UPSCALED@540"),

            // A key frame every 2 s: the 6 samples of these 6 s clips are 3 different pictures, too few for a verdict.
            ("native1080.mp4", ""),
            ("up720.mp4", ""),
            ("up540.mp4", ""),

            // One key frame: starved, but whether it was also upscaled cannot be told from one picture.
            ("starved.mp4", "VID-STARVED"),
            ("hdr8.mp4", "VID-HDR"),
            ("img.jpg", ""),
            // A 2× bicubic enlargement of a 1080-line image. At the threshold calibrated on real photos (0.996) its
            // 1080 rung falls just short, so the estimate is the conservative 1440 lines: an upper bound on its detail.
            ("img2x.jpg", "IMG-UPSCALED@1440"),
            ("shot.png", ""),
        ];
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        foreach ((string name, _) in table)
        {
            FixtureFactory.CopyTo(name, harness.Library);
        }

        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Standard);
        List<string> failures = [];
        foreach ((string name, string expected) in table)
        {
            FileRecord file = harness.AllFiles().Single(f => f.Name == name);
            List<FindingRecord> findings = [.. harness.Database.Findings.ForFile(file.Id).Where(f => f.Status == FindingStatus.Open && (f.RuleId.StartsWith("VID-", StringComparison.Ordinal) || f.RuleId.StartsWith("IMG-", StringComparison.Ordinal)))];
            output.WriteLine($"{name,-16} {string.Join("; ", findings.Select(f => $"{f.RuleId} {f.Severity} {f.Confidence:0.00} \"{f.Summary}\" {f.EvidenceJson}"))}");
            string[] wanted = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (string want in wanted)
            {
                string[] parts = want.Split('@');
                FindingRecord? match = findings.FirstOrDefault(f => f.RuleId == parts[0]);
                if (match is null)
                {
                    failures.Add($"{name}: no {parts[0]}");
                }
                else if (parts.Length > 1 && !(match.EvidenceJson ?? string.Empty).Contains("\"effectiveHeight\":" + parts[1], StringComparison.Ordinal))
                {
                    failures.Add($"{name}: {parts[0]} at the wrong height: {match.EvidenceJson}");
                }
            }

            foreach (FindingRecord extra in findings.Where(f => !wanted.Any(w => w.Split('@')[0] == f.RuleId) && f.RuleId is "VID-UPSCALED" or "IMG-UPSCALED" or "VID-STARVED"))
            {
                failures.Add($"{name}: unexpected {extra.RuleId} {extra.Summary}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
