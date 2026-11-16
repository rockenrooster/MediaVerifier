using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Core.Ffmpeg;

public enum FfTool : byte
{
    Ffmpeg = 0,
    Ffprobe = 1,
}

/// <summary>What standard output carries for a run.</summary>
public enum StdoutMode : byte
{
    /// <summary>Read and discard (still counts as activity for the idle timeout).</summary>
    Discard = 0,

    /// <summary>Collect as UTF-8 text (ffprobe JSON).</summary>
    Text = 1,

    /// <summary><c>-progress pipe:1</c> key=value blocks, parsed as they arrive.</summary>
    Progress = 2,

    /// <summary>Binary data (PCM, images) handed to <see cref="FfRequest.BinaryConsumer"/>.</summary>
    Binary = 3,
}

public enum ProcessOutcome : byte
{
    Completed = 0,
    TimedOut = 1,
    Cancelled = 2,
    FailedToStart = 3,
}

/// <summary>One FFmpeg or ffprobe invocation. Arguments go after the standard arguments, one item each.</summary>
public sealed record FfRequest
{
    public required FfTool Tool { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Per-job scratch folder (filters write stats files there); created if missing.</summary>
    public string? WorkingDirectory { get; init; }

    public StdoutMode Stdout { get; init; } = StdoutMode.Discard;

    /// <summary>Consumes stdout when <see cref="Stdout"/> is Binary. Wrap nothing in a StreamReader.</summary>
    public Func<Stream, CancellationToken, Task>? BinaryConsumer { get; init; }

    /// <summary>Called for each complete progress block.</summary>
    public Action<ProgressBlock>? OnProgress { get; init; }

    /// <summary>No bytes on stdout for this long kills the process (default 60 s, configurable).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Total time allowed (see <see cref="FfmpegArgs.HardTimeout"/>).</summary>
    public TimeSpan HardTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.BelowNormal;

    /// <summary>Called for each classified stderr line (tests use it for the unknown-line gate).</summary>
    public Action<LogLine>? OnLogLine { get; init; }
}

/// <summary>How a run ended. Exit codes are recorded but never trusted on their own (appendix A).</summary>
public sealed record FfResult(
    ProcessOutcome Outcome,
    int? ExitCode,
    TimeSpan WallTime,
    long PeakWorkingSetBytes,
    LogSummary Log,
    string? StdoutText,
    ProgressBlock? FinalProgress,
    bool IdleTimeout,
    string? StartError)
{
    public bool Completed => Outcome == ProcessOutcome.Completed;
}

/// <summary>
/// The only code that starts FFmpeg (subplan 04, Process runner): arguments through ArgumentList, no
/// shell, every child in the engine's Job Object, stdout and stderr read concurrently, idle and hard
/// timeouts, cancellation that kills and waits, bounded log collection.
/// </summary>
public sealed class FfRunner : IDisposable
{
    private readonly FfmpegLocation _location;
    private readonly JobObject _job;
    private readonly bool _ownsJob;

    public FfRunner(FfmpegLocation location, JobObject? job = null)
    {
        _location = location;
        _ownsJob = job is null;
        _job = job ?? new JobObject();
    }

    public FfmpegLocation Location => _location;

    /// <summary>Overrides the executable for tests (the helper console program).</summary>
    internal Func<FfTool, string>? ExecutableOverride { get; init; }

    /// <summary>When false the standard arguments are not prepended (tests with the helper program).</summary>
    internal bool PrependStandardArguments { get; init; } = true;

    public async Task<FfResult> RunAsync(FfRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string executable = ExecutableOverride?.Invoke(request.Tool) ?? (request.Tool == FfTool.Ffprobe ? _location.FfprobeExe : _location.FfmpegExe);
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (request.Stdout is StdoutMode.Text or StdoutMode.Progress)
        {
            start.StandardOutputEncoding = new UTF8Encoding(false);
        }

        if (request.WorkingDirectory is not null)
        {
            Directory.CreateDirectory(request.WorkingDirectory);
            start.WorkingDirectory = request.WorkingDirectory;
        }

        if (PrependStandardArguments)
        {
            foreach (string arg in FfmpegArgs.Standard(request.Tool))
            {
                start.ArgumentList.Add(arg);
            }
        }

        foreach (string arg in request.Arguments)
        {
            start.ArgumentList.Add(arg);
        }

        Stopwatch wall = Stopwatch.StartNew();
        LogCollector log = new() { OnLine = request.OnLogLine };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new FfResult(ProcessOutcome.FailedToStart, null, wall.Elapsed, 0, log.ToSummary(), null, null, false, ex.Message);
        }

        using (process)
        {
            if (!_job.TryAssign(process.SafeHandle))
            {
                AppLog.Shared?.Warning("Could not add an FFmpeg process to the job object; it will be killed directly on cancel");
            }

            TrySetPriority(process, request.Priority);

            long lastActivity = Stopwatch.GetTimestamp();
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            StringBuilder? stdoutText = request.Stdout == StdoutMode.Text ? new StringBuilder() : null;
            ProgressParser? progress = request.Stdout == StdoutMode.Progress ? new ProgressParser(request.OnProgress) : null;

            Task stdoutTask = ReadStdoutAsync(process, request, stdoutText, progress, () => Volatile.Write(ref lastActivity, Stopwatch.GetTimestamp()), stop.Token);
            Task stderrTask = ReadStderrAsync(process, log, stop.Token);

            bool idleTimeout = false;
            bool hardTimeout = false;
            bool cancelled = false;
            Task exited = process.WaitForExitAsync(CancellationToken.None);
            while (!exited.IsCompleted)
            {
                Task delay = Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                await Task.WhenAny(exited, delay, WhenCancelled(cancellationToken)).ConfigureAwait(false);
                if (exited.IsCompleted)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                if (wall.Elapsed > request.HardTimeout)
                {
                    hardTimeout = true;
                    break;
                }

                if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivity)) > request.IdleTimeout)
                {
                    idleTimeout = true;
                    break;
                }
            }

            long peak = 0;
            if (!exited.IsCompleted)
            {
                peak = SafePeak(process);
                Kill(process);
                await exited.ConfigureAwait(false);
            }

            // Drain whatever remains in the pipes, then stop the readers.
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                stop.Cancel();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Pipe closed by the kill; nothing more to read.
            }

            if (peak == 0)
            {
                peak = SafePeak(process);
            }

            progress?.Complete();
            int? exitCode = TryExitCode(process);
            ProcessOutcome outcome = cancelled ? ProcessOutcome.Cancelled : (idleTimeout || hardTimeout) ? ProcessOutcome.TimedOut : ProcessOutcome.Completed;
            return new FfResult(outcome, exitCode, wall.Elapsed, peak, log.ToSummary(), stdoutText?.ToString(), progress?.Final, idleTimeout, null);
        }
    }

    public void Dispose()
    {
        if (_ownsJob)
        {
            _job.Dispose();
        }
    }

    private static async Task ReadStdoutAsync(Process process, FfRequest request, StringBuilder? text, ProgressParser? progress, Action activity, CancellationToken token)
    {
        switch (request.Stdout)
        {
            case StdoutMode.Binary when request.BinaryConsumer is not null:
                {
                    ActivityStream stream = new(process.StandardOutput.BaseStream, activity);
                    try
                    {
                        await request.BinaryConsumer(stream, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Keep draining so FFmpeg never blocks on a full pipe after the consumer stops.
                        await DrainAsync(stream, token).ConfigureAwait(false);
                    }

                    break;
                }

            case StdoutMode.Text:
            case StdoutMode.Progress:
                {
                    char[] buffer = new char[4096];
                    LineSplitter? lines = progress is null ? null : new LineSplitter(progress.AddLine);
                    while (true)
                    {
                        int read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        activity();
                        text?.Append(buffer, 0, read);
                        lines?.Feed(buffer.AsSpan(0, read));
                    }

                    lines?.Flush();
                    break;
                }

            default:
                await DrainAsync(new ActivityStream(process.StandardOutput.BaseStream, activity), token).ConfigureAwait(false);
                break;
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) > 0)
        {
        }
    }

    private static async Task ReadStderrAsync(Process process, LogCollector log, CancellationToken token)
    {
        char[] buffer = new char[4096];
        LineSplitter lines = new(log.Add);
        while (true)
        {
            int read = await process.StandardError.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            lines.Feed(buffer.AsSpan(0, read));
        }

        lines.Flush();
    }

    private static Task WhenCancelled(CancellationToken token)
    {
        if (!token.CanBeCanceled)
        {
            return Task.Delay(Timeout.Infinite, CancellationToken.None);
        }

        TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => source.TrySetResult());
        return source.Task;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited.
        }
    }

    private static long SafePeak(Process process)
    {
        try
        {
            process.Refresh();
            return process.PeakWorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }

    private static int? TryExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void TrySetPriority(Process process, ProcessPriorityClass priority)
    {
        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process may already have exited.
        }
    }

    /// <summary>Read-only wrapper that records activity for the idle timeout.</summary>
    private sealed class ActivityStream(Stream inner, Action activity) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                activity();
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                activity();
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
