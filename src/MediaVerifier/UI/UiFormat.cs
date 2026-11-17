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
