using MediaVerifier.Core.Updates;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Updates;

/// <summary>
/// The published release, checked by the app's own updater with the key built into the app: the latest GitHub
/// release, its signed manifest, and the installer's size and SHA-256 after downloading it (subplan 14). It downloads
/// the installer, so it runs only with <c>MEDIAVERIFIER_LIVE_RELEASE=1</c>, after each release.
/// </summary>
public sealed class LiveReleaseTests
{
    [LiveReleaseFact]
    public async Task The_latest_release_verifies_with_the_key_built_into_the_app()
    {
        using TempDirectory temp = new("live-release");
        using UpdateService updates = new(null, temp.Combine("Updates"), new Version(0, 1, 0, 0));
        UpdateCheckResult result = await updates.CheckAsync();
        Assert.True(result.CanDownload, result.Message);

        string setup = await updates.DownloadLatestAsync();
        Assert.Equal(result.AssetSizeBytes, new FileInfo(setup).Length);
    }
}

/// <summary>A test that reads the published release on GitHub: it runs only with MEDIAVERIFIER_LIVE_RELEASE=1.</summary>
public sealed class LiveReleaseFactAttribute : FactAttribute
{
    public LiveReleaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_LIVE_RELEASE") != "1")
        {
            Skip = "Reads the published release on GitHub: set MEDIAVERIFIER_LIVE_RELEASE=1.";
        }
    }
}
