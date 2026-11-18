using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Storage;

/// <summary>Storage behaviour found wrong in the November 2026 audit.</summary>
public sealed class StorageAuditTests
{
    [Fact]
    public async Task A_database_that_cannot_be_opened_stays_where_it_is()
    {
        using TempDirectory temp = new("locked-open");
        string file = temp.Combine("library.db");
        FakeClock clock = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
        using (Database first = Database.Open(file, clock))
        {
            await first.Roots.AddAsync(new RootRecord { Path = @"D:\Photos" });
        }

        // Another program holds the file exclusively: an I/O problem, not damage.
        using (FileStream holder = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            StorageException error = Assert.Throws<StorageException>(() => Database.Open(file, clock));
            Assert.Contains("could not be opened", error.Message, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.EnumerateFiles(temp.Path, "library.damaged-*"));
        using Database reopened = Database.Open(file, clock);
        Assert.Null(reopened.RecoveredDamagedCopy);
        Assert.Single(reopened.Roots.List());
    }

    [Fact]
    public async Task A_window_opened_during_a_background_run_leaves_that_run_running()
    {
        using TestDatabase db = new();
        long run = await db.Database.Runs.BeginAsync(RunTrigger.Scheduled, ScanDepth.Quick, "{}", null);
        db.Database.Dispose();

        using (Database window = Database.Open(db.FilePath, db.Clock, markInterrupted: false))
        {
            Assert.Equal(RunStatus.Running, window.Runs.Get(run)!.Status);
        }

        db.Reopen();
        Assert.Equal(RunStatus.Interrupted, db.Database.Runs.Get(run)!.Status);
    }

    [Fact]
    public async Task An_interrupted_run_ends_at_its_last_checkpoint_not_at_the_next_start()
    {
        using TestDatabase db = new();
        long run = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Standard, "{}", null);
        db.Clock.Advance(TimeSpan.FromMinutes(53));
        long checkpoint = db.Clock.UtcNow.UtcTicks;
        await db.Database.Runs.CheckpointAsync(run, new RunCounters(10, 5, 5, 100, 0, 0, 0));
        await db.Database.Writer.FlushAsync();

        // The process dies; the app starts again two hours later.
        db.Clock.Advance(TimeSpan.FromHours(2));
        db.Reopen();

        RunRecord after = db.Database.Runs.Get(run)!;
        Assert.Equal(RunStatus.Interrupted, after.Status);
        Assert.Equal(checkpoint, after.EndedUtc);
        Assert.Equal(10, after.FilesSeen);
    }

    [Fact]
    public async Task A_finished_run_keeps_its_own_end_time_after_a_late_checkpoint()
    {
        using TestDatabase db = new();
        long run = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Standard, "{}", null);
        db.Clock.Advance(TimeSpan.FromMinutes(5));
        RunCounters counters = new(3, 3, 3, 30, 0, 0, 0);
        await db.Database.Runs.EndAsync(run, RunStatus.Completed, counters, null, null);
        long ended = db.Database.Runs.Get(run)!.EndedUtc!.Value;

        db.Clock.Advance(TimeSpan.FromMinutes(5));
        await db.Database.Runs.CheckpointAsync(run, counters);
        await db.Database.Writer.FlushAsync();

        Assert.Equal(ended, db.Database.Runs.Get(run)!.EndedUtc);
    }

    [Fact]
    public async Task Reset_keeps_the_journal_of_originals_still_in_quarantine()
    {
        using TestDatabase db = new();
        Database d = db.Database;
        await d.Roots.AddAsync(new RootRecord { Path = @"D:\Music" });
        long kept = await d.Journal.AddAsync(new OperationRecord { BatchId = 1, ActionId = ActionIds.Quarantine, SourcePath = @"D:\Music\a.flac", BackupPath = @"D:\Music\.mediaverifier-quarantine\a.flac", Status = OperationStatus.Done });
        await d.Journal.AddAsync(new OperationRecord { BatchId = 2, ActionId = ActionIds.Remux, SourcePath = @"D:\Music\b.flac", Status = OperationStatus.Done });
        long crashed = await d.Journal.AddAsync(new OperationRecord { BatchId = 3, ActionId = ActionIds.Remux, SourcePath = @"D:\Music\c.flac", Status = OperationStatus.Running });

        await d.Maintenance.ResetAsync();

        Assert.Empty(d.Roots.List());
        Assert.Equal([crashed, kept], [.. d.Journal.List().Select(o => o.Id)]);
    }

    [Fact]
    public async Task Findings_report_tier_changes_and_ignore_nothing_twice()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\M" });
        long file = (await db.Database.Files.UpsertAsync([new(root, "", "a.flac", "flac", MediaKind.Audio, 1, 1, null, 1)]))[0].FileId;
        string[] rules = ["AUD-CLIPPING"];
        await db.Database.Findings.ReplaceForFileAsync(file, 1, rules, [new Finding("AUD-CLIPPING", Severity.Low, 1f, "x", Finding.NoEvidence)], false);

        FindingChange tier = await db.Database.Findings.ReplaceForFileAsync(file, 2, rules, [new Finding("AUD-CLIPPING", Severity.Medium, 1f, "x", Finding.NoEvidence)], false);
        Assert.Equal(1, tier.SeverityChanged);
        Assert.Equal(0, tier.New);

        long id = Assert.Single(db.Database.Findings.ForFile(file)).Id;
        await db.Database.Findings.SetStatusAsync([id], FindingStatus.Ignored);
        FindingChange ignored = await db.Database.Findings.ReplaceForFileAsync(file, 3, rules, [new Finding("AUD-CLIPPING", Severity.High, 1f, "x", Finding.NoEvidence)], false);
        Assert.Equal(new FindingChange(0, 1, 0), ignored);
    }

    [Fact]
    public async Task A_posted_write_that_fails_is_reported_to_the_caller()
    {
        using TestDatabase db = new();
        TaskCompletionSource<Exception> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await db.Database.Writer.PostAsync(c => c.Exec("INSERT INTO nope VALUES (1)"), ChangeFlags.None, failed: ex => failed.TrySetResult(ex));
        await db.Database.Writer.PostAsync(_ => { }, ChangeFlags.None, failed: ex => failed.TrySetResult(new InvalidOperationException("the good write failed")));

        Exception error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }
}
