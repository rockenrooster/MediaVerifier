using System.Globalization;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis;

/// <summary>The stored summary of the last integrity read (GEN-EMPTY, GEN-ZEROFILL, GEN-UNREADABLE).</summary>
public sealed record IntegrityArtifact(
    IntegrityOutcome Outcome,
    long Size,
    long ZeroBlocks,
    long TotalBlocks,
    long LongestZeroRunBlocks,
    long LongestZeroRunOffset,
    long[] ZeroRunOffsets,
    int? Win32Error,
    long? ErrorOffset) : IArtifact
{
    public static IntegrityArtifact From(IntegrityResult result) => new(
        result.Outcome,
        result.Size,
        result.Zeros.ZeroBlocks,
        result.Zeros.TotalBlocks,
        result.Zeros.LongestRunBlocks,
        result.Zeros.LongestRunOffset,
        [.. result.Zeros.RunOffsets],
        result.Win32Error,
        result.ErrorOffset);

    [JsonIgnore]
    public double ZeroFraction => TotalBlocks == 0 ? 0 : (double)ZeroBlocks / TotalBlocks;
}

/// <summary>
/// The integrity read is done by the engine itself (read once, subplan 05); this "provider" only stores
/// and loads its summary so integrity rules can be re-evaluated.
/// </summary>
public sealed class IntegrityArtifactStore() : ArtifactProvider<IntegrityArtifact>(new ArtifactCodec<IntegrityArtifact>(ArtifactKind.Integrity, AnalysisJsonContext.Default.IntegrityArtifact))
{
    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Quick;

    public override bool Applies(FileContext file) => false;

    protected override Task<IntegrityArtifact> ProduceTypedAsync(FileContext file) =>
        throw new InvalidOperationException("The engine produces the integrity artifact during the read-once pass.");
}

/// <summary>GEN-EMPTY, GEN-ZEROFILL and GEN-UNREADABLE (task I-03).</summary>
public sealed class IntegrityDetector : IDetector
{
    /// <summary>Formats that legitimately contain long zero runs (uncompressed): no Low zero-run finding.</summary>
    private static readonly HashSet<string> Uncompressed = new(StringComparer.Ordinal) { "wav", "aiff", "aif", "bmp", "tif", "tiff", "dsf", "dff", "au", "caf" };

    public IReadOnlyList<string> RuleIds { get; } = ["GEN-EMPTY", "GEN-ZEROFILL", "GEN-UNREADABLE"];

    public IReadOnlyList<Type> Needs { get; } = [typeof(IntegrityArtifact)];

    /// <summary>The structure check's padding regions: zeros the container declares as padding are not lost data (subplan 06).</summary>
    public IReadOnlyList<Type> Optional { get; } = [typeof(StructureArtifact)];

    public ScanDepth MinDepth => ScanDepth.Quick;

    public bool Applies(DetectorInput input) => true;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        IntegrityArtifact integrity = input.Get<IntegrityArtifact>()!;
        if (integrity.Outcome == IntegrityOutcome.Unreadable)
        {
            sink.Raise(
                "GEN-UNREADABLE",
                Severity.Critical,
                1f,
                string.Create(CultureInfo.InvariantCulture, $"Read error {integrity.Win32Error ?? 0} at byte {integrity.ErrorOffset ?? 0:N0}"),
                new Dictionary<string, object>
                {
                    ["win32Code"] = integrity.Win32Error ?? 0,
                    ["offsetBytes"] = integrity.ErrorOffset ?? 0,
                    ["sizeBytes"] = integrity.Size,
                });
            return;
        }

        if (integrity.Outcome != IntegrityOutcome.Ok)
        {
            return;
        }

        if (integrity.Size == 0)
        {
            sink.Raise("GEN-EMPTY", Severity.Critical, 1f, "File is 0 bytes", new Dictionary<string, object> { ["sizeBytes"] = 0 });
            return;
        }

        // Known padding must not trigger (subplan 06): MP4 free boxes, FLAC PADDING, zeros after a JPEG's end and the
        // like, as the structure check recorded them. The longest run leaves the share when padding holds all of it;
        // a recorded run counts only when padding does not hold its first 64 KiB (each recorded run is at least that long).
        IReadOnlyList<PaddingRegion> padding = input.Get<StructureArtifact>() is { ReadFailed: false } structure ? structure.Padding : [];
        long longestBytes = integrity.LongestZeroRunBlocks * IntegrityReader.BlockSize;
        bool longestIsPadding = longestBytes > 0 && InPadding(padding, integrity.LongestZeroRunOffset, Math.Min(integrity.Size, integrity.LongestZeroRunOffset + longestBytes));
        long paddingBlocks = longestIsPadding ? integrity.LongestZeroRunBlocks : 0;
        long zeroBlocks = integrity.ZeroBlocks - paddingBlocks;
        long totalBlocks = integrity.TotalBlocks - paddingBlocks;
        double share = totalBlocks > 0 ? (double)zeroBlocks / totalBlocks : 0;
        const long recordedBytes = ZeroMap.RecordedRunBlocks * IntegrityReader.BlockSize;
        long[] runs = [.. integrity.ZeroRunOffsets.Where(offset => !InPadding(padding, offset, Math.Min(integrity.Size, offset + recordedBytes)))];

        double criticalShare = input.Settings.Value("GEN-ZEROFILL", "criticalPercent") / 100.0;
        Dictionary<string, object> evidence = new()
        {
            ["zeroBlocks"] = zeroBlocks,
            ["totalBlocks"] = totalBlocks,
            ["zeroPercent"] = Math.Round(share * 100, 1),
            ["runOffsetsBytes"] = runs,
            ["sizeBytes"] = integrity.Size,
        };
        if (longestIsPadding)
        {
            evidence["paddingBytes"] = Math.Min(longestBytes, integrity.Size - integrity.LongestZeroRunOffset);
        }
        else
        {
            evidence["longestRunBytes"] = longestBytes;
            evidence["longestRunOffsetBytes"] = integrity.LongestZeroRunOffset;
        }

        if (totalBlocks > 0 && share >= criticalShare)
        {
            sink.Raise("GEN-ZEROFILL", Severity.Critical, 1f, string.Create(CultureInfo.InvariantCulture, $"{share * 100:F0}% of the file is zeros; typical of a failed copy or file-system recovery"), evidence);
        }
        else if (integrity.LongestZeroRunBlocks >= ZeroMap.RecordedRunBlocks && !Uncompressed.Contains(input.Extension))
        {
            if (!longestIsPadding)
            {
                sink.Raise("GEN-ZEROFILL", Severity.Low, 1f, string.Create(CultureInfo.InvariantCulture, $"{EvidenceText.Size(longestBytes)} of zeros at byte {integrity.LongestZeroRunOffset:N0}"), evidence);
            }
            else if (runs.Length > 0)
            {
                // The longest run is padding; another one is not, and only its start is known.
                sink.Raise("GEN-ZEROFILL", Severity.Low, 1f, string.Create(CultureInfo.InvariantCulture, $"{EvidenceText.Size(recordedBytes)} or more of zeros at byte {runs[0]:N0}"), evidence);
            }
        }
    }

    /// <summary>True when the bytes <c>[start, end)</c> lie inside the declared padding (touching regions join up).</summary>
    internal static bool InPadding(IReadOnlyList<PaddingRegion> padding, long start, long end)
    {
        if (padding.Count == 0 || end <= start)
        {
            return false;
        }

        long reach = start;
        foreach (PaddingRegion region in padding.OrderBy(r => r.Offset))
        {
            long regionEnd = region.Offset + region.Length;
            if (regionEnd <= reach)
            {
                continue;
            }

            if (region.Offset > reach)
            {
                return false;
            }

            reach = regionEnd;
            if (reach >= end)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>GEN-PATH (task E-14): checks on the path alone, no I/O.</summary>
public sealed class PathDetector : IDetector
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public IReadOnlyList<string> RuleIds { get; } = ["GEN-PATH"];

    public IReadOnlyList<Type> Needs { get; } = [];

    public ScanDepth MinDepth => ScanDepth.Quick;

    public bool Applies(DetectorInput input) => true;

    public void Evaluate(DetectorInput input, FindingSink sink)
    {
        (string? condition, string? segment) = Check(input.Path, (int)input.Settings.Value("GEN-PATH", "maxPathLength"));
        if (condition is null)
        {
            return;
        }

        Dictionary<string, object> evidence = new() { ["condition"] = condition, ["pathLength"] = input.Path.Length };
        if (segment is not null)
        {
            evidence["segment"] = segment;
        }

        string summary = condition switch
        {
            "tooLong" => string.Create(CultureInfo.InvariantCulture, $"Path is {input.Path.Length} characters long"),
            "trailingDotOrSpace" => $"\"{segment}\" ends with a dot or a space",
            _ => $"\"{segment}\" is a name Windows reserves",
        };
        sink.Raise("GEN-PATH", Severity.Low, 1f, summary, evidence);
    }

    /// <summary>The first problem found: too long, a segment ending in a dot or space, or a reserved device name.</summary>
    public static (string? Condition, string? Segment) Check(string path, int maxLength = 259)
    {
        if (path.Length > maxLength)
        {
            return ("tooLong", null);
        }

        string withoutRoot = path;
        string? root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root))
        {
            withoutRoot = path[root.Length..];
        }

        foreach (string segment in withoutRoot.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return ("trailingDotOrSpace", segment);
            }

            int dot = segment.IndexOf('.', StringComparison.Ordinal);
            string stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
            if (Reserved.Contains(stem))
            {
                return ("reservedName", segment);
            }
        }

        return (null, null);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IntegrityArtifact))]
internal sealed partial class AnalysisJsonContext : JsonSerializerContext;
