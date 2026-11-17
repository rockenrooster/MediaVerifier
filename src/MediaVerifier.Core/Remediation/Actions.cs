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
        if (context.File.Hash is not byte[] baseline)
        {
            throw new InvalidOperationException(CoreStrings.Remediation.NoHealthyCopy);
        }

        HealthyCopyFinder finder = new(context.Database, fileSystem);
        foreach (CopyCandidate candidate in finder.Find(context.File))
        {
            if (await finder.VerifyAsync(candidate, baseline, token).ConfigureAwait(false))
            {
                File.Copy(candidate.FullPath, context.TempPath, overwrite: false);
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
/// output must carry exactly the same stream hashes.
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
            args.AddRange(["-movflags", "+faststart"]);
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
        return before is not null && before == after ? null : CoreStrings.Remediation.StreamsDiffer;
    }
}

/// <summary>
/// Rewrites a FLAC (subplan 12, <c>ACT-REENCODE-FLAC</c>), or stores padded audio at 16 bits
/// (<c>ACT-SHRINK-BITDEPTH</c>): the decoded audio of the new file must equal the original's exactly.
/// </summary>
public sealed class FlacRewriteAction(bool shrinkTo16) : IRemediationAction
{
    public string Id => shrinkTo16 ? ActionIds.ShrinkBitDepth : ActionIds.ReencodeFlac;

    public ActionClass Class => ActionClass.Lossless;

    public async Task ProduceAsync(OperationContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<string> args = ["-i", context.SourcePath, "-map", "0:a:0", "-map", "0:v?", "-c:v", "copy", "-map_metadata", "0", "-c:a", "flac"];
        if (shrinkTo16)
        {
            args.AddRange(["-sample_fmt", "s16"]);
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

        // Compared as 32-bit integers: padded 24-bit samples and their 16-bit copies scale to the same values.
        string? before = await Ff.PcmMd5Async(context, context.SourcePath, token).ConfigureAwait(false);
        string? after = await Ff.PcmMd5Async(context, context.TempPath, token).ConfigureAwait(false);
        return before is not null && before == after ? null : CoreStrings.Remediation.AudioDiffers;
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
    public static Task<FfResult> RunAsync(OperationContext context, IReadOnlyList<string> args, CancellationToken token, string logLevel = "warning") =>
        context.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = args,
                Stdout = StdoutMode.Text,
                Priority = ProcessPriorityClass.Normal,
                IdleTimeout = TimeSpan.FromSeconds(60),
                HardTimeout = TimeSpan.FromHours(2),
                LogLevel = logLevel,
            },
            token);

    /// <summary>One MD5 line per stream (<c>-f streamhash</c>) of every stream, or null when FFmpeg failed.</summary>
    public static async Task<string?> StreamHashesAsync(OperationContext context, string path, CancellationToken token)
    {
        FfResult result = await RunAsync(context, ["-i", path, "-map", "0", "-c", "copy", "-f", "streamhash", "-hash", "md5", "-"], token).ConfigureAwait(false);
        return result.Completed && !string.IsNullOrWhiteSpace(result.StdoutText) ? result.StdoutText.Replace("\r", string.Empty, StringComparison.Ordinal).Trim() : null;
    }

    /// <summary>MD5 of the first audio stream decoded to 32-bit integers, or null when FFmpeg failed.</summary>
    public static async Task<string?> PcmMd5Async(OperationContext context, string path, CancellationToken token)
    {
        FfResult result = await RunAsync(context, ["-i", path, "-map", "0:a:0", "-c:a", "pcm_s32le", "-f", "md5", "-"], token).ConfigureAwait(false);
        return result.Completed && result.StdoutText?.Trim() is { Length: > 4 } text ? text : null;
    }
}
