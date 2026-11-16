using System.Text.Json;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Engine;

/// <summary>Average seconds per file, per media kind and depth, and the read rate, for one storage class.</summary>
public sealed class EstimateAverages
{
    public Dictionary<string, double> SecondsPerFile { get; set; } = [];

    public double BytesPerSecond { get; set; }

    public static string Key(MediaKind kind, ScanDepth depth) => kind + "/" + depth;
}

/// <summary>
/// Remaining-time estimates (task E-11): remaining bytes ÷ smoothed read rate for integrity work, plus
/// remaining files per kind × an exponentially weighted average of analysis time, divided by the files
/// worked on in parallel. Says "estimating" until 50 files or 30 seconds of data exist, and never lets
/// an estimate move by more than 20 % per update.
/// </summary>
public sealed class Estimator
{
    private const double Alpha = 0.1;
    private const double MaxStep = 0.2;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, double> _seconds = [];
    private double _bytesPerSecond;
    private long _samples;
    private double? _lastEstimateSeconds;

    public Estimator(EstimateAverages? seed = null)
    {
        if (seed is not null)
        {
            foreach ((string key, double value) in seed.SecondsPerFile)
            {
                _seconds[key] = value;
            }

            _bytesPerSecond = seed.BytesPerSecond;
        }
    }

    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerSecond;
            }
        }
    }

    public void ObserveRead(long bytes, TimeSpan elapsed)
    {
        if (bytes <= 0 || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        double rate = bytes / elapsed.TotalSeconds;
        lock (_gate)
        {
            _bytesPerSecond = _bytesPerSecond <= 0 ? rate : ((1 - Alpha) * _bytesPerSecond) + (Alpha * rate);
        }
    }

    public void ObserveAnalysis(MediaKind kind, ScanDepth depth, TimeSpan elapsed)
    {
        string key = EstimateAverages.Key(kind, depth);
        lock (_gate)
        {
            _samples++;
            double seconds = elapsed.TotalSeconds;
            _seconds[key] = _seconds.TryGetValue(key, out double previous) ? ((1 - Alpha) * previous) + (Alpha * seconds) : seconds;
        }
    }

    public double SecondsPerFile(MediaKind kind, ScanDepth depth)
    {
        lock (_gate)
        {
            return _seconds.TryGetValue(EstimateAverages.Key(kind, depth), out double value) ? value : DefaultSeconds(kind, depth);
        }
    }

    /// <summary>
    /// The remaining time, or null while estimating. <paramref name="parallel"/> is how many files are
    /// worked on at once.
    /// </summary>
    public TimeSpan? Remaining(long remainingBytes, IReadOnlyDictionary<MediaKind, long> remainingFiles, ScanDepth depth, int parallel, TimeSpan elapsed, long filesDone)
    {
        if (filesDone < 50 && elapsed < TimeSpan.FromSeconds(30))
        {
            return null;
        }

        double seconds = RawSeconds(remainingBytes, remainingFiles, depth, parallel);
        lock (_gate)
        {
            if (_lastEstimateSeconds is double last && last > 0)
            {
                // Expected progress since the last update is folded in by the caller's cadence; clamp jumps.
                seconds = Math.Clamp(seconds, last * (1 - MaxStep), last * (1 + MaxStep));
            }

            _lastEstimateSeconds = seconds;
        }

        return TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    /// <summary>A one-off estimate without smoothing (the pre-scan estimate).</summary>
    public TimeSpan Estimate(long bytes, IReadOnlyDictionary<MediaKind, long> files, ScanDepth depth, int parallel) =>
        TimeSpan.FromSeconds(RawSeconds(bytes, files, depth, parallel));

    public EstimateAverages Snapshot()
    {
        lock (_gate)
        {
            return new EstimateAverages { SecondsPerFile = new Dictionary<string, double>(_seconds), BytesPerSecond = _bytesPerSecond };
        }
    }

    public static string Serialize(Dictionary<StorageClass, EstimateAverages> perClass) =>
        JsonSerializer.Serialize(perClass.ToDictionary(p => p.Key.ToString(), p => p.Value), EstimateJsonContext.Default.DictionaryStringEstimateAverages);

    public static Dictionary<StorageClass, EstimateAverages> Deserialize(string? json)
    {
        Dictionary<StorageClass, EstimateAverages> result = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            foreach ((string key, EstimateAverages value) in JsonSerializer.Deserialize(json, EstimateJsonContext.Default.DictionaryStringEstimateAverages) ?? [])
            {
                if (Enum.TryParse(key, out StorageClass storage))
                {
                    result[storage] = value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private double RawSeconds(long remainingBytes, IReadOnlyDictionary<MediaKind, long> remainingFiles, ScanDepth depth, int parallel)
    {
        double rate = BytesPerSecond > 0 ? BytesPerSecond : 100.0 * 1024 * 1024;
        double readSeconds = remainingBytes / rate;
        double analysisSeconds = 0;
        foreach ((MediaKind kind, long count) in remainingFiles)
        {
            analysisSeconds += count * SecondsPerFile(kind, depth);
        }

        return readSeconds + (analysisSeconds / Math.Max(1, parallel));
    }

    /// <summary>Starting values before any run has measured this machine (subplan 00, Scan depth).</summary>
    private static double DefaultSeconds(MediaKind kind, ScanDepth depth) => (kind, depth) switch
    {
        (_, ScanDepth.Quick) => 0.1,
        (MediaKind.Video, ScanDepth.Deep) => 120,
        (MediaKind.Video, _) => 10,
        _ => 0.3,
    };
}

[JsonSerializable(typeof(Dictionary<string, EstimateAverages>))]
internal sealed partial class EstimateJsonContext : JsonSerializerContext;
