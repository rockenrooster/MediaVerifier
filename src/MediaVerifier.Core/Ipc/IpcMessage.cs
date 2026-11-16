using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaVerifier.Core.Ipc;

/// <summary>
/// One command sent to the running GUI over the named pipe, as a single line of UTF-8 JSON:
/// <c>{"cmd":"activate"}</c>, <c>{"cmd":"run-plan","planId":3}</c>,
/// <c>{"cmd":"open","uri":"mediaverifier://finding/123"}</c>.
/// </summary>
public sealed record IpcMessage
{
    public const string Activate = "activate";
    public const string RunPlan = "run-plan";
    public const string Open = "open";
    public const int MaxUriLength = 2048;

    [JsonPropertyName("cmd")]
    public string Command { get; init; } = "";

    [JsonPropertyName("planId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PlanId { get; init; }

    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; init; }

    public static IpcMessage ForActivate() => new() { Command = Activate };

    public static IpcMessage ForRunPlan(long planId) => new() { Command = RunPlan, PlanId = planId };

    public static IpcMessage ForOpen(string uri) => new() { Command = Open, Uri = uri };

    /// <summary>True when the command is known and carries exactly the fields it needs.</summary>
    public bool IsValid() => Command switch
    {
        Activate => PlanId is null && Uri is null,
        RunPlan => PlanId is > 0 && Uri is null,
        Open => PlanId is null
            && Uri is { Length: > 0 and <= MaxUriLength }
            && Uri.StartsWith(ActivationUriScheme.Prefix, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public string ToJsonLine() => JsonSerializer.Serialize(this, IpcJsonContext.Default.IpcMessage) + "\n";

    /// <summary>Parses one message; returns null for malformed JSON or an invalid command.</summary>
    public static IpcMessage? TryParse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            IpcMessage? message = JsonSerializer.Deserialize(utf8, IpcJsonContext.Default.IpcMessage);
            return message is not null && message.IsValid() ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The activation URI scheme registered for toasts (subplan 13).</summary>
public static class ActivationUriScheme
{
    public const string Scheme = "mediaverifier";
    public const string Prefix = Scheme + "://";

    public static bool IsActivationUri(string? text) =>
        text is { Length: > 0 and <= IpcMessage.MaxUriLength }
        && text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(IpcMessage))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
