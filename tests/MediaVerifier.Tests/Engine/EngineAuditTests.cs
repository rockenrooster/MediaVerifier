using System.Text.Json;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Engine;

/// <summary>
/// Engine behaviour found wrong in the November 2026 audit: budgets, stopping, folder kinds and exclusions, "Scan
/// again", files that come back, a media engine that cannot start, rule switches, pausing and progress.
/// </summary>
public sealed class EngineAuditTests
{
    [Fact]
    public async Task A_budget_ends_the_run_while_enumeration_waits_on_a_full_queue()
    {
        CountingProvider provider = new(delay: TimeSpan.FromMilliseconds(400));
        using EngineHarness h = new([provider]);
        const int files = 300;
        for (int i = 0; i < files; i++)
        {
            h.Write($"f{i:D3}.jpg", EngineHarness.Bytes(100, i));
        }

        long root = await h.AddRootAsync(h.Library);
        using ScanEngine engine = new(h.Services, NewMutexName()) { QueueCapacity = 8 };

        (int _, RunSummary? summary) = await HeadlessScan.RunAsync(engine, new ScanRequest([new ScanScope(root)], ScanDepth.Standard, RunTrigger.Scheduled, Budget: TimeSpan.FromSeconds(1)))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(RunStatus.BudgetReached, summary!.Status);
        Assert.False(engine.IsRunning);
        Assert.InRange(provider.Calls, 1, files - 1);

        // The rest is still to do, and the next run does it.
        RunSummary next = await h.ScanAsync();
        Assert.Equal(RunStatus.Completed, next.Status);
        Assert.Equal(files, h.AllFiles().Count(f => f.ScannedDepth == ScanDepth.Standard));
    }

    [Fact]
    public async Task A_failed_run_ends_only_after_its_workers_have_stopped()
    {
        BlockingProvider provider = new();

        // The listing of Boom fails only once a file is being analysed, so the failure meets busy workers.
        FailingFileSystem fileSystem = new("Boom")
        {
            Failure = () =>
            {
                SpinWait.SpinUntil(() => provider.Started > 0, TimeSpan.FromSeconds(20));
                return new InvalidOperationException("A listing bug");
            },
        };
        using EngineHarness h = new([provider], fileSystem: fileSystem);
        for (int i = 0; i < 4; i++)
        {
            h.Write($"a{i}.jpg", EngineHarness.Bytes(100, i));
        }

        h.Write(@"Boom\b.jpg", EngineHarness.Bytes(100, 9));
        await h.AddRootAsync(h.Library);
        int runningAtEnd = -1;
        h.Engine.RunCompleted += _ => runningAtEnd = provider.Running;

        RunSummary run = await h.ScanAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.True(provider.Started > 0, "no file reached the provider before the failure");
        Assert.Equal(0, runningAtEnd);
    }

    [Fact]
    public async Task Stop_while_enumeration_waits_on_a_full_queue_ends_after_the_workers()
    {
        BlockingProvider provider = new();
        using EngineHarness h = new([provider]);
        for (int i = 0; i < 60; i++)
        {
            h.Write($"f{i:D2}.jpg", EngineHarness.Bytes(100, i));
        }

        long root = await h.AddRootAsync(h.Library);
        using ScanEngine engine = new(h.Services, NewMutexName()) { QueueCapacity = 4 };
        int runningAtEnd = -1;
        engine.RunCompleted += _ => runningAtEnd = provider.Running;
        await engine.StartAsync(new ScanRequest([new ScanScope(root)], ScanDepth.Standard, RunTrigger.Manual));
        await WaitFor(() => provider.Running > 0);

        engine.Cancel();
        await engine.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, runningAtEnd);
        Assert.Equal(RunStatus.Cancelled, h.Database.Runs.List(1)[0].Status);
    }

    [Fact]
    public async Task Pause_reaches_paused_while_enumeration_keeps_the_queue_full()
    {
        BlockingProvider provider = new();
        using EngineHarness h = new([provider]);
        for (int i = 0; i < 60; i++)
        {
            h.Write($"f{i:D2}.jpg", EngineHarness.Bytes(100, i));
        }

        long root = await h.AddRootAsync(h.Library);
        using ScanEngine engine = new(h.Services, NewMutexName()) { QueueCapacity = 4, PauseGrace = TimeSpan.FromMilliseconds(200) };
        await engine.StartAsync(new ScanRequest([new ScanScope(root)], ScanDepth.Standard, RunTrigger.Manual));
        await WaitFor(() => provider.Running > 0 && engine.Status.FilesQueued >= 4);

        // Files still running when the grace ends go back to a queue the listing keeps full; the pause still completes.
        engine.Pause();
        await WaitFor(() => engine.Status.Phase == EnginePhase.Paused);
        Assert.Equal(0, provider.Running);

        engine.Resume();
        await WaitFor(() => provider.Running > 0);
        engine.Cancel();
        await engine.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_folder_scans_only_the_kinds_it_contains_and_forgets_kinds_taken_out()
    {
        using EngineHarness h = new([new CountingProvider()], [new ThresholdDetector("COR-INDEX", fixedThreshold: 1)]);
        h.Write("photo.jpg", EngineHarness.Bytes(100, 1));
        for (int i = 0; i < 60; i++)
        {
            h.Write($@"Voice\memo{i:D2}.amr", EngineHarness.Bytes(100, 100 + i));
        }

        long root = await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        Assert.Equal(61, h.AllFiles().Count);
        Assert.NotEmpty(h.Database.Findings.ForFile(h.File1("memo00.amr").Id));

        // Contains: Photos only. The music rows leave the index with their findings; nothing "disappeared".
        RootRecord record = h.Database.Roots.List().Single(r => r.Id == root);
        await h.Database.Roots.UpdateAsync(record with { MediaMask = MediaKindMask.Photo });
        RunSummary run = await h.ScanAsync();

        Assert.Equal("photo.jpg", Assert.Single(h.AllFiles()).Name);
        Assert.DoesNotContain(run.Alerts, a => a.Id == "RUN-MASS-MISSING");
        Assert.Equal(0, h.Database.Read(c => Count(c, "SELECT COUNT(*) FROM findings f JOIN files x ON x.id = f.file_id WHERE x.ext = 'amr'")));
    }

    [Fact]
    public async Task Excluding_a_folder_or_a_file_type_removes_its_rows_instead_of_marking_them_missing()
    {
        using EngineHarness h = new([new CountingProvider()]);
        h.Write("keep.jpg", EngineHarness.Bytes(100, 1));
        for (int i = 0; i < 60; i++)
        {
            h.Write($@"Thumbnails\t{i:D2}.jpg", EngineHarness.Bytes(100, 10 + i));
        }

        h.Write("scan.png", EngineHarness.Bytes(100, 2));
        long root = await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        Assert.Equal(62, h.AllFiles().Count);

        RootRecord record = h.Database.Roots.List().Single(r => r.Id == root);
        await h.Database.Roots.UpdateAsync(record with { ExcludePatternsJson = JsonSerializer.Serialize(new[] { @"Thumbnails\**" }) });
        h.Settings.PhotoExtensions = [.. h.Settings.PhotoExtensions.Where(e => e != "png")];
        RunSummary run = await h.ScanAsync();

        FileRecord kept = Assert.Single(h.AllFiles());
        Assert.Equal("keep.jpg", kept.Name);
        Assert.Equal(FileState.Ok, kept.State);
        Assert.DoesNotContain(run.Alerts, a => a.Id == "RUN-MASS-MISSING");
        Assert.Equal(0, h.Database.Read(c => Count(c, "SELECT COUNT(*) FROM files WHERE state = 3")));
    }

    [Fact]
    public async Task A_file_that_comes_back_is_no_longer_missing()
    {
        using EngineHarness h = new([new CountingProvider()]);
        h.Write("keep.jpg", EngineHarness.Bytes(100, 1));
        byte[] content = EngineHarness.Bytes(100, 2);
        string path = h.Write("away.jpg", content);
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();

        File.Delete(path);
        await h.ScanAsync();
        Assert.Equal(FileState.Missing, h.File1("away.jpg").State);

        // Restored from the Recycle Bin or a backup: same size and time, so no work is due for it.
        h.Write("away.jpg", content);
        await h.ScanAsync();

        FileRecord back = h.File1("away.jpg");
        Assert.Equal(FileState.Ok, back.State);
        Assert.Contains(h.Database.Events.ForFile(back.Id), e => e.Kind == FileEventKind.Returned);
    }

    [Fact]
    public async Task Scan_again_on_chosen_files_analyses_them_from_scratch_without_listing_the_folder()
    {
        CountingProvider provider = new();
        using EngineHarness h = new([provider]);
        h.Write("a.jpg", EngineHarness.Bytes(100, 1));
        h.Write("b.jpg", EngineHarness.Bytes(100, 2));
        h.Write(@"Sub\c.jpg", EngineHarness.Bytes(100, 3));
        long root = await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        Assert.Equal(3, provider.Calls);
        FileRecord chosen = h.File1("a.jpg");
        long otherSeen = h.File1("b.jpg").LastSeenRun;

        (int _, RunSummary? run) = await HeadlessScan.RunAsync(h.Engine, new ScanRequest([new ScanScope(root, null, [chosen.Id])], ScanDepth.Standard, RunTrigger.Manual));
        await h.Database.Writer.FlushAsync();

        Assert.Equal(RunStatus.Completed, run!.Status);
        Assert.Equal(4, provider.Calls);
        Assert.Equal(1, run.Counters.FilesSeen);
        Assert.Equal(1, run.Counters.FilesAnalysed);
        Assert.Equal(1, run.Counters.FilesVerified);
        Assert.Equal(run.RunId, h.File1("a.jpg").LastSeenRun);
        Assert.Equal(otherSeen, h.File1("b.jpg").LastSeenRun);
    }

    [Fact]
    public async Task A_media_engine_that_cannot_start_leaves_the_findings_open_and_the_file_in_error()
    {
        SwitchableProbeProvider probe = new();
        using EngineHarness h = new([probe], [new ProbeIndexDetector()]);
        h.Write("clip.mp4", EngineHarness.Bytes(1000, 1));
        long root = await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        FileRecord before = h.File1("clip.mp4");
        Assert.Equal(FindingStatus.Open, Assert.Single(h.Database.Findings.ForFile(before.Id)).Status);

        // ffprobe was quarantined: the next analysis of the file runs nothing.
        probe.FailToStart = true;
        (int _, RunSummary? run) = await HeadlessScan.RunAsync(h.Engine, new ScanRequest([new ScanScope(root, null, [before.Id])], ScanDepth.Standard, RunTrigger.Manual));
        await h.Database.Writer.FlushAsync();

        Assert.Equal(1, run!.Counters.Errors);
        FileRecord after = h.File1("clip.mp4");
        Assert.Equal(FileState.Error, after.State);
        Assert.Equal(FindingStatus.Open, Assert.Single(h.Database.Findings.ForFile(after.Id)).Status);
    }

    [Fact]
    public async Task Switching_a_verification_rule_off_resolves_its_findings_and_on_reopens_them()
    {
        FakeStreamHashProvider streams = new(Convert.FromHexString("00112233445566778899aabbccddeeff"));
        CountingProvider provider = new();
        using EngineHarness h = new([provider, streams]);
        string path = h.Write("song.mp3", EngineHarness.Bytes(40_000, 9));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        Mutate.PreserveStat(path, p => Mutate.FlipByte(p, 0.01));
        await h.ScanAsync(ScanDepth.Standard, new ScanOptions { VerifyEverything = true });
        long file = h.File1("song.mp3").Id;
        Assert.Equal(FindingStatus.Open, Assert.Single(h.Database.Findings.ForFile(file)).Status);
        int calls = provider.Calls;

        h.Rules.SetEnabled("INT-META-DRIFT", false);
        ReevaluationResult off = await h.Engine.ReevaluateAsync(["INT-META-DRIFT"]);
        Assert.Equal(new ReevaluationResult(0, 0, 1), off);
        Assert.Equal(FindingStatus.Resolved, Assert.Single(h.Database.Findings.ForFile(file)).Status);
        Assert.Equal(FileState.Ok, h.File1("song.mp3").State);

        h.Rules.SetEnabled("INT-META-DRIFT", true);
        ReevaluationResult on = await h.Engine.ReevaluateAsync(["INT-META-DRIFT"]);
        Assert.Equal(new ReevaluationResult(0, 1, 0), on);
        Assert.Equal(FindingStatus.Open, Assert.Single(h.Database.Findings.ForFile(file)).Status);
        Assert.Equal(FileState.Issues, h.File1("song.mp3").State);
        Assert.Equal(calls, provider.Calls);
    }

    [Fact]
    public async Task Ignored_findings_stay_ignored_when_their_rule_goes_off_and_on_and_never_count_as_appeared()
    {
        using EngineHarness h = new([new CountingProvider()], [new ThresholdDetector("COR-INDEX", fixedThreshold: 1)]);
        h.Write("a.mp4", EngineHarness.Bytes(1000, 1));
        h.Write("b.mp4", EngineHarness.Bytes(1000, 2));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        long ignored = Assert.Single(h.Database.Findings.ForFile(h.File1("a.mp4").Id)).Id;
        Assert.Equal([ignored], await h.Database.Findings.SetStatusAsync([ignored], FindingStatus.Ignored));

        h.Rules.SetEnabled("COR-INDEX", false);
        ReevaluationResult off = await h.Engine.ReevaluateAsync(["COR-INDEX"]);
        Assert.Equal(1, off.NoLongerApply);
        Assert.Equal(FindingStatus.Ignored, h.Database.Findings.Get(ignored)!.Status);

        h.Rules.SetEnabled("COR-INDEX", true);
        ReevaluationResult on = await h.Engine.ReevaluateAsync(["COR-INDEX"]);
        Assert.Equal(1, on.Appeared);
        Assert.Equal(FindingStatus.Ignored, h.Database.Findings.Get(ignored)!.Status);
        Assert.Equal(FindingStatus.Open, Assert.Single(h.Database.Findings.ForFile(h.File1("b.mp4").Id)).Status);
    }

    [Fact]
    public async Task Ignore_takes_only_open_findings()
    {
        using EngineHarness h = new([new CountingProvider()], [new ThresholdDetector("COR-INDEX", fixedThreshold: 1)]);
        h.Write("a.mp4", EngineHarness.Bytes(1000, 1));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        long id = Assert.Single(h.Database.Findings.ForFile(h.File1("a.mp4").Id)).Id;
        await h.Database.Writer.ExecuteAsync(c => FindingStore.Resolve(c, h.File1("a.mp4").Id, "COR-INDEX", "op:7"), ChangeFlags.Findings);

        Assert.Empty(await h.Database.Findings.SetStatusAsync([id], FindingStatus.Ignored));
        FindingRecord resolved = h.Database.Findings.Get(id)!;
        Assert.Equal(FindingStatus.Resolved, resolved.Status);
        Assert.Equal("op:7", resolved.Resolution);
        Assert.Empty(await h.Database.Findings.SetStatusAsync([id], FindingStatus.Open));
    }

    [Fact]
    public async Task Progress_counts_the_files_that_need_no_work_and_shows_the_stage()
    {
        CountingProvider provider = new(delay: TimeSpan.FromSeconds(2));
        using EngineHarness h = new([provider]);
        for (int i = 0; i < 20; i++)
        {
            h.Write($"f{i:D2}.jpg", EngineHarness.Bytes(100, i));
        }

        long root = await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        h.Write("f07.jpg", EngineHarness.Bytes(120, 99), new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        await h.Engine.StartAsync(new ScanRequest([new ScanScope(root)], ScanDepth.Standard, RunTrigger.Manual));

        // The changed photo is being analysed (the provider runs after the stage is set); the other 19 need nothing.
        await WaitFor(() => provider.Calls > 20 && h.Engine.Status.FilesDone == 19);
        EngineStatus status = h.Engine.Status;
        await h.Engine.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(20, status.Total);
        Assert.True(status.Fraction >= 0.95, $"{status.Fraction:P0}");
        Assert.Equal(Core.CoreStrings.ScanStage.Decoding, status.InFlight[0].Stage);
    }

    [Fact]
    public async Task A_folder_that_failed_to_list_keeps_its_rows_when_a_copy_arrives_elsewhere()
    {
        FailingFileSystem fileSystem = new("Album") { Enabled = false };
        using EngineHarness h = new([new CountingProvider()], fileSystem: fileSystem);
        byte[] content = EngineHarness.Bytes(4000, 5);
        h.Write(@"Album\a.jpg", content);
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        long original = h.File1("a.jpg").Id;

        // The album cannot be listed this time, and an identical copy appears in another folder.
        fileSystem.Enabled = true;
        fileSystem.Failure = () => new IOException("The network name is no longer available.");
        h.Write(@"Copies\a.jpg", content);
        await h.ScanAsync();

        List<FileRecord> rows = [.. h.AllFiles().Where(f => f.Name == "a.jpg")];
        Assert.Equal(2, rows.Count);
        FileRecord kept = Assert.Single(rows, r => r.Id == original);
        Assert.Equal("Album", kept.RelDir);
        Assert.DoesNotContain(h.Database.Events.ForFile(original), e => e.Kind == FileEventKind.Moved);
    }

    [Fact]
    public async Task A_run_clears_scratch_folders_that_runs_left_behind()
    {
        using EngineHarness h = new([new CountingProvider()]);
        h.Write("a.jpg", EngineHarness.Bytes(100, 1));
        await h.AddRootAsync(h.Library);
        string stale = h.Services.Paths.JobTempDirectory(99, 5);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "frame.log"), "x");
        string other = Path.Combine(h.Services.Paths.TempDirectory, "selfcheck-1");
        Directory.CreateDirectory(other);

        RunSummary run = await h.ScanAsync();

        Assert.False(Directory.Exists(h.Services.Paths.RunTempDirectory(99)));
        Assert.False(Directory.Exists(h.Services.Paths.RunTempDirectory(run.RunId)));
        Assert.True(Directory.Exists(other));
    }

    private static string NewMutexName() => @"Local\MediaVerifier.Tests.Engine." + Guid.NewGuid().ToString("N");

    private static long Count(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Waits until cancelled, counting files in progress.</summary>
    private sealed class BlockingProvider()
        : ArtifactProvider<FakeArtifact>(new ArtifactCodec<FakeArtifact>((ArtifactKind)101, FakeJsonContext.Default.FakeArtifact))
    {
        private int _running;
        private int _started;

        public int Running => Volatile.Read(ref _running);

        public int Started => Volatile.Read(ref _started);

        public override int Version => 1;

        public override ScanDepth MinDepth => ScanDepth.Quick;

        public override bool Applies(FileContext file) => true;

        protected override async Task<FakeArtifact> ProduceTypedAsync(FileContext file)
        {
            Interlocked.Increment(ref _started);
            Interlocked.Increment(ref _running);
            try
            {
                await Task.Delay(Timeout.Infinite, file.Cancellation);
                return new FakeArtifact(0, file.FullPath);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>The real file system, except that listing one folder fails (with a bug's exception by default).</summary>
    private sealed class FailingFileSystem(string folder) : IMediaFileSystem
    {
        public bool Enabled { get; set; } = true;

        public Func<Exception> Failure { get; set; } = () => new InvalidOperationException("A listing bug");

        public IEnumerable<FsEntry> EnumerateDirectory(string path)
        {
            if (Enabled && string.Equals(Path.GetFileName(path.TrimEnd('\\')), folder, StringComparison.OrdinalIgnoreCase))
            {
                throw Failure();
            }

            return WindowsFileSystem.Instance.EnumerateDirectory(path);
        }

        public bool DirectoryExists(string path) => WindowsFileSystem.Instance.DirectoryExists(path);

        public bool FileExists(string path) => WindowsFileSystem.Instance.FileExists(path);

        public FileStat? Stat(string path) => WindowsFileSystem.Instance.Stat(path);

        public Stream OpenRead(string path, int bufferSize = 4096, bool sequential = true) => WindowsFileSystem.Instance.OpenRead(path, bufferSize, sequential);
    }

    /// <summary>A probe whose run completes, or never starts (ffprobe missing or quarantined).</summary>
    private sealed class SwitchableProbeProvider()
        : ArtifactProvider<ProbeArtifact>(new ArtifactCodec<ProbeArtifact>(ArtifactKind.Probe, ProviderJsonContext.Default.ProbeArtifact))
    {
        public bool FailToStart { get; set; }

        public override int Version => 1;

        public override ScanDepth MinDepth => ScanDepth.Quick;

        public override bool Applies(FileContext file) => true;

        protected override Task<ProbeArtifact> ProduceTypedAsync(FileContext file) => Task.FromResult(FailToStart
            ? new ProbeArtifact(false, false, null, 0, null, null, null, [], LogDigest.Empty, RunOutcome.FailedToStart)
            : new ProbeArtifact(true, false, "mov,mp4", 100, 1, null, file.Stat.Size, [], LogDigest.Empty, RunOutcome.Completed));
    }

    /// <summary>Raises COR-INDEX from a probe that ran; judges nothing from one that did not (as the real detectors).</summary>
    private sealed class ProbeIndexDetector : IDetector
    {
        public IReadOnlyList<string> RuleIds { get; } = ["COR-INDEX"];

        public IReadOnlyList<Type> Needs { get; } = [typeof(ProbeArtifact)];

        public ScanDepth MinDepth => ScanDepth.Quick;

        public bool Applies(DetectorInput input) => true;

        public void Evaluate(DetectorInput input, FindingSink sink)
        {
            if (input.Get<ProbeArtifact>() is { Outcome: RunOutcome.Completed })
            {
                sink.Raise("COR-INDEX", Severity.Medium, 1f, "index", Finding.NoEvidence);
            }
        }
    }
}
