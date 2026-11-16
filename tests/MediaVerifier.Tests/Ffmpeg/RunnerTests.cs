using System.Diagnostics;
using System.Globalization;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Ffmpeg;

/// <summary>Process runner behaviour (task FF-03) with a helper console program standing in for ffmpeg.</summary>
public sealed class RunnerTests
{
    private static readonly string Helper = Path.Combine(AppContext.BaseDirectory, "MediaVerifier.TestHelper.exe");

    private static FfRunner CreateRunner() => new(new FfmpegLocation(AppContext.BaseDirectory, IsOverride: true))
    {
        ExecutableOverride = _ => Helper,
        PrependStandardArguments = false,
    };

    [Fact]
    public async Task IdleTimeoutKillsASilentProcess()
    {
        using FfRunner runner = CreateRunner();
        Stopwatch watch = Stopwatch.StartNew();

        FfResult result = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = ["sleep", "30000"], IdleTimeout = TimeSpan.FromSeconds(1), HardTimeout = TimeSpan.FromSeconds(60) });

        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.True(result.IdleTimeout);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task HardTimeoutStopsAProcessThatKeepsTalking()
    {
        using FfRunner runner = CreateRunner();

        FfResult result = await runner.RunAsync(new FfRequest
        {
            Tool = FfTool.Ffmpeg,
            Arguments = ["progress", "1000"],
            Stdout = StdoutMode.Progress,
            IdleTimeout = TimeSpan.FromSeconds(30),
            HardTimeout = TimeSpan.FromSeconds(1),
        });

        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.False(result.IdleTimeout);
        Assert.NotNull(result.FinalProgress);
    }

    [Fact]
    public async Task CancelKillsTheProcessAndItsChildren()
    {
        using TempDirectory temp = new("cancel");
        string pidFile = temp.Combine("child.pid");
        using FfRunner runner = CreateRunner();
        using CancellationTokenSource cancel = new();

        Task<FfResult> run = runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = ["spawn", pidFile, "30000"], IdleTimeout = TimeSpan.FromSeconds(60) }, cancel.Token);
        Stopwatch watch = Stopwatch.StartNew();
        while (!File.Exists(pidFile) && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(50);
        }

        int childPid = int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture);
        await cancel.CancelAsync();
        FfResult result = await run;

        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
        Assert.True(await ExitsSoon(childPid), "the grandchild survived");
    }

    [Fact]
    public async Task FiveMillionLogLinesKeepMemoryFlat()
    {
        using FfRunner runner = CreateRunner();
        long before = GC.GetTotalMemory(forceFullCollection: true);

        FfResult result = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = ["lines", "5000000"], HardTimeout = TimeSpan.FromMinutes(5) });

        long after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.Equal(ProcessOutcome.Completed, result.Outcome);
        Assert.Equal(5_000_000, result.Log.TotalLines);
        Assert.Equal(5_000_000, result.Log.Count(LogSignalKind.DecodeError));
        Assert.Equal(200, result.Log.Head.Count);
        Assert.Equal(50, result.Log.Tail.Count);
        Assert.True(result.Log.Kinds[LogSignalKind.DecodeError].TopMessages.Count <= 20);
        Assert.True(after - before < 32 * 1024 * 1024, $"retained {(after - before) / 1024 / 1024} MB");
    }

    [Fact]
    public async Task BinaryStdoutReachesTheConsumer()
    {
        using FfRunner runner = CreateRunner();
        long total = 0;

        FfResult result = await runner.RunAsync(new FfRequest
        {
            Tool = FfTool.Ffmpeg,
            Arguments = ["binary", "10000000"],
            Stdout = StdoutMode.Binary,
            BinaryConsumer = async (stream, token) =>
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                }
            },
        });

        Assert.Equal(ProcessOutcome.Completed, result.Outcome);
        Assert.Equal(10_000_000, total);
    }

    [Fact]
    public async Task ProgressBlocksAreParsedAsTheyArrive()
    {
        using FfRunner runner = CreateRunner();
        int blocks = 0;

        FfResult result = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = ["progress", "5"], Stdout = StdoutMode.Progress, OnProgress = _ => blocks++ });

        Assert.Equal(5, blocks);
        Assert.NotNull(result.FinalProgress);
        Assert.True(result.FinalProgress!.IsEnd);
        Assert.Equal(120, result.FinalProgress.Frame);
        Assert.Equal(5.0, result.FinalProgress.OutTimeSeconds);
    }

    [Fact]
    public async Task ExitCodeIsRecordedButNotInterpreted()
    {
        using FfRunner runner = CreateRunner();
        FfResult result = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = ["exit", "183"] });
        Assert.Equal(ProcessOutcome.Completed, result.Outcome);
        Assert.Equal(183, result.ExitCode);
    }

    [Fact]
    public async Task AMissingExecutableFailsToStart()
    {
        using FfRunner runner = new(new FfmpegLocation(@"C:\Nowhere\" + Guid.NewGuid().ToString("N"), IsOverride: true));
        FfResult result = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffprobe, Arguments = ["-version"] });
        Assert.Equal(ProcessOutcome.FailedToStart, result.Outcome);
        Assert.NotNull(result.StartError);
    }

    private static async Task<bool> ExitsSoon(int pid)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
