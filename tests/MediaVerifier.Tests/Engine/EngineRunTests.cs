using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Engine;

/// <summary>End-to-end runs over a real temp library with fake providers (tasks E-07..E-10, I-06, I-07, I-10).</summary>
public sealed class EngineRunTests
{
    [Fact]
    public async Task FirstScanIndexesBaselinesAndAnalyses()
    {
        CountingProvider provider = new();
        using EngineHarness h = new([provider], [new ThresholdDetector("COR-INDEX", fixedThreshold: 1500)]);
        h.Write("a.jpg", EngineHarness.Bytes(1000, 1));
        h.Write(@"Music\b.mp3", EngineHarness.Bytes(2000, 2));
        h.Write(@"Video\Clips\c.mp4", EngineHarness.Bytes(3000, 3));
        h.Write(@"Notes\readme.txt", EngineHarness.Bytes(10, 4));
        await h.AddRootAsync(h.Library);

        RunSummary run = await h.ScanAsync();

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(3, run.Counters.FilesSeen);
        Assert.Equal(3, run.Counters.FilesAnalysed);
        Assert.Equal(3, run.Counters.FilesVerified);
        Assert.Equal(3, provider.Calls);
        List<FileRecord> files = h.AllFiles();
        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.NotNull(f.Hash));
        Assert.All(files, f => Assert.Equal(ScanDepth.Standard, f.ScannedDepth));
        Assert.Equal("fakecodec", files[0].Media.AudioCodec);
        FileRecord video = h.File1("c.mp4");
        Assert.Equal(FileState.Issues, video.State);
        Assert.Equal(Severity.Medium, video.WorstSeverity);
        Assert.Equal(FileState.Ok, h.File1("a.jpg").State);
        Assert.Contains(h.Database.Events.ForFile(video.Id), e => e.Kind == FileEventKind.Baselined);
        Assert.Equal(1, run.NewFindings.Medium);
        Assert.Equal(1, run.NewFindings.Low);
        Assert.Equal(Severity.Medium, Assert.Single(h.Database.Findings.ForFile(video.Id)).Severity);
    }

    [Fact]
    public async Task AnUnchangedLibraryNeedsNoWorkOnTheNextRun()
    {
        CountingProvider provider = new();
        using EngineHarness h = new([provider]);
        h.Write("a.jpg", EngineHarness.Bytes(1000, 1));
        h.Write("b.flac", EngineHarness.Bytes(1000, 2));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();

        RunSummary second = await h.ScanAsync();

        Assert.Equal(2, provider.Calls);
        Assert.Equal(0, second.Counters.FilesAnalysed);
        Assert.Equal(0, second.Counters.FilesVerified);
        Assert.Equal(0, second.Counters.BytesRead);
    }

    [Fact]
    public async Task BytesChangedWithTheTimePreservedIsRot()
    {
        using EngineHarness h = new([new CountingProvider()]);
        string path = h.Write("photo.jpg", EngineHarness.Bytes(50_000, 7));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Quick);
        FileRecord before = h.File1("photo.jpg");

        Mutate.PreserveStat(path, p => Mutate.FlipByte(p, 0.5));
        h.Db.Clock.Advance(TimeSpan.FromDays(3));
        RunSummary run = await h.ScanAsync(ScanDepth.Quick, new ScanOptions { VerifyEverything = true });

        FileRecord after = h.File1("photo.jpg");
        Assert.Equal(before.Hash, after.Hash);
        FindingRecord rot = Assert.Single(h.Database.Findings.ForFile(after.Id), f => f.RuleId == "INT-ROT");
        Assert.Equal(Severity.Critical, rot.Severity);
        Assert.Equal(FindingStatus.Open, rot.Status);
        Dictionary<string, object> evidence = EvidenceJson.Deserialize(rot.EvidenceJson);
        Assert.Equal(Convert.ToHexStringLower(before.Hash!), evidence["oldFingerprint"]);
        Assert.NotEqual(evidence["oldFingerprint"], evidence["newFingerprint"]);
        Assert.Contains("without the file being modified", rot.Summary, StringComparison.Ordinal);
        Assert.Contains(h.Database.Events.ForFile(after.Id), e => e.Kind == FileEventKind.Rot);
        Assert.Equal(ScanDepth.Standard, after.ScannedDepth);
        Assert.Equal(1, run.NewFindings.Critical);
    }

    [Fact]
    public async Task TagOnlyChangeWithTheSameStreamIsMetadataDrift()
    {
        FakeStreamHashProvider streams = new(Convert.FromHexString("00112233445566778899aabbccddeeff"));
        using EngineHarness h = new([new CountingProvider(), streams]);
        string path = h.Write("song.mp3", EngineHarness.Bytes(40_000, 9));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        FileRecord before = h.File1("song.mp3");
        Assert.Equal(streams.Hash, before.StreamHash);

        Mutate.PreserveStat(path, p => Mutate.FlipByte(p, 0.01));
        await h.ScanAsync(ScanDepth.Standard, new ScanOptions { VerifyEverything = true });

        FileRecord after = h.File1("song.mp3");
        Assert.NotEqual(before.Hash, after.Hash);
        FindingRecord drift = Assert.Single(h.Database.Findings.ForFile(after.Id));
        Assert.Equal("INT-META-DRIFT", drift.RuleId);
        Assert.Equal(Severity.Low, drift.Severity);
        Assert.Contains(h.Database.Events.ForFile(after.Id), e => e.Kind == FileEventKind.MetaDrift);
    }

    [Fact]
    public async Task AnEditedFileIsReanalysedWithANewBaseline()
    {
        CountingProvider provider = new();
        using EngineHarness h = new([provider]);
        string path = h.Write("clip.mp4", EngineHarness.Bytes(5000, 1));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        byte[] firstHash = h.File1("clip.mp4").Hash!;

        h.Write("clip.mp4", EngineHarness.Bytes(6000, 2), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        await h.ScanAsync();

        FileRecord row = h.File1("clip.mp4");
        Assert.NotEqual(firstHash, row.Hash);
        Assert.Equal(6000, row.Size);
        Assert.Equal(2, provider.Calls);
        Assert.Contains(h.Database.Events.ForFile(row.Id), e => e.Kind == FileEventKind.Modified);
        Assert.DoesNotContain(h.Database.Findings.ForFile(row.Id), f => f.RuleId == "INT-ROT");
        _ = path;
    }

    [Fact]
    public async Task DeletedFilesGoMissingButAnOfflineRootIsLeftAlone()
    {
        using EngineHarness h = new([new CountingProvider()]);
        h.Write("keep.jpg", EngineHarness.Bytes(100, 1));
        string gone = h.Write("gone.jpg", EngineHarness.Bytes(100, 2));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();

        File.Delete(gone);
        await h.ScanAsync();
        FileRecord missing = h.File1("gone.jpg");
        Assert.Equal(FileState.Missing, missing.State);
        Assert.Contains(h.Database.Events.ForFile(missing.Id), e => e.Kind == FileEventKind.Missing);

        string offline = h.Library + "-away";
        Directory.Move(h.Library, offline);
        RunSummary run = await h.ScanAsync();
        Assert.Contains(run.Alerts, a => a.Id == "RUN-ROOT-OFFLINE");
        Assert.Equal(FileState.Ok, h.File1("keep.jpg").State);
        Directory.Move(offline, h.Library);
    }

    [Fact]
    public async Task ARenameIsAMoveByNtfsIdAndKeepsTheRow()
    {
        CountingProvider provider = new();
        using EngineHarness h = new([provider]);
        string path = h.Write(@"Old\track.flac", EngineHarness.Bytes(4000, 5));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        long id = h.File1("track.flac").Id;

        Directory.CreateDirectory(Path.Combine(h.Library, "New"));
        File.Move(path, Path.Combine(h.Library, "New", "renamed.flac"));
        await h.ScanAsync();

        FileRecord moved = Assert.Single(h.AllFiles());
        Assert.Equal(id, moved.Id);
        Assert.Equal("renamed.flac", moved.Name);
        Assert.Equal("New", moved.RelDir);
        Assert.Contains(h.Database.Events.ForFile(id), e => e.Kind == FileEventKind.Moved);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ACopyThenDeleteIsAMoveByFingerprint()
    {
        using EngineHarness h = new([new CountingProvider()]);
        string path = h.Write("a.jpg", EngineHarness.Bytes(4000, 6));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        long id = h.File1("a.jpg").Id;

        string copy = Path.Combine(h.Library, "b.jpg");
        File.Copy(path, copy);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        await h.ScanAsync();

        FileRecord row = Assert.Single(h.AllFiles());
        Assert.Equal(id, row.Id);
        Assert.Equal("b.jpg", row.Name);
        Assert.Contains(h.Database.Events.ForFile(id), e => e.Kind == FileEventKind.Moved);
    }

    [Fact]
    public async Task AProviderRunsOnceWhenThreeDetectorsNeedIt()
    {
        CountingProvider provider = new();
        using EngineHarness h = new([provider], [new ThresholdDetector("COR-INDEX"), new ThresholdDetector("COR-TIMESTAMPS"), new ThresholdDetector("COR-STREAM-LENGTH")]);
        h.Write("v.mp4", EngineHarness.Bytes(4000, 1));
        await h.AddRootAsync(h.Library);

        await h.ScanAsync();

        Assert.Equal(1, provider.Calls);
        Assert.Equal(3, h.Database.Findings.ForFile(h.File1("v.mp4").Id).Count);
    }

    [Fact]
    public async Task ChangingAThresholdReevaluatesWithoutProviderCalls()
    {
        CountingProvider provider = new();
        using EngineHarness h = new([provider], [new ThresholdDetector("COR-STREAM-LENGTH", "seconds")]);
        h.Write("small.mp4", EngineHarness.Bytes(300, 1));
        h.Write("large.mp4", EngineHarness.Bytes(1000, 2));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync();
        Assert.Equal(2, h.Database.Queries.SeverityCounts().Total);

        h.Rules.SetParameter("COR-STREAM-LENGTH", "seconds", 500);
        ReevaluationResult result = await h.Engine.ReevaluateAsync(["COR-STREAM-LENGTH"]);

        Assert.Equal(2, provider.Calls);
        Assert.Equal(1, result.NoLongerApply);
        Assert.Equal(1, h.Database.Queries.SeverityCounts().Total);
    }

    [Fact]
    public async Task PauseResumeAndCancel()
    {
        CountingProvider provider = new(delay: TimeSpan.FromMilliseconds(300));
        using EngineHarness h = new([provider]);

        // More files than the machine works on at once, so work remains after the pause.
        int files = (Environment.ProcessorCount * 3) + 20;
        for (int i = 0; i < files; i++)
        {
            h.Write($"f{i:D3}.jpg", EngineHarness.Bytes(100, i));
        }

        await h.AddRootAsync(h.Library);
        long runId = await h.Engine.StartAsync(new ScanRequest([new ScanScope(h.Database.Roots.List()[0].Id)], ScanDepth.Standard, RunTrigger.Manual));
        await WaitFor(() => provider.Calls > 0);
        h.Engine.Pause();
        await WaitFor(() => h.Engine.Status.Phase == EnginePhase.Paused, TimeSpan.FromSeconds(20));
        int pausedAt = provider.Calls;
        await Task.Delay(700);
        Assert.Equal(pausedAt, provider.Calls);

        h.Engine.Resume();
        await WaitFor(() => provider.Calls > pausedAt);
        h.Engine.Cancel();
        await h.Engine.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        RunRecord run = h.Database.Runs.Get(runId)!;
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.False(h.Engine.IsRunning);
    }

    [Fact]
    public async Task ABudgetStopsTheRunAndTheNextOneContinues()
    {
        CountingProvider provider = new(delay: TimeSpan.FromMilliseconds(400));
        using EngineHarness h = new([provider]);
        for (int i = 0; i < 150; i++)
        {
            h.Write($"f{i:D3}.jpg", EngineHarness.Bytes(100, i));
        }

        // Analysing all 150 files takes several seconds even with many cores; two seconds lets some finish on a slow
        // CI runner and leaves some for the next run on a fast machine.
        await h.AddRootAsync(h.Library);
        RunSummary first = await h.ScanAsync(budget: TimeSpan.FromSeconds(2));
        Assert.Equal(RunStatus.BudgetReached, first.Status);
        int afterFirst = provider.Calls;
        Assert.InRange(afterFirst, 1, 149);

        RunSummary second = await h.ScanAsync();
        Assert.Equal(RunStatus.Completed, second.Status);
        Assert.Equal(150, h.AllFiles().Count(f => f.ScannedDepth == ScanDepth.Standard));
    }

    [Fact]
    public async Task ABackupRootIsFingerprintedButNotAnalysed()
    {
        CountingProvider provider = new();
        using EngineHarness h = new([provider], [new ThresholdDetector("COR-INDEX", fixedThreshold: 1)]);
        string copy = Path.Combine(h.Backup, "copy.mp3");
        File.WriteAllBytes(copy, EngineHarness.Bytes(5000, 1));
        File.SetLastWriteTimeUtc(copy, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await h.AddRootAsync(h.Backup, RootRole.Backup);

        await h.ScanAsync();

        FileRecord row = Assert.Single(h.AllFiles());
        Assert.NotNull(row.Hash);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(h.Database.Findings.ForFile(row.Id));
    }

    [Fact]
    public async Task AHealthyCopyInABackupRootIsFoundAndVerified()
    {
        using EngineHarness h = new([new CountingProvider()]);
        byte[] content = EngineHarness.Bytes(30_000, 3);
        string damaged = h.Write("song.flac", content);
        string backup = Path.Combine(h.Backup, "song.flac");
        File.WriteAllBytes(backup, content);
        File.SetLastWriteTimeUtc(backup, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await h.AddRootAsync(h.Library);
        await h.AddRootAsync(h.Backup, RootRole.Backup);
        await h.ScanAsync();

        Mutate.PreserveStat(damaged, p => Mutate.ZeroBlock(p, 0.5));
        await h.ScanAsync(options: new ScanOptions { VerifyEverything = true });
        FileRecord rotted = h.AllFiles().Single(f => f.Name == "song.flac" && f.RootId == 1);
        HealthyCopyFinder finder = new(h.Database, WindowsFileSystem.Instance);

        CopyCandidate copy = Assert.Single(finder.Find(rotted));
        Assert.Equal(backup, copy.FullPath);
        Assert.True(copy.FingerprintMatch);
        Assert.True(await finder.VerifyAsync(copy, rotted.Hash!));

        // A copy that itself changed is rejected.
        File.WriteAllBytes(backup, EngineHarness.Bytes(30_001, 4));
        Assert.DoesNotContain(finder.Find(rotted), c => c.FingerprintMatch);
    }

    [Fact]
    public async Task HeadlessScanReturnsZeroOnSuccess()
    {
        using EngineHarness h = new([new CountingProvider()]);
        h.Write("x.jpg", EngineHarness.Bytes(10, 1));
        long root = await h.AddRootAsync(h.Library);

        (int code, RunSummary? summary) = await HeadlessScan.RunAsync(h.Engine, new ScanRequest([new ScanScope(root)], ScanDepth.Quick, RunTrigger.Scheduled));

        Assert.Equal(HeadlessScan.ExitSuccess, code);
        Assert.Equal(RunStatus.Completed, summary!.Status);
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(25);
        }
    }
}
