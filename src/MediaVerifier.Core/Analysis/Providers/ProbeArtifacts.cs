using System.Text.Json.Serialization;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis.Providers;

/// <summary>One stream as stored (subplan 04 probe model, compact).</summary>
public sealed record StreamInfo(
    int Index,
    string Type,
    string? Codec,
    string? Profile,
    int? Width,
    int? Height,
    string? PixFmt,
    string? FieldOrder,
    double? FrameRate,
    long? Frames,
    int? SampleRate,
    int? Channels,
    string? SampleFmt,
    int? Bits,
    double? Duration,
    long? BitRate,
    string? ColorTransfer,
    string? ColorPrimaries,
    string? ColorSpace,
    bool AttachedPicture,
    bool Default)
{
    [JsonIgnore]
    public bool IsVideo => Type == "video" && !AttachedPicture;

    [JsonIgnore]
    public bool IsAudio => Type == "audio";

    public static StreamInfo From(ProbeStream s) => new(
        s.Index, s.CodecType, s.CodecName, s.Profile, s.Width, s.Height, s.PixFmt, s.FieldOrder,
        s.AvgFrameRate is Rational avg && avg.Value > 0 ? avg.Value : s.RFrameRate?.Value,
        s.NbFrames, s.SampleRate, s.Channels, s.SampleFmt, s.BitsPerRawSample, s.DurationSeconds, s.BitRate,
        s.ColorTransfer, s.ColorPrimaries, s.ColorSpace, s.IsAttachedPicture, s.IsDefault);
}

/// <summary>The stored probe (task FF-05 / subplan 07 artifacts table).</summary>
public sealed record ProbeArtifact(
    bool Opened,
    bool Unrecognised,
    string? FormatName,
    int ProbeScore,
    double? Duration,
    long? BitRate,
    long? Size,
    StreamInfo[] Streams,
    LogDigest Log,
    RunOutcome Outcome) : IArtifact, IMediaSummarySource, IUnsupportedSource
{
    [JsonIgnore]
    public StreamInfo? Video => Streams.FirstOrDefault(s => s.IsVideo);

    [JsonIgnore]
    public StreamInfo? Audio => Streams.FirstOrDefault(s => s.IsAudio && s.Default) ?? Streams.FirstOrDefault(s => s.IsAudio);

    [JsonIgnore]
    public MediaSummary? Summary
    {
        get
        {
            if (!Opened)
            {
                return null;
            }

            StreamInfo? video = Video ?? Streams.FirstOrDefault(s => s.Type == "video");
            StreamInfo? audio = Audio;
            int? bits = audio?.Bits is int b && b > 0 ? b : audio?.SampleFmt switch { "s16" or "s16p" => 16, "u8" or "u8p" => 8, _ => null };
            return new MediaSummary(
                FormatName?.Split(',')[0],
                Video?.Codec,
                audio?.Codec,
                video?.Width,
                video?.Height,
                Duration is double d ? (long)Math.Round(d * 1000) : null,
                BitRate,
                audio?.SampleRate,
                audio?.Channels,
                bits,
                Video?.FrameRate is double fps ? (int)Math.Round(fps * 1000) : null);
        }
    }

    /// <summary>
    /// The kind found by probing: a real video stream (not cover art) makes a video unless the file is a
    /// photo container; audio-only containers are audio. Null keeps the extension's guess.
    /// </summary>
    [JsonIgnore]
    public MediaKind? ProbedKind => null;

    /// <summary>Only the decoders this build lacks: the file is skipped, never called corrupt (subplan 04).</summary>
    [JsonIgnore]
    public bool Unsupported => Log.Counts.Unsupported > 0 && !Opened && !Streams.Any(s => s.Codec is not null);
}

/// <summary>Runs ffprobe once per file at Quick depth and above (task FF-05).</summary>
public sealed class ProbeArtifactProvider() : ArtifactProvider<ProbeArtifact>(new ArtifactCodec<ProbeArtifact>(ArtifactKind.Probe, ProviderJsonContext.Default.ProbeArtifact))
{
    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Quick;

    public override bool Applies(FileContext file) => true;

    protected override async Task<ProbeArtifact> ProduceTypedAsync(FileContext file)
    {
        string? sniffed = file.Kind == MediaKind.Photo ? await StructureProvider.SniffedImageFormatAsync(file).ConfigureAwait(false) : null;
        List<string> args = ["-show_format", "-show_streams", "-show_error", "-of", "json"];
        args.AddRange(FfmpegArgs.Input(file.FullPath, file.Kind, sniffed));
        FfResult result = await file.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffprobe,
                Arguments = args,
                Stdout = StdoutMode.Text,
                WorkingDirectory = file.TempDirectory,
                IdleTimeout = TimeSpan.FromSeconds(file.Settings.StallSeconds),
                HardTimeout = TimeSpan.FromSeconds(120),
            },
            file.Cancellation).ConfigureAwait(false);
        ProbeResult probe = result.Completed ? ProbeParser.Parse(result.StdoutText, result.Log) : ProbeResult.NotOpened(result.Log);
        return new ProbeArtifact(
            probe.Opened,
            probe.Unrecognised,
            probe.FormatName,
            probe.ProbeScore,
            probe.DurationSeconds,
            probe.BitRate,
            probe.SizeBytes,
            [.. probe.Streams.Select(StreamInfo.From)],
            LogDigest.From(result.Log),
            OutcomeMapping.From(result.Outcome));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProbeArtifact))]
[JsonSerializable(typeof(ImageDecodeArtifact))]
[JsonSerializable(typeof(VideoDecodeArtifact))]
[JsonSerializable(typeof(AudioDecodeArtifact))]
[JsonSerializable(typeof(StreamHashArtifact))]
[JsonSerializable(typeof(StructureArtifact))]
internal sealed partial class ProviderJsonContext : JsonSerializerContext;
