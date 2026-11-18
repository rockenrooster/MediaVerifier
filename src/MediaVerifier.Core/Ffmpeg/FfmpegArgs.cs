using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Ffmpeg;

/// <summary>Argument building blocks (subplan 04, Standard arguments and Input-path rules).</summary>
public static class FfmpegArgs
{
    /// <summary>
    /// Prepended to every invocation. <c>-nostdin</c> is an ffmpeg option only: ffprobe rejects it
    /// (verified on 8.0.1; see the deviations log).
    /// </summary>
    public static IReadOnlyList<string> Standard(FfTool tool, string logLevel = "warning") => tool == FfTool.Ffmpeg
        ? ["-hide_banner", "-nostdin", "-loglevel", "repeat+level+" + logLevel, "-protocol_whitelist", "file,pipe"]
        : ["-hide_banner", "-loglevel", "repeat+level+" + logLevel, "-protocol_whitelist", "file,pipe"];

    /// <summary>
    /// The decoder checks for every decode, ffmpeg and ffprobe alike (subplan 04): CRC checks are off in FFmpeg
    /// by default. <c>buffer</c> is left out on purpose: with it the MP3 decoder reports each granule that ends
    /// with legal stuffing bits as <c>bits_left=N</c> and silences it (deviations log).
    /// </summary>
    public static string ErrorDetection(bool strict = false) => strict ? "crccheck+bitstream+careful+compliant" : "crccheck+bitstream";

    /// <summary>For ffmpeg commands that decode: report every error instead of stopping at the first ones.</summary>
    public static IReadOnlyList<string> Decode(bool strict = false) => ["-max_error_rate", "1", "-err_detect", ErrorDetection(strict)];

    /// <summary>
    /// The input arguments for a file. Photos get an explicit demuxer from their sniffed format so that
    /// <c>%d</c> in a name is never read as a sequence pattern; an unknown image type whose name holds
    /// <c>%</c> uses <c>-f image2 -pattern_type none</c>. Other media take the path as is.
    /// </summary>
    public static IReadOnlyList<string> Input(string path, MediaKind kind, string? sniffedImageFormat)
    {
        if (kind == MediaKind.Photo)
        {
            string? demuxer = PhotoDemuxer(sniffedImageFormat);
            if (demuxer is not null)
            {
                return ["-f", demuxer, "-i", path];
            }

            if (Path.GetFileName(path).Contains('%', StringComparison.Ordinal))
            {
                return ["-f", "image2", "-pattern_type", "none", "-i", path];
            }
        }

        return ["-i", path];
    }

    /// <summary>The single-image demuxer for a sniffed format name (see the signature sniffer, subplan 07).</summary>
    public static string? PhotoDemuxer(string? format) => format?.ToLowerInvariant() switch
    {
        "jpeg" => "jpeg_pipe",
        "png" => "png_pipe",
        "webp" => "webp_pipe",
        "bmp" => "bmp_pipe",
        "tiff" => "tiff_pipe",
        "gif" => "gif",
        "jpegxl" => "jpegxl_pipe",

        // HEIF and AVIF are ISO-BMFF files (mov demuxer); JPEG 2000 and RAW are left to probing.
        _ => null,
    };

    /// <summary>
    /// The hard timeout (subplan 04): max(120 s, 4 × duration) at Standard, max(600 s, 20 × duration) at
    /// Deep; unknown duration allows 120 s plus 1 s per MiB.
    /// </summary>
    public static TimeSpan HardTimeout(ScanDepth depth, double? durationSeconds, long sizeBytes)
    {
        if (durationSeconds is double seconds && seconds > 0 && double.IsFinite(seconds))
        {
            return depth == ScanDepth.Deep
                ? TimeSpan.FromSeconds(Math.Max(600, 20 * seconds))
                : TimeSpan.FromSeconds(Math.Max(120, 4 * seconds));
        }

        return TimeSpan.FromSeconds(120 + (sizeBytes / (1024.0 * 1024.0)));
    }
}
