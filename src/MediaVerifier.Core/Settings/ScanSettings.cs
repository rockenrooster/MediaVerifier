using System.Text.Json.Serialization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Settings;

/// <summary>Where originals go after a repair (Settings → Integrity).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OriginalsDestination>))]
public enum OriginalsDestination
{
    Quarantine,
    RecycleBin,
    OtherFolder,
}

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

    [JsonConverter(typeof(JsonStringEnumConverter<OriginalsDestination>))]
    public OriginalsDestination KeepOriginalsIn { get; set; } = OriginalsDestination.Quarantine;

    public string? OriginalsFolder { get; set; }

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

    internal void Normalize()
    {
        PhotoExtensions = PhotoExtensions is { Count: > 0 } ? MediaClassifier.Normalize(PhotoExtensions) : [.. MediaClassifier.DefaultPhoto];
        VideoExtensions = VideoExtensions is { Count: > 0 } ? MediaClassifier.Normalize(VideoExtensions) : [.. MediaClassifier.DefaultVideo];
        AudioExtensions = AudioExtensions is { Count: > 0 } ? MediaClassifier.Normalize(AudioExtensions) : [.. MediaClassifier.DefaultAudio];
        AlwaysSkip ??= [.. EnumerationRules.DefaultExcludedDirectories];
        StallSeconds = Math.Clamp(StallSeconds, 10, 3600);
        ScrubPeriodDays = Math.Clamp(ScrubPeriodDays, 1, 3650);
        ScrubBudgetMinutes = Math.Clamp(ScrubBudgetMinutes, 1, 24 * 60);
        EmptyQuarantineAfterDays = Math.Clamp(EmptyQuarantineAfterDays, 1, 3650);
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
