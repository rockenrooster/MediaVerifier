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

        if (Contains(info, sniffed, normalisedBrand))
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

    /// <summary>True when the sniffed format is the extension's own (WAV names RF64 too) with a brand it allows.</summary>
    private static bool Contains(ExtensionInfo info, ContainerFormat format, string? brand) =>
        (format == info.Format || (info.Format == ContainerFormat.Wav && format == ContainerFormat.Rf64))
        && (info.Brand is null || info.Brand(brand));

    private static string Normalise(string extension) => extension.Trim().TrimStart('.').ToLowerInvariant();

    private static FrozenDictionary<string, ExtensionInfo> BuildExtensions()
    {
        const MediaKindMask Photo = MediaKindMask.Photo;
        const MediaKindMask Audio = MediaKindMask.Audio;
        const MediaKindMask AudioVideo = MediaKindMask.Video | MediaKindMask.Audio;
        Dictionary<string, ExtensionInfo> map = new(StringComparer.Ordinal);
        void Add(ContainerFormat format, MediaKindMask kinds, params string[] names)
        {
            foreach (string name in names)
            {
                map[name] = new ExtensionInfo(format, kinds);
            }
        }

        // Photos (subplan 05, Media classification). TIFF-based RAW is TIFF; HEIF, AVIF and Canon RAW are ISO-BMFF
        // told apart by brand.
        Add(ContainerFormat.Jpeg, Photo, "jpg", "jpeg", "jpe", "jfif");
        Add(ContainerFormat.Png, Photo, "png", "apng");
        Add(ContainerFormat.Gif, Photo, "gif");
        Add(ContainerFormat.Bmp, Photo, "bmp", "dib");
        Add(ContainerFormat.Tiff, Photo, "tif", "tiff", "dng", "cr2", "nef", "arw", "orf", "rw2", "raf", "pef", "srw");
        Add(ContainerFormat.WebP, Photo, "webp");
        ExtensionInfo heif = new(ContainerFormat.IsoBmff, Photo, b => FormatKinds.IsHeifBrand(b) || FormatKinds.IsGenericHeifBrand(b));
        map["heic"] = heif;
        map["heif"] = heif;
        map["avif"] = new(ContainerFormat.IsoBmff, Photo, b => FormatKinds.IsAvifBrand(b) || FormatKinds.IsGenericHeifBrand(b));
        map["cr3"] = new(ContainerFormat.IsoBmff, Photo, b => b == "crx");
        Add(ContainerFormat.JpegXl, Photo, "jxl");
        Add(ContainerFormat.Jpeg2000, Photo, "jp2", "j2k", "j2c", "jpf", "jpx");

        // Video containers hold video, or audio alone ("audio-only MP4 named .mp4 is fine"). Unknown: formats
        // without a signature here (MXF, RealMedia, raw AAC), which only FFmpeg can recognise.
        Add(ContainerFormat.IsoBmff, AudioVideo, "mp4", "m4v", "mov", "qt", "3gp", "3g2", "f4v");
        Add(ContainerFormat.Matroska, AudioVideo, "mkv", "mk3d", "webm");
        Add(ContainerFormat.Avi, AudioVideo, "avi", "divx");
        Add(ContainerFormat.Asf, AudioVideo, "wmv", "asf");
        Add(ContainerFormat.Flv, AudioVideo, "flv");
        Add(ContainerFormat.MpegPs, AudioVideo, "mpg", "mpeg", "mpe", "m2v", "vob");
        Add(ContainerFormat.MpegTs, AudioVideo, "ts", "m2ts", "mts", "m2t");
        Add(ContainerFormat.Ogg, AudioVideo, "ogv", "ogg");
        Add(ContainerFormat.Unknown, AudioVideo, "mxf", "rm", "rmvb");

        // Audio.
        Add(ContainerFormat.Mp3, Audio, "mp3", "mp2", "mpa", "mp1");
        Add(ContainerFormat.Flac, Audio, "flac");
        Add(ContainerFormat.Wav, Audio, "wav", "wave", "bwf", "rf64");
        Add(ContainerFormat.IsoBmff, Audio, "m4a", "m4b", "m4p", "m4r");
        Add(ContainerFormat.Ogg, Audio, "oga", "opus", "spx");
        Add(ContainerFormat.Asf, Audio, "wma");
        Add(ContainerFormat.Matroska, Audio, "mka");
        Add(ContainerFormat.Aiff, Audio, "aiff", "aif", "aifc");
        Add(ContainerFormat.Ape, Audio, "ape");
        Add(ContainerFormat.WavPack, Audio, "wv");
        Add(ContainerFormat.Tta, Audio, "tta");
        Add(ContainerFormat.Musepack, Audio, "mpc", "mp+", "mpp");
        Add(ContainerFormat.Dsf, Audio, "dsf");
        Add(ContainerFormat.Dsdiff, Audio, "dff");
        Add(ContainerFormat.Ac3, Audio, "ac3", "eac3", "ec3");
        Add(ContainerFormat.Dts, Audio, "dts");
        Add(ContainerFormat.Amr, Audio, "amr");
        Add(ContainerFormat.Caf, Audio, "caf");
        Add(ContainerFormat.Au, Audio, "au", "snd");
        Add(ContainerFormat.Unknown, Audio, "aac");
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>An extension's container (Unknown: none with a signature), the kinds it may hold, and the brands it allows.</summary>
    private readonly record struct ExtensionInfo(ContainerFormat Format, MediaKindMask Kinds, Func<string?, bool>? Brand = null);
}
