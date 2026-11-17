using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MediaVerifier.Core.Logging;
using MediaVerifier.Core.Model;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

/// <summary>
/// The library database (subplan 03): one writer connection behind <see cref="DbWriter"/>, a small pool
/// of read-only connections, WAL journal. Opening checks the SQLite version, runs <c>quick_check</c>
/// (moving a damaged file aside and starting fresh), applies migrations and marks runs that were left
/// running as interrupted.
/// </summary>
public sealed class Database : IDisposable
{
    /// <summary>The bundled SQLite (SQLitePCLRaw 2.1.14) reports 3.54.0; anything older is the wrong DLL.</summary>
    public static readonly Version MinimumSqliteVersion = new(3, 54);

    private static int s_initialized;
    private readonly SqliteConnection _writerConnection;
    private readonly ReaderPool _readers;
    private int _disposed;

    private Database(string filePath, SqliteConnection writerConnection, IClock clock, string sqliteVersion)
    {
        FilePath = filePath;
        _writerConnection = writerConnection;
        Clock = clock;
        SqliteVersion = sqliteVersion;
        _readers = new ReaderPool(ReaderConnectionString(filePath));
        Writer = new DbWriter(writerConnection, clock, changes => LibraryChanged?.Invoke(this, new LibraryChangedEventArgs(changes)));
        Roots = new RootStore(this);
        Files = new FileIndex(this);
        Findings = new FindingStore(this);
        Runs = new RunStore(this);
        Plans = new PlanStore(this);
        Journal = new Journal(this);
        Artifacts = new ArtifactStore(this);
        Notifications = new NotificationStore(this);
        RuleSettings = new RuleSettingsStore(this);
        Ignores = new IgnoreStore(this);
        Events = new FileEventStore(this);
        Queries = new LibraryQueries(this);
        Meta = new MetaStore(this);
        Maintenance = new DatabaseMaintenance(this);
    }

    /// <summary>
    /// Managed thread id of the UI thread. In debug builds every read from that thread throws, because
    /// the UI must never wait on the database (subplan 09, exit criteria).
    /// </summary>
    public static int UiThreadId { get; set; }

    public string FilePath { get; }

    public string SqliteVersion { get; }

    public IClock Clock { get; }

    public DbWriter Writer { get; }

    /// <summary>Set when the file failed <c>quick_check</c> at open: where the damaged copy went.</summary>
    public string? RecoveredDamagedCopy { get; private set; }

    /// <summary>Schema version found before migrations ran (0 for a new database).</summary>
    public int OpenedFromVersion { get; private set; }

    public RootStore Roots { get; }

    public FileIndex Files { get; }

    public FindingStore Findings { get; }

    public RunStore Runs { get; }

    public PlanStore Plans { get; }

    public Journal Journal { get; }

    public ArtifactStore Artifacts { get; }

    public NotificationStore Notifications { get; }

    public RuleSettingsStore RuleSettings { get; }

    public IgnoreStore Ignores { get; }

    public FileEventStore Events { get; }

    public LibraryQueries Queries { get; }

    public MetaStore Meta { get; }

    public DatabaseMaintenance Maintenance { get; }

    /// <summary>Raised (debounced, at most twice a second, on a pool thread) after commits.</summary>
    public event EventHandler<LibraryChangedEventArgs>? LibraryChanged;

    public static Database Open(string filePath, IClock? clock = null) => Open(filePath, clock ?? SystemClock.Instance, MigrationRunner.Default);

    internal static Database Open(string filePath, IClock clock, IReadOnlyList<Migration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        InitializeProvider();
        filePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        string? damagedCopy = null;
        if (File.Exists(filePath) && !QuickCheckPasses(filePath, out string problem))
        {
            damagedCopy = MoveDamagedAside(filePath, clock);
            AppLog.Shared?.Error($"Library database failed its integrity check ({problem}); moved to {damagedCopy} and started fresh");
        }

        SqliteConnection connection = new(WriterConnectionString(filePath));
        try
        {
            connection.Open();
            string version = Scalar(connection, "SELECT sqlite_version()");
            CheckVersion(version);
            ApplyPragmas(connection, cacheKib: 32768, writer: true);
            int previous = MigrationRunner.Run(connection, filePath, migrations, clock.UtcNow);
            int interrupted = MarkInterruptedRuns(connection, clock);
            if (damagedCopy is not null)
            {
                AddRecoveryNotification(connection, clock, damagedCopy);
            }

            Database database = new(filePath, connection, clock, version)
            {
                RecoveredDamagedCopy = damagedCopy,
                OpenedFromVersion = previous,
            };
            AppLog.Shared?.Info($"Library database {filePath} open (SQLite {version}, schema {MigrationRunner.UserVersion(connection)}, {interrupted} interrupted runs)");
            return database;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Refuses a SQLite older than the bundled one: the wrong e_sqlite3.dll was loaded.</summary>
    public static void CheckVersion(string reported)
    {
        if (!Version.TryParse(reported, out Version? version) || version < MinimumSqliteVersion)
        {
            throw new StorageException($"SQLite {reported} was loaded, but MediaVerifier needs {MinimumSqliteVersion} or newer. The e_sqlite3.dll beside MediaVerifier.exe is missing or wrong; reinstall MediaVerifier.");
        }
    }

    /// <summary>Runs <paramref name="query"/> on a pooled read-only connection.</summary>
    public T Read<T>(Func<SqliteConnection, T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        AssertNotUiThread();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SqliteConnection connection = _readers.Rent();
        try
        {
            return query(connection);
        }
        finally
        {
            _readers.Return(connection);
        }
    }

    /// <summary>Flushes the writer and truncates the WAL (end of each run and on shutdown).</summary>
    public Task CheckpointAsync() =>
        Writer.ExecuteOutsideTransactionAsync(connection =>
        {
            // A checkpoint inside a write transaction would wait out the command timeout.
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Writer.Dispose();
        _readers.Dispose();
        try
        {
            using SqliteCommand checkpoint = _writerConnection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            AppLog.Shared?.Warning("WAL checkpoint at shutdown failed", ex);
        }

        _writerConnection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    internal static void InitializeProvider()
    {
        if (Interlocked.Exchange(ref s_initialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }

    /// <summary>Seconds a write waits for a lock held by another program before it fails (on top of busy_timeout).</summary>
    internal const int WriteLockWaitSeconds = 10;

    internal static string WriterConnectionString(string file) => new SqliteConnectionStringBuilder
    {
        DataSource = file,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = false,
        DefaultTimeout = WriteLockWaitSeconds,
    }.ToString();

    internal static string ReaderConnectionString(string file) => new SqliteConnectionStringBuilder
    {
        DataSource = file,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private,
        Pooling = false,
    }.ToString();

    internal static void ApplyPragmas(SqliteConnection connection, int cacheKib, bool writer)
    {
        string pragmas = string.Create(
            CultureInfo.InvariantCulture,
            $"PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-{cacheKib};");
        if (writer)
        {
            pragmas = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; " + pragmas;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = pragmas;
        command.ExecuteNonQuery();
    }

    internal static string Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    [Conditional("DEBUG")]
    internal static void AssertNotUiThread()
    {
        if (UiThreadId != 0 && Environment.CurrentManagedThreadId == UiThreadId)
        {
            throw new InvalidOperationException("Database access on the UI thread. Query on a background thread instead.");
        }
    }

    private static bool QuickCheckPasses(string file, out string problem)
    {
        problem = "";
        try
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            string result = Scalar(connection, "PRAGMA quick_check");
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            problem = result;
            return false;
        }
        catch (SqliteException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static string MoveDamagedAside(string file, IClock clock)
    {
        SqliteConnection.ClearAllPools();
        string stamp = clock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string directory = Path.GetDirectoryName(file)!;
        string stem = Path.GetFileNameWithoutExtension(file);
        string target = Path.Combine(directory, $"{stem}.damaged-{stamp}.db");
        File.Move(file, target, overwrite: true);
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(file + suffix))
            {
                File.Move(file + suffix, target + suffix, overwrite: true);
            }
        }

        return target;
    }

    private static int MarkInterruptedRuns(SqliteConnection connection, IClock clock)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET status = $interrupted, ended_utc = COALESCE(ended_utc, MAX($now, started_utc)) WHERE status = $running";
        command.Parameters.AddWithValue("$interrupted", (int)RunStatus.Interrupted);
        command.Parameters.AddWithValue("$running", (int)RunStatus.Running);
        command.Parameters.AddWithValue("$now", clock.UtcNow.UtcTicks);
        return command.ExecuteNonQuery();
    }

    private static void AddRecoveryNotification(SqliteConnection connection, IClock clock, string damagedCopy)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO notifications(utc, level, title, body, is_read) VALUES ($utc, $level, $title, $body, 0)";
        command.Parameters.AddWithValue("$utc", clock.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$level", (int)Severity.High);
        command.Parameters.AddWithValue("$title", CoreStrings.DatabaseRecoveredTitle);
        command.Parameters.AddWithValue("$body", string.Format(CultureInfo.CurrentCulture, CoreStrings.DatabaseRecoveredBody, damagedCopy));
        command.ExecuteNonQuery();
    }

    /// <summary>Idle read-only connections, opened once with their pragmas and reused.</summary>
    private sealed class ReaderPool(string connectionString) : IDisposable
    {
        private const int MaxIdle = 4;
        private readonly ConcurrentBag<SqliteConnection> _idle = [];
        private int _disposed;

        public SqliteConnection Rent()
        {
            if (_idle.TryTake(out SqliteConnection? connection))
            {
                return connection;
            }

            connection = new SqliteConnection(connectionString);
            connection.Open();
            ApplyPragmas(connection, cacheKib: 8192, writer: false);
            return connection;
        }

        public void Return(SqliteConnection connection)
        {
            if (Volatile.Read(ref _disposed) != 0 || _idle.Count >= MaxIdle)
            {
                connection.Dispose();
                return;
            }

            _idle.Add(connection);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            while (_idle.TryTake(out SqliteConnection? connection))
            {
                connection.Dispose();
            }
        }
    }
}
