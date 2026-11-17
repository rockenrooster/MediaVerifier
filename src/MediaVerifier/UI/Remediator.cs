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

        List<FileRecord> files = await Task.Run(() => services.Database.Files.GetMany(fileIds)).ConfigureAwait(true);
        if (!Confirm(form, action, files, services.State.Settings.Scanning.EmptyQuarantineAfterDays, services.Scans.Status.IsActive))
        {
            return;
        }

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

    private static bool Confirm(Form form, IRemediationAction action, IReadOnlyList<FileRecord> files, int quarantineDays, bool scanning)
    {
        if (action.Id == ActionIds.AcceptBaseline)
        {
            return MessageDialog.Confirm(form, Strings.Dialogs.AcceptTitle, Strings.Dialogs.AcceptText, Strings.Kit.Cancel, Strings.Dialogs.AcceptCheck, destructive: false);
        }

        string things = Things(files);
        string title = string.Format(CultureInfo.CurrentCulture, TitleFormat(action.Id), things);
        List<string> lines =
        [
            Paragraph(action.Id),
            string.Empty,
            string.Format(CultureInfo.CurrentCulture, Strings.Repair.WhatAndSize, things, UiFormat.Size(files.Sum(f => f.Size))),
        ];
        if (action.Class is ActionClass.Lossless or ActionClass.Move && action.Id != ActionIds.FixExtension)
        {
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Repair.OriginalsToQuarantine, quarantineDays));
        }

        if (scanning)
        {
            lines.Add(Strings.Repair.PausesScan);
        }

        lines.Add(action.Class == ActionClass.Recycle ? Strings.Repair.CannotUndo : Strings.Repair.CanUndo);
        return MessageDialog.Confirm(form, title, string.Join(Environment.NewLine, lines), Strings.Kit.Cancel, Verb(action.Id), destructive: false);
    }

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
            services.ShowToast?.Invoke(new ToastContent(Severity.None, title, string.Join(Strings.Separator, detail), undoable ? Strings.Dialogs.Undo : null, undoable ? () => _ = UndoBatchAsync(services, batch) : null));
        }

        if (failed.Count > 0)
        {
            string title = string.Format(CultureInfo.CurrentCulture, Strings.Repair.FailedFor, Verb(action.Id), UiFormat.Plural(failed.Count, Strings.Repair.OneFile, Strings.Repair.ManyFiles));
            services.ShowToast?.Invoke(new ToastContent(Severity.Critical, title, failed[0].Reason ?? string.Empty));
        }
    }

    private static string Things(IReadOnlyList<FileRecord> files) => UiFormat.Plural(files.Count, Strings.Repair.OneFile, Strings.Repair.ManyFiles);

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
