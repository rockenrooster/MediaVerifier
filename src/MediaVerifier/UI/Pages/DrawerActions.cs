using System.Globalization;
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
/// Show in folder, the More menu (every other fix, Open file, Scan again, Copy path, Move to quarantine) and each
/// finding's Ignore and Scan again. Issues and Library wire their drawer through this, so none of its buttons is
/// left without a handler on one page.
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
    }

    /// <summary>Scan again: through the services' start, which shows the strip at once or says why nothing started.</summary>
    public static async Task RescanAsync(UiServices services, IReadOnlyList<long> fileIds)
    {
        if (fileIds.Count == 0 || services.IsDemo)
        {
            return;
        }

        List<FileRecord> files = await Task.Run(() => services.Database.Files.GetMany([.. fileIds])).ConfigureAwait(true);
        ScanScope[] scopes = [.. files.GroupBy(f => f.RootId).Select(g => new ScanScope(g.Key, null, [.. g.Select(f => f.Id)]))];
        string scope = string.Format(CultureInfo.CurrentCulture, Strings.Scans.FilesScope, UiFormat.Count(fileIds.Count));
        _ = await services.StartScanAsync(scopes, ScanDepth.Standard, scope).ConfigureAwait(true);
    }

    private async Task OnFindingActionAsync(long findingId, bool ignore)
    {
        if (_services.IsDemo || _view.Detail is not FileDetail shown)
        {
            return;
        }

        if (ignore)
        {
            await _services.Database.Findings.SetStatusAsync([findingId], FindingStatus.Ignored).ConfigureAwait(true);
            _findingIgnored(shown.FileId);
        }
        else
        {
            await RescanAsync(_services, [shown.FileId]).ConfigureAwait(true);
        }
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
                    ShellActions.OpenFile(shown.FullPath);
                    break;
                case "rescan":
                    UiTask.Run(RescanAsync(_services, [shown.FileId]));
                    break;
                case "copy":
                    ShellActions.CopyText([shown.FullPath]);
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
