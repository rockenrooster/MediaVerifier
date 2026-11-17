using System.Globalization;
using MediaVerifier.Core.Logging;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Foundation;

public sealed class AppLogTests
{
    [Fact]
    public void TenThousandCallsFromEightThreadsLoseNothingAndRollAtTheCap()
    {
        using TempDirectory temp = new("log");
        const long cap = 256 * 1024;
        const int threads = 8;
        const int perThread = 1250;
        AppLog log = new(temp.Path, maxBytes: cap);

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, thread =>
        {
            for (int i = 0; i < perThread; i++)
            {
                log.Info(string.Create(CultureInfo.InvariantCulture, $"thread {thread} message {i}"));
            }
        });
        log.Dispose();

        string[] files = [log.FilePath, log.RolledFilePath(1), log.RolledFilePath(2), log.RolledFilePath(3)];
        HashSet<string> seen = [];
        foreach (string file in files.Where(File.Exists))
        {
            Assert.True(new FileInfo(file).Length <= cap, $"{file} exceeds the cap");
            foreach (string line in File.ReadLines(file))
            {
                int start = line.IndexOf("thread ", StringComparison.Ordinal);
                Assert.True(start >= 0, line);
                Assert.True(seen.Add(line[start..]), "duplicate " + line);
            }
        }

        Assert.Equal(threads * perThread, seen.Count);
        Assert.Equal(0, log.DroppedCount);
        Assert.True(File.Exists(log.RolledFilePath(1)), "the log never rolled");
    }

    [Fact]
    public void KeepsAtMostThreeRolledFiles()
    {
        using TempDirectory temp = new("roll");
        AppLog log = new(temp.Path, maxBytes: 2048);
        for (int i = 0; i < 2000; i++)
        {
            log.Warning(string.Create(CultureInfo.InvariantCulture, $"entry {i:D5} with some padding text"));
        }

        log.Dispose();

        Assert.True(File.Exists(log.RolledFilePath(3)));
        Assert.False(File.Exists(log.RolledFilePath(4)));
        Assert.Contains("[Warning] entry 01999", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorLogWritesTheExceptionAndCollapsesRepeats()
    {
        using TempDirectory temp = new("err");
        ErrorLog.Configure(temp.Path);
        try
        {
            InvalidOperationException exception = new("boom");
            ErrorLog.Write("Test", exception);
            ErrorLog.Write("Test", exception);

            string text = File.ReadAllText(Path.Combine(temp.Path, "error.log"));
            Assert.Single(text.Split("[Test]", StringSplitOptions.None).Skip(1));
            Assert.Contains("boom", text, StringComparison.Ordinal);
        }
        finally
        {
            ErrorLog.Configure(TestLogs.Directory);
        }
    }
}
