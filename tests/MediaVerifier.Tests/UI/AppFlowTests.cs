using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using MediaVerifier.App;
using MediaVerifier.Diagnostics;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Core.Updates;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.Tests.Updates;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;
using static MediaVerifier.Tests.TestSupport.Ui;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The user's main journeys through the real window, pages, dialogs and handlers, on live services in a temporary
/// profile (no start-up maintenance, so nothing is registered with Windows): Check now, Download and install, and the
/// installer started with the app closing; Add folder, a scan and the damage recorded. A modal dialog runs its own
/// message loop, so a timer inside that loop answers it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AppFlowTests
{
    private const string SetupUrl = "https://github.com/rockenrooster/MediaVerifier/releases/download/v0.1.0.2/MediaVerifier-Setup.exe";

    [Fact]
    public void Check_now_then_Download_and_install_starts_the_verified_installer_and_closes_the_app()
    {
        using RSA key = RSA.Create(3072);
        byte[] setup = new byte[4_500_000];
        new Random(7).NextBytes(setup);
        Dictionary<string, (HttpStatusCode, byte[])> responses = UpdateCheckTests.Release(key, manifest => manifest["sha256"] = Convert.ToHexStringLower(SHA256.HashData(setup)));
        responses[SetupUrl] = (HttpStatusCode.OK, setup);
        string? confirmTitle = null;
        string? started = null;
        bool closed = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("update-flow");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UpdateService updates = new(new UpdateCheckTests.FakeGitHub(responses), temp.Combine("Updates"), new Version(0, 1, 0, 1), key.ExportSubjectPublicKeyInfoPem())
            {
                StartInstaller = path => started = File.Exists(path) ? path : null,
            };
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), updates, startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            form.FormClosed += (_, _) => closed = true;
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            form.Navigate(PageId.Settings);
            Settle(() => form.CurrentPageLoading.IsCompleted);
            SettingsPage settings = (SettingsPage)form.CurrentPageControl!;
            settings.ShowSection(5);
            Pump(TimeSpan.FromMilliseconds(200));

            Find<KitButton>(settings, b => b.Text == Strings.Settings.CheckNow).PerformClick();
            Settle(() => updates.LastResult is not null && !updates.IsChecking);
            Pump(TimeSpan.FromMilliseconds(200));
            KitButton install = Find<KitButton>(settings, b => b.Text is Strings.Settings.DownloadInstall or Strings.Settings.InstallApp);
            Assert.True(install.Enabled, "Download and install is not enabled after the check: " + updates.LastResult?.Message);

            confirmTitle = Answer<MessageDialog>(install.PerformClick, dialog => dialog.DialogResult = DialogResult.OK);
            Settle(() => closed || started is not null, TimeSpan.FromSeconds(30));
            Pump(TimeSpan.FromMilliseconds(300));
        });

        Assert.Equal("Install version 0.1.0.2?", confirmTitle);
        Assert.NotNull(started);
        Assert.True(closed, "MediaVerifier did not close for the installer.");
    }

    [FixtureFact]
    public void Add_folder_then_scan_records_the_damage_and_nothing_on_the_healthy_file()
    {
        List<string> healthy = [];
        List<string> damaged = [];
        string report = string.Empty;
        bool stripShown = false;
        bool issuesShown = false;
        List<string> found = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("app-flow");
            string library = Directory.CreateDirectory(temp.Combine("My Music")).FullName;
            FixtureFactory.CopyTo("src16.flac", library, "healthy.flac");
            string broken = FixtureFactory.CopyTo("m128.mp3", library, "broken.mp3");
            Mutate.Truncate(broken, 0.55);

            // Files written in the last two minutes are skipped as still being written (subplan 05).
            File.SetLastWriteTimeUtc(broken, DateTime.UtcNow.AddHours(-1));
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, FfmpegEnvironment.Engine, startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            form.Navigate(PageId.Library);
            Settle(() => form.CurrentPageLoading.IsCompleted);

            // Add folder: type the path into the dialog and press its Add folder button.
            KitButton add = Find<KitButton>(form.CurrentPageControl!, b => b.Text == Strings.Library.AddFolder);
            string? title = Answer<AddFolderDialog>(add.PerformClick, dialog =>
            {
                Find<PathBox>(dialog, _ => true).Text = library;

                // The folder is checked off the UI thread after a pause in typing; Add folder waits for the answer.
                KitButton confirm = Find<KitButton>(dialog, b => b.Text == Strings.Library.AddFolder);
                Assert.False(confirm.Enabled);
                Settle(() => confirm.Enabled);
                confirm.PerformClick();
            });
            Assert.Equal(Strings.Library.AddTitle, title);
            Settle(() => Db(services.Database.Roots.List).Count == 1);
            Assert.Single(Db(services.Database.Roots.List));

            // The header's Scan button, which adding the first folder enables; the strip shows the scan at once.
            SplitButton scan = form.Header.Scan;
            Settle(() => scan.Enabled);
            Assert.True(scan.Enabled, "Scan stayed disabled after the first folder was added.");
            services.ScanStarted += () => stripShown = form.Shell.StatusStripVisible;
            scan.PerformClick();

            // Wait on Issues: when the scan ends, the page shows what it found without being opened again.
            form.Navigate(PageId.Issues);
            Settle(() => Db(() => services.Database.Runs.List(10)).Any(r => r.EndedUtc is not null), TimeSpan.FromMinutes(2));
            Settle(() => form.CurrentPageControl is IssuesPage { Rows.Count: > 0 }, TimeSpan.FromSeconds(10));
            issuesShown = form.CurrentPageControl is IssuesPage { Rows.Count: > 0 };
            Pump(TimeSpan.FromMilliseconds(300));

            List<FileRecord> files = Db(() => services.Database.Files.GetMany(services.Database.Queries.FileIds(new FileFilter())));
            Assert.True(files.Count == 2, $"Files indexed: {files.Count}; runs: {string.Join(", ", Db(() => services.Database.Runs.List(10)).Select(r => $"{r.Status} ended {r.EndedUtc}"))}; engine {services.Scans.Status.Phase}");
            foreach (FileRecord file in files)
            {
                bool hasDamage = Db(() => services.Database.Findings.ForFile(file.Id)).Any(f => f.Status == FindingStatus.Open && f.RuleId.StartsWith("COR-", StringComparison.Ordinal));
                (hasDamage ? damaged : healthy).Add(file.Name);
            }

            // The header's search: Library → Browse lists the matching files of every watched folder.
            services.Navigate!(PageId.Library, new LibrarySearchRequest("broken"));
            Settle(() => form.CurrentPageLoading.IsCompleted);
            Settle(() => form.CurrentPageControl is LibraryPage { Rows.Count: > 0 });
            found = form.CurrentPageControl is LibraryPage browse ? [.. browse.Rows.Values.Select(r => r.Name)] : [];

            report = string.Join("; ", files.Select(f => $"{f.Name} {f.State} {f.SkipReason} [{string.Join(" ", Db(() => services.Database.Findings.ForFile(f.Id)).Select(x => x.RuleId + "/" + x.Status))}]"))
                + " | runs: " + string.Join(", ", Db(() => services.Database.Runs.List(10)).Select(r => $"{r.Status} {r.Depth}"));

            if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
            {
                // The pages as they show real findings (not the demo data of the render harness).
                foreach (PageId page in new[] { PageId.Issues, PageId.Library, PageId.Dashboard })
                {
                    form.Navigate(page);
                    Settle(() => form.CurrentPageLoading.IsCompleted);
                    Pump(TimeSpan.FromMilliseconds(500));
                    ScreenRenderer.Save(form, Path.Combine(folder, "live-" + page.ToString().ToLowerInvariant() + ".png"));
                }
            }

            form.Close();
        });

        Assert.True(stripShown, "The status strip did not show the scan when it started.");
        Assert.True(issuesShown, "Issues did not show the scan's findings when the scan ended.");
        Assert.Equal(["broken.mp3"], found);
        Assert.True(damaged.SequenceEqual(["broken.mp3"]), report);
        Assert.Equal(["healthy.flac"], healthy);
    }

    [Fact]
    public void Create_report_writes_the_report_before_the_dialog_closes()
    {
        string[] written = [];
        bool openWhileWriting = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("report-flow");
            string folder = Directory.CreateDirectory(temp.Combine("Reports")).FullName;
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);

            string? title = Answer<CreateReportDialog>(() => CreateReportDialog.Show(form, services), dialog =>
            {
                Find<PathBox>(dialog, _ => true).Text = folder;
                Find<KitButton>(dialog, b => b.Text == Strings.Tools.Create).PerformClick();
                openWhileWriting = dialog.DialogResult == DialogResult.None;
            });
            Assert.Equal(Strings.Tools.CreateReport, title);
            written = Directory.GetFiles(folder);
            form.Close();
        });

        Assert.True(openWhileWriting, "Create report closed before the report was written.");
        Assert.Single(written);
        Assert.EndsWith(".html", written[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Tools → Quarantine: with two kept originals selected, Restore brings both back (it acted on the focused row
    /// only).
    /// </summary>
    [FixtureFact]
    public void Restore_in_quarantine_brings_back_every_selected_original()
    {
        bool bothBack = false;
        int chosen = 0;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("quarantine-many");
            string library = Directory.CreateDirectory(temp.Combine("My Music")).FullName;
            string[] files = [FixtureFactory.CopyTo("m128.mp3", library, "one.mp3"), FixtureFactory.CopyTo("m128.mp3", library, "two.mp3")];
            foreach (string file in files)
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
            }

            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, FfmpegEnvironment.Engine, startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            services.ShowToast = _ => { };
            Task<long> add = services.Database.Roots.AddAsync(new RootRecord { Path = library, Label = "My Music", Role = RootRole.Library, MediaMask = MediaKindMask.All, AddedUtc = services.Clock.UtcNow });
            Settle(() => add.IsCompleted);
            Task<bool> scan = services.StartScanAsync([new MediaVerifier.Core.Engine.ScanScope(add.Result)], ScanDepth.Quick, "My Music");
            Settle(() => scan.IsCompleted && Db(() => services.Database.Runs.List(5)).Any(r => r.EndedUtc is not null), TimeSpan.FromMinutes(2));
            long[] ids = [.. Db(() => services.Database.Files.GetMany(services.Database.Queries.FileIds(new FileFilter()))).Select(f => f.Id)];
            Answer<RepairDialog>(() => UiTask.Run(Remediator.RunAsync(services, form, ids, MediaVerifier.Core.Rules.ActionIds.Quarantine)), d => d.DialogResult = DialogResult.OK);
            Settle(() => files.All(f => !File.Exists(f)), TimeSpan.FromSeconds(30));

            form.Navigate(PageId.Tools);
            Settle(() => form.CurrentPageLoading.IsCompleted);
            ToolsPage tools = (ToolsPage)form.CurrentPageControl!;
            typeof(ToolsPage).GetMethod("ShowView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(tools, [0]);
            Pump(TimeSpan.FromMilliseconds(200));
            MediaVerifier.UI.Kit.Lists.VirtualList table = Descendants(tools).OfType<MediaVerifier.UI.Kit.Lists.VirtualList>().First(l => l.Visible);
            table.ApplyClickSelection(0, Keys.None);
            table.ApplyClickSelection(1, Keys.Control);
            chosen = tools.ChosenKept().Count;
            Find<KitButton>(tools, b => b.Text == Strings.Tools.Restore && b.Visible).PerformClick();
            Settle(() => files.All(File.Exists), TimeSpan.FromSeconds(30));
            bothBack = files.All(File.Exists);
            form.Close();
        });

        Assert.Equal(2, chosen);
        Assert.True(bothBack, "Restore did not bring back both selected originals.");
    }

    /// <summary>
    /// Stop watching a folder while it is scanned: the scan ends cleanly, the folder's rows are gone, and its unfinished
    /// files are skipped. Each used to count as an error with a stack trace in error.log ('FOREIGN KEY constraint
    /// failed'): 22 errors for a 30-file folder removed after 8.
    /// </summary>
    [FixtureFact]
    public void Removing_a_folder_during_its_scan_skips_its_remaining_files()
    {
        RunRecord? run = null;
        int roots = -1;
        int files = -1;
        string errorLog = string.Empty;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("remove-flow");
            string library = Directory.CreateDirectory(temp.Combine("My Music")).FullName;
            for (int i = 0; i < 30; i++)
            {
                File.SetLastWriteTimeUtc(FixtureFactory.CopyTo("src16.flac", library, $"track{i:00}.flac"), DateTime.UtcNow.AddHours(-1));
            }

            MediaVerifier.Core.AppPaths paths = temp.CreateAppPaths();
            using AppState state = AppState.Load(paths, installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, FfmpegEnvironment.Engine, startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Task<long> add = services.Database.Roots.AddAsync(new RootRecord { Path = library, Label = "My Music", Role = RootRole.Library, MediaMask = MediaKindMask.All });
            Settle(() => add.IsCompleted);
            Task<bool> scan = services.StartScanAsync([new MediaVerifier.Core.Engine.ScanScope(add.Result)], ScanDepth.Standard, "My Music");
            Settle(() => scan.IsCompleted);
            Assert.True(scan.Result, "The scan did not start.");
            Settle(() => services.Scans.Status.FilesDone >= 3, TimeSpan.FromSeconds(60));
            Assert.True(services.Scans.Status.IsActive, "The scan ended before the folder was removed.");

            Task remove = services.Database.Roots.RemoveAsync(add.Result);
            Settle(() => remove.IsCompleted && !services.Scans.Status.IsActive, TimeSpan.FromMinutes(2));
            run = Db(() => services.Database.Runs.List(5))[0];
            roots = Db(services.Database.Roots.List).Count;
            files = Db(() => services.Database.Queries.FileIds(new FileFilter())).Length;
            string log = Path.Combine(paths.LogsDirectory, "error.log");
            errorLog = File.Exists(log) ? File.ReadAllText(log) : string.Empty;
            form.Close();
        });

        Assert.Equal(RunStatus.Completed, run!.Status);
        Assert.Equal(0, run.Errors);
        Assert.Equal(0, roots);
        Assert.Equal(0, files);
        Assert.DoesNotContain("FOREIGN KEY", errorLog, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Dashboard's Continue on an interrupted scan starts the scan again and shows Scans. It used to open Scans
    /// only, where Continue had to be pressed a second time.
    /// </summary>
    [FixtureFact]
    public void Continue_on_the_dashboard_starts_the_interrupted_scan()
    {
        int runsBefore = 0;
        int runsAfter = 0;
        PageId? shown = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("continue-flow");
            string library = Directory.CreateDirectory(temp.Combine("My Music")).FullName;
            FixtureFactory.CopyTo("src16.flac", library, "healthy.flac");
            MediaVerifier.Core.AppPaths paths = temp.CreateAppPaths();
            paths.EnsureCreated();

            // A scan that was running when MediaVerifier closed: opening the database marks it interrupted.
            using (Database before = Database.Open(paths.DefaultDatabaseFile))
            {
                long root = Db(() => before.Roots.AddAsync(new RootRecord { Path = library, Label = "My Music", Role = RootRole.Library, MediaMask = MediaKindMask.All }).GetAwaiter().GetResult());
                _ = Db(() => before.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Standard, "[]", null).GetAwaiter().GetResult());
            }

            using AppState state = AppState.Load(paths, installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, FfmpegEnvironment.Engine, startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            runsBefore = Db(() => services.Database.Runs.List(10)).Count;
            Assert.Equal(RunStatus.Interrupted, Db(() => services.Database.Runs.List(10))[0].Status);

            form.Navigate(PageId.Dashboard);
            Settle(() => form.CurrentPageLoading.IsCompleted);
            ((DashboardPage)form.CurrentPageControl!).RunRecommendation(new MediaVerifier.UI.Data.RecommendationItem(Severity.Medium, "Interrupted", string.Empty, Strings.Recommendations.Continue, "continue"));
            Settle(() => Db(() => services.Database.Runs.List(10)).Count > runsBefore && form.CurrentPageControl is ScansPage);
            runsAfter = Db(() => services.Database.Runs.List(10)).Count;
            shown = (form.CurrentPageControl as PageBase)?.Id;
            Settle(() => !services.Scans.Status.IsActive, TimeSpan.FromMinutes(1));
            form.Close();
        });

        Assert.Equal(runsBefore + 1, runsAfter);
        Assert.Equal(PageId.Scans, shown);
    }

    /// <summary>
    /// Switching a rule off in Settings → Rules re-judges the library off the UI thread. It used to read every file of
    /// the rule's kinds on the UI thread (a pause at each change, and an exception in a debug build): with the
    /// database guard on, the change goes through and the effect bar has its answer.
    /// </summary>
    [Fact]
    public void Changing_a_rule_rejudges_the_library_off_the_ui_thread()
    {
        string effect = string.Empty;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("rules-flow");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, null, startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            form.Navigate(PageId.Settings);
            Settle(() => form.CurrentPageLoading.IsCompleted);
            SettingsPage settings = (SettingsPage)form.CurrentPageControl!;
            settings.ShowSection(2);
            Pump(TimeSpan.FromMilliseconds(200));

            string title = MediaVerifier.Core.Rules.RuleCatalog.Get("AUD-CLIPPING").Title;
            ToggleSwitch clipping = Find<ToggleSwitch>(settings, t => t.AccessibleName == title);
            clipping.Checked = !clipping.Checked;
            Settle(() => Descendants(settings).OfType<InfoBar>().Any(b => b.Text == Strings.Settings.NoFindingsChanged));
            effect = Descendants(settings).OfType<InfoBar>().FirstOrDefault(b => b.Text == Strings.Settings.NoFindingsChanged)?.Text ?? string.Empty;
            form.Close();
        });

        Assert.Equal(Strings.Settings.NoFindingsChanged, effect);
    }

    /// <summary>
    /// The riskiest action through the app's own path: Move to quarantine asks first (dialog A), takes the file out of
    /// the library, and the toast's Undo puts back exactly the same bytes.
    /// </summary>
    [FixtureFact]
    public void Move_to_quarantine_asks_moves_the_file_and_undo_brings_back_the_same_bytes()
    {
        string? dialog = null;
        bool movedAway = false;
        bool restored = false;
        List<string> toastTitles = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("quarantine-flow");
            string library = Directory.CreateDirectory(temp.Combine("My Music")).FullName;
            string broken = FixtureFactory.CopyTo("m128.mp3", library, "broken.mp3");
            Mutate.Truncate(broken, 0.55);
            File.SetLastWriteTimeUtc(broken, DateTime.UtcNow.AddHours(-1));
            byte[] original = File.ReadAllBytes(broken);
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, FfmpegEnvironment.Engine, startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            List<MediaVerifier.UI.Kit.Popups.ToastContent> toasts = [];
            services.ShowToast = toasts.Add;

            Task<long> add = services.Database.Roots.AddAsync(new RootRecord { Path = library, Label = "My Music", Role = RootRole.Library, MediaMask = MediaKindMask.All, AddedUtc = services.Clock.UtcNow });
            Settle(() => add.IsCompleted);
            Task<bool> scan = services.StartScanAsync([new MediaVerifier.Core.Engine.ScanScope(add.Result)], ScanDepth.Standard, "My Music");
            Settle(() => scan.IsCompleted);
            Assert.True(scan.Result, "The scan did not start.");
            Settle(() => Db(() => services.Database.Runs.List(10)).Any(r => r.EndedUtc is not null), TimeSpan.FromMinutes(2));
            long fileId = Db(() => services.Database.Files.GetMany(services.Database.Queries.FileIds(new FileFilter()))).Single(f => f.Name == "broken.mp3").Id;

            dialog = Answer<RepairDialog>(() => UiTask.Run(Remediator.RunAsync(services, form, [fileId], MediaVerifier.Core.Rules.ActionIds.Quarantine)), d => d.DialogResult = DialogResult.OK);
            Settle(() => toasts.Count > 0, TimeSpan.FromSeconds(30));
            movedAway = !File.Exists(broken);
            toastTitles.AddRange(toasts.Select(t => t.Title));

            Action? undo = toasts.FirstOrDefault()?.Action;
            Assert.NotNull(undo);
            undo();
            Settle(() => File.Exists(broken) && toasts.Count > 1, TimeSpan.FromSeconds(30));
            restored = File.Exists(broken) && File.ReadAllBytes(broken).AsSpan().SequenceEqual(original);
            toastTitles.AddRange(toasts.Skip(1).Select(t => t.Title));
            form.Close();
        });

        Assert.False(string.IsNullOrEmpty(dialog), "Move to quarantine did not ask first.");
        Assert.True(movedAway, "The file is still in the library: " + string.Join(" | ", toastTitles));
        Assert.True(restored, "Undo did not bring back the same bytes: " + string.Join(" | ", toastTitles));
    }

    /// <summary>The test's own database reads, made on a pool thread: on the UI thread they would trip the guard that keeps the app off it.</summary>
    private static T Db<T>(Func<T> read) => Task.Run(read).GetAwaiter().GetResult();
}
