using System.Globalization;
using System.Text.Json.Serialization;
using MediaVerifier.Core;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>A library folder, a database and an engine wired with chosen providers and detectors.</summary>
public sealed class EngineHarness : IDisposable
{
    private readonly FfRunner _runner;

    public EngineHarness(IEnumerable<IArtifactProvider>? providers = null, IEnumerable<IDetector>? detectors = null, ScanSettings? settings = null, IMediaFileSystem? fileSystem = null)
    {
        Db = new TestDatabase("engine");
        Library = Db.Folder.Combine("Library");
        Backup = Db.Folder.Combine("Backup");
        Directory.CreateDirectory(Library);
        Directory.CreateDirectory(Backup);
        Settings = settings ?? new ScanSettings();
        Rules = new RuleSettings();
        _runner = new FfRunner(new FfmpegLocation(Db.Folder.Path, IsOverride: true));
        Providers = new ProviderRegistry([new IntegrityArtifactStore(), .. providers ?? []]);
        Detectors = new DetectorRegistry([new IntegrityDetector(), .. detectors ?? []], Providers);
        Services = new EngineServices
        {
            Database = Db.Database,
            FileSystem = fileSystem ?? WindowsFileSystem.Instance,
            Runner = _runner,
            Providers = Providers,
            Detectors = Detectors,
            Clock = Db.Clock,
            Paths = AppPaths.UnderBase(Db.Folder.Combine("profile")),
            Volumes = new FakeVolumes(),
            Settings = () => Settings,
            RuleSettings = () => Rules,
        };
        Engine = new ScanEngine(Services, @"Local\MediaVerifier.Tests.Engine." + Guid.NewGuid().ToString("N"));
    }

    public TestDatabase Db { get; }

    public Database Database => Db.Database;

    public string Library { get; }

    public string Backup { get; }

    public ScanSettings Settings { get; }

    public RuleSettings Rules { get; set; }

    public ProviderRegistry Providers { get; }

    public DetectorRegistry Detectors { get; }

    public EngineServices Services { get; }

    public ScanEngine Engine { get; }

    public string Write(string relativePath, byte[] content, DateTime? modifiedUtc = null)
    {
        string path = Path.Combine(Library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        return path;
    }

    public static byte[] Bytes(int size, int seed)
    {
        byte[] data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    public async Task<long> AddRootAsync(string path, RootRole role = RootRole.Library) =>
        await Database.Roots.AddAsync(new RootRecord { Path = path, Role = role, AddedUtc = Db.Clock.UtcNow });

    /// <summary>Runs a scan to completion and returns its summary.</summary>
    public async Task<RunSummary> ScanAsync(ScanDepth depth = ScanDepth.Standard, ScanOptions? options = null, TimeSpan? budget = null, RunTrigger trigger = RunTrigger.Manual, params long[] roots)
    {
        long[] scope = roots.Length > 0 ? roots : [.. Database.Roots.List().Select(r => r.Id)];
        (int code, RunSummary? summary) = await HeadlessScan.RunAsync(Engine, new ScanRequest([.. scope.Select(r => new ScanScope(r))], depth, trigger, Budget: budget, Options: options));
        Assert.NotNull(summary);
        _ = code;
        await Database.Writer.FlushAsync();
        return summary!;
    }

    public List<FileRecord> AllFiles() => Database.Files.GetMany(Database.Queries.FileIds(new FileFilter()));

    public FileRecord File1(string name) => AllFiles().Single(f => f.Name == name);

    public void Dispose()
    {
        Engine.Dispose();
        _runner.Dispose();
        Db.Dispose();
    }

    private sealed class FakeVolumes : IVolumeResolver
    {
        public VolumeInfo Resolve(string path) => new("test-volume", StorageClass.Ssd);
    }
}

/// <summary>A compact test artifact.</summary>
public sealed record FakeArtifact(int Value, string Path) : IArtifact, IMediaSummarySource
{
    [JsonIgnore]
    public MediaSummary? Summary => new("fake", null, "fakecodec", null, null, 1000, 128_000, 44_100, 2, 16, null);

    [JsonIgnore]
    public MediaKind? ProbedKind => null;
}

public sealed record FakeStreamHash(byte[] Hash) : IArtifact, IStreamHashSource
{
    [JsonIgnore]
    public byte[]? StreamHash => Hash;
}

[JsonSerializable(typeof(FakeArtifact))]
[JsonSerializable(typeof(FakeStreamHash))]
internal sealed partial class FakeJsonContext : JsonSerializerContext;

/// <summary>Counts calls; the artifact's value is the file size, so detectors can depend on it.</summary>
public sealed class CountingProvider(ScanDepth minDepth = ScanDepth.Quick, int version = 1, TimeSpan? delay = null, int kindNumber = 7)
    : ArtifactProvider<FakeArtifact>(new ArtifactCodec<FakeArtifact>((ArtifactKind)kindNumber, FakeJsonContext.Default.FakeArtifact))
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public override int Version => version;

    public override ScanDepth MinDepth => minDepth;

    public override bool Applies(FileContext file) => true;

    protected override async Task<FakeArtifact> ProduceTypedAsync(FileContext file)
    {
        Interlocked.Increment(ref _calls);
        if (delay is TimeSpan wait)
        {
            await Task.Delay(wait, file.Cancellation);
        }

        return new FakeArtifact((int)Math.Min(int.MaxValue, file.Stat.Size), file.FullPath);
    }
}

/// <summary>Produces a fixed stream hash for audio and video, so tag-only changes look like metadata drift.</summary>
public sealed class FakeStreamHashProvider(byte[] hash)
    : ArtifactProvider<FakeStreamHash>(new ArtifactCodec<FakeStreamHash>(ArtifactKind.StreamHash, FakeJsonContext.Default.FakeStreamHash))
{
    public byte[] Hash { get; set; } = hash;

    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Standard;

    public override bool Applies(FileContext file) => file.Kind is MediaKind.Audio or MediaKind.Video;

    protected override Task<FakeStreamHash> ProduceTypedAsync(FileContext file) => Task.FromResult(new FakeStreamHash(Hash));
}

/// <summary>Raises a rule when the fake artifact's value is at least a threshold read from the rule settings.</summary>
public sealed class ThresholdDetector(string ruleId, string? parameter = null, int fixedThreshold = 1000) : IDetector
{
    public IReadOnlyList<string> RuleIds { get; } = [ruleId];

    public IReadOnlyList<Type> Needs { get; } = [typeof(FakeArtifact)];

    public ScanDepth MinDepth => RuleCatalog.Get(ruleId).MinDepth;

    public bool Applies(DetectorInput input) => true;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        FakeArtifact artifact = input.Get<FakeArtifact>()!;
        double threshold = parameter is null ? fixedThreshold : input.Settings.Value(ruleId, parameter);
        if (artifact.Value >= threshold)
        {
            RuleDefinition rule = RuleCatalog.Get(ruleId);
            sink.Raise(ruleId, artifact.Value >= threshold * 2 ? rule.MaxSeverity : rule.MinSeverity, 1f, string.Create(CultureInfo.InvariantCulture, $"value {artifact.Value}"), new Dictionary<string, object> { ["value"] = artifact.Value });
        }
    }
}
