using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis.Providers;

/// <summary>Photo decode result (subplan 07, Photos — Standard and Deep).</summary>
public sealed record ImageDecodeArtifact(
    RunOutcome Outcome,
    LogDigest Log,
    int? FramesRead,
    int? Width,
    int? Height,
    string? Codec,
    string? FormatName,
    bool Opened) : IArtifact;

/// <summary>
/// Decodes a photo fully with one ffprobe run (<c>-count_frames</c>), which also yields the probe
/// (task C-08). The demuxer comes from the sniffed signature so names like <c>a%03d.jpg</c> work.
/// </summary>
public sealed partial class ImageDecodeProvider() : ArtifactProvider<ImageDecodeArtifact>(new ArtifactCodec<ImageDecodeArtifact>(ArtifactKind.Decode, ProviderJsonContext.Default.ImageDecodeArtifact))
{
    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Standard;

    public override bool Applies(FileContext file) => file.Kind == MediaKind.Photo && !MediaClassifier.IsRaw(file.Extension);

    protected override async Task<ImageDecodeArtifact> ProduceTypedAsync(FileContext file)
    {
        string? sniffed = await StructureProvider.SniffedImageFormatAsync(file).ConfigureAwait(false);
        List<string> args = ["-err_detect", "crccheck+bitstream+buffer"];
        args.AddRange(FfmpegArgs.Input(file.FullPath, MediaKind.Photo, sniffed).SkipLast(2));
        args.AddRange(["-count_frames", "-show_entries", "stream=codec_name,width,height,pix_fmt,nb_read_frames:format=format_name", "-of", "json", "-i", file.FullPath]);
        FfResult result = await file.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffprobe,
                Arguments = args,
                Stdout = StdoutMode.Text,
                WorkingDirectory = file.TempDirectory,
                IdleTimeout = TimeSpan.FromSeconds(file.Settings.StallSeconds),
                HardTimeout = FfmpegArgs.HardTimeout(ScanDepth.Standard, null, file.Stat.Size),
            },
            file.Cancellation).ConfigureAwait(false);
        int? frames = null;
        int? width = null;
        int? height = null;
        string? codec = null;
        string? format = null;
        bool opened = false;
        if (!string.IsNullOrWhiteSpace(result.StdoutText))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(result.StdoutText);
                if (json.RootElement.TryGetProperty("streams", out JsonElement streams) && streams.GetArrayLength() > 0)
                {
                    JsonElement first = streams[0];
                    codec = Text(first, "codec_name");
                    width = Int(first, "width");
                    height = Int(first, "height");
                    frames = Int(first, "nb_read_frames");
                    opened = codec is not null;
                }

                if (json.RootElement.TryGetProperty("format", out JsonElement formatElement))
                {
                    format = Text(formatElement, "format_name");
                }
            }
            catch (JsonException)
            {
            }
        }

        return new ImageDecodeArtifact(OutcomeMapping.From(result.Outcome), LogDigest.From(result.Log), frames, width, height, codec, format, opened && !result.Log.Has(LogSignalKind.OpenFailure));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is string s && s != "N/A" ? s : null;

    private static int? Int(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        string? text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }
}

public enum VideoPass : byte
{
    Standard = 0,
    Deep = 1,
}

/// <summary>Video decode result (subplan 07, Video — Standard and Deep), with counts split per stream.</summary>
public sealed record VideoDecodeArtifact(
    VideoPass Pass,
    RunOutcome Outcome,
    bool IdleTimeout,
    LogDigest Video,
    LogDigest Audio,
    LogDigest Container,
    double? DecodedSeconds,
    double? DeclaredSeconds,
    bool HasAudio,
    long? FramesDecoded,
    long? FramesDeclared,
    byte[]? StreamHashBytes,
    double LongestFreezeSeconds,
    int Freezes,
    double LongestBlackSeconds,
    int BlackSections,
    bool Opened) : IArtifact, IStreamHashSource
{
    [JsonIgnore]
    public byte[]? StreamHash => StreamHashBytes;

    [JsonIgnore]
    public SignalCounts All => Sum(Sum(Video.Counts, Audio.Counts), Container.Counts);

    public static SignalCounts Sum(SignalCounts a, SignalCounts b) => new(
        a.OpenFailure + b.OpenFailure, a.Truncation + b.Truncation, a.DecodeError + b.DecodeError, a.MinorDecode + b.MinorDecode,
        a.Concealed + b.Concealed, a.Checksum + b.Checksum, a.Timestamp + b.Timestamp, a.Unsupported + b.Unsupported, a.Unknown + b.Unknown);
}

/// <summary>
/// Standard pass: demux the whole file, decode key frames and all audio, compute the stream hash in the
/// same process (task C-09). Deep pass: decode every frame with freeze and black detection (task C-10).
/// </summary>
public sealed partial class VideoDecodeProvider() : ArtifactProvider<VideoDecodeArtifact>(new ArtifactCodec<VideoDecodeArtifact>(ArtifactKind.VideoDecode, ProviderJsonContext.Default.VideoDecodeArtifact))
{
    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Standard;

    public override bool Applies(FileContext file) => file.Kind == MediaKind.Video;

    protected override async Task<VideoDecodeArtifact> ProduceTypedAsync(FileContext file)
    {
        ProbeArtifact? probe = await file.GetAsync<ProbeArtifact>().ConfigureAwait(false);
        bool deep = file.Depth == ScanDepth.Deep;
        bool hasVideo = probe?.Video is not null;
        bool hasAudio = probe?.Audio is not null;
        int threads = deep ? 8 : 4;
        List<string> args = ["-progress", "pipe:1", "-nostats", "-stats_period", "0.5"];
        args.AddRange(FfmpegArgs.Decode(deep && file.Settings.StrictDecoding));
        args.AddRange(["-threads", threads.ToString(CultureInfo.InvariantCulture)]);
        if (!deep)
        {
            args.AddRange(["-skip_frame", "nokey"]);
        }

        args.AddRange(["-i", file.FullPath]);
        string videoMap = hasVideo ? "0:v:0" : "0:v?";
        args.AddRange(["-map", videoMap, "-map", "0:a?"]);
        if (deep && hasVideo)
        {
            args.AddRange(["-vf", "freezedetect=n=0.003:d=10,blackdetect=d=30:pix_th=0.10"]);
        }

        args.AddRange(["-f", "null", "-"]);
        if (!deep)
        {
            args.AddRange(["-map", videoMap, "-map", "0:a:0?", "-c", "copy", "-f", "streamhash", "-hash", "md5", "streamhash.txt"]);
        }

        HashSet<string> videoCodecs = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> audioCodecs = new(StringComparer.OrdinalIgnoreCase);
        foreach (StreamInfo stream in probe?.Streams ?? [])
        {
            if (stream.Codec is null)
            {
                continue;
            }

            (stream.IsVideo ? videoCodecs : stream.IsAudio ? audioCodecs : null)?.Add(stream.Codec);
        }

        SignalCounts video = SignalCounts.Zero;
        SignalCounts audio = SignalCounts.Zero;
        SignalCounts container = SignalCounts.Zero;
        double longestFreeze = 0;
        int freezes = 0;
        double longestBlack = 0;
        int blacks = 0;
        double? declared = probe?.Duration ?? probe?.Video?.Duration;
        FfResult result = await file.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = args,
                Stdout = StdoutMode.Progress,
                WorkingDirectory = file.TempDirectory,
                LogLevel = deep ? "info" : "warning",
                IdleTimeout = TimeSpan.FromSeconds(file.Settings.StallSeconds),
                HardTimeout = FfmpegArgs.HardTimeout(file.Depth, declared, file.Stat.Size),
                OnLogLine = line =>
                {
                    if (deep)
                    {
                        Match freeze = FreezeDuration().Match(line.Raw);
                        if (freeze.Success && double.TryParse(freeze.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                        {
                            freezes++;
                            longestFreeze = Math.Max(longestFreeze, seconds);
                        }

                        Match black = BlackDuration().Match(line.Raw);
                        if (black.Success && double.TryParse(black.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double blackSeconds))
                        {
                            blacks++;
                            longestBlack = Math.Max(longestBlack, blackSeconds);
                        }
                    }

                    if (line.Kind is LogSignalKind.Noise or LogSignalKind.Benign)
                    {
                        return;
                    }

                    switch (StreamOf(line, videoCodecs, audioCodecs))
                    {
                        case "video":
                            video = video.Add(line.Kind);
                            break;
                        case "audio":
                            audio = audio.Add(line.Kind);
                            break;
                        default:
                            container = container.Add(line.Kind);
                            break;
                    }
                },
            },
            file.Cancellation).ConfigureAwait(false);

        byte[]? streamHash = null;
        string hashFile = Path.Combine(file.TempDirectory, "streamhash.txt");
        if (!deep && result.Completed && File.Exists(hashFile))
        {
            streamHash = OutcomeMapping.StreamHash(await File.ReadAllTextAsync(hashFile, file.Cancellation).ConfigureAwait(false));
        }

        // Per-stream digests keep the top messages of the whole log for the evidence table.
        LogDigest all = LogDigest.From(result.Log);
        return new VideoDecodeArtifact(
            deep ? VideoPass.Deep : VideoPass.Standard,
            OutcomeMapping.From(result.Outcome),
            result.IdleTimeout,
            all with { Counts = video },
            new LogDigest(audio, [], null),
            new LogDigest(container, [], null),
            result.FinalProgress?.OutTimeSeconds,
            declared,
            hasAudio,
            result.FinalProgress?.Frame,
            probe?.Video?.Frames,
            streamHash,
            longestFreeze,
            freezes,
            longestBlack,
            blacks,
            probe?.Opened == true && !result.Log.Has(LogSignalKind.OpenFailure));
    }

    /// <summary>Which stream a log line is about: input-stream contexts first, then the decoder's codec name.</summary>
    internal static string StreamOf(LogLine line, IReadOnlySet<string> videoCodecs, IReadOnlySet<string> audioCodecs)
    {
        foreach (string context in line.Contexts)
        {
            if (context.StartsWith("vist#", StringComparison.Ordinal))
            {
                return "video";
            }

            if (context.StartsWith("aist#", StringComparison.Ordinal))
            {
                return "audio";
            }
        }

        foreach (string context in line.Contexts)
        {
            string name = context.StartsWith("dec:", StringComparison.Ordinal) ? context[4..] : context;
            if (videoCodecs.Any(c => name.StartsWith(c, StringComparison.OrdinalIgnoreCase)))
            {
                return "video";
            }

            if (audioCodecs.Any(c => name.StartsWith(c, StringComparison.OrdinalIgnoreCase)))
            {
                return "audio";
            }
        }

        return "container";
    }

    [GeneratedRegex(@"freezedetect\.freeze_duration:\s*([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FreezeDuration();

    [GeneratedRegex(@"black_duration:\s*([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BlackDuration();
}
