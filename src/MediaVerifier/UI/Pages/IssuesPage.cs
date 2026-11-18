using System.Globalization;
using MediaVerifier.App;
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
/// Every action takes its findings from one place (<see cref="TargetIds"/>) and reads what it needs about them from
/// the database, not from the rows that happen to be painted.
/// </summary>
internal sealed class IssuesPage : PageBase
{
    private static readonly Severity[] Tiers = [Severity.Critical, Severity.High, Severity.Medium, Severity.Low];
    private static readonly (MediaKind Kind, IconName Icon, string Label)[] KindChips = [(MediaKind.Photo, IconName.Photo, Strings.Kinds.Photos), (MediaKind.Video, IconName.Video, Strings.Kinds.Videos), (MediaKind.Audio, IconName.Music, Strings.Kinds.Music)];

    /// <summary>The row menu's Sort by (subplan 09, Issues: Sorting), in the order the spec lists them.</summary>
    private static readonly (FindingSort Sort, string Label)[] Sorts = [(FindingSort.Severity, Strings.Issues.SortSeverity), (FindingSort.Name, Strings.Issues.SortName), (FindingSort.Folder, Strings.Issues.SortFolder), (FindingSort.Size, Strings.Issues.SortSize), (FindingSort.Found, Strings.Issues.SortFirstSeen)];

    /// <summary>Rows read per query when a large selection is resolved (the Fix… state of a Ctrl+A on many findings).</summary>
    private const int ResolveChunk = 5_000;

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
    private readonly VirtualList _list = new() { Mode = ListMode.TwoLine, AccessibleName = Strings.Pages.Issues };
    private readonly EmptyState _empty = new() { Visible = false };
    private readonly SelectionBar _selectionBar = new() { Visible = false };

    /// <summary>"{n} new problems · Show" (subplan 09, Issues: new results during a scan), centred at the top of the list.</summary>
    private readonly KitButton _newResults = new() { Kind = ButtonKind.Primary, Compact = true, Visible = false };
    private bool _counting;
    private readonly IssuesSource _source;
    private readonly FileDetailView _detail = new();
    private readonly DrawerActions _drawer;
    private readonly ScrollPanel _detailScroll;
    private IssuesQuery _query = new(new FindingFilter());
    private IssuesData? _data;

    /// <summary>The kind filter, one state for the chips (wide) and the Kind drop-down (narrow): each shows what the other set.</summary>
    private readonly HashSet<MediaKind> _kinds = [];

    /// <summary>The rule and watched folder behind each item of the Rule and Folder lists (item 0 is "All").</summary>
    private string[] _ruleIds = [];
    private long[] _folderIds = [];

    /// <summary>Ids a row load did not return (the finding went away after the list was read): not asked for again.</summary>
    private readonly HashSet<long> _gone = [];

    /// <summary>A rule asked for by a request before the Rule list has it (the first load); null once shown.</summary>
    private string? _requestedRule;
    private bool _updating;
    private long? _firstRun;
    private bool _narrow;
    private bool _loadingRows;

    // The newest drawer load and Fix… state: an older one that finishes later is dropped.
    private int _detailRequest;
    private int _fixRequest;

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
        _drawer = new DrawerActions(Services, _detail, RunAndReloadAsync, fileId =>
        {
            ReloadAll();
            UiTask.Run(LoadDetailAsync(fileId));
        });

        _all.Toggled += (_, _) => OnAllChip();
        foreach (Chip chip in _tierChips)
        {
            chip.Toggled += (_, _) => OnFilterChanged();
        }

        foreach (Chip chip in _kindChips)
        {
            chip.Toggled += (_, _) => OnKindChips();
        }

        _kindDrop.Items.Add(Strings.Issues.Kind);
        foreach ((_, _, string label) in KindChips)
        {
            _kindDrop.Items.Add(label);
        }

        _kindDrop.SelectedIndex = 0;

        // The lists start with "All categories" (subplan 09); closed on it, the control shows its name ("Category").
        foreach (string category in new[] { Strings.Issues.AllCategories, Strings.Issues.Damage, Strings.Issues.Integrity, Strings.Issues.Authenticity, Strings.Issues.Quality, Strings.Issues.Structure })
        {
            _category.Items.Add(category);
        }

        _category.SelectedIndex = 0;
        _rule.Items.Add(Strings.Issues.AllRules);
        _rule.SelectedIndex = 0;
        _folder.Items.Add(Strings.Issues.AllFolders);
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
        UpdateDropLabels();

        // A rule the user picks replaces one a request asked for (subscribed first: it runs before the reload).
        _rule.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating)
            {
                _requestedRule = null;
            }
        };
        _kindDrop.SelectedIndexChanged += (_, _) => OnKindDrop();
        foreach (DropDown drop in new[] { _category, _rule, _folder, _status })
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

        // Owner request: a click shows the details, a double-click opens the file in its program.
        _list.ItemClicked += (_, index) => OpenDetail(index);

        _list.ItemDoubleClicked += (_, index) => UiTask.Run(DrawerActions.OpenFileAsync(Services, _source.RowAt(index)?.FullPath));
        _list.DetailsClicked += (_, index) => OpenDetail(index);
        _list.MoreRequested += (_, args) => UiTask.Run(ShowRowMenuAsync(args.Index, args.Screen));
        _list.SelectionChanged += (_, _) => UpdateSelectionBar();
        _list.KeyDown += OnListKey;
        _selectionBar.CloseToolTip = Strings.Kit.Close;
        _selectionBar.Closed += (_, _) => _list.ClearSelection();
        _selectionBar.SetButtons(
        [
            Button(Strings.Issues.Fix, ButtonKind.Primary, IconName.Tools, (_, _) => UiTask.Run(ShowFixMenuAsync())),
            Button(Strings.Issues.Ignore, ButtonKind.Secondary, IconName.Ignore, (_, _) => UiTask.Run(IgnoreAsync(TargetIds()))),
            Button(Strings.Issues.ScanAgain, ButtonKind.Secondary, IconName.Refresh, (_, _) => UiTask.Run(ScanAsync(TargetIds()))),
            Button(Strings.Issues.Quarantine, ButtonKind.Secondary, IconName.Quarantine, (_, _) => UiTask.Run(QuarantineAsync(TargetIds()))),
        ]);
        _selectionBar.Buttons[0].Enabled = false;
        _selectionBar.Buttons[3].Enabled = false;

        Controls.Add(_all);
        Controls.AddRange(_tierChips);
        Controls.AddRange(_kindChips);
        Controls.AddRange([_kindDrop, _category, _rule, _folder, _status, _groupDrop, _group, _list, _empty, _selectionBar, _newResults]);
        _selectionBar.BringToFront();
        _newResults.BringToFront();
        _newResults.Click += (_, _) =>
        {
            _newResults.Visible = false;
            Reload();
        };
        CreatePageTimer(2000, (_, _) => UiTask.Run(CheckNewResultsAsync()));
        int tab = 0;
        List<Control> order = [_all, .. _tierChips, .. _kindChips, _kindDrop, _category, _rule, _folder, _status, _group, _groupDrop, _newResults, _list, _selectionBar];
        foreach (Control control in order)
        {
            control.TabIndex = tab++;
        }
    }

    public override PageId Id => PageId.Issues;

    internal Dictionary<long, IssueRow> Rows { get; } = [];

    /// <summary>True while the drawer is open on this page's file (the drawer's own state, not the last file shown).</summary>
    private bool DrawerShown => _detailScroll.Parent is Drawer { IsOpen: true };

    /// <summary>Status: Ignored lists ignored findings; there Ignore (Ctrl+I, the menu, the bar) shows them again.</summary>
    private bool IgnoredView => _status.SelectedIndex == (int)FindingStatus.Ignored;

    public override void HandleRequest(object request)
    {
        if (request is IssueFindingRequest finding)
        {
            UiTask.Run(ShowFindingAsync(finding.FindingId));
            return;
        }

        if (request is IssuesFilterRequest filter)
        {
            // The request is the whole filter: what was chosen before (a rule, a folder, Ignored) would hide what it
            // asks to show.
            _firstRun = filter.FirstRun;
            _updating = true;
            _all.On = filter.Tier is null;
            for (int i = 0; i < Tiers.Length; i++)
            {
                _tierChips[i].On = filter.Tier == Tiers[i];
            }

            _kinds.Clear();
            if (filter.Kind is MediaKind kind)
            {
                _kinds.Add(kind);
            }

            ShowKinds();
            _category.SelectedIndex = 0;
            _folder.SelectedIndex = 0;
            _status.SelectedIndex = 0;
            _requestedRule = filter.RuleId;
            _rule.SelectedIndex = filter.RuleId is null ? 0 : Array.IndexOf(_ruleIds, filter.RuleId) + 1;
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
            long id = _data.Ids[i];
            if (!Rows.ContainsKey(id) && !_gone.Contains(id))
            {
                missing.Add(id);
            }
        }

        if (missing.Count > 0)
        {
            UiTask.Run(LoadRowsAsync(missing));
        }
    }

    /// <summary>
    /// While a scan runs, the shown filter is counted again every 2 seconds, off the UI thread: the chips take the new
    /// counts at once, and problems added since the list loaded show as the pill, which reloads the list when pressed.
    /// The list itself never changes under the pointer. Only the tiers the list shows count: with Critical chosen, new
    /// Low problems raised a pill whose Show changed nothing.
    /// </summary>
    internal async Task CheckNewResultsAsync()
    {
        if (_counting || _data is not IssuesData shown || !Services.Scans.Status.IsActive)
        {
            return;
        }

        _counting = true;
        try
        {
            FindingFilter filter = _query.Filter;
            SeverityCounts counts = await Task.Run(() => Services.Data.CountIssues(filter)).ConfigureAwait(true);
            if (IsDisposed || !ReferenceEquals(_data, shown) || !ReferenceEquals(filter, _query.Filter))
            {
                return;
            }

            _updating = true;
            _all.Count = (int)counts.Total;
            for (int i = 0; i < Tiers.Length; i++)
            {
                _tierChips[i].Count = counts[Tiers[i]];
            }

            _updating = false;
            long added = 0;
            foreach (Severity tier in Tiers)
            {
                if (filter.Severities is not { Count: > 0 } chosen || chosen.Contains(tier))
                {
                    added += Math.Max(0, counts[tier] - shown.TierCounts[tier]);
                }
            }

            if (added > 0)
            {
                _newResults.Text = added == 1 ? Strings.Issues.NewResultOne : string.Format(CultureInfo.CurrentCulture, Strings.Issues.NewResults, UiFormat.Count(added));
                _newResults.Visible = true;
                PerformLayout();
            }
        }
        finally
        {
            _counting = false;
        }
    }

    internal void ClearFilters()
    {
        _firstRun = null;
        _requestedRule = null;
        _updating = true;
        _all.On = true;
        foreach (Chip chip in _tierChips)
        {
            chip.On = false;
        }

        _kinds.Clear();
        ShowKinds();
        foreach (DropDown drop in new[] { _category, _rule, _folder, _status })
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

        // The Rule list's items, formatted here rather than on the UI thread.
        string[] rules = [.. data.RuleCounts.Keys.OrderBy(RuleName, StringComparer.CurrentCultureIgnoreCase)];
        string[] ruleItems = [.. rules.Select(rule => RuleItem(rule, data.RuleCounts[rule]))];
        return new Loaded(data, first, rules, ruleItems);
    }

    protected override void ApplyData(object data)
    {
        Loaded loaded = (Loaded)data;
        IssuesData issues = loaded.Issues;

        // A load started before the filter last changed (a chip clicked while the list loaded, a request right after
        // the page was shown) must not replace the newer one: it showed the previous list under the new filter.
        if (!ReferenceEquals(issues.Query, _query))
        {
            return;
        }

        _data = issues;
        Rows.Clear();
        _gone.Clear();
        foreach (IssueRow row in loaded.First)
        {
            Rows[row.Id] = row;
        }

        _updating = true;
        _all.Count = (int)issues.TierCounts.Total;
        for (int i = 0; i < Tiers.Length; i++)
        {
            _tierChips[i].Count = issues.TierCounts[Tiers[i]];
        }

        // The problems by name with their counts, rebuilt with the data. The chosen one stays chosen, also when no
        // finding of it is left under the other filters ("{title} (0)"): the list said "Rule" while still filtered by it.
        string? chosenRule = _requestedRule ?? CurrentRule();
        _requestedRule = null;
        List<string> ruleIds = [.. loaded.RuleIds];
        List<string> ruleItems = [.. loaded.RuleItems];
        if (chosenRule is not null && !issues.RuleCounts.ContainsKey(chosenRule))
        {
            int at = ruleIds.FindIndex(r => StringComparer.CurrentCultureIgnoreCase.Compare(RuleName(r), RuleName(chosenRule)) > 0);
            at = at < 0 ? ruleIds.Count : at;
            ruleIds.Insert(at, chosenRule);
            ruleItems.Insert(at, RuleItem(chosenRule, 0));
        }

        _ruleIds = [.. ruleIds];
        _rule.Items.Clear();
        _rule.Items.Add(Strings.Issues.AllRules);
        foreach (string item in ruleItems)
        {
            _rule.Items.Add(item);
        }

        _rule.SelectedIndex = chosenRule is null ? 0 : Array.IndexOf(_ruleIds, chosenRule) + 1;

        // The watched folders, rebuilt with the data and kept by id: filled once, an added folder shifted every item
        // and "Family Media" filtered another folder.
        long? chosenRoot = CurrentRoot();
        _folderIds = [.. issues.Roots.Select(r => r.RootId)];
        _folder.Items.Clear();
        _folder.Items.Add(Strings.Issues.AllFolders);
        foreach ((_, string name) in issues.Roots)
        {
            _folder.Items.Add(name);
        }

        int folder = chosenRoot is long rootId ? Array.IndexOf(_folderIds, rootId) + 1 : 0;
        _folder.SelectedIndex = folder;
        _updating = false;
        UpdateDropLabels();
        UpdateHighlights();
        _newResults.Visible = false;
        _source.Rebuild(issues);
        _list.Refresh();
        PruneSelection(issues);
        UpdateSelectionBar();
        UpdateEmptyState(issues);
        if (chosenRoot is not null && folder == 0)
        {
            // The chosen folder is no longer watched: no control would show that filter, so it goes.
            OnFilterChanged();
        }
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();

        // A drawer load still running must not open over the next page; a closed drawer is not closed again (that
        // replayed its slide-out over the next page).
        _detailRequest++;
        if (DrawerShown)
        {
            CloseDetail();
        }
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

        int chipHeight = S(28);
        int x = 0;
        int y = 0;

        // Chips and drop-downs flow left to right and wrap when the next would run past the page. With the drawer open
        // at the smallest window the page is 464 px wide, and the rows ran off it: the Low chip and a drop-down could
        // not be reached.
        void Flow(Control control, int width, int gapBefore = 0)
        {
            if (x > 0 && x + gapBefore + width > Width)
            {
                x = 0;
                y += S(40);
            }
            else if (x > 0)
            {
                x += gapBefore;
            }

            control.Bounds = new Rectangle(x, y, width, chipHeight);
            x += width + S(8);
        }

        Flow(_all, S(_all.PreferredLogicalWidth));
        foreach (Chip chip in _tierChips)
        {
            Flow(chip, S(chip.PreferredLogicalWidth));
        }

        foreach (Chip chip in _kindChips)
        {
            chip.Visible = !_narrow;
        }

        if (!_narrow)
        {
            for (int i = 0; i < _kindChips.Length; i++)
            {
                Flow(_kindChips[i], S(_kindChips[i].PreferredLogicalWidth), i == 0 ? S(9) : 0);
            }
        }

        // The drop-downs start their own row.
        x = 0;
        y += S(40);
        _kindDrop.Visible = _narrow;
        _groupDrop.Visible = _narrow;
        _group.Visible = !_narrow;
        foreach (DropDown drop in _narrow ? new[] { _kindDrop, _category, _rule, _folder, _status, _groupDrop } : new[] { _category, _rule, _folder, _status })
        {
            Flow(drop, DropWidth(drop));
        }

        if (!_narrow)
        {
            // Right-aligned on the drop-down row when it fits beside them with its label, on a row of its own if not.
            int width = S(_group.PreferredLogicalWidth);
            int label = KitPaint.MeasureWidth(Strings.Issues.GroupBy, Type.Caption) + S(16);
            if (x + label + width > Width)
            {
                y += S(40);
            }

            _group.Bounds = new Rectangle(Width - width, y, width, chipHeight);
        }

        int top = y + S(40);
        Rectangle list = new(0, top, Width, Math.Max(0, Height - top));
        _list.Bounds = list;
        _empty.Bounds = Rectangle.Inflate(list, -S(1), -S(1));
        _selectionBar.Bounds = SelectionBar.PlaceIn(list, DeviceDpi);
        _source.ShowFolder = Width >= S(720);
        int pill = S(_newResults.PreferredLogicalWidth);
        _newResults.Bounds = new Rectangle(list.X + ((list.Width - pill) / 2), list.Y + S(8), pill, S(28));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_narrow && _kindChips[0].Visible && _kindChips[0].Left > 0)
        {
            int x = _kindChips[0].Left - S(9);
            KitPaint.VerticalLine(e.Graphics, Tokens.Stroke, x, _kindChips[0].Top + S(4), _kindChips[0].Top + S(24), SF(1));
        }

        if (_group.Visible)
        {
            int width = KitPaint.MeasureWidth(Strings.Issues.GroupBy, Type.Caption);
            KitPaint.DrawText(e.Graphics, Strings.Issues.GroupBy, Type.Caption, Tokens.Text3, _group.Left - S(8) - width, _group.Top + S(14), width + 1);
        }
    }

    private async Task LoadRowsAsync(List<long> ids)
    {
        _loadingRows = true;
        try
        {
            IssuesData? requestedFor = _data;
            IReadOnlyList<IssueRow> rows = await Task.Run(() => Services.Data.LoadIssueRows(ids)).ConfigureAwait(true);
            if (ReferenceEquals(requestedFor, _data))
            {
                foreach (IssueRow row in rows)
                {
                    Rows[row.Id] = row;
                }

                // A finding deleted after the list was read (its file moved or its folder removed during a scan) comes
                // back from no load: remembered, so its row is not asked for again on every paint until the next reload.
                if (rows.Count < ids.Count)
                {
                    HashSet<long> returned = [.. rows.Select(r => r.Id)];
                    foreach (long id in ids)
                    {
                        if (!returned.Contains(id))
                        {
                            _gone.Add(id);
                        }
                    }
                }

                if (Rows.Count > 20_000)
                {
                    Rows.Clear();
                    foreach (IssueRow row in rows)
                    {
                        Rows[row.Id] = row;
                    }
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

    /// <summary>A kind chip was switched: the kinds are the chips that are on.</summary>
    private void OnKindChips()
    {
        if (_updating)
        {
            return;
        }

        _kinds.Clear();
        for (int i = 0; i < KindChips.Length; i++)
        {
            if (_kindChips[i].On)
            {
                _kinds.Add(KindChips[i].Kind);
            }
        }

        ShowKinds();
        OnFilterChanged();
    }

    /// <summary>A kind was chosen in the narrow layout's drop-down: that kind alone, or every kind.</summary>
    private void OnKindDrop()
    {
        if (_updating)
        {
            return;
        }

        _kinds.Clear();
        if (_kindDrop.SelectedIndex > 0)
        {
            _kinds.Add(KindChips[_kindDrop.SelectedIndex - 1].Kind);
        }

        ShowKinds();
        OnFilterChanged();
    }

    /// <summary>Shows the kind filter on both controls: the chips, and the drop-down with the one kind (or "Kind").</summary>
    private void ShowKinds()
    {
        bool updating = _updating;
        _updating = true;
        for (int i = 0; i < KindChips.Length; i++)
        {
            _kindChips[i].On = _kinds.Contains(KindChips[i].Kind);
        }

        _kindDrop.SelectedIndex = _kinds.Count == 1 ? Array.FindIndex(KindChips, k => _kinds.Contains(k.Kind)) + 1 : 0;
        _updating = updating;
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
        FindingCategory[]? categories = _category.SelectedIndex switch
        {
            1 => [FindingCategory.Integrity, FindingCategory.Corruption],
            2 => [FindingCategory.Integrity],
            3 => [FindingCategory.Authenticity],
            4 => [FindingCategory.Quality],
            5 => [FindingCategory.Structure],
            _ => null,
        };
        string? rule = _requestedRule ?? CurrentRule();
        FindingFilter filter = new()
        {
            Status = (FindingStatus)Math.Max(0, _status.SelectedIndex),
            Severities = tiers.Count > 0 ? tiers : null,
            Kinds = _kinds.Count > 0 ? [.. KindChips.Select(k => k.Kind).Where(_kinds.Contains)] : null,
            Categories = categories,
            RuleIds = rule is null ? null : [rule],
            RootId = CurrentRoot(),
            FirstRun = _firstRun,
        };
        _query = _query with { Filter = filter, Group = (FindingGroup)_group.SelectedIndex };
        UpdateDropLabels();
        UpdateHighlights();

        // What was selected may not be in the new list: acting on hidden rows (Ignore) or on nothing (Fix…) followed.
        _list.ClearSelection();
        PerformLayout();
        Reload();
    }

    private string? CurrentRule() => _rule.SelectedIndex > 0 ? _ruleIds.ElementAtOrDefault(_rule.SelectedIndex - 1) : null;

    private long? CurrentRoot() => _folder.SelectedIndex > 0 && _folder.SelectedIndex - 1 < _folderIds.Length ? _folderIds[_folder.SelectedIndex - 1] : null;

    /// <summary>On its first item ("All categories") a closed drop-down shows its name, as the reference draws it.</summary>
    private void UpdateDropLabels()
    {
        _category.DisplayFormat = _category.SelectedIndex <= 0 ? Strings.Issues.Category : null;
        _rule.DisplayFormat = _rule.SelectedIndex <= 0 ? Strings.Issues.Rule : null;
        _folder.DisplayFormat = _folder.SelectedIndex <= 0 ? Strings.Issues.Folder : null;
        _category.Invalidate();
        _rule.Invalidate();
        _folder.Invalidate();
    }

    private void UpdateHighlights()
    {
        foreach (DropDown drop in new[] { _category, _rule, _folder, _status })
        {
            drop.Highlighted = drop.SelectedIndex > 0;
        }

        // Two kinds chosen with the chips have no single item to show: the border still says a kind filter is on.
        _kindDrop.Highlighted = _kinds.Count > 0;
    }

    /// <summary>Drops a selection that holds findings the new list no longer has (resolved by a scan, ignored, filtered away).</summary>
    private void PruneSelection(IssuesData issues)
    {
        IReadOnlySet<long> selected = _list.SelectedIds;
        if (selected.Count == 0)
        {
            return;
        }

        int present = 0;
        foreach (long id in issues.Ids)
        {
            if (selected.Contains(id) && ++present == selected.Count)
            {
                return;
            }
        }

        _list.ClearSelection();
    }

    private void UpdateEmptyState(IssuesData data)
    {
        _empty.Visible = data.Ids.Length == 0;
        _list.Visible = !_empty.Visible;
        if (!_empty.Visible)
        {
            return;
        }

        _empty.ClearButton();
        if (!data.HasFolders)
        {
            _empty.Icon = IconName.FolderAdd;
            _empty.Headline = Strings.Issues.NoFoldersHeadline;
            _empty.Body = string.Empty;
            _empty.SetButton(Strings.Library.AddFolder).Click += (_, _) => Services.Navigate?.Invoke(PageId.Library, new AddFolderRequest(null));
        }
        else if (!data.AnyScanned)
        {
            _empty.Icon = IconName.Scans;
            _empty.Headline = Strings.Issues.NotScannedHeadline;
            _empty.Body = string.Empty;
            _empty.SetButton(Strings.Shell.Scan).Click += (_, _) =>
            {
                if (FindForm() is Form owner)
                {
                    UiTask.Run(ScanLauncher.StartLibraryScanAsync(owner, Services, State.Settings.Scanning.DefaultDepth, null));
                }
            };
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
        if (!show)
        {
            _fixRequest++;
            return;
        }

        _selectionBar.CountText = string.Format(CultureInfo.CurrentCulture, Strings.Issues.Selected, UiFormat.Count(count));
        string ignore = IgnoredView ? Strings.Menu.StopIgnoring : Strings.Issues.Ignore;
        if (_selectionBar.Buttons[1].Text != ignore)
        {
            _selectionBar.Buttons[1].Text = ignore;
            _selectionBar.PerformLayout();
        }

        _selectionBar.Buttons[3].Enabled = Services.Operations is not null;
        _selectionBar.Invalidate();
        UiTask.Run(UpdateFixStateAsync());
    }

    /// <summary>
    /// Fix… is enabled when the selected findings share a fix (subplan 09), worked out from all of them: rows that were
    /// never painted are read from the database, so a Ctrl+A over thousands of findings is judged on every one.
    /// </summary>
    private async Task UpdateFixStateAsync()
    {
        int request = ++_fixRequest;
        if (Services.Operations is null)
        {
            SetFixState([]);
            return;
        }

        // The bar shows for two or more selected findings, so the selection is the target; its order does not matter.
        long[] ids = [.. _list.SelectedIds];
        if (!ids.All(Rows.ContainsKey))
        {
            // Off until the answer is in; a run of Shift+Down presses asks once.
            SetFixState(null);
            await Task.Delay(150).ConfigureAwait(true);
            if (request != _fixRequest)
            {
                return;
            }
        }

        IReadOnlyList<string> fixes = await CommonFixesAsync(ids, request).ConfigureAwait(true);
        if (request == _fixRequest && !IsDisposed)
        {
            SetFixState(fixes);
        }
    }

    /// <summary>Fix… on (fixes in common), off with the reason (none in common), or off while the answer is worked out.</summary>
    private void SetFixState(IReadOnlyList<string>? fixes)
    {
        KitButton fix = _selectionBar.Buttons[0];
        fix.Enabled = fixes is { Count: > 0 };
        KitToolTip.Set(fix, fixes is { Count: 0 } ? Strings.Issues.NeedDifferentFixes : string.Empty);
    }

    /// <summary>The fixes every one of the findings can take; rows not in the paint cache are read in chunks, off the UI thread.</summary>
    private async Task<IReadOnlyList<string>> CommonFixesAsync(long[] ids, int request)
    {
        List<IReadOnlyList<string>> known = [];
        List<long> missing = [];
        foreach (long id in ids)
        {
            if (Rows.TryGetValue(id, out IssueRow? row))
            {
                known.Add(row.Fixes);
            }
            else
            {
                missing.Add(id);
            }
        }

        IReadOnlyList<string> common = Remediator.CommonFixes(known);
        if (missing.Count == 0 || (known.Count > 0 && common.Count == 0))
        {
            return common;
        }

        return await Task.Run(() =>
        {
            IReadOnlyList<string>? result = known.Count > 0 ? common : null;
            foreach (long[] chunk in missing.Chunk(ResolveChunk))
            {
                if (Volatile.Read(ref _fixRequest) != request)
                {
                    return [];
                }

                IReadOnlyList<IssueRow> rows = Services.Data.LoadIssueRows(chunk);
                result = Remediator.CommonFixes(result is null ? rows.Select(r => r.Fixes) : rows.Select(r => r.Fixes).Prepend(result));
                if (result.Count == 0)
                {
                    break;
                }
            }

            return result ?? [];
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// The findings an action applies to, in list order: a clicked row outside the selection alone (its menu), else the
    /// selection, else the focused row. Fix…, Ignore, Scan again, Quarantine, Copy path and their keys all use it.
    /// </summary>
    internal long[] TargetIds(IssueRow? clicked = null)
    {
        IReadOnlySet<long> selected = _list.SelectedIds;
        if (clicked is not null && !selected.Contains(clicked.Id))
        {
            return [clicked.Id];
        }

        if (selected.Count > 0 && _data is not null)
        {
            List<long> ids = new(selected.Count);
            foreach (long id in _data.Ids)
            {
                if (selected.Contains(id))
                {
                    ids.Add(id);
                    if (ids.Count == selected.Count)
                    {
                        break;
                    }
                }
            }

            return [.. ids];
        }

        return _source.RowAt(_list.FocusedIndex) is IssueRow focused ? [focused.Id] : [];
    }

    /// <summary>The rows of the findings, in order: the painted ones from the cache, the rest read off the UI thread.</summary>
    private async Task<IReadOnlyList<IssueRow>> ResolveRowsAsync(long[] ids)
    {
        List<long> missing = [.. ids.Where(id => !Rows.ContainsKey(id))];
        Dictionary<long, IssueRow> loaded = [];
        if (missing.Count > 0)
        {
            foreach (IssueRow row in await Task.Run(() => Services.Data.LoadIssueRows(missing)).ConfigureAwait(true))
            {
                loaded[row.Id] = row;
            }
        }

        List<IssueRow> rows = new(ids.Length);
        foreach (long id in ids)
        {
            if ((Rows.GetValueOrDefault(id) ?? loaded.GetValueOrDefault(id)) is IssueRow row)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static long[] Files(IReadOnlyList<IssueRow> rows) => [.. rows.Select(r => r.FileId).Distinct()];

    /// <summary>The recommended fix of the first finding, when every one of them can take it; null otherwise.</summary>
    private static string? RecommendedFor(IReadOnlyList<IssueRow> rows) =>
        rows.Count > 0 && rows[0].RecommendedFix is string fix && rows.All(r => r.Fixes.Contains(fix)) ? fix : null;

    /// <summary>Fix… (subplan 09, selection bar): the fixes valid for every selected finding, then the chosen one runs.</summary>
    private async Task ShowFixMenuAsync()
    {
        long[] ids = TargetIds();
        IReadOnlyList<IssueRow> rows = await ResolveRowsAsync(ids).ConfigureAwait(true);
        IReadOnlyList<string> fixes = Services.Operations is null ? [] : Remediator.CommonFixes(rows.Select(r => r.Fixes));
        if (IsDisposed)
        {
            return;
        }

        if (fixes.Count == 0)
        {
            SetFixState(fixes);
            return;
        }

        long[] files = Files(rows);
        PopupItem[] items = [.. fixes.Select(fix => new PopupItem(LiveUiData.ActionName(fix), IconName.Tools, Tag: fix))];
        KitMenu.ShowBelow(_selectionBar.Buttons[0], items, item => UiTask.Run(RunAndReloadAsync(files, (string)item.Tag!)));
    }

    /// <summary>Ctrl+Enter and the row menu's fix: the recommended fix, when every target can take it; otherwise says why nothing ran.</summary>
    private async Task RunRecommendedAsync(IssueRow? clicked)
    {
        if (Services.Operations is null)
        {
            return;
        }

        IReadOnlyList<IssueRow> rows = await ResolveRowsAsync(TargetIds(clicked)).ConfigureAwait(true);
        if (rows.Count == 0)
        {
            return;
        }

        if (RecommendedFor(rows) is string fix)
        {
            await RunAndReloadAsync(Files(rows), fix).ConfigureAwait(true);
        }
        else
        {
            Services.ShowToast?.Invoke(new ToastContent(Severity.None, rows.Count > 1 ? Strings.Issues.NeedDifferentFixes : Strings.Issues.NoFix, string.Empty));
        }
    }

    private async Task QuarantineAsync(long[] ids)
    {
        if (Services.Operations is null || ids.Length == 0)
        {
            return;
        }

        IReadOnlyList<IssueRow> rows = await ResolveRowsAsync(ids).ConfigureAwait(true);
        await RunAndReloadAsync(Files(rows), ActionIds.Quarantine).ConfigureAwait(true);
    }

    private async Task ScanAsync(long[] ids)
    {
        IReadOnlyList<IssueRow> rows = await ResolveRowsAsync(ids).ConfigureAwait(true);
        await DrawerActions.RescanAsync(Services, Files(rows)).ConfigureAwait(true);
    }

    private async Task CopyAsync(long[] ids)
    {
        IReadOnlyList<IssueRow> rows = await ResolveRowsAsync(ids).ConfigureAwait(true);
        string[] paths = [.. rows.Select(r => r.FullPath).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (paths.Length > 0 && !IsDisposed)
        {
            DrawerActions.Copy(Services, paths, DrawerActions.PathsCopied(paths.Length));
        }
    }

    /// <summary>
    /// Runs a fix (dialog A first). When it ran, the drawer closes and the selection goes: the repaired findings are
    /// resolved, and a selection kept on them sent them to Ignore. The shell reloads the page and its counts. Cancelling
    /// the dialog leaves the drawer as it was.
    /// </summary>
    private async Task RunAndReloadAsync(IReadOnlyList<long> fileIds, string actionId)
    {
        if (await Remediator.RunAsync(Services, this, fileIds, actionId).ConfigureAwait(true) && !IsDisposed)
        {
            if (DrawerShown)
            {
                CloseDetail();
            }

            _list.ClearSelection();
            if (FindForm() is not MainForm)
            {
                Reload();
            }
        }
    }

    private void OpenDetail(int index)
    {
        if (_source.RowAt(index) is not IssueRow row)
        {
            return;
        }

        UiTask.Run(LoadDetailAsync(row.FileId));
    }

    /// <summary>
    /// Opens the drawer on a file (subplan 09: under 100 ms). It opens on the database's answer; a healthy copy, which
    /// can mean waiting on an offline backup drive, is looked for afterwards and fills in the fix. Only the newest
    /// request is shown, and only while the page is.
    /// </summary>
    private async Task LoadDetailAsync(long fileId)
    {
        int request = ++_detailRequest;
        FindingStatus status = (FindingStatus)Math.Max(0, _status.SelectedIndex);
        FileDetail? detail = await Task.Run(() => Services.Data.LoadFileDetail(fileId, status, findCopies: false)).ConfigureAwait(true);
        if (detail is null || IsDisposed || request != _detailRequest || !IsActive)
        {
            return;
        }

        if (!DrawerShown)
        {
            // Laid out first at the width the drawer opens with; while it is open (Up and Down) the content keeps the
            // width the drawer gives it, also after the user resized it.
            _detail.Width = Math.Max(1, Dpi.Scale(Drawer.DefaultWidth, DeviceDpi) - 1);
        }

        _detail.Bind(detail, Services.Clock.UtcNow, detail.RecommendedAction);
        _detailScroll.ScrollToTop();
        Services.ShowDrawer?.Invoke(_detailScroll);
        if (detail.CopiesPending)
        {
            FileDetail? withCopies = await Task.Run(() => Services.Data.LoadFileDetail(fileId, status, findCopies: true)).ConfigureAwait(true);
            if (withCopies is not null && !IsDisposed && request == _detailRequest)
            {
                _detail.UpdateFixes(withCopies);
            }
        }
    }

    private void CloseDetail()
    {
        _detailRequest++;
        Services.ShowDrawer?.Invoke(null);
    }

    /// <summary>Opens the drawer of the file a finding belongs to (a notification's link); nothing for an unknown id.</summary>
    private async Task ShowFindingAsync(long findingId)
    {
        long? fileId = await Task.Run(() => Services.Data.FileOfFinding(findingId)).ConfigureAwait(true);
        if (fileId is long id && !IsDisposed)
        {
            await LoadDetailAsync(id).ConfigureAwait(true);
        }
    }

    private void OnListKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                // The window closes an open drawer before the list sees Esc; here it clears the selection (subplan 09,
                // Keyboard: close the menu, dialog or drawer, then clear the selection).
                _list.ClearSelection();
                e.Handled = true;
                break;
            case Keys.I when e.Control:
                UiTask.Run(IgnoreAsync(TargetIds()));
                e.Handled = true;
                break;
            case Keys.C when e.Control:
                UiTask.Run(CopyAsync(TargetIds()));
                e.Handled = true;
                break;
            case Keys.Enter when e.Control:
                UiTask.Run(RunRecommendedAsync(null));
                e.Handled = true;
                break;
            case Keys.Delete when Services.Operations is not null:
                // The row menu's Del: Move to quarantine for the selection (dialog A asks first).
                UiTask.Run(QuarantineAsync(TargetIds()));
                e.Handled = true;
                break;
            case Keys.Enter when e.Alt:
                ShellActions.ShowInFolder(_source.RowAt(_list.FocusedIndex)?.FullPath);
                e.Handled = true;
                break;
            case Keys.F5 when e.Shift:
                UiTask.Run(ScanAsync(TargetIds()));
                e.Handled = true;
                break;
            case Keys.Up or Keys.Down when DrawerShown:
                // With the drawer open the arrows browse files; with it closed (by the user) they only move. The list
                // moves its focus after this, so the file is opened once it has; not if the drawer closed meanwhile.
                BeginInvoke(() =>
                {
                    if (DrawerShown)
                    {
                        OpenDetail(_list.FocusedIndex);
                    }
                });
                break;
        }
    }

    /// <summary>The row menu (subplan 09, C): its actions apply to the selection the row is in, or to the row alone.</summary>
    private async Task ShowRowMenuAsync(int index, Point screen)
    {
        if (_source.RowAt(index) is not IssueRow row)
        {
            return;
        }

        bool live = Services.Operations is not null;
        long[] targets = TargetIds(row);
        string? fix = live ? RecommendedFor(await ResolveRowsAsync(targets).ConfigureAwait(true)) : null;
        if (IsDisposed)
        {
            return;
        }

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
            new(Strings.Menu.ScanAgain, IconName.Refresh, Strings.Menu.ShortcutShiftF5, Tag: "rescan"),
            PopupItem.Separator,
        ]);

        // In the Ignored list the same item shows the findings again.
        items.Add(new(IgnoredView ? Strings.Menu.StopIgnoring : Strings.Menu.Ignore, IconName.Ignore, Strings.Menu.ShortcutCtrlI, Tag: "ignore"));
        if (!IgnoredView)
        {
            items.Add(new(Strings.Menu.IgnoreRuleHere, IconName.Ignore, Tag: "ignore-rule"));
        }

        items.AddRange(
        [
            PopupItem.Separator,
            new(Strings.Menu.OpenFile, IconName.Open, Tag: "open"),
            new(Strings.Menu.ShowInFolder, IconName.Folder, Strings.Menu.ShortcutAltEnter, Tag: "show"),
            new(Strings.Menu.CopyPath, IconName.Copy, Strings.Menu.ShortcutCtrlC, Tag: "copy"),
            PopupItem.Separator,
            new(Strings.Menu.MoveToQuarantine, IconName.Quarantine, Strings.Menu.ShortcutDel, Enabled: live, Tag: "quarantine"),
            PopupItem.Separator,
            new(Strings.Issues.SortBy, IconName.SortDown, Tag: "sort"),
        ]);
        KitMenu.Show(_list, screen, items, item =>
        {
            switch (item.Tag as string)
            {
                case "details":
                    OpenDetail(index);
                    break;
                case "fix" when fix is not null:
                    UiTask.Run(RunRecommendedAsync(row));
                    break;
                case "rescan":
                    UiTask.Run(ScanAsync(targets));
                    break;
                case "ignore":
                    UiTask.Run(IgnoreAsync(targets));
                    break;
                case "ignore-rule":
                    UiTask.Run(IgnoreRuleInFolderAsync(row));
                    break;
                case "open":
                    UiTask.Run(DrawerActions.OpenFileAsync(Services, row.FullPath));
                    break;
                case "show":
                    ShellActions.ShowInFolder(row.FullPath);
                    break;
                case "copy":
                    UiTask.Run(CopyAsync(targets));
                    break;
                case "quarantine":
                    UiTask.Run(QuarantineAsync(targets));
                    break;
                case "sort":
                    // The first menu closes before the choices open where it was.
                    BeginInvoke(() => ShowSortMenu(screen));
                    break;
            }
        });
    }

    /// <summary>Sort by (subplan 09, Issues: Sorting): the order within each group; the current one is ticked.</summary>
    private void ShowSortMenu(Point screen)
    {
        if (IsDisposed)
        {
            return;
        }

        PopupItem[] items = [.. Sorts.Select(s => new PopupItem(s.Label, s.Sort == _query.Sort ? IconName.Check : null, Tag: s.Sort))];
        KitMenu.Show(_list, screen, items, item =>
        {
            if (item.Tag is FindingSort sort && sort != _query.Sort)
            {
                _query = _query with { Sort = sort, Descending = false };
                Reload();
            }
        });
    }

    /// <summary>Ignore a rule in a folder (subplan 09, Dialogs): hides this rule's findings there and below.</summary>
    private async Task IgnoreRuleInFolderAsync(IssueRow row)
    {
        if (FindForm() is not Form owner || !RuleCatalog.TryGet(row.RuleId, out RuleDefinition rule))
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
            long ignoreId = await Services.Database.Ignores.AddAsync(file.RootId, file.DirId, row.RuleId, null).ConfigureAwait(true);
            _list.ClearSelection();
            ReloadAll();
            Services.ShowToast?.Invoke(new ToastContent(
                Severity.None,
                Strings.Issues.Ignored,
                string.Format(CultureInfo.CurrentCulture, Strings.Issues.IgnoredRuleIn, rule.Title, folder),
                Strings.Dialogs.Undo,
                () => UiTask.Run(DrawerActions.UndoAsync(Services, () => Services.Database.Ignores.RemoveAsync(ignoreId), ReloadAll))));
        }
    }

    /// <summary>Ignore (Ctrl+I, the menu, the bar), or in the Ignored list Stop ignoring, with a toast saying how many.</summary>
    private async Task IgnoreAsync(long[] ids)
    {
        if (ids.Length == 0 || Services.IsDemo)
        {
            return;
        }

        if (IgnoredView)
        {
            int reopened = await Services.Database.Findings.ReopenAsync(ids).ConfigureAwait(true);
            _list.ClearSelection();
            ReloadAll();
            DrawerActions.ShowReopened(Services, reopened);
            return;
        }

        await Services.Database.Findings.SetStatusAsync(ids, FindingStatus.Ignored).ConfigureAwait(true);
        _list.ClearSelection();
        ReloadAll();
        DrawerActions.ShowIgnored(Services, ids, ReloadAll);
    }

    /// <summary>
    /// After findings changed here (ignored, shown again, an undo): the list and the shell's counts, so the Issues
    /// badge agrees with the list. The page reloads with the shell's current page, or when it is shown next.
    /// </summary>
    private void ReloadAll()
    {
        if (!IsDisposed && !Remediator.RefreshShell(this))
        {
            Reload();
        }
    }

    /// <summary>A rule's name as the Rule filter lists it (its title; the id when it is not in the catalog).</summary>
    private static string RuleName(string ruleId) => RuleCatalog.TryGet(ruleId, out RuleDefinition rule) ? rule.Title : ruleId;

    private static string RuleItem(string ruleId, int count) => string.Create(CultureInfo.CurrentCulture, $"{RuleName(ruleId)} ({UiFormat.Count(count)})");

    /// <summary>A load: the snapshot, its first rows and the Rule list's items.</summary>
    private sealed record Loaded(IssuesData Issues, IReadOnlyList<IssueRow> First, string[] RuleIds, string[] RuleItems);

    /// <summary>Maps list positions to group headers, the summary row and finding rows.</summary>
    private sealed class IssuesSource(IssuesPage page) : IListSource
    {
        private readonly HashSet<string> _collapsed = [];
        private IssuesData? _data;
        private int[] _groupStarts = [];
        private int[] _idStarts = [];
        private int[] _shownRows = [];
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
            _shownRows = new int[data.Groups.Count];
            int position = _summary ? 1 : 0;
            int id = 0;
            for (int g = 0; g < data.Groups.Count; g++)
            {
                IssueGroup group = data.Groups[g];

                // Never more rows than ids are left: counts read in another snapshot than the ids indexed past the end.
                int rows = Math.Clamp(group.Rows, 0, data.Ids.Length - id);
                _groupStarts[g] = position;
                _idStarts[g] = id;
                _shownRows[g] = _collapsed.Contains(group.Key) ? 0 : rows;
                position += 1 + _shownRows[g];
                id += rows;
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
                row.Line1 = _data.ShowingText;
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
                row.Count = g.CountText;
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
            row.Column = issue.Problem;
            row.Line2Narrow = issue.Line2WithProblem;
            row.Folder = ShowFolder ? issue.Folder : null;
            row.Size = issue.SizeText;
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

        /// <summary>The headers before a position, by binary search: group starts strictly increase (O(log groups)).</summary>
        public int GroupsBefore(int index)
        {
            int headers = _summary && index > 0 ? 1 : 0;
            int found = Array.BinarySearch(_groupStarts, index);
            return headers + (found >= 0 ? found : ~found);
        }

        /// <summary>The group a position falls in and the offset in it (-1 for its header), by binary search.</summary>
        private (int Group, int Offset)? Locate(int index)
        {
            int g = Array.BinarySearch(_groupStarts, index);
            if (g < 0)
            {
                g = ~g - 1;
            }

            if (g < 0)
            {
                return null;
            }

            int offset = index - _groupStarts[g] - 1;
            return offset < _shownRows[g] ? (g, offset) : null;
        }
    }
}
