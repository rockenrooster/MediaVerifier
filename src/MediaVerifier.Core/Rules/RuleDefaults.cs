using System.Globalization;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Core.Rules;

/// <summary>A tunable threshold: its default, allowed range and how Settings → Rules labels it.</summary>
public sealed record RuleParameter(
    string RuleId,
    string Name,
    double Default,
    double Min,
    double Max,
    string Label,
    string Unit,
    int Decimals = 0)
{
    public string Key => RuleId + "." + Name;

    public double Clamp(double value) => Math.Clamp(value, Min, Max);

    public string Format(double value) => value.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
}

/// <summary>
/// Every numeric threshold in one place (D-13), keyed <c>ruleId.paramName</c>. Detectors read values
/// through <see cref="RuleSettings"/>, which applies the user's overrides clamped to these ranges.
/// Values marked *calibrate* in the subplans start here and are updated by calibration (subplan 15).
/// </summary>
public static class RuleDefaults
{
    private static readonly Dictionary<string, RuleParameter> ByKey;

    static RuleDefaults()
    {
        All =
        [
            // General and integrity (subplans 02, 05, 06).
            new("GEN-ZEROFILL", "criticalPercent", 50, 1, 100, "Critical when this share is zeros", "%"),
            new("GEN-PATH", "maxPathLength", 259, 100, 32767, "Longest path without a warning", "characters"),
            new("RUN-MASS-MISSING", "percent", 20, 1, 100, "Alert when this share of a folder disappears", "%"),
            new("RUN-MASS-MISSING", "minFiles", 50, 1, 100000, "and at least this many files", "files"),

            // Corruption (subplan 07).
            new("COR-TRUNCATED", "criticalPercent", 50, 5, 100, "Critical when this share is missing", "%"),
            new("COR-TRUNCATED", "audioSeconds", 0.5, 0.1, 60, "Audio: allowed shortfall", "seconds", 1),
            new("COR-TRUNCATED", "audioPercent", 1, 0.1, 50, "or share of the length", "%", 1),
            new("COR-TRUNCATED", "mp3EstimatePercent", 3, 0.5, 50, "MP3 without a length header: allowed shortfall", "%", 1),
            new("COR-TRUNCATED", "videoSeconds", 2, 0.5, 600, "Video: allowed shortfall", "seconds", 1),
            new("COR-TRUNCATED", "videoPercent", 2, 0.1, 50, "or share of the length", "%", 1),
            new("COR-TRUNCATED", "keyFrameSeconds", 12, 1, 600, "Video without sound at Standard depth: allowed shortfall", "seconds", 1),
            new("COR-TRUNCATED", "secondsPerError", 1.5, 0.1, 60, "Audio lost mid-file that each decoder error explains", "seconds", 1),
            new("COR-DECODE", "highErrors", 5, 1, 100000, "High from this many errors", "errors"),
            new("COR-DECODE", "audioHighSeconds", 0.5, 0.1, 600, "Audio: High when this much is lost", "seconds", 1),
            new("COR-DECODE", "videoHighSeconds", 2, 0.1, 600, "Video: High when this much is lost", "seconds", 1),
            new("COR-DECODE", "frameShortfall", 2, 1, 100000, "Deep: count missing frames above", "frames"),
            new("COR-DECODE", "frameShortfallPercent", 0.1, 0.01, 50, "and above this share of the frames", "%", 2),
            new("COR-CONCEALED", "highFrames", 10, 1, 100000, "High from this many damaged frames", "frames"),

            // Structure (subplans 02, 07).
            new("COR-TIMESTAMPS", "minCount", 10, 1, 100000, "Report from this many timestamp errors", "errors"),
            new("COR-TIMESTAMPS", "mediumCount", 100, 1, 1000000, "Medium from this many", "errors"),
            new("COR-STREAM-LENGTH", "seconds", 2, 0.5, 600, "Allowed length difference", "seconds", 1),
            new("COR-STREAM-LENGTH", "percent", 2, 0.1, 50, "or share of the length", "%", 1),
            new("COR-STREAM-LENGTH", "mediumPercent", 10, 1, 100, "Medium above this difference", "%", 1),

            // Audio quality (subplan 10); *calibrate*.
            new("AUD-CLIPPING", "lowPerMinute", 5, 1, 10000, "Low from", "clipped peaks per minute"),
            new("AUD-CLIPPING", "mediumPerMinute", 60, 1, 10000, "Medium from", "clipped peaks per minute"),
            new("AUD-CLIPPING", "highPerMinute", 600, 1, 100000, "High from", "clipped peaks per minute"),
            new("AUD-CLIPPING", "minRuns", 10, 1, 100000, "Report from this many clipped peaks", "peaks"),
            new("AUD-CLIPPING", "mediumRunSamples", 16, 3, 100000, "Medium from a flat run of", "samples"),
            new("AUD-CLIPPING", "highRunSamples", 64, 3, 100000, "High from a flat run of", "samples"),
            new("AUD-CLIPPING", "highPercent", 0.05, 0.001, 100, "High from this share of clipped samples", "%", 3),
            new("AUD-CLIPPING", "lossyLowPercent", 0.05, 0.001, 100, "Lossy files: Low from this share at the peak", "%", 3),
            new("AUD-CLIPPING", "lossyMediumPercent", 0.5, 0.001, 100, "Lossy files: Medium from this share at the peak", "%", 3),
            new("AUD-FAKE-LOSSLESS", "minDropDb", 30, 10, 120, "Hard cutoff from this drop", "dB"),
            new("AUD-FAKE-LOSSLESS", "likelyKneeHz", 19200, 8000, 22000, "Likely lossy below", "Hz"),
            new("AUD-FAKE-LOSSLESS", "possibleKneeHz", 20600, 8000, 22050, "Possibly lossy below", "Hz"),
            new("AUD-UPSAMPLED", "maxKneeHz", 24500, 20600, 48000, "Upsampled when content stops below", "Hz"),
            new("AUD-BITRATE-INFLATED", "marginHz", 1500, 0, 10000, "Report when the cutoff is this far below the expected one", "Hz"),
            new("AUD-LOW-BITRATE", "mp3Kbps", 128, 8, 320, "MP3 below", "kbps"),
            new("AUD-LOW-BITRATE", "aacKbps", 96, 8, 320, "AAC below", "kbps"),
            new("AUD-LOW-BITRATE", "heAacKbps", 48, 8, 320, "HE-AAC below", "kbps"),
            new("AUD-LOW-BITRATE", "vorbisKbps", 96, 8, 500, "Vorbis below", "kbps"),
            new("AUD-LOW-BITRATE", "opusKbps", 64, 6, 510, "Opus below", "kbps"),
            new("AUD-LOW-BITRATE", "wmaKbps", 128, 8, 320, "WMA below", "kbps"),
            new("AUD-DC", "lowLevel", 0.01, 0.001, 0.5, "Low from an offset of", "of full scale", 3),
            new("AUD-DC", "mediumLevel", 0.05, 0.001, 0.5, "Medium from an offset of", "of full scale", 3),
            new("AUD-DC", "sameSignPercent", 90, 50, 100, "Offset has the same sign in this share of seconds", "%"),
            new("AUD-DEAD-CHANNEL", "silentDb", -80, -150, -20, "A channel counts as silent below", "dBFS"),
            new("AUD-DEAD-CHANNEL", "activeDb", -50, -100, 0, "while another is above", "dBFS"),
            new("AUD-PHASE", "correlationPercent", 90, 50, 100, "Report when left and right correlate below minus", "%"),
            new("AUD-SILENT", "peakDb", -60, -150, -10, "Silent when the peak stays below", "dBFS"),
            new("AUD-SILENT", "rmsDb", -70, -150, -10, "or the average level stays below", "dBFS"),
            new("AUD-LONG-SILENCE", "seconds", 10, 1, 600, "Report silence at the start or end from", "seconds"),
            new("COR-CLICKS", "lowClicks", 3, 1, 1000, "Report from", "clicks"),
            // Calibrated on real films (docs/calibration/real-material.md): soft natives reach 0.988 on detailed frames.
            new("VID-UPSCALED", "ssim", 0.990, 0.9, 0.999, "Rebuilt picture at least this similar (SSIM)", "", 3),
            new("VID-UPSCALED", "minSpatialInfo", 40, 0, 200, "Use frames with at least this much detail", "SI"),
            new("VID-STARVED", "lowBpp", 0.035, 0.001, 1, "Low below", "bits per pixel", 3),
            new("VID-STARVED", "mediumBpp", 0.020, 0.001, 1, "Medium below", "bits per pixel", 3),
            new("VID-BLOCKY", "low", 2.6, 0.5, 20, "Low from a block score of", "", 1),
            new("VID-BLOCKY", "medium", 3.4, 0.5, 20, "Medium from a block score of", "", 1),
            new("VID-BLOCKY", "blur", 8, 1, 50, "or blur score of", "", 1),
            new("VID-INFLATED", "bpp", 0.20, 0.01, 5, "High bitrate from", "bits per pixel", 2),
            new("VID-INTERLACE", "combedPercent", 70, 10, 100, "Combing: interlaced frames from", "%"),
            new("VID-INTERLACE", "progressivePercent", 90, 10, 100, "Wrong flag: progressive frames from", "%"),
            new("VID-INTERLACE", "minFrames", 100, 10, 600, "Judge only with at least", "frames"),
            new("VID-FROZEN", "seconds", 10, 2, 600, "Report a still picture from", "seconds"),
            new("VID-FROZEN", "edgeSeconds", 60, 0, 600, "Ignore the first and last", "seconds"),
            new("VID-FROZEN", "mediumPercent", 10, 1, 100, "Medium from this share of the length", "%"),
            new("VID-AUDIO-SILENT", "peakDb", -60, -150, -10, "Silent when the peak stays below", "dBFS"),
            // Calibrated on real photos: high-resolution and shallow-focus natives reach 0.995.
            new("IMG-UPSCALED", "ssim", 0.996, 0.9, 0.999, "Rebuilt image at least this similar (SSIM)", "", 3),
            new("IMG-UPSCALED", "minSpatialInfo", 25, 0, 200, "Skip images with less detail than", "SI"),
            new("IMG-HEAVY-COMPRESSION", "lowQuality", 50, 1, 100, "Low below JPEG quality", ""),
            new("IMG-HEAVY-COMPRESSION", "mediumQuality", 30, 1, 100, "Medium below JPEG quality", ""),
            new("IMG-LOW-RES", "megapixels", 0.3, 0.01, 10, "Below", "megapixels", 2),
            new("COR-CLICKS", "mediumClicks", 21, 2, 10000, "Medium from", "clicks"),
        ];
        ByKey = All.ToDictionary(p => p.Key, StringComparer.Ordinal);
    }

    public static IReadOnlyList<RuleParameter> All { get; }

    public static IEnumerable<RuleParameter> ForRule(string ruleId) => All.Where(p => p.RuleId == ruleId);

    public static RuleParameter Get(string ruleId, string name) =>
        ByKey.TryGetValue(ruleId + "." + name, out RuleParameter? parameter)
            ? parameter
            : throw new KeyNotFoundException($"Unknown rule parameter '{ruleId}.{name}'.");

    public static bool TryGet(string key, out RuleParameter parameter) => ByKey.TryGetValue(key, out parameter!);

    /// <summary>The value to use: the override when given (clamped and logged if out of range), else the default.</summary>
    public static double Resolve(RuleParameter parameter, double? overrideValue)
    {
        if (overrideValue is not double value || double.IsNaN(value))
        {
            return parameter.Default;
        }

        double clamped = parameter.Clamp(value);
        if (clamped != value)
        {
            AppLog.Shared?.Warning(string.Create(CultureInfo.InvariantCulture, $"Rule setting {parameter.Key} = {value} is outside {parameter.Min}..{parameter.Max}; using {clamped}"));
        }

        return clamped;
    }
}
