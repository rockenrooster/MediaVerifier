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
    IReadOnlyList<OperationRecord> Operations);

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
        List<OperationRecord> kept = [.. operations.Where(o => o.Status == OperationStatus.Done && o.BackupPath is not null && File.Exists(o.BackupPath))];
        long keptBytes = kept.Sum(o => new FileInfo(o.BackupPath!).Length);
        int? oldestIn = kept.Count == 0 ? null : Math.Max(0, settings().EmptyQuarantineAfterDays - (int)(clock.UtcNow - Ticks(kept.Min(o => o.Utc))).TotalDays);
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
            operations);
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
