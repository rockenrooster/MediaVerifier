using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Remediation;

/// <summary>How an action treats the file (subplan 12, Action catalogue).</summary>
public enum ActionClass
{
    /// <summary>Bit-identical media payload, verified, replaces the original (kept in quarantine).</summary>
    Lossless,

    /// <summary>Creates a new file beside the original and never replaces it.</summary>
    Copy,

    /// <summary>Relocates or renames the file.</summary>
    Move,

    /// <summary>Changes app state only.</summary>
    Meta,

    /// <summary>Hands the file to the Windows Recycle Bin; it can be restored there, not undone here.</summary>
    Recycle,
}

/// <summary>Everything an action needs about one file.</summary>
public sealed class OperationContext
{
    public required FileRecord File { get; init; }

    public required RootRecord Root { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>Where the action writes its output (Lossless and Copy): same folder, <c>name.mvtmp-{id}.ext</c>.</summary>
    public required string TempPath { get; init; }

    public required long OperationId { get; init; }

    public required FfRunner Runner { get; init; }

    public required Database Database { get; init; }

    /// <summary>Set by the action: the file name of a Copy output or a renamed file (Move).</summary>
    public string? FinalName { get; set; }

    /// <summary>Numbers for the journal and the summary (seconds lost, dimensions, new name).</summary>
    public Dictionary<string, object> Detail { get; } = new(StringComparer.Ordinal);
}

/// <summary>One remediation action (subplan 12). Only <see cref="OperationRunner"/> calls it.</summary>
public interface IRemediationAction
{
    string Id { get; }

    ActionClass Class { get; }

    /// <summary>Lossless and Copy: writes the output to <see cref="OperationContext.TempPath"/>. Move: sets the final name. Meta: does its state change.</summary>
    Task ProduceAsync(OperationContext context, CancellationToken token);

    /// <summary>Checks the output against the source; null when it passes, else the reason in plain words.</summary>
    Task<string?> VerifyAsync(OperationContext context, CancellationToken token);
}

/// <summary>The result of one file's operation.</summary>
public sealed record OperationOutcome(long OperationId, long FileId, OperationStatus Status, string? Reason, string? ResultPath);

/// <summary>What undo needs, stored in the journal (invariant JSON).</summary>
public sealed record UndoData(
    string ActionClass,
    string SourcePath,
    string? ResultPath,
    string? BackupPath,
    long Size,
    long MtimeUtc,
    string? HashHex,
    long? HashSize,
    long? HashMtimeUtc,
    long? HashUtc,
    string? OldRelDir = null,
    string? OldName = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UndoData))]
[JsonSerializable(typeof(JournalTemp))]
internal sealed partial class RemediationJsonContext : JsonSerializerContext;

/// <summary>The journal's record of a temporary file, written before it exists, so a crash can be cleaned up.</summary>
internal sealed record JournalTemp(string? TempPath, string? BackupPath);

/// <summary>
/// The safe-operation protocol of subplan 12 (task R-02): preconditions, journal, output to a temporary file
/// in the same folder, verification, swap with rollback, index update and undo data. No other code writes,
/// moves or deletes user media.
/// </summary>
public sealed class OperationRunner(Database database, FfRunner runner, IMediaFileSystem fileSystem, IClock clock)
{
    public const string QuarantineFolder = ".mediaverifier-quarantine";
    private const long MinFreeBytes = 100L * 1024 * 1024;

    /// <summary>Test hook: called with each step's name ("produce", "verify", "backup", "rename", "index"); may throw.</summary>
    internal Action<string>? BeforeStep { get; set; }

    /// <summary>The temporary output name: <c>&lt;folder&gt;\&lt;name&gt;.mvtmp-&lt;id&gt;&lt;ext&gt;</c> (same volume; FFmpeg picks the muxer from the extension).</summary>
    public static string TempPathFor(string source, long operationId) =>
        Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, Path.GetFileNameWithoutExtension(source) + ".mvtmp-" + operationId.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(source));

    /// <summary>Where an original goes: <c>&lt;root&gt;\.mediaverifier-quarantine\&lt;operation id&gt;\&lt;relative path&gt;</c>.</summary>
    public static string QuarantinePathFor(RootRecord root, FileRecord file, long operationId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(file);
        return Path.Combine(root.Path, QuarantineFolder, operationId.ToString(CultureInfo.InvariantCulture), file.RelDir ?? string.Empty, file.Name);
    }

    /// <summary>Preconditions of step 1; null when the action can run, else the reason.</summary>
    public string? CheckPreconditions(FileRecord file, RootRecord root, IRemediationAction action, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(action);
        string path = HealthyCopyFinder.FullPath(root, file);
        if (root.Role == RootRole.Backup && action.Class != ActionClass.Meta)
        {
            return CoreStrings.Remediation.BackupRoot;
        }

        if (action.Class == ActionClass.Meta)
        {
            return null;
        }

        if (action.Class == ActionClass.Recycle && root.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return CoreStrings.Remediation.NoRecycleBin;
        }

        FileStat? stat = fileSystem.Stat(path);
        if (stat is null)
        {
            return CoreStrings.Remediation.Missing;
        }

        if (stat.Value.Size != file.Size || stat.Value.LastWriteUtcTicks != file.MtimeUtc)
        {
            return CoreStrings.Remediation.ChangedSinceScan;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
        {
            return CoreStrings.Remediation.ReadOnly;
        }

        try
        {
            using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CoreStrings.Remediation.InUse;
        }

        if (action.Class is ActionClass.Lossless or ActionClass.Copy)
        {
            long free = FreeBytes(path);
            long needed = estimatedBytes + Math.Max(MinFreeBytes, estimatedBytes / 20);
            if (free >= 0 && free < needed)
            {
                return CoreStrings.Remediation.NoSpace;
            }
        }

        return null;
    }

    /// <summary>Runs one action on one file with the full protocol. Never throws for a failed operation; the outcome says why.</summary>
    public async Task<OperationOutcome> RunAsync(long fileId, IRemediationAction action, long batchId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);
        FileRecord? file = await Task.Run(() => database.Files.Get(fileId), token).ConfigureAwait(false);
        RootRecord? root = file is null ? null : await Task.Run(() => database.Roots.Get(file.RootId), token).ConfigureAwait(false);
        if (file is null || root is null)
        {
            return new OperationOutcome(0, fileId, OperationStatus.Failed, CoreStrings.Remediation.Missing, null);
        }

        string source = HealthyCopyFinder.FullPath(root, file);
        string? precondition = CheckPreconditions(file, root, action, file.Size);
        long operationId = await database.Journal.AddAsync(
            new OperationRecord { BatchId = batchId, Utc = clock.UtcNow.UtcTicks, ActionId = action.Id, FileId = file.Id, Status = precondition is null ? OperationStatus.Running : OperationStatus.Failed, SourcePath = source, DetailJson = precondition },
            token).ConfigureAwait(false);
        if (precondition is not null)
        {
            return new OperationOutcome(operationId, file.Id, OperationStatus.Failed, precondition, null);
        }

        string temp = TempPathFor(source, operationId);
        string backup = QuarantinePathFor(root, file, operationId);
        await database.Journal.UpdateAsync(operationId, OperationStatus.Running, detailJson: JsonSerializer.Serialize(new JournalTemp(temp, backup), RemediationJsonContext.Default.JournalTemp), cancellationToken: token).ConfigureAwait(false);
        OperationContext context = new() { File = file, Root = root, SourcePath = source, TempPath = temp, OperationId = operationId, Runner = runner, Database = database };
        UndoData undo = new(action.Class.ToString(), source, null, null, file.Size, file.MtimeUtc, file.Hash is null ? null : Convert.ToHexStringLower(file.Hash), file.HashSize, file.HashMtimeUtc, file.HashUtc, file.RelDir, file.Name);
        Action? rollback = null;
        try
        {
            Step("produce");
            await action.ProduceAsync(context, token).ConfigureAwait(false);
            Step("verify");
            string? failure = await action.VerifyAsync(context, token).ConfigureAwait(false);
            if (failure is not null)
            {
                DeleteQuietly(temp);
                await FinishAsync(operationId, OperationStatus.Failed, null, null, null, failure, context).ConfigureAwait(false);
                return new OperationOutcome(operationId, file.Id, OperationStatus.Failed, failure, null);
            }

            string? result;
            string? kept = null;
            switch (action.Class)
            {
                case ActionClass.Lossless:
                    Step("backup");
                    MoveInto(source, backup);
                    kept = backup;
                    rollback = () => File.Move(backup, source);
                    Step("rename");
                    File.Move(temp, source);
                    rollback = () =>
                    {
                        File.Delete(source);
                        File.Move(backup, source);
                    };
                    result = source;
                    break;
                case ActionClass.Copy:
                    string copy = UniquePath(Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, context.FinalName ?? Path.GetFileName(source)));
                    Step("rename");
                    File.Move(temp, copy);
                    rollback = () => File.Delete(copy);
                    result = copy;
                    break;
                case ActionClass.Move when context.FinalName is null:
                    // Quarantine: the original itself is the thing moved.
                    Step("backup");
                    MoveInto(source, backup);
                    kept = backup;
                    rollback = () => File.Move(backup, source);
                    result = null;
                    break;
                case ActionClass.Recycle:
                    Step("backup");
                    RecycleAction.Send(source);
                    result = null;
                    break;
                case ActionClass.Move:
                    string renamed = UniquePath(Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, context.FinalName));
                    Step("rename");
                    File.Move(source, renamed);
                    rollback = () => File.Move(renamed, source);
                    result = renamed;
                    break;
                default:
                    result = null;
                    break;
            }

            Step("index");
            await UpdateIndexAsync(action, context, result, kept, token).ConfigureAwait(false);
            rollback = null;
            undo = undo with { ResultPath = result, BackupPath = kept };
            await FinishAsync(operationId, OperationStatus.Done, result, kept, undo, null, context).ConfigureAwait(false);
            return new OperationOutcome(operationId, file.Id, OperationStatus.Done, null, result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Undo whatever file step already happened, so the original is back where it was.
            string? rollbackFailure = null;
            try
            {
                rollback?.Invoke();
            }
            catch (Exception undoError) when (undoError is IOException or UnauthorizedAccessException)
            {
                rollbackFailure = undoError.Message;
            }

            DeleteQuietly(temp);
            bool cancelled = ex is OperationCanceledException;
            string reason = (cancelled ? CoreStrings.Remediation.Cancelled : ex.Message) + (rollbackFailure is null ? string.Empty : " " + rollbackFailure);
            await FinishAsync(operationId, cancelled ? OperationStatus.Cancelled : OperationStatus.Failed, null, null, null, reason, context).ConfigureAwait(false);
            return new OperationOutcome(operationId, file.Id, cancelled ? OperationStatus.Cancelled : OperationStatus.Failed, reason, null);
        }
    }

    /// <summary>
    /// Undoes a done operation whose original was kept (subplan 12, Undo): the current file goes to quarantine as
    /// a new operation, the original comes back, the index row and baseline are restored and the findings reopen.
    /// </summary>
    public async Task<string?> UndoAsync(long operationId, long batchId, CancellationToken token)
    {
        OperationRecord? operation = await Task.Run(() => database.Journal.List(int.MaxValue).FirstOrDefault(o => o.Id == operationId), token).ConfigureAwait(false);
        if (operation is not { Status: OperationStatus.Done, UndoJson: string json } || JsonSerializer.Deserialize(json, RemediationJsonContext.Default.UndoData) is not UndoData undo)
        {
            return CoreStrings.Remediation.CannotUndo;
        }

        if (undo.ActionClass == nameof(ActionClass.Recycle))
        {
            // Windows restores it from the Recycle Bin; nothing here can.
            return CoreStrings.Remediation.CannotUndo;
        }

        FileRecord? file = operation.FileId is long id ? await Task.Run(() => database.Files.Get(id), token).ConfigureAwait(false) : null;
        RootRecord? root = file is null ? null : await Task.Run(() => database.Roots.Get(file.RootId), token).ConfigureAwait(false);
        long undoId = await database.Journal.AddAsync(
            new OperationRecord { BatchId = batchId, Utc = clock.UtcNow.UtcTicks, ActionId = operation.ActionId, FileId = operation.FileId, Status = OperationStatus.Running, SourcePath = undo.SourcePath, DetailJson = "undo:" + operationId.ToString(CultureInfo.InvariantCulture) },
            token).ConfigureAwait(false);
        try
        {
            switch (undo.ActionClass)
            {
                case nameof(ActionClass.Lossless):
                    if (undo.BackupPath is null || !File.Exists(undo.BackupPath))
                    {
                        throw new IOException(CoreStrings.Remediation.OriginalGone);
                    }

                    if (root is not null && file is not null && File.Exists(undo.SourcePath))
                    {
                        MoveInto(undo.SourcePath, QuarantinePathFor(root, file, undoId));
                    }

                    File.Move(undo.BackupPath, undo.SourcePath);
                    break;
                case nameof(ActionClass.Copy):
                    if (undo.ResultPath is not null && File.Exists(undo.ResultPath) && root is not null && file is not null)
                    {
                        MoveInto(undo.ResultPath, Path.Combine(root.Path, QuarantineFolder, undoId.ToString(CultureInfo.InvariantCulture), Path.GetFileName(undo.ResultPath)));
                    }

                    break;
                case nameof(ActionClass.Move):
                    if (undo.BackupPath is not null)
                    {
                        if (!File.Exists(undo.BackupPath) || File.Exists(undo.SourcePath))
                        {
                            throw new IOException(CoreStrings.Remediation.OriginalGone);
                        }

                        File.Move(undo.BackupPath, undo.SourcePath);
                    }
                    else if (undo.ResultPath is not null)
                    {
                        if (File.Exists(undo.SourcePath))
                        {
                            throw new IOException(CoreStrings.Remediation.NameTaken);
                        }

                        File.Move(undo.ResultPath, undo.SourcePath);
                    }

                    break;
            }

            if (file is not null)
            {
                await RestoreIndexAsync(file, undo, operationId, token).ConfigureAwait(false);
            }

            await database.Journal.UpdateAsync(operationId, OperationStatus.Undone, operation.ResultPath, operation.BackupPath, operation.UndoJson, operation.DetailJson, token).ConfigureAwait(false);
            await database.Journal.UpdateAsync(undoId, OperationStatus.Done, cancellationToken: token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await database.Journal.UpdateAsync(undoId, OperationStatus.Failed, detailJson: ex.Message, cancellationToken: token).ConfigureAwait(false);
            return ex.Message;
        }
    }

    /// <summary>
    /// Start-up cleanup (subplan 12): operations left Running by a crash get their journaled temporary file removed
    /// and an original stranded in quarantine moved back. Files are only touched when the journal names them.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken token = default)
    {
        List<OperationRecord> running = await Task.Run(() => database.Journal.List(int.MaxValue, status: OperationStatus.Running), token).ConfigureAwait(false);
        foreach (OperationRecord operation in running)
        {
            JournalTemp? temp = null;
            try
            {
                temp = operation.DetailJson is string json && json.StartsWith('{') ? JsonSerializer.Deserialize(json, RemediationJsonContext.Default.JournalTemp) : null;
            }
            catch (JsonException)
            {
            }

            if (temp?.TempPath is string tempPath && tempPath.Contains(".mvtmp-" + operation.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                DeleteQuietly(tempPath);
            }

            if (temp?.BackupPath is string backup && File.Exists(backup) && !File.Exists(operation.SourcePath))
            {
                File.Move(backup, operation.SourcePath);
            }

            await database.Journal.UpdateAsync(operation.Id, OperationStatus.Failed, detailJson: CoreStrings.Remediation.Interrupted, cancellationToken: token).ConfigureAwait(false);
        }

        return running.Count;
    }

    /// <summary>Permanently deletes the original one operation kept in quarantine (the caller confirms first); returns the bytes freed.</summary>
    public async Task<long> PurgeOneAsync(long operationId, CancellationToken token = default)
    {
        OperationRecord? operation = await Task.Run(() => database.Journal.List(int.MaxValue).FirstOrDefault(o => o.Id == operationId), token).ConfigureAwait(false);
        if (operation?.BackupPath is not string backup || !backup.Contains(QuarantineFolder, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        long bytes = File.Exists(backup) ? new FileInfo(backup).Length : 0;
        File.Delete(backup);
        await database.Journal.UpdateAsync(operation.Id, operation.Status, operation.ResultPath, null, null, operation.DetailJson, token).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Permanently deletes quarantined originals older than <paramref name="days"/> (the only permanent deletion; the caller confirms).</summary>
    public async Task<(int Files, long Bytes)> PurgeAsync(int days, CancellationToken token = default)
    {
        long cutoff = clock.UtcNow.AddDays(-days).UtcTicks;
        List<OperationRecord> kept = await Task.Run(() => database.Journal.List(int.MaxValue, status: OperationStatus.Done), token).ConfigureAwait(false);
        int files = 0;
        long bytes = 0;
        foreach (OperationRecord operation in kept.Where(o => o.Utc <= cutoff && o.BackupPath is not null && o.BackupPath.Contains(QuarantineFolder, StringComparison.OrdinalIgnoreCase)))
        {
            if (File.Exists(operation.BackupPath))
            {
                bytes += new FileInfo(operation.BackupPath!).Length;
                File.Delete(operation.BackupPath!);
                files++;
            }

            await database.Journal.UpdateAsync(operation.Id, OperationStatus.Done, operation.ResultPath, null, null, operation.DetailJson, token).ConfigureAwait(false);
        }

        return (files, bytes);
    }

    private static long FreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null || root.StartsWith(@"\\", StringComparison.Ordinal) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static void MoveInto(string source, string target)
    {
        string folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        string quarantine = folder;
        while (quarantine.Length > 0 && !Path.GetFileName(quarantine).Equals(QuarantineFolder, StringComparison.OrdinalIgnoreCase))
        {
            quarantine = Path.GetDirectoryName(quarantine) ?? string.Empty;
        }

        if (quarantine.Length > 0)
        {
            File.SetAttributes(quarantine, File.GetAttributes(quarantine) | FileAttributes.Hidden);
        }

        File.Move(source, target);
    }

    /// <summary>A free name: <c>name.ext</c>, else <c>name (2).ext</c>, <c>name (3).ext</c>, …</summary>
    internal static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name} ({n}){extension}"));
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Step(string name) => BeforeStep?.Invoke(name);

    private async Task UpdateIndexAsync(IRemediationAction action, OperationContext context, string? result, string? kept, CancellationToken token)
    {
        FileRecord file = context.File;
        long now = clock.UtcNow.UtcTicks;
        switch (action.Class)
        {
            case ActionClass.Lossless:
                IntegrityResult read = await IntegrityReader.ReadAsync(result!, fileSystem, token).ConfigureAwait(false);
                FileStat? stat = fileSystem.Stat(result!);
                await database.Writer.ExecuteAsync(
                    c =>
                    {
                        FileIndex.ApplyUpdate(c, new FileUpdate(file.Id)
                        {
                            Size = stat?.Size,
                            MtimeUtc = stat?.LastWriteUtcTicks,
                            Baseline = read.Hash is byte[] hash && stat is FileStat s ? new Baseline(hash, s.Size, s.LastWriteUtcTicks, now) : null,
                            ScannedDepth = ScanDepth.Quick,
                        });
                        FileEventStore.Add(c, file.Id, null, action.Id == Rules.ActionIds.RestoreCopy ? FileEventKind.Restored : FileEventKind.Repaired, EvidenceJson.Serialize(new Dictionary<string, object> { ["action"] = action.Id }));
                        FindingStore.ResolveAll(c, file.Id, Resolution(context.OperationId));
                        return 0;
                    },
                    ChangeFlags.Files | ChangeFlags.Findings,
                    token).ConfigureAwait(false);
                break;
            case ActionClass.Recycle:
            case ActionClass.Move when kept is not null:
                await database.Writer.ExecuteAsync(
                    c =>
                    {
                        // The row stays (undo brings the file back); it counts as missing and its findings are settled.
                        FindingStore.ResolveAll(c, file.Id, Resolution(context.OperationId));
                        FileIndex.ApplyUpdate(c, new FileUpdate(file.Id) { State = FileState.Missing });
                        return 0;
                    },
                    ChangeFlags.Files | ChangeFlags.Findings,
                    token).ConfigureAwait(false);
                break;
            case ActionClass.Move when result is not null:
                await database.Writer.ExecuteAsync(
                    c =>
                    {
                        FileIndex.Move(c, file.Id, file.RootId, file.RelDir ?? string.Empty, Path.GetFileName(result));
                        return 0;
                    },
                    ChangeFlags.Files | ChangeFlags.Findings,
                    token).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>The resolution recorded on findings settled by an operation: <c>op:{id}</c>.</summary>
    public static string Resolution(long operationId) => "op:" + operationId.ToString(CultureInfo.InvariantCulture);

    private async Task RestoreIndexAsync(FileRecord file, UndoData undo, long operationId, CancellationToken token)
    {
        await database.Writer.ExecuteAsync(
            c =>
            {
                if (undo.ActionClass == nameof(ActionClass.Move) && undo.OldName is not null && undo.ResultPath is not null)
                {
                    FileIndex.Move(c, file.Id, file.RootId, undo.OldRelDir ?? string.Empty, undo.OldName);
                }

                FileIndex.ApplyUpdate(c, new FileUpdate(file.Id)
                {
                    Size = undo.Size,
                    MtimeUtc = undo.MtimeUtc,
                    Baseline = undo.HashHex is string hex ? new Baseline(Convert.FromHexString(hex), undo.HashSize ?? undo.Size, undo.HashMtimeUtc ?? undo.MtimeUtc, undo.HashUtc ?? 0) : null,
                });
                FindingStore.Reopen(c, file.Id, Resolution(operationId));
                if (undo.ActionClass == nameof(ActionClass.Move) && undo.BackupPath is not null)
                {
                    FileIndex.ApplyUpdate(c, new FileUpdate(file.Id) { State = FileState.Issues });
                    FindingStore.RecomputeFileSummary(c, file.Id);
                }

                return 0;
            },
            ChangeFlags.Files | ChangeFlags.Findings,
            token).ConfigureAwait(false);
    }

    private async Task FinishAsync(long operationId, OperationStatus status, string? result, string? backup, UndoData? undo, string? reason, OperationContext context)
    {
        string detail = reason ?? EvidenceJson.Serialize(context.Detail);
        string? undoJson = undo is null ? null : JsonSerializer.Serialize(undo, RemediationJsonContext.Default.UndoData);
        await database.Journal.UpdateAsync(operationId, status, result, backup, undoJson, detail, CancellationToken.None).ConfigureAwait(false);
    }
}
