using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Engine;

/// <summary>The production set of providers and detectors, and the services that wire them to the engine.</summary>
public static class EngineFactory
{
    /// <summary>Every artifact provider, in no particular order (the file context resolves dependencies).</summary>
    public static IReadOnlyList<IArtifactProvider> CreateProviders() =>
    [
        new IntegrityArtifactStore(),
        new StructureProvider(),
        new ProbeArtifactProvider(),
        new StreamHashProvider(),
        new AudioDecodeProvider(),
        new ImageDecodeProvider(),
        new VideoDecodeProvider(),
    ];

    /// <summary>
    /// Every detector. Order matters only for ties: when two detectors raise the same rule at the same
    /// severity, the first one's finding is kept.
    /// </summary>
    public static IReadOnlyList<IDetector> CreateDetectors() =>
    [
        new IntegrityDetector(),
        new PathDetector(),
        new StructureDetector(),
        new StreamLengthDetector(),
        new AudioDecodeDetector(),
        new ImageDecodeDetector(),
        new VideoDecodeDetector(),
    ];

    public static EngineServices CreateServices(Database database, FfRunner runner, AppPaths paths, Func<ScanSettings> settings, Func<RuleSettings> ruleSettings, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ProviderRegistry providers = new(CreateProviders());
        return new EngineServices
        {
            Database = database,
            FileSystem = WindowsFileSystem.Instance,
            Runner = runner,
            Providers = providers,
            Detectors = new DetectorRegistry(CreateDetectors(), providers),
            Clock = clock ?? SystemClock.Instance,
            Paths = paths,
            Volumes = new WindowsVolumeResolver(),
            Settings = settings,
            RuleSettings = ruleSettings,
        };
    }
}
