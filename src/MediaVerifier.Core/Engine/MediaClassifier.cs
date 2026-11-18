using MediaVerifier.Core.Model;
using MediaVerifier.Core.Settings;

namespace MediaVerifier.Core.Engine;

/// <summary>
/// Classifies files by extension (task E-01), lower case, refined after probing. The lists are the
/// defaults of subplan 05 and are user-editable (Settings → Scanning → File types). Files with other
/// extensions are ignored and not indexed.
/// </summary>
public sealed class MediaClassifier
{
    public static readonly IReadOnlyList<string> DefaultPhoto =
    [
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif", "jxl", "jp2",
        "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf", "pef", "srw",
    ];

    public static readonly IReadOnlyList<string> DefaultVideo =
    [
        "mp4", "m4v", "mkv", "webm", "mov", "avi", "wmv", "asf", "flv", "mpg", "mpeg", "m2v", "vob", "ts", "m2ts", "mts",
        "3gp", "3g2", "ogv", "divx", "mxf", "rm", "rmvb", "f4v",
    ];

    public static readonly IReadOnlyList<string> DefaultAudio =
    [
        "mp3", "flac", "wav", "m4a", "aac", "ogg", "oga", "opus", "wma", "aiff", "aif", "ape", "wv", "tta", "mka", "ac3",
        "dts", "mp2", "amr", "dsf", "dff", "mpc", "caf", "au",
    ];

    /// <summary>RAW photos get structural and embedded-preview checks only.</summary>
    public static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf", "pef", "srw",
    };

    private readonly Dictionary<string, MediaKind> _byExtension = new(StringComparer.Ordinal);

    public MediaClassifier(IEnumerable<string>? photo = null, IEnumerable<string>? video = null, IEnumerable<string>? audio = null)
    {
        Add(photo ?? DefaultPhoto, MediaKind.Photo);
        Add(video ?? DefaultVideo, MediaKind.Video);
        Add(audio ?? DefaultAudio, MediaKind.Audio);
    }

    public static MediaClassifier Default { get; } = new();

    /// <summary>
    /// The classifier of one watched folder: the file types (Settings → Scanning) of the kinds the folder contains
    /// (Library → Contains). A kind it does not contain gets an empty list, never null (null means the defaults). No
    /// kind chosen (only possible in an old or edited database) means all of them.
    /// </summary>
    public static MediaClassifier For(ScanSettings settings, MediaKindMask contains)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MediaKindMask mask = contains == MediaKindMask.None ? MediaKindMask.All : contains;
        return new MediaClassifier(
            mask.Includes(MediaKind.Photo) ? settings.PhotoExtensions : [],
            mask.Includes(MediaKind.Video) ? settings.VideoExtensions : [],
            mask.Includes(MediaKind.Audio) ? settings.AudioExtensions : []);
    }

    /// <summary>The media kind for a file name; Unknown means "not media, do not index".</summary>
    public MediaKind Classify(string fileName)
    {
        string extension = Extension(fileName);
        return extension.Length > 0 && _byExtension.TryGetValue(extension, out MediaKind kind) ? kind : MediaKind.Unknown;
    }

    /// <summary>Lower-case extension after the last dot, without the dot; "" when there is none.</summary>
    public static string Extension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return "";
        }

        return fileName[(dot + 1)..].ToLowerInvariant();
    }

    public static bool IsRaw(string extension) => RawExtensions.Contains(extension);

    /// <summary>Normalises a user-entered list: trims, removes dots and empties, lower case, no duplicates.</summary>
    public static List<string> Normalize(IEnumerable<string> extensions) =>
        [.. extensions
            .Select(e => e.Trim().TrimStart('.', '*').ToLowerInvariant())
            .Where(e => e.Length > 0 && e.IndexOfAny(['\\', '/', ':', ' ']) < 0)
            .Distinct(StringComparer.Ordinal)];

    private void Add(IEnumerable<string> extensions, MediaKind kind)
    {
        foreach (string extension in Normalize(extensions))
        {
            _byExtension.TryAdd(extension, kind);
        }
    }
}
