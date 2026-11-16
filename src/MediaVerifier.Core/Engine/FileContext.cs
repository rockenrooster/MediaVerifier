using System.Security.Cryptography;
using System.Text;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Logging;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Engine;

/// <summary>The services one engine instance shares with every file it touches.</summary>
public sealed class EngineServices
{
    public required Database Database { get; init; }

    public required IMediaFileSystem FileSystem { get; init; }

    public required FfRunner Runner { get; init; }

    public required ProviderRegistry Providers { get; init; }

    public required DetectorRegistry Detectors { get; init; }

    public required IClock Clock { get; init; }

    public required AppPaths Paths { get; init; }

    public required IVolumeResolver Volumes { get; init; }

    public Func<ScanSettings> Settings { get; init; } = () => new ScanSettings();

    public Func<RuleSettings> RuleSettings { get; init; } = () => Rules.RuleSettings.Defaults;
}

/// <summary>Producers keyed by the artifact type they make.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<Type, IArtifactProvider> _byType = [];
    private readonly Dictionary<ArtifactKind, IArtifactProvider> _byKind = [];

    public ProviderRegistry(IEnumerable<IArtifactProvider> providers)
    {
        foreach (IArtifactProvider provider in providers)
        {
            _byType[provider.ArtifactType] = provider;
            _byKind[provider.Kind] = provider;
        }
    }

    public IEnumerable<IArtifactProvider> All => _byType.Values;

    public IArtifactProvider? For(Type artifactType) => _byType.GetValueOrDefault(artifactType);

    public IArtifactProvider? For(ArtifactKind kind) => _byKind.GetValueOrDefault(kind);
}

/// <summary>The detectors, and a number that changes whenever any rule's logic or threshold set changes.</summary>
public sealed class DetectorRegistry
{
    public DetectorRegistry(IEnumerable<IDetector> detectors, ProviderRegistry? providers = null)
    {
        Providers = providers;
        Detectors = [.. detectors];
        StringBuilder signature = new();
        foreach (RuleDefinition rule in RuleCatalog.All)
        {
            signature.Append(rule.Id).Append(':').Append(rule.Version).Append(';');
        }

        foreach (IDetector detector in Detectors)
        {
            signature.Append(detector.GetType().FullName).Append(';');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()));
        DetectorSet = BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }

    public IReadOnlyList<IDetector> Detectors { get; }

    /// <summary>Providers to run in full when a file is analysed (all applicable ones).</summary>
    public ProviderRegistry? Providers { get; }

    /// <summary>Stored per file; a file evaluated with an older set is re-evaluated without I/O.</summary>
    public int DetectorSet { get; }

    /// <summary>Media kinds a detector's rules apply to.</summary>
    public static MediaKindMask KindsFor(IDetector detector)
    {
        MediaKindMask mask = MediaKindMask.None;
        foreach (string ruleId in detector.RuleIds)
        {
            mask |= RuleCatalog.Get(ruleId).AppliesTo;
        }

        return mask;
    }
}

/// <summary>
/// One file during one run (subplan 05, File context and artifacts). Artifacts are produced at most once
/// per file per run; in re-evaluation mode only stored artifacts are used and no provider runs.
/// </summary>
public sealed class FileContext : IDisposable
{
    private readonly EngineServices _services;
    private readonly Dictionary<Type, Task<IArtifact?>> _tasks = [];
    private readonly Dictionary<Type, IArtifact> _available = [];
    private readonly List<(IArtifactProvider Provider, IArtifact Artifact)> _produced = [];
    private readonly Dictionary<ArtifactKind, (int Version, string Data)> _stored;
    private readonly Lock _gate = new();
    private string? _temp;

    public FileContext(
        EngineServices services,
        RootRecord root,
        string relDir,
        string name,
        MediaKind kind,
        FileStat stat,
        FileRecord? previous,
        ScanDepth depth,
        long runId,
        bool reevaluateOnly,
        IReadOnlyDictionary<ArtifactKind, (int ProviderVersion, string Data)>? stored,
        CancellationToken cancellationToken)
    {
        _services = services;
        Root = root;
        RelDir = relDir;
        Name = name;
        Extension = MediaClassifier.Extension(name);
        Kind = kind;
        Stat = stat;
        Previous = previous;
        Depth = depth;
        RunId = runId;
        ReevaluateOnly = reevaluateOnly;
        Cancellation = cancellationToken;
        _stored = stored?.ToDictionary(p => p.Key, p => (p.Value.ProviderVersion, p.Value.Data)) ?? [];
        FullPath = relDir.Length == 0 ? Path.Combine(root.Path, name) : Path.Combine(root.Path, relDir, name);
    }

    public RootRecord Root { get; }

    public string RelDir { get; }

    public string Name { get; }

    public string Extension { get; }

    public string FullPath { get; }

    public MediaKind Kind { get; set; }

    public FileStat Stat { get; }

    public FileRecord? Previous { get; }

    public ScanDepth Depth { get; }

    public long RunId { get; }

    public bool ReevaluateOnly { get; }

    public CancellationToken Cancellation { get; }

    public IMediaFileSystem FileSystem => _services.FileSystem;

    public FfRunner Runner => _services.Runner;

    public IClock Clock => _services.Clock;

    public ScanSettings Settings => _services.Settings();

    /// <summary>Per-file scratch folder for FFmpeg filters; created on first use, deleted on dispose.</summary>
    public string TempDirectory
    {
        get
        {
            if (_temp is null)
            {
                _temp = _services.Paths.JobTempDirectory(RunId, Previous?.Id ?? Random.Shared.NextInt64(1, long.MaxValue));
                Directory.CreateDirectory(_temp);
            }

            return _temp;
        }
    }

    /// <summary>Artifacts available now (stored or produced).</summary>
    public IReadOnlyDictionary<Type, IArtifact> Available
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<Type, IArtifact>(_available);
            }
        }
    }

    /// <summary>Artifacts produced in this run, to be stored.</summary>
    public IReadOnlyList<(IArtifactProvider Provider, IArtifact Artifact)> Produced
    {
        get
        {
            lock (_gate)
            {
                return [.. _produced];
            }
        }
    }

    /// <summary>Puts an artifact made outside a provider (the integrity read) into the context.</summary>
    public void Put<T>(T artifact, IArtifactProvider? persistWith = null)
        where T : class, IArtifact
    {
        lock (_gate)
        {
            _available[typeof(T)] = artifact;
            _tasks[typeof(T)] = Task.FromResult<IArtifact?>(artifact);
            if (persistWith is not null)
            {
                _produced.Add((persistWith, artifact));
            }
        }
    }

    public async Task<T?> GetAsync<T>()
        where T : class, IArtifact => (T?)await GetAsync(typeof(T)).ConfigureAwait(false);

    /// <summary>
    /// The artifact of <paramref name="type"/>: a stored one when its provider version is current and the
    /// depth covers it, otherwise produced once (never in re-evaluation mode).
    /// </summary>
    public Task<IArtifact?> GetAsync(Type type)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(type, out Task<IArtifact?>? task))
            {
                task = ResolveAsync(type);
                _tasks[type] = task;
            }

            return task;
        }
    }

    public void Dispose()
    {
        if (_temp is not null)
        {
            try
            {
                Directory.Delete(_temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Shared?.Warning($"Could not remove scratch folder {_temp}", ex);
            }
        }
    }

    private async Task<IArtifact?> ResolveAsync(Type type)
    {
        IArtifactProvider? provider = _services.Providers.For(type);
        if (provider is null)
        {
            return null;
        }

        if (_stored.TryGetValue(provider.Kind, out (int Version, string Data) stored) && (ReevaluateOnly || stored.Version == provider.Version))
        {
            IArtifact? loaded = provider.Deserialize(stored.Data);
            if (loaded is not null)
            {
                Remember(type, loaded, null);
                return loaded;
            }
        }

        if (ReevaluateOnly || provider.MinDepth > Depth || !provider.Applies(this))
        {
            return null;
        }

        IArtifact produced = await provider.ProduceAsync(this).ConfigureAwait(false);
        Remember(type, produced, provider);
        return produced;
    }

    private void Remember(Type type, IArtifact artifact, IArtifactProvider? producedBy)
    {
        lock (_gate)
        {
            _available[type] = artifact;
            if (producedBy is not null)
            {
                _produced.Add((producedBy, artifact));
            }
        }
    }
}

/// <summary>The outcome of evaluating one file: findings after the overlay, and which rules were judged.</summary>
public sealed record Evaluation(IReadOnlyList<Finding> Findings, IReadOnlySet<string> EvaluatedRules);

/// <summary>Runs the detectors for a file (task E-08): produce what they need once, then evaluate purely.</summary>
public static class Evaluator
{
    public static async Task<Evaluation> EvaluateAsync(
        FileContext file,
        DetectorRegistry registry,
        RuleSettings settings,
        IReadOnlyCollection<string>? onlyRules = null,
        IReadOnlyCollection<string>? extraEvaluated = null,
        IEnumerable<Finding>? extraFindings = null)
    {
        List<IDetector> candidates = [];
        foreach (IDetector detector in registry.Detectors)
        {
            if (detector.MinDepth > file.Depth || !DetectorRegistry.KindsFor(detector).Includes(file.Kind))
            {
                continue;
            }

            if (file.Root.Role == RootRole.Backup && !detector.RuleIds.All(IsIntegrityRule))
            {
                // Backup roots are fingerprinted only: no analysis, no quality findings (task I-10).
                continue;
            }

            if (onlyRules is not null && !detector.RuleIds.Any(onlyRules.Contains))
            {
                continue;
            }

            candidates.Add(detector);
        }

        // When analysing, every applicable provider runs, not only those today's detectors need: the stored
        // artifacts are what later re-evaluation (a rule switched on, a limit changed) works from.
        IEnumerable<Type> wanted = candidates.SelectMany(d => d.Needs.Concat(d.Optional));
        if (!file.ReevaluateOnly && onlyRules is null)
        {
            wanted = wanted.Concat(registry.Providers?.All.Where(p => p.MinDepth <= file.Depth).Select(p => p.ArtifactType) ?? []);
        }

        Type[] needs = [.. wanted.Distinct()];
        await Task.WhenAll(needs.Select(file.GetAsync)).ConfigureAwait(false);

        DetectorInput input = new(file.FullPath, file.Extension, file.Kind, file.Depth, file.Root.Role, file.Previous, file.Available, settings);
        FindingSink sink = new();
        HashSet<string> evaluated = new(StringComparer.Ordinal);
        foreach (IDetector detector in candidates)
        {
            if (!detector.Needs.All(input.Has) || !detector.Applies(input))
            {
                continue;
            }

            detector.Evaluate(input, sink);
            foreach (string ruleId in detector.RuleIds)
            {
                RuleDefinition rule = RuleCatalog.Get(ruleId);
                if (rule.AppliesTo.Includes(file.Kind) && (rule.OnVerification || rule.MinDepth <= file.Depth) && (onlyRules is null || onlyRules.Contains(ruleId)))
                {
                    evaluated.Add(ruleId);
                }
            }
        }

        if (extraEvaluated is not null)
        {
            evaluated.UnionWith(extraEvaluated);
        }

        List<Finding> findings = [];
        foreach (Finding raw in Merge(sink.Findings.Concat(extraFindings ?? []), file.Kind))
        {
            if (onlyRules is not null && !onlyRules.Contains(raw.RuleId))
            {
                continue;
            }

            Finding? applied = settings.Apply(raw);
            if (applied is not null)
            {
                findings.Add(applied);
            }
        }

        return new Evaluation(findings, evaluated);
    }

    /// <summary>
    /// One finding per rule (a finding is unique per file and rule, subplan 02): when two detectors raise
    /// the same rule, the more severe wins, and on a tie the later one (detectors are registered from the
    /// shallowest depth to the deepest, so it carries the deeper evidence). The other finding's evidence
    /// is kept under the winner's. Rules that do not apply to the file's kind are dropped. Small zero runs
    /// (GEN-ZEROFILL at Low) are attached as evidence to the file's corruption findings.
    /// </summary>
    internal static IReadOnlyList<Finding> Merge(IEnumerable<Finding> raw, MediaKind kind)
    {
        List<Finding> merged = [];
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        foreach (Finding finding in raw)
        {
            if (kind != MediaKind.Unknown && !RuleCatalog.Get(finding.RuleId).AppliesTo.Includes(kind))
            {
                continue;
            }

            if (index.TryGetValue(finding.RuleId, out int at))
            {
                Finding kept = merged[at];
                (Finding winner, Finding other) = finding.Severity >= kept.Severity ? (finding, kept) : (kept, finding);
                Dictionary<string, object> evidence = new(other.Evidence);
                foreach ((string key, object value) in winner.Evidence)
                {
                    evidence[key] = value;
                }

                merged[at] = winner with { Evidence = evidence };
                continue;
            }

            index[finding.RuleId] = merged.Count;
            merged.Add(finding);
        }

        if (merged.FirstOrDefault(f => f.RuleId == "GEN-ZEROFILL" && f.Severity == Severity.Low) is Finding zeros
            && zeros.Evidence.TryGetValue("runOffsetsBytes", out object? offsets))
        {
            for (int i = 0; i < merged.Count; i++)
            {
                Finding finding = merged[i];
                if (finding.RuleId.StartsWith("COR-", StringComparison.Ordinal) && RuleCatalog.Get(finding.RuleId).Category == FindingCategory.Corruption)
                {
                    Dictionary<string, object> evidence = new(finding.Evidence)
                    {
                        ["zeroRunOffsetsBytes"] = offsets,
                    };
                    if (zeros.Evidence.TryGetValue("longestRunBytes", out object? longest))
                    {
                        evidence["zeroLongestRunBytes"] = longest;
                    }

                    merged[i] = finding with { Evidence = evidence };
                }
            }
        }

        return merged;
    }

    public static bool IsIntegrityRule(string ruleId) => ruleId is "GEN-UNREADABLE" or "GEN-EMPTY" or "GEN-ZEROFILL" or "INT-ROT" or "INT-META-DRIFT";
}
