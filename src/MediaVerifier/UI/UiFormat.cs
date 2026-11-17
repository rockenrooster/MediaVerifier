using System.Globalization;
using MediaVerifier.Core.Model;

namespace MediaVerifier.UI;

/// <summary>The formats used in strings (subplan 09, Formats used in strings).</summary>
internal static class UiFormat
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>Current culture grouping: <c>12,507</c>.</summary>
    public static string Count(long n) => n.ToString("N0", Culture);

    /// <summary>Binary units, one decimal, none for bytes and KB: <c>412 KB</c>, <c>31.2 MB</c>, <c>3.2 TB</c>.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Format(Culture, Strings.Units.Bytes, bytes);
        }

        double value = bytes / 1024.0;
        if (value < 1024)
        {
            return string.Format(Culture, Strings.Units.Kilobytes, Math.Round(value));
        }

        string[] units = [Strings.Units.Megabytes, Strings.Units.Gigabytes, Strings.Units.Terabytes];
        int unit = 0;
        value /= 1024;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(Culture, units[unit], value.ToString("0.0", Culture));
    }

    /// <summary>Health percent with one decimal: <c>98.7%</c>.</summary>
    public static string HealthPercent(double percent) => percent.ToString("0.0", Culture) + "%";

    /// <summary>Other percents without decimals: <c>82%</c>.</summary>
    public static string Percent(double percent) => Math.Round(percent).ToString("0", Culture) + "%";

    /// <summary>Current culture short time: <c>03:12</c>.</summary>
    public static string TimeOfDay(DateTime local) => local.ToString("t", Culture);

    /// <summary>
    /// <c>Today 03:12</c>, <c>Yesterday 03:00</c>, the weekday for the last six days (<c>Mon 21:40</c>), then
    /// <c>8 Nov 03:00</c>, with the year when not the current year. <paramref name="inSentence"/> lower-cases
    /// the first word (<c>Scanned today 03:12</c>).
    /// </summary>
    public static string RecentDate(DateTimeOffset utc, DateTimeOffset now, bool inSentence = false, bool withTime = true)
    {
        DateTime local = utc.ToLocalTime().DateTime;
        DateTime today = now.ToLocalTime().Date;
        string time = withTime ? " " + TimeOfDay(local) : string.Empty;
        string text;
        int days = (today - local.Date).Days;
        if (days == 0)
        {
            text = Strings.Dates.Today + time;
        }
        else if (days == 1)
        {
            text = Strings.Dates.Yesterday + time;
        }
        else if (days == -1)
        {
            text = Strings.Dates.Tomorrow + time;
        }
        else if (days > 1 && days < 7)
        {
            text = local.ToString("ddd", Culture) + time;
        }
        else if (local.Year == today.Year && Math.Abs(days) <= 183)
        {
            text = local.ToString("d MMM", Culture) + time;
        }
        else
        {
            text = local.ToString("d MMM yyyy", Culture) + time;
        }

        return inSentence && text.Length > 0 && (days is 0 or 1 or -1) ? char.ToLower(text[0], Culture) + text[1..] : text;
    }

    /// <summary>A date without time in the same style (<c>12 Nov</c>, <c>Yesterday</c>).</summary>
    public static string RecentDay(DateTimeOffset utc, DateTimeOffset now) => RecentDate(utc, now, withTime: false);

    /// <summary><c>45 s</c>, <c>37 min</c>, <c>1 h 48 min</c>.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
        {
            return string.Format(Culture, Strings.Units.Seconds, Math.Max(0, (int)Math.Round(span.TotalSeconds)));
        }

        if (span.TotalMinutes < 60)
        {
            return string.Format(Culture, Strings.Units.Minutes, (int)Math.Round(span.TotalMinutes));
        }

        int hours = (int)span.TotalHours;
        int minutes = (int)Math.Round(span.TotalMinutes - (hours * 60));
        if (minutes == 60)
        {
            hours++;
            minutes = 0;
        }

        return minutes == 0 ? string.Format(Culture, Strings.Units.Hours, hours) : string.Format(Culture, Strings.Units.HoursMinutes, hours, minutes);
    }

    /// <summary>An estimate: <c>About 1 h 12 min</c> (lower case in sentences: <c>about 1 h 12 min</c>).</summary>
    public static string About(TimeSpan span, bool inSentence = false) =>
        string.Format(Culture, inSentence ? Strings.Units.AboutLower : Strings.Units.About, Duration(span));

    /// <summary><c>142 MB/s</c>.</summary>
    public static string Rate(double bytesPerSecond) => string.Format(Culture, Strings.Units.Rate, Math.Round(bytesPerSecond / (1024 * 1024)).ToString("0", Culture));

    /// <summary><c>1 problem</c> / <c>{n} problems</c>.</summary>
    public static string Plural(long n, string one, string many) => n == 1 ? string.Format(Culture, one, n) : string.Format(Culture, many, Count(n));

    public static string TierName(Severity severity) => severity switch
    {
        Severity.Critical => Strings.TierCritical,
        Severity.High => Strings.TierHigh,
        Severity.Medium => Strings.TierMedium,
        Severity.Low => Strings.TierLow,
        _ => Strings.TierOk,
    };

    public static string DepthName(ScanDepth depth) => depth switch
    {
        ScanDepth.Quick => Strings.Depths.Quick,
        ScanDepth.Deep => Strings.Depths.Deep,
        _ => Strings.Depths.Standard,
    };

    /// <summary>A codec or format name as people know it: <c>FLAC</c>, <c>JPEG</c>, <c>H.264</c>.</summary>
    public static string CodecName(string? codec) => codec?.ToLowerInvariant() switch
    {
        null or "" => string.Empty,
        "mjpeg" or "jpeg" or "jpg" => "JPEG",
        "h264" => "H.264",
        "hevc" or "h265" => "HEVC",
        "mpeg4" => "MPEG-4",
        "mpeg2video" => "MPEG-2",
        "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_s16be" or "pcm_s24be" => "PCM",
        "vorbis" => "Vorbis",
        "opus" => "Opus",
        string other => other.ToUpperInvariant(),
    };

    /// <summary>
    /// The Format column of Library → Browse (subplan 09): audio <c>FLAC 44.1 kHz 16-bit</c>, video
    /// <c>HEVC 2160p</c>, photo <c>JPEG 1400 × 1400</c>; pending and skipped files say so.
    /// </summary>
    public static string FileFormat(Core.Storage.FileRow file)
    {
        CultureInfo culture = Culture;
        switch (file.State)
        {
            case Core.Storage.FileState.Pending:
                return Strings.Library.NotScannedYet;
            case Core.Storage.FileState.Skipped:
                return string.Format(culture, Strings.Library.Skipped, SkipText(file.SkipReason));
        }

        string?[] parts = file.Kind switch
        {
            MediaKind.Audio =>
            [
                CodecName(file.AudioCodec),
                file.SampleRate is int rate ? string.Format(culture, Strings.Drawer.Kilohertz, (rate / 1000.0).ToString("0.#", culture)) : null,
                file.BitDepth is int bits ? string.Format(culture, Strings.Drawer.Bits, bits) : null,
            ],
            MediaKind.Video =>
            [
                CodecName(file.VideoCodec),
                file.Height is int height ? string.Format(culture, Strings.Drawer.Progressive, height) : null,
            ],
            _ =>
            [
                CodecName(file.VideoCodec ?? file.Container),
                file.Width is int w && file.Height is int h ? string.Format(culture, Strings.Drawer.Dimensions, w, h) : null,
            ],
        };
        return string.Join(' ', parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>Why a file was skipped, as the Format column words it.</summary>
    public static string SkipText(Core.Storage.SkipReason reason) => reason switch
    {
        Core.Storage.SkipReason.CloudPlaceholder => Strings.Library.SkipCloud,
        Core.Storage.SkipReason.InProgress => Strings.Library.SkipInProgress,
        Core.Storage.SkipReason.Locked => Strings.Library.SkipLocked,
        Core.Storage.SkipReason.AccessDenied => Strings.Library.SkipDenied,
        Core.Storage.SkipReason.TooLarge => Strings.Library.SkipTooLarge,
        _ => Strings.Library.SkipUnsupported,
    };

    /// <summary>Joins facts with the separator (<c> · </c>).</summary>
    public static string Facts(params string?[] facts) => string.Join(Strings.Separator, facts.Where(f => !string.IsNullOrEmpty(f)));

    /// <summary>Folder names joined for sentences: <c>Music</c>, <c>Music and Photos</c>, <c>Music, Photos and Videos</c>.</summary>
    public static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + Strings.And + names[^1],
    };
}
