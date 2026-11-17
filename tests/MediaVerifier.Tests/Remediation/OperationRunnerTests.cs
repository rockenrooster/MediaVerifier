using System.Security.Cryptography;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Remediation;

/// <summary>The safe-operation protocol (subplan 12, tasks R-01 to R-07) on generated fixtures.</summary>
[Trait("Category", "Integration")]
public sealed class OperationRunnerTests
{
    [FixtureFact]
    public async Task Remux_keeps_the_streams_and_undo_brings_back_the_exact_original()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = await ScannedAsync(runner, "t.mkv");
        FileRecord file = harness.File1("t.mkv");
        string path = Path.Combine(harness.Library, "t.mkv");
        byte[] original = await File.ReadAllBytesAsync(path);
        OperationRunner operations = Runner(harness, runner);

        OperationOutcome done = await operations.RunAsync(file.Id, new RemuxAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.Empty(Directory.GetFiles(harness.Library, "*.mvtmp-*"));
        string kept = OperationRunner.QuarantinePathFor(harness.Database.Roots.Get(file.RootId)!, file, done.OperationId);
        Assert.Equal(original, await File.ReadAllBytesAsync(kept));
        Assert.True((File.GetAttributes(Path.Combine(harness.Library, OperationRunner.QuarantineFolder)) & FileAttributes.Hidden) != 0);
        FileRecord repaired = harness.Database.Files.Get(file.Id)!;
        Assert.Equal(new FileInfo(path).Length, repaired.Size);
        Assert.Contains(harness.Database.Events.ForFile(file.Id), e => e.Kind == FileEventKind.Repaired);

        Assert.Null(await operations.UndoAsync(done.OperationId, 2, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        FileRecord restored = harness.Database.Files.Get(file.Id)!;
        Assert.Equal(file.Size, restored.Size);
        Assert.Equal(file.MtimeUtc, restored.MtimeUtc);
        Assert.Equal(file.Hash, restored.Hash);
        Assert.Equal(OperationStatus.Undone, harness.Database.Journal.List().Single(o => o.Id == done.OperationId).Status);
    }

    [FixtureTheory]
    [InlineData("produce")]
    [InlineData("verify")]
    [InlineData("backup")]
    [InlineData("rename")]
    [InlineData("index")]
    public async Task A_failure_at_any_step_leaves_the_original_and_no_stray_files(string step)
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = await ScannedAsync(runner, "t.mkv");
        FileRecord file = harness.File1("t.mkv");
        string path = Path.Combine(harness.Library, "t.mkv");
        byte[] original = await File.ReadAllBytesAsync(path);
        OperationRunner operations = Runner(harness, runner);
        operations.BeforeStep = name =>
        {
            if (name == step)
            {
                throw new IOException("Injected failure at " + name);
            }
        };

        OperationOutcome outcome = await operations.RunAsync(file.Id, new RemuxAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, outcome.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(harness.Library, "*.mvtmp-*", SearchOption.AllDirectories));
        string quarantine = Path.Combine(harness.Library, OperationRunner.QuarantineFolder);
        Assert.True(!Directory.Exists(quarantine) || Directory.GetFiles(quarantine, "*", SearchOption.AllDirectories).Length == 0);
        Assert.Equal(file.Size, harness.Database.Files.Get(file.Id)!.Size);
    }

    [FixtureFact]
    public async Task A_corrupted_output_fails_verification_and_the_original_stays()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = await ScannedAsync(runner, "pad24.flac");
        FileRecord file = harness.File1("pad24.flac");
        string path = Path.Combine(harness.Library, "pad24.flac");
        byte[] original = await File.ReadAllBytesAsync(path);
        OperationRunner operations = Runner(harness, runner);

        // A truncated encode: cut the temporary output before it is verified.
        operations.BeforeStep = name =>
        {
            if (name == "verify")
            {
                string temp = Directory.GetFiles(harness.Library, "*.mvtmp-*").Single();
                using FileStream stream = new(temp, FileMode.Open, FileAccess.ReadWrite);
                stream.SetLength(stream.Length / 2);
            }
        };
        OperationOutcome failed = await operations.RunAsync(file.Id, new FlacRewriteAction(shrinkTo16: true), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, failed.Status);
        Assert.Equal(CoreStrings.Remediation.AudioDiffers, failed.Reason);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(harness.Library, "*.mvtmp-*"));

        // Without the fault the 16-bit copy carries the same audio and replaces the original.
        operations.BeforeStep = null;
        OperationOutcome done = await operations.RunAsync(file.Id, new FlacRewriteAction(shrinkTo16: true), 2, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.True(new FileInfo(path).Length < original.Length);
    }

    [FixtureFact]
    public async Task Quarantine_and_undo_round_trip_restores_bytes_and_findings()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = await ScannedAsync(runner, "pad24.flac");
        FileRecord file = harness.File1("pad24.flac");
        string path = Path.Combine(harness.Library, "pad24.flac");
        byte[] original = await File.ReadAllBytesAsync(path);
        int openBefore = harness.Database.Findings.ForFile(file.Id).Count(f => f.Status == FindingStatus.Open);
        Assert.True(openBefore > 0);
        OperationRunner operations = Runner(harness, runner);

        OperationOutcome done = await operations.RunAsync(file.Id, new QuarantineAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.False(File.Exists(path));
        Assert.Equal(FileState.Missing, harness.Database.Files.Get(file.Id)!.State);
        Assert.Equal(0, harness.Database.Findings.ForFile(file.Id).Count(f => f.Status == FindingStatus.Open));

        Assert.Null(await operations.UndoAsync(done.OperationId, 2, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(openBefore, harness.Database.Findings.ForFile(file.Id).Count(f => f.Status == FindingStatus.Open));
        Assert.NotEqual(FileState.Missing, harness.Database.Files.Get(file.Id)!.State);
    }

    [FixtureFact]
    public async Task Fix_extension_renames_with_a_suffix_when_the_name_is_taken()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        FixtureFactory.CopyTo("img.jpg", harness.Library, "photo.png");
        File.WriteAllBytes(Path.Combine(harness.Library, "photo.jpg"), [1, 2, 3]);
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Quick);
        FileRecord file = harness.File1("photo.png");
        Assert.Contains(harness.Database.Findings.ForFile(file.Id), f => f.RuleId == "GEN-EXT-MISMATCH");

        OperationOutcome done = await Runner(harness, runner).RunAsync(file.Id, new FixExtensionAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.Equal(Path.Combine(harness.Library, "photo (2).jpg"), done.ResultPath);
        Assert.True(File.Exists(done.ResultPath));
        FileRecord renamed = harness.Database.Files.Get(file.Id)!;
        Assert.Equal("photo (2).jpg", renamed.Name);
        Assert.Equal(2, harness.AllFiles().Count);
    }

    [FixtureFact]
    public async Task Restore_copy_uses_only_a_verified_healthy_copy()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string damaged = FixtureFactory.CopyTo("src16.flac", harness.Library);
        string healthy = FixtureFactory.CopyTo("src16.flac", harness.Backup);
        byte[] good = await File.ReadAllBytesAsync(healthy);
        await harness.AddRootAsync(harness.Library);
        await harness.AddRootAsync(harness.Backup, RootRole.Backup);
        await harness.ScanAsync(ScanDepth.Quick);

        // Rot: the same size and time, different bytes.
        DateTime written = File.GetLastWriteTimeUtc(damaged);
        Mutate.FlipByte(damaged, 0.5);
        File.SetLastWriteTimeUtc(damaged, written);
        FileRecord file = harness.AllFiles().Single(f => f.Name == "src16.flac" && f.RootId == harness.Database.Roots.List().Single(r => r.Role == RootRole.Library).Id);
        Assert.True(ActionCatalog.HasHealthyCopy(harness.Database, WindowsFileSystem.Instance, file));
        IReadOnlyList<string> actions = ActionCatalog.ForFinding("INT-ROT", file, harness.Database.Roots.Get(file.RootId)!, hasHealthyCopy: true, decodesCleanly: false);
        Assert.Equal(ActionIds.RestoreCopy, actions[0]);
        Assert.Equal(ActionIds.RestoreCopy, ActionCatalog.Recommended(actions));

        OperationOutcome done = await Runner(harness, runner).RunAsync(file.Id, new RestoreCopyAction(WindowsFileSystem.Instance), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.Equal(SHA256.HashData(good), SHA256.HashData(await File.ReadAllBytesAsync(damaged)));

        // A "healthy" copy that changed since it was fingerprinted is refused.
        Mutate.FlipByte(healthy, 0.3);
        Mutate.FlipByte(damaged, 0.6);
        FileRecord again = harness.Database.Files.Get(file.Id)!;
        File.SetLastWriteTimeUtc(damaged, new DateTime(again.MtimeUtc, DateTimeKind.Utc));
        OperationOutcome refused = await Runner(harness, runner).RunAsync(file.Id, new RestoreCopyAction(WindowsFileSystem.Instance), 2, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, refused.Status);
        Assert.Equal(CoreStrings.Remediation.NoHealthyCopy, refused.Reason);
    }

    [FixtureFact]
    public async Task Recovery_after_a_crash_removes_only_the_journaled_temporary_file()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = await ScannedAsync(runner, "t.mkv");
        FileRecord file = harness.File1("t.mkv");
        string source = Path.Combine(harness.Library, "t.mkv");
        long id = await harness.Database.Journal.AddAsync(new OperationRecord { BatchId = 9, Utc = 1, ActionId = ActionIds.Remux, FileId = file.Id, Status = OperationStatus.Running, SourcePath = source });
        string temp = OperationRunner.TempPathFor(source, id);
        string unrelated = OperationRunner.TempPathFor(source, id + 1000);
        await harness.Database.Journal.UpdateAsync(id, OperationStatus.Running, detailJson: "{\"tempPath\":" + System.Text.Json.JsonSerializer.Serialize(temp) + "}");
        File.WriteAllBytes(temp, [1, 2, 3]);
        File.WriteAllBytes(unrelated, [4, 5, 6]);

        Assert.Equal(1, await Runner(harness, runner).RecoverAsync());
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(source));
        Assert.Equal(OperationStatus.Failed, harness.Database.Journal.List().Single(o => o.Id == id).Status);
    }

    [Fact]
    public void The_catalogue_follows_the_rule_order_and_hides_what_does_not_apply()
    {
        RootRecord library = new() { Id = 1, Path = @"D:\Music", Role = RootRole.Library };
        RootRecord share = new() { Id = 2, Path = @"\\nas\music", Role = RootRole.Library };
        FileRecord flac = new() { Id = 1, RootId = 1, Name = "a.flac", Ext = "flac", MediaKind = MediaKind.Audio };
        Assert.Equal([ActionIds.Remux, ActionIds.Rescan, ActionIds.Open, ActionIds.Reveal, ActionIds.CopyPath], ActionCatalog.ForFinding("COR-INDEX", flac, library, false, true));
        Assert.DoesNotContain(ActionIds.RestoreCopy, ActionCatalog.ForFinding("INT-ROT", flac, library, hasHealthyCopy: false, decodesCleanly: true));
        Assert.Contains(ActionIds.ReencodeFlac, ActionCatalog.ForFinding("COR-CHECKSUM", flac, library, false, decodesCleanly: true));
        Assert.DoesNotContain(ActionIds.ReencodeFlac, ActionCatalog.ForFinding("COR-CHECKSUM", flac, library, false, decodesCleanly: false));
        Assert.DoesNotContain(ActionIds.Recycle, ActionCatalog.ForFinding("INT-ROT", flac, share, false, true));
        Assert.Equal(ActionIds.ShrinkBitDepth, ActionCatalog.Recommended(ActionCatalog.ForFinding("AUD-PADDED-BITS", flac, library, false, true)));
        Assert.Null(ActionCatalog.Recommended(ActionCatalog.ForFinding("AUD-FAKE-LOSSLESS", flac, library, false, true)));
    }

    private static async Task<EngineHarness> ScannedAsync(FfRunner runner, string fixture)
    {
        EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        FixtureFactory.CopyTo(fixture, harness.Library);
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Standard);
        return harness;
    }

    private static OperationRunner Runner(EngineHarness harness, FfRunner runner) => new(harness.Database, runner, WindowsFileSystem.Instance, harness.Database.Clock);
}
