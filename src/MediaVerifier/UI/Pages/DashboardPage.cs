using MediaVerifier.App;
using MediaVerifier.Core.Model;
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
        _health = new HealthCard(this);
        _protection = new ProtectionCard(this);
        _scans = new ScansCard(this);
        _tiles = [new TileCard(this, Severity.Critical), new TileCard(this, Severity.High), new TileCard(this, Severity.Medium), new TileCard(this, Severity.Low)];
        _media = new MediaCard(this);
        _recommendations = new RecommendationsCard(this);
        _activity = new ActivityCard(this);
        _firstRun = new FirstRunView(this) { Visible = false };
        _engineBar = new InfoBar { Tier = Severity.Critical, Text = Strings.Dashboard.EngineMissingBar, Action = Strings.Dashboard.OpenDownloadPage, Visible = false };
        Controls.AddRange([_health, _protection, _scans, .. _tiles, _media, _recommendations, _activity, _firstRun, _engineBar]);
        int tab = 0;
        foreach (Control control in Controls)
        {
            control.TabIndex = tab++;
        }
    }

    public override PageId Id => PageId.Dashboard;

    internal DashboardData? Data => _data;

    internal DateTimeOffset Now => Services.Clock.UtcNow;

    public override int MeasureContentHeight(int width) => S(_data is { HasFolders: false } ? 360 + 16 + 132 : 408 + 264);

    internal void OpenIssues(Severity? tier, MediaKind? kind) => Services.Navigate?.Invoke(PageId.Issues, new IssuesFilterRequest(tier, kind));

    /// <summary>A recommendation's button (subplan 12, task R-12): fix all, review, verify, add a folder, schedule, help, continue.</summary>
    internal void RunRecommendation(RecommendationItem item)
    {
        string command = item.ActionId;
        if (command.StartsWith("fix:", StringComparison.Ordinal) && item.FileIds is { Count: > 0 } files)
        {
            _ = FixAllAsync(files, command[4..]);
            return;
        }

        switch (command)
        {
            case "scan":
                if (FindForm() is Form owner)
                {
                    _ = ScanLauncher.StartLibraryScanAsync(owner, Services, State.Settings.Scanning.DefaultDepth, null);
                }

                break;
            case "verify":
                _ = VerifyAllAsync();
                break;
            case "add-folder":
                Services.Navigate?.Invoke(PageId.Library, new AddFolderRequest(null));
                break;
            case "schedule":
                Services.Navigate?.Invoke(PageId.Schedule, null);
                break;
            case "read-errors":
                ShellActions.OpenUrl(ShellActions.HelpUrl("GEN-UNREADABLE"));
                break;
            case "continue":
                Services.Navigate?.Invoke(PageId.Scans, null);
                break;
            default:
                Services.Navigate?.Invoke(PageId.Issues, null);
                break;
        }
    }

    private async Task FixAllAsync(IReadOnlyList<long> files, string actionId)
    {
        await Remediator.RunAsync(Services, this, files, actionId).ConfigureAwait(true);
        Reload();
    }

    private async Task VerifyAllAsync()
    {
        List<long> roots = await Task.Run(() => Services.Database.Roots.List().Select(r => r.Id).ToList()).ConfigureAwait(true);
        await Services.StartScanAsync([.. roots.Select(id => new Core.Engine.ScanScope(id))], Core.Model.ScanDepth.Quick, Strings.Recommendations.VerifyNow, new Core.Engine.ScanOptions { VerifyOnly = true }).ConfigureAwait(true);
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
        }

        _engineBar.Visible = _data.EngineMissing;
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
        if (data.HasFolders && !data.AnyScanned)
        {
            // Folders exist but nothing has been scanned: one recommendation (subplan 09).
            return [new RecommendationItem(Severity.Low, Strings.Dashboard.NotScannedTitle, string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.NotScannedDetail, Strings.Dash), Strings.Recommendations.Scan, "scan")];
        }

        return data.Recommendations;
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
                Write(g, string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.HealthyLine, UiFormat.Count(d.FilesHealthy), UiFormat.Count(d.FilesScanned)), Type.Caption, t.Text3, S(124), S(92), max);
                if (d.FilesNeedingAttention > 0 && Width >= S(320))
                {
                    string attention = d.FilesNeedingAttention == 1 ? Strings.Dashboard.NeedAttentionOne : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.NeedAttention, UiFormat.Count(d.FilesNeedingAttention));
                    Write(g, attention, Type.Caption, t.Text3, S(124), S(110), max);
                }
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Page.OpenIssues(null, null);
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
                Write(g, string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.VerifiedLine, d.ScrubDays), Type.Caption, t.Text3, S(124), S(92), max);
                if (d.Overdue > 0 && Width >= S(320))
                {
                    string overdue = d.Overdue == 1 ? Strings.Dashboard.OverdueOne : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.Overdue, UiFormat.Count(d.Overdue));
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
                string trigger = run.Trigger == Core.Storage.RunTrigger.Scheduled ? Strings.Dashboard.Scheduled : Strings.Dashboard.Manual;
                string line = Width < S(320) ? UiFormat.Facts(when, UiFormat.DepthName(run.Depth)) : UiFormat.Facts(when, trigger, UiFormat.DepthName(run.Depth));
                Write(g, line, Type.Body, t.Text, valueX, S(48), max);
                string detail = run.NewFindings == 0 && run.Resolved == 0
                    ? Strings.Dashboard.NoNewProblems
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, run.NewFindings == 1 ? Strings.Dashboard.NewOneAndResolved : Strings.Dashboard.NewAndResolved, UiFormat.Count(run.NewFindings), UiFormat.Count(run.Resolved));
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

        public TileCard(DashboardPage page, Severity tier)
            : base(page, null)
        {
            _tier = tier;
            Clickable = true;
            Tile = true;
            AccessibleName = UiFormat.TierName(tier);
        }

        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            DashboardData? d = D;
            Color background = IsHovered ? t.Hover : t.Surface;
            IconRenderer.DrawSeverity(g, _tier, S(16), S(16), 16, DeviceDpi, t.ForSeverity(_tier).Text, background);
            Write(g, UiFormat.TierName(_tier), Type.Body, t.Text2, S(40), S(24));
            string count = d is null ? Strings.Dash : UiFormat.Count(d.Open[_tier]);
            Write(g, count, Type.Metric, t.Text, S(16), S(60));
            if (d?.OpenAtLastScan is Core.Storage.SeverityCounts before)
            {
                int delta = d.Open[_tier] - before[_tier];
                string text = delta > 0
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.SinceLastScanUp, UiFormat.Count(delta))
                    : delta < 0 ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.SinceLastScanDown, UiFormat.Count(-delta)) : Strings.Dashboard.NoChange;
                Write(g, text, Type.Caption, t.Text3, Width - S(16), S(66), right: true);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Page.OpenIssues(_tier, null);
        }
    }

    private sealed class MediaCard : DashCard
    {
        private readonly RectangleF[] _segments = new RectangleF[15];

        public MediaCard(DashboardPage page)
            : base(page, Strings.Dashboard.ByMediaType)
        {
        }

        protected override void PaintContent(Graphics g)
        {
            ThemeTokens t = Tokens;
            DashboardData? d = D;
            (MediaKind Kind, IconName Icon, string Name)[] kinds = [(MediaKind.Photo, IconName.Photo, Strings.Kinds.Photos), (MediaKind.Video, IconName.Video, Strings.Kinds.Videos), (MediaKind.Audio, IconName.Music, Strings.Kinds.Music)];
            int barLeft = Math.Max(S(300), (int)(Width * 0.35));
            int barRight = Width - S(152);
            for (int i = 0; i < kinds.Length; i++)
            {
                int y = S(54 + (i * 36));
                KindRow? row = d?.Kinds.FirstOrDefault(k => k.Kind == kinds[i].Kind);
                IconRenderer.Draw(g, kinds[i].Icon, S(16), y - SF(8), 16, DeviceDpi, t.Text2);
                Write(g, kinds[i].Name, Type.BodyStrong, t.Text, S(44), y);
                string facts = row is null ? Strings.Dash : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.FilesAndSize, UiFormat.Count(row.Files), UiFormat.Size(row.Bytes));
                Write(g, facts, Type.Body, t.Text2, S(124), y, barLeft - S(124) - S(16));
                if (row is not null)
                {
                    Span<RectangleF> segments = _segments.AsSpan(i * 5, 5);
                    DataPaint.SeverityBar(g, new RectangleF(barLeft, y - SF(4), barRight - barLeft, SF(8)), [row.Tiers.Critical, row.Tiers.High, row.Tiers.Medium, row.Tiers.Low, row.OkFiles], segments, DeviceDpi);
                    string problems = row.Problems == 0 ? Strings.Dashboard.NoProblems : row.Problems == 1 ? Strings.Dashboard.OneProblem : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Dashboard.Problems, UiFormat.Count(row.Problems));
                    Write(g, problems, Type.Body, t.Text2, Width - S(16), y, right: true);
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || D is null)
            {
                return;
            }

            MediaKind[] kinds = [MediaKind.Photo, MediaKind.Video, MediaKind.Audio];
            for (int i = 0; i < 3; i++)
            {
                int y = S(54 + (i * 36));
                if (Math.Abs(e.Y - y) > S(16))
                {
                    continue;
                }

                Severity? tier = null;
                for (int s = 0; s < 4; s++)
                {
                    RectangleF segment = _segments[(i * 5) + s];
                    if (!segment.IsEmpty && e.X >= segment.Left && e.X < segment.Right + S(2))
                    {
                        tier = DataPaint.TierSeverity(s);
                    }
                }

                Page.OpenIssues(tier, kinds[i]);
                return;
            }
        }
    }

    private sealed class RecommendationsCard : DashCard
    {
        private readonly List<KitButton> _buttons = [];
        private IReadOnlyList<RecommendationItem> _items = [];

        public RecommendationsCard(DashboardPage page)
            : base(page, Strings.Dashboard.Recommended)
        {
        }

        public void Bind(IReadOnlyList<RecommendationItem> items)
        {
            _items = items.Take(4).ToList();
            foreach (KitButton old in _buttons)
            {
                Controls.Remove(old);
                old.Dispose();
            }

            _buttons.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                RecommendationItem item = _items[i];
                KitButton button = new() { Text = item.Button, Kind = i == 0 ? ButtonKind.Primary : ButtonKind.Secondary, Compact = true, TabIndex = i };
                button.Click += (_, _) => Page.RunRecommendation(item);
                _buttons.Add(button);
                Controls.Add(button);
            }

            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            for (int i = 0; i < _buttons.Count; i++)
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
                KitPaint.DrawTextCentred(g, Strings.Dashboard.NothingToDo, Type.Body, t.Text2, Width / 2, Height / 2);
                return;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                RecommendationItem item = _items[i];
                int y = S(54 + (i * 52));
                int buttonLeft = i < _buttons.Count ? _buttons[i].Left : Width - S(16);
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
            _add.Click += (_, _) => page.Services.Navigate?.Invoke(PageId.Library, LibraryRequest.AddFolder);
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
                _add.Focus();
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
                KitPaint.DrawTextCentred(g, (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), Type.BodyStrong, t.Text, (int)cx, (int)cy);
                KitPaint.DrawText(g, steps[i].Title, Type.BodyStrong, t.Text, card.X + S(52), card.Y + S(32), card.Width - S(68));
                Rectangle body = new(card.X + S(16), card.Y + S(56), card.Width - S(32), card.Height - S(64));
                TextRenderer.DrawText(g, steps[i].Text, Type.Body, body, t.Text2, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
    }
}

/// <summary>Navigation request to Issues with a tier and kind filter.</summary>
/// <summary>Opens Issues filtered by tier and kind, or to the findings first raised in one run.</summary>
internal sealed record IssuesFilterRequest(Severity? Tier, MediaKind? Kind, long? FirstRun = null);

/// <summary>Navigation requests to the Library page.</summary>
internal enum LibraryRequest
{
    AddFolder,
}
