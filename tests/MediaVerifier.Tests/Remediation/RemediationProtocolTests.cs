using System.Text.Json;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Remediation;

/// <summary>
/// The safe-operation protocol around the actions (subplan 12): journal, quarantine, undo, purge, recovery and the
/// index after a repair, on plain files with an action that needs no FFmpeg.
/// </summary>
public sealed class RemediationProtocolTests
{
    [Fact]
    public async Task Delete_for_good_clears_the_journal_so_the_entry_leaves_quarantine_and_undo()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        OperationRunner runner = Runner(h);
        OperationOutcome done = await runner.RunAsync(h.File1("a.mp3").Id, new QuarantineAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        OperationRecord kept = Journal(h, done.OperationId);
        Assert.True(File.Exists(kept.BackupPath));

        Assert.True(await runner.PurgeOneAsync(done.OperationId) > 0);
        OperationRecord purged = Journal(h, done.OperationId);
        Assert.False(File.Exists(kept.BackupPath));
        Assert.Null(purged.BackupPath);
        Assert.Null(purged.UndoJson);
        Assert.Equal(CoreStrings.Remediation.CannotUndo, await runner.UndoAsync(done.OperationId, 2, CancellationToken.None));

        // The emptied quarantine folders go too.
        Assert.False(Directory.Exists(Path.Combine(h.Library, OperationRunner.QuarantineFolder)));
    }

    [Fact]
    public async Task The_purge_settles_expired_and_vanished_entries_and_keeps_offline_ones()
    {
        using EngineHarness h = await ScannedAsync("a.mp3", "b.mp3");
        OperationRunner runner = Runner(h);
        OperationOutcome a = await runner.RunAsync(h.File1("a.mp3").Id, new QuarantineAction(), 1, CancellationToken.None);
        OperationOutcome b = await runner.RunAsync(h.File1("b.mp3").Id, new QuarantineAction(), 1, CancellationToken.None);

        // b's kept file vanished by hand: a ghost row that used to stay listed forever.
        File.Delete(Journal(h, b.OperationId).BackupPath!);

        // An entry in a folder that is offline now (an unplugged drive): its file may still be there, so it is kept.
        string unplugged = Path.Combine(h.Library, "unplugged");
        string offline = Path.Combine(unplugged, OperationRunner.QuarantineFolder, "9", "c.mp3");
        long ghost = await h.Database.Journal.AddAsync(new OperationRecord { BatchId = 3, Utc = 1, ActionId = ActionIds.Quarantine, Status = OperationStatus.Done, SourcePath = Path.Combine(unplugged, "c.mp3"), BackupPath = offline, UndoJson = "{}" });

        h.Db.Clock.Advance(TimeSpan.FromDays(31));
        (int files, long bytes) = await runner.PurgeAsync(30);
        Assert.Equal(1, files);
        Assert.True(bytes > 0);
        Assert.Null(Journal(h, a.OperationId).BackupPath);
        Assert.Null(Journal(h, b.OperationId).BackupPath);
        Assert.Null(Journal(h, b.OperationId).UndoJson);
        Assert.Equal(offline, Journal(h, ghost).BackupPath);
    }

    [Fact]
    public async Task Undo_of_a_repair_records_the_file_it_sets_aside()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        OperationRunner runner = Runner(h);
        string path = Path.Combine(h.Library, "a.mp3");
        byte[] original = await File.ReadAllBytesAsync(path);
        OperationOutcome done = await runner.RunAsync(h.File1("a.mp3").Id, new AppendAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        byte[] repaired = await File.ReadAllBytesAsync(path);

        Assert.Null(await runner.UndoAsync(done.OperationId, 2, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        OperationRecord undo = h.Database.Journal.List().Single(o => OperationRunner.UndoneOperation(o) == done.OperationId);
        Assert.Equal(OperationStatus.Done, undo.Status);
        Assert.Equal(path, undo.SourcePath);
        Assert.Contains(OperationRunner.QuarantineFolder, undo.BackupPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(repaired, await File.ReadAllBytesAsync(undo.BackupPath!));

        // Not an operation to undo again (Ctrl+Z keeps walking back), but listed, purgeable and restorable by name.
        Assert.Null(undo.UndoJson);
        Assert.Equal(CoreStrings.Remediation.NameTaken, await runner.UndoAsync(undo.Id, 3, CancellationToken.None));
        File.Move(path, path + ".mine");
        Assert.Null(await runner.UndoAsync(undo.Id, 3, CancellationToken.None));
        Assert.Equal(repaired, await File.ReadAllBytesAsync(path));
        Assert.Equal(OperationStatus.Undone, Journal(h, undo.Id).Status);
    }

    [Fact]
    public async Task A_purged_set_aside_file_is_deleted_and_forgotten()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        OperationRunner runner = Runner(h);
        OperationOutcome done = await runner.RunAsync(h.File1("a.mp3").Id, new AppendAction(), 1, CancellationToken.None);
        Assert.Null(await runner.UndoAsync(done.OperationId, 2, CancellationToken.None));
        OperationRecord undo = h.Database.Journal.List().Single(o => OperationRunner.UndoneOperation(o) == done.OperationId);
        string aside = undo.BackupPath!;

        Assert.True(await runner.PurgeOneAsync(undo.Id) > 0);
        Assert.False(File.Exists(aside));
        Assert.Null(Journal(h, undo.Id).BackupPath);
    }

    [Fact]
    public async Task Undo_of_a_quarantine_says_when_the_name_is_taken()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        OperationRunner runner = Runner(h);
        string path = Path.Combine(h.Library, "a.mp3");
        OperationOutcome done = await runner.RunAsync(h.File1("a.mp3").Id, new QuarantineAction(), 1, CancellationToken.None);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Equal(CoreStrings.Remediation.NameTaken, await runner.UndoAsync(done.OperationId, 2, CancellationToken.None));
        Assert.True(File.Exists(Journal(h, done.OperationId).BackupPath));

        File.Delete(Journal(h, done.OperationId).BackupPath!);
        File.Delete(path);
        Assert.Equal(CoreStrings.Remediation.OriginalGone, await runner.UndoAsync(done.OperationId, 3, CancellationToken.None));
    }

    [Fact]
    public async Task A_repair_settles_only_what_it_repairs_and_forgets_the_old_analysis()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        FileRecord file = h.File1("a.mp3");
        await h.Database.Findings.ReplaceForFileAsync(file.Id, 1, ["COR-INDEX", "COR-DECODE"], [new Finding("COR-INDEX", Severity.Medium, 1f, "no index", Finding.NoEvidence), new Finding("COR-DECODE", Severity.High, 1f, "errors", Finding.NoEvidence)], false);
        await h.Database.Artifacts.PutAsync(file.Id, ArtifactKind.Probe, 1, "{}");
        await h.Database.Files.ApplyUpdateAsync(new FileUpdate(file.Id) { StreamHash = [1, 2, 3], ScannedDepth = ScanDepth.Standard });
        await h.Database.Writer.FlushAsync();

        OperationOutcome done = await Runner(h).RunAsync(file.Id, new AppendAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        List<FindingRecord> findings = h.Database.Findings.ForFile(file.Id);
        Assert.Equal(FindingStatus.Resolved, findings.Single(f => f.RuleId == "COR-INDEX").Status);
        Assert.Equal(FindingStatus.Open, findings.Single(f => f.RuleId == "COR-DECODE").Status);

        // The next Standard scan analyses the new content instead of re-raising from the old analysis.
        FileRecord repaired = h.Database.Files.Get(file.Id)!;
        Assert.Empty(h.Database.Artifacts.ForFile(file.Id));
        Assert.Null(repaired.StreamHash);
        Assert.Equal(ScanDepth.Quick, repaired.ScannedDepth);
        Assert.Equal(1, repaired.OpenFindings);

        // Undo reopens exactly what the repair settled.
        Assert.Null(await Runner(h).UndoAsync(done.OperationId, 2, CancellationToken.None));
        Assert.All(h.Database.Findings.ForFile(file.Id), f => Assert.Equal(FindingStatus.Open, f.Status));
    }

    [Fact]
    public async Task A_repair_waits_until_rot_is_settled()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        FileRecord file = h.File1("a.mp3");
        await h.Database.Findings.ReplaceForFileAsync(file.Id, 1, ["INT-ROT", "COR-INDEX"], [new Finding("INT-ROT", Severity.Critical, 1f, "changed", Finding.NoEvidence), new Finding("COR-INDEX", Severity.Medium, 1f, "no index", Finding.NoEvidence)], false);
        await h.Database.Writer.FlushAsync();
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(h.Library, "a.mp3"));

        OperationOutcome refused = await Runner(h).RunAsync(file.Id, new AppendAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, refused.Status);
        Assert.Equal(CoreStrings.Remediation.RotFirst, refused.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(h.Library, "a.mp3")));
        Assert.Equal(file.Hash, h.Database.Files.Get(file.Id)!.Hash);
    }

    [Fact]
    public async Task An_edit_saved_during_a_repair_is_not_replaced()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        string path = Path.Combine(h.Library, "a.mp3");
        OperationOutcome outcome = await Runner(h).RunAsync(h.File1("a.mp3").Id, new AppendAction(during: _ => File.AppendAllText(path, "edited")), 1, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, outcome.Status);
        Assert.Equal(CoreStrings.Remediation.ChangedSinceScan, outcome.Reason);
        Assert.EndsWith("edited", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(h.Library, "*.mvtmp-*"));
        Assert.False(Directory.Exists(Path.Combine(h.Library, OperationRunner.QuarantineFolder)) && Directory.GetFiles(Path.Combine(h.Library, OperationRunner.QuarantineFolder), "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task A_repaired_file_keeps_its_date_when_its_size_changed()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        FileRecord file = h.File1("a.mp3");
        string path = Path.Combine(h.Library, "a.mp3");

        Assert.Equal(OperationStatus.Done, (await Runner(h).RunAsync(file.Id, new AppendAction(), 1, CancellationToken.None)).Status);
        Assert.Equal(file.MtimeUtc, File.GetLastWriteTimeUtc(path).Ticks);
        Assert.Equal(file.MtimeUtc, h.Database.Files.Get(file.Id)!.MtimeUtc);
    }

    [Fact]
    public async Task A_file_in_a_folder_ending_in_a_dot_can_be_moved_to_quarantine()
    {
        using EngineHarness h = new();
        string folder = ExtendedPath.For(Path.Combine(h.Library, "R.E.M."));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "a.mp3");
        File.WriteAllBytes(file, EngineHarness.Bytes(5000, 1));
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Quick);

        OperationOutcome done = await Runner(h).RunAsync(h.File1("a.mp3").Id, new QuarantineAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.False(File.Exists(file));
        Assert.Null(await Runner(h).UndoAsync(done.OperationId, 2, CancellationToken.None));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_file_that_cannot_be_looked_at_fails_alone()
    {
        using EngineHarness h = await ScannedAsync("a.mp3", "b.mp3");
        OperationRunner runner = new(h.Database, h.Services.Runner, new FailingStat("a.mp3"), h.Database.Clock);

        OperationOutcome failed = await runner.RunAsync(h.File1("a.mp3").Id, new QuarantineAction(), 1, CancellationToken.None);
        OperationOutcome done = await runner.RunAsync(h.File1("b.mp3").Id, new QuarantineAction(), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, failed.Status);
        Assert.Equal(FailingStat.Message, failed.Reason);
        Assert.Equal(OperationStatus.Failed, Journal(h, failed.OperationId).Status);
        Assert.Equal(OperationStatus.Done, done.Status);
    }

    [Fact]
    public async Task An_offline_folder_is_named_as_the_reason()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        FileRecord file = h.File1("a.mp3");
        RootRecord root = h.Database.Roots.Get(file.RootId)! with { Path = Path.Combine(h.Library, "gone") };

        Assert.Equal(CoreStrings.Remediation.RootOffline, Runner(h).CheckPreconditions(file, root, new QuarantineAction(), file.Size));
    }

    [Fact]
    public async Task An_empty_file_is_never_restored_from_another_empty_file()
    {
        using EngineHarness h = new();
        h.Write("a.mp3", []);
        h.Write("b.mp3", []);
        string backup = Path.Combine(h.Backup, "a.mp3");
        File.WriteAllBytes(backup, []);
        File.SetLastWriteTimeUtc(backup, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        await h.AddRootAsync(h.Library);
        await h.AddRootAsync(h.Backup, RootRole.Backup);
        await h.ScanAsync(ScanDepth.Quick);
        FileRecord empty = h.AllFiles().Single(f => f.Name == "a.mp3" && f.RootId == 1);
        Assert.Contains(h.Database.Findings.ForFile(empty.Id), f => f.RuleId == "GEN-EMPTY");
        Assert.NotNull(empty.Hash);

        HealthyCopyFinder finder = new(h.Database, WindowsFileSystem.Instance);
        Assert.Empty(finder.Find(empty));
        Assert.Empty(finder.FindRecorded(empty));
        Assert.False(ActionCatalog.HasHealthyCopy(h.Database, WindowsFileSystem.Instance, empty));
        OperationOutcome refused = await Runner(h).RunAsync(empty.Id, new RestoreCopyAction(WindowsFileSystem.Instance), 1, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, refused.Status);
        Assert.Equal(CoreStrings.Remediation.NoHealthyCopy, refused.Reason);
        Assert.Contains(h.Database.Findings.ForFile(empty.Id), f => f.RuleId == "GEN-EMPTY" && f.Status == FindingStatus.Open);
    }

    [Fact]
    public async Task Recovery_completes_a_repair_whose_swap_had_finished()
    {
        using EngineHarness h = await ScannedAsync("a.mp3");
        FileRecord file = h.File1("a.mp3");
        RootRecord root = h.Database.Roots.Get(file.RootId)!;
        string source = Path.Combine(h.Library, "a.mp3");
        byte[] original = await File.ReadAllBytesAsync(source);

        // A crash after the swap, before the journal said Done: the new file in place, the original kept.
        long id = await h.Database.Journal.AddAsync(new OperationRecord { BatchId = 9, Utc = 1, ActionId = ActionIds.Remux, FileId = file.Id, Status = OperationStatus.Running, SourcePath = source });
        string backup = OperationRunner.QuarantinePathFor(root, file, id);
        UndoData undo = new(nameof(ActionClass.Lossless), source, source, backup, file.Size, file.MtimeUtc, Convert.ToHexStringLower(file.Hash!), file.HashSize, file.HashMtimeUtc, file.HashUtc, file.RelDir, file.Name);
        await h.Database.Journal.UpdateAsync(id, OperationStatus.Running, detailJson: JsonSerializer.Serialize(new JournalTemp(OperationRunner.TempPathFor(source, id), backup, undo), RemediationJsonContext.Default.JournalTemp));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Move(source, backup);
        File.WriteAllBytes(source, [.. original, 1, 2, 3]);

        OperationRunner runner = Runner(h);
        Assert.Equal(1, await runner.RecoverAsync());
        OperationRecord recovered = Journal(h, id);
        Assert.Equal(OperationStatus.Done, recovered.Status);
        Assert.Equal(backup, recovered.BackupPath);
        Assert.NotNull(recovered.UndoJson);

        Assert.Null(await runner.UndoAsync(id, 10, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Recovery_brings_an_original_back_into_a_removed_folder_and_waits_for_an_offline_one()
    {
        using EngineHarness h = new();
        h.Write(Path.Combine("Album", "a.mp3"), EngineHarness.Bytes(5000, 1));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Quick);
        FileRecord file = h.File1("a.mp3");
        RootRecord root = h.Database.Roots.Get(file.RootId)!;
        string source = Path.Combine(h.Library, "Album", "a.mp3");
        long id = await h.Database.Journal.AddAsync(new OperationRecord { BatchId = 9, Utc = 1, ActionId = ActionIds.Quarantine, FileId = file.Id, Status = OperationStatus.Running, SourcePath = source });
        string backup = OperationRunner.QuarantinePathFor(root, file, id);
        await h.Database.Journal.UpdateAsync(id, OperationStatus.Running, detailJson: JsonSerializer.Serialize(new JournalTemp(null, backup), RemediationJsonContext.Default.JournalTemp));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Move(source, backup);
        Directory.Delete(Path.Combine(h.Library, "Album"));

        string unplugged = Path.Combine(h.Backup, "unplugged");
        long offline = await h.Database.Journal.AddAsync(new OperationRecord { BatchId = 9, Utc = 1, ActionId = ActionIds.Quarantine, Status = OperationStatus.Running, SourcePath = Path.Combine(unplugged, "b.mp3") });
        await h.Database.Journal.UpdateAsync(offline, OperationStatus.Running, detailJson: JsonSerializer.Serialize(new JournalTemp(null, Path.Combine(unplugged, OperationRunner.QuarantineFolder, "5", "b.mp3")), RemediationJsonContext.Default.JournalTemp));

        Assert.Equal(1, await Runner(h).RecoverAsync());
        Assert.True(File.Exists(source));
        Assert.Equal(OperationStatus.Failed, Journal(h, id).Status);
        Assert.Equal(OperationStatus.Running, Journal(h, offline).Status);
    }

    [Theory]
    [InlineData("undo:12", 12L)]
    [InlineData("undo:7 The kept original is no longer there.", 7L)]
    [InlineData("{\"tempPath\":null}", null)]
    [InlineData(null, null)]
    public void Undo_records_name_the_operation_they_undid(string? detail, long? undone) =>
        Assert.Equal(undone, OperationRunner.UndoneOperation(new OperationRecord { ActionId = ActionIds.Remux, SourcePath = "x", DetailJson = detail }));

    private static async Task<EngineHarness> ScannedAsync(params string[] names)
    {
        EngineHarness h = new();
        int seed = 1;
        foreach (string name in names)
        {
            h.Write(name, EngineHarness.Bytes(5000, seed++));
        }

        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Quick);
        return h;
    }

    private static OperationRunner Runner(EngineHarness h) => new(h.Database, h.Services.Runner, WindowsFileSystem.Instance, h.Database.Clock);

    private static OperationRecord Journal(EngineHarness h, long id) => h.Database.Journal.List(int.MaxValue).Single(o => o.Id == id);

    /// <summary>A Lossless repair without FFmpeg: the new file is the original with a few bytes added.</summary>
    private sealed class AppendAction(Action<OperationContext>? during = null) : IRemediationAction
    {
        public string Id => ActionIds.Remux;

        public ActionClass Class => ActionClass.Lossless;

        public async Task ProduceAsync(OperationContext context, CancellationToken token)
        {
            byte[] data = await File.ReadAllBytesAsync(context.SourcePath, token);
            await File.WriteAllBytesAsync(context.TempPath, [.. data, .. "repaired"u8.ToArray()], token);
            during?.Invoke(context);
        }

        public Task<string?> VerifyAsync(OperationContext context, CancellationToken token) => Task.FromResult<string?>(null);
    }

    /// <summary>The real file system, except that looking at one file name fails as a broken path does.</summary>
    private sealed class FailingStat(string name) : IMediaFileSystem
    {
        public const string Message = "Could not find a part of the path.";

        public IEnumerable<FsEntry> EnumerateDirectory(string path) => WindowsFileSystem.Instance.EnumerateDirectory(path);

        public bool DirectoryExists(string path) => WindowsFileSystem.Instance.DirectoryExists(path);

        public bool FileExists(string path) => WindowsFileSystem.Instance.FileExists(path);

        public FileStat? Stat(string path) => Path.GetFileName(path) == name ? throw new DirectoryNotFoundException(Message) : WindowsFileSystem.Instance.Stat(path);

        public Stream OpenRead(string path, int bufferSize = 4096, bool sequential = true) => WindowsFileSystem.Instance.OpenRead(path, bufferSize, sequential);
    }
}
