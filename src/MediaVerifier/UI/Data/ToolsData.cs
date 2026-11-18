using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>The Tools cards: quarantine, the operations journal, reports and the database.</summary>
internal sealed record ToolsData(
    int QuarantineItems,
    long QuarantineBytes,
    int? OldestRemovedInDays,
    string? LastOperation,
    DateTimeOffset? LastOperationWhen,
    bool? LastCanUndo,
    int OpenProblems,
    DateTimeOffset? LastReport,
    long DatabaseBytes,
    string DatabaseLocation,
    DateTimeOffset? LastBackup,
    long FilesIndexed,
    IReadOnlyList<OperationRecord> Operations,
    IReadOnlyList<QuarantineEntry>? Quarantine = null);

/// <summary>
/// An original kept in quarantine, looked up with the rest of the snapshot off the UI thread (the quarantine of a
/// network folder that is offline makes every look-up wait). <paramref name="Size"/> is null when the file is out of
/// reach; an original that is gone (deleted for good, or removed by hand) is not listed at all.
/// </summary>
internal sealed record QuarantineEntry(OperationRecord Operation, long? Size);

internal partial interface IUiData
{
    ToolsData LoadTools();
}

/// <summary>Tools snapshot from the library database (off the UI thread).</summary>
internal sealed partial class LiveUiData
{
    /// <summary>Meta key of the last saved report's time (UTC ticks).</summary>
    public const string LastReportKey = "reports.last";

    /// <summary>Journal rows read for the Operations view; the batch cut off at the end is read whole.</summary>
    private const int RecentOperations = 2000;

    public ToolsData LoadTools()
    {
        List<OperationRecord> operations = LoadRecentOperations();

        // Every kept original, however old the operation: the newest 500 journal rows hid older ones after a large
        // batch, although they were still on disk and could only be restored or deleted from here.
        Dictionary<string, bool> reachable = new(StringComparer.OrdinalIgnoreCase);
        List<QuarantineEntry> quarantine = [];
        foreach (OperationRecord operation in database.Journal.List(int.MaxValue, status: OperationStatus.Done))
        {
            if (operation.BackupPath is string backup && Kept(backup, reachable, out long? size))
            {
                quarantine.Add(new QuarantineEntry(operation, size));
            }
        }

        List<QuarantineEntry> kept = [.. quarantine.Where(q => q.Size is not null)];
        long keptBytes = kept.Sum(q => q.Size!.Value);
        int? oldestIn = kept.Count == 0 ? null : Math.Max(0, settings().EmptyQuarantineAfterDays - (int)(clock.UtcNow - Ticks(kept.Min(q => q.Operation.Utc))).TotalDays);

        // An undo is recorded as an operation of its own; the card names what the user did, not the undo of it.
        OperationRecord? last = operations.FirstOrDefault(o => !IsUndo(o));
        long? lastReport = long.TryParse(database.Meta.Get(LastReportKey), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long ticks) ? ticks : null;
        string location = database.FilePath;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string shown = location.StartsWith(local, StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + Path.GetDirectoryName(location)![local.Length..] : Path.GetDirectoryName(location) ?? location;
        return new ToolsData(
            kept.Count,
            keptBytes,
            oldestIn,
            last is null ? null : ActionName(last.ActionId),
            last is null ? null : Ticks(last.Utc),
            last is null ? null : operations.Any(o => o.BatchId == last.BatchId && CanUndo(o)),
            database.Queries.SeverityCounts().Total,
            lastReport is long r ? Ticks(r) : null,
            database.Maintenance.SizeBytes(),
            shown,
            database.Maintenance.LastBackupUtc(),
            database.Queries.Health(TimeSpan.FromDays(settings().ScrubPeriodDays), clock.UtcNow).FilesIndexed,
            operations,
            quarantine);
    }

    /// <summary>The record of an undo (its detail names the operation it undid).</summary>
    internal static bool IsUndo(OperationRecord operation) => operation.DetailJson?.StartsWith("undo:", StringComparison.Ordinal) == true;

    /// <summary>A done operation whose original was kept: Undo can bring it back (the Recycle Bin is Windows' to undo).</summary>
    internal static bool CanUndo(OperationRecord operation) =>
        operation.Status == OperationStatus.Done && operation.UndoJson is string undo && !undo.Contains("\"actionClass\":\"Recycle\"", StringComparison.Ordinal);

    /// <summary>The newest journal rows; a batch cut off by the limit is read whole, so its file count is right.</summary>
    private List<OperationRecord> LoadRecentOperations()
    {
        List<OperationRecord> operations = database.Journal.List(RecentOperations);
        if (operations.Count == RecentOperations)
        {
            long oldest = operations[^1].BatchId;
            operations.RemoveAll(o => o.BatchId == oldest);
            operations.AddRange(database.Journal.List(int.MaxValue, oldest));
        }

        return operations;
    }

    /// <summary>
    /// Whether a kept original is still there, with its size (null when its drive or share is out of reach). Each
    /// drive or share is asked once, so an offline network folder makes the snapshot wait once, not once per file.
    /// </summary>
    private static bool Kept(string path, Dictionary<string, bool> reachable, out long? size)
    {
        size = null;
        try
        {
            string root = Path.GetPathRoot(path) ?? string.Empty;
            if (!reachable.TryGetValue(root, out bool online))
            {
                online = root.Length > 0 && Directory.Exists(root);
                reachable[root] = online;
            }

            if (!online)
            {
                return true;
            }

            FileInfo file = new(path);
            if (!file.Exists)
            {
                return false;
            }

            size = file.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>An action id as the journal names it (subplan 12 action catalog).</summary>
    internal static string ActionName(string actionId) => actionId switch
    {
        "ACT-RESTORE-COPY" => Strings.Actions.RestoreCopy,
        "ACT-REMUX" => Strings.Actions.Remux,
        "ACT-FIX-EXT" => Strings.Actions.FixExtension,
        "ACT-SHRINK-BITDEPTH" => Strings.Actions.ShrinkBitDepth,
        "ACT-REENCODE-FLAC" => Strings.Actions.ReencodeFlac,
        "ACT-SALVAGE" => Strings.Actions.Salvage,
        "ACT-EXTRACT-PREVIEW" => Strings.Actions.ExtractPreview,
        "ACT-DECLIP-COPY" => Strings.Actions.DeclipCopy,
        "ACT-QUARANTINE" => Strings.Actions.Quarantine,
        "ACT-RECYCLE" => Strings.Actions.Recycle,
        "ACT-ACCEPT-BASELINE" => Strings.Actions.AcceptBaseline,
        "ACT-EXPORT-LIST" => Strings.Actions.ExportList,
        _ => actionId,
    };
}
