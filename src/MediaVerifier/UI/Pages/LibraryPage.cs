using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>A request to show Library → Browse at a folder: a folder card sends it, for its watched folder's top folder.</summary>
internal sealed record LibraryBrowseRequest(long RootId, long? DirId);

/// <summary>A request to open the Add folder dialog, optionally with a folder chosen (drop, Ctrl+N).</summary>
internal sealed record AddFolderRequest(string? Path);

/// <summary>
/// Library (subplan 09, Library; task UI-06): watched folders as cards with the Add folder dialog, and
/// Browse with the folder tree and the file table. A disconnected folder shows as not connected, never
/// as missing files.
/// </summary>
internal sealed class LibraryPage : PageBase
{
    // Owner request: a Problem column with the worst open finding; the name takes the width that is left. A narrow
    // table (small window, open drawer) hides the dates first, then Format and Size, and Problem last.
    private static readonly ListColumn[] TableColumns =
    [
        new(Strings.Library.ColumnName, 0, MinWidth: 160),
        new(Strings.Library.ColumnProblem, 170, Drop: 5),
        new(Strings.Library.ColumnFormat, 130, Sortable: false, Drop: 3),
        new(Strings.Library.ColumnSize, 64, RightAligned: true, Drop: 4),
        new(Strings.Library.ColumnScanned, 96, Drop: 2),
        new(Strings.Library.ColumnVerified, 100, Drop: 1),
    ];

    private static readonly ScanOptions VerifyOptions = new() { VerifyOnly = true, VerifyEverything = true };

    private readonly SegmentedControl _view = new() { TabIndex = 0 };
    private readonly KitButton _add = new() { Kind = ButtonKind.Secondary, Icon = IconName.FolderAdd, Text = Strings.Library.AddFolder, TabIndex = 1 };
    private readonly List<FolderCardControl> _cards = [];
    private readonly DropArea _drop = new() { TabIndex = 99 };
    private readonly Breadcrumb _crumbs = new() { TabIndex = 2 };
    private readonly Chip[] _chips =
    [
        new() { Text = Strings.Library.ChipAll, On = true, TabIndex = 3 },
        new() { Text = Strings.Library.ChipOk, TabIndex = 4 },
        new() { Text = Strings.Library.ChipProblems, TabIndex = 5 },
    ];

    private readonly Card _treeCard = new() { TabIndex = 6 };
    private readonly TreeList _tree = new();
    private readonly Card _tableCard = new() { TabIndex = 7 };
    private readonly VirtualList _table = new() { Mode = ListMode.Table };
    private readonly EmptyState _empty = new() { Small = true, Visible = false };
    private readonly FileSource _source;
    private readonly Dictionary<long, FileRow> _rows = [];

    /// <summary>Per loaded row, the text of each cell (index = column), built once: painting allocates nothing.</summary>
    private readonly Dictionary<long, string?[]> _cells = [];
    private readonly FileDetailView _detail = new();
    private readonly DrawerActions _drawer;
    private readonly ScrollPanel _detailScroll;
    private LibraryData? _library;
    private BrowseData? _browse;
    private BrowseQuery _query = new(null, null);
    private bool _browsing;
    private bool _loadingRows;
    private bool _ignoreTreeSelection;

    /// <summary>The newest load started; an older one that finishes after it is not shown.</summary>
    private int _loadGeneration;

    public LibraryPage(UiServices services)
        : base(services)
    {
        _source = new FileSource(this);
        _detailScroll = new ScrollPanel(_detail);
        _detail.CloseRequested += (_, _) => Services.ShowDrawer?.Invoke(null);
        _detail.EvidenceLoader = (detail, token) => Services.Data.LoadAudioEvidence(detail, token);
        _detail.ThumbnailLoader = (detail, token) => Services.Data.LoadThumbnail(detail, token);
        _detail.StripLoader = (detail, finding, token) => Services.Data.LoadFrameStrip(detail, finding, token);
        _detail.CompareLoader = (detail, finding, time, token) => Services.Data.LoadCompare(detail, finding, time, token);
        _drawer = new DrawerActions(Services, _detail, RunAndReloadAsync, fileId =>
        {
            Reload();
            UiTask.Run(LoadDetailAsync(fileId));
        });
        _view.Segments.Add(Strings.Library.Folders);
        _view.Segments.Add(Strings.Library.Browse);
        _view.SelectedIndex = 0;
        _view.AccessibleName = PageInfo.Title(PageId.Library);
        _view.SelectedIndexChanged += (_, _) => SetBrowsing(_view.SelectedIndex == 1);
        _add.Click += (_, _) => AddFolder(null);
        _drop.FoldersDropped += (_, paths) => AddFolders(paths);
        _drop.Click += (_, e) =>
        {
            if (IsPrimaryClick(e))
            {
                AddFolder(null);
            }
        };
        _crumbs.SegmentClicked += (_, index) => BrowseCrumb(index);
        for (int i = 0; i < _chips.Length; i++)
        {
            BrowseState state = (BrowseState)i;
            _chips[i].Toggled += (_, _) => SetState(state);
        }

        _treeCard.Controls.Add(_tree);
        _tree.SelectionChanged += (_, _) => OnTreeSelection();
        _tree.ContextMenuRequested += (_, request) => ShowTreeMenu(request.Screen);
        _tableCard.Controls.Add(_table);
        _tableCard.Controls.Add(_empty);
        _empty.BringToFront();
        _table.Columns = TableColumns;
        _table.Source = _source;
        _table.SetSort(0, false);
        _table.SortChanged += (_, sort) => SetSort(sort.Column, sort.Descending);
        _table.ItemActivated += (_, index) => OpenDetails(index);

        // Owner request: a click shows the details, a double-click opens the file in its program.
        _table.ItemClicked += (_, index) => OpenDetails(index);
        _table.ItemDoubleClicked += (_, index) => ShellActions.OpenFile(RowAt(index) is FileRow row ? PathOf(row) : null);
        _table.MoreRequested += (_, args) => ShowRowMenu(args.Index, args.Screen);
        _table.KeyDown += OnTableKey;
        Controls.AddRange([_view, _add, _drop, _crumbs, .. _chips, _treeCard, _tableCard]);
        SetBrowsing(false);
    }

    public override PageId Id => PageId.Library;

    /// <summary>F1 in Browse: the help of the focused file's worst finding.</summary>
    public override string? HelpRule => _browsing ? RowAt(_table.FocusedIndex)?.WorstRuleId : null;

    internal IReadOnlyDictionary<long, FileRow> Rows => _rows;

    /// <summary>True while the drawer shows a file of this page (Up and Down then move it to the neighbouring file).</summary>
    private bool DrawerShowsFile => _detailScroll.Parent is Drawer { IsOpen: true };

    public override void HandleRequest(object request)
    {
        switch (request)
        {
            case LibraryBrowseRequest browse:
                // The user's sort stays; a folder from outside the page starts at All.
                ShowBrowse(_query with { RootId = browse.RootId, DirId = browse.DirId, Search = null, State = BrowseState.All });
                break;
            case LibrarySearchRequest search:
                // The header's search: Browse lists the matching files of every watched folder.
                ShowBrowse(_query with { RootId = null, DirId = null, Search = string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim(), State = BrowseState.All });
                break;
            case AddFolderRequest add:
                AddFolder(add.Path);
                break;
        }
    }

    public override int MeasureContentHeight(int width)
    {
        if (_browsing || _library is null)
        {
            return 0;
        }

        int columns = width >= S(1000) ? 2 : 1;
        int rows = (_library.Folders.Count + columns - 1) / columns;
        return S(48) + (rows * S(172)) + S(4) + S(64);
    }

    /// <summary>The render harness's library-browse state.</summary>
    internal void ShowDemoBrowse() => ShowBrowse(new BrowseQuery(1, 14));

    /// <summary>Asks for file rows around <paramref name="index"/> (called while painting).</summary>
    internal void RequestRows(int index)
    {
        if (_browse is null || _loadingRows)
        {
            return;
        }

        int start = Math.Max(0, index - 50);
        int end = Math.Min(_browse.Ids.Length, index + 200);
        List<long> missing = [];
        for (int i = start; i < end; i++)
        {
            if (!_rows.ContainsKey(_browse.Ids[i]))
            {
                missing.Add(_browse.Ids[i]);
            }
        }

        if (missing.Count > 0)
        {
            UiTask.Run(LoadRowsAsync(missing));
        }
    }

    protected override object? LoadData()
    {
        // Loads can finish out of order (a quick second click): only the newest one is shown.
        int generation = Interlocked.Increment(ref _loadGeneration);
        if (_browsing)
        {
            BrowseData browse = Services.Data.LoadBrowse(_query);
            IReadOnlyList<FileRow> first = Services.Data.LoadFileRows([.. browse.Ids.Take(200)]);
            return new BrowseLoad(generation, browse, first);
        }

        return new LibraryLoad(generation, Services.Data.LoadLibrary());
    }

    protected override void ApplyData(object data)
    {
        switch (data)
        {
            case LibraryLoad load when load.Generation == Volatile.Read(ref _loadGeneration):
                ApplyLibrary(load.Library);
                break;
            case BrowseLoad load when load.Generation == Volatile.Read(ref _loadGeneration):
                ApplyBrowse(load.Browse, load.First);
                break;
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int width = Width;
        _view.Bounds = new Rectangle(0, 0, S(_view.PreferredLogicalWidth), S(32));
        int addWidth = S(_add.PreferredLogicalWidth);
        _add.Bounds = new Rectangle(width - addWidth, 0, addWidth, S(32));
        if (!_browsing)
        {
            int columns = width >= S(1000) ? 2 : 1;
            int cardWidth = (width - ((columns - 1) * S(12))) / columns;
            int y = S(48);
            for (int i = 0; i < _cards.Count; i++)
            {
                int column = i % columns;
                int row = i / columns;
                _cards[i].Bounds = new Rectangle(column * (cardWidth + S(12)), y + (row * S(172)), cardWidth, S(160));
            }

            int rows = (_cards.Count + columns - 1) / columns;
            _drop.Bounds = new Rectangle(0, y + (rows * S(172)) + S(4), width, S(64));
            return;
        }

        int x = width;
        for (int i = _chips.Length - 1; i >= 0; i--)
        {
            int chipWidth = S(_chips[i].PreferredLogicalWidth);
            x -= chipWidth;
            _chips[i].Bounds = new Rectangle(x, S(2), chipWidth, S(Chip.LogicalHeight));
            x -= S(8);
        }

        // The breadcrumb (from x0 + 180) takes the room the chips leave, so the open drawer narrows it no more than that.
        _crumbs.Bounds = new Rectangle(S(180), S(2), Math.Max(0, x - S(180)), S(30));
        int top = S(48);
        int height = Math.Max(S(120), Height - top);
        // The tree gives up width (280 down to 180) to a narrow table: a small window or the open drawer.
        int treeWidth = Math.Clamp(width - S(372), S(180), S(280));
        _treeCard.Bounds = new Rectangle(0, top, treeWidth, height);
        _tree.Bounds = Rectangle.Inflate(new Rectangle(Point.Empty, _treeCard.Size), -S(1), -S(8));
        _tableCard.Bounds = new Rectangle(treeWidth + S(12), top, Math.Max(S(200), width - treeWidth - S(12)), height);
        _table.Bounds = Rectangle.Inflate(new Rectangle(Point.Empty, _tableCard.Size), -S(1), -S(1));

        // An empty list's message sits under the column headers, which stay.
        _empty.Bounds = Rectangle.FromLTRB(S(1), S(1) + S(32), Math.Max(S(1), _tableCard.Width - S(1)), Math.Max(S(1) + S(32), _tableCard.Height - S(1)));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // The tree's shortcuts (subplan 09, Keyboard), taken before the tree reads Alt+Enter as Enter (expand).
        if (_tree.Focused && _tree.SelectedNode?.Tag is BrowseFolder folder)
        {
            switch (keyData)
            {
                case Keys.Shift | Keys.F5:
                    ScanTreeFolder(folder, verify: false);
                    return true;
                case Keys.Alt | Keys.Enter:
                    ShellActions.OpenFolder(FolderPath(folder));
                    return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool IsPrimaryClick(EventArgs e) => e is not MouseEventArgs { Button: not MouseButtons.Left };

    /// <summary>Shows Browse with <paramref name="query"/>: switching the view loads it, otherwise it loads here.</summary>
    private void ShowBrowse(BrowseQuery query)
    {
        _query = query;
        bool switching = _view.SelectedIndex != 1;
        _view.SelectedIndex = 1;
        if (!switching || !IsHandleCreated)
        {
            Reload();
        }
    }

    private void SetBrowsing(bool browsing)
    {
        _browsing = browsing;
        _add.Visible = !browsing;
        _drop.Visible = !browsing;
        foreach (FolderCardControl card in _cards)
        {
            card.Visible = !browsing;
        }

        _crumbs.Visible = browsing;
        foreach (Chip chip in _chips)
        {
            chip.Visible = browsing;
        }

        _treeCard.Visible = browsing;
        _tableCard.Visible = browsing;
        if (IsHandleCreated)
        {
            Reload();
        }

        PerformLayout();
        Invalidate();
    }

    private void ApplyLibrary(LibraryData library)
    {
        _library = library;
        while (_cards.Count > library.Folders.Count)
        {
            FolderCardControl last = _cards[^1];
            _cards.RemoveAt(_cards.Count - 1);
            Controls.Remove(last);
            last.Dispose();
        }

        while (_cards.Count < library.Folders.Count)
        {
            FolderCardControl card = new(this) { TabIndex = 10 + _cards.Count, Visible = !_browsing };
            _cards.Add(card);
            Controls.Add(card);
        }

        for (int i = 0; i < library.Folders.Count; i++)
        {
            _cards[i].Bind(library.Folders[i]);
        }
    }

    private void ApplyBrowse(BrowseData browse, IReadOnlyList<FileRow> first)
    {
        BrowseQuery? before = _browse?.Query;

        // A folder the data chose (the first visit, a card) also opens, so its subfolders show under it.
        bool chosenForUser = _query.DirId is null && _query.Search is null && browse.Query.DirId is not null;
        _browse = browse;
        _query = browse.Query;
        _rows.Clear();
        _cells.Clear();
        foreach (FileRow row in first)
        {
            Store(row);
        }

        RebuildTree(browse.Tree, browse.Query.DirId, chosenForUser);
        _crumbs.SetSegments(browse.Crumbs);
        _chips[0].Count = browse.Counts.All;
        _chips[1].Count = browse.Counts.Ok;
        _chips[2].Count = browse.Counts.Problems;
        for (int i = 0; i < _chips.Length; i++)
        {
            _chips[i].On = (int)browse.Query.State == i;
        }

        _source.Reset(browse.Ids.Length);
        _table.Source = _source;
        if (before is null || before.RootId != browse.Query.RootId || before.DirId != browse.Query.DirId || before.Search != browse.Query.Search || before.State != browse.Query.State)
        {
            // Another folder, search or chip: the list starts at its top, and the old list's selection (the list keeps
            // it by id) cannot reach files that are no longer shown. A new sort or a refresh keeps both.
            _table.ClearSelection();
            _table.Refresh(keepScroll: false);
            _table.FocusRow(0, select: false);
        }

        ShowEmptyState(browse);
        _table.Invalidate();
        PerformLayout();
    }

    /// <summary>A one-line message in the table when it has no rows (otherwise Browse looks broken).</summary>
    private void ShowEmptyState(BrowseData browse)
    {
        _empty.Visible = browse.Ids.Length == 0;
        if (_empty.Visible)
        {
            (_empty.Icon, _empty.Headline) = EmptyMessage(browse);
            _empty.Body = string.Empty;
            _empty.Invalidate();
        }
    }

    /// <summary>Why the table is empty: a chip leaves out the files there are, no folder, no name matches, no scan yet, or no files.</summary>
    internal static (IconName Icon, string Headline) EmptyMessage(BrowseData browse) => browse switch
    {
        { Query.State: not BrowseState.All, Counts.All: > 0 } => (IconName.Filter, Strings.Issues.FilteredHeadline),
        { RootPaths.Count: 0 } => (IconName.FolderAdd, Strings.Issues.NoFoldersHeadline),
        { Query.Search: string text } => (IconName.Search, string.Format(CultureInfo.CurrentCulture, Strings.Library.NoSearchResults, text.Trim())),
        { NotScanned: true } => (IconName.Scans, Strings.Library.NotScannedYet),
        _ => (IconName.Folder, Strings.Library.NoFilesHere),
    };

    /// <summary>
    /// The tree of a new snapshot. Folders the user opened stay open (the old items' expansion, by folder id), the path to
    /// the shown folder opens, and the selection is always the shown folder's item, or none.
    /// </summary>
    private void RebuildTree(IReadOnlyList<BrowseFolder> folders, long? selectedDir, bool expandShown)
    {
        HashSet<long> expanded = [];
        foreach (TreeItem root in _tree.Roots)
        {
            CollectExpanded(root, expanded);
        }

        _tree.Roots.Clear();
        TreeItem? selected = null;
        foreach (BrowseFolder folder in folders)
        {
            _tree.Roots.Add(ToItem(folder, selectedDir, expanded, ref selected));
        }

        for (TreeItem? parent = selected?.Parent; parent is not null; parent = parent.Parent)
        {
            parent.Expanded = true;
        }

        if (expandShown && selected is not null)
        {
            selected.Expanded = true;
        }

        _tree.Rebuild();
        _ignoreTreeSelection = true;
        _tree.SelectedNode = selected;
        _ignoreTreeSelection = false;
    }

    private static void CollectExpanded(TreeItem item, HashSet<long> expanded)
    {
        if (item.Expanded && item.Tag is BrowseFolder folder)
        {
            expanded.Add(folder.DirId);
        }

        foreach (TreeItem child in item.LoadedChildren ?? [])
        {
            CollectExpanded(child, expanded);
        }
    }

    private static TreeItem ToItem(BrowseFolder folder, long? selectedDir, HashSet<long> expanded, ref TreeItem? selected)
    {
        TreeItem item = new(folder.Name, folder)
        {
            Severity = folder.Worst == Severity.None ? null : folder.Worst,
            Expanded = expanded.Contains(folder.DirId),
        };
        if (folder.DirId == selectedDir)
        {
            selected = item;
        }

        List<TreeItem> children = [];
        foreach (BrowseFolder child in folder.Children)
        {
            children.Add(ToItem(child, selectedDir, expanded, ref selected));
        }

        item.SetChildren(children);
        return item;
    }

    private void OnTreeSelection()
    {
        if (_ignoreTreeSelection || _tree.SelectedNode?.Tag is not BrowseFolder folder)
        {
            return;
        }

        _query = _query with { RootId = folder.RootId, DirId = folder.DirId, Search = null };
        Reload();
    }

    private void BrowseCrumb(int index)
    {
        // Search results have one heading, not a path: it leads nowhere.
        if (_query.Search is not null)
        {
            return;
        }

        TreeItem? node = _tree.SelectedNode;
        int depth = node?.Depth ?? 0;
        for (int i = depth; i > index && node?.Parent is not null; i--)
        {
            node = node.Parent;
        }

        if (node is not null)
        {
            _tree.SelectedNode = node;
        }
    }

    private void SetState(BrowseState state)
    {
        for (int i = 0; i < _chips.Length; i++)
        {
            _chips[i].On = (int)state == i;
        }

        _query = _query with { State = state };
        Reload();
    }

    private void SetSort(int column, bool descending)
    {
        _query = _query with
        {
            Sort = column switch { 1 => FileSort.Severity, 3 => FileSort.Size, 4 => FileSort.Scanned, 5 => FileSort.Verified, _ => FileSort.Name },
            Descending = descending,
        };
        Reload();
    }

    private async Task LoadRowsAsync(List<long> ids)
    {
        BrowseData? requested = _browse;
        _loadingRows = true;
        try
        {
            IReadOnlyList<FileRow> rows = await Task.Run(() => Services.Data.LoadFileRows(ids)).ConfigureAwait(true);

            // A new snapshot came in meanwhile: these rows may be older than it, and the repaint asks for its own.
            bool current = ReferenceEquals(requested, _browse);
            if (current)
            {
                foreach (FileRow row in rows)
                {
                    Store(row);
                }
            }

            // Ids whose rows are gone (a file moved or removed since the snapshot) come back empty: no repaint, so
            // painting does not ask for them again and again.
            if (rows.Count > 0 || !current)
            {
                _table.Invalidate();
            }
        }
        finally
        {
            _loadingRows = false;
        }
    }

    /// <summary>Keeps a loaded row and the texts of its cells (painting reads them, allocating nothing).</summary>
    private void Store(FileRow row)
    {
        _rows[row.Id] = row;
        DateTimeOffset now = Services.Clock.UtcNow;
        _cells[row.Id] =
        [
            // Search results come from every watched folder: the folder follows the name.
            _query.Search is null ? row.Name : UiFormat.Facts(row.Name, FolderText(row)),
            ProblemText(row),
            UiFormat.FileFormat(row),
            UiFormat.Size(row.Size),
            row.LastScannedUtc is long scanned ? UiFormat.RecentDate(new DateTimeOffset(scanned, TimeSpan.Zero), now) : Strings.Dash,
            row.LastVerifiedUtc is long verified ? UiFormat.RecentDate(new DateTimeOffset(verified, TimeSpan.Zero), now) : Strings.Dash,
        ];
    }

    /// <summary>The worst open finding's title, hedged as in Issues, and how many more the file has.</summary>
    private static string ProblemText(FileRow row)
    {
        if (row.WorstRuleId is null)
        {
            return string.Empty;
        }

        string title = LiveUiData.Title(row.WorstRuleId, row.WorstConfidence);
        return row.OpenFindings > 1 ? string.Format(CultureInfo.CurrentCulture, Strings.Library.ProblemMore, title, UiFormat.Count(row.OpenFindings - 1)) : title;
    }

    /// <summary>A file's folder as the breadcrumb names it: the watched folder's name, then the path below it.</summary>
    private string FolderText(FileRow row)
    {
        string root = _browse?.Tree.FirstOrDefault(t => t.RootId == row.RootId)?.Name ?? string.Empty;
        return row.RelDir.Length == 0 ? root : Path.Combine(root, row.RelDir);
    }

    private long IdAt(int index) => _browse is not null && index >= 0 && index < _browse.Ids.Length ? _browse.Ids[index] : -1;

    private FileRow? RowAt(int index) => _rows.TryGetValue(IdAt(index), out FileRow? row) ? row : null;

    private string? PathOf(FileRow row)
    {
        if (_browse is null || !_browse.RootPaths.TryGetValue(row.RootId, out string? root))
        {
            return null;
        }

        return row.RelDir.Length == 0 ? Path.Combine(root, row.Name) : Path.Combine(root, row.RelDir, row.Name);
    }

    private string? FolderPath(BrowseFolder folder)
    {
        if (_browse is null || !_browse.RootPaths.TryGetValue(folder.RootId, out string? root))
        {
            return null;
        }

        return folder.RelPath.Length == 0 ? root : Path.Combine(root, folder.RelPath);
    }

    /// <summary>The selected files in list order (only those of the list shown).</summary>
    private long[] SelectedIds() => _browse is null || _table.SelectedIds.Count == 0 ? [] : [.. _browse.Ids.Where(_table.SelectedIds.Contains)];

    /// <summary>What the keyboard's Shift+F5 and Ctrl+C act on: the selection, or the focused file when none is selected.</summary>
    internal long[] SelectionOrFocused()
    {
        long[] selected = SelectedIds();
        long focused = IdAt(_table.FocusedIndex);
        return selected.Length > 0 ? selected : focused >= 0 ? [focused] : [];
    }

    /// <summary>
    /// What an action on a row acts on: the selection when the row is part of it (a right-click keeps the selection it
    /// lands on), otherwise the row alone.
    /// </summary>
    internal long[] TargetsOf(int index)
    {
        long id = IdAt(index);
        return id < 0 ? [] : _table.SelectedIds.Contains(id) ? SelectedIds() : [id];
    }

    /// <summary>The rows of <paramref name="ids"/>, in their order; rows not painted yet (Ctrl+A) load off the UI thread.</summary>
    private async Task<List<FileRow>> RowsOfAsync(IReadOnlyList<long> ids)
    {
        Dictionary<long, FileRow> found = [];
        List<long> missing = [];
        foreach (long id in ids)
        {
            if (_rows.TryGetValue(id, out FileRow? row))
            {
                found[id] = row;
            }
            else
            {
                missing.Add(id);
            }
        }

        if (missing.Count > 0)
        {
            foreach (FileRow row in await Task.Run(() => Services.Data.LoadFileRows(missing)).ConfigureAwait(true))
            {
                found[row.Id] = row;
            }
        }

        return [.. ids.Select(id => found.GetValueOrDefault(id)).OfType<FileRow>()];
    }

    /// <summary>The full paths of files, for Copy path.</summary>
    internal async Task<List<string>> PathsOfAsync(IReadOnlyList<long> ids) =>
        [.. (await RowsOfAsync(ids).ConfigureAwait(true)).Select(row => PathOf(row) ?? row.Name)];

    private async Task CopyPathsAsync(IReadOnlyList<long> ids) => ShellActions.CopyText(await PathsOfAsync(ids).ConfigureAwait(true));

    /// <summary>The fix the app can run on files: when their worst findings all share one. Null without operations.</summary>
    private string? FixFor(IReadOnlyList<FileRow> rows)
    {
        if (Services.Operations is null || rows.Count == 0 || rows.Any(r => r.WorstRuleId is null))
        {
            return null;
        }

        return Remediator.CommonFix(rows.Select(r => r.WorstRuleId!));
    }

    /// <summary>Ctrl+Enter: the shared fix of the files (Remediator asks first).</summary>
    private async Task FixAsync(IReadOnlyList<long> ids)
    {
        List<FileRow> rows = await RowsOfAsync(ids).ConfigureAwait(true);
        if (rows.Count == ids.Count && FixFor(rows) is string fix)
        {
            await RunAndReloadAsync(ids, fix).ConfigureAwait(true);
        }
    }

    private void OpenDetails(int index)
    {
        if (RowAt(index) is FileRow row)
        {
            UiTask.Run(LoadDetailAsync(row.Id));
        }
    }

    /// <summary>Runs a fix (Remediator asks first), then closes the drawer and reloads the folder.</summary>
    private async Task RunAndReloadAsync(IReadOnlyList<long> fileIds, string actionId)
    {
        await Remediator.RunAsync(Services, this, fileIds, actionId).ConfigureAwait(true);
        if (!IsDisposed)
        {
            Services.ShowDrawer?.Invoke(null);
            Reload();
        }
    }

    private async Task LoadDetailAsync(long fileId)
    {
        FileDetail? detail = await Task.Run(() => Services.Data.LoadFileDetail(fileId)).ConfigureAwait(true);
        if (detail is null || IsDisposed)
        {
            return;
        }

        _detail.Width = Math.Max(1, Dpi.Scale(Drawer.DefaultWidth, DeviceDpi) - 1);
        _detail.Bind(detail, Services.Clock.UtcNow, detail.RecommendedAction);
        _detailScroll.ScrollToTop();
        Services.ShowDrawer?.Invoke(_detailScroll);
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        if (_detail.Detail is not null)
        {
            Services.ShowDrawer?.Invoke(null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _detailScroll.Parent is null)
        {
            _detailScroll.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowRowMenu(int index, Point screen)
    {
        if (RowAt(index) is not FileRow row)
        {
            return;
        }

        // The selection the row is in (all loaded, as a right-click lands on a painted row), or the row alone.
        long[] targets = TargetsOf(index);
        List<FileRow> loaded = [.. targets.Select(id => _rows.GetValueOrDefault(id)).OfType<FileRow>()];
        string? fix = loaded.Count == targets.Length ? FixFor(loaded) : null;
        List<PopupItem> items =
        [
            new(Strings.Menu.Details, IconName.Info, Strings.Menu.ShortcutEnter, Tag: "details"),
        ];
        if (fix is not null)
        {
            items.Add(new(LiveUiData.ActionName(fix), IconName.Tools, Strings.Menu.ShortcutCtrlEnter, Tag: "fix"));
        }

        items.AddRange(
        [
            new(Strings.Menu.ScanAgain, IconName.Refresh, Strings.Menu.ShortcutShiftF5, Tag: "scan"),
            new(Strings.Menu.VerifyNow, IconName.ShieldCheck, Tag: "verify"),
            PopupItem.Separator,
            new(Strings.Menu.OpenFile, IconName.Open, Tag: "open"),
            new(Strings.Menu.ShowInFolder, IconName.Folder, Strings.Menu.ShortcutAltEnter, Tag: "show"),
            new(Strings.Menu.CopyPath, IconName.Copy, Strings.Menu.ShortcutCtrlC, Tag: "copy"),
        ]);
        KitMenu.Show(_table, screen, items, item =>
        {
            switch (item.Tag)
            {
                case "details":
                    OpenDetails(index);
                    break;
                case "fix" when fix is not null:
                    UiTask.Run(RunAndReloadAsync(targets, fix));
                    break;
                case "open":
                    ShellActions.OpenFile(PathOf(row));
                    break;
                case "scan":
                    UiTask.Run(ScanFilesAsync(targets, verify: false));
                    break;
                case "verify":
                    UiTask.Run(ScanFilesAsync(targets, verify: true));
                    break;
                case "show":
                    ShellActions.ShowInFolder(PathOf(row));
                    break;
                case "copy":
                    UiTask.Run(CopyPathsAsync(targets));
                    break;
            }
        });
    }

    private void OnTableKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F5 when e.Shift:
                UiTask.Run(ScanFilesAsync(SelectionOrFocused(), verify: false));
                e.Handled = true;
                break;
            case Keys.C when e.Control:
                UiTask.Run(CopyPathsAsync(SelectionOrFocused()));
                e.Handled = true;
                break;
            case Keys.Enter when e.Alt:
                ShellActions.ShowInFolder(RowAt(_table.FocusedIndex) is FileRow shown ? PathOf(shown) : null);
                e.Handled = true;
                break;
            case Keys.Enter when e.Control:
                UiTask.Run(FixAsync(TargetsOf(_table.FocusedIndex)));
                e.Handled = true;
                break;
            case Keys.Delete when Services.Operations is not null:
                // Move to quarantine for the selection the focused row is in; dialog A asks first.
                if (TargetsOf(_table.FocusedIndex) is { Length: > 0 } quarantine)
                {
                    UiTask.Run(RunAndReloadAsync(quarantine, ActionIds.Quarantine));
                }

                e.Handled = true;
                break;
            case Keys.Escape:
                // With the drawer open the window closes it first; then Esc clears the selection.
                _table.ClearSelection();
                e.Handled = true;
                break;
            case Keys.Up or Keys.Down when DrawerShowsFile:
                // The list moves its focus once this returns: the drawer follows to the neighbouring file.
                BeginInvoke(() => OpenDetails(_table.FocusedIndex));
                break;
        }
    }

    private void ShowTreeMenu(Point screen)
    {
        if (_tree.SelectedNode?.Tag is not BrowseFolder folder)
        {
            return;
        }

        PopupItem[] items =
        [
            new(Strings.Menu.ScanThisFolder, IconName.Scans, Strings.Menu.ShortcutShiftF5, Tag: "scan"),
            new(Strings.Menu.VerifyThisFolder, IconName.ShieldCheck, Tag: "verify"),
            new(Strings.Menu.IgnoreRuleHereEllipsis, IconName.Ignore, Enabled: !Services.IsDemo, Tag: "ignore-rule"),
            new(Strings.Menu.ShowInFolder, IconName.Folder, Strings.Menu.ShortcutAltEnter, Tag: "show"),
        ];
        KitMenu.Show(_tree, screen, items, item =>
        {
            switch (item.Tag)
            {
                case "scan":
                    ScanTreeFolder(folder, verify: false);
                    break;
                case "verify":
                    ScanTreeFolder(folder, verify: true);
                    break;
                case "ignore-rule":
                    UiTask.Run(IgnoreRuleHereAsync(folder, screen));
                    break;
                case "show":
                    ShellActions.OpenFolder(FolderPath(folder));
                    break;
            }
        });
    }

    /// <summary>
    /// Ignore a rule here… (subplan 09, Library tree menu): the rules with open problems in this folder and below, by
    /// name with their counts. The one chosen is ignored here after the same question as in Issues, with Undo.
    /// </summary>
    internal async Task IgnoreRuleHereAsync(BrowseFolder folder, Point screen)
    {
        if (Services.IsDemo || FindForm() is not Form owner)
        {
            return;
        }

        Dictionary<string, int> counts = await Task.Run(() => Services.Database.Queries.CountsByRule(new FindingFilter { RootId = folder.RootId, DirId = folder.DirId })).ConfigureAwait(true);
        List<PopupItem> items = [.. counts.OrderBy(c => RuleTitle(c.Key), StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new PopupItem(string.Create(CultureInfo.CurrentCulture, $"{RuleTitle(c.Key)} ({UiFormat.Count(c.Value)})"), Tag: c.Key))];
        if (items.Count == 0)
        {
            items.Add(new PopupItem(Strings.Issues.NoneHeadline, Enabled: false));
        }

        KitMenu.Show(_tree, screen, items, item =>
        {
            if (item.Tag is string ruleId)
            {
                UiTask.Run(IgnoreRuleAsync(owner, folder, ruleId));
            }
        });
    }

    private async Task IgnoreRuleAsync(Form owner, BrowseFolder folder, string ruleId)
    {
        string title = RuleTitle(ruleId);
        if (!MessageDialog.Confirm(owner, string.Format(CultureInfo.CurrentCulture, Strings.Dialogs.IgnoreRuleTitle, title, folder.Name), Strings.Dialogs.IgnoreRuleText, Strings.Kit.Cancel, Strings.Menu.Ignore, destructive: false))
        {
            return;
        }

        long ignoreId = await Services.Database.Ignores.AddAsync(folder.RootId, folder.DirId, ruleId, null).ConfigureAwait(true);
        Reload();
        Services.ShowToast?.Invoke(new ToastContent(
            Severity.None,
            Strings.Issues.Ignored,
            string.Format(CultureInfo.CurrentCulture, Strings.Issues.IgnoredRuleIn, title, folder.Name),
            Strings.Dialogs.Undo,
            () => UiTask.Run(DrawerActions.UndoAsync(Services, () => Services.Database.Ignores.RemoveAsync(ignoreId), Reload))));
    }

    private static string RuleTitle(string ruleId) => RuleCatalog.TryGet(ruleId, out RuleDefinition rule) ? rule.Title : ruleId;

    /// <summary>Scan again or Verify now for files: one scope per watched folder, "Scanning 1 file" or "Scanning {n} files".</summary>
    private async Task ScanFilesAsync(IReadOnlyList<long> ids, bool verify)
    {
        if (ids.Count == 0)
        {
            return;
        }

        List<FileRow> rows = await RowsOfAsync(ids).ConfigureAwait(true);
        ScanScope[] scopes = [.. rows.GroupBy(r => r.RootId).Select(g => new ScanScope(g.Key, null, [.. g.Select(r => r.Id)]))];
        string scope = UiFormat.Plural(rows.Count, Strings.Repair.OneFile, Strings.Scans.FilesScope);
        await Services.StartScanAsync(scopes, verify ? ScanDepth.Quick : ScanDepth.Standard, scope, verify ? VerifyOptions : null).ConfigureAwait(true);
    }

    /// <summary>The tree menu's Scan this folder and Verify this folder (Shift+F5 scans too).</summary>
    private void ScanTreeFolder(BrowseFolder folder, bool verify) =>
        UiTask.Run(StartFolderScanAsync(new ScanScope(folder.RootId, folder.RelPath), folder.Name, verify, folder.Files, folder.Bytes));

    /// <summary>
    /// A scan of one folder at the default depth, or its verification. Settings → General, Ask before large scans: a
    /// scan of more than 10,000 files or 100 GB first shows the estimate and the depths, as the header's Scan does
    /// (subplan 09, Before a large scan).
    /// </summary>
    private async Task StartFolderScanAsync(ScanScope scope, string name, bool verify, long files, long bytes)
    {
        ScanDepth depth = verify ? ScanDepth.Quick : State.Settings.Scanning.DefaultDepth;
        if (!verify && State.Settings.Scanning.AskBeforeLargeScans && !Services.IsDemo && Services.CannotScanReason(true) is null
            && new ScanEstimate(files, bytes, name, null, null, null).IsLarge && FindForm() is Form owner)
        {
            ScanEstimate whole = await Task.Run(() => Services.Data.LoadScanEstimate([scope.RootId])).ConfigureAwait(true);
            if (LargeScanDialog.Ask(owner, PartOf(whole, name, files, bytes), depth) is not ScanDepth chosen)
            {
                return;
            }

            depth = chosen;
        }

        await Services.StartScanAsync([scope], depth, name, verify ? VerifyOptions : null).ConfigureAwait(true);
    }

    /// <summary>The estimate for part of a watched folder: its own files and size, the times in proportion to its size.</summary>
    internal static ScanEstimate PartOf(ScanEstimate whole, string name, long files, long bytes)
    {
        double share = whole.Bytes > 0 ? Math.Min(1, (double)bytes / whole.Bytes) : 0;
        return new ScanEstimate(files, bytes, name, Scale(whole.Quick), Scale(whole.Standard), Scale(whole.Deep));

        TimeSpan? Scale(TimeSpan? time) => time is TimeSpan t && share > 0 ? t * share : null;
    }

    private void AddFolder(string? path)
    {
        if (FindForm() is not Form owner)
        {
            return;
        }

        UiTask.Run(ShowFolderDialogAsync(owner, null, path));
    }

    /// <summary>Folders dropped together: the Add folder dialog for each in turn, until one is cancelled.</summary>
    private void AddFolders(IReadOnlyList<string> paths)
    {
        if (FindForm() is Form owner)
        {
            UiTask.Run(AddFoldersAsync(owner, paths));
        }
    }

    private async Task AddFoldersAsync(Form owner, IReadOnlyList<string> paths)
    {
        IReadOnlyList<string> folders = paths.Count > 1 ? await FoldersAmongAsync(paths).ConfigureAwait(true) : paths;
        int watched = await Task.Run(() => Services.Database.Roots.List().Count).ConfigureAwait(true);
        foreach (string path in folders)
        {
            if (IsDisposed)
            {
                return;
            }

            await ShowFolderDialogAsync(owner, null, path).ConfigureAwait(true);

            // A dialog that added no folder was cancelled: the rest are not asked about.
            int now = await Task.Run(() => Services.Database.Roots.List().Count).ConfigureAwait(true);
            if (now <= watched)
            {
                return;
            }

            watched = now;
        }
    }

    /// <summary>
    /// The dropped paths that may be folders: files are left out, checked off the UI thread with one 2-second wait for
    /// all. A path that has not answered by then (an offline share) stays, and its dialog says when it cannot be
    /// reached. When none is a folder, the first path, so its dialog says why.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> FoldersAmongAsync(IReadOnlyList<string> paths)
    {
        Task<bool>[] checks = [.. paths.Select(path => Task.Run(() => Directory.Exists(path)))];
        await Task.WhenAny(Task.WhenAll(checks), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        string[] folders = [.. paths.Where((_, i) => checks[i] is not { IsCompletedSuccessfully: true, Result: false })];
        return folders.Length > 0 ? folders : [paths[0]];
    }

    internal void EditFolder(FolderCard card)
    {
        if (FindForm() is Form owner)
        {
            UiTask.Run(ShowFolderDialogAsync(owner, card.RootId, null));
        }
    }

    /// <summary>Add folder or Edit folder; the page then shows what changed.</summary>
    private async Task ShowFolderDialogAsync(Form owner, long? rootId, string? path)
    {
        await AddFolderDialog.ShowAsync(owner, Services, rootId, path).ConfigureAwait(true);
        Reload();
    }

    /// <summary>A folder card's Scan, or Verify now in its menu.</summary>
    internal void ScanFolder(FolderCard card, bool verify) =>
        UiTask.Run(StartFolderScanAsync(new ScanScope(card.RootId), card.Name, verify, card.Files, card.Bytes));

    internal void BrowseFolder(FolderCard card) => HandleRequest(new LibraryBrowseRequest(card.RootId, null));

    internal void RemoveFolder(FolderCard card)
    {
        if (FindForm() is not Form owner)
        {
            return;
        }

        bool remove = MessageDialog.Confirm(
            owner,
            string.Format(CultureInfo.CurrentCulture, Strings.Library.RemoveTitle, card.Name),
            Strings.Library.RemoveText,
            Strings.Kit.Cancel,
            Strings.Library.StopWatching,
            destructive: true);
        if (remove && !Services.IsDemo)
        {
            UiTask.Run(RemoveFolderAsync(card));
        }
    }

    private async Task RemoveFolderAsync(FolderCard card)
    {
        Database database = Services.Database;
        await database.Roots.RemoveAsync(card.RootId).ConfigureAwait(true);

        // For the Dashboard's Recent activity: "Folder removed: {name}".
        await database.Meta.SetAsync(LiveUiData.RemovedFolderKey, string.Create(CultureInfo.InvariantCulture, $"{Services.Clock.UtcNow.UtcTicks}|{card.Name}")).ConfigureAwait(true);
        Reload();
        await ForgetInPlansAsync(database, card.RootId).ConfigureAwait(true);
    }

    /// <summary>
    /// Schedules that scanned a folder no longer watched: it leaves their folder list, and one left without a folder is
    /// switched off (an empty folder list fails validation, so it cannot run), so it no longer runs "Completed" over
    /// nothing. The scheduled tasks are then brought up to date, as the Schedule page does after a change.
    /// </summary>
    private async Task ForgetInPlansAsync(Database database, long rootId)
    {
        long now = Services.Clock.UtcNow.UtcTicks;
        try
        {
            await Task.Run(async () =>
            {
                bool changed = false;
                foreach (PlanRecord plan in database.Plans.List())
                {
                    if (WithoutFolder(plan, rootId, now) is PlanRecord updated)
                    {
                        await database.Plans.UpdateAsync(updated).ConfigureAwait(false);
                        changed = true;
                    }
                }

                if (changed)
                {
                    await PlanHost.ReconcileAsync(database).ConfigureAwait(false);
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            State.Log.Warning("Windows would not update the scheduled tasks after a folder was removed", ex);
        }
    }

    /// <summary>A schedule without the removed folder, or null when it did not name it.</summary>
    internal static PlanRecord? WithoutFolder(PlanRecord plan, long rootId, long nowUtcTicks)
    {
        if (PlanDefinition.FromJson(plan.DefinitionJson) is not { Roots: long[] roots } definition || !roots.Contains(rootId))
        {
            return null;
        }

        long[] left = [.. roots.Where(id => id != rootId)];
        return plan with
        {
            DefinitionJson = (definition with { Roots = left }).ToJson(),
            Enabled = plan.Enabled && left.Length > 0,
            UpdatedUtc = nowUtcTicks,
        };
    }

    private sealed record LibraryLoad(int Generation, LibraryData Library);

    private sealed record BrowseLoad(int Generation, BrowseData Browse, IReadOnlyList<FileRow> First);

    /// <summary>Rows of the file table; unloaded rows draw as skeletons until their data arrives.</summary>
    private sealed class FileSource(LibraryPage page) : IListSource
    {
        private int _count;

        public int Count => _count;

        public void Reset(int count) => _count = count;

        public long IdOf(int index) => page._browse is { } b && index < b.Ids.Length ? b.Ids[index] : -1 - index;

        public void Fill(int index, ListRow row)
        {
            if (page.RowAt(index) is not FileRow file || !page._cells.TryGetValue(file.Id, out string?[]? cells))
            {
                row.Loaded = false;
                page.RequestRows(index);
                return;
            }

            row.Line1 = cells[0] ?? string.Empty;
            for (int i = 1; i < cells.Length; i++)
            {
                row.Cells[i] = cells[i];
            }

            switch (file.State)
            {
                case FileState.Pending:
                    row.StateIcon = IconName.StatePending;
                    break;
                case FileState.Skipped:
                    row.StateIcon = IconName.StateSkipped;
                    row.MutedColumn = 2;
                    break;
                case FileState.Missing:
                    row.StateIcon = IconName.StateMissing;
                    break;
                case FileState.Error:
                    row.StateIcon = IconName.StateError;
                    break;
                default:
                    if (file.WorstSeverity == Severity.None)
                    {
                        row.OkIcon = true;
                    }
                    else
                    {
                        row.Severity = file.WorstSeverity;
                    }

                    break;
            }
        }

        public void SetCollapsed(int index, bool collapsed)
        {
        }
    }

    /// <summary>One watched folder (subplan 09, Library: Folders).</summary>
    private sealed class FolderCardControl : Card
    {
        private readonly LibraryPage _page;
        private readonly KitButton _scan = new() { Kind = ButtonKind.Secondary, Icon = IconName.Play, Text = Strings.Library.Scan, Compact = true };
        private readonly IconButton _more = new() { Icon = IconName.More, Compact = true };
        private readonly InfoBar _offline = new() { Tier = Severity.Medium, Text = Strings.Library.NotConnected };

        /// <summary>The kit's severity bar: a tooltip per segment, "<c>tier: count</c>" (subplan 08).</summary>
        private readonly SeverityBar _bar = new();
        private FolderCard? _card;

        public FolderCardControl(LibraryPage page)
        {
            _page = page;
            Clickable = true;
            _more.ToolTipText = Strings.Kit.MoreActions;
            _scan.Click += (_, _) => { if (_card is not null) { _page.ScanFolder(_card, verify: false); } };
            _more.Click += (_, _) => ShowMenu(null);

            // A left click (or Enter) opens the folder in Browse; a right click opens the card's menu.
            Click += (_, e) => { if (_card is not null && IsPrimaryClick(e)) { _page.BrowseFolder(_card); } };
            MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) { ShowMenu(PointToScreen(e.Location)); } };
            _bar.SegmentClicked += (_, _) => { if (_card is not null) { _page.BrowseFolder(_card); } };
            Controls.AddRange([_scan, _more, _offline, _bar]);
        }

        public void Bind(FolderCard card)
        {
            _card = card;
            AccessibleName = card.Name;
            bool library = card.Role == RootRole.Library;
            _scan.Visible = library && card.Connected;
            _offline.Visible = !card.Connected;

            // Backup folders and folders that are not connected show no bar.
            _bar.Visible = library && card.Connected;
            _bar.SetCounts(card.Problems.Critical, card.Problems.High, card.Problems.Medium, card.Problems.Low, card.OkFiles);
            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int moreSize = S(28);
            _more.Bounds = new Rectangle(Width - S(16) - moreSize, S(12), moreSize, moreSize);
            int scanWidth = S(_scan.PreferredLogicalWidth);
            _scan.Bounds = new Rectangle(_more.Left - S(8) - scanWidth, S(12), scanWidth, S(28));
            _offline.Bounds = new Rectangle(S(16), S(72), Width - S(32), S(36));
            _bar.Bounds = new Rectangle(S(16), S(103), Math.Max(0, Width - S(32)), S(8));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
            {
                e.Handled = true;
                ShowMenu(null);
            }
        }

        protected override void PaintContent(Graphics g)
        {
            if (_card is not FolderCard card)
            {
                return;
            }

            ThemeTokens t = Tokens;
            TypeRamp type = Type;
            int width = Width;
            IconRenderer.Draw(g, card.Role == RootRole.Backup ? IconName.Drive : IconName.Folder, SF(16), SF(19), 16, DeviceDpi, t.Text2);
            int nameWidth = KitPaint.MeasureWidth(card.Name, type.Subtitle);
            int maxName = Math.Max(S(40), (_scan.Visible ? _scan.Left : _more.Left) - S(44) - S(96));
            KitPaint.DrawText(g, card.Name, type.Subtitle, t.Text, S(44), S(27), Math.Min(nameWidth + 1, maxName));
            BadgePainter.PaintPill(g, S(44) + Math.Min(nameWidth, maxName) + S(8), S(17), card.Role == RootRole.Backup ? Strings.Library.BackupPill : Strings.Library.LibraryPill, DeviceDpi);
            KitPaint.DrawText(g, card.Path, type.Body, t.Text2, S(44), S(51), width - S(60));
            string stats = string.Format(CultureInfo.CurrentCulture, Strings.Library.FilesAndSize, UiFormat.Count(card.Files), UiFormat.Size(card.Bytes));
            string storage = StorageName(card.Storage);
            DateTimeOffset now = _page.Services.Clock.UtcNow;
            if (!card.Connected)
            {
                KitPaint.DrawText(g, stats, type.Body, t.Text3, S(16), S(133), width / 2);
                string seen = string.Format(CultureInfo.CurrentCulture, Strings.Library.LastSeen, card.LastSeen is DateTimeOffset last ? UiFormat.RecentDay(last, now) : Strings.Dash, storage);
                KitPaint.DrawText(g, seen, type.Caption, t.Text3, width / 2, S(133), (width / 2) - S(16), TextFormatFlags.Right);
                return;
            }

            KitPaint.DrawText(g, stats, type.Body, t.Text, S(16), S(87), width / 2);
            string footer;
            if (card.Role == RootRole.Backup)
            {
                footer = string.Format(CultureInfo.CurrentCulture, Strings.Library.FingerprintsRecorded, card.FingerprintsRecorded is DateTimeOffset f ? UiFormat.RecentDate(f, now, inSentence: true) : Strings.Dash, storage);
            }
            else
            {
                int problems = card.Problems.Total;
                string count = problems == 0 ? Strings.Library.NoProblems : problems == 1 ? Strings.Library.OneProblem : string.Format(CultureInfo.CurrentCulture, Strings.Library.Problems, UiFormat.Count(problems));
                KitPaint.DrawText(g, count, type.Body, t.Text, width / 2, S(87), (width / 2) - S(16), TextFormatFlags.Right);
                footer = card.LastScanned is DateTimeOffset scanned
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Library.Scanned, UiFormat.RecentDate(scanned, now, inSentence: true), storage)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Library.NotScanned, storage);
            }

            KitPaint.HorizontalLine(g, t.Stroke, S(16), width - S(16), S(126), SF(1));
            KitPaint.DrawText(g, footer, type.Caption, t.Text3, S(16), S(144), width - S(32));
        }

        private static string StorageName(StorageClass storage) => storage switch
        {
            StorageClass.Ssd => Strings.Library.Ssd,
            StorageClass.Hdd => Strings.Library.HardDrive,
            StorageClass.Network => Strings.Library.Network,
            StorageClass.Removable => Strings.Library.Removable,
            _ => Strings.Library.HardDrive,
        };

        /// <summary>The card's menu: under the More button, or at <paramref name="screen"/> for a right click.</summary>
        private void ShowMenu(Point? screen)
        {
            if (_card is not FolderCard card)
            {
                return;
            }

            List<PopupItem> items = [new(Strings.Menu.Edit, Tag: "edit")];
            if (card.Connected)
            {
                items.Add(new(Strings.Menu.VerifyNow, IconName.ShieldCheck, Tag: "verify"));
            }

            items.Add(new(Strings.Menu.OpenInExplorer, IconName.Open, Tag: "open"));
            items.Add(PopupItem.Separator);
            items.Add(new(Strings.Menu.Remove, IconName.Trash, Tag: "remove"));
            Action<PopupItem> chosen = item =>
            {
                switch (item.Tag)
                {
                    case "edit":
                        _page.EditFolder(card);
                        break;
                    case "verify":
                        _page.ScanFolder(card, verify: true);
                        break;
                    case "open":
                        ShellActions.OpenFolder(card.Path);
                        break;
                    case "remove":
                        _page.RemoveFolder(card);
                        break;
                }
            };
            if (screen is Point at)
            {
                KitMenu.Show(this, at, items, chosen);
            }
            else
            {
                KitMenu.ShowBelow(_more, items, chosen);
            }
        }
    }

    /// <summary>The dashed drop area under the cards: folders dropped here open the Add folder dialog, one after another, until one is cancelled.</summary>
    private sealed class DropArea : KitControl
    {
        private readonly System.Drawing.Drawing2D.GraphicsPath _outline = new();
        private Pen? _dash;

        public DropArea()
        {
            AllowDrop = true;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = Strings.Library.DropArea;
        }

        public event EventHandler<IReadOnlyList<string>>? FoldersDropped;

        protected override bool Interactive => true;

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor is Color back && back.A == 255 ? back : t.Bg);
            Rectangle bounds = new(0, 0, Width - 1, Height - 1);
            VisualState state = PaintState;
            Color line = state.HasFlag(VisualState.Hover) ? t.StrokeStrong : t.Stroke;
            if (_dash is null || _dash.Color != line || Math.Abs(_dash.Width - SF(1)) > 0.01f)
            {
                _dash?.Dispose();
                _dash = new Pen(line, SF(1)) { DashPattern = [4, 3] };
            }

            System.Drawing.Drawing2D.SmoothingMode smoothing = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            _outline.Reset();
            IconGeometry.AddRoundedRect(_outline, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), SF(8));
            g.DrawPath(_dash, _outline);
            g.SmoothingMode = smoothing;

            TypeRamp type = Type;
            int textWidth = KitPaint.MeasureWidth(Strings.Library.DropArea, type.Body);
            int left = (Width - textWidth - S(24)) / 2;
            IconRenderer.Draw(g, IconName.FolderAdd, left, (Height / 2f) - SF(8), 16, DeviceDpi, t.Text2);
            KitPaint.DrawText(g, Strings.Library.DropArea, type.Body, t.Text2, left + S(24), Height / 2, textWidth + 1);
            if (state.HasFlag(VisualState.Focused))
            {
                KitPaint.DrawFocusRing(g, bounds, SF(8), DeviceDpi);
            }
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            drgevent.Effect = DroppedPaths(drgevent.Data).Length == 0 ? DragDropEffects.None : DragDropEffects.Link;
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            if (DroppedPaths(drgevent.Data) is { Length: > 0 } folders)
            {
                FoldersDropped?.Invoke(this, folders);
            }
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _outline.Dispose();
                _dash?.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// The dropped paths, without touching the disk: checking them here held the window (and the drag) for minutes
        /// when one was a network folder that had gone offline. The page leaves out files off the UI thread, and the Add
        /// folder dialog says when a path is not a folder.
        /// </summary>
        private static string[] DroppedPaths(IDataObject? data) =>
            data?.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];
    }
}
