using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace MediaVerifier.Core.Logging;

public enum LogLevel : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// Small asynchronous logger for <c>logs\app.log</c> (subplan 01). Callers only enqueue: one writer
/// task drains a bounded channel, so no caller (least of all the UI thread) waits on the disk. The
/// file is capped (5 MB by default) and rolled to <c>app.1.log</c> … <c>app.3.log</c>.
/// </summary>
public sealed class AppLog : IDisposable
{
    public const long DefaultMaxBytes = 5L * 1024 * 1024;
    public const int RolledFileCount = 3;
    private const int Capacity = 32_768;

    private static volatile AppLog? s_shared;

    private static readonly TimeSpan RollRetry = TimeSpan.FromMinutes(1);

    private readonly Channel<Entry> _channel;
    private readonly Task _writerTask;
    private readonly long _maxBytes;
    private readonly IClock _clock;
    private long _dropped;
    private long _unwritten;
    private long _rollBlockedUntil;
    private int _disposed;

    public AppLog(string directory, string fileName = "app.log", long maxBytes = DefaultMaxBytes, IClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1024);
        Directory = System.IO.Path.GetFullPath(directory);
        FilePath = System.IO.Path.Combine(Directory, fileName);
        _maxBytes = maxBytes;
        _clock = clock ?? SystemClock.Instance;
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _writerTask = Task.Factory.StartNew(
            WriteLoopAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>The process-wide log, set once at startup. Null in tests that do not install one.</summary>
    public static AppLog? Shared => s_shared;

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>Messages lost because the queue was full; reported in the log once the writer catches up.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public static void SetShared(AppLog? log) => s_shared = log;

    public string RolledFilePath(int index)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(FilePath);
        string extension = System.IO.Path.GetExtension(FilePath);
        return System.IO.Path.Combine(Directory, $"{name}.{index.ToString(CultureInfo.InvariantCulture)}{extension}");
    }

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warning(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    /// <summary>Never blocks and never throws. When the queue is full the message is counted as dropped.</summary>
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        Entry entry = new(_clock.UtcNow, level, message ?? "", exception);
        if (!_channel.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Stops accepting messages and waits (bounded) for the queue to reach the disk.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        if (ReferenceEquals(s_shared, this))
        {
            s_shared = null;
        }
    }

    /// <summary>
    /// Writes entries until the channel completes. Logging is best effort: a write that fails (a full or read-only disk,
    /// the file locked by another program) loses that entry only, counted and reported once writing works again; the
    /// writer itself keeps going for the rest of the session.
    /// </summary>
    private async Task WriteLoopAsync()
    {
        ChannelReader<Entry> reader = _channel.Reader;
        StringBuilder builder = new(256);
        FileStream? stream = null;
        long reportedDropped = 0;
        long reportedUnwritten = 0;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out Entry entry))
                {
                    builder.Clear();
                    long dropped = Interlocked.Read(ref _dropped);
                    if (dropped != reportedDropped)
                    {
                        Format(builder, new Entry(entry.Time, LogLevel.Warning, $"{(dropped - reportedDropped).ToString(CultureInfo.InvariantCulture)} log messages were dropped because the queue was full", null));
                    }

                    long unwritten = Interlocked.Read(ref _unwritten);
                    if (unwritten != reportedUnwritten)
                    {
                        Format(builder, new Entry(entry.Time, LogLevel.Warning, $"{(unwritten - reportedUnwritten).ToString(CultureInfo.InvariantCulture)} log messages could not be written", null));
                    }

                    Format(builder, entry);
                    try
                    {
                        WriteText(ref stream, builder);
                        reportedDropped = dropped;
                        reportedUnwritten = unwritten;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Close(ref stream);
                        Interlocked.Increment(ref _unwritten);
                    }
                }

                try
                {
                    stream?.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Close(ref stream);
                }
            }
        }
        finally
        {
            Close(ref stream);
        }
    }

    /// <summary>
    /// Closes the file, losing what is still buffered when that cannot be written either: a failed close (it flushes,
    /// so a full disk fails it again) must not end the writer.
    /// </summary>
    private static void Close(ref FileStream? stream)
    {
        try
        {
            stream?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        stream = null;
    }

    /// <summary>Appends the text, rolling first at the cap. <paramref name="stream"/> always holds what is open, even on failure.</summary>
    private void WriteText(ref FileStream? stream, StringBuilder text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        stream ??= Open();
        if (stream.Length > 0 && stream.Length + bytes.Length > _maxBytes && Environment.TickCount64 >= _rollBlockedUntil)
        {
            Close(ref stream);
            try
            {
                Roll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another program holds app.log without delete sharing: keep appending past the cap, try again later.
                _rollBlockedUntil = Environment.TickCount64 + (long)RollRetry.TotalMilliseconds;
            }

            stream = Open();
        }

        stream.Write(bytes);
    }

    private FileStream Open()
    {
        System.IO.Directory.CreateDirectory(Directory);
        FileStream stream = new(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096);
        return stream;
    }

    private void Roll()
    {
        string oldest = RolledFilePath(RolledFileCount);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = RolledFileCount - 1; index >= 1; index--)
        {
            string source = RolledFilePath(index);
            if (File.Exists(source))
            {
                File.Move(source, RolledFilePath(index + 1));
            }
        }

        File.Move(FilePath, RolledFilePath(1));
    }

    private static void Format(StringBuilder builder, Entry entry)
    {
        builder.Append(entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        builder.Append(" [").Append(LevelName(entry.Level)).Append("] ");
        builder.Append(entry.Message);
        if (entry.Exception is not null)
        {
            builder.Append(Environment.NewLine).Append(entry.Exception.ToString());
        }

        builder.Append(Environment.NewLine);
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        _ => "Info",
    };

    private readonly record struct Entry(DateTimeOffset Time, LogLevel Level, string Message, Exception? Exception);
}
