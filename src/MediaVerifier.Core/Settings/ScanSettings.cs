using System.Text.Json.Serialization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Settings;

/// <summary>Which tiers raise a Windows notification (Settings → Notifications).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotifyThreshold>))]
public enum NotifyThreshold
{
    CriticalOnly,
    HighAndAbove,
    MediumAndAbove,
    Everything,
}

/// <summary>
/// Settings that shape scans, integrity and notifications (subplan 09, Settings sections Scanning,
/// Integrity, Notifications), with the defaults tabled there.
/// </summary>
public sealed class ScanSettings
{
    // One range per number, for every page that edits it (Settings and the Schedule page's verification card).
    public const int MinStallSeconds = 10;
    public const int MaxStallSeconds = 3600;
    public const int MinScrubPeriodDays = 1;
    public const int MaxScrubPeriodDays = 3650;
    public const int MinScrubBudgetMinutes = 1;
    public const int MaxScrubBudgetMinutes = 24 * 60;
    public const int MinQuarantineDays = 1;
    public const int MaxQuarantineDays = 3650;

    /// <summary>
    /// Folders no scan enters whatever Always skip says (subplan 12: the quarantine is excluded from scans): with the
    /// list edited down to the user's own patterns, quarantined originals and recycled media were indexed as library files.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedSkip = [Remediation.OperationRunner.QuarantineFolder, "$RECYCLE.BIN", "System Volume Information"];

    [JsonConverter(typeof(JsonStringEnumConverter<ScanDepth>))]
    public ScanDepth DefaultDepth { get; set; } = ScanDepth.Standard;

    public bool AskBeforeLargeScans { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<PerformanceLevel>))]
    public PerformanceLevel Speed { get; set; } = PerformanceLevel.Balanced;

    public int ReadersPerHdd { get; set; } = 1;

    public int ReadersPerSsd { get; set; } = 4;

    public int ReadersPerNetwork { get; set; } = 2;

    public List<string> PhotoExtensions { get; set; } = [.. MediaClassifier.DefaultPhoto];

    public List<string> VideoExtensions { get; set; } = [.. MediaClassifier.DefaultVideo];

    public List<string> AudioExtensions { get; set; } = [.. MediaClassifier.DefaultAudio];

    public List<string> AlwaysSkip { get; set; } = [.. EnumerationRules.DefaultExcludedDirectories];

    public bool ScanCloudFiles { get; set; }

    /// <summary>Null means no limit; otherwise 10, 50 or 100 GB.</summary>
    public long? SkipFilesLargerThan { get; set; }

    public int StallSeconds { get; set; } = 60;

    public bool CheckPhotosForUpscaling { get; set; } = true;

    public bool StrictDecoding { get; set; }

    public int ScrubPeriodDays { get; set; } = 90;

    public int ScrubBudgetMinutes { get; set; } = 120;

    public bool VerifyOnAnalysis { get; set; } = true;

    /// <summary>Originals kept after a repair stay in each root's quarantine folder this long (subplan 12).</summary>
    public int EmptyQuarantineAfterDays { get; set; } = 30;

    public bool ShowWindowsNotifications { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<NotifyThreshold>))]
    public NotifyThreshold NotifyAbout { get; set; } = NotifyThreshold.MediumAndAbove;

    public bool QuietHours { get; set; }

    /// <summary>Quiet hours as "HH:mm" local times.</summary>
    public string QuietFrom { get; set; } = "22:00";

    public string QuietTo { get; set; } = "07:00";

    /// <summary>Where scheduled runs post their summary (subplan 13, Optional webhook); null when off.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>The user confirmed plain HTTP for <see cref="WebhookUrl"/> (private addresses only).</summary>
    public bool WebhookAllowHttp { get; set; }

    public int ReadersFor(Storage.StorageClass storage) => storage switch
    {
        Storage.StorageClass.Hdd => Math.Clamp(ReadersPerHdd, 1, 16),
        Storage.StorageClass.Ssd => Math.Clamp(ReadersPerSsd, 1, 32),
        Storage.StorageClass.Network => Math.Clamp(ReadersPerNetwork, 1, 16),
        Storage.StorageClass.Removable => 1,
        _ => 2,
    };

    /// <summary>
    /// A typed extension list as scans use it: split at spaces too ("jpg png gif" was one entry, which was then dropped),
    /// normalised, and the defaults when nothing usable is left, so a typo never stops a whole media kind from being scanned.
    /// </summary>
    public static List<string> ExtensionsOrDefaults(IEnumerable<string>? typed, IReadOnlyList<string> defaults)
    {
        List<string> extensions = MediaClassifier.Normalize((typed ?? []).SelectMany(e => e.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return extensions.Count > 0 ? extensions : [.. defaults];
    }

    /// <summary>The user's Always skip patterns with the <see cref="ProtectedSkip"/> folders in front, each once.</summary>
    public static List<string> WithProtectedSkip(IEnumerable<string> patterns) =>
        [.. ProtectedSkip.Concat(patterns.Select(p => p.Trim()).Where(p => p.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>True for a folder name every scan skips anyway (left out of the editable list).</summary>
    public static bool IsProtectedSkip(string pattern) => ProtectedSkip.Contains(pattern.Trim(), StringComparer.OrdinalIgnoreCase);

    internal void Normalize()
    {
        PhotoExtensions = ExtensionsOrDefaults(PhotoExtensions, MediaClassifier.DefaultPhoto);
        VideoExtensions = ExtensionsOrDefaults(VideoExtensions, MediaClassifier.DefaultVideo);
        AudioExtensions = ExtensionsOrDefaults(AudioExtensions, MediaClassifier.DefaultAudio);
        AlwaysSkip = WithProtectedSkip(AlwaysSkip ?? [.. EnumerationRules.DefaultExcludedDirectories]);
        StallSeconds = Math.Clamp(StallSeconds, MinStallSeconds, MaxStallSeconds);
        ScrubPeriodDays = Math.Clamp(ScrubPeriodDays, MinScrubPeriodDays, MaxScrubPeriodDays);
        ScrubBudgetMinutes = Math.Clamp(ScrubBudgetMinutes, MinScrubBudgetMinutes, MaxScrubBudgetMinutes);
        EmptyQuarantineAfterDays = Math.Clamp(EmptyQuarantineAfterDays, MinQuarantineDays, MaxQuarantineDays);
        QuietFrom ??= "22:00";
        QuietTo ??= "07:00";
        WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim();
        WebhookAllowHttp &= WebhookUrl is not null;
        if (!Enum.IsDefined(DefaultDepth) || DefaultDepth == ScanDepth.None)
        {
            DefaultDepth = ScanDepth.Standard;
        }
    }
}
