using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using static MediaVerifier.Core.Analysis.Detectors.EvidenceText;

namespace MediaVerifier.Core.Analysis.Detectors;

/// <summary>Facts several corruption detectors share.</summary>
internal static class Damage
{
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "alac", "wavpack", "ape", "tta", "mlp", "truehd", "shorten", "tak", "wmalossless", "mp4als",
    };

    /// <summary>GEN-UNRECOGNIZED covers it: no known signature and FFmpeg cannot open it.</summary>
    public static bool Unrecognised(StructureArtifact? structure, ProbeArtifact? probe) =>
        structure is { ReadFailed: false } && structure.Sniff.Format == ContainerFormat.Unknown && probe is { Opened: false, Unsupported: false, Outcome: RunOutcome.Completed };

    /// <summary>Nothing about the file can be concluded: the build lacks the decoder, or the run itself did not happen.</summary>
    public static bool NotJudged(ProbeArtifact? probe, RunOutcome outcome) =>
        probe?.Unsupported == true || outcome is RunOutcome.Cancelled or RunOutcome.FailedToStart;

    public static bool IsLossless(string? codec) =>
        codec is not null && (LosslessCodecs.Contains(codec) || codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) || codec.StartsWith("dsd_", StringComparison.OrdinalIgnoreCase));

    /// <summary>The kind the probe found: a real video stream (cover art excluded), else audio, else unknown.</summary>
    public static MediaKind ProbeKind(ProbeArtifact? probe) =>
        probe is not { Opened: true } ? MediaKind.Unknown
        : probe.Video is not null ? MediaKind.Video
        : probe.Audio is not null ? MediaKind.Audio
        : MediaKind.Unknown;

    /// <summary>COR-HANG (task C-12): any decode that timed out.</summary>
    public static void RaiseHang(FindingSink sink, bool idle, double? decodedSeconds)
    {
        string where = decodedSeconds is double s && s > 0 ? " after " + MediaTime(s) : string.Empty;
        string summary = idle
            ? $"Decoding stopped making progress{where}"
            : $"Decoding did not finish in the time allowed{where}";
        Dictionary<string, object> evidence = new() { ["timeout"] = idle ? "idle" : "hard" };
        if (decodedSeconds is double decoded)
        {
            evidence["decodedSeconds"] = Round(decoded);
        }

        sink.Raise("COR-HANG", Severity.High, 1f, summary, evidence);
    }

    /// <summary>COR-TRUNCATED from a measured shortfall: Critical when the missing share reaches the limit.</summary>
    public static void RaiseShortfall(FindingSink sink, DetectorInput input, double declared, double decoded, Dictionary<string, object> evidence, float confidence = 1f)
    {
        double missingShare = (declared - decoded) / declared;
        Severity severity = missingShare * 100 >= input.Settings.Value("COR-TRUNCATED", "criticalPercent") ? Severity.Critical : Severity.High;
        evidence["missingSeconds"] = Round(declared - decoded);
        sink.Raise("COR-TRUNCATED", severity, confidence, $"Stops at {MediaTime(decoded)} of {MediaTime(declared)}", evidence);
    }
}

/// <summary>
/// The Quick-depth findings from the native structure check and the probe (tasks C-02 to C-06, C-13):
/// broken containers, truncation, trailing data, bad indexes, extension mismatch, unrecognised files.
/// </summary>
public sealed class StructureDetector : IDetector
{
    public IReadOnlyList<string> RuleIds { get; } = ["COR-CONTAINER", "COR-TRUNCATED", "COR-TRAILING", "COR-INDEX", "GEN-EXT-MISMATCH", "GEN-UNRECOGNIZED"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(StructureArtifact)];

    public IReadOnlyList<Type> Optional { get; } = [typeof(ProbeArtifact), typeof(IntegrityArtifact)];

    public ScanDepth MinDepth => ScanDepth.Quick;

    public bool Applies(DetectorInput input) => true;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        StructureArtifact structure = input.Get<StructureArtifact>()!;
        IntegrityArtifact? integrity = input.Get<IntegrityArtifact>();
        if (structure.ReadFailed || structure.FileLength == 0 || integrity is { Outcome: IntegrityOutcome.Unreadable } or { Size: 0 })
        {
            // GEN-UNREADABLE and GEN-EMPTY say all there is to say.
            return;
        }

        ProbeArtifact? probe = input.Get<ProbeArtifact>();
        // A probe that timed out or never ran says nothing about the file (the decode reports a hang).
        bool probeUsable = probe is { Unsupported: false, Outcome: RunOutcome.Completed };
        if (probeUsable)
        {
            ExtensionVerdict verdict = ExtensionFamilies.Check(input.Extension, structure.Sniff.Format, structure.Sniff.Brand, probe!.Opened, Damage.ProbeKind(probe));
            switch (verdict)
            {
                case ExtensionVerdict.Unrecognized:
                    sink.Raise("GEN-UNRECOGNIZED", Severity.Critical, 1f, "No known file signature, and FFmpeg cannot open it", new Dictionary<string, object>
                    {
                        ["extension"] = input.Extension,
                        ["sizeBytes"] = structure.FileLength,
                    });
                    return;
                case ExtensionVerdict.MismatchLow:
                case ExtensionVerdict.MismatchMedium:
                    RaiseMismatch(input, sink, structure, probe, verdict == ExtensionVerdict.MismatchMedium ? Severity.Medium : Severity.Low);
                    break;
            }

            if (structure.Sniff.Format != ContainerFormat.Unknown && !MediaClassifier.IsRaw(input.Extension))
            {
                RaiseUnopenable(input, sink, probe);
            }
        }

        foreach (IGrouping<StructureIssueKind, StructureIssue> group in structure.Issues.GroupBy(i => i.Kind))
        {
            StructureIssue first = group.First();
            Dictionary<string, object> evidence = IssueEvidence(first, group.Count(), structure);
            switch (group.Key)
            {
                case StructureIssueKind.ContainerBroken:
                    sink.Raise("COR-CONTAINER", Severity.Critical, 1f, ContainerPhrase(first), evidence);
                    break;
                case StructureIssueKind.Truncated:
                    RaiseTruncated(input, sink, structure, group, evidence);
                    break;
                case StructureIssueKind.TrailingData:
                    long trailing = first.Offset is long offset ? Math.Max(0, structure.FileLength - offset) : 0;
                    sink.Raise("COR-TRAILING", Severity.Low, 1f, trailing > 0 ? $"{Size(trailing)} of unexpected data after the end" : "Unexpected data after the end", evidence);
                    break;
                case StructureIssueKind.IndexNotFinalised:
                    sink.Raise("COR-INDEX", Severity.Low, 1f, "Length fields were never filled in; the writer did not finish", evidence);
                    break;
                case StructureIssueKind.IndexMissingOrWrong:
                    sink.Raise("COR-INDEX", Severity.Medium, 1f, IndexPhrase(structure, first), evidence);
                    break;
            }
        }
    }

    private static void RaiseMismatch(DetectorInput input, FindingSink sink, StructureArtifact structure, ProbeArtifact probe, Severity severity)
    {
        string content = FormatNames.Display(structure.Sniff.Format, structure.Sniff.Brand);
        string? canonical = ExtensionFamilies.CanonicalExtension(structure.Sniff.Format, structure.Sniff.Brand, Damage.ProbeKind(probe));
        Dictionary<string, object> evidence = new()
        {
            ["extension"] = input.Extension,
            ["detectedFormat"] = content,
        };
        if (canonical is not null)
        {
            evidence["expectedExtension"] = canonical;
        }

        sink.Raise("GEN-EXT-MISMATCH", severity, 1f, $"{content} content named .{input.Extension}", evidence);
    }

    /// <summary>COR-CONTAINER from the probe: a known signature that FFmpeg cannot open, or no stream of the file's kind.</summary>
    private static void RaiseUnopenable(DetectorInput input, FindingSink sink, ProbeArtifact probe)
    {
        string? summary = null;
        if (!probe.Opened)
        {
            summary = "FFmpeg cannot open the file";
        }
        else if (input.Kind == MediaKind.Audio && probe.Audio is null)
        {
            summary = "No audio stream";
        }
        else if (input.Kind == MediaKind.Video && probe.Video is null && probe.Audio is null)
        {
            summary = "No video or audio stream";
        }
        else if (input.Kind == MediaKind.Photo && probe.Streams.All(s => s.Type != "video"))
        {
            summary = "No image stream";
        }

        if (summary is null)
        {
            return;
        }

        Dictionary<string, object> evidence = new() { ["streams"] = probe.Streams.Length };
        EvidenceText.AddCounts(evidence, probe.Log.Counts, string.Empty);
        if (probe.Log.FirstLine is not null)
        {
            evidence["firstLine"] = probe.Log.FirstLine;
        }

        sink.Raise("COR-CONTAINER", Severity.Critical, 1f, summary, evidence);
    }

    private static void RaiseTruncated(DetectorInput input, FindingSink sink, StructureArtifact structure, IEnumerable<StructureIssue> issues, Dictionary<string, object> evidence)
    {
        long missing = issues.Max(i => i.MissingBytes ?? 0);
        if (missing <= 0)
        {
            sink.Raise("COR-TRUNCATED", Severity.High, 1f, "The end of the file is missing", evidence);
            return;
        }

        // Missing half or more: what exists is at most as long as what is gone.
        double share = (double)missing / (structure.FileLength + missing) * 100;
        Severity severity = share >= input.Settings.Value("COR-TRUNCATED", "criticalPercent") ? Severity.Critical : Severity.High;
        evidence["missingBytes"] = missing;
        evidence["missingPercent"] = Math.Round(share, 1);
        sink.Raise("COR-TRUNCATED", severity, 1f, $"{Size(missing)} missing at the end", evidence);
    }

    private static Dictionary<string, object> IssueEvidence(StructureIssue issue, int count, StructureArtifact structure)
    {
        Dictionary<string, object> evidence = new()
        {
            ["detail"] = issue.Detail,
            ["format"] = structure.Sniff.Format.ToString(),
            ["sizeBytes"] = structure.FileLength,
        };
        if (issue.Offset is long offset)
        {
            evidence["offsetBytes"] = offset;
        }

        if (issue.Element is not null)
        {
            evidence["element"] = issue.Element;
        }

        if (structure.DeclaredEnd is long end)
        {
            evidence["declaredEndBytes"] = end;
        }

        if (count > 1)
        {
            evidence["count"] = count;
        }

        return evidence;
    }

    private static string ContainerPhrase(StructureIssue issue) => issue.Element switch
    {
        "moov" => "Index (moov) missing; typical of an interrupted recording or download",
        "moof" => "Bare media segment without its index; it cannot play on its own",
        "meta" => "Image description (meta) missing",
        null => "Container header is damaged",
        string element => $"Container header is damaged ({element})",
    };

    private static string IndexPhrase(StructureArtifact structure, StructureIssue issue) => structure.Sniff.Format switch
    {
        ContainerFormat.Matroska => "No seek index (Cues)",
        ContainerFormat.Mp3 when structure.Mp3 is { DeclaredFrames: long } => "Length header does not match the audio",
        _ => issue.Element is null ? "Seek index missing or wrong" : $"Seek index missing or wrong ({issue.Element})",
    };
}

/// <summary>Audio decode rules (subplan 07, Audio — Standard and Deep; tasks C-07, C-11, C-12).</summary>
public sealed class AudioDecodeDetector : IDetector
{
    public IReadOnlyList<string> RuleIds { get; } = ["COR-CONTAINER", "COR-CHECKSUM", "COR-DECODE", "COR-TRUNCATED", "COR-INDEX", "COR-HANG"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(AudioDecodeArtifact)];

    public IReadOnlyList<Type> Optional { get; } = [typeof(ProbeArtifact), typeof(StructureArtifact)];

    public ScanDepth MinDepth => ScanDepth.Standard;

    public bool Applies(DetectorInput input) => input.Kind == MediaKind.Audio;

    /// <summary>
    /// The declared length: FLAC STREAMINFO, else the stream duration, else the format duration. An MP3
    /// without a VBR header only has an estimate from bitrate and size.
    /// </summary>
    public static double? Declared(StructureArtifact? structure, ProbeArtifact? probe, out bool estimate)
    {
        estimate = false;
        if (structure?.Flac is { TotalSamples: > 0, SampleRate: > 0 } flac)
        {
            return (double)flac.TotalSamples / flac.SampleRate;
        }

        double? value = probe?.Audio?.Duration is double stream && stream > 0 ? stream
            : probe?.Duration is double format && format > 0 ? format
            : null;
        estimate = value is not null && structure?.Sniff.Format == ContainerFormat.Mp3 && structure.Mp3?.VbrHeader is null;
        return value;
    }

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        AudioDecodeArtifact decode = input.Get<AudioDecodeArtifact>()!;
        ProbeArtifact? probe = input.Get<ProbeArtifact>();
        StructureArtifact? structure = input.Get<StructureArtifact>();
        if (Damage.NotJudged(probe, decode.Outcome) || Damage.Unrecognised(structure, probe))
        {
            return;
        }

        bool timedOut = decode.Outcome == RunOutcome.TimedOut;
        if (timedOut)
        {
            Damage.RaiseHang(sink, decode.IdleTimeout, decode.DecodedSeconds);
        }

        if (!decode.Opened)
        {
            if (!timedOut)
            {
                Dictionary<string, object> evidence = Decoder(decode.Log, null, null);
                sink.Raise("COR-CONTAINER", Severity.Critical, 1f, probe?.Audio is null ? "No audio stream" : "The audio cannot be decoded", evidence);
            }

            return;
        }

        SignalCounts counts = decode.Log.Counts;
        double? declared = Declared(structure, probe, out bool estimate);
        double? decoded = decode.DecodedSeconds;
        Dictionary<string, object> shared = Decoder(decode.Log, declared, decoded);
        shared["sampleRateHz"] = decode.SampleRate;
        shared["channels"] = decode.Channels;
        if (estimate)
        {
            shared["declaredIsEstimate"] = true;
        }

        double lost = !timedOut && declared is double d && decoded is double x ? Math.Max(0, d - x) : 0;
        string missing = lost >= 0.05 ? $"; about {Span(lost)} missing" : string.Empty;
        double weighted = counts.WeightedDecodeErrors;
        if (counts.Checksum > 0 || decode.Md5 == Md5Verdict.Mismatch)
        {
            Dictionary<string, object> evidence = new(shared) { ["md5"] = decode.Md5.ToString() };
            string summary = counts.Checksum == 0
                ? "The audio no longer matches the MD5 stored in the file"
                : decode.Md5 == Md5Verdict.Mismatch
                    ? $"{Plural(counts.Checksum, "checksum error", "checksum errors")}; the stored MD5 does not match"
                    : Plural(counts.Checksum, "checksum error", "checksum errors");
            sink.Raise("COR-CHECKSUM", Severity.High, 1f, weighted > 0 ? summary : summary + missing, evidence);
        }

        if (weighted > 0)
        {
            int errors = (int)Math.Ceiling(weighted);
            bool lossless = Damage.IsLossless(probe?.Audio?.Codec) || structure?.Flac is not null;
            Severity severity = lossless || errors >= input.Settings.Value("COR-DECODE", "highErrors") || lost >= input.Settings.Value("COR-DECODE", "audioHighSeconds")
                ? Severity.High
                : Severity.Medium;
            string summary = "Decoder reported " + Plural(errors, "error", "errors") + missing;
            sink.Raise("COR-DECODE", severity, 1f, summary, new Dictionary<string, object>(shared) { ["errors"] = errors });
        }
        else if (counts.MinorDecode > 0)
        {
            sink.Raise("COR-DECODE", Severity.Low, 1f, "Decoder reported " + Plural(counts.MinorDecode, "minor problem", "minor problems"), new Dictionary<string, object>(shared));
        }

        if (timedOut || declared is not double length || length <= 0 || decoded is not double got)
        {
            return;
        }

        double tolerance = estimate
            ? length * input.Settings.Value("COR-TRUNCATED", "mp3EstimatePercent") / 100
            : Math.Max(input.Settings.Value("COR-TRUNCATED", "audioSeconds"), length * input.Settings.Value("COR-TRUNCATED", "audioPercent") / 100);
        if (got < length - tolerance)
        {
            // Frames or pages dropped mid-file for errors already reported are not a cut-short file: each
            // error explains a limited loss (an Ogg page is about a second; a FLAC frame a tenth of that).
            bool corroborated = counts.Truncation > 0 || structure?.Issues.Any(i => i.Kind == StructureIssueKind.Truncated) == true;
            int damage = counts.DecodeError + counts.Checksum + counts.Unknown;
            bool explained = !corroborated && damage > 0 && length - got <= damage * input.Settings.Value("COR-TRUNCATED", "secondsPerError");
            if ((!estimate || corroborated) && !explained)
            {
                Damage.RaiseShortfall(sink, input, length, got, new Dictionary<string, object>(shared));
            }
        }
        else if (got > length + tolerance && !estimate)
        {
            sink.Raise("COR-INDEX", Severity.Low, 1f, $"Header says {MediaTime(length)}, but {MediaTime(got)} decodes", new Dictionary<string, object>(shared));
        }
    }
}

/// <summary>Photo decode rules (subplan 07, Photos — Standard and Deep; tasks C-08, C-11, C-12).</summary>
public sealed class ImageDecodeDetector : IDetector
{
    public IReadOnlyList<string> RuleIds { get; } = ["COR-CONTAINER", "COR-DECODE", "COR-PARTIAL-IMAGE", "COR-CHECKSUM", "COR-HANG"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(ImageDecodeArtifact)];

    public IReadOnlyList<Type> Optional { get; } = [typeof(ProbeArtifact), typeof(StructureArtifact)];

    public ScanDepth MinDepth => ScanDepth.Standard;

    public bool Applies(DetectorInput input) => input.Kind == MediaKind.Photo;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        ImageDecodeArtifact image = input.Get<ImageDecodeArtifact>()!;
        ProbeArtifact? probe = input.Get<ProbeArtifact>();
        StructureArtifact? structure = input.Get<StructureArtifact>();
        if (Damage.NotJudged(probe, image.Outcome) || Damage.Unrecognised(structure, probe))
        {
            return;
        }

        if (image.Outcome == RunOutcome.TimedOut)
        {
            Damage.RaiseHang(sink, idle: true, decodedSeconds: null);
            return;
        }

        SignalCounts counts = image.Log.Counts;
        Dictionary<string, object> shared = Decoder(image.Log, null, null);
        shared["framesRead"] = image.FramesRead ?? 0;
        if (image.Width is int width && image.Height is int height)
        {
            shared["widthPx"] = width;
            shared["heightPx"] = height;
        }

        if (!image.Opened)
        {
            sink.Raise("COR-CONTAINER", Severity.Critical, 1f, "No image could be read from the file", shared);
            return;
        }

        int frames = image.FramesRead ?? 0;
        int errors = counts.DecodeError + counts.Unknown + counts.Truncation;
        if (frames == 0 && errors > 0)
        {
            sink.Raise("COR-DECODE", Severity.Critical, 1f, $"Nothing could be decoded ({Plural(errors, "decoder error", "decoder errors")})", new Dictionary<string, object>(shared) { ["errors"] = errors });
        }
        else if (frames > 0 && errors > 0)
        {
            string summary = counts.Truncation > 0 ? "Image data ends before the picture is complete" : "Decoder reported " + Plural(errors, "error", "errors") + " in the picture";
            sink.Raise("COR-PARTIAL-IMAGE", Severity.High, 1f, summary, new Dictionary<string, object>(shared) { ["errors"] = errors });
        }

        if (counts.Checksum > 0)
        {
            sink.Raise("COR-CHECKSUM", Severity.High, 1f, Plural(counts.Checksum, "checksum error", "checksum errors"), new Dictionary<string, object>(shared));
        }
    }
}

/// <summary>Video decode rules (subplan 07, Video — Standard and Deep; tasks C-09 to C-12).</summary>
public sealed class VideoDecodeDetector : IDetector
{
    public IReadOnlyList<string> RuleIds { get; } = ["COR-CONTAINER", "COR-DECODE", "COR-CONCEALED", "COR-TRUNCATED", "COR-TIMESTAMPS", "COR-HANG"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(VideoDecodeArtifact)];

    public IReadOnlyList<Type> Optional { get; } = [typeof(ProbeArtifact), typeof(StructureArtifact)];

    public ScanDepth MinDepth => ScanDepth.Standard;

    public bool Applies(DetectorInput input) => input.Kind == MediaKind.Video;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        VideoDecodeArtifact video = input.Get<VideoDecodeArtifact>()!;
        ProbeArtifact? probe = input.Get<ProbeArtifact>();
        StructureArtifact? structure = input.Get<StructureArtifact>();
        if (Damage.NotJudged(probe, video.Outcome) || Damage.Unrecognised(structure, probe))
        {
            return;
        }

        bool timedOut = video.Outcome == RunOutcome.TimedOut;
        if (timedOut)
        {
            Damage.RaiseHang(sink, video.IdleTimeout, video.DecodedSeconds);
        }

        if (!video.Opened)
        {
            if (!timedOut)
            {
                sink.Raise("COR-CONTAINER", Severity.Critical, 1f, "The video cannot be opened", Decoder(video.Video, null, null));
            }

            return;
        }

        bool deep = video.Pass == VideoPass.Deep;
        SignalCounts all = video.All;
        double? declared = video.DeclaredSeconds is double ds && ds > 0 ? ds : null;
        double? decoded = video.DecodedSeconds;
        Dictionary<string, object> shared = Decoder(video.Video with { Counts = all }, declared, decoded);
        shared["pass"] = video.Pass.ToString();
        EvidenceText.AddCounts(shared, video.Video.Counts, "video");
        EvidenceText.AddCounts(shared, video.Audio.Counts, "audio");

        long missingFrames = 0;
        if (deep && !timedOut && video.FramesDeclared is long framesDeclared && framesDeclared > 0 && video.FramesDecoded is long framesDecoded)
        {
            long shortfall = framesDeclared - framesDecoded;
            double limit = Math.Max(input.Settings.Value("COR-DECODE", "frameShortfall"), framesDeclared * input.Settings.Value("COR-DECODE", "frameShortfallPercent") / 100);
            if (shortfall > limit)
            {
                missingFrames = shortfall;
                shared["framesDeclared"] = framesDeclared;
                shared["framesDecoded"] = framesDecoded;
                shared["missingFrames"] = shortfall;
            }
        }

        double lost = !timedOut && declared is double d && decoded is double x ? Math.Max(0, d - x) : 0;
        double weighted = all.WeightedDecodeErrors + missingFrames;
        if (weighted > 0)
        {
            int errors = (int)Math.Ceiling(weighted);
            Severity severity = errors >= input.Settings.Value("COR-DECODE", "highErrors") || lost >= input.Settings.Value("COR-DECODE", "videoHighSeconds")
                ? Severity.High
                : Severity.Medium;
            sink.Raise("COR-DECODE", severity, 1f, DecodePhrase(video, errors, missingFrames), new Dictionary<string, object>(shared) { ["errors"] = errors });
        }
        else if (all.MinorDecode > 0)
        {
            sink.Raise("COR-DECODE", Severity.Low, 1f, "Decoder reported " + Plural(all.MinorDecode, "minor problem", "minor problems"), new Dictionary<string, object>(shared));
        }

        if (all.Concealed > 0)
        {
            Severity severity = all.Concealed >= input.Settings.Value("COR-CONCEALED", "highFrames") ? Severity.High : Severity.Medium;
            sink.Raise("COR-CONCEALED", severity, 1f, Plural(all.Concealed, "damaged frame", "damaged frames") + " patched over by the decoder", new Dictionary<string, object>(shared));
        }

        if (!timedOut)
        {
            double tolerance = 0;
            if (declared is double length)
            {
                double percent = length * input.Settings.Value("COR-TRUNCATED", "videoPercent") / 100;
                double seconds = deep || video.HasAudio ? input.Settings.Value("COR-TRUNCATED", "videoSeconds") : input.Settings.Value("COR-TRUNCATED", "keyFrameSeconds");
                tolerance = Math.Max(seconds, percent);
            }

            if (declared is double total && decoded is double got && got < total - tolerance)
            {
                Damage.RaiseShortfall(sink, input, total, got, new Dictionary<string, object>(shared));
            }
            else if (all.Truncation > 0)
            {
                sink.Raise("COR-TRUNCATED", Severity.High, 1f, "The data ends before the file's own index says it should", new Dictionary<string, object>(shared));
            }
        }

        if (all.Timestamp >= input.Settings.Value("COR-TIMESTAMPS", "minCount"))
        {
            Severity severity = all.Timestamp >= input.Settings.Value("COR-TIMESTAMPS", "mediumCount") ? Severity.Medium : Severity.Low;
            sink.Raise("COR-TIMESTAMPS", severity, 1f, Plural(all.Timestamp, "timestamp error", "timestamp errors"), new Dictionary<string, object>(shared));
        }
    }

    private static string DecodePhrase(VideoDecodeArtifact video, int errors, long missingFrames)
    {
        int videoErrors = (int)Math.Ceiling(video.Video.Counts.WeightedDecodeErrors);
        int audioErrors = (int)Math.Ceiling(video.Audio.Counts.WeightedDecodeErrors);
        string text = "Decoder reported " + Plural(errors, "error", "errors");
        if (videoErrors > 0 && audioErrors > 0)
        {
            text += $" (video {Count(videoErrors)}, audio {Count(audioErrors)})";
        }
        else if (audioErrors > 0 && videoErrors == 0 && missingFrames == 0)
        {
            text += " in the sound";
        }

        if (missingFrames > 0)
        {
            text += "; " + Plural(missingFrames, "frame", "frames") + " could not be decoded";
        }

        return text;
    }
}

/// <summary>COR-STREAM-LENGTH from the probe: video and default audio durations differ (subplan 07).</summary>
public sealed class StreamLengthDetector : IDetector
{
    public IReadOnlyList<string> RuleIds { get; } = ["COR-STREAM-LENGTH"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(ProbeArtifact)];

    public ScanDepth MinDepth => ScanDepth.Quick;

    public bool Applies(DetectorInput input) => input.Kind == MediaKind.Video;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        ProbeArtifact probe = input.Get<ProbeArtifact>()!;
        if (!probe.Opened || probe.Video?.Duration is not double videoSeconds || probe.Audio?.Duration is not double audioSeconds || videoSeconds <= 0 || audioSeconds <= 0)
        {
            return;
        }

        double longer = Math.Max(videoSeconds, audioSeconds);
        double difference = Math.Abs(videoSeconds - audioSeconds);
        double tolerance = Math.Max(input.Settings.Value("COR-STREAM-LENGTH", "seconds"), longer * input.Settings.Value("COR-STREAM-LENGTH", "percent") / 100);
        if (difference <= tolerance)
        {
            return;
        }

        double percent = difference / longer * 100;
        Severity severity = percent > input.Settings.Value("COR-STREAM-LENGTH", "mediumPercent") ? Severity.Medium : Severity.Low;
        sink.Raise("COR-STREAM-LENGTH", severity, 1f, $"Video {MediaTime(videoSeconds)}, audio {MediaTime(audioSeconds)}", new Dictionary<string, object>
        {
            ["videoSeconds"] = Round(videoSeconds),
            ["audioSeconds"] = Round(audioSeconds),
            ["differenceSeconds"] = Round(difference),
            ["differencePercent"] = Math.Round(percent, 1),
        });
    }
}
