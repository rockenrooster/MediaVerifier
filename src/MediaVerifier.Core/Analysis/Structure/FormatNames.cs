namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>Short display names of sniffed formats for finding summaries (<c>FLAC content named .mp3</c>).</summary>
public static class FormatNames
{
    public static string Display(ContainerFormat format, string? brand) => format switch
    {
        ContainerFormat.Jpeg => "JPEG",
        ContainerFormat.Png => "PNG",
        ContainerFormat.Gif => "GIF",
        ContainerFormat.Bmp => "BMP",
        ContainerFormat.Tiff => "TIFF",
        ContainerFormat.WebP => "WebP",
        ContainerFormat.Wav => "WAV",
        ContainerFormat.Rf64 => "RF64 WAV",
        ContainerFormat.Avi => "AVI",
        ContainerFormat.IsoBmff => IsoBmff(FormatKinds.NormaliseBrand(brand)),
        ContainerFormat.JpegXl => "JPEG XL",
        ContainerFormat.Jpeg2000 => "JPEG 2000",
        ContainerFormat.Matroska => "Matroska",
        ContainerFormat.Flac => "FLAC",
        ContainerFormat.Ogg => "Ogg",
        ContainerFormat.Mp3 => "MP3",
        ContainerFormat.Asf => "Windows Media",
        ContainerFormat.Aiff => "AIFF",
        ContainerFormat.Flv => "Flash video",
        ContainerFormat.MpegPs => "MPEG program stream",
        ContainerFormat.MpegTs => "MPEG transport stream",
        ContainerFormat.Ape => "Monkey's Audio",
        ContainerFormat.WavPack => "WavPack",
        ContainerFormat.Tta => "TTA",
        ContainerFormat.Musepack => "Musepack",
        ContainerFormat.Dsf => "DSF",
        ContainerFormat.Dsdiff => "DSDIFF",
        ContainerFormat.Ac3 => "AC-3",
        ContainerFormat.Dts => "DTS",
        ContainerFormat.Amr => "AMR",
        ContainerFormat.Caf => "Core Audio",
        ContainerFormat.Au => "AU",
        _ => "Unknown",
    };

    private static string IsoBmff(string? brand)
    {
        if (FormatKinds.IsAvifBrand(brand))
        {
            return "AVIF";
        }

        if (FormatKinds.IsHeifBrand(brand) || FormatKinds.IsGenericHeifBrand(brand))
        {
            return "HEIF";
        }

        if (FormatKinds.IsAudioBrand(brand))
        {
            return "M4A";
        }

        return brand switch
        {
            "qt" => "QuickTime",
            "crx" => "Canon RAW",
            not null when brand.StartsWith("3g", StringComparison.Ordinal) => "3GP",
            _ => "MP4",
        };
    }
}
