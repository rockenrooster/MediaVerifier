using System.Collections.Frozen;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// Compares a file's extension with its sniffed content (subplan 07, Extension mismatch; task C-06).
/// Families: an extension names a container (or image type) and the media kinds it may hold. The
/// signature settles the kind when it can (an image format, an ISO-BMFF photo or audio brand); for mixed
/// containers the probe's kind decides.
/// </summary>
public static class ExtensionFamilies
{
    private static readonly FrozenDictionary<string, ExtensionInfo> Extensions = BuildExtensions();

    private enum Family
    {
        Jpeg,
        Png,
        Gif,
        Bmp,
        Tiff,
        WebP,
        Heif,
        Avif,
        Cr3,
        JpegXl,
        Jpeg2000,
        IsoBmff,
        Matroska,
        Avi,
        Asf,
        Flv,
        MpegPs,
        MpegTs,
        Ogg,
        Mp3,
        Flac,
        Wav,
        Aiff,
        Ape,
        WavPack,
        Tta,
        Musepack,
        Dsf,
        Dsdiff,
        Ac3,
        Dts,
        Amr,
        Caf,
        Au,

        /// <summary>Formats without a signature here (raw AAC, MXF, RealMedia): only FFmpeg can recognise them.</summary>
        NoSignature,
    }

    /// <summary>
    /// The verdict for one file. Call it after probing: <paramref name="probeOpened"/> false is read as
    /// "FFmpeg cannot open it", so an unknown signature without a probe is <see cref="ExtensionVerdict.Unrecognized"/>.
    /// Empty files are <c>GEN-EMPTY</c> and should not get here.
    /// </summary>
    /// <param name="extension">The extension without the dot, any case (a leading dot is tolerated).</param>
    /// <param name="sniffed">The sniffed format.</param>
    /// <param name="brand">The ISO-BMFF major brand (from the sniffer or the probe), any case, spaces optional.</param>
    /// <param name="probeOpened">ffprobe opened the file.</param>
    /// <param name="probeKind">The kind the probe found (attached pictures excluded); Unknown without a probe.</param>
    public static ExtensionVerdict Check(string extension, ContainerFormat sniffed, string? brand, bool probeOpened, MediaKind probeKind)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (sniffed == ContainerFormat.Unknown)
        {
            // The sniffer cannot identify it: no finding when FFmpeg can open it, else unrecognised.
            return probeOpened ? ExtensionVerdict.Match : ExtensionVerdict.Unrecognized;
        }

        if (!Extensions.TryGetValue(Normalise(extension), out ExtensionInfo info))
        {
            return ExtensionVerdict.Match;
        }

        string? normalisedBrand = FormatKinds.NormaliseBrand(brand);
        MediaKindMask content = ContentKinds(sniffed, normalisedBrand, probeOpened, probeKind);
        if ((content & info.Kinds) == 0)
        {
            return ExtensionVerdict.MismatchMedium;
        }

        if (Contains(info.Family, sniffed, normalisedBrand))
        {
            return ExtensionVerdict.Match;
        }

        return info.Kinds == MediaKindMask.Photo && content == MediaKindMask.Photo
            ? ExtensionVerdict.MismatchLow
            : ExtensionVerdict.MismatchMedium;
    }

    /// <summary>
    /// The extension to rename a mismatched file to (without the dot), or null when the format is unknown.
    /// <paramref name="brand"/> may carry the ISO-BMFF major brand, the Matroska DocType ("webm") or, for
    /// Ogg, the codec from the probe ("opus"); the container alone gives "ogg".
    /// </summary>
    public static string? CanonicalExtension(ContainerFormat format, string? brand, MediaKind kind)
    {
        string? b = FormatKinds.NormaliseBrand(brand);
        return format switch
        {
            ContainerFormat.Jpeg => "jpg",
            ContainerFormat.Png => "png",
            ContainerFormat.Gif => "gif",
            ContainerFormat.Bmp => "bmp",
            ContainerFormat.Tiff => "tif",
            ContainerFormat.WebP => "webp",
            ContainerFormat.JpegXl => "jxl",
            ContainerFormat.Jpeg2000 => "jp2",
            ContainerFormat.Wav or ContainerFormat.Rf64 => "wav",
            ContainerFormat.Avi => "avi",
            ContainerFormat.IsoBmff => IsoBmffExtension(b, kind),
            ContainerFormat.Matroska => b == "webm" ? "webm" : kind == MediaKind.Audio ? "mka" : "mkv",
            ContainerFormat.Flac => "flac",
            ContainerFormat.Ogg => b is "opus" or "opushead" ? "opus" : kind == MediaKind.Video ? "ogv" : "ogg",
            ContainerFormat.Mp3 => "mp3",
            ContainerFormat.Asf => kind == MediaKind.Audio ? "wma" : "wmv",
            ContainerFormat.Aiff => "aiff",
            ContainerFormat.Flv => "flv",
            ContainerFormat.MpegPs => "mpg",
            ContainerFormat.MpegTs => "ts",
            ContainerFormat.Ape => "ape",
            ContainerFormat.WavPack => "wv",
            ContainerFormat.Tta => "tta",
            ContainerFormat.Musepack => "mpc",
            ContainerFormat.Dsf => "dsf",
            ContainerFormat.Dsdiff => "dff",
            ContainerFormat.Ac3 => "ac3",
            ContainerFormat.Dts => "dts",
            ContainerFormat.Amr => "amr",
            ContainerFormat.Caf => "caf",
            ContainerFormat.Au => "au",
            _ => null,
        };
    }

    private static string IsoBmffExtension(string? brand, MediaKind kind)
    {
        if (FormatKinds.IsHeifBrand(brand))
        {
            return "heic";
        }

        if (FormatKinds.IsAvifBrand(brand))
        {
            return "avif";
        }

        if (FormatKinds.IsGenericHeifBrand(brand))
        {
            return "heif";
        }

        if (brand == "crx")
        {
            return "cr3";
        }

        if (brand == "qt")
        {
            return "mov";
        }

        if (brand == "m4b")
        {
            return "m4b";
        }

        if (FormatKinds.IsAudioBrand(brand) || kind == MediaKind.Audio)
        {
            return "m4a";
        }

        if (brand is not null && brand.StartsWith("3g2", StringComparison.Ordinal))
        {
            return "3g2";
        }

        return brand is not null && brand.StartsWith("3g", StringComparison.Ordinal) ? "3gp" : "mp4";
    }

    /// <summary>
    /// What the content can be: the signature's kind when it settles one, else the probe's kind, else every
    /// kind the container allows.
    /// </summary>
    private static MediaKindMask ContentKinds(ContainerFormat sniffed, string? brand, bool probeOpened, MediaKind probeKind)
    {
        MediaKindMask fromSignature = FormatKinds.PossibleKinds(sniffed, brand);
        if (fromSignature is MediaKindMask.Photo or MediaKindMask.Video or MediaKindMask.Audio)
        {
            return fromSignature;
        }

        return probeOpened && probeKind != MediaKind.Unknown ? probeKind.ToMask() : fromSignature;
    }

    private static bool Contains(Family family, ContainerFormat format, string? brand) => family switch
    {
        Family.Jpeg => format == ContainerFormat.Jpeg,
        Family.Png => format == ContainerFormat.Png,
        Family.Gif => format == ContainerFormat.Gif,
        Family.Bmp => format == ContainerFormat.Bmp,
        Family.Tiff => format == ContainerFormat.Tiff,
        Family.WebP => format == ContainerFormat.WebP,
        Family.Heif => format == ContainerFormat.IsoBmff && (FormatKinds.IsHeifBrand(brand) || FormatKinds.IsGenericHeifBrand(brand)),
        Family.Avif => format == ContainerFormat.IsoBmff && (FormatKinds.IsAvifBrand(brand) || FormatKinds.IsGenericHeifBrand(brand)),
        Family.Cr3 => format == ContainerFormat.IsoBmff && brand == "crx",
        Family.JpegXl => format == ContainerFormat.JpegXl,
        Family.Jpeg2000 => format == ContainerFormat.Jpeg2000,
        Family.IsoBmff => format == ContainerFormat.IsoBmff,
        Family.Matroska => format == ContainerFormat.Matroska,
        Family.Avi => format == ContainerFormat.Avi,
        Family.Asf => format == ContainerFormat.Asf,
        Family.Flv => format == ContainerFormat.Flv,
        Family.MpegPs => format == ContainerFormat.MpegPs,
        Family.MpegTs => format == ContainerFormat.MpegTs,
        Family.Ogg => format == ContainerFormat.Ogg,
        Family.Mp3 => format == ContainerFormat.Mp3,
        Family.Flac => format == ContainerFormat.Flac,
        Family.Wav => format is ContainerFormat.Wav or ContainerFormat.Rf64,
        Family.Aiff => format == ContainerFormat.Aiff,
        Family.Ape => format == ContainerFormat.Ape,
        Family.WavPack => format == ContainerFormat.WavPack,
        Family.Tta => format == ContainerFormat.Tta,
        Family.Musepack => format == ContainerFormat.Musepack,
        Family.Dsf => format == ContainerFormat.Dsf,
        Family.Dsdiff => format == ContainerFormat.Dsdiff,
        Family.Ac3 => format == ContainerFormat.Ac3,
        Family.Dts => format == ContainerFormat.Dts,
        Family.Amr => format == ContainerFormat.Amr,
        Family.Caf => format == ContainerFormat.Caf,
        Family.Au => format == ContainerFormat.Au,
        _ => false,
    };

    private static string Normalise(string extension) => extension.Trim().TrimStart('.').ToLowerInvariant();

    private static FrozenDictionary<string, ExtensionInfo> BuildExtensions()
    {
        const MediaKindMask Photo = MediaKindMask.Photo;
        const MediaKindMask Audio = MediaKindMask.Audio;
        const MediaKindMask AudioVideo = MediaKindMask.Video | MediaKindMask.Audio;
        Dictionary<string, ExtensionInfo> map = new(StringComparer.Ordinal);
        void Add(Family family, MediaKindMask kinds, params string[] names)
        {
            foreach (string name in names)
            {
                map[name] = new ExtensionInfo(family, kinds);
            }
        }

        // Photos (subplan 05, Media classification). TIFF-based RAW shares the TIFF family.
        Add(Family.Jpeg, Photo, "jpg", "jpeg", "jpe", "jfif");
        Add(Family.Png, Photo, "png", "apng");
        Add(Family.Gif, Photo, "gif");
        Add(Family.Bmp, Photo, "bmp", "dib");
        Add(Family.Tiff, Photo, "tif", "tiff", "dng", "cr2", "nef", "arw", "orf", "rw2", "raf", "pef", "srw");
        Add(Family.WebP, Photo, "webp");
        Add(Family.Heif, Photo, "heic", "heif");
        Add(Family.Avif, Photo, "avif");
        Add(Family.Cr3, Photo, "cr3");
        Add(Family.JpegXl, Photo, "jxl");
        Add(Family.Jpeg2000, Photo, "jp2", "j2k", "j2c", "jpf", "jpx");

        // Video containers hold video, or audio alone ("audio-only MP4 named .mp4 is fine").
        Add(Family.IsoBmff, AudioVideo, "mp4", "m4v", "mov", "qt", "3gp", "3g2", "f4v");
        Add(Family.Matroska, AudioVideo, "mkv", "mk3d", "webm");
        Add(Family.Avi, AudioVideo, "avi", "divx");
        Add(Family.Asf, AudioVideo, "wmv", "asf");
        Add(Family.Flv, AudioVideo, "flv");
        Add(Family.MpegPs, AudioVideo, "mpg", "mpeg", "mpe", "m2v", "vob");
        Add(Family.MpegTs, AudioVideo, "ts", "m2ts", "mts", "m2t");
        Add(Family.Ogg, AudioVideo, "ogv", "ogg");
        Add(Family.NoSignature, AudioVideo, "mxf", "rm", "rmvb");

        // Audio.
        Add(Family.Mp3, Audio, "mp3", "mp2", "mpa", "mp1");
        Add(Family.Flac, Audio, "flac");
        Add(Family.Wav, Audio, "wav", "wave", "bwf", "rf64");
        Add(Family.IsoBmff, Audio, "m4a", "m4b", "m4p", "m4r");
        Add(Family.Ogg, Audio, "oga", "opus", "spx");
        Add(Family.Asf, Audio, "wma");
        Add(Family.Matroska, Audio, "mka");
        Add(Family.Aiff, Audio, "aiff", "aif", "aifc");
        Add(Family.Ape, Audio, "ape");
        Add(Family.WavPack, Audio, "wv");
        Add(Family.Tta, Audio, "tta");
        Add(Family.Musepack, Audio, "mpc", "mp+", "mpp");
        Add(Family.Dsf, Audio, "dsf");
        Add(Family.Dsdiff, Audio, "dff");
        Add(Family.Ac3, Audio, "ac3", "eac3", "ec3");
        Add(Family.Dts, Audio, "dts");
        Add(Family.Amr, Audio, "amr");
        Add(Family.Caf, Audio, "caf");
        Add(Family.Au, Audio, "au", "snd");
        Add(Family.NoSignature, Audio, "aac");
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private readonly record struct ExtensionInfo(Family Family, MediaKindMask Kinds);
}
