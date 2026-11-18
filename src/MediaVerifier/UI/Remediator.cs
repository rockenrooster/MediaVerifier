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
/// time through the operation runner on a pool thread, then a toast with the result and Undo. One batch runs at a
/// time; the start toast's Stop ends it after the file in hand.
/// </summary>
internal static class Remediator
{
    /// <summary>Fixes that do not depend on the file's kind, format or copies, so a rule alone is enough to offer them.</summary>
    private static readonly string[] FileIndependent = [ActionIds.Remux, ActionIds.FixExtension];

    /// <summary>The batch running now (UI thread only); null when none runs.</summary>
    private static CancellationTokenSource? s_running;

    /// <summary>True while a repair batch runs.</summary>
    public static bool IsRunning => s_running is not null;

    /// <summary>
    /// The fix offered for a set of rules when nothing is known about the files (Library's row menu): one that is
    /// right for any file the rule can be raised on (rebuild the index, rename to the right extension), when all share
    /// it. Lists that know their files use <see cref="CommonFixes"/> over the rows' own fixes: the rule's first fix
    /// offered "Save a playable copy" for photos and "Rewrite FLAC" for PNG files.
    /// </summary>
    public static string? CommonFix(IEnumerable<string> ruleIds)
    {
        string? common = null;
        foreach (string rule in ruleIds)
        {
            string? fix = RuleCatalog.TryGet(rule, out RuleDefinition definition)
                ? definition.ActionIds.FirstOrDefault(a => FileIndependent.Contains(a))
                : null;
            if (fix is null || (common is not null && common != fix))
            {
                return null;
            }

            common = fix;
        }

        return common;
    }

    /// <summary>
    /// The fixes valid for every one of the findings (subplan 09: <c>Fix…</c> lists only actions valid for every
    /// selected finding), in the order of the first finding's list; empty when they need different fixes.
    /// </summary>
    public static IReadOnlyList<string> CommonFixes(IEnumerable<IReadOnlyList<string>> fixes)
    {
        List<string>? common = null;
        foreach (IReadOnlyList<string> list in fixes)
        {
            if (common is null)
            {
                common = [.. list];
            }
            else
            {
                common.RemoveAll(a => !list.Contains(a));
            }

            if (common.Count == 0)
            {
                break;
            }
        }

        return common ?? [];
    }

    /// <summary>
    /// Asks, runs and reports; nothing runs on the UI thread. True when files were worked on (false when the dialog was
    /// cancelled or nothing could run), so the page closes the drawer only then. The shell's counts and the page reload
    /// once the result is in.
    /// </summary>
    public static async Task<bool> RunAsync(UiServices services, Control owner, IReadOnlyList<long> fileIds, string actionId)
    {
        if (services.Operations is not OperationRunner runner || fileIds.Count == 0 || owner.FindForm() is not Form form
            || ActionCatalog.Create(actionId, WindowsFileSystem.Instance) is not IRemediationAction action)
        {
            return false;
        }

        if (s_running is not null)
        {
            services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Repair.AlreadyRunning, string.Empty));
            return false;
        }

        if (services.BackgroundRunActive())
        {
            // A scheduled scan in another MediaVerifier process cannot be paused from here (subplan 12: the engine
            // must be idle on the folder).
            services.ShowToast?.Invoke(new ToastContent(Severity.Medium, Strings.Shell.ScheduledRunning, Strings.Repair.TryAfterScan));
            return false;
        }

        // From here until the result one batch owns the runner: a second Fix… while the first is asked about or runs
        // is refused above instead of putting the same files through twice.
        using CancellationTokenSource cancel = new();
        s_running = cancel;
        try
        {
            return await AskAndRunAsync(services, runner, form, fileIds, action, cancel).ConfigureAwait(true);
        }
        finally
        {
            s_running = null;
        }
    }

    private static async Task<bool> AskAndRunAsync(UiServices services, OperationRunner runner, Form form, IReadOnlyList<long> fileIds, IRemediationAction action, CancellationTokenSource cancel)
    {
        // Files that changed since the scan are skipped, as the dialog says. They are told apart by id: by name, every
        // other selected cover.jpg was skipped with the one that changed.
        (List<FileRecord> all, HashSet<long> changed) = await Task.Run(() =>
        {
            List<FileRecord> records = services.Database.Files.GetMany(fileIds);
            Dictionary<long, RootRecord> roots = services.Database.Roots.List().ToDictionary(r => r.Id);
            HashSet<long> moved = [];
            foreach (FileRecord file in records)
            {
                if (action.Class != ActionClass.Meta && roots.TryGetValue(file.RootId, out RootRecord? root)
                    && WindowsFileSystem.Instance.Stat(Core.Integrity.HealthyCopyFinder.FullPath(root, file)) is FileStat stat
                    && (stat.Size != file.Size || stat.LastWriteUtcTicks != file.MtimeUtc))
                {
                    moved.Add(file.Id);
                }
            }

            return (records, moved);
        }).ConfigureAwait(true);
        List<FileRecord> files = [.. all.Where(f => !changed.Contains(f.Id))];
        if (all.Count == 0 || !Confirm(form, action, all, [.. all.Where(f => changed.Contains(f.Id)).Select(f => f.Name)], files.Count == 0, services))
        {
            return false;
        }

        if (files.Count == 0)
        {
            services.ShowToast?.Invoke(new ToastContent(Severity.Medium, string.Format(CultureInfo.CurrentCulture, Strings.Repair.FailedFor, Verb(action.Id), Things(all)), Core.CoreStrings.Remediation.ChangedSinceScan));
            return false;
        }

        long batch = services.Clock.UtcNow.UtcTicks;
        CancellationToken token = cancel.Token;

        // A batch that takes a while (the dialog said "About …"), or waits for a scan to pause first, says that it
        // started, with Stop: the file in hand is stopped and put back, the rest are not started. A quick one only
        // shows its result.
        if (Estimate(action, files.Sum(f => f.Size), files.Count) >= TimeSpan.FromSeconds(5) || services.Scans.Status.IsActive)
        {
            services.ShowToast?.Invoke(new ToastContent(
                Severity.None,
                string.Format(CultureInfo.CurrentCulture, Strings.Repair.StartedFor, Verb(action.Id), Things(files)),
                string.Empty,
                Strings.Shell.Stop,
                () =>
                {
                    if (ReferenceEquals(s_running, cancel))
                    {
                        cancel.Cancel();
                    }
                }));
        }

        List<OperationOutcome> outcomes;
        bool paused = false;
        try
        {
            paused = await PauseScanAsync(services).ConfigureAwait(true);
            outcomes = await Task.Run(async () =>
            {
                List<OperationOutcome> done = [];
                foreach (FileRecord file in files)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        done.Add(await runner.RunAsync(file.Id, action, batch, token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                return done;
            }, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            if (paused)
            {
                services.Scans.Resume();
            }
        }

        Report(services, form, action, files, outcomes, batch);
        RefreshShell(form);
        return true;
    }

    /// <summary>Ctrl+Z: undoes the newest batch that still has an undoable operation; says so when there is none.</summary>
    public static async Task UndoLatestAsync(UiServices services, Control? owner = null)
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
            await UndoBatchAsync(services, id, owner).ConfigureAwait(true);
        }
        else
        {
            services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Repair.NothingToUndo, string.Empty));
        }
    }

    /// <summary>
    /// Undoes every done operation of a batch (newest first) and reports. Every one is tried: one that fails (a file
    /// open in a player) no longer stops the rest. A running scan pauses meanwhile, as for a repair.
    /// </summary>
    public static Task UndoBatchAsync(UiServices services, long batch, Control? owner = null) =>
        UndoAsync(services, () => [.. services.Database.Journal.List(int.MaxValue, batch).Where(o => o.Status == OperationStatus.Done).OrderByDescending(o => o.Id).Select(o => o.Id)], owner);

    /// <summary>
    /// Undoes the given operations in order (Tools, Quarantine: Restore), as <see cref="UndoBatchAsync"/> does: every
    /// one is tried, a running scan pauses, the result is one toast (two when some failed) and the shell refreshes.
    /// </summary>
    public static Task UndoOperationsAsync(UiServices services, IReadOnlyList<long> operationIds, Control? owner = null) =>
        UndoAsync(services, () => operationIds, owner);

    /// <param name="operations">The operations to undo, read on a pool thread.</param>
    private static async Task UndoAsync(UiServices services, Func<IReadOnlyList<long>> operations, Control? owner)
    {
        if (services.Operations is not OperationRunner runner)
        {
            return;
        }

        // An undo swaps files back as a repair does: one at a time, never beside a running batch.
        if (s_running is not null)
        {
            services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Repair.AlreadyRunning, string.Empty));
            return;
        }

        using CancellationTokenSource busy = new();
        s_running = busy;
        bool paused = false;
        (int undone, int failed, string? reason) result;
        try
        {
            paused = await PauseScanAsync(services).ConfigureAwait(true);
            result = await Task.Run(async () =>
            {
                int undone = 0;
                int failed = 0;
                string? first = null;
                long undoBatch = services.Clock.UtcNow.UtcTicks;
                foreach (long operation in operations())
                {
                    string? failure = await runner.UndoAsync(operation, undoBatch, CancellationToken.None).ConfigureAwait(false);
                    if (failure is null)
                    {
                        undone++;
                    }
                    else
                    {
                        failed++;
                        first ??= failure;
                    }
                }

                return (undone, failed, first);
            }).ConfigureAwait(true);
        }
        finally
        {
            if (paused)
            {
                services.Scans.Resume();
            }

            s_running = null;
        }

        if (result.undone == 0 && result.failed == 0)
        {
            // Undone already (from the toast, then again from Tools or Ctrl+Z).
            services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Repair.NothingToUndo, string.Empty));
            return;
        }

        if (result.failed == 0)
        {
            services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Repair.Undone, string.Empty));
        }
        else
        {
            if (result.undone > 0)
            {
                services.ShowToast?.Invoke(new ToastContent(Severity.None, string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Strings.Repair.Undone, UiFormat.Plural(result.undone, Strings.Repair.OneFile, Strings.Repair.ManyFiles)), string.Empty));
            }

            services.ShowToast?.Invoke(new ToastContent(Severity.Critical, string.Format(CultureInfo.CurrentCulture, Strings.Repair.FailedFor, Strings.Dialogs.Undo, UiFormat.Plural(result.failed, Strings.Repair.OneFile, Strings.Repair.ManyFiles)), result.reason ?? string.Empty));
        }

        RefreshShell(owner);
    }

    /// <summary>
    /// An operation needs the engine idle (subplan 12, Interaction with scans): a running scan is paused, and the files
    /// it has in hand finish first, so nothing reads a file while it is swapped. True when this call paused the scan and
    /// must resume it; a scan the user paused stays paused.
    /// </summary>
    private static async Task<bool> PauseScanAsync(UiServices services)
    {
        EngineStatus status = services.Scans.Status;
        bool pausedHere = status.IsActive && status.Phase is not (EnginePhase.Paused or EnginePhase.Pausing);
        if (pausedHere)
        {
            services.Scans.Pause();
        }

        for (int i = 0; i < 150 && services.Scans.Status is { IsActive: true, Phase: not EnginePhase.Paused }; i++)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }

        return pausedHere;
    }

    /// <summary>Reloads the shell's counts (the Issues badge) and the page shown, after files changed.</summary>
    /// <returns>False without a main window to refresh (the caller reloads its own view).</returns>
    internal static bool RefreshShell(Control? owner)
    {
        MainForm? main = owner?.FindForm() as MainForm ?? Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main is { IsDisposed: false })
        {
            main.RefreshAll();
            return true;
        }

        return false;
    }

    private static bool Confirm(Form form, IRemediationAction action, IReadOnlyList<FileRecord> files, IReadOnlyList<string> changed, bool nothingToRun, UiServices services)
    {
        if (action.Id == ActionIds.AcceptBaseline)
        {
            return Pages.AcceptDialog.Ask(form);
        }

        string things = Things(files);
        long bytes = files.Sum(f => f.Size);
        string? whereOriginalsGo = action.Class switch
        {
            ActionClass.Lossless or ActionClass.Move when action.Id != ActionIds.FixExtension => string.Format(CultureInfo.CurrentCulture, Strings.Repair.OriginalsToQuarantine, services.State.Settings.Scanning.EmptyQuarantineAfterDays),
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
            services.Scans.Status.IsActive,
            Verb(action.Id),
            nothingToRun);
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

    /// <summary>
    /// The result toasts (subplan 09, D): <c>{Result} for {n} {things}</c> with Undo, and <c>{Action} failed for {n}
    /// {things}</c> with the first reason and Show, which lists every file that failed and why.
    /// </summary>
    internal static void Report(UiServices services, Form owner, IRemediationAction action, IReadOnlyList<FileRecord> files, IReadOnlyList<OperationOutcome> outcomes, long batch)
    {
        Dictionary<long, FileRecord> byId = files.ToDictionary(f => f.Id);
        List<FileRecord> done = [.. outcomes.Where(o => o.Status == OperationStatus.Done).Select(o => byId.GetValueOrDefault(o.FileId)).OfType<FileRecord>()];

        // Stopped files (the one in hand when Stop was pressed, and those not started) are skipped, not failed.
        List<OperationOutcome> failed = [.. outcomes.Where(o => o.Status is not (OperationStatus.Done or OperationStatus.Cancelled))];
        int skipped = files.Count - done.Count;
        if (done.Count > 0)
        {
            string title = string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Result(action.Id), Things(done));
            List<string> detail = [];
            if (skipped > 0)
            {
                detail.Add(string.Format(CultureInfo.CurrentCulture, Strings.Repair.Skipped, UiFormat.Count(skipped)));
            }

            if (action.Class is ActionClass.Lossless || action.Id == ActionIds.Quarantine)
            {
                detail.Add(Strings.Repair.OriginalsInQuarantine);
            }

            bool undoable = action.Class is not (ActionClass.Meta or ActionClass.Recycle);
            services.ShowToast?.Invoke(new ToastContent(Severity.None, title, string.Join(Strings.Separator, detail), undoable ? Strings.Dialogs.Undo : null, undoable ? () => UiTask.Run(UndoBatchAsync(services, batch, owner)) : null));
        }

        if (failed.Count > 0)
        {
            List<FileRecord> failedFiles = [.. failed.Select(o => byId.GetValueOrDefault(o.FileId)).OfType<FileRecord>()];
            string title = string.Format(CultureInfo.CurrentCulture, Strings.Repair.FailedFor, Verb(action.Id), failedFiles.Count == failed.Count ? Things(failedFiles) : UiFormat.Plural(failed.Count, Strings.Repair.OneFile, Strings.Repair.ManyFiles));
            string list = string.Join(Environment.NewLine, failed.Take(20).Select(o => (byId.GetValueOrDefault(o.FileId)?.Name ?? string.Empty) + Strings.Separator + (o.Reason ?? string.Empty)));
            services.ShowToast?.Invoke(new ToastContent(Severity.Critical, title, failed[0].Reason ?? string.Empty, Strings.Dialogs.Show, () =>
            {
                if (!owner.IsDisposed)
                {
                    MessageDialog.Show(owner, title, list, Strings.Kit.Close);
                }
            }));
        }
        else if (done.Count == 0)
        {
            // Stopped before the first file finished.
            services.ShowToast?.Invoke(new ToastContent(Severity.None, string.Format(CultureInfo.CurrentCulture, Strings.Repair.Skipped, Things(files)), string.Empty));
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
