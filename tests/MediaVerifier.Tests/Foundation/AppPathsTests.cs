using MediaVerifier.Core;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Foundation;

public sealed class AppPathsTests
{
    [Fact]
    public void EnsureCreatedMakesEveryWritableDirectoryUnderTheTempRoot()
    {
        using TempDirectory temp = new("paths");
        AppPaths paths = temp.CreateAppPaths();

        paths.EnsureCreated();

        string[] expected =
        [
            paths.RoamingDirectory,
            paths.LocalDirectory,
            paths.LogsDirectory,
            paths.CacheDirectory,
            paths.UpdatesDirectory,
            paths.TempDirectory,
        ];
        foreach (string directory in expected)
        {
            Assert.True(Directory.Exists(directory), directory);
            Assert.StartsWith(temp.Path, directory, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(Directory.Exists(paths.BundledFfmpegDirectory) && paths.BundledFfmpegDirectory.StartsWith(temp.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilesSitWhereSubplan01SaysTheyDo()
    {
        AppPaths paths = new(@"C:\R\MediaVerifier", @"C:\L\MediaVerifier", @"C:\Program\MediaVerifier");

        Assert.Equal(@"C:\R\MediaVerifier\settings.json", paths.SettingsFile);
        Assert.Equal(@"C:\L\MediaVerifier\library.db", paths.DefaultDatabaseFile);
        Assert.Equal(@"C:\L\MediaVerifier\logs", paths.LogsDirectory);
        Assert.Equal(@"C:\L\MediaVerifier\cache", paths.CacheDirectory);
        Assert.Equal(@"C:\L\MediaVerifier\Updates", paths.UpdatesDirectory);
        Assert.Equal(@"C:\L\MediaVerifier\temp\12\345", paths.JobTempDirectory(12, 345));
        Assert.Equal(@"C:\Program\MediaVerifier\ffmpeg", paths.BundledFfmpegDirectory);
    }

    [Fact]
    public void DatabaseOverrideWinsOnlyWhenSet()
    {
        AppPaths paths = new(@"C:\R\MediaVerifier", @"C:\L\MediaVerifier", @"C:\App");

        Assert.Equal(paths.DefaultDatabaseFile, paths.ResolveDatabaseFile(null));
        Assert.Equal(paths.DefaultDatabaseFile, paths.ResolveDatabaseFile("  "));
        Assert.Equal(@"D:\Data\lib.db", paths.ResolveDatabaseFile(@"D:\Data\lib.db"));
    }

    [Fact]
    public void CleanTempRemovesLeftoverJobFolders()
    {
        using TempDirectory temp = new("clean");
        AppPaths paths = temp.CreateAppPaths();
        paths.EnsureCreated();
        string job = paths.JobTempDirectory(1, 2);
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "stats.log"), "x");

        int removed = paths.CleanRunTemp();

        Assert.Equal(1, removed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TempDirectory));
    }
}
