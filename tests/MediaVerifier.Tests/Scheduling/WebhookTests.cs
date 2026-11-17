using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Scheduling;

/// <summary>The optional webhook (subplan 13; task N-06; docs/webhook.md).</summary>
public sealed class WebhookTests
{
    [Theory]
    [InlineData("https://example.com/hook", WebhookAddressVerdict.Ok)]
    [InlineData("https://203.0.113.5:8443/x?token=abc", WebhookAddressVerdict.Ok)]
    [InlineData("http://localhost:8080/hook", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://127.0.0.1/hook", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://10.1.2.3/hook", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://172.16.0.1/hook", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://172.31.255.255/hook", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://192.168.1.20:8123/api/webhook/x", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://169.254.10.10/", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://[::1]:9000/", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://[fd12:3456::1]/", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://[fe80::1]/", WebhookAddressVerdict.NeedsConfirmation)]
    [InlineData("http://172.32.0.1/hook", WebhookAddressVerdict.Invalid)]
    [InlineData("http://8.8.8.8/hook", WebhookAddressVerdict.Invalid)]
    [InlineData("http://nas.local/hook", WebhookAddressVerdict.Invalid)]
    [InlineData("http://example.com/hook", WebhookAddressVerdict.Invalid)]
    [InlineData("ftp://192.168.1.2/hook", WebhookAddressVerdict.Invalid)]
    [InlineData("file:///C:/hook.txt", WebhookAddressVerdict.Invalid)]
    [InlineData("example.com/hook", WebhookAddressVerdict.Invalid)]
    [InlineData("", WebhookAddressVerdict.Invalid)]
    [InlineData(null, WebhookAddressVerdict.Invalid)]
    public void Addresses_follow_the_plan(string? text, WebhookAddressVerdict expected) => Assert.Equal(expected, WebhookAddress.Check(text, out _));

    [Fact]
    public async Task A_local_listener_receives_the_documented_payload()
    {
        using TestListener listener = new([200]);
        RunSummary run = new(123, RunStatus.Completed, RunTrigger.Scheduled, 2, new RunCounters(12507, 412, 1860, 1L << 30, 3, 1, 0), new SeverityCounts(1, 2, 0, 0), [new RunAlert("RUN-ROOT-OFFLINE", Severity.Medium, 7, "Music is offline")], TimeSpan.FromSeconds(5532.4), null);
        WebhookPayload payload = WebhookPayload.From(run, new Dictionary<string, long> { ["audio"] = 2, ["video"] = 1 }, new DateTimeOffset(2026, 11, 17, 3, 12, 40, TimeSpan.Zero));
        List<string> log = [];
        Assert.True(await new WebhookSender().SendAsync(listener.Address, payload, log.Add));
        Assert.Empty(log);

        TestListener.Request request = Assert.Single(listener.Requests);
        Assert.Equal("POST", request.Method);
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
        Assert.StartsWith("MediaVerifier/", request.UserAgent, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(request.Body);
        JsonElement root = json.RootElement;
        Assert.Equal("run.finished", root.GetProperty("event").GetString());
        Assert.Equal(123, root.GetProperty("runId").GetInt64());
        Assert.Equal(2, root.GetProperty("planId").GetInt64());
        Assert.Equal("scheduled", root.GetProperty("trigger").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("2026-11-17T03:12:40Z", root.GetProperty("finishedUtc").GetString());
        Assert.Equal(5532.4, root.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(12507, root.GetProperty("filesSeen").GetInt64());
        Assert.Equal(1, root.GetProperty("newFindings").GetProperty("critical").GetInt32());
        Assert.Equal(2, root.GetProperty("newFindings").GetProperty("high").GetInt32());
        Assert.Equal(2, root.GetProperty("newFindingsByKind").GetProperty("audio").GetInt64());
        Assert.Equal("RUN-ROOT-OFFLINE", root.GetProperty("alerts")[0].GetString());

        // Counts only: the alert's text (which can name a folder) is not sent.
        Assert.DoesNotContain("Music", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_errors_are_retried_and_client_errors_are_not()
    {
        using (TestListener flaky = new([500, 503, 200]))
        {
            List<string> log = [];
            Assert.True(await new WebhookSender { RetryDelay = TimeSpan.FromMilliseconds(10) }.SendAsync(flaky.Address, WebhookPayload.Test(DateTimeOffset.UtcNow), log.Add));
            Assert.Equal(3, flaky.Requests.Count);
            Assert.Equal(2, log.Count);
        }

        using (TestListener rejecting = new([404, 200]))
        {
            List<string> log = [];
            Assert.False(await new WebhookSender { RetryDelay = TimeSpan.FromMilliseconds(10) }.SendAsync(rejecting.Address, WebhookPayload.Test(DateTimeOffset.UtcNow), log.Add));
            Assert.Single(rejecting.Requests);
            Assert.Contains("404", Assert.Single(log), StringComparison.Ordinal);
        }

        using (TestListener down = new([500, 500, 500, 200]))
        {
            Assert.False(await new WebhookSender { RetryDelay = TimeSpan.FromMilliseconds(10) }.SendAsync(down.Address, WebhookPayload.Test(DateTimeOffset.UtcNow), _ => { }));
            Assert.Equal(3, down.Requests.Count);
        }
    }

    [Fact]
    public async Task A_closed_port_fails_quietly_after_the_retries()
    {
        int port;
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        List<string> log = [];
        bool sent = await new WebhookSender { RetryDelay = TimeSpan.FromMilliseconds(10) }.SendAsync(new Uri($"http://127.0.0.1:{port}/hook"), WebhookPayload.Test(DateTimeOffset.UtcNow), log.Add);
        Assert.False(sent);
        Assert.Equal(1 + WebhookSender.Retries, log.Count);
    }

    [Fact]
    public async Task New_findings_are_counted_by_media_kind()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Media", Label = "Media" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, "a", "1.mp4", "mp4", MediaKind.Video, 1000, 1, null, 1),
            new(root, "a", "1.flac", "flac", MediaKind.Audio, 30, 1, null, 1),
            new(root, "a", "2.flac", "flac", MediaKind.Audio, 30, 1, null, 1),
            new(root, "a", "1.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
        ]);
        string[] rules = [.. RuleCatalog.All.Select(r => r.Id)];
        await db.Database.Findings.ReplaceForFileAsync(files[0].FileId, 5, rules, [new Finding("COR-INDEX", Severity.Medium, 1f, "no index", Finding.NoEvidence)], false);
        await db.Database.Findings.ReplaceForFileAsync(files[1].FileId, 5, rules, [new Finding("AUD-CLIPPING", Severity.High, 1f, "clipped", Finding.NoEvidence)], false);
        await db.Database.Findings.ReplaceForFileAsync(files[2].FileId, 5, rules, [new Finding("AUD-CLIPPING", Severity.High, 1f, "clipped", Finding.NoEvidence)], false);
        await db.Database.Findings.ReplaceForFileAsync(files[3].FileId, 4, rules, [new Finding("GEN-EXT-MISMATCH", Severity.Low, 1f, "png", Finding.NoEvidence)], false);

        Dictionary<string, long> counts = WebhookPayload.CountByKind(db.Database, 5);
        Assert.Equal(2, counts.Count);
        Assert.Equal(1, counts["video"]);
        Assert.Equal(2, counts["audio"]);
    }

    /// <summary>A loopback HTTP listener answering with the given status codes in turn.</summary>
    private sealed class TestListener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Queue<int> _answers;
        private readonly Task _loop;

        public TestListener(IEnumerable<int> answers)
        {
            _answers = new Queue<int>(answers);
            for (int attempt = 0; ; attempt++)
            {
                int port;
                using (TcpListener probe = new(IPAddress.Loopback, 0))
                {
                    probe.Start();
                    port = ((IPEndPoint)probe.LocalEndpoint).Port;
                }

                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    Address = new Uri($"http://127.0.0.1:{port}/hook");
                    break;
                }
                catch (HttpListenerException) when (attempt < 5)
                {
                }
            }

            _loop = Task.Run(ServeAsync);
        }

        public Uri Address { get; } = null!;

        public List<Request> Requests { get; } = [];

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

        private async Task ServeAsync()
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

                using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync().ConfigureAwait(false);
                lock (Requests)
                {
                    Requests.Add(new Request(context.Request.HttpMethod, context.Request.ContentType ?? string.Empty, context.Request.UserAgent ?? string.Empty, body));
                }

                context.Response.StatusCode = _answers.Count > 0 ? _answers.Dequeue() : 200;
                context.Response.Close();
            }
        }

        public sealed record Request(string Method, string ContentType, string UserAgent, string Body);
    }
}
