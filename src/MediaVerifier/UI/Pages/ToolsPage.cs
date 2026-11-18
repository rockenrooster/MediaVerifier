using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// Tools (subplan 09, Tools; task UI-09): five cards whose buttons open sub-views (quarantine, the
/// operations journal, media engine, database) or the Create report dialog.
/// </summary>
internal sealed class ToolsPage : PageBase
{
    private const int CardPitch = 232;
    private readonly ToolCard[] _cards;
    private readonly IconButton _back = new() { Icon = IconName.ChevronLeft, TabIndex = 0 };
    private readonly VirtualList _table = new() { Mode = ListMode.Table, Icons = false, Framed = true, TabIndex = 2 };
    private readonly EmptyState _empty = new() { TabIndex = 3 };
    private readonly FactsView _facts;
    private readonly TableSource _source = new();
    private readonly BatchView _batchView;
    private readonly KitButton _restore = new() { Kind = ButtonKind.Secondary, Text = Strings.Tools.Restore, TabIndex = 4 };
    private readonly KitButton _delete = new() { Kind = ButtonKind.Secondary, Text = Strings.Tools.DeleteForGood, TabIndex = 5 };
    private readonly KitButton _openFolder = new() { Kind = ButtonKind.Secondary, Text = Strings.Tools.OpenFolder, TabIndex = 6 };
    private readonly KitButton _deleteAll = new() { Kind = ButtonKind.Secondary, Text = Strings.Tools.DeleteAll, TabIndex = 7 };
    private readonly KitButton _undo = new() { Kind = ButtonKind.Secondary, Icon = IconName.Undo, Text = Strings.Tools.Undo, TabIndex = 4 };
    private List<QuarantineEntry> _kept = [];
    private List<OperationBatch> _batches = [];
    private ToolsData? _data;
    private int _view = -1;

    /// <summary>The newest request to show a batch in the drawer; an older one that finishes later is dropped.</summary>
    private long _batchRequest;

    /// <summary>True while Restore, Delete for good or Delete all… runs: one at a time.</summary>
    private bool _working;

    public ToolsPage(UiServices services)
        : base(services)
    {
        _facts = new FactsView(this) { TabIndex = 1 };
        _batchView = new BatchView(this);
        _cards =
        [
            new ToolCard(IconName.Quarantine, Strings.Tools.Quarantine, Strings.Tools.QuarantineText, Strings.Tools.OpenQuarantine, () => ShowView(0)),
            new ToolCard(IconName.Undo, Strings.Tools.Operations, Strings.Tools.OperationsText, Strings.Tools.OpenJournal, () => ShowView(1)),
            new ToolCard(IconName.Report, Strings.Tools.Reports, Strings.Tools.ReportsText, Strings.Tools.CreateReport, CreateReport),
            new ToolCard(IconName.Engine, Strings.Tools.MediaEngine, Strings.Tools.MediaEngineText, Strings.Tools.ViewLicences, () => ShowView(3)),
            new ToolCard(IconName.Database, Strings.Tools.Database, Strings.Tools.DatabaseText, Strings.Tools.Manage, () => ShowView(4)),
        ];
        for (int i = 0; i < _cards.Length; i++)
        {
            _cards[i].TabIndex = 10 + i;
        }

        _back.ToolTipText = Strings.Tools.BackToTools;
        _back.Click += (_, _) => ShowView(-1);
        _table.Source = _source;
        _table.SelectionChanged += (_, _) => OnSelectionChanged();
        _table.ItemActivated += (_, index) => OpenBatch(index);
        _restore.Click += (_, _) => UiTask.Run(RestoreSelectedAsync());
        _delete.Click += (_, _) => UiTask.Run(DeleteForGoodAsync(ChosenKept()));
        _deleteAll.Click += (_, _) => UiTask.Run(DeleteForGoodAsync(_kept));
        _openFolder.Click += (_, _) => ShellActions.ShowInFolder(ChosenKept().FirstOrDefault()?.Operation.BackupPath);
        _undo.Click += (_, _) => UiTask.Run(UndoSelectedAsync());
        Controls.AddRange([.. _cards, _back, _facts, _table, _empty, _restore, _delete, _openFolder, _deleteAll, _undo]);
        ShowView(-1);
    }

    public override PageId Id => PageId.Tools;

    internal ToolsData? Data => _data;

    /// <summary>The card grid scrolls when its rows do not fit (two columns in a small window: Database's Manage was out of reach).</summary>
    public override int MeasureContentHeight(int width) => _view switch
    {
        < 0 => (((_cards.Length + Columns(width) - 1) / Columns(width)) * S(CardPitch)) - S(12),
        3 or 4 => _facts.MeasureHeight() + S(56),
        _ => 0,
    };

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        CloseBatch();
    }

    protected override object? LoadData() => Services.Data.LoadTools();

    protected override void ApplyData(object data)
    {
        _data = (ToolsData)data;
        DateTimeOffset now = Services.Clock.UtcNow;
        ToolsData d = _data;
        _cards[0].Facts = [(Strings.Tools.Items, UiFormat.Count(d.QuarantineItems)), (Strings.Tools.Size, UiFormat.Size(d.QuarantineBytes)), (Strings.Tools.OldestRemovedIn, d.OldestRemovedInDays is int days ? DaysText(days) : Strings.Dash)];
        _cards[1].Facts = [(Strings.Tools.Last, d.LastOperation ?? Strings.Dash), (Strings.Tools.When, d.LastOperationWhen is DateTimeOffset when ? UiFormat.RecentDate(when, now) : Strings.Dash), (Strings.Tools.CanBeUndone, d.LastCanUndo is bool undo ? (undo ? Strings.Tools.Yes : Strings.Tools.No) : Strings.Dash)];
        _cards[2].Facts = [(Strings.Tools.OpenProblems, UiFormat.Count(d.OpenProblems)), (Strings.Tools.LastReport, d.LastReport is DateTimeOffset report ? UiFormat.RecentDay(report, now) : Strings.Tools.Never)];
        _cards[3].Facts = [(Strings.Tools.Version, Services.MediaEngine.Version), (Strings.Tools.Licence, Services.MediaEngine.Licence)];
        _cards[4].Facts = [(Strings.Tools.Size, UiFormat.Size(d.DatabaseBytes)), (Strings.Tools.Location, d.DatabaseLocation), (Strings.Tools.LastBackup, d.LastBackup is DateTimeOffset backup ? UiFormat.RecentDay(backup, now) : Strings.Tools.Never)];
        foreach (ToolCard card in _cards)
        {
            card.Invalidate();
        }

        if (_view >= 0)
        {
            ShowView(_view);
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int columns = Columns(Width);
        int cardWidth = (Width - ((columns - 1) * S(12))) / columns;
        for (int i = 0; i < _cards.Length; i++)
        {
            _cards[i].Bounds = new Rectangle((i % columns) * (cardWidth + S(12)), (i / columns) * S(CardPitch), cardWidth, S(220));
        }

        _back.Bounds = new Rectangle(0, 0, S(32), S(32));
        int right = Width;
        foreach (KitButton button in new[] { _deleteAll, _openFolder, _delete, _restore, _undo }.Where(b => b.Visible))
        {
            int w = S(button.PreferredLogicalWidth);
            right -= w;
            button.Bounds = new Rectangle(right, 0, w, S(32));
            right -= S(8);
        }

        int top = S(48);
        _facts.Bounds = new Rectangle(0, top, Width, _facts.MeasureHeight());
        _table.Bounds = new Rectangle(0, top, Width, Math.Max(S(120), Height - top));
        _empty.Bounds = _table.Bounds;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_view >= 0)
        {
            string title = _view switch { 0 => Strings.Tools.Quarantine, 1 => Strings.Tools.Operations, 3 => Strings.Tools.MediaEngine, _ => Strings.Tools.Database };
            KitPaint.DrawText(e.Graphics, title, Type.Subtitle, Tokens.Text, S(44), S(16), Width - S(44));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _batchView.Parent is null)
        {
            _batchView.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Columns(int width) => width >= S(1000) ? 3 : 2;

    private static string DaysText(int days) => UiFormat.Plural(days, Strings.Tools.OneDay, Strings.Tools.DaysValue);

    private void ShowView(int view)
    {
        bool switched = view != _view;
        _view = view;
        foreach (ToolCard card in _cards)
        {
            card.Visible = view < 0;
        }

        _back.Visible = view >= 0;
        _facts.Visible = view is 3 or 4;
        bool table = view is 0 or 1;
        _table.Visible = false;
        _empty.Visible = false;
        if (switched)
        {
            // A selection belongs to the rows of one view. Kept by row number across views and reloads, it pointed at
            // rows the user never chose, and Delete for good removed another original for good.
            _table.ClearSelection();
            _table.Source = null;
            _table.ScrollOffset = 0;
            CloseBatch();
        }

        if (table && _data is not null)
        {
            DateTimeOffset now = Services.Clock.UtcNow;
            if (view == 0)
            {
                // Sizes come with the snapshot: looking at the files here froze the window for an offline network folder.
                int keepDays = State.Settings.Scanning.EmptyQuarantineAfterDays;
                _kept = [.. _data.Quarantine ?? []];
                _source.Ids = [.. _kept.Select(q => q.Operation.Id)];
                _source.Rows = [.. _kept.Select(q => new[] { Path.GetFileName(q.Operation.SourcePath), Path.GetDirectoryName(q.Operation.SourcePath) ?? string.Empty, q.Size is long size ? UiFormat.Size(size) : Strings.Dash, UiFormat.RecentDay(LiveUiData.Ticks(q.Operation.Utc), now), DaysText(Math.Max(0, keepDays - (int)(now - LiveUiData.Ticks(q.Operation.Utc)).TotalDays)) })];
                _source.PillColumn = -1;
                _table.Columns = [new(Strings.Tools.ColFile, 280, Sortable: false), new(Strings.Tools.ColWasIn, 300, Sortable: false), new(Strings.Tools.ColSize, 96, RightAligned: true, Sortable: false, Drop: 1), new(Strings.Tools.ColMoved, 120, Sortable: false, Drop: 2), new(Strings.Tools.ColRemovedIn, 0, Sortable: false, MinWidth: 120)];
                _empty.Icon = IconName.Quarantine;
                _empty.Headline = Strings.Tools.QuarantineEmpty;
                _empty.Body = Strings.Tools.QuarantineEmptyText;
            }
            else
            {
                _batches = [.. _data.Operations.Where(o => !LiveUiData.IsUndo(o)).GroupBy(o => o.BatchId).Select(g => new OperationBatch(g.Key, [.. g]))];
                _source.Ids = [.. _batches.Select(b => b.Id)];
                _source.Rows = [.. _batches.Select(b => new[] { UiFormat.RecentDate(LiveUiData.Ticks(b.Utc), now), LiveUiData.ActionName(b.ActionId), UiFormat.Count(b.Operations.Count), b.Result })];
                _source.PillColumn = 3;
                _table.Columns = [new(Strings.Tools.ColWhen, 160, Sortable: false), new(Strings.Tools.ColWhat, 360, Sortable: false), new(Strings.Tools.ColFiles, 96, RightAligned: true, Sortable: false), new(Strings.Tools.ColResult, 0, Sortable: false, Pad: 28)];
                _empty.Icon = IconName.Undo;
                _empty.Headline = Strings.Tools.OperationsEmpty;
                _empty.Body = string.Empty;
                _batchView.Refresh(_batches);
            }

            _table.Source = _source;
            if (switched && _source.Count > 0)
            {
                // Focus starts on the first row, not on whatever row number the other view had.
                _table.FocusRow(0, select: false);
            }

            _table.Visible = _source.Count > 0;
            _empty.Visible = _source.Count == 0;
        }

        _restore.Visible = _delete.Visible = _openFolder.Visible = _deleteAll.Visible = view == 0 && _table.Visible;
        _undo.Visible = view == 1 && _table.Visible;
        UpdateViewButtons();
        _facts.Bind(view);
        if (Parent is Shell.PageViewport viewport)
        {
            viewport.LayoutPage();
        }

        PerformLayout();
        Invalidate();
    }

    /// <summary>
    /// What Restore, Delete for good and Open folder act on (subplan 09, Quarantine: they are selection actions): the
    /// selected originals, found by their operation, in list order. A click or an arrow key selects a row.
    /// </summary>
    internal List<QuarantineEntry> ChosenKept() =>
        _view == 0 ? [.. _kept.Where(e => _table.SelectedIds.Contains(e.Operation.Id))] : [];

    /// <summary>The one selected operation batch, or null.</summary>
    private OperationBatch? SelectedBatch() =>
        _view == 1 && _table.SelectedIds.Count == 1 ? _batches.FirstOrDefault(b => _table.SelectedIds.Contains(b.Id)) : null;

    private void OnSelectionChanged()
    {
        UpdateViewButtons();

        // Subplan 09, Operations: selecting a row lists its files and messages in the drawer.
        if (SelectedBatch() is OperationBatch batch)
        {
            UiTask.Run(ShowBatchAsync(batch.Id));
        }
    }

    private void OpenBatch(int index)
    {
        if (_view == 1 && index >= 0 && index < _batches.Count)
        {
            UiTask.Run(ShowBatchAsync(_batches[index].Id));
        }
    }

    private void UpdateViewButtons()
    {
        bool live = Services.Operations is not null && !Services.IsDemo;
        List<QuarantineEntry> chosen = ChosenKept();
        _restore.Enabled = _delete.Enabled = live && !_working && chosen.Count > 0;
        _openFolder.Enabled = chosen.Count > 0 && chosen[0].Size is not null;
        _deleteAll.Enabled = live && !_working && _kept.Count > 0;

        // Subplan 09, Pitfalls: a disabled button says why (Open folder also for an original whose drive is out of reach).
        string choose = chosen.Count == 0 ? Strings.Tools.SelectOriginal : string.Empty;
        string busy = _working ? Strings.Tools.Working : choose;
        KitToolTip.Set(_restore, busy);
        KitToolTip.Set(_delete, busy);
        KitToolTip.Set(_deleteAll, _working ? Strings.Tools.Working : string.Empty);
        KitToolTip.Set(_openFolder, chosen.Count > 0 && chosen[0].Size is null ? Strings.Library.ErrorUnreachable : choose);
        _undo.Enabled = live && SelectedBatch() is { CanUndo: true };
        KitToolTip.Set(_undo, _undo.Enabled ? string.Empty : Strings.Tools.SelectOperation);
    }

    /// <summary>
    /// Restore: the chosen originals come back (their operations are undone). Every one is tried, with a running scan
    /// paused meanwhile (subplan 12); one toast says how many came back and another why the first that failed did not.
    /// </summary>
    private async Task RestoreSelectedAsync()
    {
        List<QuarantineEntry> chosen = ChosenKept();
        if (_working || chosen.Count == 0 || Services.Operations is not { } runner)
        {
            return;
        }

        long batch = Services.Clock.UtcNow.UtcTicks;
        (int restored, int failed, string? firstFailure) = await WorkingAsync(() => WithScanPausedAsync(() => Task.Run(async () =>
        {
            int done = 0;
            int failures = 0;
            string? first = null;
            foreach (QuarantineEntry entry in chosen)
            {
                string? reason = await runner.UndoAsync(entry.Operation.Id, batch, CancellationToken.None).ConfigureAwait(false);
                if (reason is null)
                {
                    done++;
                }
                else
                {
                    failures++;
                    first ??= string.Format(CultureInfo.CurrentCulture, Strings.Tools.FileReason, Path.GetFileName(entry.Operation.SourcePath), reason);
                }
            }

            return (done, failures, first);
        }))).ConfigureAwait(true);
        if (restored > 0)
        {
            string title = string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Strings.Repair.ResultRestore, UiFormat.Plural(restored, Strings.Repair.OneFile, Strings.Repair.ManyFiles));
            Services.ShowToast?.Invoke(new ToastContent(Severity.None, title, failed > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Repair.Skipped, UiFormat.Count(failed)) : string.Empty));
        }

        if (failed > 0)
        {
            string title = string.Format(CultureInfo.CurrentCulture, Strings.Repair.FailedFor, Strings.Repair.VerbRestore, UiFormat.Plural(failed, Strings.Repair.OneFile, Strings.Repair.ManyFiles));
            Services.ShowToast?.Invoke(new ToastContent(Severity.Critical, title, firstFailure ?? string.Empty));
        }

        _table.ClearSelection();
        Reload();
    }

    /// <summary>
    /// Delete for good after dialog B (subplan 09): the only permanent deletion; one question for all of them, which
    /// names the file when there is one. A file that cannot be deleted (in use, read-only) is reported and the rest
    /// are still deleted; the toast says what went.
    /// </summary>
    private async Task DeleteForGoodAsync(IReadOnlyList<QuarantineEntry> entries)
    {
        List<QuarantineEntry> chosen = [.. entries];
        if (_working || chosen.Count == 0 || Services.Operations is not { } runner || FindForm() is not Form owner)
        {
            return;
        }

        long size = chosen.Sum(e => e.Size ?? 0);
        bool one = chosen.Count == 1;
        string title = one
            ? string.Format(CultureInfo.CurrentCulture, Strings.Tools.DeleteOneTitle, Path.GetFileName(chosen[0].Operation.SourcePath))
            : string.Format(CultureInfo.CurrentCulture, Strings.Dialogs.DeleteTitle, UiFormat.Count(chosen.Count));
        string text = string.Format(CultureInfo.CurrentCulture, one ? Strings.Tools.DeleteOneText : Strings.Dialogs.DeleteText, UiFormat.Size(size));
        if (!MessageDialog.Confirm(owner, title, text, one ? Strings.Tools.KeepIt : Strings.Dialogs.KeepThem, Strings.Dialogs.DeleteForGood, destructive: true))
        {
            return;
        }

        (int deleted, long freed, int failed, string? firstFailure) = await WorkingAsync(() => Task.Run(async () =>
        {
            int done = 0;
            long bytes = 0;
            int failures = 0;
            string? first = null;
            foreach (QuarantineEntry entry in chosen)
            {
                try
                {
                    bytes += await runner.PurgeOneAsync(entry.Operation.Id).ConfigureAwait(false);
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures++;
                    first ??= string.Format(CultureInfo.CurrentCulture, Strings.Tools.FileReason, Path.GetFileName(entry.Operation.SourcePath), ex.Message);
                }
            }

            return (done, bytes, failures, first);
        })).ConfigureAwait(true);
        if (deleted > 0)
        {
            Services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Tools.DeletedForGood, UiFormat.Facts(UiFormat.Plural(deleted, Strings.Repair.OneFile, Strings.Repair.ManyFiles), UiFormat.Size(freed))));
        }

        if (failed > 0)
        {
            string failedTitle = string.Format(CultureInfo.CurrentCulture, Strings.Repair.FailedFor, Strings.Tools.DeleteForGood, UiFormat.Plural(failed, Strings.Repair.OneFile, Strings.Repair.ManyFiles));
            Services.ShowToast?.Invoke(new ToastContent(Severity.Critical, failedTitle, firstFailure ?? string.Empty));
        }

        _table.ClearSelection();
        Reload();
    }

    private async Task UndoSelectedAsync()
    {
        if (SelectedBatch() is OperationBatch batch)
        {
            await WithScanPausedAsync(async () =>
            {
                await Remediator.UndoBatchAsync(Services, batch.Id).ConfigureAwait(true);
                return true;
            }).ConfigureAwait(true);
            Reload();
        }
    }

    /// <summary>
    /// Runs a Restore, Delete for good or Delete all… with those three disabled meanwhile, saying why (subplan 08, Busy):
    /// nothing showed it, and a second one started on the same originals. The keyboard focus waits on the list, as a
    /// disabled button sends it on to the next one.
    /// </summary>
    private async Task<T> WorkingAsync<T>(Func<Task<T>> work)
    {
        _working = true;
        if (_restore.ContainsFocus || _delete.ContainsFocus || _deleteAll.ContainsFocus)
        {
            _table.Focus();
        }

        UpdateViewButtons();
        try
        {
            return await work().ConfigureAwait(true);
        }
        finally
        {
            _working = false;
            UpdateViewButtons();
        }
    }

    /// <summary>Runs <paramref name="work"/> with a running scan paused (subplan 12: an operation needs the engine idle), then lets it continue.</summary>
    private async Task<T> WithScanPausedAsync<T>(Func<Task<T>> work)
    {
        EngineStatus status = Services.Scans.Status;
        bool paused = status.IsActive && status.Phase != EnginePhase.Paused;
        if (paused)
        {
            Services.Scans.Pause();
        }

        try
        {
            return await work().ConfigureAwait(true);
        }
        finally
        {
            if (paused)
            {
                Services.Scans.Resume();
            }
        }
    }

    /// <summary>
    /// Shows a batch's operations in the drawer, read off the UI thread. Only the newest request is shown, and only
    /// while its batch is still the one selected: arrowing from a large batch to a small one showed the large one last.
    /// A drawer closed meanwhile (Esc) stays closed.
    /// </summary>
    private async Task ShowBatchAsync(long batchId)
    {
        if (Services.IsDemo || _batches.FirstOrDefault(b => b.Id == batchId) is not OperationBatch batch)
        {
            return;
        }

        long request = ++_batchRequest;
        bool open = _batchView.Parent is Drawer { IsOpen: true };
        List<OperationRecord> operations = await Task.Run(() => Services.Database.Journal.List(int.MaxValue, batchId)).ConfigureAwait(true);
        if (IsDisposed || _view != 1 || request != _batchRequest || (open && _batchView.Parent is not Drawer { IsOpen: true })
            || (SelectedBatch() is OperationBatch selected && selected.Id != batchId))
        {
            return;
        }

        _batchView.Width = Math.Max(1, Dpi.Scale(Drawer.DefaultWidth, DeviceDpi) - 1);
        _batchView.Bind(batch, [.. operations.Where(o => !LiveUiData.IsUndo(o)).OrderBy(o => o.Id)]);
        Services.ShowDrawer?.Invoke(_batchView);
    }

    private void CloseBatch()
    {
        // A batch still being read is not shown afterwards.
        _batchRequest++;
        if (_batchView.Batch is not null)
        {
            _batchView.Clear();
            if (_batchView.Parent is not null)
            {
                Services.ShowDrawer?.Invoke(null);
            }
        }
    }

    private void CreateReport()
    {
        if (FindForm() is Form owner)
        {
            CreateReportDialog.Show(owner, Services);
            Reload();
        }
    }

    /// <summary>
    /// One row of the Operations view: the operations of a batch (undo records left out) and what came of them. A
    /// batch is Done while any of its files is (the rest may have failed), Undone when nothing done is left, Failed or
    /// Cancelled when nothing was done, and Running while one is still being worked on.
    /// </summary>
    private sealed record OperationBatch(long Id, List<OperationRecord> Operations)
    {
        public long Utc => Operations.Max(o => o.Utc);

        public string ActionId => Operations[0].ActionId;

        public bool CanUndo => Operations.Any(LiveUiData.CanUndo);

        public string Result =>
            Operations.Any(o => o.Status is OperationStatus.Running or OperationStatus.Planned) ? Strings.Tools.Running
            : Operations.Any(o => o.Status == OperationStatus.Done) ? Strings.Tools.Done
            : Operations.Any(o => o.Status == OperationStatus.Undone) ? Strings.Tools.Undone
            : Operations.Any(o => o.Status == OperationStatus.Failed) ? Strings.Tools.Failed
            : Strings.Tools.Cancelled;
    }

    private static string OperationResult(OperationStatus status) => status switch
    {
        OperationStatus.Done => Strings.Tools.Done,
        OperationStatus.Failed => Strings.Tools.Failed,
        OperationStatus.Undone => Strings.Tools.Undone,
        OperationStatus.Running or OperationStatus.Planned => Strings.Tools.Running,
        _ => Strings.Tools.Cancelled,
    };

    /// <summary>The pill colours of a result: OK for Done, Critical for Failed, neutral for the rest.</summary>
    private static Severity? ResultTier(string result) =>
        result == Strings.Tools.Done ? Severity.None : result == Strings.Tools.Failed ? Severity.Critical : null;

    /// <summary>
    /// Rows of the quarantine and operations tables (strings per column) with a stable id each: the operation of a
    /// kept original, or the batch. The list keeps its selection by id, so a reload never moves it to another row.
    /// </summary>
    private sealed class TableSource : IListSource
    {
        public IReadOnlyList<long> Ids { get; set; } = [];

        public IReadOnlyList<string[]> Rows { get; set; } = [];

        public int PillColumn { get; set; } = -1;

        public int Count => Rows.Count;

        public long IdOf(int index) => Ids[index];

        public void Fill(int index, ListRow row)
        {
            string[] cells = Rows[index];
            row.Line1 = cells[0];
            for (int i = 1; i < cells.Length; i++)
            {
                row.Cells[i] = cells[i];
            }

            if (PillColumn >= 0)
            {
                row.PillColumn = PillColumn;
                row.PillTier = ResultTier(cells[PillColumn]);
            }
        }

        public void SetCollapsed(int index, bool collapsed)
        {
        }
    }

    /// <summary>
    /// An operation batch in the drawer (subplan 09, Operations): what was done and when, its result, and each file with
    /// its result and, for one that failed, why.
    /// </summary>
    private sealed class BatchView : KitControl
    {
        private readonly ToolsPage _page;
        private readonly IconButton _close = new() { Icon = IconName.Close, TabIndex = 0 };
        private readonly VirtualList _files = new() { Mode = ListMode.Table, Icons = false, Framed = true, TabIndex = 1, AccessibleName = Strings.Tools.ColFiles };
        private readonly TableSource _source = new() { PillColumn = 1 };
        private string _title = string.Empty;
        private string _facts = string.Empty;

        /// <summary>The batch's result and its pill tier, worked out once per batch, not at every paint.</summary>
        private string _result = string.Empty;
        private Severity? _tier;

        public BatchView(ToolsPage page)
        {
            _page = page;
            _close.ToolTipText = Strings.Kit.CloseEsc;
            _close.Click += (_, _) => _page.CloseBatch();
            _files.Columns = [new(Strings.Tools.ColFile, 0, Sortable: false, MinWidth: 120), new(Strings.Tools.ColResult, 104, Sortable: false, Pad: 28), new(Strings.Tools.ColMessage, 140, Sortable: false, Drop: 1)];
            _files.Source = _source;
            Controls.AddRange([_close, _files]);
            BackColor = Tokens.Surface;
        }

        /// <summary>The batch shown, or null.</summary>
        public OperationBatch? Batch { get; private set; }

        public void Bind(OperationBatch batch, IReadOnlyList<OperationRecord> operations)
        {
            Batch = batch;
            _title = LiveUiData.ActionName(batch.ActionId);
            _result = batch.Result;
            _tier = ResultTier(_result);
            _facts = UiFormat.Facts(UiFormat.RecentDate(LiveUiData.Ticks(batch.Utc), _page.Services.Clock.UtcNow), UiFormat.Plural(operations.Count, Strings.Repair.OneFile, Strings.Repair.ManyFiles));
            _source.Ids = [.. operations.Select(o => o.Id)];
            _source.Rows = [.. operations.Select(o => new[] { Path.GetFileName(o.SourcePath), OperationResult(o.Status), o.Status is OperationStatus.Failed or OperationStatus.Cancelled ? o.DetailJson ?? string.Empty : string.Empty })];
            _files.ClearSelection();
            _files.Source = _source;
            _files.ScrollOffset = 0;
            PerformLayout();
            Invalidate();
        }

        /// <summary>
        /// After a reload: the drawer shows the selected batch (or the one shown) with its newest result (an Undo
        /// changes it), or closes when that batch is gone.
        /// </summary>
        public void Refresh(IReadOnlyList<OperationBatch> batches)
        {
            if (Batch is OperationBatch shown && Parent is Drawer { IsOpen: true })
            {
                long wanted = _page.SelectedBatch()?.Id ?? shown.Id;
                if (batches.Any(b => b.Id == wanted))
                {
                    UiTask.Run(_page.ShowBatchAsync(wanted));
                }
                else
                {
                    _page.CloseBatch();
                }
            }
        }

        public void Clear() => Batch = null;

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            _close.Bounds = new Rectangle(Width - S(52), S(16), S(32), S(32));
            int top = S(112);
            _files.Bounds = new Rectangle(S(20), top, Math.Max(S(120), Width - S(40)), Math.Max(S(120), Height - top - S(20)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            if (Batch is null)
            {
                return;
            }

            TypeRamp type = Type;
            KitPaint.DrawText(g, _title, type.Subtitle, t.Text, S(20), S(32), Width - S(80));
            KitPaint.DrawText(g, _facts, type.Body, t.Text2, S(20), S(58), Width - S(40));
            (Color fg, Color bg) = _tier switch
            {
                Severity s when s != Severity.None => (t.ForSeverity(s).Text, t.ForSeverity(s).Pill),
                Severity => (t.Ok.Text, t.Ok.Pill),
                null => (t.Text2, t.NeutralPill),
            };
            BadgePainter.PaintPill(g, S(20), S(76), _result, fg, bg, DeviceDpi);
        }

        protected override void OnThemeChanged()
        {
            BackColor = Tokens.Surface;
            base.OnThemeChanged();
        }
    }

    /// <summary>One tool: icon tile, title, description, facts and its button (reference <c>tools</c>).</summary>
    private sealed class ToolCard : Card
    {
        private readonly IconName _icon;
        private readonly string _title;
        private readonly string _text;
        private readonly KitButton _button;

        public ToolCard(IconName icon, string title, string text, string button, Action click)
        {
            _icon = icon;
            _title = title;
            _text = text;
            AccessibleName = title;
            _button = new KitButton { Kind = ButtonKind.Secondary, Text = button, Compact = true };
            _button.Click += (_, _) => click();
            Controls.Add(_button);
        }

        public IReadOnlyList<(string Label, string Value)> Facts { get; set; } = [];

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int width = S(_button.PreferredLogicalWidth);
            _button.Bounds = new Rectangle(S(16), Height - S(16) - S(28), width, S(28));
        }

        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            TypeRamp type = Type;
            Rectangle tile = new(S(16), S(16), S(36), S(36));
            KitPaint.FillRoundedRect(g, tile, SF(6), t.Bg);
            KitPaint.StrokeRoundedRect(g, tile, SF(6), t.Stroke, SF(1));
            IconRenderer.Draw(g, _icon, tile.X + SF(10), tile.Y + SF(10), 16, DeviceDpi, t.Accent);
            KitPaint.DrawText(g, _title, type.Subtitle, t.Text, S(64), S(35), Width - S(80));
            // GDI text runs a little wider than the references': allow 4 px of the padding.
            TextWrap.Draw(g, _text, type.Body, t.Text2, S(16), S(64), Width - S(28), S(19));
            int y = S(119);
            foreach ((string label, string value) in Facts)
            {
                int labelWidth = KitPaint.MeasureWidth(label, type.Caption);
                KitPaint.DrawText(g, label, type.Caption, t.Text2, S(16), y, labelWidth + 1);
                int valueLeft = S(16) + labelWidth + S(12);
                KitPaint.DrawText(g, value, type.CaptionStrong, t.Text, valueLeft, y, Width - S(16) - valueLeft, TextFormatFlags.Right);
                y += S(20);
            }
        }
    }

    /// <summary>The media engine and database sub-views: fact rows, buttons and text.</summary>
    private sealed class FactsView : KitControl
    {
        private readonly ToolsPage _page;
        private readonly List<KitButton> _buttons = [];
        private readonly List<string> _captions = [];
        private readonly InfoBar _changed = new() { Tier = Severity.Medium, Text = Strings.Tools.EngineChangedBar, Visible = false };
        private readonly LicenceText _licenceText = new() { TabIndex = 0 };
        private readonly ScrollPanel _licence;
        private IReadOnlyList<(string Label, string Value)> _facts = [];
        private int _view = -1;
        private int _working = -1;

        public FactsView(ToolsPage page)
        {
            _page = page;
            TabStop = false;
            _licence = new ScrollPanel(_licenceText) { Visible = false, TabIndex = 1 };
            Controls.Add(_changed);
            Controls.Add(_licence);
        }

        public int MeasureHeight() => S(_view == 3 ? 40 + (_facts.Count * 28) + 16 + (_changed.Visible ? 52 : 0) + 240 + 40 + 48 : 40 + (_facts.Count * 28) + 16 + (_buttons.Count * 56));

        /// <summary>
        /// Shows a view's facts. The buttons are made when the view opens and kept across reloads, so the one the
        /// keyboard just used keeps its focus (each action reloads the page).
        /// </summary>
        public void Bind(int view)
        {
            bool opened = view != _view;
            _view = view;
            if (opened)
            {
                foreach (KitButton button in _buttons)
                {
                    Controls.Remove(button);
                    button.Dispose();
                }

                _buttons.Clear();
                _captions.Clear();
                _working = -1;
            }

            UiServices services = _page.Services;
            _licence.Visible = view == 3;
            if (view == 3)
            {
                MediaEngineInfo engine = services.MediaEngine;
                string integrity = engine.Check.State switch { BundleState.Ok => Strings.Tools.IntegrityVerified, BundleState.Missing => Strings.Tools.IntegrityMissing, _ => Strings.Tools.IntegrityChanged };
                _facts = [(Strings.Tools.FfmpegVersion, engine.Version), (Strings.Tools.Build, engine.Build), (Strings.Tools.Licence, engine.Licence), (Strings.Tools.Location, engine.Location.Directory), (Strings.Tools.Integrity, integrity)];
                _changed.Visible = engine.Check.State is not (BundleState.Ok or BundleState.Missing);
                if (opened)
                {
                    // Read off the UI thread like every other file the window shows.
                    _licenceText.Text = Strings.Tools.EngineNotice;
                    UiTask.Run(LoadLicenceAsync(engine.Location.Directory));
                    AddButton(Strings.Tools.OpenLicencesFolder, null, ButtonKind.Secondary, () => ShellActions.OpenFolder(engine.Location.Directory));
                    AddButton(Strings.Tools.WhereSource, null, ButtonKind.Secondary, () => ShellActions.OpenUrl(FfmpegVersions.SourceUrl));
                }
            }
            else if (view == 4 && _page.Data is ToolsData d)
            {
                _facts = [(Strings.Tools.Location, d.DatabaseLocation), (Strings.Tools.Size, UiFormat.Size(d.DatabaseBytes)), (Strings.Tools.FilesIndexed, UiFormat.Count(d.FilesIndexed)), (Strings.Tools.LastBackup, d.LastBackup is DateTimeOffset b ? UiFormat.RecentDay(b, services.Clock.UtcNow) : Strings.Tools.Never)];
                _changed.Visible = false;
                if (_buttons.Count == 0)
                {
                    // Each says how it went: Check threw its answer away, so damage it found was never shown.
                    AddAction(Strings.Tools.BackUpNow, Strings.Tools.BackUpNowText, ButtonKind.Secondary, Strings.Tools.BackUpNow, null, async () =>
                    {
                        string file = await services.Database.Maintenance.BackupAsync().ConfigureAwait(true);
                        return new ToastContent(Severity.None, Strings.Tools.BackupSaved, Path.GetFileName(file), Strings.Tools.Open, () => ShellActions.ShowInFolder(file));
                    });
                    AddAction(Strings.Tools.Compact, Strings.Tools.CompactText, ButtonKind.Secondary, Strings.Tools.Compact, null, async () =>
                    {
                        await services.Database.Maintenance.CompactAsync().ConfigureAwait(true);
                        long size = await Task.Run(services.Database.Maintenance.SizeBytes).ConfigureAwait(true);
                        return new ToastContent(Severity.None, Strings.Tools.Compacted, UiFormat.Size(size));
                    });
                    AddAction(Strings.Tools.Check, Strings.Tools.CheckText, ButtonKind.Secondary, Strings.Tools.Check, null, async () =>
                    {
                        List<string> problems = await services.Database.Maintenance.CheckAsync().ConfigureAwait(true);
                        return problems.Count == 0
                            ? new ToastContent(Severity.None, Strings.Tools.CheckPassed, string.Empty)
                            : new ToastContent(Severity.Critical, Strings.Tools.CheckDamaged, problems[0]);
                    });
                    AddAction(Strings.Tools.ResetEllipsis, Strings.Tools.ResetText, ButtonKind.Danger, Strings.Tools.Reset, ConfirmReset, async () =>
                    {
                        await _page.Services.Database.Maintenance.ResetAsync().ConfigureAwait(true);
                        return null;
                    });
                }
            }
            else
            {
                _facts = [];
                _changed.Visible = false;
            }

            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int y = S(40 + (_facts.Count * 28));
            if (_changed.Visible)
            {
                _changed.Bounds = new Rectangle(0, y, Math.Min(Width, S(720)), S(40));
                y += S(52);
            }

            if (_view == 3)
            {
                // Inside the box's 1 px border, which the view paints.
                _licence.Bounds = new Rectangle(S(1), y + S(1), Math.Min(Width, S(720)) - S(2), S(232) - S(2));
                y += S(240 + 40);
                int x = 0;
                foreach (KitButton button in _buttons)
                {
                    int width = S(button.PreferredLogicalWidth);
                    button.Bounds = new Rectangle(x, y, width, S(32));
                    x += width + S(8);
                }

                return;
            }

            foreach (KitButton button in _buttons)
            {
                button.Bounds = new Rectangle(0, y + S(8), S(140), S(32));
                y += S(56);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Bg);
            TypeRamp type = Type;
            int y = S(14);
            foreach ((string label, string value) in _facts)
            {
                KitPaint.DrawText(g, label, type.Body, t.Text2, 0, y, S(160));
                KitPaint.DrawText(g, value, type.Body, t.Text, S(176), y, Width - S(176));
                y += S(28);
            }

            if (_view == 3)
            {
                int top = S(40 + (_facts.Count * 28)) + (_changed.Visible ? S(52) : 0);
                Rectangle box = new(0, top, Math.Min(Width, S(720)), S(232));
                KitPaint.FillRoundedRect(g, box, SF(4), t.Surface);
                KitPaint.StrokeRoundedRect(g, box, SF(4), t.Stroke, SF(1));
                KitPaint.DrawText(g, Strings.Tools.EngineNotice, type.Body, t.Text2, 0, box.Bottom + S(24), Width);
                return;
            }

            for (int i = 0; i < _buttons.Count; i++)
            {
                // While an action runs, its caption says so; the buttons wait (Compact and Check take minutes).
                string caption = i == _working ? Strings.Tools.Working : _captions[i];
                if (caption.Length > 0)
                {
                    KitPaint.DrawText(g, caption, type.Body, t.Text2, S(156), _buttons[i].Top + S(16), Width - S(156));
                }
            }
        }

        private async Task LoadLicenceAsync(string directory)
        {
            string? text = await Task.Run(() => ReadLicence(directory)).ConfigureAwait(true);
            if (!IsDisposed && _view == 3 && text is not null)
            {
                _licenceText.Text = text;
                _licence.ScrollToTop();
            }
        }

        /// <summary>The whole licence file (subplan 09: a scrolling text), or null when there is none.</summary>
        private static string? ReadLicence(string directory)
        {
            try
            {
                string? file = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "LICENSE*").FirstOrDefault() : null;
                return file is null ? null : File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private void AddButton(string text, string? caption, ButtonKind kind, Action click)
        {
            KitButton button = new() { Kind = kind, Text = text, TabIndex = 2 + _buttons.Count };
            button.Click += (_, _) => click();
            _buttons.Add(button);
            _captions.Add(caption ?? string.Empty);
            Controls.Add(button);
        }

        /// <summary>A database action: asks first when <paramref name="confirm"/> says so, then runs and reports.</summary>
        private void AddAction(string text, string caption, ButtonKind kind, string action, Func<bool>? confirm, Func<Task<ToastContent?>> work)
        {
            int index = _buttons.Count;
            AddButton(text, caption, kind, () =>
            {
                // While another runs, Reset asked first and then did nothing.
                if (_working < 0 && (confirm is null || confirm()))
                {
                    Run(index, action, work);
                }
            });
        }

        /// <summary>
        /// Runs one database action at a time and shows its result, or why it failed, in a toast. Meanwhile the
        /// action's caption reads Working… and the buttons wait (a second click did nothing and said nothing). They stay
        /// enabled: a disabled button sends the keyboard focus on, and it left the view at every action.
        /// </summary>
        private async void Run(int index, string action, Func<Task<ToastContent?>> work)
        {
            if (_page.Services.IsDemo || _working >= 0)
            {
                return;
            }

            _working = index;
            Invalidate();
            ToastContent? result;
            try
            {
                result = await work().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                _page.State.Log.Warning("Database maintenance failed", ex);
                result = new ToastContent(Severity.Critical, string.Format(CultureInfo.CurrentCulture, Strings.Tools.ActionFailed, action), ex.Message);
            }
            finally
            {
                _working = -1;
                if (!IsDisposed)
                {
                    Invalidate();
                }
            }

            if (result is not null)
            {
                _page.Services.ShowToast?.Invoke(result);
            }

            _page.Reload();
        }

        private bool ConfirmReset() =>
            FindForm() is Form owner && MessageDialog.Confirm(owner, Strings.Tools.ResetTitle, Strings.Tools.ResetBody, Strings.Kit.Cancel, Strings.Tools.Reset, destructive: true);
    }

    /// <summary>
    /// The licence in Mono (subplan 09, Media engine: a scrolling text) inside a scroll panel. Lines are wrapped once
    /// per text or width, by characters (the font is fixed-width), and only the visible ones are painted. Arrow and
    /// page keys scroll it, so the whole text can be read with the keyboard.
    /// </summary>
    private sealed class LicenceText : KitControl
    {
        private const int Pitch = 16;
        private List<string> _lines = [];
        private int _wrappedWidth = -1;
        private string _text = string.Empty;

        public LicenceText()
        {
            TabStop = true;
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = Strings.Tools.Licence;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string Text
        {
            get => _text;
            set
            {
                _text = value ?? string.Empty;
                _wrappedWidth = -1;
                Wrap();
            }
        }

        protected override string? AccessibleValue => _text;

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Parent is not ScrollPanel host)
            {
                return;
            }

            int page = Math.Max(S(Pitch), host.Height - S(Pitch));
            int? offset = e.KeyCode switch
            {
                Keys.Up => host.ScrollOffset - S(Pitch),
                Keys.Down => host.ScrollOffset + S(Pitch),
                Keys.PageUp => host.ScrollOffset - page,
                Keys.PageDown => host.ScrollOffset + page,
                Keys.Home => 0,
                Keys.End => host.ScrollMax,
                _ => null,
            };
            if (offset is int target)
            {
                host.ScrollOffset = target;
                e.Handled = true;
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Wrap();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            Font mono = Type.Mono;
            int pitch = S(Pitch);
            int first = Math.Max(0, (e.ClipRectangle.Top - S(8)) / pitch);
            int last = Math.Min(_lines.Count - 1, ((e.ClipRectangle.Bottom - S(8)) / pitch) + 1);
            for (int i = first; i <= last; i++)
            {
                KitPaint.DrawText(g, _lines[i], mono, t.Text2, S(12), S(8) + (pitch / 2) + (i * pitch), Width - S(24));
            }

            if (KeyboardFocused && Parent is ScrollPanel host)
            {
                KitPaint.DrawFocusRing(g, new RectangleF(SF(2), host.ScrollOffset + SF(2), Width - SF(4), host.Height - SF(4)), SF(4), DeviceDpi);
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        /// <summary>The focus ring follows the visible part when the panel scrolls.</summary>
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (Focused)
            {
                Invalidate();
            }
        }

        /// <summary>Wraps at word breaks to the columns that fit; sets the height so the panel scrolls through all of it.</summary>
        private void Wrap()
        {
            int width = Width - S(24);
            if (width <= 0 || width == _wrappedWidth)
            {
                return;
            }

            _wrappedWidth = width;
            int columns = Math.Max(20, width / Math.Max(1, KitPaint.MeasureWidth("0000000000", Type.Mono) / 10));
            List<string> lines = [];
            foreach (string raw in _text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                while (line.Length > columns)
                {
                    int cut = line.LastIndexOf(' ', columns);
                    cut = cut <= 0 ? columns : cut;
                    lines.Add(line[..cut]);
                    line = line[cut..].TrimStart();
                }

                lines.Add(line);
            }

            _lines = lines;
            int height = (lines.Count * S(Pitch)) + S(16);
            if (Height != height)
            {
                Height = height;
            }

            Invalidate();
        }
    }
}

/// <summary>Tools → Create report (subplan 09): what, format and where; a toast offers to open the file.</summary>
internal sealed class CreateReportDialog : KitDialog
{
    private const int FormHeight = 276;
    private readonly UiServices _services;
    private readonly DropDown _what = new() { TabIndex = 0 };
    private readonly OptionRow[] _formats;
    private readonly PathBox _folder = new() { Inset = true, TabIndex = 4 };
    private readonly InfoBar _reaching = new() { Tier = Severity.Low, Text = Strings.Library.Reaching, Visible = false, TabIndex = 5 };
    private readonly KitButton _create;
    private bool _reachingShown;

    private CreateReportDialog(UiServices services)
        : base(Strings.Tools.CreateReport, DialogWidth.Confirmation)
    {
        _services = services;
        _what.Items.Add(Strings.Tools.ReportAll);
        _what.Items.Add(Strings.Tools.ReportLastScan);
        _what.SelectedIndex = 0;
        _formats =
        [
            new() { Text = Strings.Tools.ReportHtml, Description = Strings.Tools.ReportHtmlText, GroupName = "format", Checked = true, TabIndex = 1 },
            new() { Text = Strings.Tools.ReportCsv, Description = Strings.Tools.ReportCsvText, GroupName = "format", TabIndex = 2 },
            new() { Text = Strings.Tools.ReportJson, Description = Strings.Tools.ReportJsonText, GroupName = "format", TabIndex = 3 },
        ];
        _folder.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Body.Controls.Add(_what);
        Body.Controls.AddRange(_formats);
        Body.Controls.Add(_folder);
        Body.Controls.Add(_reaching);
        Body.Paint += (_, e) =>
        {
            ThemeTokens t = ThemeService.Tokens;
            TypeRamp type = TypeRamp.For(DeviceDpi);
            KitPaint.DrawText(e.Graphics, Strings.Tools.ReportWhat, type.Body, t.Text2, 0, S(16), S(80));
            KitPaint.DrawText(e.Graphics, Strings.Tools.ReportFormat, type.Body, t.Text2, 0, S(68), S(80));
            KitPaint.DrawText(e.Graphics, Strings.Tools.SaveTo, type.Body, t.Text2, 0, S(260), S(80));
        };
        AddButton(Strings.Kit.Cancel, ButtonKind.Secondary, DialogResult.Cancel);
        _create = AddButton(Strings.Tools.Create, ButtonKind.Primary, DialogResult.None);

        // An async void handler: a failure reaches the app's error handler (logged and shown), never a dead button.
        _create.Click += async (_, _) => await CreateAsync().ConfigureAwait(true);
        BodyHeight = FormHeight;
    }

    /// <summary>Whether a folder exists; asked off the UI thread (tests make it slow).</summary>
    internal static Func<string, bool> FolderExists { get; set; } = Directory.Exists;

    /// <summary>True while the "Trying to reach this folder…" bar shows (tests).</summary>
    internal bool Reaching => _reachingShown;

    public static void Show(Form owner, UiServices services)
    {
        using CreateReportDialog dialog = new(services);
        dialog.ShowOver(owner);
    }

    protected override void LayoutBody()
    {
        int x = S(96);
        int width = Math.Max(0, Body.Width - x);
        _what.Bounds = new Rectangle(x, 0, width, S(32));
        for (int i = 0; i < _formats.Length; i++)
        {
            _formats[i].Bounds = new Rectangle(x, S(48 + (i * 64)), width, S(56));
        }

        _folder.Bounds = new Rectangle(x, S(244), width, S(32));
        _reaching.Bounds = new Rectangle(0, S(FormHeight + 16), Body.Width, S(40));
    }

    private async Task CreateAsync()
    {
        ReportFormat format = _formats[1].Checked ? ReportFormat.Csv : _formats[2].Checked ? ReportFormat.Json : ReportFormat.Html;
        string folder = _folder.Text.Trim();
        bool lastScan = _what.SelectedIndex == 1;
        DateTimeOffset now = _services.Clock.UtcNow;

        // Seconds in the name: two reports saved in the same minute replaced each other.
        string path = Path.Combine(folder, "MediaVerifier report " + now.ToLocalTime().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ReportWriter.Extension(format));

        // The dialog stays open until the report is written; one report at a time. Cancel still closes it (an offline
        // folder takes a while to give up on): the report is then saved, or not, without a word but its toast.
        _create.Enabled = false;
        string? problem;
        try
        {
            problem = await WriteAsync(folder, path, format, lastScan, now).ConfigureAwait(true);
        }
        finally
        {
            if (!IsDisposed)
            {
                _create.Enabled = true;
            }
        }

        bool open = !IsDisposed && Visible;
        if (problem is not null)
        {
            if (open)
            {
                // Said over the dialog, which stays open to pick another folder: the error handler showed a generic
                // message once per session and nothing after that.
                MessageDialog.Show(this, string.Format(CultureInfo.CurrentCulture, Strings.Tools.ActionFailed, Strings.Tools.CreateReport), problem, Strings.Kit.Close);
            }

            return;
        }

        if (open)
        {
            Finish(DialogResult.OK);
        }

        _services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Tools.ReportSaved, Path.GetFileName(path), Strings.Tools.Open, () => ShellActions.ShowInFolder(path)));
    }

    /// <summary>
    /// Checks the folder (an offline network folder counts as unreachable after the Add folder limit instead of
    /// keeping Create disabled for minutes, and after 2 s a bar says it is being reached, as Add folder does) and writes
    /// the report, off the UI thread; the reason when it could not.
    /// </summary>
    private async Task<string?> WriteAsync(string folder, string path, ReportFormat format, bool lastScan, DateTimeOffset now)
    {
        Func<string, bool> exists = FolderExists;
        Task<bool> probe = Task.Run(() => exists(folder));
        if (await Task.WhenAny(probe, Task.Delay(AddFolderDialog.ReachNotice)).ConfigureAwait(true) != probe)
        {
            ShowReaching(true);
            bool answered = await Task.WhenAny(probe, Task.Delay(AddFolderDialog.ReachLimit - AddFolderDialog.ReachNotice)).ConfigureAwait(true) == probe;
            ShowReaching(false);
            if (!answered)
            {
                return Strings.Library.ErrorUnreachable;
            }
        }

        if (!probe.Result)
        {
            return Strings.Library.ErrorMissing;
        }

        if (IsDisposed || !Visible)
        {
            // Cancelled while the folder was being reached: nothing is written.
            return string.Empty;
        }

        if (_services.IsDemo)
        {
            return null;
        }

        try
        {
            return await Task.Run(async () =>
            {
                long? run = null;
                if (lastScan)
                {
                    run = _services.Database.Runs.List(5).FirstOrDefault(r => r.Status != RunStatus.Running)?.Id;
                    if (run is null)
                    {
                        // "Last scan" with no finished scan wrote every open problem without saying so.
                        return Strings.Tools.NoLastScan;
                    }
                }

                long[] ids = _services.Database.Queries.FindingIds(run is long id ? new FindingFilter { FirstRun = id } : FindingFilter.OpenAll);
                ReportWriter.Write(path, format, _services.Database.Queries.FindingRows(ids), now);
                await _services.Database.Meta.SetAsync(LiveUiData.LastReportKey, now.UtcTicks.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                return (string?)null;
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.State.Log.Warning("The report could not be written", ex);
            return ex.Message;
        }
    }

    /// <summary>The Low bar under Save to while its folder has not answered; the body grows by it (Add folder's notice).</summary>
    private void ShowReaching(bool show)
    {
        // What the code decided, not Visible, which reads false while the dialog itself is hidden.
        if (IsDisposed || _reachingShown == show)
        {
            return;
        }

        _reachingShown = show;
        _reaching.Visible = show;
        BodyHeight = show ? FormHeight + 16 + 40 : FormHeight;
    }
}
