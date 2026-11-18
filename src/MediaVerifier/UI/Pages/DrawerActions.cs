using System.Globalization;
using System.Runtime.InteropServices;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// The detail drawer's buttons, the same on every page that shows it (subplan 09, Detail drawer): the recommended fix,
/// Show in folder, the More menu (every other fix, Open file, Scan again, Copy path, Move to quarantine), each
/// finding's Ignore and Scan again, Copy details and the breadcrumb's links to Library → Browse. Issues and Library
/// wire their drawer through this, so none of its buttons is left without a handler on one page.
/// </summary>
internal sealed class DrawerActions
{
    private readonly UiServices _services;
    private readonly FileDetailView _view;
    private readonly Func<IReadOnlyList<long>, string, Task> _runFix;
    private readonly Action<long> _findingIgnored;

    /// <param name="runFix">Runs a fix on files (the confirmation first) and refreshes the page.</param>
    /// <param name="findingIgnored">Refreshes the page and the drawer's file after one of its findings was ignored.</param>
    public DrawerActions(UiServices services, FileDetailView view, Func<IReadOnlyList<long>, string, Task> runFix, Action<long> findingIgnored)
    {
        _services = services;
        _view = view;
        _runFix = runFix;
        _findingIgnored = findingIgnored;
        view.ShowInFolderRequested += (_, _) => ShellActions.ShowInFolder(view.Detail?.FullPath);
        view.PrimaryRequested += (_, _) =>
        {
            if (view.Detail is { RecommendedActionId.Length: > 0 } shown)
            {
                UiTask.Run(runFix([shown.FileId], shown.RecommendedActionId));
            }
        };
        view.MoreRequested += (_, screen) => ShowMenu(screen);
        view.FindingAction += (_, action) => UiTask.Run(OnFindingActionAsync(action.FindingId, action.Ignore));
        view.CopyRequested += (_, text) => Copy(services, [text], Strings.Drawer.DetailsCopied);
        view.CrumbClicked += (_, index) => UiTask.Run(BrowseFolderAsync(index));
    }

    /// <summary>
    /// Scan again: through the services' start, which shows the strip at once or says why nothing started. Each file's
    /// folder is the scope, not its whole watched folder, and the files are read again: a scan at the depth they
    /// already have otherwise found nothing to do for an unchanged file.
    /// </summary>
    public static async Task RescanAsync(UiServices services, IReadOnlyList<long> fileIds)
    {
        if (fileIds.Count == 0 || services.IsDemo)
        {
            return;
        }

        List<FileRecord> files = await Task.Run(() => services.Database.Files.GetMany([.. fileIds])).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return;
        }

        ScanScope[] scopes = [.. files.GroupBy(f => (f.RootId, RelDir: f.RelDir ?? string.Empty)).Select(g => new ScanScope(g.Key.RootId, g.Key.RelDir.Length == 0 ? null : g.Key.RelDir, [.. g.Select(f => f.Id)]))];
        ScanDepth depth = (ScanDepth)Math.Max((int)ScanDepth.Standard, files.Max(f => (int)f.ScannedDepth));
        string scope = string.Format(CultureInfo.CurrentCulture, Strings.Scans.FilesScope, UiFormat.Count(files.Count));
        _ = await services.StartScanAsync(scopes, depth, scope, new ScanOptions { VerifyEverything = true }).ConfigureAwait(true);
    }

    /// <summary>
    /// The success toast after Ignore (subplan 09, D): <c>Ignored for {n} problems</c> with Undo, which shows them
    /// again. Before, an ignored finding could not be brought back.
    /// </summary>
    public static void ShowIgnored(UiServices services, IReadOnlyCollection<long> findingIds, Action reload) =>
        services.ShowToast?.Invoke(new ToastContent(
            Severity.None,
            string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Strings.Issues.Ignored, UiFormat.Plural(findingIds.Count, Strings.Dashboard.OneProblem, Strings.Dashboard.Problems)),
            string.Empty,
            Strings.Dialogs.Undo,
            () => UiTask.Run(UndoAsync(services, () => services.Database.Findings.ReopenAsync(findingIds), reload))));

    /// <summary>After Stop ignoring: <c>Shown again for {n} problems</c> (a lifted folder ignore can bring back more than were chosen).</summary>
    public static void ShowReopened(UiServices services, int reopened) =>
        services.ShowToast?.Invoke(new ToastContent(
            Severity.None,
            string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Strings.Issues.ShownAgain, UiFormat.Plural(reopened, Strings.Dashboard.OneProblem, Strings.Dashboard.Problems)),
            string.Empty));

    /// <summary>Runs an Undo, refreshes the page and says it is done.</summary>
    public static async Task UndoAsync(UiServices services, Func<Task> undo, Action reload)
    {
        await undo().ConfigureAwait(true);
        reload();
        services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Repair.Undone, string.Empty));
    }

    /// <summary>
    /// Copies lines to the clipboard through the shell's copy and says so in one line, <paramref name="copied"/> (Copy
    /// path seemed to do nothing). While another program holds the clipboard Windows refuses: that is said in a toast
    /// instead of the error dialog.
    /// </summary>
    public static void Copy(UiServices services, IReadOnlyList<string> lines, string copied)
    {
        if (!lines.Any(line => line.Length > 0))
        {
            return;
        }

        try
        {
            ShellActions.CopyText(lines);
            services.ShowToast?.Invoke(new ToastContent(Severity.None, copied, string.Empty));
        }
        catch (ExternalException)
        {
            services.ShowToast?.Invoke(new ToastContent(Severity.Medium, Strings.Drawer.CopyFailed, string.Empty));
        }
    }

    /// <summary>Copy path's confirmation: <c>Path copied</c>, or <c>{n} paths copied</c>.</summary>
    public static string PathsCopied(int count) =>
        count == 1 ? Strings.Drawer.PathCopied : string.Format(CultureInfo.CurrentCulture, Strings.Drawer.PathsCopied, UiFormat.Count(count));

    /// <summary>
    /// Open file: the file in the program Windows has for it. A file that is gone (deleted or moved since the scan, or
    /// on a drive that is not connected) says so; Open file did nothing at all. The check runs off the UI thread.
    /// </summary>
    public static async Task OpenFileAsync(UiServices services, string? path)
    {
        if (string.IsNullOrEmpty(path) || services.IsDemo)
        {
            return;
        }

        if (await Task.Run(() => File.Exists(path)).ConfigureAwait(true))
        {
            ShellActions.OpenFile(path);
        }
        else
        {
            services.ShowToast?.Invoke(new ToastContent(Severity.Medium, Strings.Drawer.OpenFailed, Core.CoreStrings.Remediation.Missing));
        }
    }

    private async Task OnFindingActionAsync(long findingId, bool ignore)
    {
        if (_services.IsDemo || _view.Detail is not FileDetail shown)
        {
            return;
        }

        if (!ignore)
        {
            await RescanAsync(_services, [shown.FileId]).ConfigureAwait(true);
            return;
        }

        if (shown.Findings.FirstOrDefault(f => f.Id == findingId) is { Status: FindingStatus.Ignored })
        {
            int reopened = await _services.Database.Findings.ReopenAsync([findingId]).ConfigureAwait(true);
            _findingIgnored(shown.FileId);
            ShowReopened(_services, reopened);
            return;
        }

        await _services.Database.Findings.SetStatusAsync([findingId], FindingStatus.Ignored).ConfigureAwait(true);
        _findingIgnored(shown.FileId);
        ShowIgnored(_services, [findingId], () => _findingIgnored(shown.FileId));
    }

    /// <summary>
    /// A breadcrumb segment's link (subplan 09, Detail drawer): Library → Browse at that folder; segments of the watched
    /// folder's own path open the watched folder.
    /// </summary>
    private async Task BrowseFolderAsync(int index)
    {
        if (_view.Detail is not FileDetail shown || shown.RootId == 0 || _services.Navigate is not { } navigate)
        {
            return;
        }

        string[] parts = shown.RelDir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        int depth = index + 1 - shown.RootSegments;
        long? dirId = null;
        if (depth >= parts.Length)
        {
            dirId = parts.Length == 0 ? null : shown.DirId;
        }
        else if (depth > 0)
        {
            string relPath = string.Join('\\', parts.Take(depth));
            dirId = await Task.Run(() => FindFolder(_services.Database.Queries.FolderRollup(shown.RootId), relPath)?.Id).ConfigureAwait(true);
        }

        navigate(PageId.Library, new LibraryBrowseRequest(shown.RootId, dirId));
    }

    private static FolderNode? FindFolder(IEnumerable<FolderNode> nodes, string relPath)
    {
        foreach (FolderNode node in nodes)
        {
            if (string.Equals(node.RelPath, relPath, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (FindFolder(node.Children, relPath) is FolderNode found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The More menu: every fix the app can run for the file's findings (the primary button's aside), then the file's actions.</summary>
    private void ShowMenu(Point screen)
    {
        if (_view.Detail is not FileDetail shown)
        {
            return;
        }

        bool live = _services.Operations is not null;
        List<PopupItem> items = [];
        foreach (string fix in shown.FixActionIds ?? [])
        {
            if (fix != shown.RecommendedActionId)
            {
                items.Add(new(LiveUiData.ActionName(fix), IconName.Tools, Enabled: live, Tag: fix));
            }
        }

        if (items.Count > 0)
        {
            items.Add(PopupItem.Separator);
        }

        items.AddRange(
        [
            new(Strings.Menu.OpenFile, IconName.Open, Tag: "open"),
            new(Strings.Menu.ScanAgain, IconName.Refresh, Tag: "rescan"),
            new(Strings.Menu.CopyPath, IconName.Copy, Tag: "copy"),
            PopupItem.Separator,
            new(Strings.Menu.MoveToQuarantine, IconName.Quarantine, Enabled: live, Tag: "quarantine"),
        ]);
        KitMenu.Show(_view, screen, items, item =>
        {
            switch (item.Tag as string)
            {
                case "open":
                    UiTask.Run(OpenFileAsync(_services, shown.FullPath));
                    break;
                case "rescan":
                    UiTask.Run(RescanAsync(_services, [shown.FileId]));
                    break;
                case "copy":
                    Copy(_services, [shown.FullPath], PathsCopied(1));
                    break;
                case "quarantine":
                    UiTask.Run(_runFix([shown.FileId], ActionIds.Quarantine));
                    break;
                case string fix when fix.StartsWith("ACT-", StringComparison.Ordinal):
                    UiTask.Run(_runFix([shown.FileId], fix));
                    break;
            }
        });
    }
}
