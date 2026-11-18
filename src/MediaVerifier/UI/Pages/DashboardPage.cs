using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// The Dashboard (subplan 09, task UI-03): health, protection, scans, the four tier tiles, media rows,
/// recommendations and recent activity; the first-run and not-yet-scanned states. Geometry from
/// docs/design/dashboard-dark.svg.
/// </summary>
internal sealed class DashboardPage : PageBase
{
    private readonly HealthCard _health;
    private readonly ProtectionCard _protection;
    private readonly ScansCard _scans;
    private readonly TileCard[] _tiles;
    private readonly MediaCard _media;
    private readonly RecommendationsCard _recommendations;
    private readonly ActivityCard _activity;
    private readonly FirstRunView _firstRun;
    private readonly InfoBar _engineBar;
    private DashboardData? _data;

    public DashboardPage(UiServices services)
        : base(services)
    {
        // Drawn first, so first in Tab order too.
        _engineBar = new InfoBar { Tier = Severity.Critical, Text = Strings.Dashboard.EngineMissingBar, Action = Strings.Dashboard.OpenDownloadPage, Visible = false };
        _engineBar.ActionClicked += (_, _) => ShellActions.OpenUrl(ShellActions.ReleasesUrl);
        _health = new HealthCard(this);
        _protection = new ProtectionCard(this);
        _scans = new ScansCard(this);
        _tiles = [new TileCard(this, Severity.Critical), new TileCard(this, Severity.High), new TileCard(this, Severity.Medium), new TileCard(this, Severity.Low)];
        _media = new MediaCard(this);
        _recommendations = new RecommendationsCard(this);
        _activity = new ActivityCard(this);
        _firstRun = new FirstRunView(this) { Visible = false };
        Controls.AddRange([_engineBar, _health, _protection, _scans, .. _tiles, _media, _recommendations, _activity, _firstRun]);
        int tab = 0;
        foreach (Control control in Controls)
        {
            control.TabIndex = tab++;
        }

        // The first-run screen says "or drop folders anywhere in this window" (subplan 09): a dropped folder opens Add
        // folder. Child controls have no drop target of their own, so a drop anywhere on the page arrives here.
        AllowDrop = true;
        Services.ScanStarted += OnScanStarted;
    }

    public override PageId Id => PageId.Dashboard;

    internal DashboardData? Data => _data;

    internal DateTimeOffset Now => Services.Clock.UtcNow;

    public override int MeasureContentHeight(int width) =>
        S((_data is { HasFolders: false } ? 360 + 16 + 132 : 408 + 264) + (_engineBar.Visible ? 52 : 0));

    internal void OpenIssues(Severity? tier, MediaKind? kind) => Services.Navigate?.Invoke(PageId.Issues, new IssuesFilterRequest(tier, kind));

    /// <summary>The first folder of a drag from Explorer, without touching the disk (the drop is checked by Add folder).</summary>
    internal static string? DroppedFolder(IDataObject? data) =>
        data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths[0] : null;

    /// <summary>A folder dropped on the page opens Library's Add folder dialog with it.</summary>
    internal bool DropFolder(IDataObject? data)
    {
        if (DroppedFolder(data) is not string folder)
        {
            return false;
        }

        Services.Navigate?.Invoke(PageId.Library, new AddFolderRequest(folder));
        return true;
    }

    /// <summary>A recommendation's button (subplan 12, task R-12): fix all, review, verify, add a folder, schedule, help, continue.</summary>
    internal void RunRecommendation(RecommendationItem item)
    {
        string command = item.ActionId;
        if (command.StartsWith("fix:", StringComparison.Ordinal) && item.FileIds is { Count: > 0 } files)
        {
            UiTask.Run(FixAllAsync(files, command[4..]));
            return;
        }

        switch (command)
        {
            case "scan":
                if (FindForm() is Form owner)
                {
                    UiTask.Run(ScanLauncher.StartLibraryScanAsync(owner, Services, State.Settings.Scanning.DefaultDepth, null));
                }

                break;
            case "verify":
                UiTask.Run(VerifyAllAsync());
                break;
            case "add-folder":
                Services.Navigate?.Invoke(PageId.Library, new AddFolderRequest(null));
                break;
            case "schedule":
                // Create schedule creates one: Schedule handles the request (it only switched pages before).
                Services.Navigate?.Invoke(PageId.Schedule, new NewScheduleRequest());
                break;
            case "read-errors":
                ShellActions.OpenUrl(ShellActions.HelpUrl("GEN-UNREADABLE"));
                break;
            case "continue":
                // Continue means continue: it only opened Scans, where Continue had to be pressed again.
                UiTask.Run(ContinueInterruptedAsync());
                break;
            case not null when command.StartsWith("review:", StringComparison.Ordinal):
                // Review the files the recommendation counts: Issues filtered to its rule, not every problem.
                Services.Navigate?.Invoke(PageId.Issues, new IssuesFilterRequest(null, null, RuleId: command["review:".Length..]));
                break;
            default:
                Services.Navigate?.Invoke(PageId.Issues, null);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Services.ScanStarted -= OnScanStarted;
        }

        base.Dispose(disposing);
    }

    /// <summary>A scan started (from the header, say): Continue and Scan leave the list at once, not at the next load.</summary>
    private void OnScanStarted()
    {
        if (_data is not null && !IsDisposed)
        {
            _recommendations.Bind(EffectiveRecommendations(_data));
        }
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        drgevent.Effect = DroppedFolder(drgevent.Data) is null ? DragDropEffects.None : DragDropEffects.Link;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        _ = DropFolder(drgevent.Data);
    }

    private async Task FixAllAsync(IReadOnlyList<long> files, string actionId)
    {
        await Remediator.RunAsync(Services, this, files, actionId).ConfigureAwait(true);
        Reload();
    }

    /// <summary>The interrupted scan starts again with its own folders and depth, then Scans shows it.</summary>
    private async Task ContinueInterruptedAsync()
    {
        RunRecord? run = await Task.Run(Services.Database.Runs.LatestInterrupted).ConfigureAwait(true);
        if (run is not null && FindForm() is Form owner)
        {
            _ = await ScansPage.ContinueAsync(Services, owner, run.Id).ConfigureAwait(true);
        }

        Services.Navigate?.Invoke(PageId.Scans, null);
    }

    /// <summary>
    /// "Verify now" re-reads the overdue files of every folder (the recommendation counts them all). The run card read
    /// "Scanning Verify now": its scope is all folders.
    /// </summary>
    private async Task VerifyAllAsync()
    {
        List<long> roots = await Task.Run(() => Services.Database.Roots.List().Where(r => r.Enabled).Select(r => r.Id).ToList()).ConfigureAwait(true);
        if (FindForm() is Form owner)
        {
            await ScanLauncher.StartAsync(owner, Services, [.. roots.Select(id => new Core.Engine.ScanScope(id))], ScanDepth.Quick, Strings.Scans.AllFolders, new Core.Engine.ScanOptions { VerifyOnly = true }).ConfigureAwait(true);
        }
    }

    protected override object? LoadData() => Services.Data.LoadDashboard();

    protected override void ApplyData(object data)
    {
        _data = (DashboardData)data;
        bool firstRun = !_data.HasFolders;
        _firstRun.Visible = firstRun;
        foreach (Control control in new Control[] { _health, _protection, _scans, _media, _recommendations, _activity })
        {
            control.Visible = !firstRun;
        }

        foreach (TileCard tile in _tiles)
        {
            tile.Visible = !firstRun;
            tile.Bind(_data);
        }

        // Media engine missing or damaged (subplan 09): the bar above the top cards. It never showed.
        _engineBar.Visible = !Services.MediaEngine.ScanningAllowed;
        _media.Bind(_data);
        _recommendations.Bind(EffectiveRecommendations(_data));
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int width = Width;
        int y = 0;
        if (_engineBar.Visible)
        {
            _engineBar.Bounds = new Rectangle(0, 0, width, S(40));
            y = S(52);
        }

        if (_firstRun.Visible)
        {
            _firstRun.Bounds = new Rectangle(0, y, width, Math.Max(Height - y, S(360 + 16 + 132)));
            return;
        }

        int gap = S(12);
        int third = (width - (2 * gap)) / 3;
        _health.Bounds = new Rectangle(0, y, third, S(136));
        _protection.Bounds = new Rectangle(third + gap, y, third, S(136));
        _scans.Bounds = new Rectangle((2 * (third + gap)), y, width - (2 * (third + gap)), S(136));
        int quarter = (width - (3 * gap)) / 4;
        for (int i = 0; i < _tiles.Length; i++)
        {
            int x = i * (quarter + gap);
            _tiles[i].Bounds = new Rectangle(x, y + S(148), i == 3 ? width - x : quarter, S(88));
        }

        _media.Bounds = new Rectangle(0, y + S(248), width, S(148));
        int bottomTop = y + S(408);
        int bottomHeight = Math.Max(S(264), Height - bottomTop);
        int activityWidth = S(340);
        _recommendations.Bounds = new Rectangle(0, bottomTop, width - activityWidth - gap, bottomHeight);
        _activity.Bounds = new Rectangle(width - activityWidth, bottomTop, activityWidth, bottomHeight);
    }

    private IReadOnlyList<RecommendationItem> EffectiveRecommendations(DashboardData data)
    {
        // While a scan runs (here or a scheduled one in the background), Continue and Scan could only answer "A scan
        // is already running".
        bool scanning = Services.Scans.Status.IsActive || Services.BackgroundRunActive();
        if (data.HasFolders && !data.AnyScanned)
        {
            // Folders exist but nothing has been scanned: one recommendation (subplan 09). Its time comes from the Add
            // folder count; without one the line is left out rather than reading "takes about —".
            string detail = data.FirstScanEstimate is TimeSpan time ? string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.NotScannedDetail, UiFormat.Duration(time)) : string.Empty;
            return scanning ? [] : [new RecommendationItem(Severity.Low, Strings.Dashboard.NotScannedTitle, detail, Strings.Recommendations.Scan, "scan")];
        }

        return scanning ? [.. data.Recommendations.Where(r => r.ActionId is not ("continue" or "scan"))] : data.Recommendations;
    }

    /// <summary>A card on this page: painted content plus the shared helpers.</summary>
    private abstract class DashCard : Card
    {
        protected DashCard(DashboardPage page, string? title)
        {
            Page = page;
            Title = title;
        }

        protected DashboardPage Page { get; }

        protected DashboardData? D => Page.Data;

        protected void Write(Graphics g, string text, Font font, Color color, int x, int y, int maxWidth = 0, bool right = false)
        {
            int width = maxWidth > 0 ? maxWidth : KitPaint.MeasureWidth(text, font) + 2;
            KitPaint.DrawText(g, text, font, color, right ? x - width : x, y, width, right ? TextFormatFlags.Right : TextFormatFlags.Default);
        }
    }

    /// <summary>Health has no action of its own (subplan 09): a click anywhere on it used to open Issues, unannounced.</summary>
    private sealed class HealthCard(DashboardPage page) : DashCard(page, Strings.Dashboard.Health)
    {
        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            DashboardData? d = D;
            float r = SF(40);
            PointF centre = new(S(64), S(80));
            DataPaint.Donut(g, centre, r, SF(10), d is { AnyScanned: true } ? d.HealthyPercent / 100 : 0, t.Ok.Fill);
            IconRenderer.DrawSeverity(g, Severity.None, centre.X - SF(10), centre.Y - SF(10), 20, DeviceDpi, t.Ok.Text, t.Surface);
            string metric = d is { AnyScanned: true } ? UiFormat.HealthPercent(d.HealthyPercent) : Strings.Dash;
            Write(g, metric, Type.Metric, t.Text, S(124), S(62));
            Write(g, Strings.Dashboard.Healthy, Type.Body, t.Text2, S(124) + KitPaint.MeasureWidth(metric, Type.Metric) + S(8), S(68));
            if (d is { AnyScanned: true })
            {
                // GDI text runs about 2 % wider than the references' browser text: let these lines use 4 px of the padding.
                int max = Width - S(136);
                Write(g, string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.HealthyLine, UiFormat.Count(d.FilesHealthy), UiFormat.Count(d.FilesScanned)), Type.Caption, t.Text3, S(124), S(92), max);
                if (d.FilesNeedingAttention > 0 && Width >= S(320))
                {
                    string attention = d.FilesNeedingAttention == 1 ? Strings.Dashboard.NeedAttentionOne : string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.NeedAttention, UiFormat.Count(d.FilesNeedingAttention));
                    Write(g, attention, Type.Caption, t.Text3, S(124), S(110), max);
                }
            }
        }
    }

    private sealed class ProtectionCard(DashboardPage page) : DashCard(page, Strings.Dashboard.Protection)
    {
        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            DashboardData? d = D;
            PointF centre = new(S(64), S(80));
            DataPaint.Donut(g, centre, SF(40), SF(10), d is { FilesWithBaseline: > 0 } ? d.ProtectedPercent / 100 : 0, t.Accent);
            IconRenderer.Draw(g, IconName.ShieldCheck, centre.X - SF(10), centre.Y - SF(10), 20, DeviceDpi, t.Accent);
            string metric = d is { FilesWithBaseline: > 0 } ? UiFormat.Percent(d.ProtectedPercent) : Strings.Dash;
            Write(g, metric, Type.Metric, t.Text, S(124), S(62));
            Write(g, Strings.Dashboard.Verified, Type.Body, t.Text2, S(124) + KitPaint.MeasureWidth(metric, Type.Metric) + S(8), S(68));
            if (d is { FilesWithBaseline: > 0 })
            {
                // GDI text runs about 2 % wider than the references' browser text: let these lines use 4 px of the padding.
                int max = Width - S(136);
                Write(g, string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.VerifiedLine, d.ScrubDays), Type.Caption, t.Text3, S(124), S(92), max);
                if (d.Overdue > 0 && Width >= S(320))
                {
                    string overdue = d.Overdue == 1 ? Strings.Dashboard.OverdueOne : string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.Overdue, UiFormat.Count(d.Overdue));
                    Write(g, overdue, Type.Caption, t.Text3, S(124), S(110), max);
                }
            }
        }
    }

    private sealed class ScansCard(DashboardPage page) : DashCard(page, Strings.Dashboard.Scans)
    {
        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            DashboardData? d = D;
            int valueX = S(64);
            int max = Width - valueX - S(16);
            Write(g, Strings.Dashboard.Last, Type.Caption, t.Text3, S(16), S(48));
            if (d?.LastRun is RunInfo run)
            {
                string when = UiFormat.RecentDate(run.Ended ?? run.Started, Page.Now);
                string trigger = run.Trigger == RunTrigger.Scheduled ? Strings.Dashboard.Scheduled : Strings.Dashboard.Manual;
                string line = Width < S(320) ? UiFormat.Facts(when, UiFormat.DepthName(run.Depth)) : UiFormat.Facts(when, trigger, UiFormat.DepthName(run.Depth));
                Write(g, line, Type.Body, t.Text, valueX, S(48), max);
                string detail = run.NewFindings == 0 && run.Resolved == 0
                    ? Strings.Dashboard.NoNewProblems
                    : string.Format(CultureInfo.CurrentCulture, run.NewFindings == 1 ? Strings.Dashboard.NewOneAndResolved : Strings.Dashboard.NewAndResolved, UiFormat.Count(run.NewFindings), UiFormat.Count(run.Resolved));
                Write(g, detail, Type.Caption, t.Text2, valueX, S(68), max);
            }
            else
            {
                Write(g, Strings.Dash, Type.Body, t.Text, valueX, S(48));
            }

            KitPaint.HorizontalLine(g, t.Stroke, S(16), Width - S(16), S(86), SF(1));
            Write(g, Strings.Dashboard.Next, Type.Caption, t.Text3, S(16), S(108));
            string next = d?.NextRun is NextRunInfo plan ? UiFormat.Facts(UiFormat.RecentDate(plan.When, Page.Now), plan.PlanName) : Strings.Dashboard.NoSchedule;
            Write(g, next, Type.Body, t.Text, valueX, S(108), max);
        }
    }

    private sealed class TileCard : DashCard
    {
        private readonly Severity _tier;
        private string _count = Strings.Dash;
        private string? _change;

        public TileCard(DashboardPage page, Severity tier)
            : base(page, null)
        {
            _tier = tier;
            Clickable = true;
            Tile = true;
            AccessibleName = UiFormat.TierName(tier);
        }

        /// <summary>The open count and its change since the last scan; dashes before any scan (it read 0).</summary>
        public void Bind(DashboardData data)
        {
            _count = data.AnyScanned ? UiFormat.Count(data.Open[_tier]) : Strings.Dash;
            _change = null;
            if (data.AnyScanned && data.OpenAtLastScan is SeverityCounts before)
            {
                int delta = data.Open[_tier] - before[_tier];
                _change = delta > 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.SinceLastScanUp, UiFormat.Count(delta))
                    : delta < 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.SinceLastScanDown, UiFormat.Count(-delta)) : Strings.Dashboard.NoChange;
            }

            AccessibleDescription = _change is null ? _count : UiFormat.Facts(_count, _change);
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            Color background = IsHovered ? t.Hover : t.Surface;
            IconRenderer.DrawSeverity(g, _tier, S(16), S(16), 16, DeviceDpi, t.ForSeverity(_tier).Text, background);
            Write(g, UiFormat.TierName(_tier), Type.Body, t.Text2, S(40), S(24));
            Write(g, _count, Type.Metric, t.Text, S(16), S(60));
            if (_change is not null)
            {
                Write(g, _change, Type.Caption, t.Text3, Width - S(16), S(66), right: true);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Page.OpenIssues(_tier, null);
        }
    }

    /// <summary>
    /// By media type: three rows, each opening Issues on its kind, and the bar's segments on their tier too (subplan 09).
    /// The rows have the hover fill and the hand cursor, Tab reaches the card and Up, Down and Enter choose a row, and a
    /// segment's tooltip reads "{tier}: {count}" (subplan 08, Severity bar). The rows reacted to the mouse only, with
    /// nothing to show they could.
    /// </summary>
    private sealed class MediaCard : DashCard
    {
        private static readonly (MediaKind Kind, IconName Icon, string Name)[] Kinds = [(MediaKind.Photo, IconName.Photo, Strings.Kinds.Photos), (MediaKind.Video, IconName.Video, Strings.Kinds.Videos), (MediaKind.Audio, IconName.Music, Strings.Kinds.Music)];
        private readonly RectangleF[] _segments = new RectangleF[15];
        private readonly long[][] _counts = [new long[5], new long[5], new long[5]];
        private readonly string[] _facts = [Strings.Dash, Strings.Dash, Strings.Dash];
        private readonly string[] _problems = [Strings.Dash, Strings.Dash, Strings.Dash];
        private bool _bars;
        private int _hoverRow = -1;
        private int _hoverSegment = -1;
        private int _focusRow;

        public MediaCard(DashboardPage page)
            : base(page, Strings.Dashboard.ByMediaType)
        {
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = Strings.Dashboard.ByMediaType;
        }

        /// <summary>The rows' texts and bars. Before any scan they read dashes and draw no bar (they claimed "No problems").</summary>
        public void Bind(DashboardData data)
        {
            _bars = data.AnyScanned;
            for (int i = 0; i < Kinds.Length; i++)
            {
                KindRow? row = data.Kinds.FirstOrDefault(k => k.Kind == Kinds[i].Kind);
                long[] counts = _counts[i];
                if (row is null || !data.AnyScanned)
                {
                    _facts[i] = Strings.Dash;
                    _problems[i] = Strings.Dash;
                    Array.Clear(counts);
                    continue;
                }

                _facts[i] = string.Format(CultureInfo.CurrentCulture, row.Files == 1 ? Strings.Dashboard.FilesAndSizeOne : Strings.Dashboard.FilesAndSize, UiFormat.Count(row.Files), UiFormat.Size(row.Bytes));
                _problems[i] = row.Problems == 0 ? Strings.Dashboard.NoProblems : row.Problems == 1 ? Strings.Dashboard.OneProblem : string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.Problems, UiFormat.Count(row.Problems));
                counts[0] = row.Tiers.Critical;
                counts[1] = row.Tiers.High;
                counts[2] = row.Tiers.Medium;
                counts[3] = row.Tiers.Low;
                counts[4] = row.OkFiles;
            }

            Invalidate();
        }

        protected override string? AccessibleValue => _focusRow is >= 0 and < 3 ? UiFormat.Facts(Kinds[_focusRow].Name, _facts[_focusRow], _problems[_focusRow]) : null;

        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            int barLeft = Math.Max(S(300), (int)(Width * 0.35));
            int barRight = Width - S(152);
            for (int i = 0; i < Kinds.Length; i++)
            {
                int y = S(54 + (i * 36));
                Rectangle band = RowBounds(i);
                if (i == _hoverRow)
                {
                    KitPaint.FillRoundedRect(g, band, SF(4), t.Hover);
                }

                if (i == _focusRow && KeyboardFocused)
                {
                    KitPaint.DrawFocusRing(g, band, SF(4), DeviceDpi);
                }

                IconRenderer.Draw(g, Kinds[i].Icon, S(16), y - SF(8), 16, DeviceDpi, t.Text2);
                Write(g, Kinds[i].Name, Type.BodyStrong, t.Text, S(44), y);
                Write(g, _facts[i], Type.Body, t.Text2, S(124), y, barLeft - S(124) - S(16));
                if (_bars)
                {
                    DataPaint.SeverityBar(g, new RectangleF(barLeft, y - SF(4), barRight - barLeft, SF(8)), _counts[i], _segments.AsSpan(i * 5, 5), DeviceDpi);
                }

                Write(g, _problems[i], Type.Body, t.Text2, Width - S(16), y, right: true);
            }
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Up or Keys.Down:
                    _focusRow = Math.Clamp(_focusRow + (e.KeyCode == Keys.Up ? -1 : 1), 0, Kinds.Length - 1);
                    e.Handled = true;
                    AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                    Invalidate();
                    break;
                case Keys.Enter or Keys.Space when D is not null:
                    e.Handled = true;
                    Page.OpenIssues(null, Kinds[_focusRow].Kind);
                    break;
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row = RowAt(e.Location);
            int segment = row >= 0 ? SegmentAt(row, e.X) : -1;
            Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
            if (row != _hoverRow)
            {
                _hoverRow = row;
                Invalidate();
            }

            if (segment != _hoverSegment)
            {
                _hoverSegment = segment;
                string tip = segment >= 0 ? UiFormat.TierName(DataPaint.TierSeverity(segment % 5)) + ": " + UiFormat.Count(_counts[segment / 5][segment % 5]) : string.Empty;
                KitToolTip.Hide();
                KitToolTip.Set(this, tip);
                if (tip.Length > 0)
                {
                    // The tooltip waits for the pointer to enter a control; moving onto another segment starts that wait again.
                    OnMouseEnter(EventArgs.Empty);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverSegment = -1;
            KitToolTip.Set(this, string.Empty);
            if (_hoverRow >= 0)
            {
                _hoverRow = -1;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int row = RowAt(e.Location);
            if (e.Button != MouseButtons.Left || D is null || row < 0)
            {
                return;
            }

            _focusRow = row;
            int segment = SegmentAt(row, e.X);
            Page.OpenIssues(segment >= 0 && segment % 5 < 4 ? DataPaint.TierSeverity(segment % 5) : null, Kinds[row].Kind);
        }

        /// <summary>A row's band: the full width inside the card's padding, 32 px around its text line.</summary>
        private Rectangle RowBounds(int row) => new(S(8), S(54 + (row * 36)) - S(16), Width - S(16), S(32));

        private int RowAt(Point point)
        {
            for (int i = 0; i < Kinds.Length; i++)
            {
                if (RowBounds(i).Contains(point))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The bar segment under <paramref name="x"/> in <paramref name="row"/> (index into the segments), or -1.</summary>
        private int SegmentAt(int row, int x)
        {
            if (!_bars)
            {
                return -1;
            }

            for (int s = 0; s < 5; s++)
            {
                RectangleF segment = _segments[(row * 5) + s];
                if (!segment.IsEmpty && x >= segment.Left && x < segment.Right + S(2))
                {
                    return (row * 5) + s;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Up to four recommendations with their buttons. The buttons are kept and updated in place: rebuilding them at
    /// each load (after Fix all, at the end of each scan) took the keyboard focus away.
    /// </summary>
    private sealed class RecommendationsCard : DashCard
    {
        private const int MaxItems = 4;
        private readonly KitButton[] _buttons = new KitButton[MaxItems];
        private IReadOnlyList<RecommendationItem> _items = [];

        public RecommendationsCard(DashboardPage page)
            : base(page, Strings.Dashboard.Recommended)
        {
            for (int i = 0; i < MaxItems; i++)
            {
                int index = i;
                _buttons[i] = new KitButton { Kind = i == 0 ? ButtonKind.Primary : ButtonKind.Secondary, Compact = true, TabIndex = i, Visible = false };
                _buttons[i].Click += (_, _) =>
                {
                    if (index < _items.Count)
                    {
                        Page.RunRecommendation(_items[index]);
                    }
                };
            }

            Controls.AddRange(_buttons);
        }

        public void Bind(IReadOnlyList<RecommendationItem> items)
        {
            _items = [.. items.Take(MaxItems)];
            for (int i = 0; i < MaxItems; i++)
            {
                bool shown = i < _items.Count;
                if (shown)
                {
                    _buttons[i].Text = _items[i].Button;
                }

                _buttons[i].Visible = shown;
            }

            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            for (int i = 0; i < _buttons.Length; i++)
            {
                int width = S(_buttons[i].PreferredLogicalWidth);
                _buttons[i].Bounds = new Rectangle(Width - S(16) - width, S(49 + (i * 52)), width, S(28));
            }
        }

        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            if (_items.Count == 0)
            {
                if (D is not null)
                {
                    // Nothing while the first load runs: "Nothing needs doing" before anything was read was not true.
                    KitPaint.DrawTextCentred(g, Strings.Dashboard.NothingToDo, Type.Body, t.Text2, Width / 2, Height / 2);
                }

                return;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                RecommendationItem item = _items[i];
                int y = S(54 + (i * 52));
                int buttonLeft = _buttons[i].Left;
                IconRenderer.DrawSeverity(g, item.Tier, S(16), y - SF(8), 16, DeviceDpi, t.ForSeverity(item.Tier).Text, t.Surface);
                Write(g, item.Title, Type.BodyStrong, t.Text, S(44), y, buttonLeft - S(60));
                Write(g, item.Detail, Type.Caption, t.Text2, S(44), y + S(19), buttonLeft - S(60));

                if (i < _items.Count - 1)
                {
                    KitPaint.HorizontalLine(g, t.Stroke, S(16), Width - S(16), y + S(35), SF(1));
                }
            }
        }
    }

    private sealed class ActivityCard(DashboardPage page) : DashCard(page, Strings.Dashboard.Activity)
    {
        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            DashboardData? d = D;
            if (d is null)
            {
                return;
            }

            int count = Math.Min(4, d.Activity.Count);
            float lineX = S(23) + 0.5f;
            float r = SF(4);
            for (int i = 0; i < count; i++)
            {
                int y = S(48 + (i * 30));
                if (i < count - 1)
                {
                    KitPaint.VerticalLine(g, t.Stroke, lineX - 0.5f, y + S(4), y + S(26), SF(1));
                }

                System.Drawing.Drawing2D.SmoothingMode previous = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(GdiCache.Brush(t.Surface), lineX - r, y - r, 2 * r, 2 * r);
                g.DrawEllipse(GdiCache.Pen(t.StrokeStrong, SF(1.5f)), lineX - r, y - r, 2 * r, 2 * r);
                g.SmoothingMode = previous;
                string time = UiFormat.RecentDate(d.Activity[i].When, Page.Now) is string when && when.StartsWith(Strings.Dates.Today, StringComparison.Ordinal)
                    ? UiFormat.TimeOfDay(d.Activity[i].When.ToLocalTime().DateTime)
                    : UiFormat.RecentDay(d.Activity[i].When, Page.Now);
                int timeWidth = KitPaint.MeasureWidth(time, Type.Caption);
                Write(g, d.Activity[i].Text, Type.Body, t.Text, S(40), y, Width - S(40) - timeWidth - S(28));
                Write(g, time, Type.Caption, t.Text3, Width - S(16), y, right: true);
            }

            if (d.OpenProblemsHistory.Count >= 2)
            {
                Write(g, Strings.Dashboard.Sparkline, Type.Caption, t.Text3, S(16), S(186));
                double[] values = [.. d.OpenProblemsHistory];
                DataPaint.Sparkline(g, new RectangleF(S(16), S(200), Width - S(32), Height - S(200) - S(16)), values, DeviceDpi);
            }
        }
    }

    /// <summary>Before any folder exists (reference first-run).</summary>
    private sealed class FirstRunView : KitControl
    {
        private readonly DashboardPage _page;
        private readonly KitButton _add;

        public FirstRunView(DashboardPage page)
        {
            _page = page;
            _add = new KitButton { Text = Strings.Dashboard.AddFolder, Kind = ButtonKind.Primary, Icon = IconName.FolderAdd };
            _add.Click += (_, _) => page.Services.Navigate?.Invoke(PageId.Library, new AddFolderRequest(null));
            Controls.Add(_add);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int width = S(_add.PreferredLogicalWidth);
            _add.Bounds = new Rectangle((Width - width) / 2, S(246), width, S(32));
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _page.IsActive)
            {
                KitFocus.Move(_add);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Bg);
            Rectangle drop = new(0, 0, Width, S(360));
            using (Pen dashed = new(t.StrokeStrong, SF(1.5f)) { DashPattern = [4, 3] })
            {
                System.Drawing.Drawing2D.GraphicsPath path = new();
                IconGeometry.AddRoundedRect(path, RectangleF.Inflate(drop, -SF(1), -SF(1)), SF(8));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawPath(dashed, path);
                path.Dispose();
            }

            int centre = Width / 2;
            IconRenderer.Draw(g, IconName.FolderAdd, centre - SF(24), S(72), 48, DeviceDpi, t.Text3);
            KitPaint.DrawTextCentred(g, Strings.Dashboard.FirstRunHeadline, Type.Subtitle, t.Text, centre, S(152));
            KitPaint.DrawTextCentred(g, Strings.Dashboard.FirstRunLine1, Type.Body, t.Text2, centre, S(186));
            KitPaint.DrawTextCentred(g, Strings.Dashboard.FirstRunLine2, Type.Body, t.Text2, centre, S(208));
            KitPaint.DrawTextCentred(g, Strings.Dashboard.DropCaption, Type.Caption, t.Text3, centre, S(302));
            (string Title, string Text)[] steps = [(Strings.Dashboard.Step1Title, Strings.Dashboard.Step1Text), (Strings.Dashboard.Step2Title, Strings.Dashboard.Step2Text), (Strings.Dashboard.Step3Title, Strings.Dashboard.Step3Text)];
            int gap = S(12);
            int cardWidth = (Width - (2 * gap)) / 3;
            for (int i = 0; i < steps.Length; i++)
            {
                Rectangle card = new(i * (cardWidth + gap), S(376), cardWidth, S(132));
                Card.Draw(g, card, null, VisualState.Rest, false, DeviceDpi);
                float cx = card.X + SF(28);
                float cy = card.Y + SF(32);
                float r = SF(12);
                g.FillEllipse(GdiCache.Brush(i == 0 ? t.Selected : t.Surface), cx - r, cy - r, 2 * r, 2 * r);
                g.DrawEllipse(GdiCache.Pen(i == 0 ? t.Accent : t.Stroke, SF(1)), cx - r, cy - r, 2 * r, 2 * r);
                KitPaint.DrawTextCentred(g, (i + 1).ToString(CultureInfo.CurrentCulture), Type.BodyStrong, t.Text, (int)cx, (int)cy);
                KitPaint.DrawText(g, steps[i].Title, Type.BodyStrong, t.Text, card.X + S(52), card.Y + S(32), card.Width - S(68));
                Rectangle body = new(card.X + S(16), card.Y + S(56), card.Width - S(32), card.Height - S(64));
                TextRenderer.DrawText(g, steps[i].Text, Type.Body, body, t.Text2, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
    }
}

/// <summary>Opens Issues with exactly this filter: the other filters go back to all (open findings).</summary>
internal sealed record IssuesFilterRequest(Severity? Tier, MediaKind? Kind, long? FirstRun = null, string? RuleId = null);

/// <summary>Opens Issues with one finding's file in the drawer (a notification's link, subplan 13).</summary>
internal sealed record IssueFindingRequest(long FindingId);

/// <summary>
/// The Dashboard's "Create schedule" (subplan 13: it adds the first two templates): a request for Schedule to create a
/// schedule, where only switching to the page left New schedule to be pressed again.
/// </summary>
internal sealed record NewScheduleRequest;
