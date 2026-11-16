using MediaVerifier.Core;

namespace MediaVerifier.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void VersionComesFromDirectoryBuildProps()
    {
        Assert.Equal(4, AppInfo.VersionText.Split('.').Length);
        Assert.StartsWith("MediaVerifier ", AppInfo.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void AppAndCoreShareOneVersion()
    {
        Version? appVersion = typeof(MediaVerifier.App.MainForm).Assembly.GetName().Version;
        Assert.Equal(AppInfo.Version, appVersion);
    }
}
