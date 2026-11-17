using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MediaVerifier.Core.Updates;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Updates;

/// <summary>
/// The download path against a local HTTP listener (subplan 14, task U-04): the right file lands in the update folder;
/// a short stream, a long stream, a wrong hash and a wrong Content-Length each fail and leave no temporary file.
/// </summary>
public sealed class UpdateDownloadTests
{
    private static readonly byte[] Setup = MakeSetup();

    [Fact]
    public async Task The_right_file_lands_in_the_update_folder()
    {
        using TempDirectory temp = new("update");
        using SetupServer server = new((response, body) =>
        {
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body);
        });
        using UpdateService updates = new(null, temp.Path, new Version(0, 1, 0, 1));
        string path = await updates.DownloadSetupAsync(Result(server.Address, Setup.Length, Sha(Setup)), CancellationToken.None);
        Assert.Equal(Path.Combine(temp.Path, "MediaVerifier-Setup.exe"), path);
        Assert.Equal(Setup, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".download"));
        Assert.False(updates.IsDownloading);
        Assert.Equal(100, updates.DownloadProgress);
    }

    [Fact]
    public async Task A_short_stream_fails_and_leaves_nothing()
    {
        using TempDirectory temp = new("update");
        using SetupServer server = new((response, body) =>
        {
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length / 2);
            response.OutputStream.Flush();
            response.Abort();
        });
        await AssertFailsCleanly(temp, server, Setup.Length, Sha(Setup));
    }

    [Fact]
    public async Task A_long_stream_fails_and_leaves_nothing()
    {
        using TempDirectory temp = new("update");
        using SetupServer server = new((response, body) =>
        {
            response.SendChunked = true;
            response.OutputStream.Write(body);
            response.OutputStream.Write(new byte[4096]);
        });
        await AssertFailsCleanly(temp, server, Setup.Length, Sha(Setup));
    }

    [Fact]
    public async Task A_wrong_hash_fails_and_leaves_nothing()
    {
        using TempDirectory temp = new("update");
        byte[] other = (byte[])Setup.Clone();
        other[1000] ^= 0xFF;
        using SetupServer server = new((response, _) =>
        {
            response.ContentLength64 = other.Length;
            response.OutputStream.Write(other);
        });
        InvalidOperationException error = await AssertFailsCleanly<InvalidOperationException>(temp, server, Setup.Length, Sha(Setup));
        Assert.Contains("hash", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_content_length_fails_before_reading_and_leaves_nothing()
    {
        using TempDirectory temp = new("update");
        using SetupServer server = new((response, body) =>
        {
            response.ContentLength64 = body.Length + 1;
            response.OutputStream.Write(body);
            response.OutputStream.WriteByte(0);
        });
        InvalidOperationException error = await AssertFailsCleanly<InvalidOperationException>(temp, server, Setup.Length, Sha(Setup));
        Assert.Contains("size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Incomplete_metadata_downloads_nothing()
    {
        using TempDirectory temp = new("update");
        using SetupServer server = new((response, body) => response.OutputStream.Write(body));
        using UpdateService updates = new(null, temp.Path, new Version(0, 1, 0, 1));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => updates.DownloadSetupAsync(Result(server.Address, Setup.Length, null), CancellationToken.None));
        Assert.Equal(0, server.Requests);
    }

    private static Task AssertFailsCleanly(TempDirectory temp, SetupServer server, long size, string sha) => AssertFailsCleanly<Exception>(temp, server, size, sha);

    private static async Task<T> AssertFailsCleanly<T>(TempDirectory temp, SetupServer server, long size, string sha)
        where T : Exception
    {
        using UpdateService updates = new(null, temp.Path, new Version(0, 1, 0, 1));
        T error = await Assert.ThrowsAnyAsync<T>(() => updates.DownloadSetupAsync(Result(server.Address, size, sha), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(temp.Path));
        Assert.False(updates.IsDownloading);
        return error;
    }

    private static UpdateCheckResult Result(Uri address, long size, string? sha) => new(
        new Version(0, 1, 0, 1),
        new Version(0, 1, 0, 2),
        "v0.1.0.2",
        IsUpdateAvailable: true,
        CanDownload: true,
        CanInstall: true,
        "https://github.com/rockenrooster/MediaVerifier/releases/tag/v0.1.0.2",
        "MediaVerifier-Setup.exe",
        address,
        size,
        sha,
        "Update available.");

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] MakeSetup()
    {
        byte[] data = new byte[300_000];
        new Random(14).NextBytes(data);
        return data;
    }

    /// <summary>A loopback listener that answers every request with <c>respond</c>.</summary>
    private sealed class SetupServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private int _requests;

        public SetupServer(Action<HttpListenerResponse, byte[]> respond)
        {
            int port;
            using (TcpListener probe = new(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{port}/rockenrooster/MediaVerifier/releases/download/v0.1.0.2/MediaVerifier-Setup.exe");
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    Interlocked.Increment(ref _requests);
                    try
                    {
                        respond(context.Response, Setup);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or IOException)
                    {
                        // The response was aborted on purpose (short stream).
                    }
                }
            });
        }

        public Uri Address { get; }

        public int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }
    }
}
