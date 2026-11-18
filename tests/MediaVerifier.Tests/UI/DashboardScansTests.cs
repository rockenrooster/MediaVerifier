using System.Globalization;
using System.Reflection;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Pages;
using MediaVerifier.UI.Shell;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The Dashboard, Scans and status strip as the audit found them: run alerts, Continue's scope, the large-scan count,
/// the dropped folder, the engine bar, "last scan", the tiles' change, the media rows, the run card and the strip.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DashboardScansTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly RootRecord[] Roots =
    [
        new() { Id = 1, Path = @"D:\Music", Label = "Music" },
        new() { Id = 2, Path = @"D:\Photos", Label = "Photos" },
        new() { Id = 3, Path = @"D:\Videos", Label = "Videos", Enabled = false },
    ];

    /// <summary>
    /// The engine stores alerts with lower-case names; read with the record's own names they all came back empty and
    /// the drawer drew one blank green OK bar per alert. Each bar now has its tier and the alert's title and detail.
    /// </summary>
    [Fact]
    public async Task A_runs_alerts_show_with_their_tier_title_and_detail()
    {
        using TestDatabase db = new();
        long run = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Standard, """[{"rootId":1,"dir":null,"files":null}]""", null);
        string alerts = """[{"id":"RUN-READ-ERRORS","severity":4,"rootId":null,"detail":"12 files had read errors"},{"id":"RUN-ROOT-OFFLINE","severity":2,"rootId":1,"detail":"D:\\Music"}]""";
        await db.Database.Runs.EndAsync(run, RunStatus.Completed, new RunCounters(10, 10, 10, 1000, 0, 0, 0), alerts, null);
        LiveUiData data = new(db.Database, () => new ScanSettings(), db.Clock);

        RunDetail detail = Assert.IsType<RunDetail>(data.LoadRunDetail(run));
        Assert.Equal(
            [(Severity.Critical, "Storage read errors" + Strings.Separator + "12 files had read errors"), (Severity.Medium, "Folder unavailable" + Strings.Separator + @"D:\Music")],
            detail.Alerts);
        Assert.Empty(LiveUiData.RunAlerts("not json"));
    }

    /// <summary>A notification's link to a run older than the 200 newest opened nothing; any stored run opens now.</summary>
    [Fact]
    public async Task An_old_run_still_opens_and_a_missing_one_is_null()
    {
        using TestDatabase db = new();
        long first = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Quick, "[]", null);
        for (int i = 0; i < 201; i++)
        {
            _ = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Quick, "[]", null);
        }

        LiveUiData data = new(db.Database, () => new ScanSettings(), db.Clock);
        Assert.Equal(first, data.LoadRunDetail(first)?.Run.Id);
        Assert.Null(data.LoadRunDetail(99_999));
    }

    /// <summary>Continue scans the interrupted run's own folders (it scanned every Library folder under the old heading).</summary>
    [Fact]
    public void Continue_scans_the_interrupted_runs_own_folders()
    {
        List<ScanScope>? scopes = LiveUiData.ScopesOf("""[{"rootId":1,"dir":"Night Drive","files":null}]""", Roots);
        Assert.Equal([new ScanScope(1, "Night Drive")], scopes);
        Assert.Equal("Night Drive", LiveUiData.ScopeText(scopes!, Roots));

        // A removed or disabled folder is left out; a file list continues as its folder.
        Assert.Equal([new ScanScope(2)], LiveUiData.ScopesOf("""[{"rootId":2,"dir":null,"files":3},{"rootId":3},{"rootId":9}]""", Roots));
        Assert.Empty(LiveUiData.ScopesOf("""[{"rootId":3}]""", Roots)!);

        // A whole folder covers its subfolders.
        Assert.Equal([new ScanScope(1)], LiveUiData.ScopesOf("""[{"rootId":1,"dir":"A"},{"rootId":1}]""", Roots));

        // A stored scope that names no folder: Continue falls back to every Library folder.
        Assert.Null(LiveUiData.ScopesOf("[]", Roots));
        Assert.Null(LiveUiData.ScopesOf("not json", Roots));
    }

    /// <summary>
    /// A folder no scan has indexed counted as empty, so the first scan (the longest) never asked first; its Add folder
    /// count now stands in until the files table has it.
    /// </summary>
    [Fact]
    public async Task A_new_folders_add_folder_count_makes_its_first_scan_ask_first()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\New", Label = "New" });
        LiveUiData data = new(db.Database, () => new ScanSettings(), db.Clock);
        Assert.False(data.LoadScanEstimate([root]).IsLarge);

        FolderCount count = new(40_000, 2_000, 8_000, 900L * 1024 * 1024 * 1024);
        Assert.Equal(count, FolderCount.Parse(count.ToMeta()));
        Assert.Null(FolderCount.Parse("1|2|x|4"));
        await db.Database.Meta.SetAsync(FolderCount.Key(root), count.ToMeta());
        ScanEstimate estimate = data.LoadScanEstimate([root]);
        Assert.Equal(50_000, estimate.Files);
        Assert.Equal(count.Bytes, estimate.Bytes);
        Assert.True(estimate.IsLarge);
        Assert.Equal(Strings.Scans.AllFolders, estimate.Folders);

        // The not-yet-scanned recommendation has its time from the same count (it read "takes about —").
        Assert.NotNull(data.LoadDashboard().FirstScanEstimate);
    }

    /// <summary>The full pass took ceil(1 / (1 - overdue share)) days whatever the library's size; it is size ÷ nightly budget now.</summary>
    [Fact]
    public void The_full_pass_follows_the_library_size_and_the_budget()
    {
        Assert.Equal(0, LiveUiData.VerificationPassDays(1_000, 0, 120));
        Assert.Equal(5, LiveUiData.VerificationPassDays(3_500_000_000_000, 10, 120));
        Assert.Equal(10, LiveUiData.VerificationPassDays(3_500_000_000_000, 10, 60));
        Assert.Equal("2 h", LiveUiData.BudgetText(120));
        Assert.Equal("90 min", LiveUiData.BudgetText(90));
        RecommendationItem item = LiveUiData.Word(new Recommendation(RecommendationKind.VerificationOverdue, Severity.Low, 10, 0, null, [], Percent: 0.18), new ScanSettings(), 41);
        Assert.Equal("At 2 h per night a full pass takes 41 days", item.Detail);
    }

    /// <summary>Recommendation titles have a singular: "1 videos have no seek index" was written for one file.</summary>
    [Fact]
    public void Recommendation_titles_have_a_singular()
    {
        RecommendationItem Title(RecommendationKind kind, int files) => LiveUiData.Word(new Recommendation(kind, Severity.Medium, files, 0, "x", [1], Place: "E"), new ScanSettings(), 1);
        Assert.Equal("1 video has no seek index", Title(RecommendationKind.MissingIndex, 1).Title);
        Assert.Equal("2 videos have no seek index", Title(RecommendationKind.MissingIndex, 2).Title);
        Assert.Equal("1 file has the wrong extension", Title(RecommendationKind.WrongExtension, 1).Title);
        Assert.Equal("1 album looks converted from MP3", Title(RecommendationKind.FakeLossless, 1).Title);
        Assert.Equal("1 video has less detail than its resolution", Title(RecommendationKind.UpscaledVideo, 1).Title);
        Assert.Equal("Drive E: returned read errors on 1 file", Title(RecommendationKind.ReadErrors, 1).Title);
    }

    /// <summary>The Dashboard's "Last" was the newest run of any result; it is the last completed one, as on Scans.</summary>
    [Fact]
    public async Task The_dashboards_last_scan_is_the_last_completed_run_as_on_scans()
    {
        using TestDatabase db = new();
        long completed = await db.Database.Runs.BeginAsync(RunTrigger.Scheduled, ScanDepth.Standard, "[]", null);
        await db.Database.Runs.EndAsync(completed, RunStatus.Completed, new RunCounters(5, 5, 5, 0, 0, 0, 0), null, null);
        long stopped = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Deep, "[]", null);
        await db.Database.Runs.EndAsync(stopped, RunStatus.Cancelled, new RunCounters(1, 1, 1, 0, 0, 0, 0), null, null);
        LiveUiData data = new(db.Database, () => new ScanSettings(), db.Clock);

        DashboardData dashboard = data.LoadDashboard();
        Assert.Equal(completed, dashboard.LastRun?.Id);
        Assert.Equal(data.LoadScans().LastScan, dashboard.LastRun?.Ended);
    }

    /// <summary>
    /// The tiles' change and the sparkline were never filled outside the demo. They come from the open counts each run
    /// records at its end: the change against what was open when the last completed scan began, the sparkline per scan.
    /// </summary>
    [Fact]
    public void Tiles_compare_with_the_counts_the_last_scan_began_with()
    {
        static RunRecord Run(long id, RunStatus status, string? summary) => new() { Id = id, Status = status, SummaryJson = summary };
        static string Open(int c, int h, int m, int l) => $$"""{"newCritical":0,"openCritical":{{c}},"openHigh":{{h}},"openMedium":{{m}},"openLow":{{l}}}""";
        RunRecord last = Run(5, RunStatus.Completed, Open(3, 14, 96, 212));
        List<RunRecord> runs = [Run(6, RunStatus.Running, null), last, Run(4, RunStatus.Cancelled, Open(2, 13, 96, 216)), Run(3, RunStatus.Completed, null), Run(2, RunStatus.Completed, Open(1, 1, 1, 1))];

        (SeverityCounts? before, List<double> history) = LiveUiData.OpenHistory(runs, last, complete: true);
        Assert.Equal(new SeverityCounts(2, 13, 96, 216), before);
        Assert.Equal([4, 327, 325], history);

        // Before the first scan of all nothing was open; a run that recorded no counts gives no change.
        Assert.Equal(SeverityCounts.Zero, LiveUiData.OpenHistory([last], last, complete: true).Before);
        Assert.Null(LiveUiData.OpenHistory([last, Run(4, RunStatus.Completed, "{}")], last, complete: true).Before);
        Assert.Null(LiveUiData.OpenAtEnd("""{"openCritical":1}"""));
    }

    /// <summary>
    /// The first-run screen says "or drop folders anywhere in this window", but nothing accepted a drop. A folder dropped
    /// on the Dashboard opens Add folder with it; anything else is refused without reading the disk.
    /// </summary>
    [Fact]
    public void A_folder_dropped_on_the_dashboard_opens_add_folder_with_it()
    {
        List<(PageId Page, object? Request)> navigations = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("dashboard-drop");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(1280, 800));
            form.Navigate(PageId.Dashboard);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            DashboardPage page = Assert.IsType<DashboardPage>(form.CurrentPageControl);
            services.Navigate = (p, r) => navigations.Add((p, r));

            Assert.True(page.AllowDrop);
            Assert.True(page.DropFolder(new DataObject(DataFormats.FileDrop, new[] { @"D:\Music" })));
            Assert.False(page.DropFolder(new DataObject(DataFormats.Text, "D:\\Music")));
            Assert.Null(DashboardPage.DroppedFolder(null));
            form.Close();
        });

        (PageId page, object? request) = Assert.Single(navigations);
        Assert.Equal(PageId.Library, page);
        Assert.Equal(new AddFolderRequest(@"D:\Music"), request);
    }

    /// <summary>
    /// With the media engine missing the Dashboard showed no bar (it was bound to a constant false), and the bar's
    /// "Open download page" had no handler. The bar shows above the cards, first in Tab order, and the page is taller by it.
    /// </summary>
    [Fact]
    public void The_media_engine_bar_shows_when_scanning_is_not_allowed()
    {
        bool visible = false, wired = false, first = false;
        int height = 0, expected = 0;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("dashboard-engine");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Services(state, temp, new BundleCheck(BundleState.Missing, null, [], [], "bundle.json not found"));
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(1280, 800));
            form.Navigate(PageId.Dashboard);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            DashboardPage page = Assert.IsType<DashboardPage>(form.CurrentPageControl);
            InfoBar bar = Field<InfoBar>(page, "_engineBar");
            visible = bar.Visible;
            wired = typeof(InfoBar).GetField("ActionClicked", Private)?.GetValue(bar) is not null;
            first = bar.TabIndex == 0;
            height = page.MeasureContentHeight(page.Width);
            expected = Dpi.Scale(408 + 264 + 52, page.DeviceDpi);
            form.Close();
        });

        Assert.True(visible, "The engine bar should show.");
        Assert.True(wired, "Open download page should have a handler.");
        Assert.True(first, "The bar should come first in Tab order.");
        Assert.Equal(expected, height);
    }

    /// <summary>
    /// Folders exist but nothing was scanned: tiles and media rows read dashes (they read 0 and "No problems"), and the
    /// one recommendation has its time. While a scan runs, Continue and Scan are not offered (they could only fail).
    /// </summary>
    [Fact]
    public void Before_any_scan_values_are_dashes_and_running_scans_hide_continue()
    {
        string tile = string.Empty;
        string[] facts = [];
        List<string> notScanned = [];
        List<string> whileScanning = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("dashboard-not-scanned");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(1280, 800));
            form.Navigate(PageId.Dashboard);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            DashboardPage page = Assert.IsType<DashboardPage>(form.CurrentPageControl);
            DemoScanControl scans = (DemoScanControl)services.Scans;
            scans.Status = EngineStatus.Idle;
            DashboardData demo = new DemoUiData().LoadDashboard();
            Apply(page, demo with { AnyScanned = false, FilesScanned = 0, FirstScanEstimate = TimeSpan.FromMinutes(90) });
            tile = Field<string>(Field<Array>(page, "_tiles").GetValue(0)!, "_count");
            facts = [.. Field<string[]>(Field<object>(page, "_media"), "_facts")];
            notScanned = [.. Items(page).Select(i => i.Title + "|" + i.Detail)];

            RecommendationItem continueItem = new(Severity.Medium, Strings.Recommendations.InterruptedTitle, string.Empty, Strings.Recommendations.Continue, "continue");
            scans.Status = DemoScanControl.Running;
            Apply(page, demo with { Recommendations = [continueItem, .. demo.Recommendations] });
            whileScanning = [.. Items(page).Select(i => i.ActionId)];
            form.Close();
        });

        Assert.Equal(Strings.Dash, tile);
        Assert.All(facts, f => Assert.Equal(Strings.Dash, f));
        Assert.Equal([Strings.Dashboard.NotScannedTitle + "|" + string.Format(CultureInfo.CurrentCulture, Strings.Dashboard.NotScannedDetail, "1 h 30 min")], notScanned);
        Assert.DoesNotContain("continue", whileScanning);
        Assert.NotEmpty(whileScanning);
    }

    /// <summary>
    /// The media rows are reached with Tab and chosen with Up, Down and Enter (they reacted to the mouse only); the
    /// recommendation buttons survive a reload (they were rebuilt, taking the keyboard focus away); Health no longer
    /// opens Issues on any click; Create schedule asks Schedule for a new schedule.
    /// </summary>
    [Fact]
    public void Media_rows_take_the_keyboard_and_recommendation_buttons_survive_a_reload()
    {
        List<(PageId Page, object? Request)> navigations = [];
        bool sameButtons = false, tabStop = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("dashboard-keys");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(1280, 800));
            form.Navigate(PageId.Dashboard);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            DashboardPage page = Assert.IsType<DashboardPage>(form.CurrentPageControl);
            services.Navigate = (p, r) => navigations.Add((p, r));

            Control media = Field<Control>(page, "_media");
            tabStop = media.TabStop;
            Call(media, "OnKeyDown", new KeyEventArgs(Keys.Down));
            Call(media, "OnKeyDown", new KeyEventArgs(Keys.Enter));

            Call(Field<Control>(page, "_health"), "OnClick", EventArgs.Empty);

            KitButton[] before = [.. Field<KitButton[]>(Field<object>(page, "_recommendations"), "_buttons")];
            page.Reload();
            ScreenRenderer.WaitFor(page.Loading);
            KitButton[] after = Field<KitButton[]>(Field<object>(page, "_recommendations"), "_buttons");
            sameButtons = before.SequenceEqual(after) && after.All(b => !b.IsDisposed);

            page.RunRecommendation(new RecommendationItem(Severity.Low, Strings.Recommendations.NoScheduleTitle, string.Empty, Strings.Recommendations.CreateSchedule, "schedule"));
            form.Close();
        });

        Assert.True(tabStop, "By media type should be a tab stop.");
        Assert.True(sameButtons, "The recommendation buttons should be kept.");
        Assert.Equal(2, navigations.Count);
        Assert.Equal((PageId.Issues, (object?)new IssuesFilterRequest(null, MediaKind.Video)), navigations[0]);
        Assert.Equal(PageId.Schedule, navigations[1].Page);
        Assert.IsType<NewScheduleRequest>(navigations[1].Request);
    }

    /// <summary>
    /// The run card: a scheduled scan in the background disables Scan with the reason (the card said "No scan is
    /// running"); files keep their rows and the card keeps its height while workers move between files; Finishing
    /// disables Pause and Stop with "Finishing…"; a paused scan shows no speed.
    /// </summary>
    [Fact]
    public void The_run_card_says_what_runs_and_keeps_its_rows()
    {
        bool scanEnabled = true;
        string tip = string.Empty;
        List<int> heights = [];
        List<string?> rows = [];
        bool pauseEnabled = true, stopEnabled = true;
        string heading = string.Empty, speed = string.Empty;
        int idlePoll = 0, activePoll = 0, endedHeight = 0, idleHeight = 0;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("scans-card");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            DemoScanControl scans = new() { Status = EngineStatus.Idle };
            using UiServices services = Services(state, temp, new BundleCheck(BundleState.Ok, null, [], [], null), scans, background: true);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(1280, 800));
            form.Navigate(PageId.Scans);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            ScansPage page = Assert.IsType<ScansPage>(form.CurrentPageControl);
            object card = Field<object>(page, "_run");
            KitButton scan = Field<KitButton>(card, "_scan");
            scanEnabled = scan.Enabled;
            tip = KitToolTip.Get(scan);
            idlePoll = Field<System.Windows.Forms.Timer>(page, "_poll").Interval;

            InFlightItem a = new(@"D:\Music\a.flac", "Reading", 0, MediaKind.Audio);
            InFlightItem b = new(@"D:\Music\b.flac", "Reading", 0, MediaKind.Audio);
            InFlightItem c = new(@"D:\Music\c.flac", "Reading", 0, MediaKind.Audio);
            InFlightItem d = new(@"D:\Music\d.flac", "Reading", 0, MediaKind.Audio);
            foreach (InFlightItem[] inFlight in new[] { new[] { a, b, c }, [c, a], [d, c, a] })
            {
                scans.Status = DemoScanControl.Running with { InFlight = inFlight };
                Call(card, "UpdateStatus");
                heights.Add((int)card.GetType().GetProperty("LogicalHeight")!.GetValue(card)!);
            }

            rows = [.. Field<string?[]>(card, "_slotNames").Take(3)];
            activePoll = Field<System.Windows.Forms.Timer>(page, "_poll").Interval;

            scans.Status = DemoScanControl.Running with { Phase = EnginePhase.Finishing };
            Call(card, "UpdateStatus");
            pauseEnabled = Field<KitButton>(card, "_pause").Enabled;
            stopEnabled = Field<KitButton>(card, "_stop").Enabled;
            heading = Field<string>(card, "_heading");

            scans.Status = DemoScanControl.Running with { Phase = EnginePhase.Paused, InFlight = [] };
            Call(card, "UpdateStatus");
            speed = Field<string[]>(card, "_values")[2];

            // The run ends: the card goes back to its idle height, and History moves up with it.
            scans.Status = EngineStatus.Idle;
            Call(card, "UpdateStatus");
            endedHeight = ((Control)card).Height;
            idleHeight = Dpi.Scale(176, page.DeviceDpi);
            form.Close();
        });

        Assert.False(scanEnabled, "Scan should be disabled during a background scan.");
        Assert.Equal(Strings.Shell.ScheduledRunning, tip);
        Assert.Equal(1000, idlePoll);
        Assert.Equal(250, activePoll);
        Assert.All(heights, h => Assert.Equal(207 + (28 * 3), h));
        Assert.Equal(["a.flac", "d.flac", "c.flac"], rows);
        Assert.False(pauseEnabled);
        Assert.False(stopEnabled);
        Assert.Equal(Strings.Shell.Finishing, heading);
        Assert.Equal(Strings.Dash, speed);
        Assert.Equal(idleHeight, endedHeight);
    }

    /// <summary>
    /// Continue on the interrupted bar hid the bar before it knew whether a scan started; when none starts the bar
    /// stays and a toast says why.
    /// </summary>
    [Fact]
    public void The_interrupted_bar_stays_when_continue_cannot_start()
    {
        bool shownAfter = false;
        List<ToastContent> toasts = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("scans-continue");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = Services(state, temp, new BundleCheck(BundleState.Ok, null, [], [], null), new DemoScanControl { Status = EngineStatus.Idle }, background: false);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(1280, 800));
            services.ShowToast = toasts.Add;
            form.Navigate(PageId.Scans);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            ScansPage page = Assert.IsType<ScansPage>(form.CurrentPageControl);
            RunInfo interrupted = new(77, DateTimeOffset.UtcNow, null, RunTrigger.Manual, ScanDepth.Standard, "Music", 10, 0, 0, RunStatus.Interrupted);
            Apply(page, new ScansData([interrupted], interrupted, null, null));
            InfoBar bar = Field<InfoBar>(page, "_interrupted");
            Assert.True(bar.Visible);
            Call(page, "ContinueInterrupted");
            Settle(() => toasts.Count > 0);
            shownAfter = bar.Visible;
            form.Close();
        });

        Assert.True(shownAfter, "The bar should stay when nothing started.");
        Assert.Equal(Strings.Scans.RunGone, Assert.Single(toasts).Text);
    }

    /// <summary>
    /// The status strip: a click opens Scans, so it is interactive (hover fill, hand cursor); the ring stops while the
    /// scan is paused and while the window is minimised (it woke the window 60 times a second).
    /// </summary>
    [Fact]
    public void The_strip_stops_its_ring_while_paused_or_minimised()
    {
        bool interactive = false, hand = false, spinsPaused = true, spinsAgain = false, spinsMinimised = true, spinsRestored = false, hooked = false;
        Sta.Run(() =>
        {
            // A strip with no window is visible as far as it knows; no window is shown or minimised on the desktop.
            using ScanStatusStrip strip = new();
            System.Windows.Forms.Timer spin = Field<System.Windows.Forms.Timer>(strip, "_spin");
            interactive = (bool)typeof(KitControl).GetProperty("Interactive", Private)!.GetValue(strip)!;
            hand = strip.Cursor == Cursors.Hand;

            strip.Pause.Text = Strings.Shell.Resume;
            spinsPaused = spin.Enabled;
            strip.Pause.Text = Strings.Shell.Pause;
            spinsAgain = spin.Enabled || !SystemInformation.UIEffectsEnabled;

            // The window's state as the strip reads it when the window is resized (minimised and restored).
            using Form window = new() { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
            typeof(ScanStatusStrip).GetField("_form", Private)!.SetValue(strip, window);
            Call(strip, "OnWindowResize", window, EventArgs.Empty);
            spinsMinimised = spin.Enabled;
            window.WindowState = FormWindowState.Normal;
            Call(strip, "OnWindowResize", window, EventArgs.Empty);
            spinsRestored = spin.Enabled || !SystemInformation.UIEffectsEnabled;
            typeof(ScanStatusStrip).GetField("_form", Private)!.SetValue(strip, null);

            // In a window, the strip follows that window's resizes.
            using Form host = new() { ShowInTaskbar = false };
            using ScanStatusStrip hosted = new();
            host.Controls.Add(hosted);
            hooked = ReferenceEquals(typeof(ScanStatusStrip).GetField("_form", Private)!.GetValue(hosted), host);
        });

        Assert.True(interactive);
        Assert.True(hand);
        Assert.False(spinsPaused, "The ring should stop while paused.");
        Assert.True(spinsAgain, "The ring should turn again after Resume.");
        Assert.False(spinsMinimised, "The ring should stop while minimised.");
        Assert.True(spinsRestored, "The ring should turn again when the window is restored.");
        Assert.True(hooked, "The strip should follow its window.");
    }

    /// <summary>
    /// The Dashboard's Continue on a scan of one folder scans that folder again at the run's depth. It scanned every
    /// Library folder (Music and Photos here) under the old run's heading.
    /// </summary>
    [FixtureFact]
    public void Continue_on_the_dashboard_scans_only_the_interrupted_runs_folder()
    {
        long photos = 0;
        string? scope = null;
        ScanDepth depth = ScanDepth.None;
        string heading = string.Empty;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("continue-scope");
            string music = Directory.CreateDirectory(temp.Combine("My Music")).FullName;
            string pictures = Directory.CreateDirectory(temp.Combine("My Photos")).FullName;
            FixtureFactory.CopyTo("src16.flac", music, "song.flac");
            FixtureFactory.CopyTo("src16.flac", pictures, "other.flac");
            MediaVerifier.Core.AppPaths paths = temp.CreateAppPaths();
            paths.EnsureCreated();

            // A Quick scan of the Photos folder that was running when MediaVerifier closed.
            using (Database before = Database.Open(paths.DefaultDatabaseFile))
            {
                _ = Db(() => before.Roots.AddAsync(new RootRecord { Path = music, Label = "My Music" }).GetAwaiter().GetResult());
                photos = Db(() => before.Roots.AddAsync(new RootRecord { Path = pictures, Label = "My Photos" }).GetAwaiter().GetResult());
                _ = Db(() => before.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Quick, $$"""[{"rootId":{{photos}},"dir":null,"files":null}]""", null).GetAwaiter().GetResult());
            }

            using AppState state = AppState.Load(paths, installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, FfmpegEnvironment.Engine, startupMaintenance: false, engineMutexName: @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N"));
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            int runsBefore = Db(() => services.Database.Runs.List(10)).Count;

            form.Navigate(PageId.Dashboard);
            Settle(() => form.CurrentPageLoading.IsCompleted);
            ((DashboardPage)form.CurrentPageControl!).RunRecommendation(new RecommendationItem(Severity.Medium, Strings.Recommendations.InterruptedTitle, string.Empty, Strings.Recommendations.Continue, "continue"));
            Settle(() => Db(() => services.Database.Runs.List(10)).Count > runsBefore);
            heading = services.Scans.ScopeText;
            RunRecord continued = Db(() => services.Database.Runs.List(1))[0];
            scope = continued.ScopeJson;
            depth = continued.Depth;
            Settle(() => !services.Scans.Status.IsActive, TimeSpan.FromMinutes(1));
            form.Close();
        });

        Assert.Equal($$"""[{"rootId":{{photos}},"dir":null,"files":null}]""", scope);
        Assert.Equal(ScanDepth.Quick, depth);
        Assert.Equal("My Photos", heading);
    }

    /// <summary>Services like the render harness's, with the media engine, the scan control and a background run chosen by the test.</summary>
    private static UiServices Services(AppState state, TempDirectory temp, BundleCheck engine, IScanControl? scans = null, bool? background = null)
    {
        Database database = Database.Open(Path.Combine(temp.Path, "test.db"), DemoUiData.Clock);
        UiServices services = new()
        {
            State = state,
            Database = database,
            Clock = DemoUiData.Clock,
            Scans = scans ?? new DemoScanControl(),
            MediaEngine = new MediaEngineInfo(new FfmpegLocation(Path.Combine(temp.Path, "ffmpeg"), IsOverride: false), engine),
            Data = new DemoUiData(),
            IsDemo = background is null,
            BackgroundProbe = () => background == true,
        };
        services.Own(database);
        return services;
    }

    private static IReadOnlyList<RecommendationItem> Items(DashboardPage page) => Field<IReadOnlyList<RecommendationItem>>(Field<object>(page, "_recommendations"), "_items");

    private static void Apply(PageBase page, object data) => Call(page, "ApplyData", data);

    private static T Field<T>(object owner, string name)
    {
        for (Type? type = owner.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(name, Private) is FieldInfo field)
            {
                return (T)field.GetValue(owner)!;
            }
        }

        throw new MissingFieldException(owner.GetType().Name, name);
    }

    private static void Call(object owner, string name, params object[] arguments)
    {
        for (Type? type = owner.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetMethod(name, Private | BindingFlags.Public, [.. arguments.Select(a => a.GetType())]) is MethodInfo method)
            {
                method.Invoke(owner, arguments);
                return;
            }
        }

        throw new MissingMethodException(owner.GetType().Name, name);
    }

    private static void Settle(Func<bool> done, TimeSpan? limit = null)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.Elapsed < (limit ?? TimeSpan.FromSeconds(10)))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>The test's own database reads, made on a pool thread (the UI thread's guard throws).</summary>
    private static T Db<T>(Func<T> read) => Task.Run(read).GetAwaiter().GetResult();
}
