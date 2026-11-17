using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>A request to show Library → Browse at a folder (from the dashboard, a card or the drawer).</summary>
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
    private static readonly ListColumn[] TableColumns =
    [
        new(Strings.Library.ColumnName, 284),
        new(Strings.Library.ColumnFormat, 192, Sortable: false),
        new(Strings.Library.ColumnSize, 64, RightAligned: true),
        new(Strings.Library.ColumnScanned, 88),
        new(Strings.Library.ColumnVerified, 0),
    ];

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
    private readonly FileSource _source;
    private readonly Dictionary<long, FileRow> _rows = [];
    private readonly FileDetailView _detail = new();
    private readonly ScrollPanel _detailScroll;
    private LibraryData? _library;
    private BrowseData? _browse;
    private BrowseQuery _query = new(null, null);
    private bool _browsing;
    private bool _loadingRows;
    private bool _ignoreTreeSelection;

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
        _detail.ShowInFolderRequested += (_, _) => ShellActions.ShowInFolder(_detail.Detail?.FullPath);
        _view.Segments.Add(Strings.Library.Folders);
        _view.Segments.Add(Strings.Library.Browse);
        _view.SelectedIndex = 0;
        _view.AccessibleName = PageInfo.Title(PageId.Library);
        _view.SelectedIndexChanged += (_, _) => SetBrowsing(_view.SelectedIndex == 1);
        _add.Click += (_, _) => AddFolder(null);
        _drop.FolderDropped += (_, path) => AddFolder(path);
        _drop.Click += (_, _) => AddFolder(null);
        _crumbs.SegmentClicked += (_, index) => BrowseCrumb(index);
        for (int i = 0; i < _chips.Length; i++)
        {
            BrowseState state = (BrowseState)i;
            _chips[i].Toggled += (_, _) => SetState(state);
        }

        _treeCard.Controls.Add(_tree);
        _tree.SelectionChanged += (_, _) => OnTreeSelection();
        _tree.KeyDown += OnTreeKey;
        _tree.MouseUp += OnTreeMouseUp;
        _tableCard.Controls.Add(_table);
        _table.Columns = TableColumns;
        _table.Source = _source;
        _table.SetSort(0, false);
        _table.SortChanged += (_, sort) => SetSort(sort.Column, sort.Descending);
        _table.ItemActivated += (_, index) => OpenDetails(index);
        _table.MoreRequested += (_, args) => ShowRowMenu(args.Index, args.Screen);
        _table.KeyDown += OnTableKey;
        Controls.AddRange([_view, _add, _drop, _crumbs, .. _chips, _treeCard, _tableCard]);
        SetBrowsing(false);
    }

    public override PageId Id => PageId.Library;

    internal IReadOnlyDictionary<long, FileRow> Rows => _rows;

    public override void HandleRequest(object request)
    {
        switch (request)
        {
            case LibraryBrowseRequest browse:
                _query = new BrowseQuery(browse.RootId, browse.DirId);
                _view.SelectedIndex = 1;
                Reload();
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
    internal void ShowDemoBrowse()
    {
        _query = new BrowseQuery(1, 14);
        _view.SelectedIndex = 1;
    }

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
        if (_browsing)
        {
            BrowseData browse = Services.Data.LoadBrowse(_query);
            IReadOnlyList<FileRow> first = Services.Data.LoadFileRows([.. browse.Ids.Take(200)]);
            return (browse, first);
        }

        return Services.Data.LoadLibrary();
    }

    protected override void ApplyData(object data)
    {
        switch (data)
        {
            case LibraryData library:
                ApplyLibrary(library);
                break;
            case (BrowseData browse, IReadOnlyList<FileRow> rows):
                ApplyBrowse(browse, rows);
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

        _crumbs.Bounds = new Rectangle(S(180), S(2), Math.Max(0, width - S(180) - S(320)), S(30));
        int x = width;
        for (int i = _chips.Length - 1; i >= 0; i--)
        {
            int chipWidth = S(_chips[i].PreferredLogicalWidth);
            x -= chipWidth;
            _chips[i].Bounds = new Rectangle(x, S(2), chipWidth, S(Chip.LogicalHeight));
            x -= S(8);
        }

        int top = S(48);
        int height = Math.Max(S(120), Height - top);
        _treeCard.Bounds = new Rectangle(0, top, S(280), height);
        _tree.Bounds = Rectangle.Inflate(new Rectangle(Point.Empty, _treeCard.Size), -S(1), -S(8));
        _tableCard.Bounds = new Rectangle(S(292), top, Math.Max(S(200), width - S(292)), height);
        _table.Bounds = Rectangle.Inflate(new Rectangle(Point.Empty, _tableCard.Size), -S(1), -S(1));
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
        bool treeChanged = _browse is null || !ReferenceEquals(_browse.Tree, browse.Tree);
        _browse = browse;
        _query = browse.Query;
        _rows.Clear();
        foreach (FileRow row in first)
        {
            _rows[row.Id] = row;
        }

        if (treeChanged)
        {
            RebuildTree(browse.Tree, browse.Query.DirId);
        }

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
        _table.Invalidate();
        PerformLayout();
    }

    private void RebuildTree(IReadOnlyList<BrowseFolder> folders, long? selectedDir)
    {
        _tree.Roots.Clear();
        TreeItem? selected = null;
        foreach (BrowseFolder folder in folders)
        {
            TreeItem item = ToItem(folder, selectedDir, ref selected);
            _tree.Roots.Add(item);
        }

        // Open the path to the shown folder.
        for (TreeItem? parent = selected?.Parent; parent is not null; parent = parent.Parent)
        {
            parent.Expanded = true;
        }

        _tree.Rebuild();
        if (selected is not null)
        {
            _ignoreTreeSelection = true;
            _tree.SelectedNode = selected;
            _ignoreTreeSelection = false;
        }
    }

    private static TreeItem ToItem(BrowseFolder folder, long? selectedDir, ref TreeItem? selected)
    {
        TreeItem item = new(folder.Name, folder)
        {
            Severity = folder.Worst == Severity.None ? null : folder.Worst,
            HasChildren = folder.Children.Count > 0,
        };
        if (folder.DirId == selectedDir)
        {
            selected = item;
        }

        List<TreeItem> children = [];
        foreach (BrowseFolder child in folder.Children)
        {
            children.Add(ToItem(child, selectedDir, ref selected));
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

        _query = _query with { RootId = folder.RootId, DirId = folder.DirId };
        Reload();
    }

    private void BrowseCrumb(int index)
    {
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
            Sort = column switch { 2 => FileSort.Size, 3 => FileSort.Scanned, 4 => FileSort.Verified, _ => FileSort.Name },
            Descending = descending,
        };
        Reload();
    }

    private async Task LoadRowsAsync(List<long> ids)
    {
        _loadingRows = true;
        try
        {
            IReadOnlyList<FileRow> rows = await Task.Run(() => Services.Data.LoadFileRows(ids)).ConfigureAwait(true);
            foreach (FileRow row in rows)
            {
                _rows[row.Id] = row;
            }

            _table.Invalidate();
        }
        finally
        {
            _loadingRows = false;
        }
    }

    private FileRow? RowAt(int index) =>
        _browse is not null && index >= 0 && index < _browse.Ids.Length && _rows.TryGetValue(_browse.Ids[index], out FileRow? row) ? row : null;

    private string? PathOf(FileRow row)
    {
        string? root = _library?.Folders.FirstOrDefault(f => f.RootId == row.RootId)?.Path ?? _browse?.Tree.FirstOrDefault(t => t.RootId == row.RootId)?.Name;
        return root is null ? null : row.RelDir.Length == 0 ? Path.Combine(root, row.Name) : Path.Combine(root, row.RelDir, row.Name);
    }

    private async void OpenDetails(int index)
    {
        if (RowAt(index) is not FileRow row)
        {
            return;
        }

        FileDetail? detail = await Task.Run(() => Services.Data.LoadFileDetail(row.Id)).ConfigureAwait(true);
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

        PopupItem[] items =
        [
            new(Strings.Menu.Details, IconName.Info, Strings.Menu.ShortcutEnter, Tag: "details"),
            new(Strings.Menu.ScanAgain, IconName.Refresh, Strings.Menu.ShortcutShiftF5, Tag: "scan"),
            new(Strings.Menu.VerifyNow, IconName.ShieldCheck, Tag: "verify"),
            PopupItem.Separator,
            new(Strings.Menu.ShowInFolder, IconName.Folder, Strings.Menu.ShortcutAltEnter, Tag: "show"),
            new(Strings.Menu.CopyPath, IconName.Copy, Strings.Menu.ShortcutCtrlC, Tag: "copy"),
        ];
        KitMenu.Show(_table, screen, items, item =>
        {
            switch (item.Tag)
            {
                case "details":
                    OpenDetails(index);
                    break;
                case "scan":
                    UiTask.Run(ScanFilesAsync([row], verify: false));
                    break;
                case "verify":
                    UiTask.Run(ScanFilesAsync([row], verify: true));
                    break;
                case "show":
                    ShellActions.ShowInFolder(PathOf(row));
                    break;
                case "copy":
                    ShellActions.CopyText([PathOf(row) ?? row.Name]);
                    break;
            }
        });
    }

    private void OnTableKey(object? sender, KeyEventArgs e)
    {
        FileRow? row = RowAt(_table.FocusedIndex);
        switch (e.KeyCode)
        {
            case Keys.F5 when e.Shift && row is not null:
                UiTask.Run(ScanFilesAsync([row], verify: false));
                e.Handled = true;
                break;
            case Keys.Enter when e.Alt && row is not null:
                ShellActions.ShowInFolder(PathOf(row));
                e.Handled = true;
                break;
            case Keys.C when e.Control && row is not null:
                ShellActions.CopyText([PathOf(row) ?? row.Name]);
                e.Handled = true;
                break;
        }
    }

    private void OnTreeKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
        {
            ShowTreeMenu(_tree.PointToScreen(new Point(S(44), S(32))));
            e.Handled = true;
        }
    }

    private void OnTreeMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ShowTreeMenu(_tree.PointToScreen(e.Location));
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
            new(Strings.Menu.ScanThisFolder, IconName.Scans, Tag: "scan"),
            new(Strings.Menu.VerifyThisFolder, IconName.ShieldCheck, Tag: "verify"),
            new(Strings.Menu.ShowInFolder, IconName.Folder, Tag: "show"),
        ];
        KitMenu.Show(_tree, screen, items, item =>
        {
            string relDir = FolderRelPath(_tree.SelectedNode);
            switch (item.Tag)
            {
                case "scan":
                    UiTask.Run(Services.StartScanAsync([new ScanScope(folder.RootId, relDir)], State.Settings.Scanning.DefaultDepth, folder.Name));
                    break;
                case "verify":
                    UiTask.Run(Services.StartScanAsync([new ScanScope(folder.RootId, relDir)], ScanDepth.Quick, folder.Name, new ScanOptions { VerifyOnly = true, VerifyEverything = true }));
                    break;
                case "show":
                    string? root = _library?.Folders.FirstOrDefault(f => f.RootId == folder.RootId)?.Path;
                    ShellActions.OpenFolder(root is null ? null : Path.Combine(root, relDir));
                    break;
            }
        });
    }

    private static string FolderRelPath(TreeItem? node)
    {
        List<string> parts = [];
        for (TreeItem? n = node; n?.Parent is not null; n = n.Parent)
        {
            parts.Insert(0, n.Text);
        }

        return string.Join('\\', parts);
    }

    private async Task ScanFilesAsync(IReadOnlyList<FileRow> rows, bool verify)
    {
        ScanScope[] scopes = [.. rows.GroupBy(r => r.RootId).Select(g => new ScanScope(g.Key, null, [.. g.Select(r => r.Id)]))];
        string scope = string.Format(CultureInfo.CurrentCulture, Strings.Scans.FilesScope, UiFormat.Count(rows.Count));
        await Services.StartScanAsync(scopes, verify ? ScanDepth.Quick : ScanDepth.Standard, scope, verify ? new ScanOptions { VerifyOnly = true, VerifyEverything = true } : null).ConfigureAwait(true);
    }

    private void AddFolder(string? path)
    {
        if (FindForm() is not Form owner)
        {
            return;
        }

        UiTask.Run(ShowFolderDialogAsync(owner, null, path));
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

    internal void ScanFolder(FolderCard card, bool verify) =>
        UiTask.Run(Services.StartScanAsync([new ScanScope(card.RootId)], verify ? ScanDepth.Quick : State.Settings.Scanning.DefaultDepth, card.Name, verify ? new ScanOptions { VerifyOnly = true, VerifyEverything = true } : null));

    internal void BrowseFolder(FolderCard card) => HandleRequest(new LibraryBrowseRequest(card.RootId, null));

    internal async void RemoveFolder(FolderCard card)
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
        if (!remove || Services.IsDemo)
        {
            return;
        }

        await Services.Database.Roots.RemoveAsync(card.RootId).ConfigureAwait(true);
        Reload();
    }

    /// <summary>Rows of the file table; unloaded rows draw as skeletons until their data arrives.</summary>
    private sealed class FileSource(LibraryPage page) : IListSource
    {
        private int _count;

        public int Count => _count;

        public void Reset(int count) => _count = count;

        public long IdOf(int index) => page._browse is { } b && index < b.Ids.Length ? b.Ids[index] : -1 - index;

        public void Fill(int index, ListRow row)
        {
            if (page.RowAt(index) is not FileRow file)
            {
                row.Loaded = false;
                page.RequestRows(index);
                return;
            }

            row.Line1 = file.Name;
            row.Cells[1] = UiFormat.FileFormat(file);
            row.Cells[2] = UiFormat.Size(file.Size);
            DateTimeOffset now = page.Services.Clock.UtcNow;
            row.Cells[3] = file.LastScannedUtc is long scanned ? UiFormat.RecentDate(new DateTimeOffset(scanned, TimeSpan.Zero), now) : Strings.Dash;
            row.Cells[4] = file.LastVerifiedUtc is long verified ? UiFormat.RecentDate(new DateTimeOffset(verified, TimeSpan.Zero), now) : Strings.Dash;
            switch (file.State)
            {
                case FileState.Pending:
                    row.StateIcon = IconName.StatePending;
                    break;
                case FileState.Skipped:
                    row.StateIcon = IconName.StateSkipped;
                    row.MutedColumn = 1;
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
        private readonly long[] _bar = new long[5];
        private readonly RectangleF[] _segments = new RectangleF[5];
        private FolderCard? _card;

        public FolderCardControl(LibraryPage page)
        {
            _page = page;
            Clickable = true;
            _more.ToolTipText = Strings.Kit.MoreActions;
            _scan.Click += (_, _) => { if (_card is not null) { _page.ScanFolder(_card, verify: false); } };
            _more.Click += (_, _) => ShowMenu();
            Click += (_, _) => { if (_card is not null) { _page.BrowseFolder(_card); } };
            Controls.AddRange([_scan, _more, _offline]);
        }

        public void Bind(FolderCard card)
        {
            _card = card;
            AccessibleName = card.Name;
            bool library = card.Role == RootRole.Library;
            _scan.Visible = library && card.Connected;
            _offline.Visible = !card.Connected;
            _bar[0] = card.Problems.Critical;
            _bar[1] = card.Problems.High;
            _bar[2] = card.Problems.Medium;
            _bar[3] = card.Problems.Low;
            _bar[4] = card.OkFiles;
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
                DataPaint.SeverityBar(g, new RectangleF(SF(16), SF(103), width - SF(32), SF(8)), _bar, _segments, DeviceDpi);
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

        private void ShowMenu()
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
            KitMenu.ShowBelow(_more, items, item =>
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
            });
        }
    }

    /// <summary>The dashed drop area under the cards: folders dropped here open the Add folder dialog.</summary>
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

        public event EventHandler<string>? FolderDropped;

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
            drgevent.Effect = FirstFolder(drgevent.Data) is null ? DragDropEffects.None : DragDropEffects.Link;
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            if (FirstFolder(drgevent.Data) is string folder)
            {
                FolderDropped?.Invoke(this, folder);
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

        private static string? FirstFolder(IDataObject? data) =>
            data?.GetData(DataFormats.FileDrop) is string[] paths ? paths.FirstOrDefault(Directory.Exists) : null;
    }
}
