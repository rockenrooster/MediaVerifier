using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Logging;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis.Providers;

/// <summary>
/// The native structure check at Quick depth (subplan 07, Structure checks): headers and seeks only, no
/// FFmpeg. Its sniffed format also gives FFmpeg an explicit image demuxer.
/// </summary>
public sealed class StructureProvider() : ArtifactProvider<StructureArtifact>(new ArtifactCodec<StructureArtifact>(ArtifactKind.Structure, ProviderJsonContext.Default.StructureArtifact))
{
    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Quick;

    public override bool Applies(FileContext file) => true;

    /// <summary>The image format name FFmpeg's demuxer table uses (<see cref="Ffmpeg.FfmpegArgs.PhotoDemuxer"/>), or null.</summary>
    public static string? ImageFormatName(ContainerFormat format) => format switch
    {
        ContainerFormat.Jpeg => "jpeg",
        ContainerFormat.Png => "png",
        ContainerFormat.WebP => "webp",
        ContainerFormat.Bmp => "bmp",
        ContainerFormat.Tiff => "tiff",
        ContainerFormat.Gif => "gif",
        ContainerFormat.JpegXl => "jpegxl",
        _ => null,
    };

    /// <summary>The sniffed image format of a file, from its structure artifact (stored or produced now).</summary>
    public static async Task<string?> SniffedImageFormatAsync(FileContext file)
    {
        StructureArtifact? structure = await file.GetAsync<StructureArtifact>().ConfigureAwait(false);
        return structure is null ? null : ImageFormatName(structure.Sniff.Format);
    }

    protected override Task<StructureArtifact> ProduceTypedAsync(FileContext file)
    {
        try
        {
            using Stream stream = file.FileSystem.OpenRead(file.FullPath, 4096, sequential: false);
            return Task.FromResult(StructureReader.Read(stream, file.Extension));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // GEN-UNREADABLE and the skip reasons come from the integrity read; nothing structural is known.
            AppLog.Shared?.Info($"Structure check could not read {file.FullPath}: {ex.Message}");
            return Task.FromResult(new StructureArtifact(new SniffResult(ContainerFormat.Unknown, null, 0, MediaKind.Unknown), file.Stat.Size, null, [], []) { ReadFailed = true });
        }
    }
}
