using System.Diagnostics;
using System.Threading.Channels;
using MediaVerifier.Core.Logging;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

/// <summary>
/// The only code that executes INSERT, UPDATE or DELETE (subplan 03). Operations arrive over a bounded
/// channel and run on one connection in batched transactions, committed every 250 ms or 500 operations,
/// or as soon as the queue is empty. Each operation runs in a savepoint, so one failing operation does
/// not undo the others.
/// </summary>
public sealed class DbWriter : IDisposable
{
    public const int MaxOpsPerBatch = 500;
    public static readonly TimeSpan MaxBatchTime = TimeSpan.FromMilliseconds(250);
    private const int QueueCapacity = 4096;

    private readonly SqliteConnection _connection;
    private readonly Channel<WriteOp> _channel;
    private readonly Task _loop;
    private readonly IClock _clock;
    private readonly ChangeNotifier _notifier;
    private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);
    private int _disposed;

    internal DbWriter(SqliteConnection connection, IClock clock, Action<ChangeFlags> changed)
    {
        _connection = connection;
        _clock = clock;
        _notifier = new ChangeNotifier(changed);
        _channel = Channel.CreateBounded<WriteOp>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _loop = Task.Factory.StartNew(RunAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Queues an operation and completes when its batch has committed, with its result.</summary>
    public async Task<T> ExecuteAsync<T>(Func<WriteContext, T> operation, ChangeFlags changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        WriteOp<T> op = new(operation, changes);
        await _channel.Writer.WriteAsync(op, cancellationToken).ConfigureAwait(false);
        return await op.Completion.Task.ConfigureAwait(false);
    }

    public Task ExecuteAsync(Action<WriteContext> operation, ChangeFlags changes, CancellationToken cancellationToken = default) =>
        ExecuteAsync<bool>(
            context =>
            {
                operation(context);
                return true;
            },
            changes,
            cancellationToken);

    /// <summary>
    /// Queues an operation without waiting for its commit; waits only while the queue is full
    /// (back-pressure). Failures are logged, and reported to <paramref name="failed"/> (on the writer thread) so a
    /// caller can tell that work it posted was lost.
    /// </summary>
    public async ValueTask PostAsync(Action<WriteContext> operation, ChangeFlags changes, CancellationToken cancellationToken = default, Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        WriteOp<bool> op = new(
            context =>
            {
                operation(context);
                return true;
            },
            changes,
            observeFailure: true,
            failed);
        await _channel.Writer.WriteAsync(op, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on the writer connection between batches, outside any transaction
    /// (for VACUUM and checkpoints that SQLite refuses inside one).
    /// </summary>
    public async Task ExecuteOutsideTransactionAsync(Action<SqliteConnection> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ExclusiveOp op = new(operation);
        await _channel.Writer.WriteAsync(op, cancellationToken).ConfigureAwait(false);
        await op.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>Completes once everything queued before the call has committed.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => ExecuteAsync(_ => { }, ChangeFlags.None, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException ex)
        {
            ErrorLog.Write("Database writer", ex);
        }

        foreach (SqliteCommand command in _commands.Values)
        {
            command.Dispose();
        }

        _commands.Clear();
        _notifier.Dispose();
    }

    private async Task RunAsync()
    {
        ChannelReader<WriteOp> reader = _channel.Reader;
        List<WriteOp> batch = new(MaxOpsPerBatch);
        WriteContext context = new(_connection, _commands, _clock);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            if (reader.TryPeek(out WriteOp? next) && next is ExclusiveOp exclusive && reader.TryRead(out _))
            {
                exclusive.Run(_connection);
                continue;
            }

            batch.Clear();
            ChangeFlags changes = ChangeFlags.None;
            long started = Stopwatch.GetTimestamp();
            SqliteTransaction? transaction = null;
            bool began = false;
            try
            {
                // BEGIN IMMEDIATE: waits for the write lock up to the connection's timeout (another program may hold it).
                transaction = _connection.BeginTransaction();
                began = true;
                context.Begin(transaction);
                while (batch.Count < MaxOpsPerBatch
                    && Stopwatch.GetElapsedTime(started) < MaxBatchTime
                    && reader.TryPeek(out WriteOp? peeked)
                    && peeked is not ExclusiveOp
                    && reader.TryRead(out WriteOp? op))
                {
                    batch.Add(op);
                    if (op.RunInSavepoint(context))
                    {
                        changes |= op.Changes;
                    }
                }

                transaction.Commit();
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                try
                {
                    transaction?.Rollback();
                }
                catch (Exception rollback) when (rollback is SqliteException or InvalidOperationException)
                {
                    // SQLite already rolled the transaction back itself (disk full, I/O error).
                }

                if (!began)
                {
                    // The write lock could not be had within the timeout (a backup tool or another program holds the
                    // database): fail what is waiting with that error instead of retrying it forever (subplan 16).
                    while (reader.TryRead(out WriteOp? waiting))
                    {
                        batch.Add(waiting);
                    }
                }

                foreach (WriteOp op in batch)
                {
                    op.Abort(ex);
                }

                AppLog.Shared?.Error("Database batch failed and was rolled back", ex);
                context.Caches.Clear();
                batch.Clear();
                changes = ChangeFlags.None;
            }
            finally
            {
                context.End();
                transaction?.Dispose();
            }

            foreach (WriteOp op in batch)
            {
                op.Complete();
            }

            if (changes != ChangeFlags.None)
            {
                _notifier.Notify(changes);
            }
        }
    }

    private sealed class ExclusiveOp(Action<SqliteConnection> operation) : WriteOp(ChangeFlags.None)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Run(SqliteConnection connection)
        {
            try
            {
                operation(connection);
                Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }

        public override bool RunInSavepoint(WriteContext context) => throw new InvalidOperationException("Exclusive operations run outside batches.");

        public override void Complete()
        {
        }

        public override void Abort(Exception exception) => Completion.TrySetException(exception);
    }

    private abstract class WriteOp(ChangeFlags changes)
    {
        public ChangeFlags Changes { get; } = changes;

        /// <summary>Runs inside a savepoint. Returns false when the operation failed and was rolled back.</summary>
        public abstract bool RunInSavepoint(WriteContext context);

        public abstract void Complete();

        public abstract void Abort(Exception exception);
    }

    private sealed class WriteOp<T>(Func<WriteContext, T> operation, ChangeFlags changes, bool observeFailure = false, Action<Exception>? failed = null) : WriteOp(changes)
    {
        private T? _result;
        private Exception? _failure;

        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool RunInSavepoint(WriteContext context)
        {
            context.Exec("SAVEPOINT op");
            try
            {
                _result = operation(context);
                context.Exec("RELEASE op");
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    context.Exec("ROLLBACK TO op");
                    context.Exec("RELEASE op");
                }
                catch (Exception rollback) when (rollback is SqliteException or InvalidOperationException)
                {
                    // SQLite rolled back the whole transaction (disk full, I/O error): the batch fails with the reason.
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex);
                }

                // Rows the operation added are gone; so must be any cached ids pointing at them.
                context.Caches.Clear();
                _failure = ex;
                if (observeFailure)
                {
                    AppLog.Shared?.Error("Database operation failed", ex);
                }

                return false;
            }
        }

        public override void Complete()
        {
            if (_failure is not null)
            {
                Completion.TrySetException(_failure);
                Report(_failure);
            }
            else
            {
                Completion.TrySetResult(_result!);
            }

            if (observeFailure)
            {
                _ = Completion.Task.Exception;
            }
        }

        public override void Abort(Exception exception)
        {
            Completion.TrySetException(exception);
            Report(exception);
            if (observeFailure)
            {
                _ = Completion.Task.Exception;
            }
        }

        private void Report(Exception exception)
        {
            try
            {
                failed?.Invoke(exception);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Database writer failure callback", ex);
            }
        }
    }

    /// <summary>Raises the change callback at most twice a second with the union of what changed.</summary>
    private sealed class ChangeNotifier : IDisposable
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);
        private readonly Action<ChangeFlags> _changed;
        private readonly Timer _timer;
        private readonly Lock _gate = new();
        private ChangeFlags _pending;
        private long _lastRaised;
        private bool _scheduled;

        public ChangeNotifier(Action<ChangeFlags> changed)
        {
            _changed = changed;
            _timer = new Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Notify(ChangeFlags changes)
        {
            lock (_gate)
            {
                _pending |= changes;
                if (_scheduled)
                {
                    return;
                }

                _scheduled = true;
                TimeSpan since = Stopwatch.GetElapsedTime(_lastRaised);
                TimeSpan delay = _lastRaised == 0 || since >= MinInterval ? TimeSpan.Zero : MinInterval - since;
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose() => _timer.Dispose();

        private void Raise()
        {
            ChangeFlags changes;
            lock (_gate)
            {
                changes = _pending;
                _pending = ChangeFlags.None;
                _scheduled = false;
                _lastRaised = Stopwatch.GetTimestamp();
            }

            if (changes != ChangeFlags.None)
            {
                try
                {
                    _changed(changes);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("LibraryChanged handler", ex);
                }
            }
        }
    }
}

/// <summary>
/// What a write operation sees: the writer connection inside the current transaction, cached prepared
/// commands, and the clock. Only valid during the operation.
/// </summary>
public sealed class WriteContext
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, SqliteCommand> _commands;
    private SqliteTransaction? _transaction;

    internal WriteContext(SqliteConnection connection, Dictionary<string, SqliteCommand> commands, IClock clock)
    {
        _connection = connection;
        _commands = commands;
        Clock = clock;
    }

    public IClock Clock { get; }

    public long NowTicks => Clock.UtcNow.UtcTicks;

    /// <summary>Writer-side caches (directory interning) keyed by name; cleared when a batch fails.</summary>
    internal Dictionary<string, object> Caches { get; } = new(StringComparer.Ordinal);

    /// <summary>A prepared command for <paramref name="sql"/>, reused across operations, parameters cleared.</summary>
    public SqliteCommand Command(string sql)
    {
        if (!_commands.TryGetValue(sql, out SqliteCommand? command))
        {
            command = _connection.CreateCommand();
            command.CommandText = sql;
            _commands[sql] = command;
        }

        command.Transaction = _transaction;
        command.Parameters.Clear();
        return command;
    }

    public int Exec(string sql)
    {
        SqliteCommand command = Command(sql);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs an INSERT and returns the new row's id. Used instead of <c>RETURNING</c>: reading a
    /// <c>RETURNING</c> row inside the per-operation savepoint is about 20 times slower and gets
    /// slower as the batch grows (measured, see the deviations log).
    /// </summary>
    public long Insert(SqliteCommand command)
    {
        command.ExecuteNonQuery();
        return LastInsertRowId();
    }

    public long LastInsertRowId() => SQLitePCL.raw.sqlite3_last_insert_rowid(_connection.Handle);

    internal void Begin(SqliteTransaction transaction) => _transaction = transaction;

    internal void End() => _transaction = null;
}
