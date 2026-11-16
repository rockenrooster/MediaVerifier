using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>Which media kinds a sniffed format (and ISO-BMFF brand) can hold.</summary>
internal static class FormatKinds
{
    /// <summary>A brand trimmed and lower-cased for comparison ("M4A " becomes "m4a"); null when empty.</summary>
    public static string? NormaliseBrand(string? brand)
    {
        string? trimmed = brand?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    /// <summary>HEIF brands for HEVC-coded images and image sequences.</summary>
    public static bool IsHeifBrand(string? brand) =>
        brand is "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "hevm" or "hevs";

    /// <summary>AVIF brands.</summary>
    public static bool IsAvifBrand(string? brand) => brand is "avif" or "avis" or "avio";

    /// <summary>Generic HEIF brands that do not name the codec.</summary>
    public static bool IsGenericHeifBrand(string? brand) => brand is "mif1" or "mif2" or "msf1";

    /// <summary>Brands of still images and image sequences, including Canon CR3.</summary>
    public static bool IsPhotoBrand(string? brand) =>
        IsHeifBrand(brand) || IsAvifBrand(brand) || IsGenericHeifBrand(brand) || brand is "crx";

    /// <summary>Brands that declare audio-only content.</summary>
    public static bool IsAudioBrand(string? brand) => brand is "m4a" or "m4b" or "m4p" or "f4a" or "f4b";

    public static bool IsImageFormat(ContainerFormat format) => format is ContainerFormat.Jpeg or ContainerFormat.Png
        or ContainerFormat.Gif or ContainerFormat.Bmp or ContainerFormat.Tiff or ContainerFormat.WebP
        or ContainerFormat.JpegXl or ContainerFormat.Jpeg2000;

    /// <summary>Containers that hold video, audio or both.</summary>
    public static bool IsMixedContainer(ContainerFormat format) => format is ContainerFormat.Avi or ContainerFormat.Matroska
        or ContainerFormat.Asf or ContainerFormat.Flv or ContainerFormat.MpegPs or ContainerFormat.MpegTs or ContainerFormat.Ogg;

    /// <summary>The sniffer's guess (subplan 07 contract): mixed containers guess Video, except Ogg (Audio).</summary>
    public static MediaKind KindHint(ContainerFormat format, string? brand)
    {
        if (format == ContainerFormat.Unknown)
        {
            return MediaKind.Unknown;
        }

        if (IsImageFormat(format))
        {
            return MediaKind.Photo;
        }

        if (format == ContainerFormat.IsoBmff)
        {
            string? b = NormaliseBrand(brand);
            return IsPhotoBrand(b) ? MediaKind.Photo : IsAudioBrand(b) ? MediaKind.Audio : MediaKind.Video;
        }

        return format == ContainerFormat.Ogg || !IsMixedContainer(format) ? MediaKind.Audio : MediaKind.Video;
    }

    /// <summary>Every kind the signature allows; a single kind when the signature settles it.</summary>
    public static MediaKindMask PossibleKinds(ContainerFormat format, string? normalisedBrand)
    {
        if (format == ContainerFormat.Unknown)
        {
            return MediaKindMask.None;
        }

        if (IsImageFormat(format))
        {
            return MediaKindMask.Photo;
        }

        if (format == ContainerFormat.IsoBmff)
        {
            return IsPhotoBrand(normalisedBrand) ? MediaKindMask.Photo
                : IsAudioBrand(normalisedBrand) ? MediaKindMask.Audio
                : MediaKindMask.Video | MediaKindMask.Audio;
        }

        return IsMixedContainer(format) ? MediaKindMask.Video | MediaKindMask.Audio : MediaKindMask.Audio;
    }
}
