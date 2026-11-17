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
                Find<KitButton>(dialog, b => b.Text == Strings.Library.AddFolder && b.Enabled).PerformClick();
            });
            Assert.Equal(Strings.Library.AddTitle, title);
            Settle(() => services.Database.Roots.List().Count == 1);
            Assert.Single(services.Database.Roots.List());

            // The header's Scan button, which adding the first folder enables; the strip shows the scan at once.
            SplitButton scan = form.Header.Scan;
            Settle(() => scan.Enabled);
            Assert.True(scan.Enabled, "Scan stayed disabled after the first folder was added.");
            services.ScanStarted += () => stripShown = form.Shell.StatusStripVisible;
            scan.PerformClick();

            // Wait on Issues: when the scan ends, the page shows what it found without being opened again.
            form.Navigate(PageId.Issues);
            Settle(() => services.Database.Runs.List(10).Any(r => r.EndedUtc is not null), TimeSpan.FromMinutes(2));
            Settle(() => form.CurrentPageControl is IssuesPage { Rows.Count: > 0 }, TimeSpan.FromSeconds(10));
            issuesShown = form.CurrentPageControl is IssuesPage { Rows.Count: > 0 };
            Pump(TimeSpan.FromMilliseconds(300));

            List<FileRecord> files = services.Database.Files.GetMany(services.Database.Queries.FileIds(new FileFilter()));
            Assert.True(files.Count == 2, $"Files indexed: {files.Count}; runs: {string.Join(", ", services.Database.Runs.List(10).Select(r => $"{r.Status} ended {r.EndedUtc}"))}; engine {services.Scans.Status.Phase}");
            foreach (FileRecord file in files)
            {
                bool hasDamage = services.Database.Findings.ForFile(file.Id).Any(f => f.Status == FindingStatus.Open && f.RuleId.StartsWith("COR-", StringComparison.Ordinal));
                (hasDamage ? damaged : healthy).Add(file.Name);
            }

            // The header's search: Library → Browse lists the matching files of every watched folder.
            services.Navigate!(PageId.Library, new LibrarySearchRequest("broken"));
            Settle(() => form.CurrentPageLoading.IsCompleted);
            Settle(() => form.CurrentPageControl is LibraryPage { Rows.Count: > 0 });
            found = form.CurrentPageControl is LibraryPage browse ? [.. browse.Rows.Values.Select(r => r.Name)] : [];

            report = string.Join("; ", files.Select(f => $"{f.Name} {f.State} {f.SkipReason} [{string.Join(" ", services.Database.Findings.ForFile(f.Id).Select(x => x.RuleId + "/" + x.Status))}]"))
                + " | runs: " + string.Join(", ", services.Database.Runs.List(10).Select(r => $"{r.Status} {r.Depth}"));

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
            Settle(() => services.Database.Runs.List(10).Any(r => r.EndedUtc is not null), TimeSpan.FromMinutes(2));
            long fileId = services.Database.Files.GetMany(services.Database.Queries.FileIds(new FileFilter())).Single(f => f.Name == "broken.mp3").Id;

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

    /// <summary>Runs <paramref name="open"/>, answers the first <typeparamref name="T"/> dialog it shows, returns its title.</summary>
    private static string? Answer<T>(Action open, Action<T> answer)
        where T : Form
    {
        string? title = null;
        using System.Windows.Forms.Timer timer = new() { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            if (title is null && Application.OpenForms.OfType<T>().FirstOrDefault() is T dialog && dialog.Visible)
            {
                title = dialog.Text;
                answer(dialog);
            }
        };
        timer.Start();
        open();
        Settle(() => title is not null && !Application.OpenForms.OfType<T>().Any());
        timer.Stop();
        return title;
    }

    private static T Find<T>(Control root, Func<T, bool> match)
        where T : Control =>
        Descendants(root).OfType<T>().FirstOrDefault(match) ?? throw new InvalidOperationException($"No matching {typeof(T).Name} in {root.GetType().Name}.");

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>An engine mutex of the test's own: a MediaVerifier scanning on this machine does not block the test.</summary>
    private static string OwnEngine() => @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N");

    private static void Settle(Func<bool> done, TimeSpan? limit = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < (limit ?? TimeSpan.FromSeconds(10)))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void Pump(TimeSpan time) => Settle(() => false, time);
}
