using System.Globalization;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;

namespace MediaVerifier.Core.Analysis.Detectors;

/// <summary>
/// Audio authenticity and quality from the decode's signal features (subplan 10): spectral cutoff,
/// padded bit depth, clipping, overs, DC, dead and out-of-phase channels, silence and dropouts. Pure:
/// it reads <see cref="PcmStats"/>, so a threshold change re-judges without decoding again.
/// </summary>
public sealed class AudioQualityDetector : IDetector
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public IReadOnlyList<string> RuleIds { get; } =
    [
        "AUD-FAKE-LOSSLESS", "AUD-UPSAMPLED", "AUD-BITRATE-INFLATED", "AUD-PADDED-BITS", "AUD-CLIPPING", "AUD-OVERS", "AUD-DC",
        "AUD-DEAD-CHANNEL", "AUD-PHASE", "AUD-DUAL-MONO", "AUD-SILENT", "AUD-LONG-SILENCE", "COR-DROPOUT", "COR-CLICKS",
    ];

    public IReadOnlyList<Type> Needs { get; } = [typeof(AudioDecodeArtifact)];

    public IReadOnlyList<Type> Optional { get; } = [typeof(ProbeArtifact), typeof(StructureArtifact)];

    public ScanDepth MinDepth => ScanDepth.Standard;

    public bool Applies(DetectorInput input) => input.Kind == MediaKind.Audio;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        AudioDecodeArtifact decode = input.Get<AudioDecodeArtifact>()!;
        ProbeArtifact? probe = input.Get<ProbeArtifact>();
        StructureArtifact? structure = input.Get<StructureArtifact>();

        // A damaged or partial decode is the corruption rules' business; quality is judged on whole files.
        SignalCounts log = decode.Log.Counts;
        bool damaged = log.OpenFailure + log.Truncation + log.DecodeError + log.Concealed + log.Checksum + log.Unknown > 0;
        if (decode.Stats is not PcmStats stats || decode.Outcome != RunOutcome.Completed || stats.Seconds <= 0 || damaged)
        {
            return;
        }

        StreamInfo? audio = probe?.Audio;
        string? codec = audio?.Codec;
        bool lossless = Damage.IsLossless(codec) || (codec is null && structure?.Flac is not null);
        RuleSettings rules = input.Settings;
        Signal(stats, rules, sink);
        Spectral(stats, audio, codec, lossless, structure, probe, rules, sink);
        if (lossless)
        {
            PaddedBits(stats, audio, structure, sink);
        }

        Clipping(stats, lossless, rules, sink);
        Dropouts(stats, sink);
        Clicks(stats, rules, sink);
    }

    /// <summary>A codec as people name it in a sentence: FLAC, ALAC, WAV.</summary>
    public static string FormatName(string? codec, string? container) => codec?.ToLowerInvariant() switch
    {
        "flac" => "FLAC",
        "alac" => "ALAC",
        "wavpack" => "WavPack",
        "ape" => "Monkey's Audio",
        "tta" => "TTA",
        string pcm when pcm.StartsWith("pcm_", StringComparison.Ordinal) => container?.Contains("aiff", StringComparison.OrdinalIgnoreCase) == true ? "AIFF" : "WAV",
        _ => "a lossless format",
    };

    /// <summary>The usual source of a cutoff (subplan 10, Typical source table): rate label and codec.</summary>
    public static (string Typical, string Source, string Phrase) TypicalSource(double kneeHz) => kneeHz switch
    {
        <= 11_500 => ("MP3 at 64 kbps or lower", "MP3", "a low-bitrate MP3"),
        <= 16_000 => ("MP3 at 96–112 kbps", "MP3", "an MP3"),
        <= 17_000 => ("MP3 at 128 kbps", "MP3", "an MP3"),
        <= 17_600 => ("AAC at 128 kbps or MP3 at 160 kbps", "AAC", "an AAC or MP3 file"),
        <= 19_000 => ("MP3 at 192 kbps", "MP3", "an MP3"),
        <= 19_800 => ("MP3 at 224–256 kbps", "MP3", "an MP3"),
        <= 20_600 => ("MP3 at 320 kbps or Opus", "lossy", "an MP3 or Opus file"),
        _ => ("a lossy encoder", "lossy", "a lossy file"),
    };

    private static string Khz(double hz) => (hz / 1000).ToString("0.0", Invariant) + " kHz";

    private static string RateLabel(int rate) => (rate / 1000.0).ToString(rate % 1000 == 0 ? "0" : "0.0", Invariant) + " kHz";

    private static Severity SeverityFor(float confidence) => confidence >= 0.80f ? Severity.High : confidence >= 0.50f ? Severity.Medium : Severity.Low;

    private static void Spectral(PcmStats stats, StreamInfo? audio, string? codec, bool lossless, StructureArtifact? structure, ProbeArtifact? probe, RuleSettings rules, FindingSink sink)
    {
        SpectrumStats spectrum = stats.Spectrum;
        int rate = stats.SampleRate;
        double minDrop = rules.Value("AUD-FAKE-LOSSLESS", "minDropDb");
        if (!spectrum.Sufficient || spectrum.DropDb < minDrop || rate <= 32_000)
        {
            return;
        }

        double knee = spectrum.KneeHz;
        double floor = stats.AboveVsFloorDb;
        Dictionary<string, object> evidence = new(StringComparer.Ordinal)
        {
            ["kneeHz"] = Math.Round(knee),
            ["dropDb"] = Math.Round(spectrum.DropDb, 1),
            ["sampleRateHz"] = rate,
            ["analysedSeconds"] = Math.Round(spectrum.AnalysedSeconds, 1),
        };
        if (!double.IsNaN(floor))
        {
            evidence["aboveVsFloorDb"] = Math.Round(floor, 1);
        }

        if (lossless)
        {
            LosslessCutoff(stats, codec, probe, rules, knee, floor, evidence, sink);
        }
        else
        {
            InflatedBitrate(stats, audio, codec, structure, probe, rules, knee, evidence, sink);
        }
    }

    private static void LosslessCutoff(PcmStats stats, string? codec, ProbeArtifact? probe, RuleSettings rules, double knee, double floor, Dictionary<string, object> evidence, FindingSink sink)
    {
        int rate = stats.SampleRate;
        SpectrumStats spectrum = stats.Spectrum;
        double likely = rules.Value("AUD-FAKE-LOSSLESS", "likelyKneeHz");
        double possible = rules.Value("AUD-FAKE-LOSSLESS", "possibleKneeHz");
        bool hiRes = rate >= 88_200;
        string format = FormatName(codec, probe?.FormatName);
        float? baseConfidence = null;
        bool capped = false;
        if (rate >= 44_100 && !hiRes)
        {
            if (knee <= likely)
            {
                baseConfidence = 0.75f;
            }
            else if (knee <= possible)
            {
                baseConfidence = 0.45f;
                capped = true;
            }
            else if (rate == 48_000 && knee is >= 21_300 and <= 22_500)
            {
                sink.Raise("AUD-UPSAMPLED", Severity.Low, 0.6f, "content stops at " + Khz(knee) + " in a 48 kHz file: resampled from 44.1 kHz", evidence);
                return;
            }
        }
        else if (hiRes)
        {
            if (knee <= possible)
            {
                baseConfidence = 0.75f;
            }
            else
            {
                Upsampled(rate, knee, floor, spectrum, rules, evidence, sink);
                return;
            }
        }

        if (baseConfidence is not float confidence)
        {
            return;
        }

        confidence += spectrum.DropDb >= 45 ? 0.10f : 0f;
        confidence += !double.IsNaN(floor) && floor <= 6 ? 0.10f : 0f;
        confidence -= spectrum.AnalysedSeconds < 30 ? 0.20f : 0f;
        confidence = Math.Clamp(confidence, 0.30f, capped ? 0.70f : 0.97f);
        (string typical, string source, string phrase) = TypicalSource(knee);
        evidence["sourceName"] = source;
        evidence["sourcePhrase"] = phrase;
        evidence["formatName"] = format;
        evidence["typicalSource"] = typical;
        string noise = !double.IsNaN(floor) && floor <= 6 ? "; only rounding noise above" : string.Empty;
        sink.Raise("AUD-FAKE-LOSSLESS", SeverityFor(confidence), confidence, "hard cutoff at " + Khz(knee) + noise + ", typical of " + typical, evidence);
    }

    private static void Upsampled(int rate, double knee, double floor, SpectrumStats spectrum, RuleSettings rules, Dictionary<string, object> evidence, FindingSink sink)
    {
        double maxKnee = rules.Value("AUD-UPSAMPLED", "maxKneeHz");
        if (knee <= maxKnee)
        {
            float confidence = !double.IsNaN(floor) && floor <= 6 ? 0.90f : 0.80f;
            string from = Math.Abs(knee - 22_050) <= 500 ? "44.1 kHz" : Math.Abs(knee - 24_000) <= 500 ? "48 kHz" : "44.1 or 48 kHz";
            sink.Raise("AUD-UPSAMPLED", Severity.Medium, confidence, "content stops at " + Khz(knee) + " in a " + RateLabel(rate) + " file: upsampled from " + from, evidence);
        }
        else if (spectrum.Ratio < 0.93)
        {
            sink.Raise("AUD-UPSAMPLED", Severity.Low, 0.6f, "content stops at " + Khz(knee) + ", band-limited below what " + RateLabel(rate) + " can hold", evidence);
        }
    }

    /// <summary>Expected minimum knee by codec and nominal bitrate (subplan 10, AUD-BITRATE-INFLATED).</summary>
    private static readonly (string Codec, int Kbps, double KneeHz)[] Expected =
    [
        ("mp3", 256, 18_500), ("mp3", 192, 17_500), ("mp3", 160, 16_500), ("mp3", 128, 15_000),
        ("aac", 256, 18_500), ("aac", 160, 17_000), ("aac", 128, 15_500),
        ("vorbis", 192, 18_000), ("vorbis", 128, 15_500),
        ("opus", 96, 15_000),
    ];

    private static void InflatedBitrate(PcmStats stats, StreamInfo? audio, string? codec, StructureArtifact? structure, ProbeArtifact? probe, RuleSettings rules, double knee, Dictionary<string, object> evidence, FindingSink sink)
    {
        string? family = codec?.ToLowerInvariant() switch
        {
            "mp3" or "mp3float" => "mp3",
            "aac" when audio?.Profile is null || audio.Profile.Equals("LC", StringComparison.OrdinalIgnoreCase) => "aac",
            "vorbis" => "vorbis",
            "opus" => "opus",
            _ => null,
        };
        if (family is null || stats.Channels > 2 || stats.SampleRate is not (44_100 or 48_000))
        {
            return;
        }

        long? bps = audio?.BitRate ?? probe?.BitRate;
        if (bps is not long bitrate || bitrate <= 0)
        {
            return;
        }

        int kbps = (int)(bitrate / 1000);
        bool vbr = structure?.Mp3?.VbrHeader is "Xing" or "VBRI";
        if (vbr && kbps < 128)
        {
            return;
        }

        // Mono needs half the bitrate for the same quality: compare as if it were stereo.
        int stereoKbps = stats.Channels == 1 ? kbps * 2 : kbps;
        (string Codec, int Kbps, double KneeHz)[] rows = [.. Expected.Where(e => e.Codec == family)];
        int row = Array.FindIndex(rows, e => stereoKbps >= e.Kbps);
        if (row < 0)
        {
            return;
        }

        double margin = rules.Value("AUD-BITRATE-INFLATED", "marginHz");
        if (knee > rows[row].KneeHz - margin)
        {
            return;
        }

        int equivalent = Array.FindIndex(rows, e => knee >= e.KneeHz);
        if (equivalent < 0)
        {
            equivalent = rows.Length;
        }

        float confidence = equivalent - row >= 2 ? 0.80f : 0.60f;
        (string typical, _, _) = TypicalSource(knee);
        evidence["bitrateKbps"] = kbps;
        evidence["expectedKneeHz"] = rows[row].KneeHz;
        evidence["typicalSource"] = typical;
        string name = family switch { "mp3" => "MP3", "aac" => "AAC", "vorbis" => "Vorbis", _ => "Opus" };
        sink.Raise(
            "AUD-BITRATE-INFLATED",
            confidence >= 0.70f ? Severity.Medium : Severity.Low,
            confidence,
            kbps.ToString(Invariant) + " kbps " + name + " whose audio stops at " + Khz(knee) + ", typical of " + typical,
            evidence);
    }

    private static void PaddedBits(PcmStats stats, StreamInfo? audio, StructureArtifact? structure, FindingSink sink)
    {
        int container = structure?.Flac?.BitsPerSample ?? audio?.Bits ?? 0;
        if (!stats.Integral || container <= 16 || stats.EffectiveBits is <= 0 or > 16)
        {
            return;
        }

        sink.Raise(
            "AUD-PADDED-BITS",
            Severity.Medium,
            1f,
            container.ToString(Invariant) + "-bit file with " + stats.EffectiveBits.ToString(Invariant) + " significant bits",
            new Dictionary<string, object>(StringComparer.Ordinal) { ["containerBits"] = container, ["effectiveBits"] = stats.EffectiveBits });
    }

    private static void Clipping(PcmStats stats, bool lossless, RuleSettings rules, FindingSink sink)
    {
        ClippingStats clip = stats.Clipping;
        double minutes = Math.Max(1.0 / 60, stats.Seconds / 60);
        long overs = stats.Levels.Sum(l => l.Overs);
        if (!lossless && overs > 0)
        {
            sink.Raise(
                "AUD-OVERS",
                Severity.Low,
                1f,
                "decodes to " + (clip.PeakDb >= 0 ? "+" : string.Empty) + clip.PeakDb.ToString("0.0", Invariant) + " dBFS; will clip on playback without headroom",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["overs"] = overs, ["peakDb"] = Math.Round(clip.PeakDb, 2) });
        }

        if (stats.Integral)
        {
            double perMinute = clip.Runs / minutes;
            double percent = 100.0 * clip.ClippedSamples / Math.Max(1, stats.Seconds * stats.SampleRate);
            Severity? tier = null;
            if (perMinute >= rules.Value("AUD-CLIPPING", "highPerMinute") || percent >= rules.Value("AUD-CLIPPING", "highPercent") || clip.LongestRun >= rules.Value("AUD-CLIPPING", "highRunSamples"))
            {
                tier = Severity.High;
            }
            else if (perMinute >= rules.Value("AUD-CLIPPING", "mediumPerMinute") || clip.LongestRun >= rules.Value("AUD-CLIPPING", "mediumRunSamples"))
            {
                tier = Severity.Medium;
            }
            else if (clip.Runs >= rules.Value("AUD-CLIPPING", "minRuns") && perMinute >= rules.Value("AUD-CLIPPING", "lowPerMinute"))
            {
                tier = Severity.Low;
            }

            if (tier is Severity severity)
            {
                sink.Raise(
                    "AUD-CLIPPING",
                    severity,
                    0.9f,
                    Math.Round(perMinute).ToString("N0", Invariant) + " clipped peaks per minute",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["runs"] = clip.Runs,
                        ["runsPerMinute"] = Math.Round(perMinute, 1),
                        ["clippedPercent"] = Math.Round(percent, 3),
                        ["longestRun"] = clip.LongestRun,
                        ["peakDb"] = Math.Round(clip.PeakDb, 2),
                        ["positionsSeconds"] = clip.Positions,
                    });
            }

            return;
        }

        // Lossy and float sources smear flat tops: the evidence is indirect, so never High.
        double crowding = clip.CrowdingRatio * 100;
        Severity? lossy = crowding >= rules.Value("AUD-CLIPPING", "lossyMediumPercent") ? Severity.Medium
            : crowding >= rules.Value("AUD-CLIPPING", "lossyLowPercent") ? Severity.Low
            : null;
        if (lossy is Severity s && clip.PeakDb >= -1)
        {
            sink.Raise(
                "AUD-CLIPPING",
                s,
                0.6f,
                crowding.ToString("0.00", Invariant) + " % of samples crowd the peak",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["crowdingPercent"] = Math.Round(crowding, 3), ["peakDb"] = Math.Round(clip.PeakDb, 2) });
        }
    }

    private static void Signal(PcmStats stats, RuleSettings rules, FindingSink sink)
    {
        double peakDb = stats.PeakDb;
        double rmsDb = stats.RmsDb;
        if (peakDb < rules.Value("AUD-SILENT", "peakDb") || rmsDb < rules.Value("AUD-SILENT", "rmsDb"))
        {
            sink.Raise(
                "AUD-SILENT",
                Severity.High,
                1f,
                "peak " + peakDb.ToString("0", Invariant) + " dBFS over the whole track",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["peakDb"] = Math.Round(peakDb, 1), ["rmsDb"] = Math.Round(rmsDb, 1) });
            return;
        }

        double longSilence = rules.Value("AUD-LONG-SILENCE", "seconds");
        double silence = Math.Max(stats.LeadingSilenceSeconds, stats.TrailingSilenceSeconds);
        if (silence >= longSilence)
        {
            bool leading = stats.LeadingSilenceSeconds >= stats.TrailingSilenceSeconds;
            sink.Raise(
                "AUD-LONG-SILENCE",
                Severity.Low,
                1f,
                Math.Round(silence).ToString(Invariant) + " s of digital silence at the " + (leading ? "start" : "end"),
                new Dictionary<string, object>(StringComparer.Ordinal) { ["leadingSeconds"] = Math.Round(stats.LeadingSilenceSeconds, 1), ["trailingSeconds"] = Math.Round(stats.TrailingSilenceSeconds, 1) });
        }

        double low = rules.Value("AUD-DC", "lowLevel");
        double medium = rules.Value("AUD-DC", "mediumLevel");
        double share = rules.Value("AUD-DC", "sameSignPercent") / 100;
        ChannelLevels? dc = stats.Levels.Where(l => Math.Abs(l.Mean) >= low && l.DcSameSignShare >= share).MaxBy(l => Math.Abs(l.Mean));
        if (dc is not null)
        {
            sink.Raise(
                "AUD-DC",
                Math.Abs(dc.Mean) >= medium ? Severity.Medium : Severity.Low,
                0.9f,
                "offset of " + (Math.Abs(dc.Mean) * 100).ToString("0.0", Invariant) + " %",
                new Dictionary<string, object>(StringComparer.Ordinal) { ["mean"] = Math.Round(dc.Mean, 5), ["sameSignShare"] = Math.Round(dc.DcSameSignShare, 2) });
        }

        if (stats.Channels >= 2)
        {
            double silent = rules.Value("AUD-DEAD-CHANNEL", "silentDb");
            double active = rules.Value("AUD-DEAD-CHANNEL", "activeDb");
            double[] levels = [.. stats.Levels.Select(l => 20 * Math.Log10(Math.Max(1e-12, l.Rms)))];
            for (int c = 0; c < levels.Length; c++)
            {
                bool lfe = stats.Channels is 6 or 8 && c == 3;
                if (lfe || levels[c] >= silent || !levels.Where((_, i) => i != c).Any(l => l > active))
                {
                    continue;
                }

                sink.Raise(
                    "AUD-DEAD-CHANNEL",
                    stats.Channels == 2 ? Severity.Medium : Severity.Low,
                    1f,
                    (stats.Channels == 2 ? (c == 0 ? "left" : "right") : "channel " + (c + 1).ToString(Invariant)) + " channel is silent",
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["channel"] = c, ["rmsDb"] = Math.Round(levels[c], 1) });
                break;
            }

            if (stats.Correlation < -rules.Value("AUD-PHASE", "correlationPercent") / 100)
            {
                sink.Raise(
                    "AUD-PHASE",
                    Severity.Medium,
                    0.85f,
                    "left and right correlate at " + stats.Correlation.ToString("0.00", Invariant),
                    new Dictionary<string, object>(StringComparer.Ordinal) { ["correlation"] = Math.Round(stats.Correlation, 3) });
            }

            if (stats.IdenticalChannels && stats.Channels == 2)
            {
                sink.Raise("AUD-DUAL-MONO", Severity.Low, 1f, "left and right are identical", new Dictionary<string, object>(StringComparer.Ordinal));
            }
        }
    }

    private static void Dropouts(PcmStats stats, FindingSink sink)
    {
        DropoutStats dropouts = stats.Dropouts;
        if (dropouts.Gaps == 0)
        {
            return;
        }

        Dictionary<string, object> evidence = new(StringComparer.Ordinal)
        {
            ["gaps"] = dropouts.Gaps,
            ["alignedGaps"] = dropouts.AlignedGaps,
            ["positionsSeconds"] = dropouts.Positions,
            ["lengthsSamples"] = dropouts.Lengths,
        };
        string summary = EvidenceText.Plural(dropouts.Gaps, "gap", "gaps") + " of digital silence inside the audio";
        if (dropouts.AlignedGaps > 0)
        {
            sink.Raise("COR-DROPOUT", dropouts.AlignedGaps >= 3 ? Severity.High : Severity.Medium, 0.9f, summary, evidence);
        }
        else
        {
            sink.Raise("COR-DROPOUT", Severity.Low, 0.5f, summary, evidence);
        }
    }

    /// <summary>COR-CLICKS (experimental, disabled by default): isolated sample-level jumps.</summary>
    private static void Clicks(PcmStats stats, RuleSettings rules, FindingSink sink)
    {
        if (stats.Clicks is not { } clicks || clicks.Clicks < rules.Value("COR-CLICKS", "lowClicks"))
        {
            return;
        }

        Dictionary<string, object> evidence = new(StringComparer.Ordinal)
        {
            ["clicks"] = clicks.Clicks,
            ["positionsSeconds"] = clicks.Positions,
        };
        Severity severity = clicks.Clicks >= rules.Value("COR-CLICKS", "mediumClicks") ? Severity.Medium : Severity.Low;
        sink.Raise("COR-CLICKS", severity, 0.5f, EvidenceText.Plural(clicks.Clicks, "click", "clicks") + " far above the surrounding sound", evidence);
    }
}

/// <summary>Low bitrate from the probe alone (subplan 10, AUD-LOW-BITRATE; Quick depth).</summary>
public sealed class LowBitrateDetector : IDetector
{
    public IReadOnlyList<string> RuleIds { get; } = ["AUD-LOW-BITRATE"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(ProbeArtifact)];

    public ScanDepth MinDepth => ScanDepth.Quick;

    public bool Applies(DetectorInput input) => input.Kind == MediaKind.Audio;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        ProbeArtifact probe = input.Get<ProbeArtifact>()!;
        if (probe is not { Opened: true } || probe.Audio is not StreamInfo audio || probe.Video is not null)
        {
            return;
        }

        (string? key, string name) = audio.Codec?.ToLowerInvariant() switch
        {
            "mp3" or "mp3float" => ((string?)"mp3Kbps", "MP3"),
            "aac" when audio.Profile?.StartsWith("HE", StringComparison.OrdinalIgnoreCase) == true => ("heAacKbps", "HE-AAC"),
            "aac" => ("aacKbps", "AAC"),
            "vorbis" => ("vorbisKbps", "Vorbis"),
            "opus" => ("opusKbps", "Opus"),
            "wmav1" or "wmav2" => ("wmaKbps", "WMA"),
            _ => (null, string.Empty),
        };
        long? bps = audio.BitRate ?? probe.BitRate;
        if (key is null || bps is not long bitrate || bitrate <= 0)
        {
            return;
        }

        double threshold = input.Settings.Value("AUD-LOW-BITRATE", key) / (audio.Channels == 1 ? 2 : 1);
        double kbps = bitrate / 1000.0;
        if (kbps < threshold)
        {
            sink.Raise(
                "AUD-LOW-BITRATE",
                Severity.Low,
                1f,
                Math.Round(kbps).ToString(CultureInfo.InvariantCulture) + " kbps " + name,
                new Dictionary<string, object>(StringComparer.Ordinal) { ["bitrateKbps"] = Math.Round(kbps), ["thresholdKbps"] = threshold });
        }
    }
}
