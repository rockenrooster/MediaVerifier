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
/// network folder that is offline makes every look-up wait). <paramref name="Size"/> is null when the file is gone or
/// out of reach.
/// </summary>
internal sealed record QuarantineEntry(OperationRecord Operation, long? Size);

internal interface IToolsData
{
    ToolsData LoadTools();
}

/// <summary>Tools snapshot from the library database (off the UI thread).</summary>
internal sealed partial class LiveUiData
{
    /// <summary>Meta key of the last saved report's time (UTC ticks).</summary>
    public const string LastReportKey = "reports.last";

    public ToolsData LoadTools()
    {
        List<OperationRecord> operations = database.Journal.List(500);
        List<QuarantineEntry> quarantine = [.. operations.Where(o => o.Status == OperationStatus.Done && o.BackupPath is not null).Select(o => new QuarantineEntry(o, SizeOf(o.BackupPath!)))];
        List<QuarantineEntry> kept = [.. quarantine.Where(q => q.Size is not null)];
        long keptBytes = kept.Sum(q => q.Size!.Value);
        int? oldestIn = kept.Count == 0 ? null : Math.Max(0, settings().EmptyQuarantineAfterDays - (int)(clock.UtcNow - Ticks(kept.Min(q => q.Operation.Utc))).TotalDays);
        OperationRecord? last = operations.FirstOrDefault();
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
            last is null ? null : last.Status == OperationStatus.Done && last.UndoJson is not null,
            database.Queries.SeverityCounts().Total,
            lastReport is long r ? Ticks(r) : null,
            database.Maintenance.SizeBytes(),
            shown,
            database.Maintenance.LastBackupUtc(),
            database.Queries.Health(TimeSpan.FromDays(settings().ScrubPeriodDays), clock.UtcNow).FilesIndexed,
            operations,
            quarantine);
    }

    /// <summary>A kept file's size, or null when it is gone or cannot be reached.</summary>
    private static long? SizeOf(string path)
    {
        try
        {
            FileInfo file = new(path);
            return file.Exists ? file.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
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
