using System.Buffers.Binary;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.Analysis.Structure;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;
using static MediaVerifier.Tests.Analysis.Structure.TestMedia;

namespace MediaVerifier.Tests.Analysis;

/// <summary>
/// False findings from the 2026-11-17 library audit: decoder arguments and log lines the app causes itself, padding
/// read as lost data, repeated key frames, confidence rounding and the error-spot count.
/// </summary>
public sealed class AnalysisFindingsTests
{
    [Fact]
    public void Decodes_check_crcs_without_the_buffer_flag()
    {
        // With buffer the MP3 decoder reports legal stuffing bits as "bits_left=N" errors and zeroes those granules.
        Assert.DoesNotContain("buffer", FfmpegArgs.ErrorDetection(), StringComparison.Ordinal);
        Assert.DoesNotContain("buffer", FfmpegArgs.ErrorDetection(strict: true), StringComparison.Ordinal);
        Assert.Equal(["-max_error_rate", "1", "-err_detect", "crccheck+bitstream"], FfmpegArgs.Decode());

        List<string> jpeg = ImageDecodeProvider.Arguments(@"D:\p\IMG_1.jpg", "jpeg");
        Assert.Equal(["-err_detect", "crccheck+bitstream", "-f", "jpeg_pipe", "-read_intervals", "%+#1", "-count_frames"], jpeg.Take(7));
        Assert.Equal(@"D:\p\IMG_1.jpg", jpeg[^1]);

        // Animated formats keep every frame.
        Assert.DoesNotContain("-read_intervals", ImageDecodeProvider.Arguments(@"D:\p\a.png", "png"));
        Assert.DoesNotContain("-read_intervals", ImageDecodeProvider.Arguments(@"D:\p\a.gif", "gif"));
    }

    [Fact]
    public void Lines_the_decode_command_causes_are_ignored()
    {
        LogLine nullMuxer = LogClassifier.Classify("[null @ 000001e3e496d280] [error] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 5 >= 5");
        Assert.Equal(LogSignalKind.Timestamp, nullMuxer.Kind);
        Assert.True(VideoDecodeProvider.Ignored(nullMuxer, deep: false));
        Assert.True(VideoDecodeProvider.Ignored(nullMuxer, deep: true));

        // The stream hash copies the file's own packets: its muxer's complaints are about the file.
        LogLine streamHash = LogClassifier.Classify("[streamhash @ 000001e3e496d280] [error] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 5 >= 5");
        Assert.False(VideoDecodeProvider.Ignored(streamHash, deep: false));

        LogLine poc = LogClassifier.Classify("[hevc @ 000001e3e496d280] [error] Duplicate POC in a sequence: 0.");
        Assert.Equal(LogSignalKind.Unknown, poc.Kind);
        Assert.True(VideoDecodeProvider.Ignored(poc, deep: false));
        Assert.False(VideoDecodeProvider.Ignored(poc, deep: true));

        Assert.False(VideoDecodeProvider.Ignored(LogClassifier.Classify("[h264 @ 000001e3e496d280] [error] error while decoding MB 52 30, bytestream -7"), deep: false));
    }

    [Fact]
    public void An_unknown_line_beside_a_decoded_picture_is_not_a_partial_image()
    {
        // A Pixel photo read as two pictures and a stub: "No JPEG data found in image" (unknown) with 2 frames.
        List<Finding> stub = EvaluateImage(new SignalCounts(Unknown: 1), frames: 2);
        Assert.Empty(stub);

        // Nothing decoded: the unknown line still counts, at half weight rounded up.
        Finding none = Assert.Single(EvaluateImage(new SignalCounts(Unknown: 1), frames: 0));
        Assert.Equal("COR-DECODE", none.RuleId);
        Assert.Equal(Severity.Critical, none.Severity);
        Assert.Equal("Nothing could be decoded (1 decoder error)", none.Summary);

        Finding partial = Assert.Single(EvaluateImage(new SignalCounts(DecodeError: 2, Unknown: 3), frames: 1));
        Assert.Equal("COR-PARTIAL-IMAGE", partial.RuleId);
        Assert.Equal("Decoder reported 2 errors in the picture", partial.Summary);
    }

    [Fact]
    public void Every_error_spot_is_counted_and_the_summary_says_how_many_more()
    {
        // Errors every half second for 4 minutes: spots every 2 s, 120 in all, the first 50 kept.
        double[] seconds = [.. Enumerable.Range(0, 480).Select(i => i * 0.5)];
        double[] merged = ErrorSpots.Merge(seconds, out int count);
        Assert.Equal(ErrorSpots.Max, merged.Length);
        Assert.Equal(120, count);

        SpotList online = new();
        foreach (double s in seconds)
        {
            online.Add(s);
        }

        Assert.Equal(merged, online.ToArray());
        Assert.Equal(count, online.Count);
        Assert.Equal(", around 0:00, 0:02, 0:04 and 117 more places", EvidenceText.Around(merged, count));
        Assert.Equal(", around 0:00, 0:02, 0:04 and 47 more places", EvidenceText.Around(merged));

        // Arrival times are thinned, never capped: the last error of a long run is still there.
        ErrorTimes times = new();
        for (int i = 0; i < 300_000; i++)
        {
            times.Add(i * 0.001);
        }

        Assert.InRange(times.Times.Count, 10_000, 65_536);
        Assert.True(times.Times[^1] > 299 * 0.99, $"last time {times.Times[^1]}");
    }

    [Fact]
    public void Zeros_the_container_declares_as_padding_are_not_lost_data()
    {
        // 20170408_193444.jpg: the picture ends at EOI and 160 KiB of zeros follow, which the walker records as padding.
        byte[] image = BaselineJpeg();
        byte[] file = Concat(image, new byte[163_840 + 3000]);
        StructureArtifact structure = Read(file, "jpg");
        Assert.Empty(structure.Issues);
        PaddingRegion trailer = Assert.Single(structure.Padding);
        Assert.Equal(new PaddingRegion(image.Length, file.Length - image.Length), trailer);

        long runOffset = ((image.Length / 4096) + 1) * 4096L;
        long runBlocks = (file.Length - runOffset + 4095) / 4096;
        long totalBlocks = (file.Length + 4095) / 4096;
        Assert.Empty(EvaluateZeros(file.Length, runBlocks, totalBlocks, runBlocks, runOffset, [runOffset], structure, "jpg"));

        // HTC panorama: most of the file is zeros after EOI; only the picture counts towards the share.
        long bigRun = 400;
        Assert.Empty(EvaluateZeros(4096 * 402, bigRun, 402, bigRun, 8192, [8192], Structure(new PaddingRegion(4000, (4096 * 402) - 4000)), "jpg"));

        // Without the structure the same zeros are most of this small file; a run in a large file is reported in KB.
        Assert.Equal(Severity.Critical, Assert.Single(EvaluateZeros(file.Length, runBlocks, totalBlocks, runBlocks, runOffset, [runOffset], null, "jpg")).Severity);
        Finding low = Assert.Single(EvaluateZeros(10_000_000, 40, 2442, 40, 8192, [8192], null, "jpg"));
        Assert.Equal(Severity.Low, low.Severity);
        Assert.Equal("160 KB of zeros at byte 8,192", low.Summary);

        // A free box holds the longest run, but another run lies in the media data: that one is reported.
        StructureArtifact mp4 = Structure(new PaddingRegion(2537, 807_527));
        Finding other = Assert.Single(EvaluateZeros(10_000_000, 196 + 20, 2442, 196, 4096, [4096, 5_000_000], mp4, "mp4"));
        Assert.Equal(Severity.Low, other.Severity);
        Assert.Equal("64 KB or more of zeros at byte 5,000,000", other.Summary);
        Assert.Equal([5_000_000L], Assert.IsType<long[]>(other.Evidence["runOffsetsBytes"]));

        // VIDEO0021.mp4: 52 % zeros, all of it the 800 KB free box. Not Critical, and no Low either.
        Assert.Empty(EvaluateZeros(1_600_000, 196, 391, 196, 4096, [4096], mp4, "mp4"));

        // Zeros that are not padding still make a Critical.
        Finding critical = Assert.Single(EvaluateZeros(1_600_000, 300, 391, 196, 4096, [4096, 900_000], Structure(new PaddingRegion(900_000, 10)), "mp4"));
        Assert.Equal(Severity.Critical, critical.Severity);
    }

    [Fact]
    public async Task A_scan_reads_the_padding_before_judging_zeros()
    {
        // Through the engine at Quick: the structure check runs for the zero-fill check and its padding is honoured.
        using EngineHarness harness = new([new StructureProvider()], [new StructureDetector()]);
        harness.Write("padded.jpg", Concat(BaselineJpeg(), new byte[200 * 1024]));
        byte[] head = BaselineJpeg()[..^2];
        harness.Write("zeroed.jpg", Concat(head, new byte[200 * 1024], Entropy(4000, 5), EoiMarker));
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Quick);

        Assert.Empty(harness.Database.Findings.ForFile(harness.File1("padded.jpg").Id));
        FindingRecord zeros = Assert.Single(harness.Database.Findings.ForFile(harness.File1("zeroed.jpg").Id));
        Assert.Equal("GEN-ZEROFILL", zeros.RuleId);
    }

    [Fact]
    public void Camera_trailers_after_a_jpeg_are_known()
    {
        byte[] image = BaselineJpeg();

        // Sony: zero padding, then the preview its MPF index points at. The zeros are padding.
        byte[] sony = Concat(image, new byte[1486], BaselineJpeg(160, 120));
        StructureArtifact sonyRead = Read(sony, "jpg");
        Assert.Empty(sonyRead.Issues);
        Assert.Equal(new PaddingRegion(image.Length, 1486), Assert.Single(sonyRead.Padding));

        // Olympus: some 30 bytes, then a preview JPEG.
        Assert.Empty(Read(Concat(image, Ascii("OLYMP\0\0II\x03\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"), BaselineJpeg(160, 120)), "jpg").Issues);

        // Google camera: [payload][length LE][tag] records back to EOI. "ctrace" alone, "best" after it, and a depth map.
        byte[] ctrace = Record(new ByteBuilder().Fill(1100).Noise(96, 3).ToArray(), "ctrace");
        Assert.Equal(1208, ctrace.Length);
        Assert.Empty(Read(Concat(image, ctrace), "jpg").Issues);
        Assert.Empty(Read(Concat(image, ctrace, Record([1, 0, 0, 0], "best")), "jpg").Issues);
        Assert.Empty(Read(Concat(image, ctrace, Record(Ascii("edof"), "edof"), Record(BaselineJpeg(320, 240), "DepthEn")), "jpg").Issues);

        // Leftovers that are none of these stay trailing data, and a chain that does not end at EOI does not count.
        Assert.Equal(StructureIssueKind.TrailingData, Assert.Single(Read(Concat(image, Ascii("leftovers"), ctrace), "jpg").Issues).Kind);
        Assert.Equal(StructureIssueKind.TrailingData, Assert.Single(Read(Concat(image, Ascii("appended by something, not a picture")), "jpg").Issues).Kind);
    }

    [Fact]
    public void A_riff_size_that_counts_its_own_header_is_not_a_cut()
    {
        // MOV0001A.avi: the RIFF size equals the file length (8 too many); the last chunk ends at the end of the file.
        byte[] avi = Avi();
        BinaryPrimitives.WriteUInt32LittleEndian(avi.AsSpan(4), (uint)avi.Length);
        StructureArtifact quirk = Read(avi, "avi");
        Assert.Empty(quirk.Issues);
        Assert.Equal(avi.Length, quirk.DeclaredEnd);

        // A real cut of 8 bytes: the last chunk runs past the end.
        byte[] cut = Avi()[..^8];
        StructureIssue issue = Assert.Single(Read(cut, "avi").Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal(8, issue.MissingBytes);
    }

    [Fact]
    public void A_key_frame_sampled_several_times_counts_once()
    {
        // 20160519_105250.mkv: one key frame, so all six samples are the same picture: insufficient, no finding.
        double[] ssim = [0.9909, 0.9925, 0.9600];
        FrameSamplesArtifact repeated = new(RunOutcome.Completed, LogDigest.Empty, 1920, 1080, 1152, 648, [720, 540, 360],
            [.. Enumerable.Range(0, 6).Select(i => new SampledFrame(i * 1.5, 60.5, 1.18392 + (i * 1e-6), 5.756994, [.. ssim]))]);
        Assert.Single(UpscaleDecision.UsableFrames(repeated, 40));
        Assert.Null(UpscaleDecision.Decide(repeated, 40, 0.990, 4, starved: false));
        Assert.Empty(EvaluateVideoSamples(repeated));

        // Two pictures seen three times each: still too few.
        FrameSamplesArtifact twice = repeated with { Frames = [.. Enumerable.Range(0, 6).Select(i => new SampledFrame(i, 60 + (i % 2), 1.2, 5.7, [.. ssim]))] };
        Assert.Equal(2, UpscaleDecision.UsableFrames(twice, 40).Length);
        Assert.Null(UpscaleDecision.Decide(twice, 40, 0.990, 4, starved: false));

        // Four distinct pictures, two of them repeated: four count, the repeats are shown as unused.
        FrameSamplesArtifact four = repeated with { Frames = [.. new[] { 0, 1, 1, 2, 3, 3 }.Select(k => new SampledFrame(k, 60 + k, 1.2, 5.7, [0.9930 + (k * 0.0001), 0.9925, 0.9600]))] };
        UpscaleVerdict verdict = UpscaleDecision.Decide(four, 40, 0.990, 4, starved: false)!;
        Assert.Equal(4, verdict.UsableFrames);
        Assert.Equal([true, true, false, true, true, false], verdict.FrameUsable);
    }

    [Fact]
    public void A_plan_confidence_of_one_half_is_one_half()
    {
        Assert.True(0.65f - 0.15f < 0.5f);
        Assert.Equal(0.5f, FindingSink.RoundConfidence(0.65f - 0.15f));
        Assert.Equal(0.5f, FindingSink.RoundConfidence(0.85f - 0.20f - 0.15f));
        Assert.Equal(1f, FindingSink.RoundConfidence(1.2f));

        // Small margin, no fall-off: 0.65 - 0.15 = 0.50, so 540p from 1080p is Medium and reads "Possibly".
        FrameSamplesArtifact samples = new(RunOutcome.Completed, LogDigest.Empty, 1920, 1080, 1152, 648, [720, 540, 360],
            [.. Enumerable.Range(0, 8).Select(i => new SampledFrame(i, 60 + i, 1.2, 5.7, [0.9930, 0.9920, 0.9900]))]);
        Finding upscaled = Assert.Single(EvaluateVideoSamples(samples), f => f.RuleId == "VID-UPSCALED");
        Assert.Equal(0.5f, upscaled.Confidence);
        Assert.Equal(Severity.Medium, upscaled.Severity);
        Assert.Equal(ConfidenceWording.Possibly, SeverityPolicy.Wording(upscaled.Confidence));
        Assert.Equal("1080p file with detail equivalent to 360p (8 of 8 sampled frames are reproducible from 360p)", upscaled.Summary);

        FindingSink sink = new();
        sink.Raise("VID-UPSCALED", Severity.Low, 0.65f - 0.15f, "x");
        Assert.Equal(0.5f, sink.Findings[0].Confidence);
    }

    [Fact]
    public void A_silent_soundtrack_reads_digital_silence()
    {
        Finding silent = Assert.Single(EvaluateSoundtrack(-91.0), f => f.RuleId == "VID-AUDIO-SILENT");
        Assert.Equal("the main soundtrack is silent from start to end (digital silence)", silent.Summary);
        Assert.Equal(-91.0, silent.Evidence["maxVolumeDb"]);

        Finding quiet = Assert.Single(EvaluateSoundtrack(-66.2), f => f.RuleId == "VID-AUDIO-SILENT");
        Assert.Equal("the main soundtrack is silent from start to end (peak -66.2 dBFS)", quiet.Summary);
    }

    [Fact]
    public void Overs_show_the_peak_to_two_decimals()
    {
        ChannelLevels level = new(1.0023, 0.1, 0.001, 1, 0.5);
        SpectrumStats spectrum = new(true, 22_050, 0, 0, 0, 22_050, 4096, 500, 60, new byte[96]);
        PcmStats stats = new(44_100, 2, 60, [level, level], false, -1, new ClippingStats(0, 0, 0, 20 * Math.Log10(1.0023), 0, []), 0.5, false, spectrum, new DropoutStats(0, 0, [], []), 0, 0);
        AudioDecodeArtifact decode = new(RunOutcome.Completed, false, LogDigest.Empty, true, 60 * 44_100, 44_100, 2, Md5Verdict.NotApplicable, stats);
        ProbeArtifact probe = new(true, false, "mp3", 100, 60, 320_000, 1000,
            [new StreamInfo(0, "audio", "mp3", null, null, null, null, null, null, null, 44_100, 2, null, null, 60, 320_000, null, null, null, false, true)],
            LogDigest.Empty, RunOutcome.Completed);
        DetectorInput input = new(@"D:\a.mp3", "mp3", MediaKind.Audio, ScanDepth.Standard, RootRole.Library, null, new Dictionary<Type, IArtifact> { [typeof(AudioDecodeArtifact)] = decode, [typeof(ProbeArtifact)] = probe }, new RuleSettings());
        FindingSink sink = new();
        new AudioQualityDetector().Evaluate(input, sink);
        Finding overs = Assert.Single(sink.Findings, f => f.RuleId == "AUD-OVERS");
        Assert.Equal("decodes to +0.02 dBFS; will clip on playback without headroom", overs.Summary);
    }

    private static byte[] Record(byte[] payload, string tag)
    {
        byte[] name = new byte[8];
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(name, 0);
        return new ByteBuilder().Bytes(payload).U32LE((uint)payload.Length).Bytes(name).ToArray();
    }

    private static StructureArtifact Structure(params PaddingRegion[] padding) =>
        new(new SniffResult(ContainerFormat.IsoBmff, "isom", 0, MediaKind.Video), 0, null, [], padding);

    private static List<Finding> EvaluateZeros(long size, long zeroBlocks, long totalBlocks, long longestBlocks, long longestOffset, long[] runs, StructureArtifact? structure, string extension)
    {
        IntegrityArtifact integrity = new(IntegrityOutcome.Ok, size, zeroBlocks, totalBlocks, longestBlocks, longestOffset, runs, null, null);
        Dictionary<Type, IArtifact> artifacts = new() { [typeof(IntegrityArtifact)] = integrity };
        if (structure is not null)
        {
            artifacts[typeof(StructureArtifact)] = structure;
        }

        DetectorInput input = new(@"D:\m\f." + extension, extension, MediaKind.Video, ScanDepth.Quick, RootRole.Library, null, artifacts, new RuleSettings());
        FindingSink sink = new();
        new IntegrityDetector().Evaluate(input, sink);
        return [.. sink.Findings];
    }

    private static List<Finding> EvaluateImage(SignalCounts counts, int frames)
    {
        ImageDecodeArtifact image = new(RunOutcome.Completed, new LogDigest(counts, [], "[fatal] No JPEG data found in image"), frames, 3264, 2448, "mjpeg", "jpeg_pipe", Opened: true);
        DetectorInput input = new(@"D:\p\IMG.jpg", "jpg", MediaKind.Photo, ScanDepth.Standard, RootRole.Library, null, new Dictionary<Type, IArtifact> { [typeof(ImageDecodeArtifact)] = image }, new RuleSettings());
        FindingSink sink = new();
        new ImageDecodeDetector().Evaluate(input, sink);
        return [.. sink.Findings];
    }

    private static List<Finding> EvaluateVideoSamples(FrameSamplesArtifact samples)
    {
        StreamInfo video = new(0, "video", "h264", null, 1920, 1080, "yuv420p", "progressive", 30, null, null, null, null, null, 10, 20_000_000, null, null, null, false, true);
        ProbeArtifact probe = new(true, false, "matroska", 100, 10, 20_000_000, 25_000_000, [video], LogDigest.Empty, RunOutcome.Completed);
        DetectorInput input = new(@"D:\v.mkv", "mkv", MediaKind.Video, ScanDepth.Standard, RootRole.Library, null, new Dictionary<Type, IArtifact> { [typeof(ProbeArtifact)] = probe, [typeof(FrameSamplesArtifact)] = samples }, new RuleSettings());
        FindingSink sink = new();
        new VideoQualityDetector().Evaluate(input, sink);
        return [.. sink.Findings.Where(f => f.RuleId is "VID-UPSCALED" or "VID-INFLATED")];
    }

    private static List<Finding> EvaluateSoundtrack(double peak)
    {
        StreamInfo video = new(0, "video", "h264", null, 1920, 1080, "yuv420p", "progressive", 30, null, null, null, null, null, 10, 8_000_000, null, null, null, false, true);
        ProbeArtifact probe = new(true, false, "mov,mp4", 100, 10, 8_000_000, 10_000_000, [video], LogDigest.Empty, RunOutcome.Completed);
        VideoDecodeArtifact decode = new(VideoPass.Standard, RunOutcome.Completed, false, LogDigest.Empty, LogDigest.Empty, LogDigest.Empty, 10, 10, true, null, null, null, 0, 0, 0, 0, true, AudioMaxVolumeDb: peak);
        DetectorInput input = new(@"D:\v.mp4", "mp4", MediaKind.Video, ScanDepth.Standard, RootRole.Library, null, new Dictionary<Type, IArtifact> { [typeof(ProbeArtifact)] = probe, [typeof(VideoDecodeArtifact)] = decode }, new RuleSettings());
        FindingSink sink = new();
        new VideoQualityDetector().Evaluate(input, sink);
        return [.. sink.Findings];
    }
}

/// <summary>The same fixes through the real engine and FFmpeg.</summary>
[Trait("Category", "Integration")]
public sealed class AnalysisFindingsFixtureTests(ITestOutputHelper output)
{
    [FixtureFact]
    public async Task A_photo_with_pictures_appended_is_judged_by_its_first_picture()
    {
        // IMG_20181001_202934.jpg: the photo, a second full picture, then a 17-byte stub that starts like a JPEG.
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string path = FixtureFactory.CopyTo("img.jpg", harness.Library, "pixel.jpg");
        byte[] photo = File.ReadAllBytes(path);
        byte[] stub = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00];
        File.WriteAllBytes(path, [.. photo, .. photo, .. stub]);
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Standard);

        FileRecord file = harness.AllFiles().Single();
        List<FindingRecord> findings = harness.Database.Findings.ForFile(file.Id);
        output.WriteLine(string.Join(Environment.NewLine, findings.Select(f => $"{f.RuleId} {f.Severity} {f.Summary}")));
        Assert.DoesNotContain(findings, f => f.RuleId is "COR-PARTIAL-IMAGE" or "COR-DECODE" or "COR-CONTAINER");
        ImageDecodeArtifact image = Assert.IsType<ImageDecodeArtifact>(new ImageDecodeProvider().Deserialize(harness.Database.Artifacts.ForFile(file.Id)[ArtifactKind.Decode].Data));
        Assert.Equal(1, image.FramesRead);
        Assert.False(image.Log.Counts.AnyDamage);
    }

    [FixtureFact]
    public async Task Photos_skipped_while_the_upscaling_check_was_off_are_checked_once_it_is_on()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        FixtureFactory.CopyTo("img2x.jpg", harness.Library);
        harness.Settings.CheckPhotosForUpscaling = false;
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Standard);
        FileRecord file = harness.AllFiles().Single();
        Assert.Equal("disabled", Samples(harness, file.Id).Skipped);

        harness.Settings.CheckPhotosForUpscaling = true;
        await harness.ScanAsync(ScanDepth.Deep);
        FrameSamplesArtifact samples = Samples(harness, file.Id);
        Assert.True(samples.Sampled, $"skipped: {samples.Skipped}");
        Assert.Contains(harness.Database.Findings.ForFile(file.Id), f => f.RuleId == "IMG-UPSCALED");
    }

    private static FrameSamplesArtifact Samples(EngineHarness harness, long fileId) =>
        Assert.IsType<FrameSamplesArtifact>(new FrameSampleProvider().Deserialize(harness.Database.Artifacts.ForFile(fileId)[ArtifactKind.FrameSamples].Data));
}
