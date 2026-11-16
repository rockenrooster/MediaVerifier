using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

/// <summary>One forward-only schema step, applied in its own transaction.</summary>
internal abstract class Migration
{
    public abstract int Version { get; }

    public abstract void Apply(SqliteConnection connection, SqliteTransaction transaction);

    protected static void Exec(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>Schema v1 exactly as subplan 03 specifies.</summary>
internal sealed class InitialSchema : Migration
{
    public override int Version => 1;

    public override void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        Exec(connection, transaction, """
            CREATE TABLE meta (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE roots (
              id               INTEGER PRIMARY KEY,
              path             TEXT    NOT NULL UNIQUE,
              label            TEXT,
              role             INTEGER NOT NULL DEFAULT 0,
              media_mask       INTEGER NOT NULL DEFAULT 7,
              default_depth    INTEGER NOT NULL DEFAULT 2,
              include_patterns TEXT,
              exclude_patterns TEXT,
              volume_id        TEXT,
              storage_class    INTEGER NOT NULL DEFAULT 0,
              enabled          INTEGER NOT NULL DEFAULT 1,
              added_utc        INTEGER NOT NULL,
              last_online_utc  INTEGER
            );

            CREATE TABLE dirs (
              id        INTEGER PRIMARY KEY,
              root_id   INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
              parent_id INTEGER REFERENCES dirs(id) ON DELETE CASCADE,
              rel_path  TEXT    NOT NULL,
              name      TEXT    NOT NULL,
              UNIQUE (root_id, rel_path)
            );
            CREATE INDEX ix_dirs_parent ON dirs(parent_id);

            CREATE TABLE files (
              id                INTEGER PRIMARY KEY,
              root_id           INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
              dir_id            INTEGER NOT NULL REFERENCES dirs(id) ON DELETE CASCADE,
              name              TEXT    NOT NULL,
              ext               TEXT    NOT NULL,
              media_kind        INTEGER NOT NULL,
              size              INTEGER NOT NULL,
              mtime_utc         INTEGER NOT NULL,
              ntfs_file_id      INTEGER,
              state             INTEGER NOT NULL DEFAULT 0,
              skip_reason       INTEGER NOT NULL DEFAULT 0,
              worst_severity    INTEGER NOT NULL DEFAULT 0,
              open_findings     INTEGER NOT NULL DEFAULT 0,
              scanned_depth     INTEGER NOT NULL DEFAULT 0,
              detector_set      INTEGER NOT NULL DEFAULT 0,
              first_seen_utc    INTEGER NOT NULL,
              last_seen_run     INTEGER NOT NULL DEFAULT 0,
              last_scanned_utc  INTEGER,
              hash              BLOB,
              hash_size         INTEGER,
              hash_mtime_utc    INTEGER,
              hash_utc          INTEGER,
              stream_hash       BLOB,
              last_verified_utc INTEGER,
              verify_count      INTEGER NOT NULL DEFAULT 0,
              container         TEXT,
              video_codec       TEXT,
              audio_codec       TEXT,
              width             INTEGER,
              height            INTEGER,
              duration_ms       INTEGER,
              bitrate           INTEGER,
              sample_rate       INTEGER,
              channels          INTEGER,
              bit_depth         INTEGER,
              fps_milli         INTEGER,
              UNIQUE (dir_id, name)
            );
            CREATE INDEX ix_files_root_state ON files(root_id, state, worst_severity);
            CREATE INDEX ix_files_severity   ON files(worst_severity) WHERE worst_severity > 0;
            CREATE INDEX ix_files_hash       ON files(hash) WHERE hash IS NOT NULL;
            CREATE INDEX ix_files_verify     ON files(last_verified_utc) WHERE hash IS NOT NULL;
            CREATE INDEX ix_files_run        ON files(root_id, last_seen_run);

            CREATE TABLE findings (
              id            INTEGER PRIMARY KEY,
              file_id       INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              rule_id       TEXT    NOT NULL,
              category      INTEGER NOT NULL,
              severity      INTEGER NOT NULL,
              confidence    REAL    NOT NULL,
              summary       TEXT    NOT NULL,
              evidence_json TEXT,
              status        INTEGER NOT NULL DEFAULT 0,
              first_run     INTEGER NOT NULL,
              last_run      INTEGER NOT NULL,
              first_utc     INTEGER NOT NULL,
              last_utc      INTEGER NOT NULL,
              resolved_utc  INTEGER,
              resolution    TEXT,
              UNIQUE (file_id, rule_id)
            );
            CREATE INDEX ix_findings_open ON findings(status, severity, rule_id);
            CREATE INDEX ix_findings_run  ON findings(first_run);

            CREATE TABLE artifacts (
              file_id          INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              kind             INTEGER NOT NULL,
              provider_version INTEGER NOT NULL,
              data             TEXT    NOT NULL,
              PRIMARY KEY (file_id, kind)
            ) WITHOUT ROWID;

            CREATE TABLE file_events (
              id          INTEGER PRIMARY KEY,
              file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              utc         INTEGER NOT NULL,
              run_id      INTEGER,
              kind        INTEGER NOT NULL,
              detail_json TEXT
            );
            CREATE INDEX ix_events_file ON file_events(file_id, utc);

            CREATE TABLE runs (
              id                INTEGER PRIMARY KEY,
              plan_id           INTEGER,
              trigger           INTEGER NOT NULL,
              depth             INTEGER NOT NULL,
              scope_json        TEXT    NOT NULL,
              status            INTEGER NOT NULL,
              started_utc       INTEGER NOT NULL,
              ended_utc         INTEGER,
              files_seen        INTEGER NOT NULL DEFAULT 0,
              files_analysed    INTEGER NOT NULL DEFAULT 0,
              files_verified    INTEGER NOT NULL DEFAULT 0,
              bytes_read        INTEGER NOT NULL DEFAULT 0,
              new_findings      INTEGER NOT NULL DEFAULT 0,
              resolved_findings INTEGER NOT NULL DEFAULT 0,
              errors            INTEGER NOT NULL DEFAULT 0,
              alerts_json       TEXT,
              summary_json      TEXT
            );

            CREATE TABLE plans (
              id              INTEGER PRIMARY KEY,
              name            TEXT    NOT NULL,
              enabled         INTEGER NOT NULL DEFAULT 1,
              definition_json TEXT    NOT NULL,
              task_name       TEXT,
              created_utc     INTEGER NOT NULL,
              updated_utc     INTEGER NOT NULL,
              last_run_id     INTEGER
            );

            CREATE TABLE rule_settings (
              rule_id      TEXT PRIMARY KEY,
              enabled      INTEGER,
              severity_cap INTEGER,
              params_json  TEXT
            ) WITHOUT ROWID;

            CREATE TABLE ignores (
              id          INTEGER PRIMARY KEY,
              root_id     INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
              dir_id      INTEGER REFERENCES dirs(id) ON DELETE CASCADE,
              rule_id     TEXT,
              reason      TEXT,
              created_utc INTEGER NOT NULL
            );

            CREATE TABLE operations (
              id           INTEGER PRIMARY KEY,
              batch_id     INTEGER NOT NULL,
              utc          INTEGER NOT NULL,
              action_id    TEXT    NOT NULL,
              file_id      INTEGER,
              status       INTEGER NOT NULL,
              source_path  TEXT    NOT NULL,
              result_path  TEXT,
              backup_path  TEXT,
              undo_json    TEXT,
              detail_json  TEXT
            );
            CREATE INDEX ix_operations_batch ON operations(batch_id);

            CREATE TABLE notifications (
              id         INTEGER PRIMARY KEY,
              utc        INTEGER NOT NULL,
              level      INTEGER NOT NULL,
              title      TEXT    NOT NULL,
              body       TEXT,
              action_uri TEXT,
              is_read    INTEGER NOT NULL DEFAULT 0
            );
            """);
    }
}

/// <summary>
/// Applies pending migrations (subplan 03, Migrations): forward only, one transaction each, with a copy
/// of the database taken first (two most recent kept). A database newer than the app is refused.
/// </summary>
internal static class MigrationRunner
{
    public static IReadOnlyList<Migration> Default { get; } = [new InitialSchema()];

    public static int LatestVersion(IReadOnlyList<Migration> migrations) => migrations.Count == 0 ? 0 : migrations.Max(m => m.Version);

    /// <summary>Returns the version before migrating; throws <see cref="StorageException"/> for a newer database.</summary>
    public static int Run(SqliteConnection connection, string databaseFile, IReadOnlyList<Migration> migrations, DateTimeOffset nowUtc)
    {
        int target = LatestVersion(migrations);
        int current = UserVersion(connection);
        if (current > target)
        {
            throw new StorageException(string.Create(
                CultureInfo.InvariantCulture,
                $"The library database uses schema version {current}, but this version of MediaVerifier supports up to {target}. Update MediaVerifier to open it."));
        }

        if (current == target)
        {
            return current;
        }

        if (current > 0)
        {
            BackupBeforeMigration(connection, databaseFile, target);
        }

        foreach (Migration migration in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            migration.Apply(connection, transaction);
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO meta(key, value) VALUES ('schema_version', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                command.Parameters.AddWithValue("$v", migration.Version.ToString(CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
                if (migration.Version == 1)
                {
                    command.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES ('created_utc', $c)";
                    command.Parameters.AddWithValue("$c", nowUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                }

                // PRAGMA cannot take parameters; the version is an integer we control.
                command.Parameters.Clear();
                command.CommandText = "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return current;
    }

    public static int UserVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void BackupBeforeMigration(SqliteConnection connection, string databaseFile, int target)
    {
        string directory = Path.GetDirectoryName(databaseFile) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(databaseFile);
        string backup = Path.Combine(directory, $"{stem}.pre-v{target.ToString(CultureInfo.InvariantCulture)}.db");
        using (SqliteConnection destination = new(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
        {
            destination.Open();
            connection.BackupDatabase(destination);
        }

        // Keep the two most recent pre-migration copies.
        IEnumerable<FileInfo> old = new DirectoryInfo(directory)
            .EnumerateFiles(stem + ".pre-v*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(2);
        foreach (FileInfo file in old)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
        }
    }
}
