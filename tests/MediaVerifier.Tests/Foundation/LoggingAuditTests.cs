using MediaVerifier.Core.Logging;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Foundation;

/// <summary>Logging behaviour found wrong in the November 2026 audit.</summary>
public sealed class AppLogAuditTests
{
    [Fact]
    public void A_log_whose_roll_is_blocked_keeps_writing()
    {
        using TempDirectory temp = new("log-blocked");
        AppLog log = new(temp.Path, maxBytes: 2048);
        for (int i = 0; i < 20; i++)
        {
            log.Info($"before {i:D2} with some padding text to reach the cap soon");
        }

        WaitForFile(log.FilePath, "before 19");

        // A viewer holds app.log without delete sharing: the roll fails, the writer goes on.
        using (FileStream viewer = new(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            for (int i = 0; i < 40; i++)
            {
                log.Warning($"during {i:D2} with some padding text to reach the cap soon");
            }

            WaitForFile(log.FilePath, "during 39");
        }

        log.Info("after the viewer closed");
        log.Dispose();
        string all = string.Concat(new[] { log.FilePath, log.RolledFilePath(1), log.RolledFilePath(2), log.RolledFilePath(3) }.Where(File.Exists).Select(File.ReadAllText));
        Assert.Contains("during 39", all, StringComparison.Ordinal);
        Assert.Contains("after the viewer closed", all, StringComparison.Ordinal);
    }

    private static void WaitForFile(string path, string text)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < end)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                if (reader.ReadToEnd().Contains(text, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException)
            {
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException($"'{text}' never reached {path}");
    }
}

/// <summary>Tests that point the process-wide error log at their own folder run alone: every test shares it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ErrorLogCollection
{
    public const string Name = "Error log";
}

[Collection(ErrorLogCollection.Name)]
public sealed class ErrorLogAuditTests
{
    [Fact]
    public void The_same_error_in_many_contexts_is_written_once_with_a_count()
    {
        using TempDirectory temp = new("err-repeat");
        ErrorLog.Configure(temp.Path);
        try
        {
            IOException full = new("There is not enough space on the disk.");
            ErrorLog.Write("Unhandled", full);
            ErrorLog.Write("UI thread", full);
            ErrorLog.Write("Unobserved task", full);
            ErrorLog.Write("Unhandled", new InvalidOperationException("something else"));

            string text = File.ReadAllText(Path.Combine(temp.Path, "error.log"));
            Assert.Single(text.Split("not enough space", StringSplitOptions.None).Skip(1));
            Assert.Contains("repeated 2 more times", text, StringComparison.Ordinal);
            Assert.Contains("something else", text, StringComparison.Ordinal);
        }
        finally
        {
            ErrorLog.Configure(TestEnvironment.ErrorLogDirectory);
        }
    }

    [Fact]
    public void An_error_that_is_not_a_crash_goes_to_the_app_log_only()
    {
        using TempDirectory temp = new("err-sink");
        ErrorLog.Configure(temp.Path);
        AppLog log = new(temp.Path);
        AppLog.SetShared(log);
        try
        {
            ErrorLog.Write("Scanning D:\\a.flac", new InvalidOperationException("a provider bug"));
        }
        finally
        {
            AppLog.SetShared(null);
            log.Dispose();
            ErrorLog.Configure(TestEnvironment.ErrorLogDirectory);
        }

        Assert.False(File.Exists(Path.Combine(temp.Path, "error.log")));
        string app = File.ReadAllText(log.FilePath);
        Assert.Contains("Scanning D:\\a.flac", app, StringComparison.Ordinal);
        Assert.Contains("a provider bug", app, StringComparison.Ordinal);
    }
}
