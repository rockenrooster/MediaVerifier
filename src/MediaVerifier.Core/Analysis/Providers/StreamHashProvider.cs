using System.Text.Json.Serialization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis.Providers;

/// <summary>The packet-level stream hash of an audio or video file (subplan 06, Stream hash).</summary>
public sealed record StreamHashArtifact(byte[]? Hash, RunOutcome Outcome) : IArtifact, IStreamHashSource
{
    [JsonIgnore]
    public byte[]? StreamHash => Hash;
}

/// <summary>
/// Hashes the encoded packets only, so re-tagging leaves the value unchanged (task I-04). A demux-only
/// pass: <c>-c copy -f streamhash</c>. For video at Standard depth or deeper the decode pass already
/// writes the stream hash as a second output, and that value is reused instead of reading the file again.
/// </summary>
public sealed class StreamHashProvider() : ArtifactProvider<StreamHashArtifact>(new ArtifactCodec<StreamHashArtifact>(ArtifactKind.StreamHash, ProviderJsonContext.Default.StreamHashArtifact))
{
    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Standard;

    public override bool Applies(FileContext file) => file.Kind is MediaKind.Audio or MediaKind.Video;

    protected override async Task<StreamHashArtifact> ProduceTypedAsync(FileContext file)
    {
        if (file.Kind == MediaKind.Video && file.Depth >= ScanDepth.Standard)
        {
            VideoDecodeArtifact? decode = await file.GetAsync<VideoDecodeArtifact>().ConfigureAwait(false);
            if (decode?.StreamHash is { Length: > 0 } shared)
            {
                return new StreamHashArtifact(shared, decode.Outcome);
            }
        }

        List<string> args = ["-i", file.FullPath];
        args.AddRange(file.Kind == MediaKind.Video ? ["-map", "0:v:0?", "-map", "0:a:0?"] : ["-map", "0:a:0"]);
        args.AddRange(["-c", "copy", "-f", "streamhash", "-hash", "md5", "-"]);
        FfResult result = await file.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = args,
                Stdout = StdoutMode.Text,
                WorkingDirectory = file.TempDirectory,
                IdleTimeout = TimeSpan.FromSeconds(file.Settings.StallSeconds),
                HardTimeout = FfmpegArgs.HardTimeout(ScanDepth.Standard, null, file.Stat.Size),
            },
            file.Cancellation).ConfigureAwait(false);
        return new StreamHashArtifact(result.Completed ? OutcomeMapping.StreamHash(result.StdoutText) : null, OutcomeMapping.From(result.Outcome));
    }
}
