using System.Diagnostics;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Remediation;

/// <summary>The pieces of remediation that decide without touching media: watchdog, error mapping, formats and advice.</summary>
public sealed class RemediationRulesTests
{
    [Fact]
    public void A_run_whose_position_keeps_moving_is_never_stopped()
    {
        int stops = 0;
        StallWatch watch = new(TimeSpan.FromMinutes(5), () => stops++);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100; i++)
        {
            // An hour of reports, each a little further on: a long remux is not a stall.
            watch.Observe(Block(i * 1_000_000L), start + (Stopwatch.Frequency * 36 * i));
        }

        Assert.False(watch.Stalled);
        Assert.Equal(0, stops);
    }

    [Fact]
    public void A_run_that_reports_without_moving_is_stopped_once()
    {
        int stops = 0;
        StallWatch watch = new(TimeSpan.FromMinutes(5), () => stops++);
        long start = Stopwatch.GetTimestamp();
        watch.Observe(Block(null), start);
        watch.Observe(Block(5_000_000), start + Stopwatch.Frequency);
        watch.Observe(Block(5_000_000), start + (Stopwatch.Frequency * 200));
        Assert.False(watch.Stalled);
        watch.Observe(Block(5_000_000), start + (Stopwatch.Frequency * 302));
        watch.Observe(Block(5_000_000), start + (Stopwatch.Frequency * 400));
        Assert.True(watch.Stalled);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void The_first_report_starts_the_clock_and_the_last_never_stops_the_run()
    {
        int stops = 0;
        StallWatch watch = new(TimeSpan.FromMinutes(5), () => stops++);
        long start = Stopwatch.GetTimestamp();

        // Opening a big damaged file before the first report is not a stall; nor is the final report.
        watch.Observe(Block(null), start + (Stopwatch.Frequency * 3600));
        watch.Observe(new ProgressBlock(null, 7_000_000, null, null, null, IsEnd: true), start + (Stopwatch.Frequency * 7200));
        Assert.Equal(0, stops);
    }

    [Theory]
    [InlineData(23, IntegrityOutcome.Unreadable)]
    [InlineData(1117, IntegrityOutcome.Unreadable)]
    [InlineData(32, IntegrityOutcome.Locked)]
    [InlineData(5, IntegrityOutcome.AccessDenied)]
    [InlineData(64, IntegrityOutcome.DeviceGone)]
    [InlineData(121, IntegrityOutcome.DeviceGone)]
    [InlineData(54, IntegrityOutcome.DeviceGone)]
    [InlineData(1450, IntegrityOutcome.Locked)]
    [InlineData(362, IntegrityOutcome.Locked)]
    [InlineData(123, IntegrityOutcome.Locked)]
    [InlineData(2, IntegrityOutcome.NotFound)]
    public void Only_media_errors_mean_damage(int code, IntegrityOutcome outcome) =>
        Assert.Equal(outcome, IntegrityReader.MapError(code));

    [Fact]
    public void A_wav_stays_pcm_and_only_flac_is_rewritten()
    {
        Assert.Equal(["-c:a", "pcm_s16le"], FlacRewriteAction.CodecArguments(@"D:\Music\a.WAV", shrinkTo16: true));
        Assert.Equal(["-c:a", "flac", "-sample_fmt", "s16"], FlacRewriteAction.CodecArguments(@"D:\Music\a.flac", shrinkTo16: true));
        Assert.Equal(["-c:a", "flac"], FlacRewriteAction.CodecArguments(@"D:\Music\a.flac", shrinkTo16: false));
        Assert.Null(FlacRewriteAction.CodecArguments(@"D:\Music\a.wav", shrinkTo16: false));
        Assert.Null(FlacRewriteAction.CodecArguments(@"D:\Music\a.aiff", shrinkTo16: true));
        Assert.Equal("pcm", Ff.CodecFamily("pcm_s24le"));
        Assert.Equal(Ff.CodecFamily("pcm_s16le"), Ff.CodecFamily("pcm_s32le"));
        Assert.NotEqual(Ff.CodecFamily("pcm_s24le"), Ff.CodecFamily("flac"));
    }

    [Fact]
    public void Tags_count_as_kept_by_name_and_phone_keys_need_the_keyed_writer()
    {
        Dictionary<string, string> phone = new() { ["major_brand"] = "qt  ", ["com.apple.quicktime.location.ISO6709"] = "+51.5-000.1/", ["encoder"] = "Lavf62" };
        Dictionary<string, string> stripped = new() { ["major_brand"] = "qt  ", ["ENCODER"] = "Lavf63" };
        Assert.Equal(["com.apple.quicktime.location.ISO6709"], Ff.MissingTags(phone, stripped));
        Assert.Empty(Ff.MissingTags(stripped, phone));
        Assert.True(Ff.HasKeyedTags(phone));
        Assert.False(Ff.HasKeyedTags(new Dictionary<string, string> { ["title"] = "Song", ["iTunSMPB"] = "x" }));
    }

    [Fact]
    public void A_repair_addresses_the_rules_that_list_it()
    {
        Assert.Equal(["COR-INDEX", "COR-STREAM-LENGTH", "COR-TIMESTAMPS"], OperationRunner.RulesAddressedBy(ActionIds.Remux).Order(StringComparer.Ordinal));
        Assert.Equal(["GEN-EXT-MISMATCH"], OperationRunner.RulesAddressedBy(ActionIds.FixExtension));
        Assert.Equal(["AUD-PADDED-BITS"], OperationRunner.RulesAddressedBy(ActionIds.ShrinkBitDepth));
        Assert.Contains("INT-ROT", OperationRunner.RulesAddressedBy(ActionIds.RestoreCopy));
        Assert.DoesNotContain("AUD-FAKE-LOSSLESS", OperationRunner.RulesAddressedBy(ActionIds.RestoreCopy));
    }

    [Fact]
    public async Task Advice_counts_only_what_it_can_state_and_skips_rotted_files_for_a_rebuild()
    {
        using TestDatabase db = new();
        long videos = await db.Database.Roots.AddAsync(new RootRecord { Path = @"E:\Videos", Label = "Videos" });
        long music = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(videos, "a", "likely.mp4", "mp4", MediaKind.Video, 1000, 1, null, 1),
            new(videos, "a", "possibly.mp4", "mp4", MediaKind.Video, 1000, 1, null, 1),
            new(videos, "a", "suspicious.mp4", "mp4", MediaKind.Video, 1000, 1, null, 1),
            new(videos, "b", "rotted.mkv", "mkv", MediaKind.Video, 1000, 1, null, 1),
            new(videos, "b", "plain.mkv", "mkv", MediaKind.Video, 1000, 1, null, 1),
            new(music, "album1", "1.flac", "flac", MediaKind.Audio, 30, 1, null, 1),
            new(music, "album2", "1.flac", "flac", MediaKind.Audio, 30, 1, null, 1),
        ]);
        string[] rules = [.. RuleCatalog.All.Select(r => r.Id)];
        await Raise(db, files[0].FileId, rules, new Finding("VID-UPSCALED", Severity.Medium, 0.9f, "likely", Finding.NoEvidence));
        await Raise(db, files[1].FileId, rules, new Finding("VID-UPSCALED", Severity.Low, 0.6f, "possibly", Finding.NoEvidence));
        await Raise(db, files[2].FileId, rules, new Finding("VID-UPSCALED", Severity.Low, 0.3f, "suspicious", Finding.NoEvidence));
        await Raise(db, files[3].FileId, rules, new Finding("COR-INDEX", Severity.Medium, 1f, "no cues", Finding.NoEvidence), new Finding("INT-ROT", Severity.Critical, 1f, "changed", Finding.NoEvidence));
        await Raise(db, files[4].FileId, rules, new Finding("COR-INDEX", Severity.Medium, 1f, "no cues", Finding.NoEvidence));
        await Raise(db, files[5].FileId, rules, new Finding("AUD-FAKE-LOSSLESS", Severity.Medium, 0.6f, "possibly", Finding.NoEvidence));
        await Raise(db, files[6].FileId, rules, new Finding("AUD-FAKE-LOSSLESS", Severity.Low, 0.45f, "suspicious", Finding.NoEvidence));

        IReadOnlyList<Recommendation> advice = RecommendationEngine.Build(db.Database, new ScanSettings(), new NoDisk(), DateTimeOffset.UtcNow, max: 20);
        Assert.Equal([files[0].FileId], advice.Single(a => a.Kind == RecommendationKind.UpscaledVideo).FileIds);
        Recommendation fake = advice.Single(a => a.Kind == RecommendationKind.FakeLossless);
        Assert.Equal(1, fake.FileCount);
        Assert.Equal([files[5].FileId], fake.FileIds);
        Assert.Equal([files[4].FileId], advice.Single(a => a.Kind == RecommendationKind.MissingIndex).FileIds);
    }

    [Fact]
    public async Task Copies_for_the_dashboard_come_from_the_index_without_touching_a_disk()
    {
        using TestDatabase db = new();
        long library = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        long backup = await db.Database.Roots.AddAsync(new RootRecord { Path = @"\\nas\backup", Label = "NAS", Role = RootRole.Backup });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(library, "x", "rot.flac", "flac", MediaKind.Audio, 30, 100, null, 1),
            new(backup, "x", "rot.flac", "flac", MediaKind.Audio, 30, 100, null, 1),
            new(library, "x", "alone.flac", "flac", MediaKind.Audio, 30, 100, null, 1),
        ]);
        byte[] good = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        foreach (FileUpsertResult row in files)
        {
            await db.Database.Files.ApplyUpdateAsync(new FileUpdate(row.FileId) { Baseline = new Baseline(row == files[2] ? [.. good.Reverse()] : good, 30, 100, 1) });
        }

        string[] rules = [.. RuleCatalog.All.Select(r => r.Id)];
        await Raise(db, files[0].FileId, rules, new Finding("INT-ROT", Severity.Critical, 1f, "changed", Finding.NoEvidence));
        await Raise(db, files[2].FileId, rules, new Finding("INT-ROT", Severity.Critical, 1f, "changed", Finding.NoEvidence));

        // NoDisk throws on any look at a file: an offline share would have frozen the Dashboard here.
        Recommendation rot = RecommendationEngine.Build(db.Database, new ScanSettings(), new NoDisk(), DateTimeOffset.UtcNow, max: 20).Single(a => a.Kind is RecommendationKind.RotWithCopies);
        Assert.Equal(2, rot.FileCount);
        Assert.Equal(1, rot.Covered);
        Assert.Equal("NAS", rot.Place);
        Assert.Null(rot.ActionId);
    }

    [Fact]
    public async Task An_interrupted_scan_counts_each_finished_file_once()
    {
        using TestDatabase db = new();
        await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        long run = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Standard, "[]", null);
        await db.Database.Runs.EndAsync(run, RunStatus.Interrupted, new RunCounters(30_477, 24_456, 24_474, 1000, 0, 0, 0), null, null);

        Recommendation interrupted = RecommendationEngine.Build(db.Database, new ScanSettings(), new NoDisk(), DateTimeOffset.UtcNow, max: 20).Single(a => a.Kind == RecommendationKind.InterruptedScan);
        Assert.Equal(24_474, interrupted.Done);
        Assert.Equal(30_477, interrupted.Total);
    }

    private static ProgressBlock Block(long? outTime) => new(null, outTime, null, null, null, IsEnd: false);

    private static async Task Raise(TestDatabase db, long fileId, string[] rules, params Finding[] findings)
    {
        await db.Database.Findings.ReplaceForFileAsync(fileId, 1, rules, findings, false);
        await db.Database.Writer.FlushAsync();
    }

    /// <summary>Fails any look at the disk.</summary>
    private sealed class NoDisk : IMediaFileSystem
    {
        public IEnumerable<FsEntry> EnumerateDirectory(string path) => throw new InvalidOperationException("The disk was touched: " + path);

        public bool DirectoryExists(string path) => throw new InvalidOperationException("The disk was touched: " + path);

        public bool FileExists(string path) => throw new InvalidOperationException("The disk was touched: " + path);

        public FileStat? Stat(string path) => throw new InvalidOperationException("The disk was touched: " + path);

        public Stream OpenRead(string path, int bufferSize = 4096, bool sequential = true) => throw new InvalidOperationException("The disk was touched: " + path);
    }
}
