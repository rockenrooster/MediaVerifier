using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// Scans (subplan 09, Scans; task UI-07): the running scan in every phase, the interrupted-scan bar, the
/// history of runs and a run's details in the drawer.
/// </summary>
/// <summary>Opens one run's detail (a notification's link, subplan 13).</summary>
internal sealed record ScanRunRequest(long RunId);

internal sealed class ScansPage : PageBase
{
    private static readonly ListColumn[] HistoryColumns =
    [
        new(Strings.Scans.ColStarted, 152, Sortable: false),
        new(Strings.Scans.ColStartedBy, 108, Sortable: false),
        new(Strings.Scans.ColDepth, 92, Sortable: false),
        new(Strings.Scans.ColFolders, 168, Sortable: false),
        new(Strings.Scans.ColDuration, 104, RightAligned: true, Sortable: false),
        new(Strings.Scans.ColFiles, 84, RightAligned: true, Sortable: false),
        new(Strings.Scans.ColNew, 60, RightAligned: true, Sortable: false),
        new(Strings.Scans.ColResolved, 84, RightAligned: true, Sortable: false),
        new(Strings.Scans.ColResult, 0, Sortable: false, Pad: 28),
    ];

    private readonly InfoBar _interrupted = new() { Tier = Severity.Medium, Text = Strings.Scans.InterruptedBar, Action = Strings.Scans.Continue, TabIndex = 0 };
    private readonly RunCard _run;
    private readonly Card _historyCard = new() { Title = Strings.Scans.History, TabIndex = 2 };
    private readonly VirtualList _history = new() { Mode = ListMode.Table, Icons = false };
    private readonly HistorySource _source;
    private readonly RunDetailView _detail;
    private readonly ScrollPanel _detailScroll;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly System.Windows.Forms.Timer _spin;
    private ScansData? _data;

    public ScansPage(UiServices services)
        : base(services)
    {
        _run = new RunCard(this) { TabIndex = 1 };
        _source = new HistorySource(this);
        _detail = new RunDetailView(this);
        _detailScroll = new ScrollPanel(_detail);
        _history.Columns = HistoryColumns;
        _history.Source = _source;
        _history.SelectionChanged += (_, _) => OpenRun(_history.FocusedIndex);
        _history.ItemActivated += (_, index) => OpenRun(index);
        _historyCard.Controls.Add(_history);
        _interrupted.ActionClicked += (_, _) => ContinueInterrupted();
        _interrupted.Visible = false;
        Controls.AddRange([_interrupted, _run, _historyCard]);
        _poll = CreatePageTimer(250, (_, _) => _run.UpdateStatus());
        _spin = CreatePageTimer(50, (_, _) => _run.Spin());
    }

    public override PageId Id => PageId.Scans;

    internal ScansData? Data => _data;

    internal void StartLibraryScan()
    {
        if (FindForm() is Form owner)
        {
            _ = ScanLauncher.StartLibraryScanAsync(owner, Services, State.Settings.Scanning.DefaultDepth, null);
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
        if (_detail.Detail is not null)
        {
            Services.ShowDrawer?.Invoke(null);
        }
    }

    public override void HandleRequest(object request)
    {
        if (request is ScanRunRequest run)
        {
            _ = ShowRunAsync(run.RunId);
        }
    }

    protected override object? LoadData() => Services.Data.LoadScans();

    protected override void ApplyData(object data)
    {
        _data = (ScansData)data;
        _interrupted.Visible = _data.Interrupted is not null && !Services.Scans.Status.IsActive;
        _source.Rows = _data.History;
        _history.Source = _source;
        _history.Invalidate();
        _run.Invalidate();
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
        if (disposing && _detailScroll.Parent is null)
        {
            _detailScroll.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ContinueInterrupted()
    {
        if (_data?.Interrupted is RunInfo run)
        {
            _ = Services.StartScanAsync([.. Services.Database.Roots.List().Where(r => r.Role == RootRole.Library && r.Enabled).Select(r => new ScanScope(r.Id))], run.Depth, run.Folders);
            _interrupted.Visible = false;
            PerformLayout();
        }
    }

    private async void OpenRun(int index)
    {
        if (_data is null || index < 0 || index >= _data.History.Count)
        {
            return;
        }

        await ShowRunAsync(_data.History[index].Id).ConfigureAwait(true);
    }

    /// <summary>Shows a run's detail in the drawer; nothing when the run does not exist.</summary>
    private async Task ShowRunAsync(long runId)
    {
        RunDetail? detail = await Task.Run(() => Services.Data.LoadRunDetail(runId)).ConfigureAwait(true);
        if (detail is null || IsDisposed)
        {
            return;
        }

        _detail.Width = Math.Max(1, Dpi.Scale(Drawer.DefaultWidth, DeviceDpi) - 1);
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

    private sealed class HistorySource(ScansPage page) : IListSource
    {
        public IReadOnlyList<RunInfo> Rows { get; set; } = [];

        public int Count => Rows.Count;

        public long IdOf(int index) => index < Rows.Count ? Rows[index].Id : -1 - index;

        public void Fill(int index, ListRow row)
        {
            RunInfo run = Rows[index];
            DateTimeOffset now = page.Services.Clock.UtcNow;
            row.Line1 = UiFormat.RecentDate(run.Started, now);
            row.Cells[1] = run.Trigger == RunTrigger.Scheduled ? Strings.Scans.ScheduleBy : Strings.Scans.You;
            row.Cells[2] = UiFormat.DepthName(run.Depth);
            row.Cells[3] = run.Folders;
            row.Cells[4] = run.Ended is DateTimeOffset ended && run.Status != RunStatus.Failed ? UiFormat.Duration(ended - run.Started) : Strings.Dash;
            row.Cells[5] = UiFormat.Count(run.Files);
            row.Cells[6] = UiFormat.Count(run.NewFindings);
            row.Cells[7] = UiFormat.Count(run.Resolved);
            row.Cells[8] = ResultText(run.Status);
            row.PillColumn = 8;
            row.PillTier = ResultTier(run.Status);
        }

        public void SetCollapsed(int index, bool collapsed)
        {
        }
    }

    /// <summary>The running scan, or "No scan is running" with a Scan button.</summary>
    private sealed class RunCard : Card
    {
        private readonly ScansPage _page;
        private readonly KitButton _pause = new() { Kind = ButtonKind.Secondary, Icon = IconName.Pause, Text = Strings.Shell.Pause };
        private readonly KitButton _stop = new() { Kind = ButtonKind.Secondary, Icon = IconName.Stop, Text = Strings.Shell.Stop };
        private readonly KitButton _scan = new() { Kind = ButtonKind.Primary, Icon = IconName.Play, Text = Strings.Shell.Scan };
        private readonly long _start = Environment.TickCount64;
        private EngineStatus _status = EngineStatus.Idle;

        public RunCard(ScansPage page)
        {
            _page = page;
            _pause.Click += (_, _) => TogglePause();
            _stop.Click += (_, _) => AskStop();
            _scan.Click += (_, _) => _page.StartLibraryScan();
            Controls.AddRange([_pause, _stop, _scan]);
        }

        public int LogicalHeight => !_status.IsActive ? 176 : _status.InFlight.Count == 0 ? 176 : 207 + (28 * Math.Min(8, _status.InFlight.Count));

        public void UpdateStatus()
        {
            EngineStatus status = _page.Services.Scans.Status;
            bool resize = status.IsActive != _status.IsActive || Math.Min(8, status.InFlight.Count) != Math.Min(8, _status.InFlight.Count);
            _status = status;
            bool active = status.IsActive;
            _pause.Visible = active;
            _stop.Visible = active;
            _scan.Visible = !active;
            _pause.Text = status.Phase is EnginePhase.Paused or EnginePhase.Pausing ? Strings.Shell.Resume : Strings.Shell.Pause;
            _pause.Icon = status.Phase is EnginePhase.Paused or EnginePhase.Pausing ? IconName.Play : IconName.Pause;
            if (resize)
            {
                _page.PerformLayout();
            }

            PerformLayout();
            Invalidate();
        }

        public void Spin()
        {
            if (_status.Phase is EnginePhase.Scanning or EnginePhase.Enumerating or EnginePhase.Finishing)
            {
                Invalidate(new Rectangle(S(12), S(14), S(24), S(24)));
            }
        }

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
                KitPaint.DrawTextCentred(g, Strings.Scans.NoScan, type.Subtitle, t.Text, Width / 2, S(76));
                string last = _page.Data?.LastScan is DateTimeOffset when ? string.Format(CultureInfo.CurrentCulture, Strings.Scans.LastScan, UiFormat.RecentDate(when, _page.Services.Clock.UtcNow, inSentence: true)) : string.Empty;
                KitPaint.DrawTextCentred(g, last, type.Body, t.Text2, Width / 2, S(100));
                return;
            }

            string scope = _page.Services.Scans.ScopeText;
            string heading = s.Phase switch
            {
                EnginePhase.Paused or EnginePhase.Pausing => string.Format(CultureInfo.CurrentCulture, Strings.Scans.PausedScope, scope),
                EnginePhase.Enumerating => string.Format(CultureInfo.CurrentCulture, Strings.Scans.FindingIn, scope),
                _ => string.Format(CultureInfo.CurrentCulture, Strings.Scans.Scanning, scope),
            };
            float angle = (float)((Environment.TickCount64 - _start) % 1000 * 360 / 1000.0);
            if (s.Phase is EnginePhase.Paused or EnginePhase.Pausing)
            {
                IconRenderer.Draw(g, IconName.Pause, SF(16), SF(23), 16, DeviceDpi, t.Text2);
            }
            else
            {
                DataPaint.ProgressRing(g, new PointF(SF(25), SF(31)), SF(8), angle, DeviceDpi);
            }

            KitPaint.DrawText(g, heading, type.Subtitle, t.Text, S(46), S(31), _pause.Left - S(56));
            DateTimeOffset now = _page.Services.Clock.UtcNow;
            string started = UiFormat.TimeOfDay((now - s.Elapsed).ToLocalTime().DateTime);
            string sub = s.Trigger == RunTrigger.Scheduled
                ? string.Format(CultureInfo.CurrentCulture, Strings.Scans.SubLineSchedule, UiFormat.DepthName(s.Depth), started, _page.Data?.RunningPlan ?? string.Empty)
                : string.Format(CultureInfo.CurrentCulture, Strings.Scans.SubLineYou, UiFormat.DepthName(s.Depth), started);
            KitPaint.DrawText(g, sub, type.Body, t.Text2, S(16), S(59), Width - S(32));

            float barRight = Width - SF(72);
            DataPaint.ProgressBar(g, new RectangleF(SF(16), SF(84), barRight - SF(16), SF(8)), s.Fraction);
            KitPaint.DrawText(g, UiFormat.Percent(s.Fraction * 100), type.BodyStrong, t.Text, Width - S(64), S(89), S(48), TextFormatFlags.Right);

            int column = (Width - S(32)) / 5;
            string[] labels = [Strings.Scans.Files, Strings.Scans.DataRead, Strings.Scans.Speed, Strings.Scans.TimeLeft, Strings.Scans.FoundSoFar];
            string[] values =
            [
                string.Format(CultureInfo.CurrentCulture, Strings.Scans.OfTotal, UiFormat.Count(s.FilesDone), UiFormat.Count(s.Total)),
                UiFormat.Size(s.BytesRead),
                UiFormat.Rate(s.BytesPerSecond),
                s.Estimating || s.Remaining is null ? Strings.Scans.Estimating : UiFormat.About(s.Remaining.Value),
            ];
            for (int i = 0; i < labels.Length; i++)
            {
                int x = S(16) + (i * column);
                KitPaint.DrawText(g, labels[i], type.Caption, t.Text3, x, S(123), column - S(8));
                if (i < values.Length)
                {
                    KitPaint.DrawText(g, values[i], type.BodyStrong, t.Text, x, S(143), column - S(8));
                }
            }

            int fx = S(16) + (4 * column);
            foreach (Severity tier in new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low })
            {
                IconRenderer.DrawSeverity(g, tier, fx, SF(135), 16, DeviceDpi, t.ForSeverity(tier).Text, t.Surface);
                string count = UiFormat.Count(s.NewFindings[tier]);
                KitPaint.DrawText(g, count, type.BodyStrong, t.Text, fx + S(20), S(143), S(40));
                fx += S(20) + KitPaint.MeasureWidth(count, type.BodyStrong) + S(12);
            }

            if (s.InFlight.Count == 0)
            {
                return;
            }

            KitPaint.HorizontalLine(g, t.Stroke, S(16), Width - S(16), S(166), SF(1));
            KitPaint.DrawText(g, Strings.Scans.WorkingOn, type.CaptionStrong, t.Text3, S(16), S(186), Width - S(32));
            for (int i = 0; i < Math.Min(8, s.InFlight.Count); i++)
            {
                InFlightItem item = s.InFlight[i];
                int y = S(215 + (28 * i));
                IconName icon = item.Kind switch { MediaKind.Video => IconName.Video, MediaKind.Photo => IconName.Photo, _ => IconName.Music };
                IconRenderer.Draw(g, icon, SF(16), y - SF(8), 16, DeviceDpi, t.Text2);
                KitPaint.DrawText(g, Path.GetFileName(item.Path), type.Body, t.Text, S(44), y, (Width / 2) - S(60));
                KitPaint.DrawText(g, item.Stage, type.Body, t.Text2, S(500), y, Width - S(700));
                DataPaint.ProgressBar(g, new RectangleF(Width - SF(176), y - SF(2), SF(160), SF(4)), item.Fraction);
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
                UpdateStatus();
            }
        }
    }

    /// <summary>A finished run in the drawer: heading, numbers, alerts, errors and a link to its findings.</summary>
    private sealed class RunDetailView : KitControl
    {
        private readonly ScansPage _page;
        private readonly IconButton _close = new() { Icon = IconName.Close };
        private readonly KitButton _problems = new() { Kind = ButtonKind.Secondary, Icon = IconName.Issues, Text = Strings.Scans.ShowProblems };
        private readonly List<InfoBar> _alerts = [];

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

            if (Detail is { Errors: > 0 })
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
            RunInfo run = d.Run;
            DateTimeOffset now = _page.Services.Clock.UtcNow;
            KitPaint.DrawText(g, string.Format(CultureInfo.CurrentCulture, Strings.Scans.RunOf, run.Folders), type.Subtitle, t.Text, S(20), S(32), Width - S(80));
            string when = UiFormat.Facts(
                UiFormat.RecentDate(run.Started, now),
                run.Trigger == RunTrigger.Scheduled ? Strings.Scans.ScheduleBy : Strings.Scans.You,
                UiFormat.DepthName(run.Depth),
                run.Ended is DateTimeOffset ended && run.Status != RunStatus.Failed ? UiFormat.Duration(ended - run.Started) : null);
            KitPaint.DrawText(g, when, type.Body, t.Text2, S(20), S(58), Width - S(40));
            Severity? tier = ResultTier(run.Status);
            (Color fg, Color bg) = tier switch
            {
                Severity s when s != Severity.None => (t.ForSeverity(s).Text, t.ForSeverity(s).Pill),
                Severity => (t.Ok.Text, t.Ok.Pill),
                null => (t.Text2, t.NeutralPill),
            };
            BadgePainter.PaintPill(g, S(20), S(76), ResultText(run.Status), fg, bg, DeviceDpi);
            string[] labels = [Strings.Scans.Files, Strings.Scans.DataRead, Strings.Scans.ColNew, Strings.Scans.ColResolved];
            string[] values = [UiFormat.Count(run.Files), UiFormat.Size(d.BytesRead), UiFormat.Count(run.NewFindings), UiFormat.Count(run.Resolved)];
            int column = (Width - S(40)) / 4;
            for (int i = 0; i < labels.Length; i++)
            {
                KitPaint.DrawText(g, labels[i], type.Caption, t.Text3, S(20) + (i * column), S(128), column - S(8));
                KitPaint.DrawText(g, values[i], type.BodyStrong, t.Text, S(20) + (i * column), S(148), column - S(8));
            }

            KitPaint.HorizontalLine(g, t.Stroke, 0, Width, S(176), SF(1));
            if (d.Errors > 0)
            {
                int y = S(196) + (_alerts.Count * S(48)) + S(14);
                KitPaint.DrawText(g, string.Format(CultureInfo.CurrentCulture, Strings.Scans.ErrorsCount, UiFormat.Count(d.Errors)), type.Body, t.Critical.Text, S(20), y, Width - S(40));
            }
        }

        protected override void OnThemeChanged()
        {
            BackColor = Tokens.Surface;
            base.OnThemeChanged();
        }
    }
}
