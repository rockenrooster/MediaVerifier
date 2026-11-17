using System.Diagnostics;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Engine;

/// <summary>
/// Budgets of the master plan that the engine owns (subplan 16, Performance pass; tasks H-01, T-11): enumeration rate,
/// fingerprint speed against a raw read, engine overhead with no-op providers and scan memory. Run by
/// scripts/Measure-Performance.ps1, which also records the results in docs/perf-results.md.
/// </summary>
[Trait("Category", "Perf")]
public sealed class EnginePerfTests(ITestOutputHelper output)
{
    private static readonly int TreeFiles = int.TryParse(Environment.GetEnvironmentVariable("MEDIAVERIFIER_PERF_FILES"), out int files) ? files : 100_000;

    [Fact]
    public void Enumeration_reaches_50000_files_a_second()
    {
        using TempDirectory temp = new("enum");
        BuildTree(temp.Path, TreeFiles);
        TreeEnumerator enumerator = new(WindowsFileSystem.Instance, MediaClassifier.Default, new EnumerationRules { InProgressAge = TimeSpan.Zero }, SystemClock.Instance);
        _ = enumerator.Enumerate(temp.Path).Sum(d => d.Files.Count);
        Stopwatch watch = Stopwatch.StartNew();
        int files = enumerator.Enumerate(temp.Path).Sum(d => d.Files.Count);
        watch.Stop();
        double rate = files / watch.Elapsed.TotalSeconds;
        Record("Enumeration", $"{files:N0} files in {watch.Elapsed.TotalSeconds:N2} s", $"{rate:N0} files/s", "≥ 50,000 files/s on SSD");
        Assert.Equal(TreeFiles, files);
        Assert.True(rate >= 50_000, $"{rate:N0} files/s");
    }

    [Fact]
    public async Task Fingerprinting_keeps_up_with_a_raw_read()
    {
        using TempDirectory temp = new("hash");
        string path = Path.Combine(temp.Path, "big.mkv");
        byte[] block = new byte[1 << 20];
        new Random(16).NextBytes(block);
        await using (FileStream file = File.Create(path))
        {
            for (int i = 0; i < 1024; i++)
            {
                await file.WriteAsync(block);
            }
        }

        // Both read the same (cached) gigabyte: the difference is the hashing cost.
        _ = await RawReadAsync(path);
        Stopwatch raw = Stopwatch.StartNew();
        long bytes = await RawReadAsync(path);
        raw.Stop();
        _ = await IntegrityReader.ReadAsync(path, WindowsFileSystem.Instance);
        Stopwatch hashed = Stopwatch.StartNew();
        IntegrityResult result = await IntegrityReader.ReadAsync(path, WindowsFileSystem.Instance);
        hashed.Stop();
        double rawRate = bytes / raw.Elapsed.TotalSeconds / 1e9;
        double hashRate = bytes / hashed.Elapsed.TotalSeconds / 1e9;
        Record("Fingerprint", "1 GiB from the file cache", $"{hashRate:N2} GB/s (raw read {rawRate:N2} GB/s)", "limited by storage");
        Assert.NotNull(result.Hash);

        // Faster than any disk the app is meant for (an NVMe drive reads about 3 to 7 GB/s; SATA SSD 0.5).
        Assert.True(hashRate >= 2.0, $"{hashRate:N2} GB/s");
    }

    [Fact]
    public async Task Engine_overhead_and_scan_memory_stay_within_budget()
    {
        using EngineHarness h = new([new CountingProvider()]);
        BuildTree(h.Library, TreeFiles);
        await h.AddRootAsync(h.Library);
        using Process self = Process.GetCurrentProcess();
        GC.Collect();
        self.Refresh();
        long privateBefore = self.PrivateMemorySize64;
        TimeSpan cpuBefore = self.TotalProcessorTime;
        long peak = privateBefore;
        using CancellationTokenSource stop = new();
        Task sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                self.Refresh();
                peak = Math.Max(peak, self.PrivateMemorySize64);
                await Task.Delay(100).ConfigureAwait(false);
            }
        });
        Stopwatch wall = Stopwatch.StartNew();
        RunSummary run = await h.ScanAsync(ScanDepth.Quick);
        wall.Stop();
        stop.Cancel();
        await sampler;
        self.Refresh();
        double cpuSeconds = (self.TotalProcessorTime - cpuBefore).TotalSeconds;
        double perFileMicroseconds = cpuSeconds / TreeFiles * 1e6;

        // At the analysis rate of a real Standard scan (about 20 files a second on one core's worth of decoding),
        // the engine's own share is the per-file cost times that rate.
        double shareAt20PerSecond = perFileMicroseconds * 20 / 1e6 * 100;
        long scanMegabytes = (peak - privateBefore) / (1 << 20);
        Record("Engine overhead", $"{TreeFiles:N0} files, no-op providers, {wall.Elapsed.TotalSeconds:N1} s", $"{perFileMicroseconds:N0} µs CPU a file ({shareAt20PerSecond:N2} % of a core at 20 files/s)", "< 5 % of one core");
        Record("Scan memory", $"{TreeFiles:N0} files, peak private bytes above the start", $"{scanMegabytes:N0} MB", "≤ 400 MB at 1,000,000 files");
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(TreeFiles, (int)run.Counters.FilesSeen);
        Assert.True(shareAt20PerSecond < 5, $"{shareAt20PerSecond:N2} %");
        Assert.True(scanMegabytes < 400, $"{scanMegabytes} MB");
    }

    /// <summary>100 files a folder, 10 folders a level, empty photo files.</summary>
    private static void BuildTree(string root, int files)
    {
        byte[] content = [0xFF, 0xD8, 0xFF, 0xE0];
        DateTime old = DateTime.UtcNow.AddDays(-30);
        Parallel.For(0, files / 100, folder =>
        {
            string path = Path.Combine(root, "A" + (folder / 100).ToString("000", System.Globalization.CultureInfo.InvariantCulture), "B" + (folder % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(path);
            for (int i = 0; i < 100; i++)
            {
                string file = Path.Combine(path, "IMG_" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + ".jpg");
                File.WriteAllBytes(file, content);
                File.SetLastWriteTimeUtc(file, old);
            }
        });
    }

    private static async Task<long> RawReadAsync(string path)
    {
        byte[] buffer = new byte[IntegrityReader.BufferSize];
        long total = 0;
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }

        return total;
    }

    private void Record(string measurement, string setup, string result, string budget)
    {
        output.WriteLine($"{measurement}: {setup}: {result} (budget {budget})");
        PerfReport.Add(measurement, setup, result, budget);
    }
}
