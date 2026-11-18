using System.Globalization;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;

namespace MediaVerifier.Core.Analysis.Detectors;

/// <summary>The outcome of the down-up decision (subplan 11, Decision steps 1–5).</summary>
public sealed record UpscaleVerdict(int EffectiveHeight, int ContainerHeight, double Percentile, double Margin, int UsableFrames, int ReproducibleFrames, int SampledFrames, float Confidence, bool FallOff)
{
    /// <summary>Per sampled frame: time, SSIM at the effective height, and whether it was usable (frame strip, subplan 11).</summary>
    public double[] FrameTimes { get; init; } = [];

    public double[] FrameSsim { get; init; } = [];

    public bool[] FrameUsable { get; init; } = [];
}

/// <summary>The down-up decision shared by video and photos (subplan 11, task V-02 and P-01).</summary>
public static class UpscaleDecision
{
    /// <summary>The q-th percentile (0–1) by linear interpolation; values need not be sorted.</summary>
    public static double Percentile(IReadOnlyList<double> values, double q)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return double.NaN;
        }

        double[] sorted = [.. values.Order()];
        double position = q * (sorted.Length - 1);
        int low = (int)Math.Floor(position);
        int high = Math.Min(sorted.Length - 1, low + 1);
        return sorted[low] + ((sorted[high] - sorted[low]) * (position - low));
    }

    /// <summary>
    /// The frames that count (step 1): spatial information at or above the minimum, every candidate measured, and each
    /// picture once. Fast seeking lands on the key frame at or before each sample time, so in a clip shorter than a
    /// group of pictures several samples are the same frame; those measure bit-identically and count once.
    /// </summary>
    public static SampledFrame[] UsableFrames(FrameSamplesArtifact samples, double minSpatialInfo)
    {
        ArgumentNullException.ThrowIfNull(samples);
        List<SampledFrame> usable = [];
        foreach (SampledFrame frame in samples.Frames)
        {
            if (frame.SpatialInfo >= minSpatialInfo && frame.Ssim.Length == samples.Candidates.Length && !usable.Exists(f => SameFrame(f, frame)))
            {
                usable.Add(frame);
            }
        }

        return [.. usable];
    }

    /// <summary>
    /// Steps 1–5: usable frames by spatial information; per candidate the 10th percentile of SSIM-Y; the
    /// smallest candidate that passes, with all larger ones passing, provided the next smaller one fails or
    /// drops by at least 0.008. Null when there are too few usable frames or nothing passes.
    /// </summary>
    public static UpscaleVerdict? Decide(FrameSamplesArtifact samples, double minSpatialInfo, double threshold, int minFrames, bool starved)
    {
        ArgumentNullException.ThrowIfNull(samples);
        SampledFrame[] usable = UsableFrames(samples, minSpatialInfo);
        if (usable.Length < minFrames || samples.Candidates.Length == 0)
        {
            return null;
        }

        double[] percentiles = [.. Enumerable.Range(0, samples.Candidates.Length).Select(k => Percentile([.. usable.Select(f => f.Ssim[k])], 0.10))];
        int chosen = -1;
        for (int k = 0; k < percentiles.Length && percentiles[k] >= threshold; k++)
        {
            chosen = k;
        }

        if (chosen < 0)
        {
            return null;
        }

        bool fallOff = chosen + 1 < percentiles.Length && (percentiles[chosen + 1] < threshold || percentiles[chosen] - percentiles[chosen + 1] >= 0.008);
        double margin = percentiles[chosen] - threshold;
        float confidence = margin >= 0.005 ? 0.85f : 0.65f;
        if (!fallOff)
        {
            confidence -= 0.15f;
        }

        if (usable.Length < 6 && minFrames > 1)
        {
            confidence -= 0.20f;
        }

        if (starved)
        {
            confidence -= 0.15f;
        }

        int reproducible = usable.Count(f => f.Ssim[chosen] >= threshold);

        // Rounded to the plan's two decimals: 0.65 - 0.15 in float arithmetic is 0.49999997, which would read as
        // "Suspicious" and cap the finding at Low, while the same plan value reached another way reads "Possibly".
        return new UpscaleVerdict(samples.Candidates[chosen], samples.Height, percentiles[chosen], margin, usable.Length, reproducible, samples.Frames.Length, FindingSink.RoundConfidence(Math.Max(0.3f, confidence)), fallOff)
        {
            FrameTimes = [.. samples.Frames.Select(f => Math.Round(f.Time, 3))],
            FrameSsim = [.. samples.Frames.Select(f => f.Ssim.Length > chosen ? Math.Round(f.Ssim[chosen], 4) : 0)],

            // A repeated frame is shown faded like an unusable one: it did not count again.
            FrameUsable = [.. samples.Frames.Select(f => usable.Any(u => ReferenceEquals(u, f)))],
        };
    }

    private static bool SameFrame(SampledFrame a, SampledFrame b) => a.SpatialInfo == b.SpatialInfo && a.Ssim.AsSpan().SequenceEqual(b.Ssim);
}

/// <summary>Bits per pixel and the H.264-equivalent factor (subplan 11, VID-STARVED; task V-03).</summary>
public static class VideoBitrate
{
    /// <summary>The codec factor, or null for codecs the rule does not judge (intra and lossless codecs).</summary>
    public static double? CodecFactor(string? codec) => codec?.ToLowerInvariant() switch
    {
        "h264" => 1.0,
        "hevc" or "h265" => 0.6,
        "vp9" => 0.65,
        "av1" => 0.5,
        "vvc" or "h266" => 0.4,
        "vc1" or "wmv3" => 1.2,
        "mpeg4" or "msmpeg4v3" or "msmpeg4v2" => 1.6,
        "mpeg2video" or "mpeg1video" => 2.2,
        _ => null,
    };

    /// <summary>The video bitrate: the stream's, else the container's minus audio, else size over duration minus audio.</summary>
    public static (long? BitRate, string Source) VideoBitRate(ProbeArtifact probe, StreamInfo video)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(video);
        if (video.BitRate is > 0)
        {
            return (video.BitRate, "stream");
        }

        long audio = probe.Streams.Where(s => s.IsAudio).Sum(s => s.BitRate ?? 0);
        if (probe.BitRate is > 0 && probe.BitRate > audio)
        {
            return (probe.BitRate - audio, "container");
        }

        double? duration = probe.Duration ?? video.Duration;
        if (probe.Size is > 0 && duration is > 0)
        {
            long total = (long)(probe.Size.Value * 8 / duration.Value);
            return total > audio ? (total - audio, "size") : (null, "none");
        }

        return (null, "none");
    }

    /// <summary>H.264-equivalent bits per pixel; null when it cannot be computed or the codec is not judged.</summary>
    public static double? EquivalentBpp(ProbeArtifact probe, StreamInfo video, out long bitRate, out double bpp)
    {
        bitRate = 0;
        bpp = 0;
        double? factor = CodecFactor(video.Codec);
        (long? rate, _) = VideoBitRate(probe, video);
        if (factor is null || rate is not long r || video.Width is not int w || video.Height is not int h || video.FrameRate is not double fps || fps <= 0 || w <= 0 || h <= 0)
        {
            return null;
        }

        bitRate = r;
        bpp = r / ((double)w * h * fps);
        return bpp / factor.Value;
    }
}

/// <summary>
/// Video quality (subplan 11): <c>VID-STARVED</c> and <c>VID-HDR</c> from the probe at every depth; with
/// the sampled frames (Standard) also <c>VID-UPSCALED</c>, <c>VID-BLOCKY</c> and <c>VID-INFLATED</c>.
/// </summary>
public sealed class VideoQualityDetector : IDetector
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public IReadOnlyList<string> RuleIds { get; } =
        ["VID-STARVED", "VID-HDR", "VID-UPSCALED", "VID-BLOCKY", "VID-INFLATED", "VID-INTERLACE", "VID-FROZEN", "VID-BLACK", "VID-AUDIO-SILENT"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(ProbeArtifact)];

    public IReadOnlyList<Type> Optional { get; } = [typeof(FrameSamplesArtifact), typeof(InterlaceArtifact), typeof(VideoDecodeArtifact)];

    public ScanDepth MinDepth => ScanDepth.Quick;

    public bool Applies(DetectorInput input) => input.Kind == MediaKind.Video;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        ProbeArtifact probe = input.Get<ProbeArtifact>()!;
        if (probe is not { Opened: true } || probe.Video is not StreamInfo video || video.Width is not > 0 || video.Height is not > 0)
        {
            return;
        }

        RuleSettings rules = input.Settings;
        Hdr(video, sink);
        double? equivalent = VideoBitrate.EquivalentBpp(probe, video, out long bitRate, out double bpp);
        double starvedLow = rules.Value("VID-STARVED", "lowBpp");
        bool starved = equivalent is double e && e < starvedLow;
        FrameSamplesArtifact? samples = input.Get<FrameSamplesArtifact>();
        double? block = null;
        double? blur = null;
        UpscaleVerdict? upscale = null;
        if (samples is { Sampled: true })
        {
            // Each picture once: a short clip's samples can all be the same key frame.
            SampledFrame[] usable = UpscaleDecision.UsableFrames(samples, rules.Value("VID-UPSCALED", "minSpatialInfo"));
            if (usable.Length > 0)
            {
                block = UpscaleDecision.Percentile([.. usable.Select(f => f.Block)], 0.5);
                blur = UpscaleDecision.Percentile([.. usable.Select(f => f.Blur)], 0.5);
            }

            upscale = UpscaleDecision.Decide(samples, rules.Value("VID-UPSCALED", "minSpatialInfo"), rules.Value("VID-UPSCALED", "ssim"), 4, starved);
        }

        bool blocky = Blocky(block, blur, equivalent, starvedLow, rules, sink);
        Starved(equivalent, bpp, bitRate, video, starvedLow, blocky, rules, sink);
        if (upscale is not null)
        {
            Upscaled(upscale, starved, samples!.Width, sink);
        }

        Inflated(equivalent, bitRate, upscale, blocky, rules, sink);
        if (input.Get<InterlaceArtifact>() is { Skipped: null } interlace)
        {
            Interlace(interlace, rules, sink);
        }

        if (input.Get<VideoDecodeArtifact>() is { Outcome: RunOutcome.Completed } decode)
        {
            Soundtrack(decode, rules, sink);
            if (decode.Pass == VideoPass.Deep)
            {
                Frozen(decode, rules, sink);
                Black(decode, sink);
            }
        }
    }

    /// <summary>VID-INTERLACE: idet against the stream's field-order flag.</summary>
    private static void Interlace(InterlaceArtifact idet, RuleSettings rules, FindingSink sink)
    {
        if (idet.Determined < rules.Value("VID-INTERLACE", "minFrames"))
        {
            return;
        }

        double interlaced = (double)idet.Interlaced / idet.Determined;
        Dictionary<string, object> evidence = new(StringComparer.Ordinal)
        {
            ["topFieldFirst"] = idet.TopFirst,
            ["bottomFieldFirst"] = idet.BottomFirst,
            ["progressive"] = idet.Progressive,
            ["undetermined"] = idet.Undetermined,
        };
        if (!idet.FlaggedInterlaced && interlaced * 100 >= rules.Value("VID-INTERLACE", "combedPercent"))
        {
            sink.Raise("VID-INTERLACE", Severity.Medium, 0.8f, $"combing artifacts: {Math.Round(interlaced * 100).ToString("0", Invariant)} % of sampled frames are interlaced, but the video is marked progressive", evidence);
        }
        else if (idet.FlaggedInterlaced && (1 - interlaced) * 100 >= rules.Value("VID-INTERLACE", "progressivePercent"))
        {
            sink.Raise("VID-INTERLACE", Severity.Low, 0.8f, $"marked interlaced, but {Math.Round((1 - interlaced) * 100).ToString("0", Invariant)} % of sampled frames are progressive", evidence);
        }
    }

    /// <summary>VID-AUDIO-SILENT: the default soundtrack's peak stays below the limit for the whole file.</summary>
    private static void Soundtrack(VideoDecodeArtifact decode, RuleSettings rules, FindingSink sink)
    {
        if (decode.HasAudio && decode.AudioMaxVolumeDb is double peak && peak < rules.Value("VID-AUDIO-SILENT", "peakDb"))
        {
            // volumedetect measures 16-bit samples: all zeros read -91.0 dB, and one step above zero -90.3 dB.
            string level = peak <= -90.5 ? "digital silence" : $"peak {peak.ToString("0.0", Invariant)} dBFS";
            sink.Raise("VID-AUDIO-SILENT", Severity.Medium, 1f, "the main soundtrack is silent from start to end (" + level + ")", new Dictionary<string, object>(StringComparer.Ordinal) { ["maxVolumeDb"] = Math.Round(peak, 1) });
        }
    }

    /// <summary>VID-FROZEN: freezes of 10 s or more away from the first and last minute and not on black.</summary>
    private static void Frozen(VideoDecodeArtifact decode, RuleSettings rules, FindingSink sink)
    {
        double duration = decode.DecodedSeconds ?? decode.DeclaredSeconds ?? 0;
        double margin = rules.Value("VID-FROZEN", "edgeSeconds");
        double minimum = rules.Value("VID-FROZEN", "seconds");
        double[] starts = decode.FreezeStarts ?? [];
        double[] lengths = decode.FreezeDurations ?? [];
        List<(double Start, double Length)> freezes = [];
        for (int i = 0; i < Math.Min(starts.Length, lengths.Length); i++)
        {
            (double start, double length) = (starts[i], lengths[i]);
            bool edge = start < margin || start + length > duration - margin;
            if (length >= minimum && !edge && !OnBlack(decode, start, length))
            {
                freezes.Add((start, length));
            }
        }

        if (freezes.Count == 0 || duration <= 0)
        {
            return;
        }

        double total = freezes.Sum(f => f.Length);
        Severity severity = total / duration * 100 >= rules.Value("VID-FROZEN", "mediumPercent") ? Severity.Medium : Severity.Low;
        string first = EvidenceText.MediaTime(freezes[0].Start);
        sink.Raise(
            "VID-FROZEN",
            severity,
            0.8f,
            $"the picture stands still for {Math.Round(total).ToString("0", Invariant)} s, first at {first}",
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["positionsSeconds"] = freezes.Select(f => Math.Round(f.Start, 1)).ToArray(),
                ["lengthsSeconds"] = freezes.Select(f => Math.Round(f.Length, 1)).ToArray(),
            });
    }

    private static bool OnBlack(VideoDecodeArtifact decode, double start, double length)
    {
        double[] blackStarts = decode.BlackStarts ?? [];
        double[] blackLengths = decode.BlackDurations ?? [];
        for (int i = 0; i < Math.Min(blackStarts.Length, blackLengths.Length); i++)
        {
            double overlap = Math.Min(start + length, blackStarts[i] + blackLengths[i]) - Math.Max(start, blackStarts[i]);
            if (overlap >= length / 2)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>VID-BLACK (disabled by default): black sections of 30 s or more.</summary>
    private static void Black(VideoDecodeArtifact decode, FindingSink sink)
    {
        double[] starts = decode.BlackStarts ?? [];
        double[] lengths = decode.BlackDurations ?? [];
        if (starts.Length == 0 || lengths.Length == 0)
        {
            return;
        }

        sink.Raise(
            "VID-BLACK",
            Severity.Low,
            1f,
            $"{Math.Round(lengths.Sum()).ToString("0", Invariant)} s of black picture, first at {EvidenceText.MediaTime(starts[0])}",
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["positionsSeconds"] = starts.Select(s => Math.Round(s, 1)).ToArray(),
                ["lengthsSeconds"] = lengths.Select(l => Math.Round(l, 1)).ToArray(),
            });
    }

    private static void Hdr(StreamInfo video, FindingSink sink)
    {
        bool hdr = video.ColorTransfer is "smpte2084" or "arib-std-b67";
        if (!hdr)
        {
            return;
        }

        bool eightBit = video.PixFmt is string pix && !pix.Contains("10", StringComparison.Ordinal) && !pix.Contains("12", StringComparison.Ordinal) && !pix.Contains("16", StringComparison.Ordinal);
        string transfer = video.ColorTransfer == "smpte2084" ? "PQ" : "HLG";
        Dictionary<string, object> evidence = new(StringComparer.Ordinal) { ["transfer"] = video.ColorTransfer!, ["pixelFormat"] = video.PixFmt ?? string.Empty, ["primaries"] = video.ColorPrimaries ?? string.Empty };
        if (eightBit)
        {
            sink.Raise("VID-HDR", Severity.Medium, 1f, $"{transfer} HDR marked on 8-bit video", evidence);
        }
        else if (video.ColorPrimaries is not null && video.ColorPrimaries != "bt2020")
        {
            sink.Raise("VID-HDR", Severity.Low, 1f, $"{transfer} HDR without BT.2020 colours", evidence);
        }
    }

    private static void Starved(double? equivalent, double bpp, long bitRate, StreamInfo video, double low, bool blocky, RuleSettings rules, FindingSink sink)
    {
        if (equivalent is not double e || e >= low)
        {
            return;
        }

        Severity severity = e < rules.Value("VID-STARVED", "mediumBpp") || blocky ? Severity.Medium : Severity.Low;
        Dictionary<string, object> evidence = new(StringComparer.Ordinal)
        {
            ["bitrateKbps"] = Math.Round(bitRate / 1000.0),
            ["bpp"] = Math.Round(bpp, 4),
            ["equivalentBpp"] = Math.Round(e, 4),
            ["codec"] = video.Codec ?? string.Empty,
            ["width"] = video.Width ?? 0,
            ["height"] = video.Height ?? 0,
            ["fps"] = Math.Round(video.FrameRate ?? 0, 3),
        };
        sink.Raise("VID-STARVED", severity, 1f, $"{Kbps(bitRate)} for {video.Height}p: {e.ToString("0.000", Invariant)} bits per pixel", evidence);
    }

    private static bool Blocky(double? block, double? blur, double? equivalent, double starvedLow, RuleSettings rules, FindingSink sink)
    {
        if (block is not double b || b < rules.Value("VID-BLOCKY", "low"))
        {
            return false;
        }

        bool supported = (equivalent is double e && e < 2 * starvedLow) || (blur is double u && u >= rules.Value("VID-BLOCKY", "blur"));
        if (!supported)
        {
            return false;
        }

        Severity severity = b >= rules.Value("VID-BLOCKY", "medium") ? Severity.Medium : Severity.Low;
        sink.Raise(
            "VID-BLOCKY",
            severity,
            0.7f,
            $"block edges score {b.ToString("0.0", Invariant)} in sampled frames",
            new Dictionary<string, object>(StringComparer.Ordinal) { ["block"] = Math.Round(b, 2), ["blur"] = Math.Round(blur ?? 0, 2) });
        return true;
    }

    private static void Upscaled(UpscaleVerdict verdict, bool starved, int width, FindingSink sink)
    {
        double r = (double)verdict.EffectiveHeight / verdict.ContainerHeight;
        Severity severity = r <= 0.55 && verdict.Confidence >= 0.80f ? Severity.High
            : r <= 0.80 && verdict.Confidence >= 0.50f ? Severity.Medium
            : Severity.Low;
        // Subplan 11: how many of the frames the verdict rests on are reproducible from the effective height.
        string frames = string.Create(Invariant, $"{verdict.ReproducibleFrames} of {verdict.UsableFrames} sampled frames are reproducible from {verdict.EffectiveHeight}p");
        string summary = $"{verdict.ContainerHeight}p file with detail equivalent to {verdict.EffectiveHeight}p ({(starved ? "upscaled or heavily compressed; " : string.Empty)}{frames})";

        sink.Raise(
            "VID-UPSCALED",
            severity,
            verdict.Confidence,
            summary,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["effectiveHeight"] = verdict.EffectiveHeight,
                ["containerHeight"] = verdict.ContainerHeight,
                ["containerWidth"] = width,
                ["ssimP10"] = Math.Round(verdict.Percentile, 4),
                ["reproducibleFrames"] = verdict.ReproducibleFrames,
                ["usableFrames"] = verdict.UsableFrames,
                ["sampledFrames"] = verdict.SampledFrames,
                ["frameTimes"] = verdict.FrameTimes,
                ["frameSsim"] = verdict.FrameSsim,
                ["frameUsable"] = verdict.FrameUsable,
            });
    }

    private static void Inflated(double? equivalent, long bitRate, UpscaleVerdict? upscale, bool blocky, RuleSettings rules, FindingSink sink)
    {
        if (equivalent is not double e || e < rules.Value("VID-INFLATED", "bpp"))
        {
            return;
        }

        bool soft = upscale is { Confidence: >= 0.65f };
        if (!soft && !blocky)
        {
            return;
        }

        float confidence = soft ? upscale!.Confidence : 0.6f;
        string detail = soft ? $"detail equivalent to {upscale!.EffectiveHeight}p" : "heavy compression artifacts";
        sink.Raise(
            "VID-INFLATED",
            confidence >= 0.7f ? Severity.Medium : Severity.Low,
            confidence,
            $"High bitrate ({Kbps(bitRate)}) but {detail}: probably re-encoded from a lower-quality source",
            new Dictionary<string, object>(StringComparer.Ordinal) { ["bitrateKbps"] = Math.Round(bitRate / 1000.0), ["equivalentBpp"] = Math.Round(e, 4) });
    }

    private static string Kbps(long bitRate) => bitRate >= 1_000_000
        ? (bitRate / 1_000_000.0).ToString(bitRate >= 10_000_000 ? "0" : "0.0", Invariant) + " Mbps"
        : Math.Round(bitRate / 1000.0).ToString("0", Invariant) + " kbps";
}

/// <summary>Estimated JPEG quality from the luminance quantisation table (subplan 11, IMG-HEAVY-COMPRESSION; task P-02).</summary>
public static class JpegQuality
{
    /// <summary>The standard (Annex K) luminance table in row order.</summary>
    public static readonly int[] StandardLuminance =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    /// <summary>Row-order index of each zigzag position.</summary>
    public static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>
    /// The estimated quality (1–100) of a luminance table stored in zigzag order, and whether the table is a
    /// scaled copy of the standard one (otherwise the estimate is approximate).
    /// </summary>
    public static (double Quality, bool Standard) Estimate(IReadOnlyList<int> zigzagTable)
    {
        ArgumentNullException.ThrowIfNull(zigzagTable);
        if (zigzagTable.Count != 64)
        {
            return (double.NaN, false);
        }

        int[] table = new int[64];
        for (int i = 0; i < 64; i++)
        {
            table[ZigZag[i]] = zigzagTable[i];
        }

        // The plan's mean ratio, over the entries not clamped at 1 or 255 (low and high qualities clamp many).
        double s = 0;
        int used = 0;
        for (int i = 0; i < 64; i++)
        {
            if (table[i] is > 1 and < 255)
            {
                s += (double)table[i] / StandardLuminance[i];
                used++;
            }
        }

        if (used < 8)
        {
            s = 0;
            used = 64;
            for (int i = 0; i < 64; i++)
            {
                s += (double)table[i] / StandardLuminance[i];
            }
        }

        s *= 100.0 / used;
        double quality = s <= 100 ? (200 - s) / 2 : 5000 / s;
        quality = Math.Clamp(quality, 1, 100);

        // A scaled copy: every entry within one step of the standard entry scaled by the same factor.
        bool standard = true;
        for (int i = 0; i < 64 && standard; i++)
        {
            double expected = Math.Clamp(Math.Floor(((StandardLuminance[i] * s) + 50) / 100), 1, 255);
            standard = Math.Abs(table[i] - expected) <= 1.5 + (0.03 * expected);
        }

        return (quality, standard);
    }
}

/// <summary>Photo quality (subplan 11): <c>IMG-HEAVY-COMPRESSION</c> and <c>IMG-LOW-RES</c> (Quick), <c>IMG-UPSCALED</c> (Standard).</summary>
public sealed class PhotoQualityDetector : IDetector
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public IReadOnlyList<string> RuleIds { get; } = ["IMG-HEAVY-COMPRESSION", "IMG-LOW-RES", "IMG-UPSCALED"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(StructureArtifact)];

    public IReadOnlyList<Type> Optional { get; } = [typeof(ProbeArtifact), typeof(FrameSamplesArtifact)];

    public ScanDepth MinDepth => ScanDepth.Quick;

    public bool Applies(DetectorInput input) => input.Kind == MediaKind.Photo;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        StructureArtifact? structure = input.Get<StructureArtifact>();
        ProbeArtifact? probe = input.Get<ProbeArtifact>();
        RuleSettings rules = input.Settings;
        JpegInfo? jpeg = structure?.Jpeg;
        int width = jpeg?.Width ?? probe?.Video?.Width ?? 0;
        int height = jpeg?.Height ?? probe?.Video?.Height ?? 0;
        double? quality = null;
        if (jpeg is { QuantTables.Count: > 0 } && jpeg.QuantTables[0].Length == 64)
        {
            (double q, bool standard) = JpegQuality.Estimate(jpeg.QuantTables[0]);
            quality = q;
            if (q < rules.Value("IMG-HEAVY-COMPRESSION", "lowQuality"))
            {
                long size = structure?.FileLength ?? 0;
                double bitsPerPixel = width > 0 && height > 0 ? size * 8.0 / ((double)width * height) : 0;
                string estimate = (standard ? string.Empty : "about ") + Math.Round(q).ToString("0", Invariant);
                sink.Raise(
                    "IMG-HEAVY-COMPRESSION",
                    q < rules.Value("IMG-HEAVY-COMPRESSION", "mediumQuality") ? Severity.Medium : Severity.Low,
                    standard ? 0.9f : 0.7f,
                    $"JPEG quality {estimate} of 100",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["quality"] = Math.Round(q, 1),
                        ["standardTable"] = standard,
                        ["bitsPerPixel"] = Math.Round(bitsPerPixel, 2),
                    });
            }
        }

        if (width > 0 && height > 0 && (double)width * height < rules.Value("IMG-LOW-RES", "megapixels") * 1_000_000)
        {
            sink.Raise(
                "IMG-LOW-RES",
                Severity.Low,
                1f,
                $"{width} × {height} pixels",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["width"] = width, ["height"] = height });
        }

        // Heavy JPEG compression removes detail the same way enlarging does: such a verdict carries less confidence
        // (calibration, docs/calibration/photo.md), as a starved video's does.
        bool compressed = quality is double qv && qv < rules.Value("IMG-HEAVY-COMPRESSION", "lowQuality");
        if (input.Get<FrameSamplesArtifact>() is { Sampled: true } samples
            && UpscaleDecision.Decide(samples, rules.Value("IMG-UPSCALED", "minSpatialInfo"), rules.Value("IMG-UPSCALED", "ssim"), 1, compressed) is UpscaleVerdict verdict)
        {
            double r = (double)verdict.EffectiveHeight / verdict.ContainerHeight;
            int effectiveWidth = (int)Math.Round(samples.Width * r);
            sink.Raise(
                "IMG-UPSCALED",
                r <= 0.5 && verdict.Confidence >= 0.80f ? Severity.Medium : Severity.Low,
                verdict.Confidence,
                $"{samples.Width} × {samples.Height} image with the detail of about {effectiveWidth} × {verdict.EffectiveHeight}" + (compressed ? " (enlarged or over-compressed)" : string.Empty),
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["effectiveHeight"] = verdict.EffectiveHeight,
                    ["containerHeight"] = verdict.ContainerHeight,
                    ["containerWidth"] = samples.Width,
                    ["ssim"] = Math.Round(verdict.Percentile, 4),
                });
        }
    }
}
