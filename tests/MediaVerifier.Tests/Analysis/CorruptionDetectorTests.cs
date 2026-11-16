using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Tests.Analysis;

/// <summary>Every row of the subplan 07 rule tables over hand-built artifacts (tasks C-11, C-12, C-14).</summary>
public sealed class CorruptionDetectorTests
{
    // ---------------------------------------------------------------- builders

    private static StreamInfo Stream(string type, string codec, double? duration = null, bool attached = false, bool isDefault = true, long? frames = null) =>
        new(0, type, codec, null, type == "video" ? 1920 : null, type == "video" ? 1080 : null, null, null, type == "video" ? 24 : null, frames,
            type == "audio" ? 44100 : null, type == "audio" ? 2 : null, null, null, duration, null, null, null, null, attached, isDefault);

    private static ProbeArtifact Probe(params StreamInfo[] streams) =>
        new(true, false, "test", 100, streams.Max(s => s.Duration), null, null, streams, LogDigest.Empty, RunOutcome.Completed);

    private static ProbeArtifact NotOpened(RunOutcome outcome = RunOutcome.Completed, int unsupported = 0) =>
        new(false, false, null, 0, null, null, null, [], new LogDigest(new SignalCounts(OpenFailure: 1, Unsupported: unsupported), [], "[error] Invalid data found when processing input"), outcome);

    private static LogDigest Log(SignalCounts counts) => new(counts, [new MessageCount("error while decoding", Math.Max(1, counts.DecodeError))], "[error] first line");

    private static StructureArtifact Structure(ContainerFormat format, string? brand = null, long length = 1000, FlacInfo? flac = null, Mp3Info? mp3 = null, StructureIssue? issue = null) =>
        new(new SniffResult(format, brand, 0, MediaKind.Unknown), length, null, issue is null ? [] : [issue], [], flac, null, mp3);

    private static AudioDecodeArtifact Audio(double decodedSeconds, SignalCounts? counts = null, RunOutcome outcome = RunOutcome.Completed, Md5Verdict md5 = Md5Verdict.NotApplicable, bool opened = true) =>
        new(outcome, outcome == RunOutcome.TimedOut, Log(counts ?? SignalCounts.Zero), opened, (long)(decodedSeconds * 44100), opened ? 44100 : 0, opened ? 2 : 0, md5);

    private static VideoDecodeArtifact Video(double? declared, double? decoded, SignalCounts? video = null, SignalCounts? audio = null, bool hasAudio = true, VideoPass pass = VideoPass.Standard, RunOutcome outcome = RunOutcome.Completed, long? framesDecoded = null, long? framesDeclared = null, bool opened = true) =>
        new(pass, outcome, outcome == RunOutcome.TimedOut, Log(video ?? SignalCounts.Zero), new LogDigest(audio ?? SignalCounts.Zero, [], null), LogDigest.Empty,
            decoded, declared, hasAudio, framesDecoded, framesDeclared, null, 0, 0, 0, 0, opened);

    private static ImageDecodeArtifact Image(int? frames, SignalCounts? counts = null, RunOutcome outcome = RunOutcome.Completed, bool opened = true) =>
        new(outcome, Log(counts ?? SignalCounts.Zero), frames, 1920, 1080, "mjpeg", "jpeg_pipe", opened);

    private static List<Finding> Run(IDetector detector, MediaKind kind, string extension, params IArtifact[] artifacts)
    {
        Dictionary<Type, IArtifact> map = artifacts.ToDictionary(a => a.GetType(), a => a);
        DetectorInput input = new(@"C:\lib\file." + extension, extension, kind, ScanDepth.Deep, RootRole.Library, null, map, RuleSettings.Defaults);
        FindingSink sink = new();
        Assert.True(detector.Applies(input));
        detector.Evaluate(input, sink);
        return [.. sink.Findings];
    }

    private static Finding Only(List<Finding> findings, string ruleId)
    {
        Finding finding = Assert.Single(findings, f => f.RuleId == ruleId);
        Assert.Equal(1f, finding.Confidence);
        return finding;
    }

    private static readonly FlacInfo Flac20s = new(44100, 2, 16, 20 * 44100, "0123456789abcdef0123456789abcdef", 8);

    // ---------------------------------------------------------------- audio

    [Fact]
    public void Audio_cannot_open_is_container_damage()
    {
        List<Finding> findings = Run(new AudioDecodeDetector(), MediaKind.Audio, "flac", Audio(0, opened: false), Probe(Stream("audio", "flac", 20)), Structure(ContainerFormat.Flac, flac: Flac20s));
        Finding finding = Only(findings, "COR-CONTAINER");
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Single(findings);
    }

    [Fact]
    public void Audio_checksum_signal_and_md5_mismatch_are_high()
    {
        Finding fromSignal = Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "flac", Audio(20, new SignalCounts(Checksum: 2)), Structure(ContainerFormat.Flac, flac: Flac20s)), "COR-CHECKSUM");
        Assert.Equal(Severity.High, fromSignal.Severity);
        Assert.Equal("2 checksum errors", fromSignal.Summary);

        Finding fromMd5 = Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "flac", Audio(20, md5: Md5Verdict.Mismatch), Structure(ContainerFormat.Flac, flac: Flac20s)), "COR-CHECKSUM");
        Assert.Equal(Severity.High, fromMd5.Severity);
        Assert.Equal("The audio no longer matches the MD5 stored in the file", fromMd5.Summary);
    }

    [Fact]
    public void Audio_decode_errors_scale_with_source_count_and_loss()
    {
        // Lossless source: one error is High.
        Assert.Equal(Severity.High, Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "flac", Audio(20, new SignalCounts(DecodeError: 1)), Structure(ContainerFormat.Flac, flac: Flac20s)), "COR-DECODE").Severity);

        // Lossy, one error, nothing lost: Medium.
        ProbeArtifact mp3 = Probe(Stream("audio", "mp3", 100));
        Finding one = Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(100, new SignalCounts(DecodeError: 1)), mp3), "COR-DECODE");
        Assert.Equal(Severity.Medium, one.Severity);
        Assert.Equal("Decoder reported 1 error", one.Summary);

        // Lossy, five errors: High.
        Assert.Equal(Severity.High, Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(100, new SignalCounts(DecodeError: 5)), mp3), "COR-DECODE").Severity);

        // Lossy, one error but 0.6 s lost (inside the 1 s truncation tolerance of a 100 s file): High.
        List<Finding> lost = Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(99.4, new SignalCounts(DecodeError: 1)), mp3);
        Assert.Equal(Severity.High, Only(lost, "COR-DECODE").Severity);
        Assert.DoesNotContain(lost, f => f.RuleId == "COR-TRUNCATED");

        // Unknown error lines count at half weight; minor complaints alone are Low.
        Assert.Equal(Severity.Medium, Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(100, new SignalCounts(Unknown: 1)), mp3), "COR-DECODE").Severity);
        Finding minor = Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(100, new SignalCounts(MinorDecode: 3)), mp3), "COR-DECODE");
        Assert.Equal(Severity.Low, minor.Severity);
        Assert.Equal("Decoder reported 3 minor problems", minor.Summary);
    }

    [Fact]
    public void Audio_truncation_uses_declared_length_and_tolerance()
    {
        ProbeArtifact probe = Probe(Stream("audio", "mp3", 20));
        Mp3Info xing = new(0, 320_000, 765, 320_000, "Xing", 128_000, 44100, 2);
        StructureArtifact withHeader = Structure(ContainerFormat.Mp3, mp3: xing);

        Finding high = Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(11, null), probe, withHeader), "COR-TRUNCATED");
        Assert.Equal(Severity.High, high.Severity);
        Assert.Equal("Stops at 0:11 of 0:20", high.Summary);
        Assert.Equal(11.0, (double)high.Evidence["decodedSeconds"], 3);
        Assert.Equal(20.0, (double)high.Evidence["declaredSeconds"], 3);

        Assert.Equal(Severity.Critical, Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(9, null), probe, withHeader), "COR-TRUNCATED").Severity);
        Assert.Empty(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(19.6, null), probe, withHeader));

        // FLAC STREAMINFO wins over the probe's stream duration.
        Assert.Single(Run(new AudioDecodeDetector(), MediaKind.Audio, "flac", Audio(15, null), Probe(Stream("audio", "flac", 15)), Structure(ContainerFormat.Flac, flac: Flac20s)), f => f.RuleId == "COR-TRUNCATED");
    }

    [Fact]
    public void Audio_lost_to_reported_errors_is_not_truncation()
    {
        ProbeArtifact opus = Probe(Stream("audio", "opus", 20));

        // Two Ogg pages dropped for CRC errors: about 2 s short, reported with the checksum finding.
        List<Finding> pages = Run(new AudioDecodeDetector(), MediaKind.Audio, "opus", Audio(18, new SignalCounts(Checksum: 2)), opus);
        Assert.DoesNotContain(pages, f => f.RuleId == "COR-TRUNCATED");
        Assert.Equal("2 checksum errors; about 2.0 s missing", Only(pages, "COR-CHECKSUM").Summary);

        // One error cannot explain 8 s: the file is cut short as well.
        Assert.Single(Run(new AudioDecodeDetector(), MediaKind.Audio, "opus", Audio(12, new SignalCounts(DecodeError: 1)), opus), f => f.RuleId == "COR-TRUNCATED");
    }

    [Fact]
    public void Mp3_without_vbr_header_needs_corroboration_and_three_percent()
    {
        ProbeArtifact probe = Probe(Stream("audio", "mp3", 20));
        StructureArtifact cbr = Structure(ContainerFormat.Mp3, mp3: new Mp3Info(0, 320_000, null, null, null, 128_000, 44100, 2));
        Assert.Empty(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(18, null), probe, cbr));
        Finding corroborated = Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(18, new SignalCounts(Truncation: 1)), probe, cbr), "COR-TRUNCATED");
        Assert.True((bool)corroborated.Evidence["declaredIsEstimate"]);
        Assert.DoesNotContain(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(19.6, new SignalCounts(Truncation: 1)), probe, cbr), f => f.RuleId == "COR-TRUNCATED");
    }

    [Fact]
    public void Audio_longer_than_declared_is_a_wrong_length_header()
    {
        Finding finding = Only(Run(new AudioDecodeDetector(), MediaKind.Audio, "m4a", Audio(25, null), Probe(Stream("audio", "aac", 20))), "COR-INDEX");
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal("Header says 0:20, but 0:25 decodes", finding.Summary);
    }

    [Fact]
    public void Audio_timeout_is_a_hang_and_suppresses_length_rules()
    {
        List<Finding> findings = Run(new AudioDecodeDetector(), MediaKind.Audio, "flac", Audio(5, null, RunOutcome.TimedOut), Structure(ContainerFormat.Flac, flac: Flac20s));
        Finding hang = Only(findings, "COR-HANG");
        Assert.Equal(Severity.High, hang.Severity);
        Assert.Equal("Decoding stopped making progress after 0:05", hang.Summary);
        Assert.DoesNotContain(findings, f => f.RuleId == "COR-TRUNCATED");
    }

    [Fact]
    public void Audio_not_judged_when_unsupported_cancelled_or_unrecognised()
    {
        Assert.Empty(Run(new AudioDecodeDetector(), MediaKind.Audio, "wma", Audio(0, opened: false), NotOpened(unsupported: 1)));
        Assert.Empty(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(0, null, RunOutcome.Cancelled, opened: false), Probe(Stream("audio", "mp3", 20))));
        Assert.Empty(Run(new AudioDecodeDetector(), MediaKind.Audio, "mp3", Audio(0, opened: false), NotOpened(), Structure(ContainerFormat.Unknown)));
    }

    // ---------------------------------------------------------------- photos

    [Fact]
    public void Image_rules()
    {
        Assert.Equal(Severity.Critical, Only(Run(new ImageDecodeDetector(), MediaKind.Photo, "jpg", Image(null, opened: false)), "COR-CONTAINER").Severity);
        Assert.Equal(Severity.Critical, Only(Run(new ImageDecodeDetector(), MediaKind.Photo, "jpg", Image(0, new SignalCounts(DecodeError: 2))), "COR-DECODE").Severity);

        Finding truncated = Only(Run(new ImageDecodeDetector(), MediaKind.Photo, "jpg", Image(1, new SignalCounts(Truncation: 1))), "COR-PARTIAL-IMAGE");
        Assert.Equal(Severity.High, truncated.Severity);
        Assert.Equal("Image data ends before the picture is complete", truncated.Summary);

        Finding errors = Only(Run(new ImageDecodeDetector(), MediaKind.Photo, "jpg", Image(1, new SignalCounts(DecodeError: 3))), "COR-PARTIAL-IMAGE");
        Assert.Equal("Decoder reported 3 errors in the picture", errors.Summary);

        Assert.Equal(Severity.High, Only(Run(new ImageDecodeDetector(), MediaKind.Photo, "png", Image(0, new SignalCounts(Checksum: 1))), "COR-CHECKSUM").Severity);
        Assert.Equal(Severity.High, Only(Run(new ImageDecodeDetector(), MediaKind.Photo, "png", Image(null, null, RunOutcome.TimedOut, opened: false)), "COR-HANG").Severity);
        Assert.Empty(Run(new ImageDecodeDetector(), MediaKind.Photo, "jpg", Image(1)));
    }

    // ---------------------------------------------------------------- video

    [Fact]
    public void Video_container_and_decode_errors()
    {
        Assert.Equal(Severity.Critical, Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(6, null, opened: false)), "COR-CONTAINER").Severity);

        Finding medium = Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 60, new SignalCounts(DecodeError: 3))), "COR-DECODE");
        Assert.Equal(Severity.Medium, medium.Severity);
        Assert.Equal("Decoder reported 3 errors", medium.Summary);
        Assert.Equal(3, medium.Evidence["videoDecodeErrors"]);

        Finding split = Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 60, new SignalCounts(DecodeError: 3), new SignalCounts(DecodeError: 2))), "COR-DECODE");
        Assert.Equal(Severity.High, split.Severity);
        Assert.Equal("Decoder reported 5 errors (video 3, audio 2)", split.Summary);
        Assert.Equal(2, split.Evidence["audioDecodeErrors"]);

        // 2 s of decoded time lost makes one error High (the shortfall stays inside the truncation tolerance of 2 %).
        Assert.Equal(Severity.High, Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(200, 198, new SignalCounts(DecodeError: 1))), "COR-DECODE").Severity);
        Assert.Equal(Severity.Low, Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 60, new SignalCounts(MinorDecode: 1))), "COR-DECODE").Severity);
    }

    [Fact]
    public void Video_concealment_and_timestamps()
    {
        Assert.Equal(Severity.Medium, Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 60, new SignalCounts(Concealed: 3))), "COR-CONCEALED").Severity);
        Finding high = Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 60, new SignalCounts(Concealed: 10))), "COR-CONCEALED");
        Assert.Equal(Severity.High, high.Severity);
        Assert.Equal("10 damaged frames patched over by the decoder", high.Summary);

        Assert.Empty(Run(new VideoDecodeDetector(), MediaKind.Video, "mkv", Video(60, 60, new SignalCounts(Timestamp: 9))));
        Assert.Equal(Severity.Low, Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mkv", Video(60, 60, new SignalCounts(Timestamp: 10))), "COR-TIMESTAMPS").Severity);
        Assert.Equal(Severity.Medium, Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mkv", Video(60, 60, new SignalCounts(Timestamp: 100))), "COR-TIMESTAMPS").Severity);
    }

    [Fact]
    public void Video_truncation_tolerance_depends_on_audio_and_pass()
    {
        Finding withAudio = Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 57)), "COR-TRUNCATED");
        Assert.Equal(Severity.High, withAudio.Severity);
        Assert.Equal("Stops at 0:57 of 1:00", withAudio.Summary);

        // Key frames only and no sound: the last key frame may be 12 s before the end.
        Assert.Empty(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 50, hasAudio: false)));
        Assert.Single(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 40, hasAudio: false)), f => f.RuleId == "COR-TRUNCATED");
        Assert.Single(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 57, hasAudio: false, pass: VideoPass.Deep)), f => f.RuleId == "COR-TRUNCATED");

        Assert.Equal(Severity.Critical, Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 20)), "COR-TRUNCATED").Severity);
        Assert.Equal(Severity.High, Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 60, new SignalCounts(Truncation: 1))), "COR-TRUNCATED").Severity);
    }

    [Fact]
    public void Deep_frame_shortfall_counts_as_errors()
    {
        Finding finding = Only(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 60, pass: VideoPass.Deep, framesDecoded: 1430, framesDeclared: 1440)), "COR-DECODE");
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal("Decoder reported 10 errors; 10 frames could not be decoded", finding.Summary);
        Assert.Equal(10L, finding.Evidence["missingFrames"]);

        // Within max(2 frames, 0.1 %).
        Assert.Empty(Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 60, pass: VideoPass.Deep, framesDecoded: 1438, framesDeclared: 1440)));
    }

    [Fact]
    public void Video_timeout_is_a_hang_without_length_rules()
    {
        List<Finding> findings = Run(new VideoDecodeDetector(), MediaKind.Video, "mp4", Video(60, 10, outcome: RunOutcome.TimedOut));
        Assert.Equal(Severity.High, Only(findings, "COR-HANG").Severity);
        Assert.DoesNotContain(findings, f => f.RuleId == "COR-TRUNCATED");
    }

    [Fact]
    public void Stream_length_difference()
    {
        Assert.Empty(Run(new StreamLengthDetector(), MediaKind.Video, "mp4", Probe(Stream("video", "h264", 60), Stream("audio", "aac", 61))));
        Finding low = Only(Run(new StreamLengthDetector(), MediaKind.Video, "mp4", Probe(Stream("video", "h264", 60), Stream("audio", "aac", 55))), "COR-STREAM-LENGTH");
        Assert.Equal(Severity.Low, low.Severity);
        Assert.Equal("Video 1:00, audio 0:55", low.Summary);
        Assert.Equal(Severity.Medium, Only(Run(new StreamLengthDetector(), MediaKind.Video, "mp4", Probe(Stream("video", "h264", 60), Stream("audio", "aac", 40))), "COR-STREAM-LENGTH").Severity);

        // Cover art is not a video stream.
        Assert.Empty(Run(new StreamLengthDetector(), MediaKind.Video, "mp4", Probe(Stream("video", "mjpeg", 1, attached: true), Stream("audio", "aac", 40))));
    }

    // ---------------------------------------------------------------- structure

    [Fact]
    public void Structure_issues_map_to_rules()
    {
        ProbeArtifact probe = Probe(Stream("video", "h264", 6), Stream("audio", "aac", 6));
        Finding moov = Only(Run(new StructureDetector(), MediaKind.Video, "mp4", probe, Structure(ContainerFormat.IsoBmff, "isom", issue: new StructureIssue(StructureIssueKind.ContainerBroken, "moov box not found", 0, null, "moov"))), "COR-CONTAINER");
        Assert.Equal("Index (moov) missing; typical of an interrupted recording or download", moov.Summary);
        Assert.Equal(Severity.Critical, moov.Severity);

        Finding high = Only(Run(new StructureDetector(), MediaKind.Video, "mp4", probe, Structure(ContainerFormat.IsoBmff, "isom", 1000, issue: new StructureIssue(StructureIssueKind.Truncated, "mdat past end", 40, 100, "mdat"))), "COR-TRUNCATED");
        Assert.Equal(Severity.High, high.Severity);
        Assert.Equal("100 bytes missing at the end", high.Summary);
        Assert.Equal(100L, high.Evidence["missingBytes"]);

        Assert.Equal(Severity.Critical, Only(Run(new StructureDetector(), MediaKind.Video, "mp4", probe, Structure(ContainerFormat.IsoBmff, "isom", 1000, issue: new StructureIssue(StructureIssueKind.Truncated, "mdat past end", 40, 4096, "mdat"))), "COR-TRUNCATED").Severity);
        Assert.Equal(Severity.High, Only(Run(new StructureDetector(), MediaKind.Photo, "jpg", Probe(Stream("video", "mjpeg")), Structure(ContainerFormat.Jpeg, issue: new StructureIssue(StructureIssueKind.Truncated, "no EOI"))), "COR-TRUNCATED").Severity);

        Finding trailing = Only(Run(new StructureDetector(), MediaKind.Photo, "jpg", Probe(Stream("video", "mjpeg")), Structure(ContainerFormat.Jpeg, length: 10_000, issue: new StructureIssue(StructureIssueKind.TrailingData, "junk after EOI", 7952))), "COR-TRAILING");
        Assert.Equal(Severity.Low, trailing.Severity);
        Assert.Equal("2 KB of unexpected data after the end", trailing.Summary);

        Assert.Equal(Severity.Low, Only(Run(new StructureDetector(), MediaKind.Audio, "wav", Probe(Stream("audio", "pcm_s16le", 5)), Structure(ContainerFormat.Wav, "WAVE", issue: new StructureIssue(StructureIssueKind.IndexNotFinalised, "RIFF size 0", 4, null, "RIFF"))), "COR-INDEX").Severity);
        Finding cues = Only(Run(new StructureDetector(), MediaKind.Video, "mkv", probe, Structure(ContainerFormat.Matroska, issue: new StructureIssue(StructureIssueKind.IndexMissingOrWrong, "no Cues", null, null, "Cues"))), "COR-INDEX");
        Assert.Equal(Severity.Medium, cues.Severity);
        Assert.Equal("No seek index (Cues)", cues.Summary);
    }

    [Fact]
    public void Unrecognised_and_extension_mismatch()
    {
        List<Finding> text = Run(new StructureDetector(), MediaKind.Audio, "mp3", NotOpened(), Structure(ContainerFormat.Unknown));
        Finding unrecognised = Assert.Single(text);
        Assert.Equal("GEN-UNRECOGNIZED", unrecognised.RuleId);
        Assert.Equal(Severity.Critical, unrecognised.Severity);

        Finding medium = Only(Run(new StructureDetector(), MediaKind.Audio, "mp3", Probe(Stream("audio", "flac", 20)), Structure(ContainerFormat.Flac, flac: Flac20s)), "GEN-EXT-MISMATCH");
        Assert.Equal(Severity.Medium, medium.Severity);
        Assert.Equal("FLAC content named .mp3", medium.Summary);
        Assert.Equal("flac", medium.Evidence["expectedExtension"]);

        Finding low = Only(Run(new StructureDetector(), MediaKind.Photo, "jpg", Probe(Stream("video", "png")), Structure(ContainerFormat.Png)), "GEN-EXT-MISMATCH");
        Assert.Equal(Severity.Low, low.Severity);

        Assert.Empty(Run(new StructureDetector(), MediaKind.Audio, "flac", Probe(Stream("audio", "flac", 20)), Structure(ContainerFormat.Flac, flac: Flac20s)));
    }

    [Fact]
    public void Probe_based_container_damage()
    {
        Finding unopenable = Only(Run(new StructureDetector(), MediaKind.Audio, "flac", NotOpened(), Structure(ContainerFormat.Flac)), "COR-CONTAINER");
        Assert.Equal("FFmpeg cannot open the file", unopenable.Summary);
        Assert.Equal("No audio stream", Only(Run(new StructureDetector(), MediaKind.Audio, "m4a", Probe(Stream("video", "h264", 5)), Structure(ContainerFormat.IsoBmff, "M4A ")), "COR-CONTAINER").Summary);

        // A probe that timed out, an unsupported codec, an unreadable file: nothing from the probe.
        Assert.Empty(Run(new StructureDetector(), MediaKind.Audio, "flac", NotOpened(RunOutcome.TimedOut), Structure(ContainerFormat.Flac)));
        Assert.Empty(Run(new StructureDetector(), MediaKind.Audio, "wma", NotOpened(unsupported: 1), Structure(ContainerFormat.Asf)));
        Assert.Empty(Run(new StructureDetector(), MediaKind.Audio, "flac", NotOpened(), Structure(ContainerFormat.Unknown) with { ReadFailed = true }));
    }

    // ---------------------------------------------------------------- merge

    [Fact]
    public void Merge_keeps_one_finding_per_rule_drops_other_kinds_and_attaches_zero_runs()
    {
        Finding structural = new("COR-TRUNCATED", Severity.High, 1f, "100 bytes missing at the end", Finding.NoEvidence);
        Finding decoded = new("COR-TRUNCATED", Severity.Critical, 1f, "Stops at 0:09 of 0:20", Finding.NoEvidence);
        Finding index = new("COR-INDEX", Severity.Low, 1f, "Length fields were never filled in", Finding.NoEvidence);
        Finding zeros = new("GEN-ZEROFILL", Severity.Low, 1f, "4 KiB of zeros", new Dictionary<string, object> { ["runOffsetsBytes"] = new long[] { 8192 }, ["longestRunBytes"] = 4096L });
        Finding decode = new("COR-DECODE", Severity.High, 1f, "Decoder reported 3 errors", Finding.NoEvidence);

        IReadOnlyList<Finding> audio = Evaluator.Merge([structural, decoded, index, zeros, decode], MediaKind.Audio);
        Assert.Equal(new[] { "COR-TRUNCATED", "COR-INDEX", "GEN-ZEROFILL", "COR-DECODE" }, audio.Select(f => f.RuleId));
        Assert.Equal(Severity.Critical, audio[0].Severity);
        Assert.Equal(new long[] { 8192 }, audio.Single(f => f.RuleId == "COR-DECODE").Evidence["zeroRunOffsetsBytes"]);
        Assert.False(audio.Single(f => f.RuleId == "COR-INDEX").Evidence.ContainsKey("zeroRunOffsetsBytes"));

        // On a tie the later (deeper) finding wins and keeps the earlier one's evidence underneath.
        Finding quick = new("COR-TRUNCATED", Severity.High, 1f, "100 bytes missing at the end", new Dictionary<string, object> { ["missingBytes"] = 100L });
        Finding standard = new("COR-TRUNCATED", Severity.High, 1f, "Stops at 0:11 of 0:20", new Dictionary<string, object> { ["decodedSeconds"] = 11.0 });
        Finding tie = Assert.Single(Evaluator.Merge([quick, standard], MediaKind.Audio));
        Assert.Equal("Stops at 0:11 of 0:20", tie.Summary);
        Assert.Equal(100L, tie.Evidence["missingBytes"]);
        Assert.Equal(11.0, tie.Evidence["decodedSeconds"]);

        // COR-INDEX applies to audio and video only.
        Assert.DoesNotContain(Evaluator.Merge([index], MediaKind.Photo), f => f.RuleId == "COR-INDEX");
    }

    [Fact]
    public void Every_threshold_the_detectors_read_is_registered()
    {
        foreach ((string rule, string name) in new[]
        {
            ("COR-TRUNCATED", "criticalPercent"), ("COR-TRUNCATED", "audioSeconds"), ("COR-TRUNCATED", "audioPercent"),
            ("COR-TRUNCATED", "mp3EstimatePercent"), ("COR-TRUNCATED", "videoSeconds"), ("COR-TRUNCATED", "videoPercent"),
            ("COR-TRUNCATED", "keyFrameSeconds"), ("COR-TRUNCATED", "secondsPerError"), ("COR-DECODE", "highErrors"), ("COR-DECODE", "audioHighSeconds"),
            ("COR-DECODE", "videoHighSeconds"), ("COR-DECODE", "frameShortfall"), ("COR-DECODE", "frameShortfallPercent"),
            ("COR-CONCEALED", "highFrames"), ("COR-TIMESTAMPS", "minCount"), ("COR-TIMESTAMPS", "mediumCount"),
            ("COR-STREAM-LENGTH", "seconds"), ("COR-STREAM-LENGTH", "percent"), ("COR-STREAM-LENGTH", "mediumPercent"),
        })
        {
            Assert.NotNull(RuleDefaults.Get(rule, name));
        }
    }
}
