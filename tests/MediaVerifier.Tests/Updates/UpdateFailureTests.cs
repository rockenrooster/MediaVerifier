using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MediaVerifier.App;
using MediaVerifier.Core.Updates;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;

namespace MediaVerifier.Tests.Updates;

/// <summary>
/// Failures after a successful check (subplan 14, task U-08): each says which step failed, stays on the update card
/// until the next check, and a connection that stops sending ends the download instead of hanging it.
/// </summary>
public sealed class UpdateFailureTests
{
    [Fact]
    public void Each_failure_names_its_step()
    {
        Assert.Equal(Strings.Settings.InstallCancelled, UpdateInstaller.FailureText(new Win32Exception(1223), scanStopped: false));
        Assert.Equal(Strings.Settings.InstallCancelled + " " + Strings.Settings.ScanStopped, UpdateInstaller.FailureText(new Win32Exception(1223), scanStopped: true));
        Assert.StartsWith("Could not start the installer. ", UpdateInstaller.FailureText(new Win32Exception(2), scanStopped: false), StringComparison.Ordinal);
        Assert.Equal("Could not download the update. Response status code does not indicate success: 404 (Not Found).", UpdateInstaller.FailureText(new HttpRequestException("Response status code does not indicate success: 404 (Not Found)."), scanStopped: false));
        Assert.Equal("Could not download the update. There is not enough space on the disk.", UpdateInstaller.FailureText(new IOException("There is not enough space on the disk."), scanStopped: false));
        Assert.Equal(Strings.Settings.NotVerified, UpdateInstaller.FailureText(new InvalidOperationException("Downloaded update hash does not match the signed manifest."), scanStopped: false));
        Assert.DoesNotContain("Could not check for updates", UpdateInstaller.FailureText(new HttpRequestException("No such host is known."), scanStopped: false), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_stays_on_the_card_until_the_next_check()
    {
        UpdateCheckResult available = new(new Version(0, 1, 0, 1), new Version(0, 1, 0, 2), "v0.1.0.2", true, true, true, null, "MediaVerifier-Setup.exe", new Uri("https://github.com/"), 10, new string('a', 64), "Update available.");
        Assert.Equal("Version 0.1.0.2 is available.", UpdateStatusText.For(false, false, 0, available));
        Assert.Equal("Could not download the update. Timed out.", UpdateStatusText.For(false, false, 0, available with { Failure = "Could not download the update. Timed out." }));
        Assert.Equal("Downloading 5%", UpdateStatusText.For(false, true, 5, available with { Failure = "Could not download the update. Timed out." }));
    }

    [Fact]
    public async Task Recording_a_failure_keeps_the_offer_and_tells_the_card()
    {
        using RSA key = RSA.Create(3072);
        using UpdateService updates = UpdateCheckTests.Service(UpdateCheckTests.Release(key, _ => { }), key, new Version(0, 1, 0, 1));
        UpdateCheckResult checkedResult = await updates.CheckAsync();
        Assert.True(checkedResult.CanDownload);
        int changes = 0;
        updates.StatusChanged += (_, _) => changes++;

        updates.RecordFailure("Could not download the update. Timed out.");
        Assert.Equal("Could not download the update. Timed out.", updates.LastResult!.Failure);
        Assert.True(updates.LastResult.CanDownload);
        Assert.Equal(1, changes);

        // The next check starts over.
        Assert.Null((await updates.CheckAsync()).Failure);
    }

    [Fact]
    public async Task A_connection_that_stops_sending_ends_the_download_and_leaves_nothing()
    {
        using TempDirectory temp = new("update-stall");
        byte[] setup = new byte[200_000];
        new Random(5).NextBytes(setup);
        using StallingServer server = new(setup);
        using UpdateService updates = new(null, temp.Path, new Version(0, 1, 0, 1)) { StallTimeout = TimeSpan.FromSeconds(1) };
        UpdateCheckResult result = new(new Version(0, 1, 0, 1), new Version(0, 1, 0, 2), "v0.1.0.2", true, true, true, null, "MediaVerifier-Setup.exe", server.Address, setup.Length, Convert.ToHexStringLower(SHA256.HashData(setup)), "Update available.");

        Task<string> download = updates.DownloadSetupAsync(result, CancellationToken.None);
        Task finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(download, finished);
        IOException error = await Assert.ThrowsAsync<IOException>(() => download);
        Assert.Contains("stopped sending", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temp.Path));
        Assert.False(updates.IsDownloading);
    }

    /// <summary>Sends the headers and half the file, then nothing until it is disposed.</summary>
    private sealed class StallingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public StallingServer(byte[] body)
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
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body.AsMemory(0, body.Length / 2));
                    await context.Response.OutputStream.FlushAsync();
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or IOException)
                {
                    // Disposed: the test is over.
                }
            });
        }

        public Uri Address { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }
    }
}
