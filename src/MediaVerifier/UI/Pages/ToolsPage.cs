using System.Globalization;
using MediaVerifier.App;
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
    private readonly ToolCard[] _cards;
    private readonly IconButton _back = new() { Icon = IconName.ChevronLeft, TabIndex = 0 };
    private readonly VirtualList _table = new() { Mode = ListMode.Table, Icons = false, Framed = true, TabIndex = 2 };
    private readonly EmptyState _empty = new() { TabIndex = 3 };
    private readonly FactsView _facts;
    private readonly TableSource _source = new();
    private readonly KitButton _restore = new() { Kind = ButtonKind.Secondary, Text = Strings.Tools.Restore, TabIndex = 4 };
    private readonly KitButton _delete = new() { Kind = ButtonKind.Secondary, Text = Strings.Tools.DeleteForGood, TabIndex = 5 };
    private readonly KitButton _openFolder = new() { Kind = ButtonKind.Secondary, Text = Strings.Tools.OpenFolder, TabIndex = 6 };
    private readonly KitButton _undo = new() { Kind = ButtonKind.Secondary, Icon = IconName.Undo, Text = Strings.Tools.Undo, TabIndex = 4 };
    private List<QuarantineEntry> _kept = [];
    private List<long> _batches = [];
    private ToolsData? _data;
    private int _view = -1;

    public ToolsPage(UiServices services)
        : base(services)
    {
        _facts = new FactsView(this) { TabIndex = 1 };
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
        _table.SelectionChanged += (_, _) => UpdateViewButtons();
        _restore.Click += (_, _) => UiTask.Run(RestoreSelectedAsync());
        _delete.Click += (_, _) => UiTask.Run(DeleteSelectedAsync());
        _openFolder.Click += (_, _) => ShellActions.ShowInFolder(SelectedKept()?.Operation.BackupPath);
        _undo.Click += (_, _) => UiTask.Run(UndoSelectedAsync());
        Controls.AddRange([.. _cards, _back, _facts, _table, _empty, _restore, _delete, _openFolder, _undo]);
        ShowView(-1);
    }

    public override PageId Id => PageId.Tools;

    internal ToolsData? Data => _data;

    public override int MeasureContentHeight(int width) => _view is 3 or 4 ? _facts.MeasureHeight() + S(56) : 0;

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override object? LoadData() => Services.Data.LoadTools();

    protected override void ApplyData(object data)
    {
        _data = (ToolsData)data;
        DateTimeOffset now = Services.Clock.UtcNow;
        ToolsData d = _data;
        _cards[0].Facts = [(Strings.Tools.Items, UiFormat.Count(d.QuarantineItems)), (Strings.Tools.Size, UiFormat.Size(d.QuarantineBytes)), (Strings.Tools.OldestRemovedIn, d.OldestRemovedInDays is int days ? string.Format(CultureInfo.CurrentCulture, Strings.Tools.DaysValue, days) : Strings.Dash)];
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
        int columns = Width >= S(1000) ? 3 : 2;
        int cardWidth = (Width - ((columns - 1) * S(12))) / columns;
        for (int i = 0; i < _cards.Length; i++)
        {
            _cards[i].Bounds = new Rectangle((i % columns) * (cardWidth + S(12)), (i / columns) * S(232), cardWidth, S(220));
        }

        _back.Bounds = new Rectangle(0, 0, S(32), S(32));
        int right = Width;
        foreach (KitButton button in new[] { _openFolder, _delete, _restore, _undo }.Where(b => b.Visible))
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

    private void ShowView(int view)
    {
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
        if (table && _data is not null)
        {
            if (view == 0)
            {
                // Sizes come with the snapshot: looking at the files here froze the window for an offline network folder.
                _kept = [.. _data.Quarantine ?? []];
                _source.Rows = [.. _kept.Select(q => new[] { Path.GetFileName(q.Operation.SourcePath), Path.GetDirectoryName(q.Operation.SourcePath) ?? string.Empty, q.Size is long size ? UiFormat.Size(size) : Strings.Dash, UiFormat.RecentDay(LiveUiData.Ticks(q.Operation.Utc), Services.Clock.UtcNow), string.Format(CultureInfo.CurrentCulture, Strings.Tools.DaysValue, Math.Max(0, State.Settings.Scanning.EmptyQuarantineAfterDays - (int)(Services.Clock.UtcNow - LiveUiData.Ticks(q.Operation.Utc)).TotalDays)) })];
                _table.Columns = [new(Strings.Tools.ColFile, 280, Sortable: false), new(Strings.Tools.ColWasIn, 300, Sortable: false), new(Strings.Tools.ColSize, 96, RightAligned: true, Sortable: false, Drop: 1), new(Strings.Tools.ColMoved, 120, Sortable: false, Drop: 2), new(Strings.Tools.ColRemovedIn, 0, Sortable: false, MinWidth: 120)];
                _empty.Icon = IconName.Quarantine;
                _empty.Headline = Strings.Tools.QuarantineEmpty;
                _empty.Body = Strings.Tools.QuarantineEmptyText;
            }
            else
            {
                _batches = [.. _data.Operations.GroupBy(o => o.BatchId).Select(g => g.Key)];
                _source.Rows = [.. _data.Operations.GroupBy(o => o.BatchId).Select(g => new[] { UiFormat.RecentDate(LiveUiData.Ticks(g.Max(o => o.Utc)), Services.Clock.UtcNow), LiveUiData.ActionName(g.First().ActionId), UiFormat.Count(g.Count()), OperationResult(g.First().Status) })];
                _table.Columns = [new(Strings.Tools.ColWhen, 160, Sortable: false), new(Strings.Tools.ColWhat, 360, Sortable: false), new(Strings.Tools.ColFiles, 96, RightAligned: true, Sortable: false), new(Strings.Tools.ColResult, 0, Sortable: false, Pad: 28)];
                _source.PillColumn = 3;
                _empty.Icon = IconName.Undo;
                _empty.Headline = Strings.Tools.OperationsEmpty;
                _empty.Body = string.Empty;
            }

            _source.PillColumn = view == 1 ? 3 : -1;
            _table.Source = _source;
            _table.Visible = _source.Count > 0;
            _empty.Visible = _source.Count == 0;
        }

        _restore.Visible = _delete.Visible = _openFolder.Visible = view == 0 && _table.Visible;
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

    private QuarantineEntry? SelectedKept() => _view == 0 && _table.FocusedIndex >= 0 && _table.FocusedIndex < _kept.Count ? _kept[_table.FocusedIndex] : null;

    /// <summary>
    /// What Restore and Delete for good act on (subplan 09, Quarantine: they are selection actions): the selected rows
    /// (Ctrl or Shift with a click, Ctrl+A), or the focused one. They acted on the focused row only.
    /// </summary>
    internal List<QuarantineEntry> ChosenKept()
    {
        if (_view != 0)
        {
            return [];
        }

        List<QuarantineEntry> chosen = [.. _table.SelectedIds.Where(i => i >= 0 && i < _kept.Count).Order().Select(i => _kept[(int)i])];
        if (chosen.Count == 0 && SelectedKept() is QuarantineEntry focused)
        {
            chosen.Add(focused);
        }

        return chosen;
    }

    private void UpdateViewButtons()
    {
        bool live = Services.Operations is not null && !Services.IsDemo;
        _restore.Enabled = _delete.Enabled = live && ChosenKept().Count > 0;
        _openFolder.Enabled = SelectedKept() is { Size: not null };
        _undo.Enabled = live && _view == 1 && _table.FocusedIndex >= 0 && _table.FocusedIndex < _batches.Count
            && _data?.Operations.Any(o => o.BatchId == _batches[_table.FocusedIndex] && o.Status == OperationStatus.Done && o.UndoJson is not null) == true;
    }

    /// <summary>Restore: the originals come back (their operations are undone), one toast for all of them.</summary>
    private async Task RestoreSelectedAsync()
    {
        List<QuarantineEntry> chosen = ChosenKept();
        if (chosen.Count == 0 || Services.Operations is not { } runner)
        {
            return;
        }

        long batch = Services.Clock.UtcNow.UtcTicks;
        string? failure = await Task.Run(async () =>
        {
            string? first = null;
            foreach (QuarantineEntry entry in chosen)
            {
                first ??= await runner.UndoAsync(entry.Operation.Id, batch, CancellationToken.None).ConfigureAwait(false);
            }

            return first;
        }).ConfigureAwait(true);
        string what = chosen.Count == 1 ? Path.GetFileName(chosen[0].Operation.SourcePath) : UiFormat.Plural(chosen.Count, Strings.Repair.OneFile, Strings.Repair.ManyFiles);
        Services.ShowToast?.Invoke(failure is null
            ? new ToastContent(Severity.None, Strings.Repair.Undone, what)
            : new ToastContent(Severity.Critical, Strings.Repair.UndoFailed, failure));
        _table.ClearSelection();
        Reload();
    }

    /// <summary>Delete for good after dialog B (subplan 09): the only permanent deletion; one question for all chosen.</summary>
    private async Task DeleteSelectedAsync()
    {
        List<QuarantineEntry> chosen = ChosenKept();
        if (chosen.Count == 0 || Services.Operations is not { } runner || FindForm() is not Form owner)
        {
            return;
        }

        long size = chosen.Sum(e => e.Size ?? 0);
        string title = string.Format(CultureInfo.CurrentCulture, Strings.Dialogs.DeleteTitle, UiFormat.Count(chosen.Count));
        string text = string.Format(CultureInfo.CurrentCulture, Strings.Dialogs.DeleteText, UiFormat.Size(size));
        if (!MessageDialog.Confirm(owner, title, text, Strings.Dialogs.KeepThem, Strings.Dialogs.DeleteForGood, destructive: true))
        {
            return;
        }

        await Task.Run(async () =>
        {
            foreach (QuarantineEntry entry in chosen)
            {
                await runner.PurgeOneAsync(entry.Operation.Id).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
        _table.ClearSelection();
        Reload();
    }

    private async Task UndoSelectedAsync()
    {
        if (_view == 1 && _table.FocusedIndex >= 0 && _table.FocusedIndex < _batches.Count)
        {
            await Remediator.UndoBatchAsync(Services, _batches[_table.FocusedIndex]).ConfigureAwait(true);
            Reload();
        }
    }

    private static string OperationResult(OperationStatus status) => status switch
    {
        OperationStatus.Done => Strings.Tools.Done,
        OperationStatus.Failed => Strings.Tools.Failed,
        OperationStatus.Undone => Strings.Tools.Undone,
        _ => Strings.Tools.Cancelled,
    };

    private void CreateReport()
    {
        if (FindForm() is Form owner)
        {
            CreateReportDialog.Show(owner, Services);
            Reload();
        }
    }

    /// <summary>Rows of the quarantine and operations tables (strings per column).</summary>
    private sealed class TableSource : IListSource
    {
        public IReadOnlyList<string[]> Rows { get; set; } = [];

        public int PillColumn { get; set; } = -1;

        public int Count => Rows.Count;

        public long IdOf(int index) => index;

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
                row.PillTier = cells[PillColumn] == Strings.Tools.Done ? Severity.None : cells[PillColumn] == Strings.Tools.Failed ? Severity.Critical : null;
            }
        }

        public void SetCollapsed(int index, bool collapsed)
        {
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
        private IReadOnlyList<(string Label, string Value)> _facts = [];
        private string? _licence;
        private int _view;
        private bool _busy;

        public FactsView(ToolsPage page)
        {
            _page = page;
            TabStop = false;
            Controls.Add(_changed);
        }

        public int MeasureHeight() => S(_view == 3 ? 40 + (_facts.Count * 28) + 16 + (_changed.Visible ? 52 : 0) + 240 + 40 + 48 : 40 + (_facts.Count * 28) + 16 + (_buttons.Count * 56));

        public void Bind(int view)
        {
            _view = view;
            foreach (KitButton button in _buttons)
            {
                Controls.Remove(button);
                button.Dispose();
            }

            _buttons.Clear();
            _captions.Clear();
            UiServices services = _page.Services;
            if (view == 3)
            {
                MediaEngineInfo engine = services.MediaEngine;
                string integrity = engine.Check.State switch { BundleState.Ok => Strings.Tools.IntegrityVerified, BundleState.Missing => Strings.Tools.IntegrityMissing, _ => Strings.Tools.IntegrityChanged };
                _facts = [(Strings.Tools.FfmpegVersion, engine.Version), (Strings.Tools.Build, engine.Build), (Strings.Tools.Licence, engine.Licence), (Strings.Tools.Location, engine.Location.Directory), (Strings.Tools.Integrity, integrity)];
                _changed.Visible = engine.Check.State is not (BundleState.Ok or BundleState.Missing);
                _licence = ReadLicence(engine.Location.Directory);
                AddButton(Strings.Tools.OpenLicencesFolder, null, ButtonKind.Secondary, () => ShellActions.OpenFolder(engine.Location.Directory));
                AddButton(Strings.Tools.WhereSource, null, ButtonKind.Secondary, () => ShellActions.OpenFolder(FfmpegVersions.SourceUrl));
            }
            else if (view == 4 && _page.Data is ToolsData d)
            {
                _facts = [(Strings.Tools.Location, d.DatabaseLocation), (Strings.Tools.Size, UiFormat.Size(d.DatabaseBytes)), (Strings.Tools.FilesIndexed, UiFormat.Count(d.FilesIndexed)), (Strings.Tools.LastBackup, d.LastBackup is DateTimeOffset b ? UiFormat.RecentDay(b, services.Clock.UtcNow) : Strings.Tools.Never)];
                _changed.Visible = false;
                // Each says how it went: Check threw its answer away, so damage it found was never shown.
                AddButton(Strings.Tools.BackUpNow, Strings.Tools.BackUpNowText, ButtonKind.Secondary, () => Run(Strings.Tools.BackUpNow, async () =>
                {
                    string file = await services.Database.Maintenance.BackupAsync().ConfigureAwait(true);
                    return new ToastContent(Severity.None, Strings.Tools.BackupSaved, Path.GetFileName(file), Strings.Tools.Open, () => ShellActions.ShowInFolder(file));
                }));
                AddButton(Strings.Tools.Compact, Strings.Tools.CompactText, ButtonKind.Secondary, () => Run(Strings.Tools.Compact, async () =>
                {
                    await services.Database.Maintenance.CompactAsync().ConfigureAwait(true);
                    long size = await Task.Run(services.Database.Maintenance.SizeBytes).ConfigureAwait(true);
                    return new ToastContent(Severity.None, Strings.Tools.Compacted, UiFormat.Size(size));
                }));
                AddButton(Strings.Tools.Check, Strings.Tools.CheckText, ButtonKind.Secondary, () => Run(Strings.Tools.Check, async () =>
                {
                    List<string> problems = await services.Database.Maintenance.CheckAsync().ConfigureAwait(true);
                    return problems.Count == 0
                        ? new ToastContent(Severity.None, Strings.Tools.CheckPassed, string.Empty)
                        : new ToastContent(Severity.Critical, Strings.Tools.CheckDamaged, problems[0]);
                }));
                AddButton(Strings.Tools.ResetEllipsis, Strings.Tools.ResetText, ButtonKind.Danger, ConfirmReset);
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
                TextWrap.Draw(g, _licence ?? Strings.Tools.EngineNotice, type.Mono, t.Text2, box.X + S(12), box.Y + S(8), box.Width - S(24), S(16));
                KitPaint.DrawText(g, Strings.Tools.EngineNotice, type.Body, t.Text2, 0, box.Bottom + S(24), Width);
                return;
            }

            for (int i = 0; i < _buttons.Count; i++)
            {
                if (_captions[i].Length > 0)
                {
                    KitPaint.DrawText(g, _captions[i], type.Body, t.Text2, S(156), _buttons[i].Top + S(16), Width - S(156));
                }
            }
        }

        private static string? ReadLicence(string directory)
        {
            try
            {
                string? file = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "LICENSE*").FirstOrDefault() : null;
                return file is null ? null : string.Join('\n', File.ReadLines(file).Take(12));
            }
            catch (IOException)
            {
                return null;
            }
        }

        private void AddButton(string text, string? caption, ButtonKind kind, Action click)
        {
            KitButton button = new() { Kind = kind, Text = text, TabIndex = _buttons.Count };
            button.Click += (_, _) => click();
            _buttons.Add(button);
            _captions.Add(caption ?? string.Empty);
            Controls.Add(button);
        }

        /// <summary>Runs one database action at a time and shows its result, or why it failed, in a toast.</summary>
        private async void Run(string action, Func<Task<ToastContent?>> work)
        {
            if (_page.Services.IsDemo || _busy)
            {
                return;
            }

            _busy = true;
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
                _busy = false;
            }

            if (result is not null)
            {
                _page.Services.ShowToast?.Invoke(result);
            }

            _page.Reload();
        }

        private void ConfirmReset()
        {
            if (FindForm() is Form owner && MessageDialog.Confirm(owner, Strings.Tools.ResetTitle, Strings.Tools.ResetBody, Strings.Kit.Cancel, Strings.Tools.Reset, destructive: true))
            {
                Run(Strings.Tools.Reset, async () =>
                {
                    await _page.Services.Database.Maintenance.ResetAsync().ConfigureAwait(true);
                    return null;
                });
            }
        }
    }
}

/// <summary>Tools → Create report (subplan 09): what, format and where; a toast offers to open the file.</summary>
internal sealed class CreateReportDialog : KitDialog
{
    private readonly UiServices _services;
    private readonly DropDown _what = new() { TabIndex = 0 };
    private readonly OptionRow[] _formats;
    private readonly PathBox _folder = new() { Inset = true, TabIndex = 4 };
    private readonly KitButton _create;

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
        BodyHeight = 276;
    }

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
    }

    private async Task CreateAsync()
    {
        ReportFormat format = _formats[1].Checked ? ReportFormat.Csv : _formats[2].Checked ? ReportFormat.Json : ReportFormat.Html;
        string folder = _folder.Text.Trim();

        // Checked off the UI thread: a folder on an offline network share makes the check wait.
        _create.Enabled = false;
        bool exists;
        try
        {
            exists = await Task.Run(() => Directory.Exists(folder)).ConfigureAwait(true);
        }
        finally
        {
            _create.Enabled = true;
        }

        if (!exists)
        {
            MessageDialog.Show(this, Strings.Tools.CreateReport, Strings.Library.ErrorMissing, Strings.Kit.Close);
            return;
        }

        string path = Path.Combine(folder, "MediaVerifier report " + _services.Clock.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + ReportWriter.Extension(format));
        bool lastScan = _what.SelectedIndex == 1;
        DateTimeOffset now = _services.Clock.UtcNow;
        if (!_services.IsDemo)
        {
            // The dialog stays open until the report is written; one report at a time.
            _create.Enabled = false;
            try
            {
                await Task.Run(() =>
                {
                    long? run = lastScan ? _services.Database.Runs.List(5).FirstOrDefault(r => r.Status != RunStatus.Running)?.Id : null;
                    long[] ids = _services.Database.Queries.FindingIds(run is long id ? new FindingFilter { FirstRun = id } : FindingFilter.OpenAll);
                    ReportWriter.Write(path, format, _services.Database.Queries.FindingRows(ids), now);
                    return _services.Database.Meta.SetAsync(LiveUiData.LastReportKey, now.UtcTicks.ToString(CultureInfo.InvariantCulture));
                }).ConfigureAwait(true);
            }
            finally
            {
                _create.Enabled = true;
            }
        }

        Finish(DialogResult.OK);
        _services.ShowToast?.Invoke(new ToastContent(Severity.None, Strings.Tools.ReportSaved, Path.GetFileName(path), Strings.Tools.Open, () => ShellActions.ShowInFolder(path)));
    }
}
