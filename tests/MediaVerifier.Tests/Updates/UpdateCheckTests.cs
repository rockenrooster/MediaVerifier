using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaVerifier.App;
using MediaVerifier.Core.Updates;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Updates;

/// <summary>
/// The whole check against a fake GitHub (subplan 14; tasks U-03, U-08): a valid signed release enables the download,
/// and each kind of tampering leaves nothing installable with the card saying why.
/// </summary>
public sealed class UpdateCheckTests
{
    private const string Tag = "v0.1.0.2";
    private const string SetupUrl = "https://github.com/rockenrooster/MediaVerifier/releases/download/v0.1.0.2/MediaVerifier-Setup.exe";
    private const long SetupSize = 4_500_000;
    private static readonly string Sha = new('a', 64);

    public static TheoryData<string> Tamperings => ["tampered", "wrong-key", "wrong-tag", "wrong-version", "wrong-asset", "wrong-size", "bad-hash", "http", "foreign-host", "wrong-path", "no-signature"];

    [Fact]
    public async Task A_valid_signed_release_can_be_downloaded()
    {
        using RSA key = RSA.Create(2048);
        using UpdateService updates = Service(Release(key, _ => { }), key, new Version(0, 1, 0, 1));
        UpdateCheckResult result = await updates.CheckAsync();
        Assert.True(result.IsUpdateAvailable);
        Assert.True(result.CanInstall);
        Assert.True(result.CanDownload);
        Assert.Equal(new Uri(SetupUrl), result.DownloadUrl);
        Assert.Equal(SetupSize, result.AssetSizeBytes);
        Assert.Equal(Sha, result.AssetSha256);
        Assert.Equal("Version 0.1.0.2 is available.", UpdateStatusText.For(false, false, 0, result));
    }

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task Tampered_metadata_enables_nothing(string tampering)
    {
        using RSA key = RSA.Create(2048);
        using RSA other = RSA.Create(2048);
        Dictionary<string, (HttpStatusCode, byte[])> responses = Release(tampering == "wrong-key" ? other : key, manifest =>
        {
            switch (tampering)
            {
                case "wrong-tag": manifest["tag"] = "v0.1.0.3"; break;
                case "wrong-version": manifest["version"] = "0.1.0.3"; break;
                case "wrong-asset": manifest["assetName"] = "Other-Setup.exe"; break;
                case "wrong-size": manifest["sizeBytes"] = SetupSize + 1; break;
                case "bad-hash": manifest["sha256"] = "not-a-hash"; break;
                case "http": manifest["downloadUrl"] = SetupUrl.Replace("https://", "http://", StringComparison.Ordinal); break;
                case "foreign-host": manifest["downloadUrl"] = SetupUrl.Replace("github.com", "github.example.com", StringComparison.Ordinal); break;
                case "wrong-path": manifest["downloadUrl"] = SetupUrl.Replace("/MediaVerifier/releases", "/Other/releases", StringComparison.Ordinal); break;
            }
        });
        if (tampering == "tampered")
        {
            byte[] manifest = responses[ManifestUrl].Item2;
            responses[ManifestUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("4500000", "4500001", StringComparison.Ordinal)));
        }
        else if (tampering == "no-signature")
        {
            responses.Remove(SignatureUrl);
        }

        using UpdateService updates = Service(responses, key, new Version(0, 1, 0, 1));
        UpdateCheckResult result = await updates.CheckAsync();
        Assert.True(result.IsUpdateAvailable);
        Assert.False(result.CanInstall);
        Assert.False(result.CanDownload);
        Assert.Null(result.DownloadUrl);
        Assert.Equal("Update found, but it could not be verified. Nothing was installed.", UpdateStatusText.For(false, false, 0, result));
    }

    [Fact]
    public async Task The_current_release_can_install_a_portable_copy_but_not_download()
    {
        using RSA key = RSA.Create(2048);
        using UpdateService updates = Service(Release(key, _ => { }), key, new Version(0, 1, 0, 2));
        UpdateCheckResult result = await updates.CheckAsync();
        Assert.False(result.IsUpdateAvailable);
        Assert.True(result.CanInstall);
        Assert.False(result.CanDownload);
        Assert.Equal("MediaVerifier is up to date.", UpdateStatusText.For(false, false, 0, result));
    }

    [Fact]
    public async Task No_release_and_no_network_say_so()
    {
        using RSA key = RSA.Create(2048);
        using (UpdateService none = Service([], key, new Version(0, 1, 0, 1)))
        {
            UpdateCheckResult result = await none.CheckAsync();
            Assert.Equal("Could not check for updates. No GitHub release was found.", UpdateStatusText.For(false, false, 0, result));
        }

        using UpdateService offline = new(new FakeGitHub([]) { Offline = true }, Path.GetTempPath(), new Version(0, 1, 0, 1), key.ExportSubjectPublicKeyInfoPem());
        UpdateCheckResult failed = await offline.CheckAsync();
        Assert.False(failed.CanInstall);
        Assert.StartsWith("Could not check for updates. No such host", UpdateStatusText.For(false, false, 0, failed), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_card_state_has_its_line()
    {
        Assert.Equal("Update status has not been checked.", UpdateStatusText.For(false, false, 0, null));
        Assert.Equal("Checking…", UpdateStatusText.For(true, false, 0, null));
        Assert.Equal("Downloading 42%", UpdateStatusText.For(false, true, 42.7, null));
    }

    [Theory]
    [InlineData(@"C:\Users\A\AppData\Local\Programs\MediaVerifier\MediaVerifier.exe", @"C:\Users\A\AppData\Local\Programs\MediaVerifier\", true)]
    [InlineData(@"C:\Program Files\MediaVerifier\MediaVerifier.exe", @"c:\program files\mediaverifier", true)]
    [InlineData(@"D:\Tools\MediaVerifier\MediaVerifier.exe", @"C:\Program Files\MediaVerifier\", false)]
    [InlineData(@"D:\Tools\MediaVerifier\MediaVerifier.exe", null, false)]
    public void Installed_means_the_exe_folder_is_the_install_location(string exe, string? location, bool installed) =>
        Assert.Equal(installed, InstallDetection.IsInstalledCopy(exe, [null, location]));

    private const string ApiUrl = "https://api.github.com/repos/rockenrooster/MediaVerifier/releases/latest";
    private const string ManifestUrl = "https://github.com/rockenrooster/MediaVerifier/releases/download/v0.1.0.2/MediaVerifier-Setup.exe.manifest.json";
    private const string SignatureUrl = "https://github.com/rockenrooster/MediaVerifier/releases/download/v0.1.0.2/MediaVerifier-Setup.exe.manifest.sig";

    internal static UpdateService Service(Dictionary<string, (HttpStatusCode, byte[])> responses, RSA key, Version current) =>
        new(new FakeGitHub(responses), Path.GetTempPath(), current, key.ExportSubjectPublicKeyInfoPem());

    /// <summary>The release JSON, the manifest (changed by <paramref name="change"/>) and its signature by <paramref name="signer"/>.</summary>
    internal static Dictionary<string, (HttpStatusCode, byte[])> Release(RSA signer, Action<Dictionary<string, object>> change)
    {
        Dictionary<string, object> manifest = new()
        {
            ["schemaVersion"] = 1,
            ["version"] = "0.1.0.2",
            ["tag"] = Tag,
            ["assetName"] = "MediaVerifier-Setup.exe",
            ["downloadUrl"] = SetupUrl,
            ["sha256"] = Sha,
            ["sizeBytes"] = SetupSize,
            ["createdUtc"] = "2026-11-17T03:00:00Z",
        };
        change(manifest);
        string downloadUrl = (string)manifest["downloadUrl"];
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var release = new
        {
            tag_name = Tag,
            html_url = "https://github.com/rockenrooster/MediaVerifier/releases/tag/v0.1.0.2",
            assets = new object[]
            {
                new { name = "MediaVerifier-Setup.exe", browser_download_url = downloadUrl, size = SetupSize },
                new { name = "MediaVerifier-Setup.exe.manifest.json", browser_download_url = ManifestUrl, size = manifestBytes.Length },
                new { name = "MediaVerifier-Setup.exe.manifest.sig", browser_download_url = SignatureUrl, size = signature.Length },
            },
        };
        return new Dictionary<string, (HttpStatusCode, byte[])>(StringComparer.Ordinal)
        {
            [ApiUrl] = (HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(release)),
            [ManifestUrl] = (HttpStatusCode.OK, manifestBytes),
            [SignatureUrl] = (HttpStatusCode.OK, signature),
        };
    }

    /// <summary>Answers from a table; anything else is 404. Offline throws like a missing network.</summary>
    internal sealed class FakeGitHub(Dictionary<string, (HttpStatusCode Status, byte[] Body)> responses) : HttpMessageHandler
    {
        public bool Offline { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("No such host is known. (api.github.com:443)");
            }

            string url = request.RequestUri!.AbsoluteUri;
            HttpResponseMessage response = responses.TryGetValue(url, out (HttpStatusCode Status, byte[] Body) answer)
                ? new HttpResponseMessage(answer.Status) { Content = new ByteArrayContent(answer.Body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
            return Task.FromResult(response);
        }
    }
}
