using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Remediation;

/// <summary>
/// Which actions apply to a finding, in the rule's order (subplan 12, task R-01), and the runnable ones.
/// Actions not built yet (<c>ACT-EXTRACT-PREVIEW</c>, <c>ACT-DECLIP-COPY</c>) are never offered.
/// </summary>
public static class ActionCatalog
{
    /// <summary>The actions that change files or the baseline (they run through <see cref="OperationRunner"/>).</summary>
    public static IReadOnlyList<string> Runnable { get; } =
    [
        ActionIds.RestoreCopy, ActionIds.Remux, ActionIds.FixExtension, ActionIds.ShrinkBitDepth, ActionIds.ReencodeFlac,
        ActionIds.Salvage, ActionIds.Quarantine, ActionIds.AcceptBaseline,
    ];

    /// <summary>Creates a runnable action, or null for advice, meta actions handled by the UI, and actions not built.</summary>
    public static IRemediationAction? Create(string actionId, IMediaFileSystem fileSystem) => actionId switch
    {
        ActionIds.RestoreCopy => new RestoreCopyAction(fileSystem),
        ActionIds.Remux => new RemuxAction(),
        ActionIds.FixExtension => new FixExtensionAction(),
        ActionIds.ShrinkBitDepth => new FlacRewriteAction(shrinkTo16: true),
        ActionIds.ReencodeFlac => new FlacRewriteAction(shrinkTo16: false),
        ActionIds.Salvage => new SalvageAction(),
        ActionIds.Quarantine => new QuarantineAction(),
        ActionIds.AcceptBaseline => new AcceptBaselineAction(fileSystem),
        _ => null,
    };

    /// <summary>
    /// The actions for one finding, most recommended first: the rule's list filtered by what applies to this
    /// file, then the actions every rule has.
    /// </summary>
    public static IReadOnlyList<string> ForFinding(string ruleId, FileRecord file, RootRecord root, bool hasHealthyCopy, bool decodesCleanly)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(root);
        List<string> actions = [];
        IReadOnlyList<string> listed = RuleCatalog.TryGet(ruleId, out RuleDefinition rule) ? rule.ActionIds : [];
        foreach (string action in listed)
        {
            if (Applies(action, ruleId, file, root, hasHealthyCopy, decodesCleanly) && !actions.Contains(action))
            {
                actions.Add(action);
            }
        }

        foreach (string action in ActionIds.Common)
        {
            if (!actions.Contains(action))
            {
                actions.Add(action);
            }
        }

        return actions;
    }

    /// <summary>The first runnable action that applies, for the drawer's primary button; null when only advice applies.</summary>
    public static string? Recommended(IReadOnlyList<string> actions) => actions?.FirstOrDefault(a => Runnable.Contains(a) && a != ActionIds.Quarantine && a != ActionIds.AcceptBaseline);

    private static bool Applies(string action, string ruleId, FileRecord file, RootRecord root, bool hasHealthyCopy, bool decodesCleanly)
    {
        bool share = root.Path.StartsWith(@"\\", StringComparison.Ordinal);
        bool backupRoot = root.Role == RootRole.Backup;
        bool flac = file.Ext.Equals("flac", StringComparison.OrdinalIgnoreCase);
        return action switch
        {
            ActionIds.RestoreCopy => hasHealthyCopy && !backupRoot,
            ActionIds.Recycle => !share && !backupRoot,
            ActionIds.Salvage => file.MediaKind is MediaKind.Audio or MediaKind.Video && !backupRoot,
            ActionIds.ExtractPreview or ActionIds.DeclipCopy => false,
            ActionIds.ReencodeFlac => flac && decodesCleanly && ruleId == "COR-CHECKSUM" && !backupRoot,
            ActionIds.ShrinkBitDepth => (flac || file.Ext.Equals("wav", StringComparison.OrdinalIgnoreCase)) && !backupRoot,
            ActionIds.AcceptBaseline => ruleId == "INT-ROT",
            ActionIds.Quarantine or ActionIds.Remux or ActionIds.FixExtension => !backupRoot,
            _ => true,
        };
    }

    /// <summary>Whether a fingerprint-verified copy exists in another root (the finder checks name, size and fingerprint).</summary>
    public static bool HasHealthyCopy(Database database, IMediaFileSystem fileSystem, FileRecord file) =>
        file.Hash is not null && new HealthyCopyFinder(database, fileSystem).Find(file).Any(c => c.FingerprintMatch);
}
