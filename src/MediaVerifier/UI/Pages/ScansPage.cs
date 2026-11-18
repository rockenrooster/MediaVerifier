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

/// <summary>Opens one run's detail (a notification's link, subplan 13).</summary>
internal sealed record ScanRunRequest(long RunId);

/// <summary>
/// Scans (subplan 09, Scans; task UI-07): the running scan in every phase, the interrupted-scan bar, the
/// history of runs and a run's details in the drawer.
/// </summary>
internal sealed class ScansPage : PageBase
{
    /// <summary>The card follows a running scan at 4 Hz; while nothing runs it only checks once a second.</summary>
    private const int ActivePoll = 250;
    private const int IdlePoll = 1000;

    private static readonly ListColumn[] HistoryColumns =
    [
        // A narrow table (smallest window, open drawer) hides columns in their drop order so Result stays in view.
        new(Strings.Scans.ColStarted, 152, Sortable: false),
        new(Strings.Scans.ColStartedBy, 108, Sortable: false, Drop: 1),
        new(Strings.Scans.ColDepth, 92, Sortable: false, Drop: 2),
        new(Strings.Scans.ColFolders, 168, Sortable: false, Drop: 6),
        new(Strings.Scans.ColDuration, 104, RightAligned: true, Sortable: false, Drop: 3),
        new(Strings.Scans.ColFiles, 84, RightAligned: true, Sortable: false, Drop: 7),
        new(Strings.Scans.ColNew, 60, RightAligned: true, Sortable: false, Drop: 5),
        new(Strings.Scans.ColResolved, 84, RightAligned: true, Sortable: false, Drop: 4),
        new(Strings.Scans.ColResult, 0, Sortable: false, Pad: 28, MinWidth: 160),
    ];

    private static readonly string AllFoldersAtLineStart = char.ToUpper(Strings.Scans.AllFolders[0], CultureInfo.CurrentCulture) + Strings.Scans.AllFolders[1..];

    private readonly InfoBar _interrupted = new() { Tier = Severity.Medium, Text = Strings.Scans.InterruptedBar, Action = Strings.Scans.Continue, TabIndex = 0 };
    private readonly RunCard _run;
    private readonly Card _historyCard = new() { Title = Strings.Scans.History, TabIndex = 2 };
    private readonly VirtualList _history = new() { Mode = ListMode.Table, Icons = false, AccessibleName = Strings.Drawer.History };
    private readonly HistorySource _source = new();
    private readonly RunDetailView _detail;
    private readonly ScrollPanel _detailScroll;
    private readonly System.Windows.Forms.Timer _poll;

    // Not a page timer: it turns the ring only while a scan runs, so an idle Scans page does not wake 20 times a second.
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 50 };
    private ScansData? _data;
    private int _openRequest;
    private bool _continuing;

    public ScansPage(UiServices services)
        : base(services)
    {
        _run = new RunCard(this) { TabIndex = 1 };
        _detail = new RunDetailView(this);
        _detailScroll = new ScrollPanel(_detail);
        _history.Columns = HistoryColumns;
        _history.Source = _source;
        _history.SelectionChanged += (_, _) => OpenFocusedRun();
        _history.ItemActivated += (_, index) => OpenRun(index);
        _historyCard.Controls.Add(_history);
        _interrupted.ActionClicked += (_, _) => ContinueInterrupted();
        _interrupted.Visible = false;
        Controls.AddRange([_interrupted, _run, _historyCard]);
        _poll = CreatePageTimer(IdlePoll, (_, _) => _run.UpdateStatus());
        _spin.Tick += (_, _) => Spin();
        Services.ScanStarted += OnScanStarted;
    }

    public override PageId Id => PageId.Scans;

    internal ScansData? Data => _data;

    /// <summary>
    /// A scope at the start of a line or in the Folders column: <c>all folders</c> capitalised; folder names and
    /// <c>{n} files</c> as they are.
    /// </summary>
    internal static string AtLineStart(string scope) => scope == Strings.Scans.AllFolders ? AllFoldersAtLineStart : scope;

    internal void StartLibraryScan()
    {
        if (FindForm() is Form owner)
        {
            UiTask.Run(ScanLauncher.StartLibraryScanAsync(owner, Services, State.Settings.Scanning.DefaultDepth, null));
        }
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        _run.UpdateStatus();
        Reload();
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        _spin.Stop();
        if (_detail.Detail is not null)
        {
            Services.ShowDrawer?.Invoke(null);
        }
    }

    public override void HandleRequest(object request)
    {
        if (request is ScanRunRequest run)
        {
            UiTask.Run(ShowRunAsync(run.RunId));
        }
    }

    protected override object? LoadData() => Services.Data.LoadScans();

    protected override void ApplyData(object data)
    {
        _data = (ScansData)data;
        _interrupted.Visible = _data.Interrupted is not null && !Services.Scans.Status.IsActive && !_continuing;
        _source.SetRows(_data.History, Services.Clock.UtcNow);
        _history.Source = _source;
        _history.Invalidate();
        _run.SetLastScan(_data.LastScan);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int y = 0;
        if (_interrupted.Visible)
        {
            _interrupted.Bounds = new Rectangle(0, 0, Width, S(40));
            y = S(52);
        }

        _run.Bounds = new Rectangle(0, y, Width, S(_run.LogicalHeight));
        int top = _run.Bottom + S(13);
        _historyCard.Bounds = new Rectangle(0, top, Width, Math.Max(S(120), Height - top));
        _history.Bounds = new Rectangle(S(1), S(40), _historyCard.Width - S(2), _historyCard.Height - S(41));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Services.ScanStarted -= OnScanStarted;
            _spin.Dispose();
            if (_detailScroll.Parent is null)
            {
                _detailScroll.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>The ring turns only while a scan runs and the page is on show.</summary>
    private void SetSpinning(bool spinning) => _spin.Enabled = spinning && IsActive && !IsSuspended && SystemInformation.UIEffectsEnabled;

    private void SetPollInterval(int interval)
    {
        if (_poll.Interval != interval)
        {
            _poll.Interval = interval;
        }
    }

    private void Spin()
    {
        if (!IsActive || IsSuspended)
        {
            // Minimised or hidden: the poll restarts the ring when the page is shown again.
            _spin.Stop();
            return;
        }

        _run.Spin();
    }

    /// <summary>A scan started anywhere shows in the card at once, not at the next poll.</summary>
    private void OnScanStarted()
    {
        if (IsActive && !IsDisposed)
        {
            _run.UpdateStatus();
        }
    }

    private void ContinueInterrupted()
    {
        if (_data?.Interrupted is RunInfo run && !_continuing && FindForm() is Form owner)
        {
            UiTask.Run(ContinueFromBarAsync(owner, run.Id));
        }
    }

    /// <summary>The bar goes once the scan has started; when it cannot start, a toast says why and Continue stays offered.</summary>
    private async Task ContinueFromBarAsync(Form owner, long runId)
    {
        _continuing = true;
        try
        {
            if (await ContinueAsync(Services, owner, runId).ConfigureAwait(true) && !IsDisposed)
            {
                _interrupted.Visible = false;
                PerformLayout();
                _run.UpdateStatus();
            }
        }
        finally
        {
            _continuing = false;
        }
    }

    /// <summary>
    /// Continue an interrupted scan (the Scans bar and the Dashboard's recommendation; subplan 05, Run lifecycle: a new
    /// run with the same scope). The run's folders that are still watched and enabled are scanned at its depth, asking
    /// first when the scan is large; files it finished are skipped. It used to scan every Library folder under the old
    /// run's heading. A run whose stored scope names no folder continues as a scan of every Library folder. False when
    /// nothing started (a toast says why).
    /// </summary>
    internal static async Task<bool> ContinueAsync(UiServices services, Form owner, long runId)
    {
        (RunRecord? run, List<RootRecord> roots) = await Task.Run(() => (services.Database.Runs.Get(runId), services.Database.Roots.List())).ConfigureAwait(true);
        if (run is null)
        {
            services.ShowScanNotStarted(Strings.Scans.RunGone);
            return false;
        }

        List<ScanScope> scopes = LiveUiData.ScopesOf(run.ScopeJson, roots)
            ?? [.. roots.Where(r => r.Role == RootRole.Library && r.Enabled).Select(r => new ScanScope(r.Id))];
        return await ScanLauncher.StartAsync(owner, services, scopes, run.Depth, LiveUiData.ScopeText(scopes, roots)).ConfigureAwait(true);
    }

    /// <summary>Selecting a row opens it; a Ctrl+click that deselects the focused row no longer opens the run it deselected.</summary>
    private void OpenFocusedRun()
    {
        int index = _history.FocusedIndex;
        if (index >= 0 && index < _source.Count && _history.SelectedIds.Contains(_source.IdOf(index)))
        {
            OpenRun(index);
        }
    }

    private void OpenRun(int index)
    {
        if (_data is null || index < 0 || index >= _data.History.Count)
        {
            return;
        }

        UiTask.Run(ShowRunAsync(_data.History[index].Id));
    }

    /// <summary>
    /// Shows a run's detail in the drawer. Only the newest request is shown: arrowing through History could leave the
    /// drawer on a row whose load finished last. A run that no longer exists says so.
    /// </summary>
    private async Task ShowRunAsync(long runId)
    {
        int request = ++_openRequest;
        RunDetail? detail = await Task.Run(() => Services.Data.LoadRunDetail(runId)).ConfigureAwait(true);
        if (IsDisposed || request != _openRequest)
        {
            return;
        }

        if (detail is null)
        {
            Services.ShowToast?.Invoke(new ToastContent(Severity.Low, Strings.Scans.RunGone, string.Empty));
            return;
        }

        _detail.Bind(detail);
        _detailScroll.ScrollToTop();
        Services.ShowDrawer?.Invoke(_detailScroll);
    }

    internal static string ResultText(RunStatus status) => status switch
    {
        RunStatus.Completed => Strings.Scans.Completed,
        RunStatus.Cancelled => Strings.Scans.Stopped,
        RunStatus.BudgetReached => Strings.Scans.TimeLimit,
        RunStatus.Interrupted => Strings.Scans.Interrupted,
        RunStatus.Failed => Strings.Scans.CouldNotRun,
        _ => Strings.Scans.Skipped,
    };

    /// <summary>The pill colours of a result: OK, a tier, or neutral (null).</summary>
    internal static Severity? ResultTier(RunStatus status) => status switch
    {
        RunStatus.Completed => Severity.None,
        RunStatus.Cancelled or RunStatus.Interrupted => Severity.Medium,
        RunStatus.BudgetReached => Severity.Low,
        RunStatus.Failed => Severity.Critical,
        _ => null,
    };

    /// <summary>The history rows, with every cell's text made when the rows load: painting allocates nothing (ListRow).</summary>
    private sealed class HistorySource : IListSource
    {
        private IReadOnlyList<RunInfo> _rows = [];
        private string[][] _cells = [];

        public int Count => _rows.Count;

        public void SetRows(IReadOnlyList<RunInfo> rows, DateTimeOffset now)
        {
            _rows = rows;
            _cells = new string[rows.Count][];
            for (int i = 0; i < rows.Count; i++)
            {
                RunInfo run = rows[i];
                _cells[i] =
                [
                    UiFormat.RecentDate(run.Started, now),
                    run.Trigger == RunTrigger.Scheduled ? Strings.Scans.ScheduleBy : Strings.Scans.You,
                    UiFormat.DepthName(run.Depth),
                    AtLineStart(run.Folders),
                    run.Ended is DateTimeOffset ended && run.Status != RunStatus.Failed ? UiFormat.Duration(ended - run.Started) : Strings.Dash,
                    UiFormat.Count(run.Files),
                    UiFormat.Count(run.NewFindings),
                    UiFormat.Count(run.Resolved),
                    ResultText(run.Status),
                ];
            }
        }

        public long IdOf(int index) => index < _rows.Count ? _rows[index].Id : -1 - index;

        public void Fill(int index, ListRow row)
        {
            string[] cells = _cells[index];
            row.Line1 = cells[0];
            for (int i = 1; i < cells.Length; i++)
            {
                row.Cells[i] = cells[i];
            }

            row.PillColumn = 8;
            row.PillTier = ResultTier(_rows[index].Status);
        }

        public void SetCollapsed(int index, bool collapsed)
        {
        }
    }

    /// <summary>
    /// The running scan, a scheduled scan running in the background, or "No scan is running" with a Scan button. Texts
    /// are made when the status is read, so painting (also for each turn of the ring) allocates nothing.
    /// </summary>
    private sealed class RunCard : Card
    {
        private const int MaxRows = 8;
        private static readonly string[] Labels = [Strings.Scans.Files, Strings.Scans.DataRead, Strings.Scans.Speed, Strings.Scans.TimeLeft, Strings.Scans.FoundSoFar];
        private static readonly Severity[] Tiers = [Severity.Critical, Severity.High, Severity.Medium, Severity.Low];
        private readonly ScansPage _page;
        private readonly KitButton _pause = new() { Kind = ButtonKind.Secondary, Icon = IconName.Pause, Text = Strings.Shell.Pause, Visible = false };
        private readonly KitButton _stop = new() { Kind = ButtonKind.Secondary, Icon = IconName.Stop, Text = Strings.Shell.Stop, Visible = false };
        private readonly KitButton _scan = new() { Kind = ButtonKind.Primary, Icon = IconName.Play, Text = Strings.Shell.Scan };
        private readonly long _start = Environment.TickCount64;

        // Each file keeps the row it started in: the engine's list comes in a different order at each poll.
        private readonly InFlightItem?[] _slots = new InFlightItem?[MaxRows];
        private readonly string?[] _slotNames = new string?[MaxRows];
        private readonly string[] _values = new string[4];
        private readonly string[] _found = new string[4];
        private EngineStatus _status = EngineStatus.Idle;
        private bool _updated;
        private bool _background;
        private bool _stopping;
        private string? _reason;
        private int _rows;
        private string _heading = string.Empty;
        private string _sub = string.Empty;
        private string _percent = string.Empty;
        private string _last = string.Empty;

        public RunCard(ScansPage page)
        {
            _page = page;
            _pause.Click += (_, _) => TogglePause();
            _stop.Click += (_, _) => AskStop();
            _scan.Click += (_, _) => _page.StartLibraryScan();
            Controls.AddRange([_pause, _stop, _scan]);
        }

        /// <summary>
        /// The card's height: rows are kept for the most files worked on at once in this run, so the card no longer
        /// grows and shrinks (moving History under the pointer) each time a worker is between two files.
        /// </summary>
        public int LogicalHeight => !_status.IsActive || _rows == 0 ? 176 : 207 + (28 * _rows);

        /// <summary>The page's data changed: "Last scan {when}." under "No scan is running".</summary>
        public void SetLastScan(DateTimeOffset? lastScan)
        {
            _last = lastScan is DateTimeOffset when ? string.Format(CultureInfo.CurrentCulture, Strings.Scans.LastScan, UiFormat.RecentDate(when, _page.Services.Clock.UtcNow, inSentence: true)) : string.Empty;
            Invalidate();
        }

        public void UpdateStatus()
        {
            EngineStatus status = _page.Services.Scans.Status;
            bool background = !status.IsActive && _page.Services.BackgroundRunActive();
            string? reason = status.IsActive ? null : _page.Services.CannotScanReason(true);
            bool spinning = status.IsActive && status.Phase is not (EnginePhase.Paused or EnginePhase.Pausing);
            _page.SetSpinning(spinning);
            _page.SetPollInterval(status.IsActive ? ActivePoll : IdlePoll);

            // Idle and nothing new: no layout, no repaint (UI-02, 0 % CPU when idle).
            if (_updated && !status.IsActive && !_status.IsActive && background == _background && reason == _reason)
            {
                return;
            }

            _updated = true;
            if (!status.IsActive || status.RunId != _status.RunId)
            {
                Array.Clear(_slots);
                _rows = 0;
                _stopping = false;
            }

            int height = LogicalHeight;
            PlaceInFlight(status.InFlight);
            _status = status;
            _background = background;
            _reason = reason;
            bool active = status.IsActive;
            bool paused = status.Phase is EnginePhase.Paused or EnginePhase.Pausing;

            // Stop was confirmed, or the run is writing its end: Pause and Stop can do nothing more (Pause was ignored).
            bool finishing = status.Phase == EnginePhase.Finishing || _stopping;
            _pause.Visible = active;
            _stop.Visible = active;
            _scan.Visible = !active;
            _pause.Text = paused ? Strings.Shell.Resume : Strings.Shell.Pause;
            _pause.Icon = paused ? IconName.Play : IconName.Pause;
            _pause.Enabled = !finishing;
            _stop.Enabled = !finishing;
            KitToolTip.Set(_pause, finishing ? Strings.Shell.Finishing : string.Empty);
            KitToolTip.Set(_stop, finishing ? Strings.Shell.Finishing : string.Empty);

            // A scheduled scan in the background, or no media engine: Scan says why it cannot run, as the header's does.
            _scan.Enabled = reason is null;
            KitToolTip.Set(_scan, reason ?? string.Empty);
            if (active)
            {
                MakeTexts(status, paused, finishing);
            }

            if (height != LogicalHeight)
            {
                _page.PerformLayout();
            }

            PerformLayout();
            Invalidate();
        }

        public void Spin() => Invalidate(new Rectangle(S(12), S(12), S(28), S(30)));

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int stopWidth = S(_stop.PreferredLogicalWidth);
            _stop.Bounds = new Rectangle(Width - S(16) - stopWidth, S(17), stopWidth, S(32));
            int pauseWidth = S(_pause.PreferredLogicalWidth);
            _pause.Bounds = new Rectangle(_stop.Left - S(8) - pauseWidth, S(17), pauseWidth, S(32));
            int scanWidth = S(_scan.PreferredLogicalWidth);
            _scan.Bounds = new Rectangle((Width - scanWidth) / 2, S(124), scanWidth, S(32));
        }

        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            TypeRamp type = Type;
            EngineStatus s = _status;
            if (!s.IsActive)
            {
                IconRenderer.Draw(g, IconName.Scans, (Width / 2f) - SF(16), SF(20), 32, DeviceDpi, t.Text3);
                KitPaint.DrawTextCentred(g, _background ? Strings.Shell.ScheduledRunning : Strings.Scans.NoScan, type.Subtitle, t.Text, Width / 2, S(76));
                KitPaint.DrawTextCentred(g, _last, type.Body, t.Text2, Width / 2, S(100));
                return;
            }

            if (s.Phase is EnginePhase.Paused or EnginePhase.Pausing)
            {
                IconRenderer.Draw(g, IconName.Pause, SF(16), SF(23), 16, DeviceDpi, t.Text2);
            }
            else
            {
                float angle = (float)((Environment.TickCount64 - _start) % 1000 * 360 / 1000.0);
                DataPaint.ProgressRing(g, new PointF(SF(25), SF(31)), SF(8), angle, DeviceDpi);
            }

            KitPaint.DrawText(g, _heading, type.Subtitle, t.Text, S(46), S(31), _pause.Left - S(56));
            KitPaint.DrawText(g, _sub, type.Body, t.Text2, S(16), S(59), Width - S(32));

            float barRight = Width - SF(72);
            DataPaint.ProgressBar(g, new RectangleF(SF(16), SF(84), barRight - SF(16), SF(8)), s.Fraction);
            KitPaint.DrawText(g, _percent, type.BodyStrong, t.Text, Width - S(64), S(89), S(48), TextFormatFlags.Right);

            int column = (Width - S(32)) / 5;
            for (int i = 0; i < Labels.Length; i++)
            {
                int x = S(16) + (i * column);
                KitPaint.DrawText(g, Labels[i], type.Caption, t.Text3, x, S(123), column - S(8));
                if (i < _values.Length)
                {
                    KitPaint.DrawText(g, _values[i], type.BodyStrong, t.Text, x, S(143), column - S(8));
                }
            }

            int fx = S(16) + (4 * column);
            for (int i = 0; i < Tiers.Length; i++)
            {
                IconRenderer.DrawSeverity(g, Tiers[i], fx, SF(135), 16, DeviceDpi, t.ForSeverity(Tiers[i]).Text, t.Surface);
                KitPaint.DrawText(g, _found[i], type.BodyStrong, t.Text, fx + S(20), S(143), S(40));
                fx += S(20) + KitPaint.MeasureWidth(_found[i], type.BodyStrong) + S(12);
            }

            if (_rows == 0)
            {
                return;
            }

            KitPaint.HorizontalLine(g, t.Stroke, S(16), Width - S(16), S(166), SF(1));
            KitPaint.DrawText(g, Strings.Scans.WorkingOn, type.CaptionStrong, t.Text3, S(16), S(186), Width - S(32));
            for (int i = 0; i < _rows; i++)
            {
                if (_slots[i] is not InFlightItem item)
                {
                    continue;
                }

                int y = S(215 + (28 * i));
                IconName icon = item.Kind switch { MediaKind.Video => IconName.Video, MediaKind.Photo => IconName.Photo, _ => IconName.Music };
                IconRenderer.Draw(g, icon, SF(16), y - SF(8), 16, DeviceDpi, t.Text2);
                KitPaint.DrawText(g, _slotNames[i] ?? string.Empty, type.Body, t.Text, S(44), y, (Width / 2) - S(60));
                KitPaint.DrawText(g, item.Stage, type.Body, t.Text2, S(500), y, Width - S(700));
                DataPaint.ProgressBar(g, new RectangleF(Width - SF(176), y - SF(2), SF(160), SF(4)), item.Fraction);
            }
        }

        /// <summary>The files worked on keep their rows; a new file takes the first free row.</summary>
        private void PlaceInFlight(IReadOnlyList<InFlightItem> inFlight)
        {
            int count = Math.Min(MaxRows, inFlight.Count);
            for (int slot = 0; slot < MaxRows; slot++)
            {
                InFlightItem? kept = null;
                for (int i = 0; i < count && _slots[slot] is not null; i++)
                {
                    if (string.Equals(inFlight[i].Path, _slots[slot]!.Path, StringComparison.Ordinal))
                    {
                        kept = inFlight[i];
                        break;
                    }
                }

                if (kept is null)
                {
                    _slotNames[slot] = null;
                }

                _slots[slot] = kept;
            }

            for (int i = 0; i < count; i++)
            {
                if (Array.FindIndex(_slots, s => s is not null && string.Equals(s.Path, inFlight[i].Path, StringComparison.Ordinal)) >= 0)
                {
                    continue;
                }

                int free = Array.IndexOf(_slots, null);
                if (free < 0)
                {
                    break;
                }

                _slots[free] = inFlight[i];
                _slotNames[free] = Path.GetFileName(inFlight[i].Path);
            }

            for (int slot = MaxRows - 1; slot >= _rows; slot--)
            {
                if (_slots[slot] is not null)
                {
                    _rows = slot + 1;
                    break;
                }
            }
        }

        private void MakeTexts(EngineStatus s, bool paused, bool finishing)
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            string scope = _page.Services.Scans.ScopeText;
            _heading = finishing ? Strings.Shell.Finishing
                : paused ? string.Format(culture, Strings.Scans.PausedScope, scope)
                : s.Phase == EnginePhase.Enumerating ? string.Format(culture, Strings.Scans.FindingIn, scope)
                : string.Format(culture, Strings.Scans.Scanning, scope);
            DateTimeOffset now = _page.Services.Clock.UtcNow;
            string started = UiFormat.TimeOfDay((now - s.Elapsed).ToLocalTime().DateTime);
            _sub = s.Trigger == RunTrigger.Scheduled
                ? string.Format(culture, Strings.Scans.SubLineSchedule, UiFormat.DepthName(s.Depth), started, _page.Data?.RunningPlan ?? string.Empty)
                : string.Format(culture, Strings.Scans.SubLineYou, UiFormat.DepthName(s.Depth), started);
            _percent = UiFormat.Percent(s.Fraction * 100);
            _values[0] = string.Format(culture, Strings.Scans.OfTotal, UiFormat.Count(s.FilesDone), UiFormat.Count(s.Total));
            _values[1] = UiFormat.Size(s.BytesRead);

            // Nothing is read while paused: the last rate was shown as if the scan were still reading.
            _values[2] = paused ? Strings.Dash : UiFormat.Rate(s.BytesPerSecond);
            _values[3] = s.Estimating || s.Remaining is null ? Strings.Scans.Estimating : UiFormat.About(s.Remaining.Value);
            for (int i = 0; i < Tiers.Length; i++)
            {
                _found[i] = UiFormat.Count(s.NewFindings[Tiers[i]]);
            }
        }

        private void TogglePause()
        {
            if (_page.Services.Scans.Status.Phase is EnginePhase.Paused or EnginePhase.Pausing)
            {
                _page.Services.Scans.Resume();
            }
            else
            {
                _page.Services.Scans.Pause();
            }

            UpdateStatus();
        }

        private void AskStop()
        {
            if (FindForm() is Form owner && MessageDialog.Confirm(owner, Strings.Shell.StopTitle, Strings.Shell.StopText, Strings.Shell.KeepScanning, Strings.Shell.StopButton, destructive: false))
            {
                _page.Services.Scans.Cancel();
                _stopping = _page.Services.Scans.Status.IsActive;
                _updated = false;
                UpdateStatus();
            }
        }
    }

    /// <summary>A finished run in the drawer: heading, numbers, alerts, errors and a link to its findings.</summary>
    private sealed class RunDetailView : KitControl
    {
        private static readonly string[] Labels = [Strings.Scans.Files, Strings.Scans.DataRead, Strings.Scans.ColNew, Strings.Scans.ColResolved];
        private readonly ScansPage _page;
        private readonly IconButton _close = new() { Icon = IconName.Close };
        private readonly KitButton _problems = new() { Kind = ButtonKind.Secondary, Icon = IconName.Issues, Text = Strings.Scans.ShowProblems };
        private readonly List<InfoBar> _alerts = [];
        private readonly string[] _values = new string[4];
        private string _heading = string.Empty;
        private string _when = string.Empty;
        private string _errors = string.Empty;

        public RunDetailView(ScansPage page)
        {
            _page = page;
            _close.ToolTipText = Strings.Kit.CloseEsc;
            _close.Click += (_, _) => _page.Services.ShowDrawer?.Invoke(null);
            _problems.Click += (_, _) =>
            {
                if (Detail is RunDetail d)
                {
                    _page.Services.ShowDrawer?.Invoke(null);
                    _page.Services.Navigate?.Invoke(PageId.Issues, new IssuesFilterRequest(null, null, d.Run.Id));
                }
            };
            Controls.AddRange([_close, _problems]);
        }

        public RunDetail? Detail { get; private set; }

        public void Bind(RunDetail detail)
        {
            Detail = detail;
            RunInfo run = detail.Run;
            _heading = string.Format(CultureInfo.CurrentCulture, Strings.Scans.RunOf, run.Folders);
            _when = UiFormat.Facts(
                UiFormat.RecentDate(run.Started, _page.Services.Clock.UtcNow),
                run.Trigger == RunTrigger.Scheduled ? Strings.Scans.ScheduleBy : Strings.Scans.You,
                UiFormat.DepthName(run.Depth),
                run.Ended is DateTimeOffset ended && run.Status != RunStatus.Failed ? UiFormat.Duration(ended - run.Started) : null);
            _values[0] = UiFormat.Count(run.Files);
            _values[1] = UiFormat.Size(detail.BytesRead);
            _values[2] = UiFormat.Count(run.NewFindings);
            _values[3] = UiFormat.Count(run.Resolved);

            // Errors count files that failed and folders that could not be listed.
            _errors = detail.Errors > 0 ? UiFormat.Plural(detail.Errors, Strings.Scans.ErrorsCountOne, Strings.Scans.ErrorsCount) : string.Empty;
            foreach (InfoBar bar in _alerts)
            {
                Controls.Remove(bar);
                bar.Dispose();
            }

            _alerts.Clear();
            foreach ((Severity tier, string text) in detail.Alerts)
            {
                InfoBar bar = new() { Tier = tier, Text = text };
                _alerts.Add(bar);
                Controls.Add(bar);
            }

            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            _close.Bounds = new Rectangle(Width - S(52), S(16), S(32), S(32));
            int y = S(196);
            foreach (InfoBar bar in _alerts)
            {
                bar.Bounds = new Rectangle(S(20), y, Width - S(40), S(40));
                y += S(48);
            }

            if (_errors.Length > 0)
            {
                y += S(28);
            }

            int width = S(_problems.PreferredLogicalWidth);
            _problems.Bounds = new Rectangle(S(20), y + S(8), width, S(32));
            int height = _problems.Bottom + S(20);
            if (Height != height)
            {
                Height = height;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            if (Detail is not RunDetail d)
            {
                return;
            }

            TypeRamp type = Type;
            KitPaint.DrawText(g, _heading, type.Subtitle, t.Text, S(20), S(32), Width - S(80));
            KitPaint.DrawText(g, _when, type.Body, t.Text2, S(20), S(58), Width - S(40));
            Severity? tier = ResultTier(d.Run.Status);
            (Color fg, Color bg) = tier switch
            {
                Severity s when s != Severity.None => (t.ForSeverity(s).Text, t.ForSeverity(s).Pill),
                Severity => (t.Ok.Text, t.Ok.Pill),
                null => (t.Text2, t.NeutralPill),
            };
            BadgePainter.PaintPill(g, S(20), S(76), ResultText(d.Run.Status), fg, bg, DeviceDpi);
            int column = (Width - S(40)) / 4;
            for (int i = 0; i < Labels.Length; i++)
            {
                KitPaint.DrawText(g, Labels[i], type.Caption, t.Text3, S(20) + (i * column), S(128), column - S(8));
                KitPaint.DrawText(g, _values[i], type.BodyStrong, t.Text, S(20) + (i * column), S(148), column - S(8));
            }

            KitPaint.HorizontalLine(g, t.Stroke, 0, Width, S(176), SF(1));
            if (_errors.Length > 0)
            {
                int y = S(196) + (_alerts.Count * S(48)) + S(14);
                KitPaint.DrawText(g, _errors, type.Body, t.Critical.Text, S(20), y, Width - S(40));
            }
        }

        protected override void OnThemeChanged()
        {
            BackColor = Tokens.Surface;
            base.OnThemeChanged();
        }
    }
}
