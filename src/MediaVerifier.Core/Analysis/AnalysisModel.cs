using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis;

/// <summary>
/// A cached analysis result for one file (probe, structure, decode, PCM statistics, frame samples).
/// Artifacts are compact (numbers and a few sample lines) because they are stored, so that rules can be
/// re-evaluated later without touching the media again (subplan 05).
/// </summary>
public interface IArtifact
{
}

/// <summary>How an artifact type is stored in the artifacts table.</summary>
public sealed class ArtifactCodec<T>(ArtifactKind kind, JsonTypeInfo<T> typeInfo)
    where T : class, IArtifact
{
    public ArtifactKind Kind { get; } = kind;

    public string Serialize(T artifact) => JsonSerializer.Serialize(artifact, typeInfo);

    public T? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Produces one artifact type (subplan 05). Providers own all I/O and process execution; a provider never
/// throws for bad media: process failures, timeouts and unsupported formats are data in the artifact.
/// </summary>
public interface IArtifactProvider
{
    Type ArtifactType { get; }

    ArtifactKind Kind { get; }

    /// <summary>Bumped when the provider's output changes; stored artifacts from older versions are re-produced.</summary>
    int Version { get; }

    ScanDepth MinDepth { get; }

    bool Applies(FileContext file);

    Task<IArtifact> ProduceAsync(FileContext file);

    string Serialize(IArtifact artifact);

    IArtifact? Deserialize(string json);
}

/// <summary>Typed base so providers only implement the interesting part.</summary>
public abstract class ArtifactProvider<T> : IArtifactProvider
    where T : class, IArtifact
{
    protected ArtifactProvider(ArtifactCodec<T> codec)
    {
        Codec = codec;
    }

    public ArtifactCodec<T> Codec { get; }

    public Type ArtifactType => typeof(T);

    public ArtifactKind Kind => Codec.Kind;

    public abstract int Version { get; }

    public abstract ScanDepth MinDepth { get; }

    public abstract bool Applies(FileContext file);

    public async Task<IArtifact> ProduceAsync(FileContext file) => await ProduceTypedAsync(file).ConfigureAwait(false);

    public string Serialize(IArtifact artifact) => Codec.Serialize((T)artifact);

    public IArtifact? Deserialize(string json) => Codec.Deserialize(json);

    protected abstract Task<T> ProduceTypedAsync(FileContext file);
}

/// <summary>
/// Turns artifacts into findings (subplan 05). Pure: the same artifacts always give the same findings.
/// Detectors read thresholds from <see cref="DetectorInput.Settings"/>, never from literals.
/// </summary>
public interface IDetector
{
    IReadOnlyList<string> RuleIds { get; }

    /// <summary>The artifact types the detector reads; the engine produces them first.</summary>
    IReadOnlyList<Type> Needs { get; }

    ScanDepth MinDepth { get; }

    bool Applies(DetectorInput input);

    void Evaluate(DetectorInput input, FindingSink sink);
}

/// <summary>What a detector sees: the file's identity and the artifacts available for it.</summary>
public sealed class DetectorInput
{
    private readonly IReadOnlyDictionary<Type, IArtifact> _artifacts;

    public DetectorInput(string path, string extension, MediaKind kind, ScanDepth depth, RootRole role, FileRecord? previous, IReadOnlyDictionary<Type, IArtifact> artifacts, RuleSettings settings)
    {
        Path = path;
        Extension = extension;
        Kind = kind;
        Depth = depth;
        Role = role;
        Previous = previous;
        _artifacts = artifacts;
        Settings = settings;
    }

    public string Path { get; }

    public string Extension { get; }

    public MediaKind Kind { get; }

    public ScanDepth Depth { get; }

    public RootRole Role { get; }

    public FileRecord? Previous { get; }

    public RuleSettings Settings { get; }

    public T? Get<T>()
        where T : class, IArtifact => _artifacts.TryGetValue(typeof(T), out IArtifact? artifact) ? (T)artifact : null;

    public bool Has(Type type) => _artifacts.ContainsKey(type);
}

/// <summary>Collects raw findings; the engine applies the rule settings overlay afterwards.</summary>
public sealed class FindingSink
{
    private readonly List<Finding> _findings = [];

    public IReadOnlyList<Finding> Findings => _findings;

    public void Raise(string ruleId, Severity severity, float confidence, string summary, IReadOnlyDictionary<string, object>? evidence = null)
    {
        RuleDefinition rule = RuleCatalog.Get(ruleId);
        severity = SeverityPolicy.ClampToRule(severity, rule);
        _findings.Add(new Finding(ruleId, severity, Math.Clamp(confidence, 0f, 1f), summary, evidence ?? Finding.NoEvidence));
    }

    /// <summary>Attaches extra evidence to a finding already raised (for example zero runs to a decode finding).</summary>
    public bool Amend(string ruleId, Func<Finding, Finding> change)
    {
        int index = _findings.FindIndex(f => f.RuleId == ruleId);
        if (index < 0)
        {
            return false;
        }

        _findings[index] = change(_findings[index]);
        return true;
    }
}

/// <summary>An artifact that knows the file's media facts (the probe), so the engine can store them.</summary>
public interface IMediaSummarySource
{
    MediaSummary? Summary { get; }

    /// <summary>The kind the probe found when it differs from the extension's guess (null to keep it).</summary>
    MediaKind? ProbedKind { get; }
}

/// <summary>An artifact that carries the packet-level stream hash (subplan 06), 16 bytes.</summary>
public interface IStreamHashSource
{
    byte[]? StreamHash { get; }
}

/// <summary>An artifact that can say the file is not media FFmpeg can read (Skipped: UnsupportedByFfmpeg).</summary>
public interface IUnsupportedSource
{
    bool Unsupported { get; }
}
