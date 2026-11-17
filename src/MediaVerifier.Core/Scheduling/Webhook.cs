using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Scheduling;

/// <summary>New findings of a run by tier.</summary>
public sealed record WebhookTiers(int Critical, int High, int Medium, int Low);

/// <summary>
/// The JSON a webhook receives after a scheduled run (subplan 13, Optional webhook; task N-06; documented in
/// docs/webhook.md). Counts only: no file names, folder names or paths.
/// </summary>
public sealed record WebhookPayload
{
    public string Event { get; init; } = "run.finished";

    public string App { get; init; } = AppInfo.ProductName;

    public string Version { get; init; } = AppInfo.VersionText;

    public long RunId { get; init; }

    public long? PlanId { get; init; }

    /// <summary><c>scheduled</c>, <c>manual</c> or <c>resume</c>.</summary>
    public string Trigger { get; init; } = string.Empty;

    /// <summary><c>completed</c>, <c>stopped</c>, <c>failed</c> and the other run states, in lower case.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>When the run ended, ISO 8601 UTC.</summary>
    public string FinishedUtc { get; init; } = string.Empty;

    public double DurationSeconds { get; init; }

    public long FilesSeen { get; init; }

    public long FilesAnalysed { get; init; }

    public long FilesVerified { get; init; }

    public long Errors { get; init; }

    public WebhookTiers NewFindings { get; init; } = new(0, 0, 0, 0);

    /// <summary>Open findings first raised in this run by media kind: <c>photo</c>, <c>video</c>, <c>audio</c>, <c>other</c>.</summary>
    public IReadOnlyDictionary<string, long> NewFindingsByKind { get; init; } = new Dictionary<string, long>();

    /// <summary>Run-level alert ids such as <c>RUN-READ-ERRORS</c>.</summary>
    public IReadOnlyList<string> Alerts { get; init; } = [];

    /// <summary>The payload of a finished run; the kind counts come from the findings it raised.</summary>
    public static WebhookPayload From(RunSummary run, IReadOnlyDictionary<string, long> byKind, DateTimeOffset finished)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new WebhookPayload
        {
            RunId = run.RunId,
            PlanId = run.PlanId,
            Trigger = run.Trigger.ToString().ToLowerInvariant(),
            Status = run.Status.ToString().ToLowerInvariant(),
            FinishedUtc = finished.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            DurationSeconds = Math.Round(run.Elapsed.TotalSeconds, 1),
            FilesSeen = run.Counters.FilesSeen,
            FilesAnalysed = run.Counters.FilesAnalysed,
            FilesVerified = run.Counters.FilesVerified,
            Errors = run.Counters.Errors,
            NewFindings = new WebhookTiers(run.NewFindings.Critical, run.NewFindings.High, run.NewFindings.Medium, run.NewFindings.Low),
            NewFindingsByKind = byKind,
            Alerts = [.. run.Alerts.Select(a => a.Id)],
        };
    }

    /// <summary>The test payload of the Send test button: run id 0, no counts.</summary>
    public static WebhookPayload Test(DateTimeOffset now) => new()
    {
        Event = "test",
        Trigger = "manual",
        Status = "completed",
        FinishedUtc = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    };

    /// <summary>Open findings first raised in <paramref name="runId"/>, by media kind.</summary>
    public static Dictionary<string, long> CountByKind(Database database, long runId)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT media_kind, COUNT(*) FROM findings WHERE first_run = $run AND status = $open GROUP BY media_kind";
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$open", (int)FindingStatus.Open);
            using SqliteDataReader reader = command.ExecuteReader();
            Dictionary<string, long> counts = new(StringComparer.Ordinal);
            while (reader.Read())
            {
                MediaKind kind = (MediaKind)reader.GetInt32(0);
                string name = kind is MediaKind.Photo or MediaKind.Video or MediaKind.Audio ? kind.ToString().ToLowerInvariant() : "other";
                counts[name] = counts.GetValueOrDefault(name) + reader.GetInt64(1);
            }

            return counts;
        });
    }

    public string ToJson() => JsonSerializer.Serialize(this, WebhookJsonContext.Default.WebhookPayload);
}

/// <summary>Whether an address may receive the summary.</summary>
public enum WebhookAddressVerdict
{
    /// <summary>An absolute HTTPS address.</summary>
    Ok,

    /// <summary>Plain HTTP to a loopback or private-range address: allowed after the user confirms.</summary>
    NeedsConfirmation,

    /// <summary>Anything else, including plain HTTP to a public address.</summary>
    Invalid,
}

/// <summary>The rules for webhook addresses (subplan 13, Optional webhook).</summary>
public static class WebhookAddress
{
    public static WebhookAddressVerdict Check(string? text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed) || parsed.Host.Length == 0)
        {
            return WebhookAddressVerdict.Invalid;
        }

        if (parsed.Scheme == Uri.UriSchemeHttps)
        {
            uri = parsed;
            return WebhookAddressVerdict.Ok;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && IsPrivate(parsed))
        {
            uri = parsed;
            return WebhookAddressVerdict.NeedsConfirmation;
        }

        return WebhookAddressVerdict.Invalid;
    }

    /// <summary>
    /// Loopback (<c>localhost</c>, 127.0.0.0/8, ::1) or a private range given as an IP literal (10/8, 172.16/12,
    /// 192.168/16, 169.254/16, fc00::/7, fe80::/10). Host names other than localhost are not resolved.
    /// </summary>
    public static bool IsPrivate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.IsLoopback)
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out IPAddress? address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 127;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || IPAddress.IsLoopback(address);
        }

        return false;
    }
}

/// <summary>
/// Posts the summary: 10-second timeout, two retries after 2 and 4 seconds, no redirects. Failures are logged and
/// reported as false; they never throw into the run.
/// </summary>
public sealed class WebhookSender(HttpMessageHandler? handler = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public const int Retries = 2;

    /// <summary>The wait before retry n is n times this (tests shorten it).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<bool> SendAsync(Uri address, WebhookPayload payload, Action<string> log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(log);
        using HttpClient client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = true }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.ProductName, AppInfo.VersionText));
        string json = payload.ToJson();
        for (int attempt = 0; attempt <= Retries; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using StringContent content = new(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await client.PostAsync(address, content, cancellationToken).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                log(string.Create(CultureInfo.InvariantCulture, $"Webhook attempt {attempt + 1} answered HTTP {status}"));
                if (status is >= 300 and < 500 and not 408 and not 429)
                {
                    // Redirects and client errors will not change on a retry.
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                log(string.Create(CultureInfo.InvariantCulture, $"Webhook attempt {attempt + 1} failed: {ex.Message}"));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                log(string.Create(CultureInfo.InvariantCulture, $"Webhook attempt {attempt + 1} timed out"));
            }
        }

        return false;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebhookPayload))]
internal sealed partial class WebhookJsonContext : JsonSerializerContext;
