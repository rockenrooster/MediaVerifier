using System.Diagnostics;
using System.Globalization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Remediation;

/// <summary>Moves the original to the root's quarantine folder (subplan 12, <c>ACT-QUARANTINE</c>).</summary>
public sealed class QuarantineAction : IRemediationAction
{
    public string Id => ActionIds.Quarantine;

    public ActionClass Class => ActionClass.Move;

    public Task ProduceAsync(OperationContext context, CancellationToken token) => Task.CompletedTask;

    public Task<string?> VerifyAsync(OperationContext context, CancellationToken token) => Task.FromResult<string?>(null);
}

/// <summary>Renames to the detected format's extension (subplan 12, <c>ACT-FIX-EXT</c>); a taken name gets a suffix.</summary>
public sealed class FixExtensionAction : IRemediationAction
{
    public string Id => ActionIds.FixExtension;

    public ActionClass Class => ActionClass.Move;

    public Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        FindingRecord? mismatch = context.Database.Findings.ForFile(context.File.Id).FirstOrDefault(f => f.RuleId == "GEN-EXT-MISMATCH" && f.Status == FindingStatus.Open);
        IReadOnlyDictionary<string, object> evidence = EvidenceJson.Deserialize(mismatch?.EvidenceJson);
        string? expected = evidence.TryGetValue("expectedExtension", out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        if (string.IsNullOrEmpty(expected) || string.Equals(Path.GetExtension(context.SourcePath).TrimStart('.'), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(CoreStrings.Remediation.NotNeeded);
        }

        context.FinalName = Path.GetFileNameWithoutExtension(context.SourcePath) + "." + expected;
        context.Detail["newName"] = context.FinalName;
        return Task.CompletedTask;
    }

    public Task<string?> VerifyAsync(OperationContext context, CancellationToken token) => Task.FromResult<string?>(null);
}

/// <summary>Accepts the current content as the new baseline (subplan 12, <c>ACT-ACCEPT-BASELINE</c>); the caller has the user confirm first.</summary>
public sealed class AcceptBaselineAction(IMediaFileSystem fileSystem) : IRemediationAction
{
    public string Id => ActionIds.AcceptBaseline;

    public ActionClass Class => ActionClass.Meta;

    public async Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        IntegrityResult read = await IntegrityReader.ReadAsync(context.SourcePath, fileSystem, token).ConfigureAwait(false);
        if (read.Hash is not byte[] hash || fileSystem.Stat(context.SourcePath) is not FileStat stat)
        {
            throw new IOException(CoreStrings.Remediation.OutputUnreadable);
        }

        long now = context.Database.Clock.UtcNow.UtcTicks;
        await context.Database.Writer.ExecuteAsync(
            c =>
            {
                FileIndex.ApplyUpdate(c, new FileUpdate(context.File.Id) { Size = stat.Size, MtimeUtc = stat.LastWriteUtcTicks, Baseline = new Baseline(hash, stat.Size, stat.LastWriteUtcTicks, now) });
                FindingStore.Resolve(c, context.File.Id, "INT-ROT", OperationRunner.Resolution(context.OperationId));
                FileEventStore.Add(c, context.File.Id, null, FileEventKind.Baselined);
                return 0;
            },
            ChangeFlags.Files | ChangeFlags.Findings,
            token).ConfigureAwait(false);
    }

    public Task<string?> VerifyAsync(OperationContext context, CancellationToken token) => Task.FromResult<string?>(null);
}

/// <summary>
/// Copies a fingerprint-verified healthy copy from another root over the damaged file (subplan 12,
/// <c>ACT-RESTORE-COPY</c>): the copy is checked before copying and the written file after.
/// </summary>
public sealed class RestoreCopyAction(IMediaFileSystem fileSystem) : IRemediationAction
{
    public string Id => ActionIds.RestoreCopy;

    public ActionClass Class => ActionClass.Lossless;

    public async Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only damage that happened after the fingerprint can be undone from a copy of that fingerprint.
        if (context.File.Hash is not byte[] baseline || !ActionCatalog.ContentDiffersFromBaseline(context.Database, context.File.Id))
        {
            throw new InvalidOperationException(CoreStrings.Remediation.NoHealthyCopy);
        }

        HealthyCopyFinder finder = new(context.Database, fileSystem);
        foreach (CopyCandidate candidate in finder.Find(context.File))
        {
            if (await finder.VerifyAsync(candidate, baseline, token).ConfigureAwait(false))
            {
                File.Copy(ExtendedPath.For(candidate.FullPath), context.TempPath, overwrite: false);
                context.Detail["copiedFrom"] = candidate.FullPath;
                return;
            }
        }

        throw new InvalidOperationException(CoreStrings.Remediation.NoHealthyCopy);
    }

    public async Task<string?> VerifyAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        IntegrityResult read = await IntegrityReader.ReadAsync(context.TempPath, fileSystem, token).ConfigureAwait(false);
        return read.Hash is byte[] hash && context.File.Hash is byte[] baseline && hash.AsSpan().SequenceEqual(baseline) ? null : CoreStrings.Remediation.CopyChanged;
    }
}

/// <summary>
/// Rebuilds the container and its index without touching the streams (subplan 12, <c>ACT-REMUX</c>); the
/// output must carry exactly the same stream hashes and every tag of the original.
/// </summary>
public sealed class RemuxAction : IRemediationAction
{
    public string Id => ActionIds.Remux;

    public ActionClass Class => ActionClass.Lossless;

    public async Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        string extension = Path.GetExtension(context.SourcePath).ToLowerInvariant();
        List<string> args = ["-fflags", "+genpts", "-i", context.SourcePath, "-map", "0", "-c", "copy", "-map_metadata", "0", "-ignore_unknown"];
        if (extension is ".mp4" or ".m4v" or ".mov" or ".m4a" or ".3gp")
        {
            ProbeResult? probe = await Ff.ProbeAsync(context, context.SourcePath, token).ConfigureAwait(false);
            if (probe is not null && Ff.HasKeyedTags(probe.FormatTags))
            {
                // Phone videos keep capture date, place and camera as QuickTime "mdta" keys, which the muxer writes only
                // with use_metadata_tags. Only such files get it: it would move an M4A's iTunes tags where players do not
                // look. The ftyp fields are left out so they are not written as keys as well.
                args.AddRange(["-movflags", "+faststart+use_metadata_tags", "-metadata", "major_brand=", "-metadata", "minor_version=", "-metadata", "compatible_brands="]);
            }
            else
            {
                args.AddRange(["-movflags", "+faststart"]);
            }
        }

        args.Add(context.TempPath);
        FfResult result = await Ff.RunAsync(context, args, token).ConfigureAwait(false);
        if (!result.Completed || !File.Exists(context.TempPath))
        {
            throw new IOException(CoreStrings.Remediation.ToolFailed);
        }
    }

    public async Task<string?> VerifyAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? before = await Ff.StreamHashesAsync(context, context.SourcePath, token).ConfigureAwait(false);
        string? after = await Ff.StreamHashesAsync(context, context.TempPath, token).ConfigureAwait(false);
        if (before is null || before != after)
        {
            return CoreStrings.Remediation.StreamsDiffer;
        }

        return await Ff.CompareTagsAsync(context, sameCodec: false, token).ConfigureAwait(false);
    }
}

/// <summary>
/// Rewrites a FLAC (subplan 12, <c>ACT-REENCODE-FLAC</c>), or stores padded audio at 16 bits
/// (<c>ACT-SHRINK-BITDEPTH</c>): the decoded audio of the new file must equal the original's exactly, in the same
/// format (a WAV stays PCM: FLAC inside a .wav plays almost nowhere) and with the same tags.
/// </summary>
public sealed class FlacRewriteAction(bool shrinkTo16) : IRemediationAction
{
    public string Id => shrinkTo16 ? ActionIds.ShrinkBitDepth : ActionIds.ReencodeFlac;

    public ActionClass Class => ActionClass.Lossless;

    /// <summary>The audio codec arguments for a file, or null when this action cannot write its format.</summary>
    public static IReadOnlyList<string>? CodecArguments(string path, bool shrinkTo16) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".flac" => shrinkTo16 ? ["-c:a", "flac", "-sample_fmt", "s16"] : ["-c:a", "flac"],
        ".wav" when shrinkTo16 => ["-c:a", "pcm_s16le"],
        _ => null,
    };

    public async Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<string> codec = CodecArguments(context.SourcePath, shrinkTo16) ?? throw new InvalidOperationException(CoreStrings.Remediation.WrongFormat);
        List<string> args = ["-i", context.SourcePath, "-map", "0:a:0", "-map", "0:v?", "-c:v", "copy", "-map_metadata", "0", .. codec, context.TempPath];
        FfResult result = await Ff.RunAsync(context, args, token).ConfigureAwait(false);
        if (!result.Completed || !File.Exists(context.TempPath))
        {
            throw new IOException(CoreStrings.Remediation.ToolFailed);
        }
    }

    public async Task<string?> VerifyAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Compared as 32-bit integers: padded 24-bit samples and their 16-bit copies scale to the same values.
        string? before = await Ff.PcmMd5Async(context, context.SourcePath, token).ConfigureAwait(false);
        string? after = await Ff.PcmMd5Async(context, context.TempPath, token).ConfigureAwait(false);
        if (before is null || before != after)
        {
            return CoreStrings.Remediation.AudioDiffers;
        }

        return await Ff.CompareTagsAsync(context, sameCodec: true, token).ConfigureAwait(false);
    }
}

/// <summary>
/// Writes a playable copy of damaged audio or video beside the original (subplan 12, <c>ACT-SALVAGE</c>):
/// damaged packets are dropped; the copy must decode cleanly.
/// </summary>
public sealed class SalvageAction : IRemediationAction
{
    public string Id => ActionIds.Salvage;

    public ActionClass Class => ActionClass.Copy;

    public async Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<string> args = ["-err_detect", "ignore_err", "-fflags", "+discardcorrupt+genpts", "-i", context.SourcePath, "-map", "0", "-c", "copy", "-ignore_unknown", context.TempPath];
        FfResult result = await Ff.RunAsync(context, args, token).ConfigureAwait(false);
        if (!File.Exists(context.TempPath) || new FileInfo(context.TempPath).Length == 0)
        {
            throw new IOException(CoreStrings.Remediation.ToolFailed);
        }

        _ = result;
        context.FinalName = Path.GetFileNameWithoutExtension(context.SourcePath) + ".salvaged" + Path.GetExtension(context.SourcePath);
    }

    public async Task<string?> VerifyAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        FfResult decode = await Ff.RunAsync(context, ["-xerror", "-i", context.TempPath, "-map", "0", "-f", "null", "-"], token, "error").ConfigureAwait(false);
        return decode.Completed && !decode.Log.Has(LogSignalKind.DecodeError) && !decode.Log.Has(LogSignalKind.OpenFailure) && !decode.Log.Has(LogSignalKind.Truncation) ? null : CoreStrings.Remediation.OutputUnreadable;
    }
}

/// <summary>FFmpeg helpers for the actions; arguments only through the runner's argument list.</summary>
internal static class Ff
{
    /// <summary>FFmpeg still reports but its position has not moved for this long: it is stuck (a damaged input can loop).</summary>
    internal static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs FFmpeg with progress reports on stdout. A repair copies, hashes or decodes the whole file, which takes far
    /// more than a minute for a big video, so a run is not stopped for being quiet: FFmpeg prints nothing while it
    /// finishes the file (fast start rewrites a whole MP4). It is stopped when its reported position stops moving
    /// (<see cref="StallLimit"/>) or at the overall limit, which grows with the file.
    /// </summary>
    public static async Task<FfResult> RunAsync(OperationContext context, IReadOnlyList<string> args, CancellationToken token, string logLevel = "warning", Action<LogLine>? onLogLine = null, string? workingDirectory = null)
    {
        TimeSpan limit = TimeSpan.FromHours(2) + TimeSpan.FromSeconds(context.File.Size / 5_000_000.0);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        StallWatch watch = new(StallLimit, stop.Cancel);
        FfResult result = await context.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = ["-progress", "pipe:1", "-nostats", .. args],
                Stdout = StdoutMode.Progress,
                OnProgress = watch.Observe,
                OnLogLine = onLogLine,
                WorkingDirectory = workingDirectory,
                Priority = ProcessPriorityClass.Normal,
                IdleTimeout = limit,
                HardTimeout = limit,
                LogLevel = logLevel,
            },
            stop.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return watch.Stalled ? result with { Outcome = ProcessOutcome.TimedOut, IdleTimeout = true } : result;
    }

    /// <summary>One MD5 line per stream (<c>-f streamhash</c>) of every stream, or null when FFmpeg failed.</summary>
    public static Task<string?> StreamHashesAsync(OperationContext context, string path, CancellationToken token) =>
        HashAsync(context, ["-i", path, "-map", "0", "-c", "copy", "-f", "streamhash", "-hash", "md5"], token);

    /// <summary>MD5 of the first audio stream decoded to 32-bit integers, or null when FFmpeg failed.</summary>
    public static async Task<string?> PcmMd5Async(OperationContext context, string path, CancellationToken token) =>
        await HashAsync(context, ["-i", path, "-map", "0:a:0", "-c:a", "pcm_s32le", "-f", "md5"], token).ConfigureAwait(false) is { Length: > 4 } text ? text : null;

    /// <summary>What ffprobe reports about a file, or null when it could not open it.</summary>
    public static async Task<ProbeResult?> ProbeAsync(OperationContext context, string path, CancellationToken token)
    {
        FfResult result = await context.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffprobe,
                Arguments = ["-show_format", "-show_streams", "-show_error", "-of", "json", path],
                Stdout = StdoutMode.Text,
                Priority = ProcessPriorityClass.Normal,
            },
            token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ProbeResult probe = result.Completed ? ProbeParser.Parse(result.StdoutText, result.Log) : ProbeResult.NotOpened(result.Log);
        return probe.Opened ? probe : null;
    }

    /// <summary>
    /// Null when the new file keeps every tag of the original (values the muxer rewrites, such as the encoder, may
    /// differ) and, when asked, the same kind of audio; otherwise the reason.
    /// </summary>
    public static async Task<string?> CompareTagsAsync(OperationContext context, bool sameCodec, CancellationToken token)
    {
        ProbeResult? before = await ProbeAsync(context, context.SourcePath, token).ConfigureAwait(false);
        ProbeResult? after = await ProbeAsync(context, context.TempPath, token).ConfigureAwait(false);
        if (before is null || after is null)
        {
            return CoreStrings.Remediation.OutputUnreadable;
        }

        if (sameCodec && CodecFamily(before.Audio?.CodecName) != CodecFamily(after.Audio?.CodecName))
        {
            return CoreStrings.Remediation.FormatChanged;
        }

        return MissingTags(before.FormatTags, after.FormatTags).Count == 0 ? null : CoreStrings.Remediation.TagsLost;
    }

    /// <summary>Tag names of the original that the new file no longer has (case-insensitive).</summary>
    public static List<string> MissingTags(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        HashSet<string> kept = new(after.Keys, StringComparer.OrdinalIgnoreCase);
        return [.. before.Keys.Where(key => !kept.Contains(key))];
    }

    /// <summary>True when a file carries QuickTime "mdta" keys: only those have dotted names such as <c>com.apple.quicktime.make</c>.</summary>
    public static bool HasKeyedTags(IReadOnlyDictionary<string, string> tags) => tags.Keys.Any(key => key.Contains('.', StringComparison.Ordinal));

    /// <summary>The codec family: every PCM variant is one (16-bit PCM is the same format as 24-bit PCM).</summary>
    public static string? CodecFamily(string? codec) => codec is not null && codec.StartsWith("pcm_", StringComparison.Ordinal) ? "pcm" : codec;

    /// <summary>
    /// Runs a hash muxer into a scratch folder outside the library and returns its text. The hash cannot go to stdout,
    /// which carries the progress reports.
    /// </summary>
    private static async Task<string?> HashAsync(OperationContext context, List<string> args, CancellationToken token)
    {
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("MediaVerifier-hash-");
        try
        {
            FfResult result = await RunAsync(context, [.. args, "hash.txt"], token, workingDirectory: scratch.FullName).ConfigureAwait(false);
            string file = Path.Combine(scratch.FullName, "hash.txt");
            if (!result.Completed || !File.Exists(file))
            {
                return null;
            }

            string text = (await File.ReadAllTextAsync(file, token).ConfigureAwait(false)).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
            return text.Length > 0 ? text : null;
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A scanner holding the file: the temporary folder is cleaned by Windows later.
            }
        }
    }
}

/// <summary>
/// Watches <c>-progress</c> reports: FFmpeg reports twice a second whatever happens, so a stuck run still talks.
/// When the reported position has not moved for <paramref name="limit"/>, <paramref name="stop"/> is called once.
/// </summary>
internal sealed class StallWatch(TimeSpan limit, Action stop)
{
    private long _position = long.MinValue;
    private long _movedAt;
    private int _stalled;

    public bool Stalled => Volatile.Read(ref _stalled) != 0;

    /// <summary>Called for each report, in order, from one thread at a time.</summary>
    public void Observe(ProgressBlock block) => Observe(block, Stopwatch.GetTimestamp());

    internal void Observe(ProgressBlock block, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (_movedAt == 0 || (block.OutTimeMicroseconds is long position && position > _position))
        {
            _position = Math.Max(_position, block.OutTimeMicroseconds ?? long.MinValue);
            _movedAt = timestamp;
            return;
        }

        if (!block.IsEnd && Stopwatch.GetElapsedTime(_movedAt, timestamp) > limit && Interlocked.Exchange(ref _stalled, 1) == 0)
        {
            stop();
        }
    }
}
