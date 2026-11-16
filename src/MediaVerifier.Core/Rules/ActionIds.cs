namespace MediaVerifier.Core.Rules;

/// <summary>Remediation action identifiers (subplan 12, Action catalogue).</summary>
public static class ActionIds
{
    public const string RestoreCopy = "ACT-RESTORE-COPY";
    public const string Remux = "ACT-REMUX";
    public const string FixExtension = "ACT-FIX-EXT";
    public const string ShrinkBitDepth = "ACT-SHRINK-BITDEPTH";
    public const string ReencodeFlac = "ACT-REENCODE-FLAC";
    public const string Salvage = "ACT-SALVAGE";
    public const string ExtractPreview = "ACT-EXTRACT-PREVIEW";
    public const string DeclipCopy = "ACT-DECLIP-COPY";
    public const string Quarantine = "ACT-QUARANTINE";
    public const string Recycle = "ACT-RECYCLE";
    public const string AcceptBaseline = "ACT-ACCEPT-BASELINE";
    public const string Ignore = "ACT-IGNORE";
    public const string IgnoreFolderRule = "ACT-IGNORE-FOLDER-RULE";
    public const string Rescan = "ACT-RESCAN";
    public const string Verify = "ACT-VERIFY";
    public const string ExportList = "ACT-EXPORT-LIST";
    public const string CheckDrive = "ACT-CHECK-DRIVE";
    public const string RecoverIndexHelp = "ACT-RECOVER-INDEX-HELP";
    public const string Open = "ACT-OPEN";
    public const string Reveal = "ACT-REVEAL";
    public const string CopyPath = "ACT-COPY-PATH";

    /// <summary>Offered on every rule (subplan 12: "every rule").</summary>
    public static IReadOnlyList<string> Common { get; } = [Rescan, Open, Reveal, CopyPath];

    public static IReadOnlyList<string> All { get; } =
    [
        RestoreCopy, Remux, FixExtension, ShrinkBitDepth, ReencodeFlac, Salvage, ExtractPreview, DeclipCopy,
        Quarantine, Recycle, AcceptBaseline, Ignore, IgnoreFolderRule, Rescan, Verify, ExportList, CheckDrive,
        RecoverIndexHelp, Open, Reveal, CopyPath,
    ];
}
