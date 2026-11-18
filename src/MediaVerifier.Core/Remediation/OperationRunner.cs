using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Logging;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using Microsoft.Data.Sqlite;

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

/// <summary>
/// The journal's record of a running operation, written before any file exists, so a crash can be cleaned up: the
/// temporary output, where the original goes, and the undo data that applies once the swap is done.
/// </summary>
internal sealed record JournalTemp(string? TempPath, string? BackupPath, UndoData? Undo = null);

/// <summary>
/// The safe-operation protocol of subplan 12 (task R-02): preconditions, journal, output to a temporary file
/// in the same folder, verification, swap with rollback, index update and undo data. No other code writes,
/// moves or deletes user media.
/// </summary>
public sealed class OperationRunner(Database database, FfRunner runner, IMediaFileSystem fileSystem, IClock clock)
{
    public const string QuarantineFolder = ".mediaverifier-quarantine";

    /// <summary>The detail of an undo record starts with this and the id of the operation it undid.</summary>
    public const string UndoMarker = "undo:";

    private const long MinFreeBytes = 100L * 1024 * 1024;

    /// <summary>Test hook: called with each step's name ("produce", "verify", "backup", "rename", "index"); may throw.</summary>
    internal Action<string>? BeforeStep { get; set; }

    /// <summary>The temporary output name: <c>&lt;folder&gt;\&lt;name&gt;.mvtmp-&lt;id&gt;&lt;ext&gt;</c> (same volume; FFmpeg picks the muxer from the extension).</summary>
    public static string TempPathFor(string source, long operationId) =>
        Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, Path.GetFileNameWithoutExtension(source) + TempMarker + operationId.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(source));

    /// <summary>In the name of every temporary output; scans leave such files alone.</summary>
    public const string TempMarker = ".mvtmp-";

    /// <summary>Where an original goes: <c>&lt;root&gt;\.mediaverifier-quarantine\&lt;operation id&gt;\&lt;relative path&gt;</c>.</summary>
    public static string QuarantinePathFor(RootRecord root, FileRecord file, long operationId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(file);
        return ExtendedPath.For(Path.Combine(root.Path, QuarantineFolder, operationId.ToString(CultureInfo.InvariantCulture), file.RelDir ?? string.Empty, file.Name));
    }

    /// <summary>The operation an undo record undid, or null for every other journal row (the Tools page labels them by it).</summary>
    public static long? UndoneOperation(OperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.DetailJson is not string detail || !detail.StartsWith(UndoMarker, StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> rest = detail.AsSpan(UndoMarker.Length);
        int end = rest.IndexOf(' ');
        return long.TryParse(end < 0 ? rest : rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : null;
    }

    /// <summary>Preconditions of step 1; null when the action can run, else the reason.</summary>
    public string? CheckPreconditions(FileRecord file, RootRecord root, IRemediationAction action, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(action);
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

        if (!fileSystem.DirectoryExists(root.Path))
        {
            return CoreStrings.Remediation.RootOffline;
        }

        // Every look at the file goes through the \\?\ form where needed: a folder named "Demo." is otherwise not found.
        string path = ExtendedPath.For(HealthyCopyFinder.FullPath(root, file));
        FileStat? stat = fileSystem.Stat(path);
        if (stat is null)
        {
            return CoreStrings.Remediation.Missing;
        }

        if (stat.Value.Size != file.Size || stat.Value.LastWriteUtcTicks != file.MtimeUtc)
        {
            return CoreStrings.Remediation.ChangedSinceScan;
        }

        if ((stat.Value.Attributes & FileAttributes.ReadOnly) != 0)
        {
            return CoreStrings.Remediation.ReadOnly;
        }

        // A repair takes a new fingerprint: content that changed on its own would become the new truth.
        if (action.Class == ActionClass.Lossless && action.Id != ActionIds.RestoreCopy
            && database.Findings.ForFile(file.Id).Any(f => f.RuleId == "INT-ROT" && f.Status == FindingStatus.Open))
        {
            return CoreStrings.Remediation.RotFirst;
        }

        try
        {
            using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (UnauthorizedAccessException)
        {
            return CoreStrings.Remediation.NotAllowed;
        }
        catch (IOException)
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

        string source = ExtendedPath.For(HealthyCopyFinder.FullPath(root, file));
        string? precondition;
        try
        {
            precondition = CheckPreconditions(file, root, action, file.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // One file's odd path or permissions fails that file, never the rest of the batch.
            precondition = ex.Message;
        }

        long operationId = await database.Journal.AddAsync(
            new OperationRecord { BatchId = batchId, Utc = clock.UtcNow.UtcTicks, ActionId = action.Id, FileId = file.Id, Status = precondition is null ? OperationStatus.Running : OperationStatus.Failed, SourcePath = source, DetailJson = precondition },
            token).ConfigureAwait(false);
        if (precondition is not null)
        {
            return new OperationOutcome(operationId, file.Id, OperationStatus.Failed, precondition, null);
        }

        string temp = TempPathFor(source, operationId);
        string backup = QuarantinePathFor(root, file, operationId);
        UndoData undo = new(action.Class.ToString(), source, null, null, file.Size, file.MtimeUtc, file.Hash is null ? null : Convert.ToHexStringLower(file.Hash), file.HashSize, file.HashMtimeUtc, file.HashUtc, file.RelDir, file.Name);

        // Journaled before anything moves: after a crash the start-up recovery finds the temporary file, the kept
        // original and, when the swap had finished, the undo data.
        UndoData? keptUndo = action.Class is ActionClass.Lossless ? undo with { ResultPath = source, BackupPath = backup }
            : action.Class == ActionClass.Move ? undo with { BackupPath = backup }
            : null;
        await database.Journal.UpdateAsync(operationId, OperationStatus.Running, detailJson: JsonSerializer.Serialize(new JournalTemp(temp, backup, keptUndo), RemediationJsonContext.Default.JournalTemp), cancellationToken: token).ConfigureAwait(false);
        OperationContext context = new() { File = file, Root = root, SourcePath = source, TempPath = temp, OperationId = operationId, Runner = runner, Database = database };
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

            // Producing and verifying can take many minutes: an edit saved meanwhile must not be replaced.
            if (action.Class is ActionClass.Lossless or ActionClass.Move && ChangedSince(file, source))
            {
                throw new IOException(CoreStrings.Remediation.ChangedSinceScan);
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
                    KeepModifiedTime(source, file);
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
    /// a new operation (listed in Tools and purged like any kept file), the original comes back, the index row and
    /// baseline are restored and the findings reopen. On a file an undo set aside, puts that file back when its name
    /// is free.
    /// </summary>
    public async Task<string?> UndoAsync(long operationId, long batchId, CancellationToken token)
    {
        OperationRecord? operation = await Task.Run(() => Operation(operationId), token).ConfigureAwait(false);
        if (operation is { Status: OperationStatus.Done, UndoJson: null, BackupPath: string setAsideBefore } && UndoneOperation(operation) is not null)
        {
            return await PutBackAsync(operation, setAsideBefore, token).ConfigureAwait(false);
        }

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

        // The undo record names the file it moves aside: the current file, or for a copy the copy.
        string movedAside = undo.ActionClass == nameof(ActionClass.Copy) ? undo.ResultPath ?? undo.SourcePath : undo.SourcePath;
        long undoId = await database.Journal.AddAsync(
            new OperationRecord { BatchId = batchId, Utc = clock.UtcNow.UtcTicks, ActionId = operation.ActionId, FileId = operation.FileId, Status = OperationStatus.Running, SourcePath = movedAside, DetailJson = UndoMarker + operationId.ToString(CultureInfo.InvariantCulture) },
            token).ConfigureAwait(false);
        string? setAside = null;
        try
        {
            switch (undo.ActionClass)
            {
                case nameof(ActionClass.Lossless):
                    if (undo.BackupPath is null || !File.Exists(undo.BackupPath))
                    {
                        throw new IOException(CoreStrings.Remediation.OriginalGone);
                    }

                    if (File.Exists(undo.SourcePath))
                    {
                        setAside = root is not null && file is not null ? QuarantinePathFor(root, file, undoId) : throw new IOException(CoreStrings.Remediation.CannotUndo);
                        await SetAsideAsync(undoId, undo.SourcePath, setAside, token).ConfigureAwait(false);
                    }

                    try
                    {
                        MoveBack(undo.BackupPath, undo.SourcePath);
                    }
                    catch (Exception ex) when (setAside is not null && ex is IOException or UnauthorizedAccessException)
                    {
                        // The library keeps a file at that name: the one it had.
                        File.Move(setAside, undo.SourcePath);
                        setAside = null;
                        throw;
                    }

                    break;
                case nameof(ActionClass.Copy):
                    if (undo.ResultPath is not null && File.Exists(undo.ResultPath))
                    {
                        setAside = root is not null
                            ? ExtendedPath.For(Path.Combine(root.Path, QuarantineFolder, undoId.ToString(CultureInfo.InvariantCulture), Path.GetFileName(undo.ResultPath)))
                            : throw new IOException(CoreStrings.Remediation.CannotUndo);
                        await SetAsideAsync(undoId, undo.ResultPath, setAside, token).ConfigureAwait(false);
                    }

                    break;
                case nameof(ActionClass.Move):
                    if (undo.BackupPath is not null)
                    {
                        if (!File.Exists(undo.BackupPath))
                        {
                            throw new IOException(CoreStrings.Remediation.OriginalGone);
                        }

                        if (File.Exists(undo.SourcePath))
                        {
                            throw new IOException(CoreStrings.Remediation.NameTaken);
                        }

                        MoveBack(undo.BackupPath, undo.SourcePath);
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
            await database.Journal.UpdateAsync(undoId, OperationStatus.Done, backupPath: setAside, cancellationToken: token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file already set aside stays recorded, so it is listed and purged like any kept file.
            await database.Journal.UpdateAsync(undoId, OperationStatus.Failed, backupPath: setAside, detailJson: UndoMarker + operationId.ToString(CultureInfo.InvariantCulture) + " " + ex.Message, cancellationToken: token).ConfigureAwait(false);
            return ex.Message;
        }
    }

    /// <summary>
    /// Start-up cleanup (subplan 12): operations left Running by a crash get their journaled temporary file removed
    /// and an original stranded in quarantine moved back, or, when the swap had finished, are completed with their
    /// undo data. Files are only touched when the journal names them; a folder that is offline is left for the next
    /// start, and one operation that cannot be cleaned up does not stop the others. Returns how many were settled.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken token = default)
    {
        List<OperationRecord> running = await Task.Run(() => database.Journal.List(int.MaxValue, status: OperationStatus.Running), token).ConfigureAwait(false);
        int settled = 0;
        foreach (OperationRecord operation in running)
        {
            try
            {
                if (await RecoverOneAsync(operation, token).ConfigureAwait(false))
                {
                    settled++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Shared?.Warning(string.Create(CultureInfo.InvariantCulture, $"Interrupted operation {operation.Id} could not be cleaned up; it is tried again at the next start"), ex);
            }
        }

        return settled;
    }

    /// <summary>Permanently deletes the original one operation kept in quarantine (the caller confirms first); returns the bytes freed.</summary>
    public async Task<long> PurgeOneAsync(long operationId, CancellationToken token = default)
    {
        OperationRecord? operation = await Task.Run(() => Operation(operationId), token).ConfigureAwait(false);
        if (operation?.BackupPath is not string backup || !backup.Contains(QuarantineFolder, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        long bytes = await Task.Run(() => DeleteKept(backup), token).ConfigureAwait(false);
        if (bytes >= 0)
        {
            await ForgetKeptAsync(operation.Id, token).ConfigureAwait(false);
        }

        return Math.Max(0, bytes);
    }

    /// <summary>
    /// Permanently deletes quarantined originals older than <paramref name="days"/> (the only permanent deletion; the
    /// caller confirms). Each one's journal row stops naming the file and can no longer be undone; a row whose file is
    /// already gone is settled the same way. A file that cannot be deleted is left for the next time.
    /// </summary>
    public async Task<(int Files, long Bytes)> PurgeAsync(int days, CancellationToken token = default)
    {
        long cutoff = clock.UtcNow.AddDays(-days).UtcTicks;
        List<OperationRecord> kept = await Task.Run(() => database.Journal.List(int.MaxValue, status: OperationStatus.Done), token).ConfigureAwait(false);
        int files = 0;
        long bytes = 0;
        foreach (OperationRecord operation in kept.Where(o => o.Utc <= cutoff && o.BackupPath is not null && o.BackupPath.Contains(QuarantineFolder, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool existed = File.Exists(operation.BackupPath);
                long freed = DeleteKept(operation.BackupPath!);
                if (freed < 0)
                {
                    continue;
                }

                if (existed)
                {
                    files++;
                    bytes += freed;
                }

                await ForgetKeptAsync(operation.Id, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Shared?.Warning(string.Create(CultureInfo.InvariantCulture, $"A quarantined file of operation {operation.Id} could not be deleted; it is tried again next time"), ex);
            }
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

    /// <summary>Moves a kept file back to its place, making its folder again if it was removed meanwhile.</summary>
    private static void MoveBack(string kept, string target)
    {
        if (Path.GetDirectoryName(target) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        File.Move(kept, target);
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

    /// <summary>
    /// A repaired file keeps the original's modified time when its size changed, so date-sorted libraries keep their
    /// order while sync tools that compare size and time still see the change. A same-size result keeps the new time,
    /// or those tools would skip it.
    /// </summary>
    private static void KeepModifiedTime(string path, FileRecord original)
    {
        try
        {
            FileInfo info = new(path);
            if (info.Length != original.Size)
            {
                info.LastWriteTimeUtc = new DateTime(original.MtimeUtc, DateTimeKind.Utc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // Only the date is lost; the repair stands.
        }
    }

    /// <summary>
    /// Deletes a kept file and the quarantine folders it leaves empty. Returns the bytes freed (0 when the file was
    /// already gone), or -1 when its folder cannot be reached, so the journal keeps naming it for a later try.
    /// </summary>
    private static long DeleteKept(string path)
    {
        if (!File.Exists(path))
        {
            return RootReachable(path) ? 0 : -1;
        }

        long bytes = new FileInfo(path).Length;
        File.Delete(path);
        string? folder = Path.GetDirectoryName(path);
        while (folder is not null && folder.Contains(QuarantineFolder, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return bytes;
    }

    /// <summary>Whether the root that holds a quarantine path is reachable now (an offline share says nothing about its files).</summary>
    private static bool RootReachable(string quarantinePath)
    {
        int at = quarantinePath.IndexOf(Path.DirectorySeparatorChar + QuarantineFolder, StringComparison.OrdinalIgnoreCase);
        string root = at > 0 ? quarantinePath[..at] : Path.GetDirectoryName(quarantinePath) ?? quarantinePath;
        return Directory.Exists(root.EndsWith(':') ? root + Path.DirectorySeparatorChar : root);
    }

    private bool ChangedSince(FileRecord file, string path) =>
        fileSystem.Stat(path) is not FileStat now || now.Size != file.Size || now.LastWriteUtcTicks != file.MtimeUtc;

    private void Step(string name) => BeforeStep?.Invoke(name);

    /// <summary>Journals where an undo puts a file before moving it, so a crash in between cannot hide it.</summary>
    private async Task SetAsideAsync(long undoId, string path, string setAside, CancellationToken token)
    {
        await database.Journal.UpdateAsync(undoId, OperationStatus.Running, backupPath: setAside, cancellationToken: token).ConfigureAwait(false);
        MoveInto(path, setAside);
    }

    /// <summary>
    /// An undo cut short by a crash: a file it set aside goes back when its place is still empty; when the original is
    /// back in its place the undo had finished, so it is completed.
    /// </summary>
    private async Task<bool> RecoverUndoAsync(OperationRecord operation, long undone, CancellationToken token)
    {
        if (operation.BackupPath is string setAside && File.Exists(setAside))
        {
            if (!RootReachable(setAside))
            {
                return false;
            }

            if (File.Exists(operation.SourcePath) && Operation(undone) is { Status: OperationStatus.Done, BackupPath: string kept } done && !File.Exists(kept))
            {
                if (done.UndoJson is string json && JsonSerializer.Deserialize(json, RemediationJsonContext.Default.UndoData) is UndoData undo
                    && done.FileId is long fileId && database.Files.Get(fileId) is FileRecord file)
                {
                    await RestoreIndexAsync(file, undo, undone, token).ConfigureAwait(false);
                }

                await database.Journal.UpdateAsync(undone, OperationStatus.Undone, cancellationToken: token).ConfigureAwait(false);
                await database.Journal.UpdateAsync(operation.Id, OperationStatus.Done, cancellationToken: token).ConfigureAwait(false);
                return true;
            }

            if (!File.Exists(operation.SourcePath))
            {
                MoveBack(setAside, operation.SourcePath);
            }
        }

        await database.Journal.UpdateAsync(operation.Id, OperationStatus.Failed, detailJson: UndoMarker + undone.ToString(CultureInfo.InvariantCulture) + " " + CoreStrings.Remediation.Interrupted, cancellationToken: token).ConfigureAwait(false);
        return true;
    }

    /// <summary>One crashed operation; false when its folder is offline and it waits for the next start.</summary>
    private async Task<bool> RecoverOneAsync(OperationRecord operation, CancellationToken token)
    {
        if (UndoneOperation(operation) is long undone)
        {
            return await RecoverUndoAsync(operation, undone, token).ConfigureAwait(false);
        }

        JournalTemp? temp = null;
        try
        {
            temp = operation.DetailJson is string json && json.StartsWith('{') ? JsonSerializer.Deserialize(json, RemediationJsonContext.Default.JournalTemp) : null;
        }
        catch (JsonException)
        {
        }

        if (temp?.BackupPath is string keptPath && !RootReachable(keptPath))
        {
            return false;
        }

        if (temp?.TempPath is string tempPath && tempPath.Contains(".mvtmp-" + operation.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            DeleteQuietly(tempPath);
        }

        if (temp?.BackupPath is string backup && File.Exists(backup))
        {
            if (!File.Exists(operation.SourcePath))
            {
                MoveBack(backup, operation.SourcePath);
            }
            else if (temp.Undo is UndoData undo)
            {
                // The swap had finished: the repaired file is in place and the original is kept. The next scan sees
                // the new size and time and takes the new fingerprint.
                await database.Journal.UpdateAsync(operation.Id, OperationStatus.Done, undo.ResultPath, backup, JsonSerializer.Serialize(undo, RemediationJsonContext.Default.UndoData), EvidenceJson.Serialize(null), token).ConfigureAwait(false);
                return true;
            }
        }

        await database.Journal.UpdateAsync(operation.Id, OperationStatus.Failed, detailJson: CoreStrings.Remediation.Interrupted, cancellationToken: token).ConfigureAwait(false);
        return true;
    }

    /// <summary>Puts back a file an undo set aside, when nothing has taken its name.</summary>
    private async Task<string?> PutBackAsync(OperationRecord operation, string setAside, CancellationToken token)
    {
        try
        {
            if (!File.Exists(setAside))
            {
                return CoreStrings.Remediation.OriginalGone;
            }

            if (File.Exists(operation.SourcePath))
            {
                return CoreStrings.Remediation.NameTaken;
            }

            MoveBack(setAside, operation.SourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        await database.Journal.UpdateAsync(operation.Id, OperationStatus.Undone, cancellationToken: token).ConfigureAwait(false);
        return null;
    }

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
                        ForgetAnalysis(c, file.Id);
                        FileEventStore.Add(c, file.Id, null, action.Id == ActionIds.RestoreCopy ? FileEventKind.Restored : FileEventKind.Repaired, EvidenceJson.Serialize(new Dictionary<string, object> { ["action"] = action.Id }));
                        ResolveAddressed(c, file.Id, action.Id, Resolution(context.OperationId));
                        return 0;
                    },
                    ChangeFlags.Files | ChangeFlags.Findings | ChangeFlags.Artifacts,
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
                        string name = Path.GetFileName(result);
                        FileIndex.Move(c, file.Id, file.RootId, file.RelDir ?? string.Empty, name);
                        SetExtension(c, file.Id, name);
                        ResolveAddressed(c, file.Id, action.Id, Resolution(context.OperationId));
                        return 0;
                    },
                    ChangeFlags.Files | ChangeFlags.Findings,
                    token).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>The resolution recorded on findings settled by an operation: <c>op:{id}</c>.</summary>
    public static string Resolution(long operationId) => "op:" + operationId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The rules an action addresses: those that list it (subplan 12, Rule → actions).</summary>
    internal static IEnumerable<string> RulesAddressedBy(string actionId) =>
        RuleCatalog.All.Where(r => r.ActionIds.Contains(actionId, StringComparer.Ordinal)).Select(r => r.Id);

    /// <summary>
    /// Resolves the open findings of the rules the action addresses. A repair settles what it repairs: a rebuilt index
    /// says nothing about decode errors, which stay listed (and ignored findings stay ignored).
    /// </summary>
    private static void ResolveAddressed(WriteContext context, long fileId, string actionId, string resolution)
    {
        foreach (string rule in RulesAddressedBy(actionId))
        {
            SqliteCommand command = context.Command(
                "UPDATE findings SET status = $resolved, resolved_utc = $now, resolution = $resolution WHERE file_id = $file AND rule_id = $rule AND status = $open");
            command.Add("$resolved", (int)FindingStatus.Resolved);
            command.Add("$open", (int)FindingStatus.Open);
            command.Add("$now", context.NowTicks);
            command.Add("$resolution", resolution);
            command.Add("$file", fileId);
            command.Add("$rule", rule);
            command.ExecuteNonQuery();
        }

        FindingStore.RecomputeFileSummary(context, fileId);
    }

    /// <summary>
    /// Drops what the index knew about the old content (stored analysis and stream hash), so the next Standard scan
    /// analyses the file as it is now instead of re-raising what a repair fixed from the old analysis.
    /// </summary>
    private static void ForgetAnalysis(WriteContext context, long fileId)
    {
        ArtifactStore.DeleteAll(context, fileId);
        SqliteCommand command = context.Command("UPDATE files SET stream_hash = NULL WHERE id = $id");
        command.Add("$id", fileId);
        command.ExecuteNonQuery();
    }

    /// <summary>The stored extension follows a rename (filters and the action catalogue read it).</summary>
    private static void SetExtension(WriteContext context, long fileId, string name)
    {
        SqliteCommand command = context.Command("UPDATE files SET ext = $ext WHERE id = $id");
        command.Add("$ext", MediaClassifier.Extension(name));
        command.Add("$id", fileId);
        command.ExecuteNonQuery();
    }

    private async Task RestoreIndexAsync(FileRecord file, UndoData undo, long operationId, CancellationToken token)
    {
        bool contentBack = undo.ActionClass == nameof(ActionClass.Lossless);
        await database.Writer.ExecuteAsync(
            c =>
            {
                if (undo.ActionClass == nameof(ActionClass.Move) && undo.OldName is not null && undo.ResultPath is not null)
                {
                    FileIndex.Move(c, file.Id, file.RootId, undo.OldRelDir ?? string.Empty, undo.OldName);
                    SetExtension(c, file.Id, undo.OldName);
                }

                FileIndex.ApplyUpdate(c, new FileUpdate(file.Id)
                {
                    Size = undo.Size,
                    MtimeUtc = undo.MtimeUtc,
                    Baseline = undo.HashHex is string hex ? new Baseline(Convert.FromHexString(hex), undo.HashSize ?? undo.Size, undo.HashMtimeUtc ?? undo.MtimeUtc, undo.HashUtc ?? 0) : null,
                    ScannedDepth = contentBack ? ScanDepth.Quick : null,
                });
                if (contentBack)
                {
                    ForgetAnalysis(c, file.Id);
                }

                FindingStore.Reopen(c, file.Id, Resolution(operationId));
                if (undo.ActionClass == nameof(ActionClass.Move) && undo.BackupPath is not null)
                {
                    FileIndex.ApplyUpdate(c, new FileUpdate(file.Id) { State = FileState.Issues });
                    FindingStore.RecomputeFileSummary(c, file.Id);
                }

                return 0;
            },
            ChangeFlags.Files | ChangeFlags.Findings | ChangeFlags.Artifacts,
            token).ConfigureAwait(false);
    }

    /// <summary>The journal can no longer bring the kept file back: it stops naming it and the operation stops being undoable.</summary>
    private Task ForgetKeptAsync(long operationId, CancellationToken token) =>
        database.Writer.ExecuteAsync(
            c =>
            {
                SqliteCommand command = c.Command("UPDATE operations SET backup_path = NULL, undo_json = NULL WHERE id = $id");
                command.Add("$id", operationId);
                return command.ExecuteNonQuery();
            },
            ChangeFlags.Operations,
            token);

    /// <summary>One journal row by id.</summary>
    private OperationRecord? Operation(long id) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, batch_id, utc, action_id, file_id, status, source_path, result_path, backup_path, undo_json, detail_json FROM operations WHERE id = $id";
        command.Add("$id", id);
        using SqliteDataReader r = command.ExecuteReader();
        return r.Read()
            ? new OperationRecord
            {
                Id = r.GetInt64(0),
                BatchId = r.GetInt64(1),
                Utc = r.GetInt64(2),
                ActionId = r.GetString(3),
                FileId = r.GetInt64OrNull(4),
                Status = (OperationStatus)r.GetInt32(5),
                SourcePath = r.GetString(6),
                ResultPath = r.GetStringOrNull(7),
                BackupPath = r.GetStringOrNull(8),
                UndoJson = r.GetStringOrNull(9),
                DetailJson = r.GetStringOrNull(10),
            }
            : null;
    });

    private async Task FinishAsync(long operationId, OperationStatus status, string? result, string? backup, UndoData? undo, string? reason, OperationContext context)
    {
        string detail = reason ?? EvidenceJson.Serialize(context.Detail);
        string? undoJson = undo is null ? null : JsonSerializer.Serialize(undo, RemediationJsonContext.Default.UndoData);
        await database.Journal.UpdateAsync(operationId, status, result, backup, undoJson, detail, CancellationToken.None).ConfigureAwait(false);
    }
}
