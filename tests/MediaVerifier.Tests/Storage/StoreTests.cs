using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Storage;

public sealed class StoreTests
{
    private static readonly HashSet<string> StandardRules = ["COR-DECODE", "COR-CHECKSUM", "AUD-CLIPPING", "COR-INDEX"];

    [Fact]
    public async Task FilesInsertUpdateMoveAndCascadeWithTheirRoot()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });

        FileUpsertResult[] first = await db.Database.Files.UpsertAsync(
        [
            new(root, @"Night Drive\Overpass", "01.flac", "flac", MediaKind.Audio, 100, 10, 5, 1),
            new(root, "", "loose.mp3", "mp3", MediaKind.Audio, 50, 11, null, 1),
        ]);
        Assert.All(first, r => Assert.True(r.Inserted));

        FileUpsertResult[] second = await db.Database.Files.UpsertAsync([new(root, "Night Drive/Overpass/", "01.flac", "flac", MediaKind.Audio, 120, 20, 5, 2)]);
        Assert.False(second[0].Inserted);
        Assert.Equal(first[0].FileId, second[0].FileId);
        FileRecord updated = db.Database.Files.Get(first[0].FileId)!;
        Assert.Equal(120, updated.Size);
        Assert.Equal(@"Night Drive\Overpass", updated.RelDir);
        Assert.Equal(2, updated.LastSeenRun);

        // Directory interning is case-exact: a differently cased folder is a different folder.
        FileUpsertResult[] cased = await db.Database.Files.UpsertAsync([new(root, @"night drive\overpass", "01.flac", "flac", MediaKind.Audio, 1, 1, null, 2)]);
        Assert.True(cased[0].Inserted);

        await db.Database.Writer.ExecuteAsync(c => FileIndex.Move(c, first[0].FileId, root, @"Archive\2026", "01 - Overpass.flac"), ChangeFlags.Files);
        FileRecord moved = db.Database.Files.Get(first[0].FileId)!;
        Assert.Equal(@"Archive\2026", moved.RelDir);
        Assert.Equal("01 - Overpass.flac", moved.Name);

        await db.Database.Findings.ReplaceForFileAsync(first[0].FileId, 2, StandardRules, [Raise("COR-DECODE", Severity.High)], contentChanged: false);
        await db.Database.Artifacts.PutAsync(first[0].FileId, ArtifactKind.Decode, 1, "{}");
        await db.Database.Roots.RemoveAsync(root);

        Assert.Equal(0, db.Database.Files.Count());
        Assert.Null(db.Database.Artifacts.Get(first[0].FileId, ArtifactKind.Decode));
        Assert.Empty(db.Database.Findings.ForFile(first[0].FileId));
    }

    [Fact]
    public async Task NewFindingIsOpenAndUpdatesTheFileSummary()
    {
        using TestDatabase db = new();
        long file = await NewFile(db);

        FindingChange change = await db.Database.Findings.ReplaceForFileAsync(file, 1, StandardRules, [Raise("COR-DECODE", Severity.High), Raise("AUD-CLIPPING", Severity.Low)], false);

        Assert.Equal(2, change.New);
        FileRecord row = db.Database.Files.Get(file)!;
        Assert.Equal(Severity.High, row.WorstSeverity);
        Assert.Equal(2, row.OpenFindings);
        Assert.Equal(FileState.Issues, row.State);
    }

    [Fact]
    public async Task RedetectionUpdatesEvidenceAndSeverity()
    {
        using TestDatabase db = new();
        long file = await NewFile(db);
        await db.Database.Findings.ReplaceForFileAsync(file, 1, StandardRules, [Raise("COR-DECODE", Severity.Medium, "3 errors")], false);
        db.Clock.Advance(TimeSpan.FromDays(1));

        FindingChange change = await db.Database.Findings.ReplaceForFileAsync(file, 2, StandardRules, [Raise("COR-DECODE", Severity.Critical, "40 errors")], false);

        Assert.Equal(0, change.New);
        Assert.Equal(1, change.Updated);
        FindingRecord finding = Assert.Single(db.Database.Findings.ForFile(file));
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Equal("40 errors", finding.Summary);
        Assert.Equal(1, finding.FirstRun);
        Assert.Equal(2, finding.LastRun);
        Assert.True(finding.LastUtc > finding.FirstUtc);
    }

    [Fact]
    public async Task EvaluatedRuleThatNoLongerFiresIsResolvedAsNotReproduced()
    {
        using TestDatabase db = new();
        long file = await NewFile(db);
        await db.Database.Findings.ReplaceForFileAsync(file, 1, StandardRules, [Raise("COR-DECODE", Severity.High)], false);

        FindingChange change = await db.Database.Findings.ReplaceForFileAsync(file, 2, StandardRules, [], false);

        Assert.Equal(1, change.Resolved);
        FindingRecord finding = Assert.Single(db.Database.Findings.ForFile(file));
        Assert.Equal(FindingStatus.Resolved, finding.Status);
        Assert.Equal(FindingStore.NotReproduced, finding.Resolution);
        FileRecord row = db.Database.Files.Get(file)!;
        Assert.Equal(Severity.None, row.WorstSeverity);
        Assert.Equal(FileState.Ok, row.State);
    }

    [Fact]
    public async Task AShallowerScanLeavesDeeperFindingsUntouched()
    {
        using TestDatabase db = new();
        long file = await NewFile(db);
        await db.Database.Findings.ReplaceForFileAsync(file, 1, StandardRules, [Raise("COR-DECODE", Severity.High)], false);

        // A Quick scan evaluates only Quick rules: COR-DECODE (Standard) was not judged.
        await db.Database.Findings.ReplaceForFileAsync(file, 2, ["COR-INDEX", "GEN-EMPTY"], [], false);

        Assert.Equal(FindingStatus.Open, Assert.Single(db.Database.Findings.ForFile(file)).Status);
    }

    [Fact]
    public async Task IgnoredStaysIgnoredUntilTheContentChanges()
    {
        using TestDatabase db = new();
        long file = await NewFile(db);
        await db.Database.Findings.ReplaceForFileAsync(file, 1, StandardRules, [Raise("AUD-CLIPPING", Severity.Medium)], false);
        long id = Assert.Single(db.Database.Findings.ForFile(file)).Id;
        await db.Database.Findings.SetStatusAsync([id], FindingStatus.Ignored);
        Assert.Equal(0, db.Database.Files.Get(file)!.OpenFindings);

        await db.Database.Findings.ReplaceForFileAsync(file, 2, StandardRules, [Raise("AUD-CLIPPING", Severity.Medium)], contentChanged: false);
        Assert.Equal(FindingStatus.Ignored, Assert.Single(db.Database.Findings.ForFile(file)).Status);

        FindingChange change = await db.Database.Findings.ReplaceForFileAsync(file, 3, StandardRules, [Raise("AUD-CLIPPING", Severity.Medium)], contentChanged: true);
        Assert.Equal(1, change.New);
        Assert.Equal(FindingStatus.Open, Assert.Single(db.Database.Findings.ForFile(file)).Status);
        Assert.Equal(1, db.Database.Files.Get(file)!.OpenFindings);
    }

    [Fact]
    public async Task ResolvedFindingThatFiresAgainReopensAsNew()
    {
        using TestDatabase db = new();
        long file = await NewFile(db);
        await db.Database.Findings.ReplaceForFileAsync(file, 1, StandardRules, [Raise("COR-CHECKSUM", Severity.High)], false);
        await db.Database.Findings.ReplaceForFileAsync(file, 2, StandardRules, [], false);

        FindingChange change = await db.Database.Findings.ReplaceForFileAsync(file, 3, StandardRules, [Raise("COR-CHECKSUM", Severity.High)], false);

        Assert.Equal(1, change.New);
        FindingRecord finding = Assert.Single(db.Database.Findings.ForFile(file));
        Assert.Equal(FindingStatus.Open, finding.Status);
        Assert.Equal(3, finding.FirstRun);
        Assert.Null(finding.Resolution);
    }

    [Fact]
    public async Task RemediationResolvesWithTheOperationAsReason()
    {
        using TestDatabase db = new();
        long file = await NewFile(db);
        await db.Database.Findings.ReplaceForFileAsync(file, 1, StandardRules, [Raise("COR-INDEX", Severity.Medium)], false);

        await db.Database.Writer.ExecuteAsync(c => FindingStore.Resolve(c, file, "COR-INDEX", "operation 42"), ChangeFlags.Findings);

        FindingRecord finding = Assert.Single(db.Database.Findings.ForFile(file));
        Assert.Equal(FindingStatus.Resolved, finding.Status);
        Assert.Equal("operation 42", finding.Resolution);
    }

    [Fact]
    public async Task FolderIgnoreHidesExistingAndFutureFindingsAndUndoReopensThem()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, @"Live\2019", "a.flac", "flac", MediaKind.Audio, 1, 1, null, 1),
            new(root, "Studio", "b.flac", "flac", MediaKind.Audio, 1, 1, null, 1),
        ]);
        await db.Database.Findings.ReplaceForFileAsync(files[0].FileId, 1, StandardRules, [Raise("AUD-CLIPPING", Severity.Medium)], false);
        await db.Database.Findings.ReplaceForFileAsync(files[1].FileId, 1, StandardRules, [Raise("AUD-CLIPPING", Severity.Medium)], false);
        long liveDir = db.Database.Files.Get(files[0].FileId)!.DirId;
        long parentOfLive = db.Database.Queries.FolderRollup(root).Single().Children.Single(c => c.Name == "Live").Id;
        Assert.NotEqual(liveDir, parentOfLive);

        long ignore = await db.Database.Ignores.AddAsync(root, parentOfLive, "AUD-CLIPPING", null);

        Assert.Equal(FindingStatus.Ignored, Assert.Single(db.Database.Findings.ForFile(files[0].FileId)).Status);
        Assert.Equal(FindingStatus.Open, Assert.Single(db.Database.Findings.ForFile(files[1].FileId)).Status);

        await db.Database.Findings.ReplaceForFileAsync(files[0].FileId, 2, StandardRules, [Raise("AUD-CLIPPING", Severity.High)], contentChanged: true);
        Assert.Equal(FindingStatus.Ignored, Assert.Single(db.Database.Findings.ForFile(files[0].FileId)).Status);

        await db.Database.Ignores.RemoveAsync(ignore);
        Assert.Equal(FindingStatus.Open, Assert.Single(db.Database.Findings.ForFile(files[0].FileId)).Status);
        Assert.Empty(db.Database.Ignores.List());
    }

    /// <summary>
    /// Undo of Ignore and Stop ignoring: an ignored finding opens again, and one hidden by a folder ignore removes the
    /// ignore, so the next scan does not hide it again. Open findings are left alone.
    /// </summary>
    [Fact]
    public async Task ReopenShowsIgnoredFindingsAgainAndLiftsTheirFolderIgnore()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, "Live", "a.flac", "flac", MediaKind.Audio, 1, 1, null, 1),
            new(root, "Studio", "b.flac", "flac", MediaKind.Audio, 1, 1, null, 1),
            new(root, "Studio", "c.flac", "flac", MediaKind.Audio, 1, 1, null, 1),
        ]);
        foreach (FileUpsertResult file in files)
        {
            await db.Database.Findings.ReplaceForFileAsync(file.FileId, 1, StandardRules, [Raise("AUD-CLIPPING", Severity.Medium)], false);
        }

        long one = Assert.Single(db.Database.Findings.ForFile(files[0].FileId)).Id;
        long scoped = Assert.Single(db.Database.Findings.ForFile(files[1].FileId)).Id;
        long open = Assert.Single(db.Database.Findings.ForFile(files[2].FileId)).Id;
        await db.Database.Findings.SetStatusAsync([one], FindingStatus.Ignored);
        await db.Database.Ignores.AddAsync(root, db.Database.Files.Get(files[1].FileId)!.DirId, "AUD-CLIPPING", null);
        Assert.Equal(FindingStatus.Ignored, db.Database.Findings.ForFile(files[2].FileId).Single().Status);
        await db.Database.Findings.SetStatusAsync([open], FindingStatus.Open);

        // c.flac was reopened by hand above; b.flac's finding still hides under the folder ignore.
        Assert.Equal(2, await db.Database.Findings.ReopenAsync([one, scoped, open]));

        Assert.All(files, f => Assert.Equal(FindingStatus.Open, Assert.Single(db.Database.Findings.ForFile(f.FileId)).Status));
        Assert.Empty(db.Database.Ignores.List());
        Assert.Equal(1, db.Database.Files.Get(files[0].FileId)!.OpenFindings);

        await db.Database.Findings.ReplaceForFileAsync(files[1].FileId, 2, StandardRules, [Raise("AUD-CLIPPING", Severity.High)], contentChanged: true);
        Assert.Equal(FindingStatus.Open, Assert.Single(db.Database.Findings.ForFile(files[1].FileId)).Status);
    }

    [Fact]
    public async Task RunsPlansJournalArtifactsNotificationsAndRuleSettingsRoundTrip()
    {
        using TestDatabase db = new();
        Database d = db.Database;

        long plan = await d.Plans.AddAsync(new PlanRecord { Name = "Nightly verification", DefinitionJson = "{\"depth\":1}" });
        long run = await d.Runs.BeginAsync(RunTrigger.Scheduled, ScanDepth.Standard, "{\"roots\":[1]}", plan);
        await d.Runs.EndAsync(run, RunStatus.Completed, new RunCounters(10, 9, 8, 1000, 2, 1, 0), "[]", "{}");
        RunRecord finished = d.Runs.Get(run)!;
        Assert.Equal(RunStatus.Completed, finished.Status);
        Assert.Equal(9, finished.FilesAnalysed);
        Assert.Equal(run, d.Plans.Get(plan)!.LastRunId);

        await d.Plans.UpdateAsync(d.Plans.Get(plan)! with { Name = "Renamed", Enabled = false });
        Assert.False(d.Plans.Get(plan)!.Enabled);
        await d.Plans.RemoveAsync(plan);
        Assert.Empty(d.Plans.List());

        long batch = await d.Journal.NextBatchIdAsync();
        long op = await d.Journal.AddAsync(new OperationRecord { BatchId = batch, ActionId = ActionIds.Remux, SourcePath = @"D:\v.mkv", Status = OperationStatus.Planned });
        await d.Journal.UpdateAsync(op, OperationStatus.Done, resultPath: @"D:\v.mkv", backupPath: @"D:\q\v.mkv");
        OperationRecord done = Assert.Single(d.Journal.List(batchId: batch));
        Assert.Equal(OperationStatus.Done, done.Status);
        Assert.Equal(@"D:\q\v.mkv", done.BackupPath);

        await d.Notifications.AddAsync(new NotificationRecord { Title = "Scan finished", Level = Severity.Medium });
        Assert.Equal(1, d.Notifications.UnreadCount());
        await d.Notifications.MarkAllReadAsync();
        Assert.Equal(0, d.Notifications.UnreadCount());
        await d.Notifications.ClearAllAsync();
        Assert.Empty(d.Notifications.List());

        RuleSettings settings = new();
        settings.SetEnabled("AUD-DUAL-MONO", true);
        settings.SetSeverityCap("COR-DECODE", Severity.High);
        settings.SetParameter("GEN-ZEROFILL", "criticalPercent", 75);
        await d.RuleSettings.SaveAsync(settings);
        db.Reopen();
        RuleSettings loaded = db.Database.RuleSettings.Load();
        Assert.True(loaded.IsEnabled(RuleCatalog.Get("AUD-DUAL-MONO")));
        Assert.Equal(Severity.High, loaded.Cap(RuleCatalog.Get("COR-DECODE")));
        Assert.Equal(75, loaded.Value("GEN-ZEROFILL", "criticalPercent"));
    }

    [Fact]
    public async Task MissingFilesAreMarkedExceptInUnlistedDirectories()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\P" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, "a", "1.jpg", "jpg", MediaKind.Photo, 1, 1, null, 1),
            new(root, "a", "2.jpg", "jpg", MediaKind.Photo, 1, 1, null, 1),
            new(root, "locked", "3.jpg", "jpg", MediaKind.Photo, 1, 1, null, 1),
        ]);
        await db.Database.Files.UpsertAsync([new(root, "a", "1.jpg", "jpg", MediaKind.Photo, 1, 1, null, 2)]);
        long lockedDir = db.Database.Files.Get(files[2].FileId)!.DirId;

        List<long> missing = await db.Database.Writer.ExecuteAsync(c => FileIndex.MarkMissing(c, root, 2, [lockedDir]), ChangeFlags.Files);

        Assert.Equal([files[1].FileId], missing);
        Assert.Equal(FileState.Missing, db.Database.Files.Get(files[1].FileId)!.State);
        Assert.NotEqual(FileState.Missing, db.Database.Files.Get(files[2].FileId)!.State);
    }

    [Fact]
    public async Task BackupPassesIntegrityCheck()
    {
        using TestDatabase db = new();
        await SyntheticLibrary.GenerateAsync(db.Database, new SyntheticLibraryOptions { Files = 2_000 });

        string backup = await db.Database.Maintenance.BackupAsync();

        using Database copy = Database.Open(backup, db.Clock);
        Assert.Empty(await copy.Maintenance.CheckAsync());
        Assert.Equal(2_000, copy.Files.Count());
        Assert.NotNull(db.Database.Maintenance.LastBackupUtc());
        await db.Database.Maintenance.CompactAsync();
        await db.Database.Maintenance.ResetAsync();
        Assert.Equal(0, db.Database.Files.Count());
    }

    private static async Task<long> NewFile(TestDatabase db)
    {
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Lib" + Guid.NewGuid().ToString("N")[..6] });
        FileUpsertResult[] result = await db.Database.Files.UpsertAsync([new(root, "", "x.flac", "flac", MediaKind.Audio, 1, 1, null, 1)]);
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(result[0].FileId) { State = FileState.Ok, ScannedDepth = ScanDepth.Standard });
        return result[0].FileId;
    }

    private static Finding Raise(string rule, Severity severity, string summary = "summary") =>
        new(rule, severity, 1f, summary, new Dictionary<string, object> { ["count"] = 3 });
}
