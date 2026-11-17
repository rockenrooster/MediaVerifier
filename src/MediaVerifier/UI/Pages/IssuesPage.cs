using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// Issues (subplan 09, task UI-04): tier and kind chips, drop-downs, grouping, the grouped two-line list
/// with hover actions, the selection bar, the filter summary row, empty states, and the detail drawer.
/// </summary>
internal sealed class IssuesPage : PageBase
{
    private static readonly Severity[] Tiers = [Severity.Critical, Severity.High, Severity.Medium, Severity.Low];
    private static readonly (MediaKind Kind, IconName Icon, string Label)[] KindChips = [(MediaKind.Photo, IconName.Photo, Strings.Kinds.Photos), (MediaKind.Video, IconName.Video, Strings.Kinds.Videos), (MediaKind.Audio, IconName.Music, Strings.Kinds.Music)];

    private readonly Chip _all = new() { Text = Strings.Issues.All, On = true };
    private readonly Chip[] _tierChips;
    private readonly Chip[] _kindChips;
    private readonly DropDown _kindDrop = new() { Compact = true };
    private readonly DropDown _category = new() { Compact = true };
    private readonly DropDown _rule = new() { Compact = true };
    private readonly DropDown _folder = new() { Compact = true };
    private readonly DropDown _status = new() { Compact = true };
    private readonly DropDown _groupDrop = new() { Compact = true, DisplayFormat = Strings.Issues.GroupFormat };
    private readonly SegmentedControl _group = new() { Compact = true };
    private readonly VirtualList _list = new() { Mode = ListMode.TwoLine };
    private readonly EmptyState _empty = new() { Visible = false };
    private readonly SelectionBar _selectionBar = new() { Visible = false };
    private readonly IssuesSource _source;
    private readonly FileDetailView _detail = new();
    private readonly ScrollPanel _detailScroll;
    private IssuesQuery _query = new(new FindingFilter());
    private IssuesData? _data;
    private IReadOnlyList<(long RootId, string Name)> _roots = [];
    private bool _updating;
    private long? _firstRun;
    private bool _narrow;

    public IssuesPage(UiServices services)
        : base(services)
    {
        _tierChips = [.. Tiers.Select(t => new Chip { Text = UiFormat.TierName(t), Severity = t })];
        _kindChips = [.. KindChips.Select(k => new Chip { Text = k.Label, Icon = k.Icon })];
        _source = new IssuesSource(this);
        _list.Source = _source;
        _detailScroll = new ScrollPanel(_detail);
        _detail.CloseRequested += (_, _) => CloseDetail();
        _detail.EvidenceLoader = (detail, token) => Services.Data.LoadAudioEvidence(detail, token);
        _detail.ThumbnailLoader = (detail, token) => Services.Data.LoadThumbnail(detail, token);
        _detail.StripLoader = (detail, finding, token) => Services.Data.LoadFrameStrip(detail, finding, token);
        _detail.CompareLoader = (detail, finding, time, token) => Services.Data.LoadCompare(detail, finding, time, token);
        _detail.FindingAction += (_, action) => _ = OnFindingActionAsync(action.FindingId, action.Ignore);
        _detail.ShowInFolderRequested += (_, _) => ShowInFolder(_detail.Detail?.FullPath);

        _all.Toggled += (_, _) => OnAllChip();
        foreach (Chip chip in _tierChips.Concat(_kindChips))
        {
            chip.Toggled += (_, _) => OnFilterChanged();
        }

        _kindDrop.Items.Add(Strings.Issues.Kind);
        foreach ((_, _, string label) in KindChips)
        {
            _kindDrop.Items.Add(label);
        }

        _kindDrop.SelectedIndex = 0;
        foreach (string category in new[] { Strings.Issues.Category, Strings.Issues.Damage, Strings.Issues.Integrity, Strings.Issues.Authenticity, Strings.Issues.Quality, Strings.Issues.Structure })
        {
            _category.Items.Add(category);
        }

        _category.SelectedIndex = 0;
        _rule.Items.Add(Strings.Issues.Rule);
        _rule.SelectedIndex = 0;
        _folder.Items.Add(Strings.Issues.Folder);
        _folder.SelectedIndex = 0;
        foreach (string status in new[] { Strings.Issues.Open, Strings.Issues.Ignored, Strings.Issues.Resolved })
        {
            _status.Items.Add(status);
        }

        _status.DisplayFormat = Strings.Issues.Status;
        _status.SelectedIndex = 0;
        foreach (string group in new[] { Strings.Issues.BySeverity, Strings.Issues.ByFolder, Strings.Issues.ByRule })
        {
            _group.Segments.Add(group);
            _groupDrop.Items.Add(group);
        }

        _groupDrop.SelectedIndex = 0;
        foreach (DropDown drop in new[] { _kindDrop, _category, _rule, _folder, _status })
        {
            drop.SelectedIndexChanged += (_, _) => OnFilterChanged();
        }

        _group.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating)
            {
                _groupDrop.SelectedIndex = _group.SelectedIndex;
                OnFilterChanged();
            }
        };
        _groupDrop.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating)
            {
                _group.SelectedIndex = _groupDrop.SelectedIndex;
            }
        };

        _list.ItemActivated += (_, index) => OpenDetail(index);
        _list.DetailsClicked += (_, index) => OpenDetail(index);
        _list.MoreRequested += (_, args) => ShowRowMenu(args.Index, args.Screen);
        _list.SelectionChanged += (_, _) => UpdateSelectionBar();
        _list.KeyDown += OnListKey;
        _selectionBar.CloseToolTip = Strings.Kit.Close;
        _selectionBar.Closed += (_, _) => _list.ClearSelection();
        _selectionBar.SetButtons(
        [
            Button(Strings.Issues.Fix, ButtonKind.Primary, IconName.Tools, (_, _) => _ = FixSelectedAsync()),
            Button(Strings.Issues.Ignore, ButtonKind.Secondary, IconName.Ignore, (_, _) => _ = IgnoreSelectedAsync()),
            Button(Strings.Issues.ScanAgain, ButtonKind.Secondary, IconName.Refresh, (_, _) => ScanSelected()),
            Button(Strings.Issues.Quarantine, ButtonKind.Secondary, IconName.Quarantine, (_, _) => _ = RunOnSelectedAsync(ActionIds.Quarantine)),
        ]);
        _detail.PrimaryRequested += (_, _) =>
        {
            if (_detail.Detail is { RecommendedActionId.Length: > 0 } shown)
            {
                _ = RunAndReloadAsync([shown.FileId], shown.RecommendedActionId);
            }
        };
        KitButton fix = _selectionBar.Buttons[0];
        fix.Enabled = false;
        KitToolTip.Set(fix, Strings.Issues.NeedDifferentFixes);
        _selectionBar.Buttons[3].Enabled = false;

        Controls.Add(_all);
        Controls.AddRange(_tierChips);
        Controls.AddRange(_kindChips);
        Controls.AddRange([_kindDrop, _category, _rule, _folder, _status, _groupDrop, _group, _list, _empty, _selectionBar]);
        _selectionBar.BringToFront();
        int tab = 0;
        List<Control> order = [_all, .. _tierChips, .. _kindChips, _kindDrop, _category, _rule, _folder, _status, _group, _groupDrop, _list, _selectionBar];
        foreach (Control control in order)
        {
            control.TabIndex = tab++;
        }
    }

    public override PageId Id => PageId.Issues;

    internal IssuesData? Data => _data;

    internal bool Narrow => _narrow;

    internal Dictionary<long, IssueRow> Rows { get; } = [];

    public override void HandleRequest(object request)
    {
        if (request is IssuesFilterRequest filter)
        {
            _firstRun = filter.FirstRun;
            _updating = true;
            _all.On = filter.Tier is null;
            for (int i = 0; i < Tiers.Length; i++)
            {
                _tierChips[i].On = filter.Tier == Tiers[i];
            }

            for (int i = 0; i < KindChips.Length; i++)
            {
                _kindChips[i].On = filter.Kind == KindChips[i].Kind;
            }

            _updating = false;
            OnFilterChanged();
        }
    }

    public override string? HelpRule => _source.RowAt(_list.FocusedIndex)?.RuleId;

    /// <summary>The render harness's issues-detail state: the Night Drive row selected, its drawer open.</summary>
    internal async Task ShowDemoDetailAsync()
    {
        for (int i = 0; i < _source.Count; i++)
        {
            if (_source.RowAt(i) is IssueRow row && row.Name.StartsWith("Night Drive", StringComparison.Ordinal))
            {
                _list.ApplyClickSelection(i, Keys.None);
                UpdateSelectionBar();
                await LoadDetailAsync(row.FileId).ConfigureAwait(true);
                await _detail.EvidenceLoading.ConfigureAwait(true);
                return;
            }
        }
    }

    /// <summary>The render harness's issues-selection state: three rows selected and the selection bar.</summary>
    internal void ShowDemoSelection()
    {
        for (int i = 5; i <= 7; i++)
        {
            _list.ApplyClickSelection(i, i == 5 ? Keys.None : Keys.Control);
        }

        UpdateSelectionBar();
    }

    /// <summary>Asks for the rows around <paramref name="position"/> (called while painting).</summary>
    internal void RequestRows(int position)
    {
        if (_data is null || _loadingRows)
        {
            return;
        }

        int start = Math.Max(0, position - 50);
        int end = Math.Min(_data.Ids.Length, position + 200);
        List<long> missing = [];
        for (int i = start; i < end; i++)
        {
            if (!Rows.ContainsKey(_data.Ids[i]))
            {
                missing.Add(_data.Ids[i]);
            }
        }

        if (missing.Count > 0)
        {
            _ = LoadRowsAsync(missing);
        }
    }

    internal void ClearFilters()
    {
        _firstRun = null;
        _updating = true;
        _all.On = true;
        foreach (Chip chip in _tierChips.Concat(_kindChips))
        {
            chip.On = false;
        }

        foreach (DropDown drop in new[] { _kindDrop, _category, _rule, _folder, _status })
        {
            drop.SelectedIndex = 0;
        }

        _updating = false;
        OnFilterChanged();
    }

    protected override object? LoadData()
    {
        IssuesData data = Services.Data.LoadIssues(_query);
        IReadOnlyList<IssueRow> first = Services.Data.LoadIssueRows([.. data.Ids.Take(200)]);
        return (data, first);
    }

    protected override void ApplyData(object data)
    {
        (IssuesData issues, IReadOnlyList<IssueRow> first) = ((IssuesData, IReadOnlyList<IssueRow>))data;
        _data = issues;
        Rows.Clear();
        foreach (IssueRow row in first)
        {
            Rows[row.Id] = row;
        }

        _updating = true;
        _all.Count = (int)issues.TierCounts.Total;
        for (int i = 0; i < Tiers.Length; i++)
        {
            _tierChips[i].Count = issues.TierCounts[Tiers[i]];
        }

        if (_rule.Items.Count == 1 && issues.RuleCounts.Count > 0)
        {
            foreach ((string rule, int count) in issues.RuleCounts.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                _rule.Items.Add(string.Create(CultureInfo.CurrentCulture, $"{rule} ({count})"));
            }
        }

        _roots = issues.Roots;
        if (_folder.Items.Count == 1)
        {
            foreach ((_, string name) in _roots)
            {
                _folder.Items.Add(name);
            }
        }

        _updating = false;
        _source.Rebuild(issues);
        _list.Refresh();
        UpdateEmptyState(issues);
        UpdateHighlights();
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        CloseDetail();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        _narrow = Width < S(760);

        // Narrower than 760 the status drop-down shows only its value (subplan 08, responsive rules).
        string? statusFormat = _narrow ? null : Strings.Issues.Status;
        if (_status.DisplayFormat != statusFormat)
        {
            _status.DisplayFormat = statusFormat;
            _status.Invalidate();
        }

        int x = 0;
        int chipHeight = S(28);
        x = PlaceChip(_all, x);
        foreach (Chip chip in _tierChips)
        {
            x = PlaceChip(chip, x);
        }

        foreach (Chip chip in _kindChips)
        {
            chip.Visible = !_narrow;
        }

        if (!_narrow)
        {
            x += S(9);
            foreach (Chip chip in _kindChips)
            {
                x = PlaceChip(chip, x);
            }
        }

        int y = S(40);
        x = 0;
        _kindDrop.Visible = _narrow;
        _groupDrop.Visible = _narrow;
        _group.Visible = !_narrow;
        foreach (DropDown drop in _narrow ? new[] { _kindDrop, _category, _rule, _folder, _status, _groupDrop } : new[] { _category, _rule, _folder, _status })
        {
            int width = DropWidth(drop);
            drop.Bounds = new Rectangle(x, y, width, chipHeight);
            x += width + S(8);
        }

        if (!_narrow)
        {
            int width = S(_group.PreferredLogicalWidth);
            _group.Bounds = new Rectangle(Width - width, y, width, chipHeight);
        }

        Rectangle list = new(0, S(80), Width, Math.Max(0, Height - S(80)));
        _list.Bounds = list;
        _empty.Bounds = Rectangle.Inflate(list, -S(1), -S(1));
        _selectionBar.Bounds = SelectionBar.PlaceIn(list, DeviceDpi);
        _source.ShowFolder = Width >= S(720);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_narrow && _kindChips[0].Visible)
        {
            int x = _kindChips[0].Left - S(9);
            KitPaint.VerticalLine(e.Graphics, Tokens.Stroke, x, S(4), S(24), SF(1));
        }

        if (_group.Visible)
        {
            int width = KitPaint.MeasureWidth(Strings.Issues.GroupBy, Type.Caption);
            KitPaint.DrawText(e.Graphics, Strings.Issues.GroupBy, Type.Caption, Tokens.Text3, _group.Left - S(8) - width, S(54), width + 1);
        }
    }

    private bool _loadingRows;

    private async Task LoadRowsAsync(List<long> ids)
    {
        _loadingRows = true;
        try
        {
            IReadOnlyList<IssueRow> rows = await Task.Run(() => Services.Data.LoadIssueRows(ids)).ConfigureAwait(true);
            foreach (IssueRow row in rows)
            {
                Rows[row.Id] = row;
            }

            if (Rows.Count > 20_000)
            {
                Rows.Clear();
                foreach (IssueRow row in rows)
                {
                    Rows[row.Id] = row;
                }
            }

            _list.Invalidate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            State.Log.Warning("Issue rows could not be loaded", ex);
        }
        finally
        {
            _loadingRows = false;
        }
    }

    private int PlaceChip(Chip chip, int x)
    {
        int width = S(chip.PreferredLogicalWidth);
        chip.Bounds = new Rectangle(x, 0, width, S(28));
        return x + width + S(8);
    }

    private int DropWidth(DropDown drop) => KitPaint.MeasureWidth(drop.ShownText, Type.Body) + S(12 + 4 + 26);

    private static KitButton Button(string text, ButtonKind kind, IconName icon, EventHandler? click = null)
    {
        KitButton button = new() { Text = text, Kind = kind, Icon = icon };
        if (click is not null)
        {
            button.Click += click;
        }

        return button;
    }

    private void OnAllChip()
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        _all.On = true;
        foreach (Chip chip in _tierChips)
        {
            chip.On = false;
        }

        _updating = false;
        OnFilterChanged();
    }

    private void OnFilterChanged()
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        bool anyTier = _tierChips.Any(c => c.On);
        _all.On = !anyTier;
        _updating = false;
        List<Severity> tiers = [.. Tiers.Where((t, i) => _tierChips[i].On)];
        List<MediaKind> kinds = _narrow
            ? (_kindDrop.SelectedIndex > 0 ? [KindChips[_kindDrop.SelectedIndex - 1].Kind] : [])
            : [.. KindChips.Where((k, i) => _kindChips[i].On).Select(k => k.Kind)];
        FindingCategory[]? categories = _category.SelectedIndex switch
        {
            1 => [FindingCategory.Integrity, FindingCategory.Corruption],
            2 => [FindingCategory.Integrity],
            3 => [FindingCategory.Authenticity],
            4 => [FindingCategory.Quality],
            5 => [FindingCategory.Structure],
            _ => null,
        };
        string? rule = _rule.SelectedIndex > 0 && _data is not null ? _data.RuleCounts.Keys.OrderBy(k => k, StringComparer.Ordinal).ElementAtOrDefault(_rule.SelectedIndex - 1) : null;
        long? root = _folder.SelectedIndex > 0 && _folder.SelectedIndex - 1 < _roots.Count ? _roots[_folder.SelectedIndex - 1].RootId : null;
        FindingFilter filter = new()
        {
            Status = (FindingStatus)Math.Max(0, _status.SelectedIndex),
            Severities = tiers.Count > 0 ? tiers : null,
            Kinds = kinds.Count > 0 ? kinds : null,
            Categories = categories,
            RuleIds = rule is null ? null : [rule],
            RootId = root,
            FirstRun = _firstRun,
        };
        _query = _query with { Filter = filter, Group = (FindingGroup)_group.SelectedIndex };
        UpdateHighlights();
        PerformLayout();
        Reload();
    }

    private void UpdateHighlights()
    {
        foreach (DropDown drop in new[] { _kindDrop, _category, _rule, _folder, _status })
        {
            drop.Highlighted = drop.SelectedIndex > 0;
        }
    }

    private void UpdateEmptyState(IssuesData data)
    {
        _empty.Visible = data.Ids.Length == 0;
        _list.Visible = !_empty.Visible;
        if (!_empty.Visible)
        {
            return;
        }

        if (!data.HasFolders)
        {
            _empty.Icon = IconName.FolderAdd;
            _empty.Headline = Strings.Issues.NoFoldersHeadline;
            _empty.Body = string.Empty;
            _empty.SetButton(Strings.Library.AddFolder);
        }
        else if (!data.AnyScanned)
        {
            _empty.Icon = IconName.Scans;
            _empty.Headline = Strings.Issues.NotScannedHeadline;
            _empty.Body = string.Empty;
            _empty.SetButton(Strings.Shell.Scan);
        }
        else if (data.Query.Filtered)
        {
            _empty.Icon = IconName.Filter;
            _empty.Headline = Strings.Issues.FilteredHeadline;
            _empty.Body = string.Empty;
            _empty.SetButton(Strings.Issues.ClearFilters).Click += (_, _) => ClearFilters();
        }
        else
        {
            _empty.Icon = IconName.Check;
            _empty.Headline = Strings.Issues.NoneHeadline;
            string when = data.LastScan is DateTimeOffset last ? UiFormat.RecentDate(last, Services.Clock.UtcNow, inSentence: true) : Strings.Dash;
            _empty.Body = string.Format(CultureInfo.CurrentCulture, Strings.Issues.NoneText, UiFormat.Count(data.FilesScanned), when);
        }

        _empty.Invalidate();
    }

    private void UpdateSelectionBar()
    {
        int count = _list.SelectedIds.Count;
        bool show = count >= 2;
        _selectionBar.Visible = show;
        _list.BottomPadding = show ? 64 : 0;
        if (show)
        {
            _selectionBar.CountText = string.Format(CultureInfo.CurrentCulture, Strings.Issues.Selected, UiFormat.Count(count));
            bool live = Services.Operations is not null;
            string? fix = live ? Remediator.CommonFix(SelectedRows().Select(r => r.RuleId)) : null;
            _selectionBar.Buttons[0].Enabled = fix is not null;
            _selectionBar.Buttons[3].Enabled = live;
            _selectionBar.Invalidate();
        }
    }

    private List<IssueRow> SelectedRows() => [.. _list.SelectedIds.Select(id => Rows.TryGetValue(id, out IssueRow? row) ? row : null).OfType<IssueRow>()];

    private Task FixSelectedAsync()
    {
        List<IssueRow> rows = SelectedRows();
        return Remediator.CommonFix(rows.Select(r => r.RuleId)) is string fix ? RunAndReloadAsync([.. rows.Select(r => r.FileId).Distinct()], fix) : Task.CompletedTask;
    }

    private Task RunOnSelectedAsync(string actionId) => RunAndReloadAsync([.. SelectedRows().Select(r => r.FileId).Distinct()], actionId);

    private async Task RunAndReloadAsync(IReadOnlyList<long> fileIds, string actionId)
    {
        await Remediator.RunAsync(Services, this, fileIds, actionId).ConfigureAwait(true);
        if (!IsDisposed)
        {
            CloseDetail();
            Reload();
        }
    }

    private void OpenDetail(int index)
    {
        if (_source.RowAt(index) is not IssueRow row)
        {
            return;
        }

        _ = LoadDetailAsync(row.FileId);
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

    private void CloseDetail() => Services.ShowDrawer?.Invoke(null);

    private void OnListKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                if (_detail.Detail is not null)
                {
                    CloseDetail();
                }
                else
                {
                    _list.ClearSelection();
                }

                e.Handled = true;
                break;
            case Keys.I when e.Control:
                _ = IgnoreSelectedAsync();
                e.Handled = true;
                break;
            case Keys.C when e.Control:
                CopySelectedPaths();
                e.Handled = true;
                break;
            case Keys.Enter when e.Alt:
                ShowInFolder(_source.RowAt(_list.FocusedIndex)?.FullPath);
                e.Handled = true;
                break;
            case Keys.F5 when e.Shift:
                ScanSelected();
                e.Handled = true;
                break;
            case Keys.Up or Keys.Down when _detail.Detail is not null:
                BeginInvoke(() => OpenDetail(_list.FocusedIndex));
                break;
        }
    }

    private void ShowRowMenu(int index, Point screen)
    {
        IssueRow? row = _source.RowAt(index);
        if (row is null)
        {
            return;
        }

        PopupItem[] items =
        [
            new(Strings.Menu.Details, IconName.Info, Strings.Menu.ShortcutEnter, Tag: "details"),
            new(Strings.Menu.ScanAgain, IconName.Refresh, Strings.Menu.ShortcutShiftF5, Tag: "rescan"),
            PopupItem.Separator,
            new(Strings.Menu.Ignore, IconName.Ignore, Strings.Menu.ShortcutCtrlI, Tag: "ignore"),
            new(Strings.Menu.IgnoreRuleHere, IconName.Ignore, Tag: "ignore-rule"),
            PopupItem.Separator,
            new(Strings.Menu.ShowInFolder, IconName.Folder, Strings.Menu.ShortcutAltEnter, Tag: "show"),
            new(Strings.Menu.CopyPath, IconName.Copy, Strings.Menu.ShortcutCtrlC, Tag: "copy"),
            PopupItem.Separator,
            new(Strings.Menu.MoveToQuarantine, IconName.Quarantine, Strings.Menu.ShortcutDel, Enabled: Services.Operations is not null, Tag: "quarantine"),
        ];
        KitMenu.Show(_list, screen, items, item =>
        {
            switch (item.Tag as string)
            {
                case "details":
                    OpenDetail(index);
                    break;
                case "rescan":
                    ScanSelected();
                    break;
                case "ignore":
                    _ = IgnoreSelectedAsync();
                    break;
                case "ignore-rule":
                    _ = IgnoreRuleInFolderAsync(row);
                    break;
                case "show":
                    ShowInFolder(row.FullPath);
                    break;
                case "copy":
                    CopySelectedPaths();
                    break;
                case "quarantine":
                    _ = RunAndReloadAsync(SelectedRows() is { Count: > 0 } selected ? [.. selected.Select(r => r.FileId).Distinct()] : [row.FileId], ActionIds.Quarantine);
                    break;
            }
        });
    }

    /// <summary>Ignore a rule in a folder (subplan 09, Dialogs): hides this rule's findings there and below.</summary>
    private async Task IgnoreRuleInFolderAsync(IssueRow row)
    {
        if (FindForm() is not Form owner || !Core.Rules.RuleCatalog.TryGet(row.RuleId, out Core.Rules.RuleDefinition rule))
        {
            return;
        }

        string folder = Path.GetFileName(row.Folder.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : row.Folder;
        bool ignore = MessageDialog.Confirm(
            owner,
            string.Format(CultureInfo.CurrentCulture, Strings.Dialogs.IgnoreRuleTitle, rule.Title, folder),
            Strings.Dialogs.IgnoreRuleText,
            Strings.Kit.Cancel,
            Strings.Menu.Ignore,
            destructive: false);
        if (!ignore || Services.IsDemo)
        {
            return;
        }

        List<FileRecord> files = await Task.Run(() => Services.Database.Files.GetMany([row.FileId])).ConfigureAwait(true);
        if (files.FirstOrDefault() is FileRecord file)
        {
            await Services.Database.Ignores.AddAsync(file.RootId, file.DirId, row.RuleId, null).ConfigureAwait(true);
            Reload();
        }
    }

    private async Task IgnoreSelectedAsync()
    {
        long[] ids = _list.SelectedIds.Count > 0 ? [.. _list.SelectedIds] : _source.RowAt(_list.FocusedIndex) is IssueRow row ? [row.Id] : [];
        if (ids.Length == 0 || Services.IsDemo)
        {
            return;
        }

        await Services.Database.Findings.SetStatusAsync(ids, FindingStatus.Ignored).ConfigureAwait(true);
        _list.ClearSelection();
        Reload();
    }

    private async Task OnFindingActionAsync(long findingId, bool ignore)
    {
        if (Services.IsDemo)
        {
            return;
        }

        if (ignore)
        {
            await Services.Database.Findings.SetStatusAsync([findingId], FindingStatus.Ignored).ConfigureAwait(true);
            Reload();
            if (_detail.Detail is FileDetail d)
            {
                _ = LoadDetailAsync(d.FileId);
            }
        }
        else if (_detail.Detail is FileDetail detail)
        {
            await RescanFilesAsync([detail.FileId]).ConfigureAwait(true);
        }
    }

    private void ScanSelected()
    {
        List<long> files = [];
        foreach (long id in _list.SelectedIds)
        {
            if (Rows.TryGetValue(id, out IssueRow? row))
            {
                files.Add(row.FileId);
            }
        }

        if (files.Count == 0 && _source.RowAt(_list.FocusedIndex) is IssueRow focused)
        {
            files.Add(focused.FileId);
        }

        _ = RescanFilesAsync(files);
    }

    private async Task RescanFilesAsync(IReadOnlyList<long> fileIds)
    {
        if (fileIds.Count == 0 || Services.IsDemo || Services.CannotScanReason(true) is not null)
        {
            return;
        }

        try
        {
            List<FileRecord> files = await Task.Run(() => Services.Database.Files.GetMany([.. fileIds])).ConfigureAwait(true);
            ScanScope[] scopes = [.. files.GroupBy(f => f.RootId).Select(g => new ScanScope(g.Key, null, [.. g.Select(f => f.Id)]))];
            await Services.Scans.StartAsync(new ScanRequest(scopes, ScanDepth.Standard, RunTrigger.Manual), string.Format(CultureInfo.CurrentCulture, Strings.Scans.FilesScope, UiFormat.Count(fileIds.Count))).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            State.Log.Warning("Rescan not started: " + ex.Message);
        }
    }

    private void CopySelectedPaths()
    {
        List<string> paths = [];
        foreach (long id in _list.SelectedIds)
        {
            if (Rows.TryGetValue(id, out IssueRow? row))
            {
                paths.Add(row.FullPath);
            }
        }

        if (paths.Count == 0 && _source.RowAt(_list.FocusedIndex) is IssueRow focused)
        {
            paths.Add(focused.FullPath);
        }

        if (paths.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }
    }

    private static void ShowInFolder(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        System.Diagnostics.ProcessStartInfo start = new("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select," + path);
        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
    }

    /// <summary>Maps list positions to group headers, the summary row and finding rows.</summary>
    private sealed class IssuesSource(IssuesPage page) : IListSource
    {
        private readonly HashSet<string> _collapsed = [];
        private IssuesData? _data;
        private int[] _groupStarts = [];
        private int[] _idStarts = [];
        private bool _summary;
        private int _count;

        public bool ShowFolder { get; set; } = true;

        public int Count => _count;

        public void Rebuild(IssuesData data)
        {
            _data = data;
            _summary = data.Query.Filtered && data.Ids.Length > 0;
            _groupStarts = new int[data.Groups.Count];
            _idStarts = new int[data.Groups.Count];
            int position = _summary ? 1 : 0;
            int id = 0;
            for (int g = 0; g < data.Groups.Count; g++)
            {
                IssueGroup group = data.Groups[g];
                _groupStarts[g] = position;
                _idStarts[g] = id;
                position += 1 + (_collapsed.Contains(group.Key) ? 0 : group.Rows);
                id += group.Rows;
            }

            _count = data.Ids.Length == 0 ? 0 : position;
        }

        public IssueRow? RowAt(int index)
        {
            if (Locate(index) is (int group, int offset) && offset >= 0 && _data is not null)
            {
                long id = _data.Ids[_idStarts[group] + offset];
                return page.Rows.GetValueOrDefault(id);
            }

            return null;
        }

        public long IdOf(int index)
        {
            if (_summary && index == 0)
            {
                return long.MinValue;
            }

            if (Locate(index) is (int group, int offset))
            {
                return offset < 0 ? -1 - group : _data!.Ids[_idStarts[group] + offset];
            }

            return -1;
        }

        public void Fill(int index, ListRow row)
        {
            if (_data is null)
            {
                return;
            }

            if (_summary && index == 0)
            {
                row.IsGroup = true;
                row.IsSummary = true;
                row.Line1 = string.Format(CultureInfo.CurrentCulture, Strings.Issues.Showing, UiFormat.Count(_data.Ids.Length), UiFormat.Count(_data.TotalOpen));
                row.Link = Strings.Issues.ClearFilters;
                return;
            }

            if (Locate(index) is not (int group, int offset))
            {
                return;
            }

            IssueGroup g = _data.Groups[group];
            if (offset < 0)
            {
                row.IsGroup = true;
                row.Line1 = g.Label;
                row.Count = UiFormat.Count(g.Count);
                row.Severity = g.Tier;
                row.Collapsed = _collapsed.Contains(g.Key);
                return;
            }

            int position = _idStarts[group] + offset;
            long id = _data.Ids[position];
            if (!page.Rows.TryGetValue(id, out IssueRow? issue))
            {
                row.Loaded = false;
                page.RequestRows(position);
                return;
            }

            row.Severity = issue.Severity;
            row.Line1 = issue.Name;
            row.Line2 = issue.Line2;
            row.Folder = ShowFolder ? issue.Folder : null;
            row.Size = UiFormat.Size(issue.Size);
        }

        public void SetCollapsed(int index, bool collapsed)
        {
            if (_summary && index == 0)
            {
                page.ClearFilters();
                return;
            }

            if (_data is not null && Locate(index) is (int group, int offset) && offset < 0)
            {
                string key = _data.Groups[group].Key;
                if (collapsed)
                {
                    _collapsed.Add(key);
                }
                else
                {
                    _collapsed.Remove(key);
                }

                Rebuild(_data);
            }
        }

        public int GroupsBefore(int index)
        {
            int headers = _summary && index > 0 ? 1 : 0;
            for (int g = 0; g < _groupStarts.Length && _groupStarts[g] < index; g++)
            {
                headers++;
            }

            return headers;
        }

        private (int Group, int Offset)? Locate(int index)
        {
            for (int g = _groupStarts.Length - 1; g >= 0; g--)
            {
                if (index >= _groupStarts[g])
                {
                    int offset = index - _groupStarts[g] - 1;
                    return offset < (_collapsed.Contains(_data!.Groups[g].Key) ? 0 : _data.Groups[g].Rows) ? (g, offset) : null;
                }
            }

            return null;
        }
    }
}
