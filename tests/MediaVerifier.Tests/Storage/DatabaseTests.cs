using System.Diagnostics;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Tests.Storage;

public sealed class DatabaseTests
{
    [Fact]
    public void OpensWithTheBundledSqlite()
    {
        using TestDatabase db = new();
        Assert.Equal("3.54.0", db.Database.SqliteVersion);
    }

    [Theory]
    [InlineData("3.45.1")]
    [InlineData("3.53.9")]
    [InlineData("garbage")]
    public void OlderSqliteIsRefusedWithASpecificError(string version)
    {
        StorageException error = Assert.Throws<StorageException>(() => Database.CheckVersion(version));
        Assert.Contains(version, error.Message, StringComparison.Ordinal);
        Assert.Contains("e_sqlite3.dll", error.Message, StringComparison.Ordinal);
        Database.CheckVersion("3.54.0");
        Database.CheckVersion("3.60.2");
    }

    [Fact]
    public void FreshCreateHasTheLatestSchemaAndReopenIsIdempotent()
    {
        int latest = MigrationRunner.LatestVersion(MigrationRunner.Default);
        using TestDatabase db = new();
        Assert.Equal(0, db.Database.OpenedFromVersion);
        Assert.Equal(latest, UserVersion(db.FilePath));
        string[] tables = Tables(db.FilePath);
        foreach (string table in new[] { "meta", "roots", "dirs", "files", "findings", "artifacts", "file_events", "runs", "plans", "rule_settings", "ignores", "operations", "notifications" })
        {
            Assert.Contains(table, tables);
        }

        db.Reopen();
        Assert.Equal(latest, db.Database.OpenedFromVersion);
        Assert.Equal(latest, UserVersion(db.FilePath));
    }

    [Fact]
    public async Task MigrationKeepsDataAndWritesABackup()
    {
        using TempDirectory temp = new("migrate");
        string file = temp.Combine("library.db");
        FakeClock clock = new(DateTimeOffset.UtcNow);
        using (Database v1 = Database.Open(file, clock, [new InitialSchema()]))
        {
            await v1.Roots.AddAsync(new RootRecord { Path = @"D:\Music" });
        }

        using (Database v2 = Database.Open(file, clock, [new InitialSchema(), new AddColumnMigration()]))
        {
            Assert.Equal(1, v2.OpenedFromVersion);
            Assert.Single(v2.Roots.List());
        }

        Assert.Equal(2, UserVersion(file));
        Assert.True(File.Exists(temp.Combine("library.pre-v2.db")));
        using SqliteConnection backup = new($"Data Source={temp.Combine("library.pre-v2.db")};Pooling=False");
        backup.Open();
        using SqliteCommand count = backup.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM roots";
        Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public void NewerDatabaseIsRefusedNamingBothVersions()
    {
        using TempDirectory temp = new("newer");
        string file = temp.Combine("library.db");
        FakeClock clock = new(DateTimeOffset.UtcNow);
        Database.Open(file, clock).Dispose();
        using (SqliteConnection connection = new($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 7";
            command.ExecuteNonQuery();
        }

        StorageException error = Assert.Throws<StorageException>(() => Database.Open(file, clock));
        Assert.Contains("7", error.Message, StringComparison.Ordinal);
        Assert.Contains(MigrationRunner.LatestVersion(MigrationRunner.Default).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DamagedDatabaseIsSetAsideAndAFreshOneStarts()
    {
        using TempDirectory temp = new("damaged");
        string file = temp.Combine("library.db");
        FakeClock clock = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
        using (Database first = Database.Open(file, clock))
        {
            await first.Roots.AddAsync(new RootRecord { Path = @"D:\Photos" });
        }

        // Overwrite the middle of the file with garbage, as a failing disk might.
        byte[] bytes = File.ReadAllBytes(file);
        Random random = new(3);
        for (int i = 4096; i < Math.Min(bytes.Length, 12288); i++)
        {
            bytes[i] = (byte)random.Next(256);
        }

        File.WriteAllBytes(file, bytes);

        using Database reopened = Database.Open(file, clock);
        Assert.NotNull(reopened.RecoveredDamagedCopy);
        Assert.True(File.Exists(reopened.RecoveredDamagedCopy));
        Assert.Empty(reopened.Roots.List());
        NotificationRecord note = Assert.Single(reopened.Notifications.List());
        Assert.Equal("The library database was damaged", note.Title);
        Assert.Contains(reopened.RecoveredDamagedCopy!, note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunsLeftRunningAreMarkedInterruptedAtStartup()
    {
        using TestDatabase db = new();
        long run = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Standard, "{}", null);
        Assert.Equal(RunStatus.Running, db.Database.Runs.Get(run)!.Status);

        db.Reopen();

        RunRecord after = db.Database.Runs.Get(run)!;
        Assert.Equal(RunStatus.Interrupted, after.Status);
        Assert.NotNull(after.EndedUtc);
    }

    [Fact]
    public async Task WriterUpserts200000FilesAtAtLeast5000RowsPerSecond()
    {
        using TestDatabase db = new("throughput");
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Big" });
        Stopwatch watch = Stopwatch.StartNew();
        List<Task> pending = [];
        for (int batch = 0; batch < 200; batch++)
        {
            FileUpsert[] files = new FileUpsert[1000];
            for (int i = 0; i < files.Length; i++)
            {
                int n = (batch * 1000) + i;
                files[i] = new FileUpsert(root, "dir" + (n / 500).ToString(System.Globalization.CultureInfo.InvariantCulture), "f" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mp3", "mp3", MediaKind.Audio, n, n, null, 1);
            }

            pending.Add(db.Database.Files.UpsertAsync(files));
        }

        await Task.WhenAll(pending);
        watch.Stop();

        Assert.Equal(200_000, db.Database.Files.Count());
        double rate = 200_000 / watch.Elapsed.TotalSeconds;
        Assert.True(rate >= 5_000, $"{rate:N0} rows/s");
    }

    [Fact]
    public async Task AFailedOperationRollsBackAloneAndTheBatchCommits()
    {
        using TestDatabase db = new();
        Task<long> good = db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\A" });
        Task bad = db.Database.Writer.ExecuteAsync(c => c.Exec("INSERT INTO nope VALUES (1)"), ChangeFlags.None);
        Task<long> after = db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\B" });

        await Assert.ThrowsAnyAsync<SqliteException>(() => bad);
        await good;
        await after;
        Assert.Equal(2, db.Database.Roots.List().Count);
    }

    [Fact]
    public async Task UncommittedWorkIsGoneAfterACrashAndTheDatabaseStaysConsistent()
    {
        using TempDirectory temp = new("crash");
        string file = temp.Combine("library.db");
        FakeClock clock = new(DateTimeOffset.UtcNow);
        using (Database first = Database.Open(file, clock))
        {
            await first.Roots.AddAsync(new RootRecord { Path = @"D:\Kept" });
        }

        // A second writer starts a transaction and dies without committing (as when the process is killed).
        using (SqliteConnection crashing = new($"Data Source={file};Pooling=False"))
        {
            crashing.Open();
            using SqliteCommand pragma = crashing.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            pragma.ExecuteNonQuery();
            SqliteTransaction transaction = crashing.BeginTransaction();
            using SqliteCommand insert = crashing.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO roots(path, added_utc) VALUES ('D:\\Lost', 0)";
            insert.ExecuteNonQuery();
        }

        using Database reopened = Database.Open(file, clock);
        Assert.Null(reopened.RecoveredDamagedCopy);
        RootRecord only = Assert.Single(reopened.Roots.List());
        Assert.Equal(@"D:\Kept", only.Path);
        Assert.Empty(await reopened.Maintenance.CheckAsync());
    }

    [Fact]
    public async Task LibraryChangedIsRaisedWithWhatChanged()
    {
        using TestDatabase db = new();
        TaskCompletionSource<ChangeFlags> raised = new(TaskCreationOptions.RunContinuationsAsynchronously);
        db.Database.LibraryChanged += (_, e) => raised.TrySetResult(e.Changes);

        await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\X" });

        ChangeFlags changes = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(changes.HasFlag(ChangeFlags.Roots));
    }

    private static int UserVersion(string file)
    {
        using SqliteConnection connection = new($"Data Source={file};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Tables(string file)
    {
        using SqliteConnection connection = new($"Data Source={file};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        List<string> names = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    [Fact]
    public async Task FindingsCarryTheirFilesNameAndKindAcrossTheV2Migration()
    {
        using TempDirectory temp = new("findings-v2");
        string file = temp.Combine("library.db");
        FakeClock clock = new(DateTimeOffset.UtcNow);
        long fileId;
        using (Database v1 = Database.Open(file, clock, [new InitialSchema()]))
        {
            long root = await v1.Roots.AddAsync(new RootRecord { Path = @"D:\Music" });
            fileId = (await v1.Files.UpsertAsync([new(root, "Album", "track.flac", "flac", MediaKind.Audio, 100, 1, null, 1)]))[0].FileId;
            await v1.Writer.ExecuteAsync(
                context =>
                {
                    SqliteCommand insert = context.Command(
                        "INSERT INTO findings(file_id, rule_id, category, severity, confidence, summary, status, first_run, last_run, first_utc, last_utc) " +
                        "VALUES ($file, 'AUD-CLIPPING', 4, 2, 1.0, 'clipping', 0, 1, 1, 0, 0)");
                    insert.Parameters.AddWithValue("$file", fileId);
                    insert.ExecuteNonQuery();
                },
                ChangeFlags.Findings);
        }

        using Database v2 = Database.Open(file, clock);
        Assert.Equal(1, v2.OpenedFromVersion);
        Assert.Equal(("track.flac", (long)MediaKind.Audio), ListColumns(v2, fileId));

        // A rename or move detected later updates the copy the Issues list sorts by.
        await v2.Writer.ExecuteAsync(context => FileIndex.Move(context, fileId, 1, "Album", "renamed.flac"), ChangeFlags.Files);
        Assert.Equal(("renamed.flac", (long)MediaKind.Audio), ListColumns(v2, fileId));

        // New findings take the values from their file when inserted.
        await v2.Findings.ReplaceForFileAsync(fileId, 2, ["AUD-CLIPPING", "AUD-DC"], [new Finding("AUD-CLIPPING", Severity.Medium, 1f, "clipping", Finding.NoEvidence), new Finding("AUD-DC", Severity.Low, 1f, "dc", Finding.NoEvidence)], false);
        long named = v2.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM findings WHERE file_name = 'renamed.flac' AND media_kind = 3";
            return (long)command.ExecuteScalar()!;
        });
        Assert.Equal(2, named);
    }

    private static (string Name, long Kind) ListColumns(Database database, long fileId) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT file_name, media_kind FROM findings WHERE file_id = $file";
        command.Parameters.AddWithValue("$file", fileId);
        using SqliteDataReader r = command.ExecuteReader();
        Assert.True(r.Read());
        return (r.GetString(0), r.GetInt64(1));
    });

    private sealed class AddColumnMigration : Migration
    {
        public override int Version => 2;

        public override void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
            Exec(connection, transaction, "ALTER TABLE roots ADD COLUMN note TEXT");
    }
}
