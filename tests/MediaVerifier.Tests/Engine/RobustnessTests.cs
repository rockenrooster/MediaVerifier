using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Engine;

/// <summary>
/// Task H-03 (subplan 16, Robustness scenarios): each scenario ends with a consistent database, no stray processes and
/// a clear message. The rows without a test here are mapped in docs/test-procedures.md.
/// </summary>
public sealed class RobustnessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_full_data_volume_fails_the_run_and_the_library_stays_usable()
    {
        using EngineHarness h = new([new CountingProvider()]);
        for (int i = 0; i < 20; i++)
        {
            h.Write($"a{i:00}.jpg", EngineHarness.Bytes(2000, i));
        }

        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Quick);

        // The data volume fills up: the database may not grow by a single page.
        await h.Database.Writer.ExecuteOutsideTransactionAsync(connection => Exec(connection, "PRAGMA max_page_count = " + Scalar(connection, "PRAGMA page_count")));
        for (int i = 0; i < 400; i++)
        {
            h.Write($"Folder {i % 40}\\b{i:000}.jpg", EngineHarness.Bytes(2000, 1000 + i));
        }

        RunSummary full = await h.ScanAsync(ScanDepth.Quick);
        output.WriteLine($"{full.Status}: {full.Error}");
        Assert.Equal(RunStatus.Failed, full.Status);
        Assert.Contains("full", full.Error, StringComparison.OrdinalIgnoreCase);

        // Space comes back: the same database scans everything and is intact.
        await h.Database.Writer.ExecuteOutsideTransactionAsync(connection => Exec(connection, "PRAGMA max_page_count = 1073741823"));
        RunSummary after = await h.ScanAsync(ScanDepth.Quick);
        Assert.Equal(RunStatus.Completed, after.Status);
        Assert.Equal(420, h.AllFiles().Count);
        Assert.Equal("ok", h.Database.Read(connection => Scalar(connection, "PRAGMA integrity_check")));
    }

    [Fact]
    public async Task A_database_locked_by_another_program_waits_then_reports_and_is_not_damaged()
    {
        using EngineHarness h = new([new CountingProvider()]);
        h.Write("a.jpg", EngineHarness.Bytes(2000, 1));
        await h.AddRootAsync(h.Library);

        // A backup tool holds an exclusive lock for longer than the busy timeout (5 s).
        using (SqliteConnection other = new($"Data Source={h.Db.FilePath};Pooling=False"))
        {
            other.Open();
            Exec(other, "BEGIN EXCLUSIVE");
            DateTime started = DateTime.UtcNow;
            Task<long> add = h.Database.Roots.AddAsync(new RootRecord { Path = h.Backup, Role = RootRole.Backup, AddedUtc = h.Db.Clock.UtcNow });
            bool finished = await Task.WhenAny(add, Task.Delay(TimeSpan.FromSeconds(60))) == add;
            TimeSpan waited = DateTime.UtcNow - started;
            output.WriteLine($"finished {finished} after {waited.TotalSeconds:0.0} s");
            if (!finished)
            {
                Exec(other, "ROLLBACK");
                await Task.WhenAny(add, Task.Delay(TimeSpan.FromSeconds(10)));
                output.WriteLine($"after release: {add.Status}");
                Assert.Fail("the write waited more than 60 s for the lock");
            }

            Exception? error = await Record.ExceptionAsync(() => add);
            output.WriteLine($"{error?.GetType().Name}: {error?.Message} after {waited.TotalSeconds:0.0} s");
            Assert.NotNull(error);
            Assert.True(waited >= TimeSpan.FromSeconds(4), $"gave up after {waited.TotalSeconds:0.0} s");
            Assert.Contains("locked", error.Message, StringComparison.OrdinalIgnoreCase);
            Exec(other, "ROLLBACK");
        }

        RunSummary run = await h.ScanAsync(ScanDepth.Quick);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("ok", h.Database.Read(connection => Scalar(connection, "PRAGMA integrity_check")));
    }

    [FfmpegFact]
    public async Task A_crashing_ffmpeg_is_recorded_and_the_run_continues()
    {
        string crash = Path.Combine(AppContext.BaseDirectory, "crash-ffmpeg.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "MediaVerifier.TestHelper.exe"), crash, overwrite: true);
        using FfRunner runner = new(new FfmpegLocation(AppContext.BaseDirectory, IsOverride: true)) { ExecutableOverride = tool => tool == FfTool.Ffprobe ? FfmpegEnvironment.Engine!.FfprobeExe : crash };
        using EngineHarness h = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string[] names = ["one.mp3", "two.mp3", "three.mp3"];
        foreach (string name in names)
        {
            h.Write(name, EngineHarness.Bytes(64 * 1024, name.Length));
        }

        await h.AddRootAsync(h.Library);
        RunSummary run = await h.ScanAsync(ScanDepth.Standard);
        output.WriteLine($"{run.Status}, {run.Counters.Errors} errors");
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(names.Length, h.AllFiles().Count);
        foreach (FileRecord file in h.AllFiles())
        {
            List<FindingRecord> findings = h.Database.Findings.ForFile(file.Id);
            output.WriteLine($"{file.Name}: {string.Join(", ", findings.Select(f => f.RuleId))}");
            Assert.NotEmpty(findings);
        }

        Assert.DoesNotContain(System.Diagnostics.Process.GetProcessesByName("crash-ffmpeg"), p => !p.HasExited);
    }

    [Fact]
    public async Task A_clock_that_jumps_back_gives_no_negative_durations()
    {
        using EngineHarness h = new([new CountingProvider(delay: TimeSpan.FromMilliseconds(30))]);
        for (int i = 0; i < 10; i++)
        {
            h.Write($"a{i}.jpg", EngineHarness.Bytes(2000, i));
        }

        await h.AddRootAsync(h.Library);
        Task<RunSummary> scan = h.ScanAsync(ScanDepth.Quick);
        await Task.Delay(50);
        h.Db.Clock.Advance(TimeSpan.FromHours(-2));
        RunSummary run = await scan;
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.True(run.Elapsed >= TimeSpan.Zero, $"elapsed {run.Elapsed}");
        RunRecord record = h.Database.Runs.Get(run.RunId)!;
        Assert.True(record.EndedUtc >= record.StartedUtc, $"{record.StartedUtc} → {record.EndedUtc}");

        // Files verified "in the future" are simply not due: no negative ages.
        DateTimeOffset now = h.Db.Clock.UtcNow;
        IReadOnlyList<ScrubCandidate> due = ScrubPolicy.Select([new ScrubCandidate(1, "C", "a", "a.jpg", now.AddDays(5).UtcTicks, 1000)], now, TimeSpan.FromDays(90), null, 0);
        Assert.Empty(due);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

/// <summary>A Deep scan after a Standard one runs the Deep-only passes (found by the T-05 benchmark).</summary>
[Trait("Category", "Integration")]
public sealed class DepthUpgradeTests
{
    [FixtureFact]
    public async Task A_deep_scan_after_a_standard_one_decodes_every_frame()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness h = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string path = FixtureFactory.CopyTo("fs.mp4", h.Library);
        Mutate.RandomBlock(path, 0.5, 2000, seed: 1);
        await h.AddRootAsync(h.Library);

        await h.ScanAsync(ScanDepth.Standard);
        FileRecord file = h.AllFiles().Single();
        Assert.Equal(Core.Analysis.Providers.VideoPass.Standard, Pass(h, file));

        await h.ScanAsync(ScanDepth.Deep);
        Assert.Equal(Core.Analysis.Providers.VideoPass.Deep, Pass(h, file));
        Assert.Contains(h.Database.Findings.ForFile(file.Id), f => f.RuleId == "COR-CONCEALED");
    }

    private static Core.Analysis.Providers.VideoPass Pass(EngineHarness h, FileRecord file)
    {
        (int _, string data) = h.Database.Artifacts.ForFile(file.Id)[ArtifactKind.VideoDecode];
        return ((Core.Analysis.Providers.VideoDecodeArtifact)new Core.Analysis.Providers.VideoDecodeProvider().Deserialize(data)!).Pass;
    }
}
