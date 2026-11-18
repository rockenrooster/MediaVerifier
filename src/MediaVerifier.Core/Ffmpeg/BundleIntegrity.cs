using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaVerifier.Core.Ffmpeg;

public enum BundleState : byte
{
    /// <summary>bundle.json present and every listed file has the listed SHA-256.</summary>
    Ok = 0,

    /// <summary>Every file present, at least one hash differs: scans run, Tools shows a Medium info bar.</summary>
    Changed = 1,

    /// <summary>bundle.json or a listed file missing, or <c>ffmpeg -version</c> fails: scanning is disabled.</summary>
    Missing = 2,

    /// <summary>The developer override is in use; its integrity is not checked.</summary>
    Override = 3,
}

/// <summary>The bundle's description (subplan 17, bundle.json).</summary>
public sealed class BundleInfo
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("ffmpegVersion")]
    public string? FfmpegVersion { get; set; }

    [JsonPropertyName("extraVersion")]
    public string? ExtraVersion { get; set; }

    [JsonPropertyName("licence")]
    public string? Licence { get; set; }





    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

}

public sealed record BundleCheck(BundleState State, BundleInfo? Info, IReadOnlyList<string> ChangedFiles, IReadOnlyList<string> MissingFiles, string? Detail)
{
    public bool ScanningAllowed => State != BundleState.Missing;
}

/// <summary>
/// Checks the bundled FFmpeg against its bundle.json (task FF-02). Runs on a background thread after the
/// window shows and in <c>--self-check</c>; a scan waits for the result.
/// </summary>
public static class BundleIntegrity
{
    public static BundleCheck Check(FfmpegLocation location)
    {
        if (location.IsOverride)
        {
            return new BundleCheck(location.ExecutablesExist ? BundleState.Override : BundleState.Missing, null, [], [], location.ExecutablesExist ? null : "ffmpeg.exe or ffprobe.exe missing in the override folder");
        }

        if (!File.Exists(location.BundleManifest))
        {
            return new BundleCheck(BundleState.Missing, null, [], ["bundle.json"], "bundle.json not found");
        }

        BundleInfo? info;
        try
        {
            info = JsonSerializer.Deserialize(File.ReadAllBytes(location.BundleManifest), BundleJsonContext.Default.BundleInfo);
        }
        catch (JsonException ex)
        {
            return new BundleCheck(BundleState.Missing, null, [], [], "bundle.json unreadable: " + ex.Message);
        }

        if (info is null || info.Files.Count == 0)
        {
            return new BundleCheck(BundleState.Missing, info, [], [], "bundle.json lists no files");
        }

        List<string> missing = [];
        List<string> changed = [];

        // Hashing ~50 MB is quick, but it must not compete with the UI.
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            foreach ((string name, string expected) in info.Files)
            {
                string path = Path.Combine(location.Directory, name);
                if (name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name) || !File.Exists(path))
                {
                    missing.Add(name);
                    continue;
                }

                if (!string.Equals(Sha256Hex(path), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    changed.Add(name);
                }
            }
        }
        finally
        {
            Thread.CurrentThread.Priority = ThreadPriority.Normal;
        }

        if (!location.ExecutablesExist)
        {
            missing.Add(File.Exists(location.FfmpegExe) ? "ffprobe.exe" : "ffmpeg.exe");
        }

        BundleState state = missing.Count > 0 ? BundleState.Missing : changed.Count > 0 ? BundleState.Changed : BundleState.Ok;
        return new BundleCheck(state, info, changed, missing, null);
    }

    public static string Sha256Hex(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// The startup check of task FF-04: <c>ffmpeg -version</c> runs and names the expected version, and
    /// <c>ffmpeg -protocols</c> lists only <c>file</c> and <c>pipe</c>. Returns null when both hold.
    /// </summary>
    public static async Task<string?> VerifyEngineAsync(FfRunner runner, string expectedVersion, CancellationToken cancellationToken = default)
    {
        FfResult version = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = ["-version"], Stdout = StdoutMode.Text, HardTimeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
        string firstLine = version.StdoutText?.Split('\n', 2)[0].Trim() ?? "";
        if (!version.Completed || version.ExitCode != 0 || firstLine.Length == 0)
        {
            return "ffmpeg -version failed" + (version.StartError is null ? "" : ": " + version.StartError);
        }

        if (!firstLine.Contains("version " + expectedVersion, StringComparison.Ordinal))
        {
            return $"Expected FFmpeg {expectedVersion}, found: {firstLine}";
        }

        FfResult protocols = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = ["-protocols"], Stdout = StdoutMode.Text, HardTimeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<string> input, IReadOnlyList<string> output) = ParseProtocols(protocols.StdoutText ?? "");
        string[] allowed = ["file", "pipe"];
        if (!input.Order(StringComparer.Ordinal).SequenceEqual(allowed) || !output.Order(StringComparer.Ordinal).SequenceEqual(allowed))
        {
            return $"FFmpeg protocols must be only file and pipe; input: {string.Join(' ', input)}; output: {string.Join(' ', output)}";
        }

        return null;
    }

    /// <summary>Parses <c>ffmpeg -protocols</c>: an "Input:" list then an "Output:" list.</summary>
    public static (IReadOnlyList<string> Input, IReadOnlyList<string> Output) ParseProtocols(string text)
    {
        List<string> input = [];
        List<string> output = [];
        List<string>? current = null;
        foreach (string raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.StartsWith("Input:", StringComparison.Ordinal))
            {
                current = input;
            }
            else if (line.StartsWith("Output:", StringComparison.Ordinal))
            {
                current = output;
            }
            else if (current is not null && line.Length > 0 && !line.Contains(':', StringComparison.Ordinal))
            {
                current.Add(line);
            }
        }

        return (input, output);
    }
}

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(BundleInfo))]
internal sealed partial class BundleJsonContext : JsonSerializerContext;
