using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

/// <summary>Tools → Database actions (task S-09): back up, compact, check, reset.</summary>
public sealed class DatabaseMaintenance(Database database)
{
    /// <summary>
    /// Copies the database with SQLite's online backup API, next to it by default
    /// (<c>library.backup-&lt;timestamp&gt;.db</c>). The copy is consistent even while scans write.
    /// </summary>
    public Task<string> BackupAsync(string? targetFile = null, CancellationToken cancellationToken = default) => Task.Run(
        () =>
        {
            string directory = Path.GetDirectoryName(database.FilePath)!;
            string stamp = database.Clock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = targetFile ?? Path.Combine(directory, Path.GetFileNameWithoutExtension(database.FilePath) + ".backup-" + stamp + ".db");
            database.Read(source =>
            {
                using SqliteConnection destination = new(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
                destination.Open();
                source.BackupDatabase(destination);
                return 0;
            });
            return target;
        },
        cancellationToken);

    /// <summary>Runs VACUUM between write batches (it cannot run inside a transaction).</summary>
    public Task CompactAsync(CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteOutsideTransactionAsync(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "VACUUM";
            command.ExecuteNonQuery();
        }, cancellationToken);

    /// <summary>Full <c>integrity_check</c>; returns an empty list when the database is sound.</summary>
    public Task<List<string>> CheckAsync(CancellationToken cancellationToken = default) => Task.Run(
        () => database.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            List<string> problems = [];
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string line = reader.GetString(0);
                if (!string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(line);
                }
            }

            return problems;
        }),
        cancellationToken);

    /// <summary>
    /// Forgets the library: folders, fingerprints, findings, history, plans, the journal and notifications.
    /// Media files are not touched. Rule settings are preferences and stay. Journal entries that still keep an original
    /// in a quarantine folder stay too, and so does an operation left running by a crash: they are the only record of
    /// those files, which Restore, the automatic purge and start-up recovery work from.
    /// </summary>
    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                context.Exec("DELETE FROM roots");
                context.Exec("DELETE FROM runs");
                context.Exec("DELETE FROM plans");
                SqliteCommand operations = context.Command(
                    "DELETE FROM operations WHERE status <> $running AND NOT (status = $done AND backup_path IS NOT NULL)");
                operations.Parameters.AddWithValue("$running", (int)OperationStatus.Running);
                operations.Parameters.AddWithValue("$done", (int)OperationStatus.Done);
                operations.ExecuteNonQuery();
                context.Exec("DELETE FROM notifications");
                context.Caches.Clear();
            },
            ChangeFlags.All,
            cancellationToken);

    /// <summary>File size of the database and its WAL, in bytes.</summary>
    public long SizeBytes()
    {
        long size = 0;
        foreach (string file in new[] { database.FilePath, database.FilePath + "-wal" })
        {
            if (File.Exists(file))
            {
                size += new FileInfo(file).Length;
            }
        }

        return size;
    }

    /// <summary>The newest backup made by <see cref="BackupAsync"/>, if any.</summary>
    public DateTimeOffset? LastBackupUtc()
    {
        string directory = Path.GetDirectoryName(database.FilePath)!;
        string pattern = Path.GetFileNameWithoutExtension(database.FilePath) + ".backup-*.db";
        FileInfo? newest = new DirectoryInfo(directory).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        return newest is null ? null : new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero);
    }
}
