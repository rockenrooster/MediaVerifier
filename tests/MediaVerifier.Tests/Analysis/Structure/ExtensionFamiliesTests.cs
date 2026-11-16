using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Model;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Task C-06: <c>GEN-EXT-MISMATCH</c> and <c>GEN-UNRECOGNIZED</c>, one row per family rule.</summary>
public sealed class ExtensionFamiliesTests
{
    [Theory]

    // Same family: no finding.
    [InlineData("jpg", ContainerFormat.Jpeg, null, true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("jpeg", ContainerFormat.Jpeg, null, true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("JPE", ContainerFormat.Jpeg, null, false, MediaKind.Unknown, ExtensionVerdict.Match)]
    [InlineData(".jfif", ContainerFormat.Jpeg, null, true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("tif", ContainerFormat.Tiff, null, true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("tiff", ContainerFormat.Tiff, null, true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("nef", ContainerFormat.Tiff, null, false, MediaKind.Unknown, ExtensionVerdict.Match)]
    [InlineData("dng", ContainerFormat.Tiff, null, true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("mp4", ContainerFormat.IsoBmff, "isom", true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("m4v", ContainerFormat.IsoBmff, "M4V ", true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("mov", ContainerFormat.IsoBmff, "qt  ", true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("mp4", ContainerFormat.IsoBmff, "M4A ", true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("mp4", ContainerFormat.IsoBmff, "isom", true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("m4a", ContainerFormat.IsoBmff, "M4A ", true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("m4a", ContainerFormat.IsoBmff, "isom", true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("m4a", ContainerFormat.IsoBmff, "isom", false, MediaKind.Unknown, ExtensionVerdict.Match)]
    [InlineData("heic", ContainerFormat.IsoBmff, "heic", true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("heif", ContainerFormat.IsoBmff, "mif1", true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("avif", ContainerFormat.IsoBmff, "avif", true, MediaKind.Photo, ExtensionVerdict.Match)]
    [InlineData("cr3", ContainerFormat.IsoBmff, "crx ", false, MediaKind.Unknown, ExtensionVerdict.Match)]
    [InlineData("mkv", ContainerFormat.Matroska, null, true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("mkv", ContainerFormat.Matroska, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("mka", ContainerFormat.Matroska, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("webm", ContainerFormat.Matroska, null, true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("webm", ContainerFormat.Matroska, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("wav", ContainerFormat.Rf64, "WAVE", true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("opus", ContainerFormat.Ogg, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("ogg", ContainerFormat.Ogg, null, true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("mp3", ContainerFormat.Mp3, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("mp2", ContainerFormat.Mp3, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("flac", ContainerFormat.Flac, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("wma", ContainerFormat.Asf, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("wmv", ContainerFormat.Asf, null, true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("m2ts", ContainerFormat.MpegTs, null, true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("vob", ContainerFormat.MpegPs, null, true, MediaKind.Video, ExtensionVerdict.Match)]
    [InlineData("gif", ContainerFormat.Gif, null, true, MediaKind.Video, ExtensionVerdict.Match)]

    // Image named as another image type: Low.
    [InlineData("jpg", ContainerFormat.Png, null, true, MediaKind.Photo, ExtensionVerdict.MismatchLow)]
    [InlineData("png", ContainerFormat.Jpeg, null, true, MediaKind.Photo, ExtensionVerdict.MismatchLow)]
    [InlineData("jpg", ContainerFormat.IsoBmff, "heic", true, MediaKind.Photo, ExtensionVerdict.MismatchLow)]
    [InlineData("heic", ContainerFormat.IsoBmff, "avif", true, MediaKind.Photo, ExtensionVerdict.MismatchLow)]
    [InlineData("avif", ContainerFormat.IsoBmff, "heic", true, MediaKind.Photo, ExtensionVerdict.MismatchLow)]
    [InlineData("gif", ContainerFormat.WebP, null, true, MediaKind.Photo, ExtensionVerdict.MismatchLow)]
    [InlineData("tif", ContainerFormat.Jpeg, null, false, MediaKind.Unknown, ExtensionVerdict.MismatchLow)]

    // Audio or video container named as a different container: Medium.
    [InlineData("mp3", ContainerFormat.Flac, null, true, MediaKind.Audio, ExtensionVerdict.MismatchMedium)]
    [InlineData("mp4", ContainerFormat.Matroska, null, true, MediaKind.Video, ExtensionVerdict.MismatchMedium)]
    [InlineData("mkv", ContainerFormat.IsoBmff, "isom", true, MediaKind.Video, ExtensionVerdict.MismatchMedium)]
    [InlineData("m4a", ContainerFormat.Mp3, null, true, MediaKind.Audio, ExtensionVerdict.MismatchMedium)]
    [InlineData("wav", ContainerFormat.Mp3, null, true, MediaKind.Audio, ExtensionVerdict.MismatchMedium)]
    [InlineData("aac", ContainerFormat.IsoBmff, "M4A ", true, MediaKind.Audio, ExtensionVerdict.MismatchMedium)]
    [InlineData("ogg", ContainerFormat.Flac, null, true, MediaKind.Audio, ExtensionVerdict.MismatchMedium)]
    [InlineData("mpg", ContainerFormat.MpegTs, null, true, MediaKind.Video, ExtensionVerdict.MismatchMedium)]

    // Media kind differs: Medium.
    [InlineData("jpg", ContainerFormat.IsoBmff, "isom", true, MediaKind.Video, ExtensionVerdict.MismatchMedium)]
    [InlineData("jpg", ContainerFormat.Avi, null, false, MediaKind.Unknown, ExtensionVerdict.MismatchMedium)]
    [InlineData("mp4", ContainerFormat.Jpeg, null, true, MediaKind.Photo, ExtensionVerdict.MismatchMedium)]
    [InlineData("mp4", ContainerFormat.IsoBmff, "heic", true, MediaKind.Photo, ExtensionVerdict.MismatchMedium)]
    [InlineData("mp4", ContainerFormat.IsoBmff, "isom", true, MediaKind.Photo, ExtensionVerdict.MismatchMedium)]
    [InlineData("m4a", ContainerFormat.IsoBmff, "isom", true, MediaKind.Video, ExtensionVerdict.MismatchMedium)]
    [InlineData("mka", ContainerFormat.Matroska, null, true, MediaKind.Video, ExtensionVerdict.MismatchMedium)]
    [InlineData("heic", ContainerFormat.IsoBmff, "isom", true, MediaKind.Video, ExtensionVerdict.MismatchMedium)]
    [InlineData("mp3", ContainerFormat.Png, null, true, MediaKind.Photo, ExtensionVerdict.MismatchMedium)]
    [InlineData("opus", ContainerFormat.Ogg, null, true, MediaKind.Video, ExtensionVerdict.MismatchMedium)]

    // No signature: FFmpeg decides.
    [InlineData("mp3", ContainerFormat.Unknown, null, false, MediaKind.Unknown, ExtensionVerdict.Unrecognized)]
    [InlineData("jpg", ContainerFormat.Unknown, null, false, MediaKind.Unknown, ExtensionVerdict.Unrecognized)]
    [InlineData("aac", ContainerFormat.Unknown, null, true, MediaKind.Audio, ExtensionVerdict.Match)]
    [InlineData("mxf", ContainerFormat.Unknown, null, true, MediaKind.Video, ExtensionVerdict.Match)]

    // An extension outside the media lists has nothing to compare with.
    [InlineData("bin", ContainerFormat.Jpeg, null, true, MediaKind.Photo, ExtensionVerdict.Match)]
    public void FamilyRules(string extension, ContainerFormat sniffed, string? brand, bool probeOpened, MediaKind probeKind, ExtensionVerdict expected) =>
        Assert.Equal(expected, ExtensionFamilies.Check(extension, sniffed, brand, probeOpened, probeKind));

    [Fact]
    public void TextFileNamedMp3IsUnrecognised()
    {
        byte[] text = TestMedia.Ascii("Shopping list\r\n- milk\r\n- bread\r\n- a new hard drive, the old one is dying\r\n");
        StructureArtifact artifact = Read(text, "mp3");
        Assert.Equal(ContainerFormat.Unknown, artifact.Sniff.Format);
        Assert.Empty(artifact.Issues);
        Assert.Equal(ExtensionVerdict.Unrecognized, ExtensionFamilies.Check("mp3", artifact.Sniff.Format, artifact.Sniff.Brand, probeOpened: false, MediaKind.Unknown));
    }

    [Fact]
    public void EveryClassifiedExtensionBelongsToAFamily()
    {
        // The default extension lists of subplan 05 (Media classification): a recognised container named
        // with any of them gets a verdict from a family, so none silently falls through to "no opinion".
        string[] photo = ["jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif", "jxl", "jp2", "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf", "pef", "srw"];
        string[] video = ["mp4", "m4v", "mkv", "webm", "mov", "avi", "wmv", "asf", "flv", "mpg", "mpeg", "m2v", "vob", "ts", "m2ts", "mts", "3gp", "3g2", "ogv", "divx", "mxf", "rm", "rmvb", "f4v"];
        string[] audio = ["mp3", "flac", "wav", "m4a", "aac", "ogg", "oga", "opus", "wma", "aiff", "aif", "ape", "wv", "tta", "mka", "ac3", "dts", "mp2", "amr", "dsf", "dff", "mpc", "caf", "au"];
        foreach (string extension in photo)
        {
            Assert.Equal(ExtensionVerdict.MismatchMedium, ExtensionFamilies.Check(extension, ContainerFormat.Flac, null, true, MediaKind.Audio));
        }

        foreach (string extension in video.Concat(audio))
        {
            Assert.Equal(ExtensionVerdict.MismatchMedium, ExtensionFamilies.Check(extension, ContainerFormat.Jpeg, null, true, MediaKind.Photo));
        }
    }

    [Theory]
    [InlineData(ContainerFormat.Jpeg, null, MediaKind.Photo, "jpg")]
    [InlineData(ContainerFormat.Png, null, MediaKind.Photo, "png")]
    [InlineData(ContainerFormat.WebP, "WEBP", MediaKind.Photo, "webp")]
    [InlineData(ContainerFormat.Tiff, null, MediaKind.Photo, "tif")]
    [InlineData(ContainerFormat.IsoBmff, "isom", MediaKind.Video, "mp4")]
    [InlineData(ContainerFormat.IsoBmff, "isom", MediaKind.Audio, "m4a")]
    [InlineData(ContainerFormat.IsoBmff, "M4A ", MediaKind.Unknown, "m4a")]
    [InlineData(ContainerFormat.IsoBmff, "qt  ", MediaKind.Video, "mov")]
    [InlineData(ContainerFormat.IsoBmff, "heic", MediaKind.Photo, "heic")]
    [InlineData(ContainerFormat.IsoBmff, "avif", MediaKind.Photo, "avif")]
    [InlineData(ContainerFormat.IsoBmff, "crx ", MediaKind.Photo, "cr3")]
    [InlineData(ContainerFormat.IsoBmff, "3gp4", MediaKind.Video, "3gp")]
    [InlineData(ContainerFormat.Matroska, null, MediaKind.Video, "mkv")]
    [InlineData(ContainerFormat.Matroska, null, MediaKind.Audio, "mka")]
    [InlineData(ContainerFormat.Matroska, "webm", MediaKind.Video, "webm")]
    [InlineData(ContainerFormat.Flac, null, MediaKind.Audio, "flac")]
    [InlineData(ContainerFormat.Mp3, null, MediaKind.Audio, "mp3")]
    [InlineData(ContainerFormat.Ogg, null, MediaKind.Audio, "ogg")]
    [InlineData(ContainerFormat.Ogg, "opus", MediaKind.Audio, "opus")]
    [InlineData(ContainerFormat.Ogg, null, MediaKind.Video, "ogv")]
    [InlineData(ContainerFormat.Wav, "WAVE", MediaKind.Audio, "wav")]
    [InlineData(ContainerFormat.Rf64, "WAVE", MediaKind.Audio, "wav")]
    [InlineData(ContainerFormat.Avi, "AVI ", MediaKind.Video, "avi")]
    [InlineData(ContainerFormat.Asf, null, MediaKind.Audio, "wma")]
    [InlineData(ContainerFormat.Asf, null, MediaKind.Video, "wmv")]
    [InlineData(ContainerFormat.Unknown, null, MediaKind.Audio, null)]
    public void CanonicalExtensions(ContainerFormat format, string? brand, MediaKind kind, string? expected) =>
        Assert.Equal(expected, ExtensionFamilies.CanonicalExtension(format, brand, kind));

    [Fact]
    public void EveryKnownFormatHasACanonicalExtensionThatMatchesIt()
    {
        foreach (ContainerFormat format in Enum.GetValues<ContainerFormat>().Where(f => f != ContainerFormat.Unknown))
        {
            MediaKind kind = format is ContainerFormat.Jpeg or ContainerFormat.Png or ContainerFormat.Gif or ContainerFormat.Bmp
                or ContainerFormat.Tiff or ContainerFormat.WebP or ContainerFormat.JpegXl or ContainerFormat.Jpeg2000
                ? MediaKind.Photo
                : format is ContainerFormat.Avi or ContainerFormat.IsoBmff or ContainerFormat.Matroska or ContainerFormat.Asf
                    or ContainerFormat.Flv or ContainerFormat.MpegPs or ContainerFormat.MpegTs ? MediaKind.Video : MediaKind.Audio;
            string? extension = ExtensionFamilies.CanonicalExtension(format, null, kind);
            Assert.NotNull(extension);
            Assert.Equal(ExtensionVerdict.Match, ExtensionFamilies.Check(extension, format, null, true, kind));
        }
    }
}
