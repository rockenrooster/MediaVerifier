using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;

namespace MediaVerifier.UI;

/// <summary>
/// The window's side of subplan 12 (task R-12): confirm (dialog A, or the accept dialog), run the files one at a
/// time through the operation runner on a pool thread, then a toast with the result and Undo.
/// </summary>
internal static class Remediator
{
    /// <summary>The fix offered for a set of findings: the first runnable action of their rule when all share it.</summary>
    public static string? CommonFix(IEnumerable<string> ruleIds)
    {
        string? common = null;
        foreach (string rule in ruleIds)
        {
            string? fix = RuleCatalog.TryGet(rule, out RuleDefinition definition)
                ? definition.ActionIds.FirstOrDefault(a => ActionCatalog.Runnable.Contains(a) && a is not (ActionIds.RestoreCopy or ActionIds.Quarantine or ActionIds.AcceptBaseline))
                : null;
            if (fix is null || (common is not null && common != fix))
            {
                return null;
            }

            common = fix;
        }

        return common;
    }

    /// <summary>Asks, runs and reports. Returns after the toast is shown; nothing runs on the UI thread.</summary>
    public static async Task RunAsync(UiServices services, Control owner, IReadOnlyList<long> fileIds, string actionId)
    {
        if (services.Operations is not OperationRunner runner || fileIds.Count == 0 || owner.FindForm() is not Form form
            || ActionCatalog.Create(actionId, WindowsFileSystem.Instance) is not IRemediationAction action)
        {
            return;
        }

        (List<FileRecord> all, List<string> changed) = await Task.Run(() =>
        {
            List<FileRecord> records = services.Database.Files.GetMany(fileIds);
            Dictionary<long, RootRecord> roots = services.Database.Roots.List().ToDictionary(r => r.Id);
            List<string> moved = [];
            foreach (FileRecord file in records)
            {
                if (action.Class != ActionClass.Meta && roots.TryGetValue(file.RootId, out RootRecord? root)
                    && WindowsFileSystem.Instance.Stat(Core.Integrity.HealthyCopyFinder.FullPath(root, file)) is FileStat stat
                    && (stat.Size != file.Size || stat.LastWriteUtcTicks != file.MtimeUtc))
                {
                    moved.Add(file.Name);
                }
            }

            return (records, moved);
        }).ConfigureAwait(true);
        if (!Confirm(form, action, all, changed, services.State.Settings.Scanning.EmptyQuarantineAfterDays, services.Scans.Status.IsActive))
        {
            return;
        }

        // Files that changed since the scan are skipped, as the dialog said.
        List<FileRecord> files = [.. all.Where(f => !changed.Contains(f.Name))];

        long batch = services.Clock.UtcNow.UtcTicks;

        // An operation needs the engine idle: a running scan pauses while it works and then continues (subplan 12).
        bool paused = services.Scans.Status.IsActive && services.Scans.Status.Phase != Core.Engine.EnginePhase.Paused;
        if (paused)
        {
            services.Scans.Pause();
        }

        List<OperationOutcome> outcomes;
        try
        {
            outcomes = await Task.Run(async () =>
            {
                List<OperationOutcome> done = [];
                foreach (FileRecord file in files)
                {
                    done.Add(await runner.RunAsync(file.Id, action, batch, CancellationToken.None).ConfigureAwait(false));
                }

                return done;
            }).ConfigureAwait(true);
        }
        finally
        {
            if (paused)
            {
                services.Scans.Resume();
            }
        }

        Report(services, action, outcomes, batch);
    }

    /// <summary>Ctrl+Z: undoes the newest batch that still has an undoable operation.</summary>
    public static async Task UndoLatestAsync(UiServices services)
    {
        if (services.Operations is null || services.IsDemo)
        {
            return;
        }

        long? batch = await Task.Run(() => services.Database.Journal.List(500)
            .Where(o => o.Status == OperationStatus.Done && o.UndoJson is not null && !o.UndoJson.Contains("\"actionClass\":\"Recycle\"", StringComparison.Ordinal))
            .OrderByDescending(o => o.Id)
            .Select(o => (long?)o.BatchId)
            .FirstOrDefault()).ConfigureAwait(true);
        if (batch is long id)
        {
            await UndoBatchAsync(services, id).ConfigureAwait(true);
        }
    }

    /// <summary>Undoes every done operation of a batch (newest first) and reports.</summary>
    public static async Task UndoBatchAsync(UiServices services, long batch)
    {
        if (services.Operations is not OperationRunner runner)
        {
            return;
        }

        string? failure = await Task.Run(async () =>
        {
            string? first = null;
            long undoBatch = services.Clock.UtcNow.UtcTicks;
            foreach (OperationRecord operation in services.Database.Journal.List(int.MaxValue, batch).Where(o => o.Status == OperationStatus.Done).OrderByDescending(o => o.Id))
            {
                first ??= await runner.UndoAsync(operation.Id, undoBatch, CancellationToken.None).ConfigureAwait(false);
            }

            return first;
        }).ConfigureAwait(true);
        services.ShowToast?.Invoke(failure is null
            ? new ToastContent(Severity.None, Strings.Repair.Undone, string.Empty)
            : new ToastContent(Severity.Critical, Strings.Repair.UndoFailed, failure));
    }

    private static bool Confirm(Form form, IRemediationAction action, IReadOnlyList<FileRecord> files, IReadOnlyList<string> changed, int quarantineDays, bool scanning)
    {
        if (action.Id == ActionIds.AcceptBaseline)
        {
            return Pages.AcceptDialog.Ask(form);
        }

        string things = Things(files);
        long bytes = files.Sum(f => f.Size);
        string? whereOriginalsGo = action.Class switch
        {
            ActionClass.Lossless or ActionClass.Move when action.Id != ActionIds.FixExtension => string.Format(CultureInfo.CurrentCulture, Strings.Repair.OriginalsToQuarantine, quarantineDays),
            ActionClass.Recycle => Strings.Repair.OriginalsToRecycle,
            _ => null,
        };
        Pages.RepairPreview preview = new(
            string.Format(CultureInfo.CurrentCulture, TitleFormat(action.Id), things),
            Paragraph(action.Id),
            KindIcon(files),
            string.Format(CultureInfo.CurrentCulture, Strings.Repair.FactsTotal, things, UiFormat.Size(bytes)),
            UiFormat.About(Estimate(action, bytes, files.Count)),
            whereOriginalsGo,
            action.Class != ActionClass.Recycle,
            changed.Count,
            changed,
            scanning,
            Verb(action.Id));
        return Pages.RepairDialog.Ask(form, preview);
    }

    /// <summary>A rough duration from the bytes involved: copies and remuxes run at disk speed, re-encodes slower.</summary>
    private static TimeSpan Estimate(IRemediationAction action, long bytes, int files)
    {
        double perSecond = action.Id switch
        {
            ActionIds.ShrinkBitDepth or ActionIds.ReencodeFlac or ActionIds.DeclipCopy => 40_000_000,
            ActionIds.Quarantine or ActionIds.FixExtension or ActionIds.Recycle => double.PositiveInfinity,
            _ => 200_000_000,
        };
        return TimeSpan.FromSeconds(Math.Max(1, (bytes / perSecond) + (files * 0.2)));
    }

    private static IconName KindIcon(IReadOnlyList<FileRecord> files) =>
        files.All(f => f.MediaKind == MediaKind.Video) ? IconName.Video
        : files.All(f => f.MediaKind == MediaKind.Photo) ? IconName.Photo
        : files.All(f => f.MediaKind == MediaKind.Audio) ? IconName.Music
        : IconName.Folder;

    private static void Report(UiServices services, IRemediationAction action, IReadOnlyList<OperationOutcome> outcomes, long batch)
    {
        int done = outcomes.Count(o => o.Status == OperationStatus.Done);
        List<OperationOutcome> failed = [.. outcomes.Where(o => o.Status != OperationStatus.Done)];
        if (done > 0)
        {
            string title = string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Result(action.Id), UiFormat.Plural(done, Strings.Repair.OneFile, Strings.Repair.ManyFiles));
            List<string> detail = [];
            if (failed.Count > 0)
            {
                detail.Add(string.Format(CultureInfo.CurrentCulture, Strings.Repair.Skipped, UiFormat.Count(failed.Count)));
            }

            if (action.Class is ActionClass.Lossless || action.Id == ActionIds.Quarantine)
            {
                detail.Add(Strings.Repair.OriginalsInQuarantine);
            }

            bool undoable = action.Class is not (ActionClass.Meta or ActionClass.Recycle);
            services.ShowToast?.Invoke(new ToastContent(Severity.None, title, string.Join(Strings.Separator, detail), undoable ? Strings.Dialogs.Undo : null, undoable ? () => UiTask.Run(UndoBatchAsync(services, batch)) : null));
        }

        if (failed.Count > 0)
        {
            string title = string.Format(CultureInfo.CurrentCulture, Strings.Repair.FailedFor, Verb(action.Id), UiFormat.Plural(failed.Count, Strings.Repair.OneFile, Strings.Repair.ManyFiles));
            services.ShowToast?.Invoke(new ToastContent(Severity.Critical, title, failed[0].Reason ?? string.Empty));
        }
    }

    /// <summary><c>38 videos</c>, <c>1 photo</c>, <c>12 audio files</c>, or <c>files</c> for a mix.</summary>
    private static string Things(IReadOnlyList<FileRecord> files) =>
        files.Count > 0 && files.All(f => f.MediaKind == MediaKind.Video) ? UiFormat.Plural(files.Count, Strings.Repair.OneVideo, Strings.Repair.ManyVideos)
        : files.Count > 0 && files.All(f => f.MediaKind == MediaKind.Photo) ? UiFormat.Plural(files.Count, Strings.Repair.OnePhoto, Strings.Repair.ManyPhotos)
        : files.Count > 0 && files.All(f => f.MediaKind == MediaKind.Audio) ? UiFormat.Plural(files.Count, Strings.Repair.OneAudio, Strings.Repair.ManyAudio)
        : UiFormat.Plural(files.Count, Strings.Repair.OneFile, Strings.Repair.ManyFiles);

    private static string Verb(string actionId) => actionId switch
    {
        ActionIds.RestoreCopy => Strings.Repair.VerbRestore,
        ActionIds.Remux => Strings.Repair.VerbRemux,
        ActionIds.FixExtension => Strings.Repair.VerbRename,
        ActionIds.ShrinkBitDepth => Strings.Repair.VerbShrink,
        ActionIds.ReencodeFlac => Strings.Repair.VerbRewrite,
        ActionIds.Salvage => Strings.Repair.VerbSalvage,
        ActionIds.ExtractPreview => Strings.Repair.VerbPreview,
        ActionIds.DeclipCopy => Strings.Repair.VerbDeclip,
        ActionIds.Recycle => Strings.Repair.VerbRecycle,
        ActionIds.AcceptBaseline => Strings.Dialogs.Accept,
        _ => Strings.Repair.VerbQuarantine,
    };

    private static string Result(string actionId) => actionId switch
    {
        ActionIds.RestoreCopy => Strings.Repair.ResultRestore,
        ActionIds.Remux => Strings.Repair.ResultRemux,
        ActionIds.FixExtension => Strings.Repair.ResultRename,
        ActionIds.ShrinkBitDepth => Strings.Repair.ResultShrink,
        ActionIds.ReencodeFlac => Strings.Repair.ResultRewrite,
        ActionIds.Salvage => Strings.Repair.ResultSalvage,
        ActionIds.ExtractPreview => Strings.Repair.ResultPreview,
        ActionIds.DeclipCopy => Strings.Repair.ResultDeclip,
        ActionIds.Recycle => Strings.Repair.ResultRecycle,
        ActionIds.AcceptBaseline => Strings.Repair.ResultAccept,
        _ => Strings.Repair.ResultQuarantine,
    };

    private static string TitleFormat(string actionId) => actionId switch
    {
        ActionIds.RestoreCopy => Strings.Repair.TitleRestore,
        ActionIds.Remux => Strings.Repair.TitleRemux,
        ActionIds.FixExtension => Strings.Repair.TitleRename,
        ActionIds.ShrinkBitDepth => Strings.Repair.TitleShrink,
        ActionIds.ReencodeFlac => Strings.Repair.TitleRewrite,
        ActionIds.Salvage => Strings.Repair.TitleSalvage,
        ActionIds.ExtractPreview => Strings.Repair.TitlePreview,
        ActionIds.DeclipCopy => Strings.Repair.TitleDeclip,
        ActionIds.Recycle => Strings.Repair.TitleRecycle,
        _ => Strings.Repair.TitleQuarantine,
    };

    private static string Paragraph(string actionId) => actionId switch
    {
        ActionIds.RestoreCopy => Strings.Repair.AboutRestore,
        ActionIds.Remux => Strings.Repair.AboutRemux,
        ActionIds.FixExtension => Strings.Repair.AboutRename,
        ActionIds.ShrinkBitDepth => Strings.Repair.AboutShrink,
        ActionIds.ReencodeFlac => Strings.Repair.AboutRewrite,
        ActionIds.Salvage => Strings.Repair.AboutSalvage,
        ActionIds.ExtractPreview => Strings.Repair.AboutPreview,
        ActionIds.DeclipCopy => Strings.Repair.AboutDeclip,
        ActionIds.Recycle => Strings.Repair.AboutRecycle,
        _ => Strings.Repair.AboutQuarantine,
    };
}
