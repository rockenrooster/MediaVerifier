using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;

namespace MediaVerifier.Diagnostics;

/// <summary>
/// The data the reference mockups show (subplan 09, Demo data for the harness; task UI-01): names and
/// numbers taken from docs/design/*.svg, dates relative to a fixed now of Monday 16 November 2026, 14:20.
/// </summary>
internal sealed partial class DemoUiData : IUiData
{
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 16, 14, 20, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 16, 14, 20, 0))).ToUniversalTime();

    public static IClock Clock { get; } = new FixedClock(Now);

    public ShellCounts LoadShell() => new(128, Severity.Critical, 2, true, UpdateAvailable: true);

    /// <summary>Services for the render harness: an empty throwaway database, the demo data and the demo scan.</summary>
    public static UiServices CreateServices(AppState state, string root)
    {
        Database database = Database.Open(Path.Combine(root, "demo.db"), Clock);
        Core.Ffmpeg.FfmpegLocation location = new(Path.Combine(root, "ffmpeg"), IsOverride: false);
        Core.Ffmpeg.BundleInfo info = new() { FfmpegVersion = Core.Ffmpeg.FfmpegVersions.Bundled, ExtraVersion = "mediaverifier-1", Licence = "LGPL 2.1 or later" };
        UiServices services = new()
        {
            State = state,
            Database = database,
            Clock = Clock,
            Scans = new DemoScanControl(),
            MediaEngine = new MediaEngineInfo(location, new Core.Ffmpeg.BundleCheck(Core.Ffmpeg.BundleState.Ok, info, [], [], null)),
            Data = new DemoUiData(),
            IsDemo = true,
        };
        services.Own(database);
        return services;
    }

    public DashboardData LoadDashboard() => new(
        HasFolders: true,
        AnyScanned: true,
        EngineMissing: false,
        FilesScanned: 12_507,
        FilesHealthy: 12_345,
        FilesNeedingAttention: 162,
        FilesWithBaseline: 12_507,
        FilesVerified: 10_256,
        ScrubDays: 90,
        Open: new SeverityCounts(3, 14, 96, 212),
        OpenAtLastScan: new SeverityCounts(2, 13, 96, 216),
        LastRun: new RunInfo(41, At(16, 2, 0), At(16, 3, 12), RunTrigger.Scheduled, "Nightly verification", ScanDepth.Standard, "Music, Photos and Videos", 12_507, 2, 1, RunStatus.Completed),
        NextRun: new NextRunInfo(At(17, 3, 0), "Nightly verification"),
        Kinds:
        [
            new KindRow(MediaKind.Photo, 8_201, 212L * 1024 * 1024 * 1024, new SeverityCounts(1, 1, 2, 0), 404),
            new KindRow(MediaKind.Video, 1_105, (long)(3.2 * 1024 * 1024 * 1024 * 1024), new SeverityCounts(0, 6, 18, 7), 27),
            new KindRow(MediaKind.Audio, 3_201, 96L * 1024 * 1024 * 1024, new SeverityCounts(2, 7, 76, 5), 77),
        ],
        Recommendations:
        [
            new RecommendationItem(Severity.Critical, "3 files changed on their own", "A healthy copy exists in Backup (E:) for all 3", "Review", "review-rot"),
            new RecommendationItem(Severity.Medium, "38 videos have no seek index", "Rebuilding is lossless and takes about 4 minutes", "Fix all", "ACT-REMUX"),
            new RecommendationItem(Severity.High, "41 albums look converted from MP3", "They cannot be repaired, only replaced", "Review", "review-fake"),
            new RecommendationItem(Severity.Low, "18% of files not verified in 90 days", "At 2 h per night a full pass takes 41 days", "Verify now", "verify-now"),
        ],
        Activity:
        [
            new ActivityItem("Scheduled scan finished", At(16, 3, 12)),
            new ActivityItem("1 file restored from backup", At(15, 18, 40)),
            new ActivityItem("Scan stopped by you", At(9, 21, 40)),
            new ActivityItem("Folder added: Music", At(12, 10, 5)),
        ],
        OpenProblemsHistory: [302, 310, 322, 317, 328, 340, 337, 329, 334, 341, 337, 332, 329, 330, 325]);

    /// <summary>A local time on a day of 2026, as UTC.</summary>
    public static DateTimeOffset Local(int month, int day, int hour, int minute)
    {
        DateTime local = new(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>A local time on a day of November 2026, as UTC.</summary>
    public static DateTimeOffset At(int day, int hour, int minute)
    {
        DateTime local = new(2026, 11, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public long TickCount64 => Environment.TickCount64;
    }
}

/// <summary>The running scan of the mockups: 12,404 of 58,210 files, 21 %, 142 MB/s, about 1 h 12 min left.</summary>
internal sealed class DemoScanControl : IScanControl
{
    /// <summary>The run of the dashboard and issues mockups.</summary>
    public static readonly EngineStatus Running = new(
        EnginePhase.Scanning,
        42,
        ScanDepth.Standard,
        58_210,
        45_806,
        12_404,
        0,
        0,
        311L * 1024 * 1024 * 1024,
        142 * 1024 * 1024,
        new Dictionary<MediaKind, long>(),
        new SeverityCounts(0, 2, 9, 4),
        [
            new InFlightItem(@"D:\Music\Static Bloom\Static Bloom - 05.flac", "Decoding and analysing", 0.62, MediaKind.Audio),
            new InFlightItem(@"D:\Music\Static Bloom\Static Bloom - 06.flac", "Reading", 0.9, MediaKind.Audio),
            new InFlightItem(@"D:\Photos\2026\IMG_8812.jpg", "Decoding", 0.4, MediaKind.Photo),
        ],
        TimeSpan.FromMinutes(18),
        TimeSpan.FromMinutes(72),
        false,
        0,
        RunTrigger.Manual);

    /// <summary>Running for the dashboard, issues and scans renders; idle for the other pages, as in their mockups.</summary>
    public EngineStatus Status { get; set; } = Running;

    public string ScopeText => "Music and Photos";

    public event Action<RunSummary>? RunCompleted
    {
        add { }
        remove { }
    }

    public Task<long> StartAsync(ScanRequest request, string scopeText) => Task.FromResult(0L);

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void Cancel()
    {
    }
}
