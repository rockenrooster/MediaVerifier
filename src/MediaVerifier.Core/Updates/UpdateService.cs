using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaVerifier.Core.Updates;

public sealed class UpdateService : IDisposable
{
    private const string RepositoryOwner = "rockenrooster";
    private const string RepositoryName = "MediaVerifier";
    private const string SetupAssetName = "MediaVerifier-Setup.exe";
    private const string ManifestAssetName = "MediaVerifier-Setup.exe.manifest.json";
    private const string ManifestSignatureAssetName = "MediaVerifier-Setup.exe.manifest.sig";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _updatesDirectory;
    private readonly string _publicKeyPem;
    private readonly Lock _lock = new();
    private Task<UpdateCheckResult>? _checkTask;
    private bool _isChecking;
    private bool _isDownloading;
    private double _downloadProgress;

    public UpdateService()
        : this(null, AppPaths.Default.UpdatesDirectory, null)
    {
    }

    /// <summary>
    /// Tests: a local HTTP handler, a temporary update folder, a chosen current version and a test public key
    /// (tasks U-04, U-08). The app always uses <see cref="UpdateTrust.PublicKeyPem"/>.
    /// </summary>
    internal UpdateService(HttpMessageHandler? handler, string updatesDirectory, Version? currentVersion, string? publicKeyPem = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _updatesDirectory = updatesDirectory;
        _publicKeyPem = publicKeyPem ?? UpdateTrust.PublicKeyPem;
        Version rawVersion = currentVersion ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        CurrentVersion = Normalize(rawVersion);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"MediaVerifier/{CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public event EventHandler? StatusChanged;

    public Version CurrentVersion { get; }
    public UpdateCheckResult? LastResult { get; private set; }

    /// <summary>No bytes for this long ends a download (the HTTP timeout stops covering it once the headers arrive).</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public bool IsChecking
    {
        get
        {
            lock (_lock)
            {
                return _isChecking;
            }
        }
    }

    public bool IsDownloading
    {
        get
        {
            lock (_lock)
            {
                return _isDownloading;
            }
        }
    }

    public double DownloadProgress
    {
        get
        {
            lock (_lock)
            {
                return _downloadProgress;
            }
        }
    }

    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_checkTask is { IsCompleted: false })
            {
                return _checkTask;
            }

            _checkTask = CheckCoreAsync(cancellationToken);
            return _checkTask;
        }
    }

    public async Task<string> DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult result = LastResult ?? throw new InvalidOperationException("Check for updates before downloading.");
        if (!result.CanDownload || result.DownloadUrl is null)
        {
            throw new InvalidOperationException("No downloadable update is available.");
        }

        return await DownloadSetupAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadInstallerAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult result = LastResult ?? await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (!result.CanInstall || result.DownloadUrl is null)
        {
            throw new InvalidOperationException("No signed installer is available.");
        }

        return await DownloadSetupAsync(result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the status line of a failed download or install on the update card until the next check (the toast alone
    /// was gone while the card went back to "Version {v} is available.").
    /// </summary>
    public void RecordFailure(string statusLine)
    {
        if (LastResult is UpdateCheckResult result)
        {
            LastResult = result with { Failure = statusLine };
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task<string> DownloadSetupAsync(UpdateCheckResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.AssetSha256) || result.AssetSizeBytes <= 0)
        {
            throw new InvalidOperationException("Update metadata is incomplete.");
        }

        SetDownloading(true, 0);
        string updateDir = _updatesDirectory;
        string targetPath = Path.Combine(updateDir, "MediaVerifier-Setup.exe");
        string tempPath = targetPath + ".download";

        // Re-armed with every chunk: a connection that stops sending ends the download instead of hanging it.
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        try
        {
            Directory.CreateDirectory(updateDir);
            using HttpResponseMessage response = await _http.GetAsync(result.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            if (total is > 0 && total.Value != result.AssetSizeBytes)
            {
                throw new InvalidOperationException("Downloaded update size does not match the signed manifest.");
            }

            string hash;
            {
                await using Stream input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using FileStream output = File.Create(tempPath);
                using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                byte[] buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    stall.CancelAfter(StallTimeout);
                    await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    sha256.AppendData(buffer, 0, read);
                    received += read;
                    if (total is > 0)
                    {
                        SetDownloadProgress(received / (double)total.Value * 100);
                    }
                }

                if (received != result.AssetSizeBytes)
                {
                    throw new InvalidOperationException("Downloaded update size does not match the signed manifest.");
                }

                hash = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            }

            if (!hash.Equals(result.AssetSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Downloaded update hash does not match the signed manifest.");
            }

            File.Move(tempPath, targetPath, overwrite: true);
            SetDownloading(false, 100);
            return targetPath;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(tempPath);
            SetDownloading(false, 0);
            throw new IOException("The connection stopped sending data.", ex);
        }
        catch
        {
            TryDelete(tempPath);
            SetDownloading(false, 0);
            throw;
        }
    }

    /// <summary>
    /// Starts the downloaded installer silently; it closes MediaVerifier and opens it again afterwards. Tests replace it
    /// so that nothing is installed.
    /// </summary>
    internal Action<string> StartInstaller { get; set; } = static path => Process.Start(new ProcessStartInfo(path)
    {
        Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
        UseShellExecute = true,
    })?.Dispose();

    public void InstallDownloadedUpdate(string setupPath)
    {
        if (!File.Exists(setupPath))
        {
            throw new FileNotFoundException("Downloaded update file was not found.", setupPath);
        }

        UpdateCheckResult result = LastResult ?? throw new InvalidOperationException("Check for updates before installing.");
        ValidateDownloadedFile(setupPath, result);
        StartInstaller(setupPath);
    }

    public void Dispose() => _http.Dispose();

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        SetChecking(true);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return StoreResult(UpdateCheckResult.NotFound(CurrentVersion));
            }

            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            GitHubRelease? release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return StoreResult(UpdateCheckResult.Error(CurrentVersion, "GitHub returned an empty release response."));
            }

            string versionText = release.TagName.Trim();
            if (!TryParseVersion(versionText, out Version? latestVersion))
            {
                return StoreResult(UpdateCheckResult.Error(CurrentVersion, $"Latest release tag {versionText} is not a version."));
            }

            GitHubAsset? asset = SelectAsset(release.Assets, SetupAssetName);
            GitHubAsset? manifestAsset = SelectAsset(release.Assets, ManifestAssetName);
            GitHubAsset? signatureAsset = SelectAsset(release.Assets, ManifestSignatureAssetName);
            Version normalizedLatest = Normalize(latestVersion!);
            bool updateAvailable = normalizedLatest.CompareTo(CurrentVersion) > 0;
            bool canDownload = false;
            bool canInstall = false;
            string? assetSha256 = null;
            Uri? downloadUrl = asset is null ? null : new Uri(asset.BrowserDownloadUrl);
            long assetSize = asset?.Size ?? 0;
            string message = "MediaVerifier is up to date.";

            if (asset is not null && manifestAsset is not null && signatureAsset is not null)
            {
                try
                {
                    byte[] manifestBytes = await DownloadBytesAsync(new Uri(manifestAsset.BrowserDownloadUrl), cancellationToken).ConfigureAwait(false);
                    byte[] signatureBytes = await DownloadBytesAsync(new Uri(signatureAsset.BrowserDownloadUrl), cancellationToken).ConfigureAwait(false);
                    if (!UpdateTrust.VerifyManifestSignature(manifestBytes, signatureBytes, _publicKeyPem))
                    {
                        throw new InvalidOperationException("manifest signature is invalid");
                    }

                    UpdateManifest manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions)
                        ?? throw new InvalidOperationException("manifest is empty");
                    ValidateManifest(manifest, versionText, normalizedLatest, asset);
                    assetSha256 = NormalizeSha256(manifest.Sha256);
                    downloadUrl = new Uri(manifest.DownloadUrl);
                    assetSize = manifest.SizeBytes;
                    canInstall = true;
                    canDownload = updateAvailable;
                    message = updateAvailable ? "Update available." : message;
                }
                catch (Exception ex)
                {
                    if (updateAvailable)
                    {
                        message = $"Update found, but manifest validation failed: {ex.Message}.";
                    }
                }
            }
            else if (updateAvailable)
            {
                message = "Update found, but signed update assets are missing.";
            }

            return StoreResult(new UpdateCheckResult(
                CurrentVersion,
                normalizedLatest,
                versionText,
                updateAvailable,
                canDownload,
                canInstall,
                release.HtmlUrl,
                asset?.Name,
                canInstall ? downloadUrl : null,
                assetSize,
                assetSha256,
                message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StoreResult(UpdateCheckResult.Error(CurrentVersion, $"Update check failed: {ex.Message}"));
        }
        finally
        {
            SetChecking(false);
        }
    }

    private UpdateCheckResult StoreResult(UpdateCheckResult result)
    {
        LastResult = result;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void SetChecking(bool value)
    {
        lock (_lock)
        {
            _isChecking = value;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDownloading(bool value, double progress)
    {
        lock (_lock)
        {
            _isDownloading = value;
            _downloadProgress = progress;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDownloadProgress(double progress)
    {
        lock (_lock)
        {
            if (Math.Abs(_downloadProgress - progress) < 1)
            {
                return;
            }

            _downloadProgress = progress;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<byte[]> DownloadBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GitHubAsset? SelectAsset(IReadOnlyList<GitHubAsset>? assets, string name)
    {
        if (assets is null || assets.Count == 0)
        {
            return null;
        }

        return assets.FirstOrDefault(asset => asset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    internal static void ValidateManifest(UpdateManifest manifest, string releaseTag, Version releaseVersion, GitHubAsset setupAsset)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidOperationException("manifest schema version is unsupported");
        }

        if (!releaseTag.Equals(manifest.Tag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("manifest tag does not match release tag");
        }

        if (!TryParseVersion(manifest.Version, out Version? manifestVersion) || Normalize(manifestVersion!).CompareTo(releaseVersion) != 0)
        {
            throw new InvalidOperationException("manifest version does not match release version");
        }

        if (!SetupAssetName.Equals(manifest.AssetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("manifest asset name is invalid");
        }

        if (manifest.SizeBytes <= 0 || manifest.SizeBytes != setupAsset.Size)
        {
            throw new InvalidOperationException("manifest size does not match release asset");
        }

        _ = NormalizeSha256(manifest.Sha256);

        if (!DateTimeOffset.TryParse(manifest.CreatedUtc, out _))
        {
            throw new InvalidOperationException("manifest createdUtc is invalid");
        }

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? uri)
            || !uri.AbsoluteUri.Equals(setupAsset.BrowserDownloadUrl, StringComparison.Ordinal)
            || !IsExpectedGithubDownloadUri(uri, releaseTag))
        {
            throw new InvalidOperationException("manifest download URL is invalid");
        }
    }

    internal static bool IsExpectedGithubDownloadUri(Uri uri, string releaseTag)
    {
        string expectedPath = $"/{RepositoryOwner}/{RepositoryName}/releases/download/{releaseTag}/{SetupAssetName}";
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal);
    }

    internal static string NormalizeSha256(string value)
    {
        string hash = value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidOperationException("manifest SHA-256 is invalid");
        }

        return hash;
    }

    private static void ValidateDownloadedFile(string path, UpdateCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(result.AssetSha256) || result.AssetSizeBytes <= 0)
        {
            throw new InvalidOperationException("Update metadata is incomplete.");
        }

        FileInfo file = new(path);
        if (file.Length != result.AssetSizeBytes)
        {
            throw new InvalidOperationException("Downloaded update size does not match the signed manifest.");
        }

        string hash = UpdateTrust.ComputeSha256Hex(path);
        if (!hash.Equals(result.AssetSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Downloaded update hash does not match the signed manifest.");
        }
    }

    internal static bool TryParseVersion(string tag, out Version? version)
    {
        string text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        if (Version.TryParse(text, out Version? parsed))
        {
            version = parsed;
            return true;
        }

        version = null;
        return false;
    }

    private static Version Normalize(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version? LatestVersion,
    string LatestVersionText,
    bool IsUpdateAvailable,
    bool CanDownload,
    bool CanInstall,
    string? ReleaseUrl,
    string? AssetName,
    Uri? DownloadUrl,
    long AssetSizeBytes,
    string? AssetSha256,
    string Message)
{
    /// <summary>The status line of a download or install that failed after this check, shown until the next check.</summary>
    public string? Failure { get; init; }

    public static UpdateCheckResult NotFound(Version currentVersion) =>
        Error(currentVersion, "No GitHub release was found.") with { LatestVersionText = "No release" };

    public static UpdateCheckResult Error(Version currentVersion, string message) => new(
        currentVersion,
        null,
        "Unknown",
        false,
        false,
        false,
        null,
        null,
        null,
        0,
        null,
        message);
}

public sealed record UpdateManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("assetName")] string AssetName,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("createdUtc")] string CreatedUtc);

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

public sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size);

public static class UpdateTrust
{
    /// <summary>
    /// The update-signing public key (task U-01): RSA 3072, generated by the owner with
    /// scripts/New-UpdateSigningKey.ps1 on 2026-11-17. Only manifests signed with its private half (held as the
    /// release workflow's secret MEDIAVERIFIER_UPDATE_SIGNING_PRIVATE_KEY_PEM) verify; anything else fails closed.
    /// </summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA2+MwRpQ3aKHxz1sIlREb
        iYezYiqJ1pnV16QQleys0YNyt4Uxjb3FiVakPKCGrDUVCLBa0LqXMXQ4SE5QeHD8
        +f+1U6FvfLunYEqPwpRNe0Q7Te/BSZEsy/P/qnDdFIY/u+uFOyfwdxKhYhYtDarD
        bhPG71Z3Pdd88ljpBO8wdj56gbtlLw+5YedJgAlDISGMQq6h6s7JHcTfmMEZh53H
        2ZiIbZmTeA2IKzpRUoyUJhmfUS/KZk4VtYTS50ZzkTroXKPRN8a51VVvVVAE74xD
        Pl2hOHk0n1/NhsNGn3MJmC6lvqE57C9zKKFhdP5EPzHLJR86QZJ3e482yeRo1SXf
        kkwUzhVuxthVC7r/GDXhLsz7M8119m1E8kf6v2kSpx+XbBeLP5M86Df/6Dn0i4eP
        ybh2mtv6I1ohtpoEODjKU0EpPCZEHoT4P2jNvdYucuseGbeZxCCjIVGhVfFaIt/P
        n6rnJ+2DXZart3Lrm7WOx8AMqRQMgBQyf7ZSOVEjvD6VAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    public static bool VerifyManifestSignature(byte[] manifestBytes, byte[] signatureBytes, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        using RSA rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return rsa.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public static string ComputeSha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
