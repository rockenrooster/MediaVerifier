using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Ipc;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Core.Updates;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.Tests.Updates;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Pages;
using MediaVerifier.UI.Shell;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The shell (2026-11-18 audit): Ctrl+Z and the toasts' Undo, Scan a folder…, a scan asked for during a scan, the
/// badge reloads, page reloads that finish out of order, the rail for screen readers, start-up and shutdown.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ShellTests
{
    private const int WmKeyDown = 0x0100;

    /// <summary>
    /// Ctrl+Z undid the newest repair batch in the journal, however old, even right after an Ignore whose toast offered
    /// Undo. Now it runs the newest toast's Undo, once, whether from the button or the key.
    /// </summary>
    [Fact]
    public void Ctrl_Z_runs_the_newest_toasts_Undo_once()
    {
        using TempDirectory temp = new("shell-undo");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        using UiServices services = Live(state, temp, new FakeScans());
        int repairUndone = 0;
        int ignoreUndone = 0;
        ToastContent repair = services.TrackUndo(new ToastContent(Severity.None, "Index rebuilt for 1 file", string.Empty, Strings.Dialogs.Undo, () => repairUndone++));
        ToastContent ignore = services.TrackUndo(new ToastContent(Severity.None, "Ignored for 1 problem", string.Empty, Strings.Dialogs.Undo, () => ignoreUndone++));
        ToastContent report = new(Severity.None, "Report saved", "report.html", Strings.Tools.Open, () => { });

        Assert.Same(report, services.TrackUndo(report));
        Assert.True(services.CanUndo);
        Assert.True(services.UndoLast());
        Assert.Equal((0, 1), (repairUndone, ignoreUndone));

        // Its button afterwards does nothing more, and Ctrl+Z does not reach back to the older repair.
        ignore.Action!();
        Assert.Equal(1, ignoreUndone);
        Assert.False(services.CanUndo);
        Assert.False(services.UndoLast());

        // The repair's own toast still undoes it, once.
        repair.Action!();
        repair.Action!();
        Assert.Equal((1, 1), (repairUndone, ignoreUndone));
    }

    [Fact]
    public void Ctrl_Z_in_the_window_undoes_the_last_toast_and_says_when_there_is_nothing_to_undo()
    {
        List<string> toasts = [];
        int undone = 0;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-ctrlz");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp, new FakeScans());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Action<ToastContent> show = services.ShowToast!;
            services.ShowToast = content =>
            {
                toasts.Add(content.Title);
                show(content);
            };

            Assert.True(Press(form, Keys.Control | Keys.Z));
            services.ShowToast(new ToastContent(Severity.None, "Ignored for 2 problems", string.Empty, Strings.Dialogs.Undo, () => undone++));
            Assert.True(Press(form, Keys.Control | Keys.Z));
            Assert.True(Press(form, Keys.Control | Keys.Z));
            form.Close();
        });

        Assert.Equal(1, undone);
        Assert.Equal([Strings.Shell.NothingToUndo, "Ignored for 2 problems", Strings.Shell.NothingToUndo], toasts);
    }

    /// <summary>Scan a folder… only opened the Library page (and on it did nothing). It now asks which folder and scans it.</summary>
    [Fact]
    public void Scan_a_folder_offers_the_enabled_Library_folders_and_scans_the_chosen_one()
    {
        FakeScans scans = new();
        List<string> offered = [];
        long music = 0;
        string scope = string.Empty;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-folder");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp, scans);
            music = AddRoot(services, temp.Combine("Music"), "Music");
            _ = AddRoot(services, temp.Combine("Photos"), "Photos");
            _ = AddRoot(services, temp.Combine("Old"), "Old", enabled: false);
            _ = AddRoot(services, temp.Combine("Backup"), "Backup", RootRole.Backup);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);

            ScreenRenderer.WaitFor(form.Binder!.ScanFolderAsync());
            PopupList menu = Application.OpenForms.OfType<PopupList>().Single(p => p.Visible);
            offered.AddRange(((IReadOnlyList<PopupItem>)typeof(PopupList).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(menu)!).Select(i => i.Text));
            Key(menu, Keys.Down);
            Key(menu, Keys.Enter);
            Settle(() => scans.Started.Count > 0);
            scope = scans.ScopeText;
            form.Close();
        });

        Assert.Equal(["Music", "Photos"], offered);
        ScanRequest request = Assert.Single(scans.Started);
        Assert.Equal(music, Assert.Single(request.Scopes).RootId);
        Assert.Equal(RunTrigger.Manual, request.Trigger);
        Assert.Equal("Music", scope);
    }

    [Fact]
    public void Scan_a_folder_scans_the_only_Library_folder_at_once_and_says_to_add_one_when_there_is_none()
    {
        FakeScans scans = new();
        List<string> reasons = [];
        long only = 0;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-folder-one");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp, scans);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Action<ToastContent> show = services.ShowToast!;
            services.ShowToast = content =>
            {
                reasons.Add(content.Text);
                show(content);
            };

            ScreenRenderer.WaitFor(form.Binder!.ScanFolderAsync());
            only = AddRoot(services, temp.Combine("Music"), "Music");
            ScreenRenderer.WaitFor(form.Binder!.ScanFolderAsync());
            Assert.DoesNotContain(Application.OpenForms.OfType<PopupList>(), p => p.Visible);
            form.Close();
        });

        Assert.Equal([Strings.Shell.AddFolderFirst], reasons);
        Assert.Equal(only, Assert.Single(Assert.Single(scans.Started).Scopes).RootId);
    }

    /// <summary>Subplan 05: a second manual request while a scan runs is queued, not refused. It starts when the scan ends.</summary>
    [Fact]
    public void A_scan_asked_for_during_a_scan_waits_for_it_and_then_starts()
    {
        FakeScans scans = new() { Status = DemoScanControl.Running };
        List<string> toasts = [];
        bool accepted = false;
        int queued = -1;
        int startedWhileBusy = -1;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-queue");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp, scans);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Action<ToastContent> show = services.ShowToast!;
            services.ShowToast = content =>
            {
                toasts.Add(content.Title);
                show(content);
            };

            Task<bool> start = services.StartScanAsync([new ScanScope(7, null, [42])], ScanDepth.Standard, "1 file");
            ScreenRenderer.WaitFor(start);
            accepted = start.Result;

            // The same request again (Scan again pressed twice) waits once.
            ScreenRenderer.WaitFor(services.StartScanAsync([new ScanScope(7, null, [42])], ScanDepth.Standard, "1 file"));
            queued = services.QueuedScanCount;
            startedWhileBusy = scans.Started.Count;
            form.PollScan();
            scans.Status = EngineStatus.Idle;
            form.PollScan();
            Settle(() => scans.Started.Count > 0);
            form.Close();
        });

        Assert.True(accepted);
        Assert.Equal(1, queued);
        Assert.Equal(0, startedWhileBusy);
        Assert.Equal([Strings.Shell.ScanQueued, Strings.Shell.ScanQueued], toasts);
        ScanRequest request = Assert.Single(scans.Started);
        Assert.Equal(new long[] { 42 }, Assert.Single(request.Scopes).FileIds);
        Assert.Equal(ScanDepth.Standard, request.Depth);
    }

    /// <summary>
    /// A request that waited for this window's scan, when a scheduled scan in another process takes over as it ends,
    /// keeps waiting and starts once that one ends too. It used to wait for the next scan of this window, which might
    /// never come.
    /// </summary>
    [Fact]
    public void A_queued_scan_waits_for_a_scheduled_scan_that_follows_and_then_starts()
    {
        FakeScans scans = new() { Status = DemoScanControl.Running };
        bool background = false;
        List<string> toasts = [];
        int startedDuringBackground = -1;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-queue-background");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp, scans, background: () => background);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Action<ToastContent> show = services.ShowToast!;
            services.ShowToast = content =>
            {
                toasts.Add(content.Title);
                show(content);
            };

            ScreenRenderer.WaitFor(services.StartScanAsync([new ScanScope(7)], ScanDepth.Quick, "Music"));
            form.PollScan();

            // This window's scan ends as a scheduled scan starts in another process.
            scans.Status = EngineStatus.Idle;
            background = true;
            form.PollScan();
            Settle(() => false, TimeSpan.FromMilliseconds(200));
            startedDuringBackground = scans.Started.Count;

            background = false;
            form.PollScan();
            Settle(() => scans.Started.Count > 0);
            form.Close();
        });

        Assert.Equal(0, startedDuringBackground);
        Assert.Equal([Strings.Shell.ScanQueued], toasts);
        Assert.Equal(7, Assert.Single(Assert.Single(scans.Started).Scopes).RootId);
    }

    /// <summary>
    /// The rail's badge and the bell reloaded only when folders changed: an Ignore, a repair, an undo or a notification
    /// left them stale until the next scan ended. Findings written by this window's own scan wait for its end.
    /// </summary>
    [Theory]
    [InlineData(ChangeFlags.Roots, true, true)]
    [InlineData(ChangeFlags.Findings | ChangeFlags.Files, false, true)]
    [InlineData(ChangeFlags.Findings | ChangeFlags.Files, true, false)]
    [InlineData(ChangeFlags.Ignores | ChangeFlags.Findings | ChangeFlags.Files, true, true)]
    [InlineData(ChangeFlags.Notifications, true, true)]
    [InlineData(ChangeFlags.Operations, true, true)]
    [InlineData(ChangeFlags.Files, false, false)]
    [InlineData(ChangeFlags.Runs, false, false)]
    public void The_shell_counts_reload_on_changes_that_alter_them(ChangeFlags changes, bool scanning, bool reloads)
    {
        Assert.Equal(reloads, ShellBinder.ReloadsCounts(changes, scanning));
    }

    [Fact]
    public void A_new_notification_updates_the_bell_without_a_scan()
    {
        int before = -1;
        int after = -1;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-bell");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp, new FakeScans(), live: true);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Settle(() => form.Header.Bell.Badge == 0);
            before = form.Header.Bell.Badge;
            _ = services.Database.Notifications.AddAsync(new NotificationRecord { Utc = DateTimeOffset.UtcNow.UtcTicks, Level = Severity.High, Title = "Scheduled scan finished" }).GetAwaiter().GetResult();
            Settle(() => form.Header.Bell.Badge == 1);
            after = form.Header.Bell.Badge;
            form.Close();
        });

        Assert.Equal((0, 1), (before, after));
    }

    /// <summary>Enter in the header search no longer reaches the single-line box, which answered it with the system beep.</summary>
    [Fact]
    public void Enter_in_the_header_search_opens_Browse_without_the_beep()
    {
        bool suppressed = false;
        PageId page = PageId.Dashboard;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-search");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            form.Header.Search.Text = "overpass";
            KeyEventArgs enter = new(Keys.Enter);
            form.Binder!.OnSearchKey(form.Header.Search, enter);
            suppressed = enter.SuppressKeyPress && enter.Handled;
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            page = form.CurrentPage;
            form.Close();
        });

        Assert.True(suppressed);
        Assert.Equal(PageId.Library, page);
    }

    /// <summary>
    /// Loads run side by side on the reader pool: the folder chosen before the last one, finishing later, overwrote it.
    /// Only the newest reload is applied now.
    /// </summary>
    [Fact]
    public void A_page_applies_only_its_newest_reload()
    {
        List<object> applied = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-reload");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using SlowPage page = new(services);
            TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Gates.Enqueue((1, first.Task));
            page.Gates.Enqueue((2, second.Task));
            page.Applied = applied;
            page.Reload();
            Task older = page.Loading;

            // The first load takes its gate before the second starts, so the second reload is the one that gets 2.
            Settle(() => page.GateCount == 1);
            page.Reload();
            Task newer = page.Loading;
            second.SetResult();
            ScreenRenderer.WaitFor(newer);
            first.SetResult();
            ScreenRenderer.WaitFor(older);
        });

        Assert.Equal(new object[] { 2 }, applied);
    }

    /// <summary>
    /// Library loads its folder cards and its Browse view separately, and Browse takes the folders' paths from the
    /// cards. When the header search opens Library, the cards' load starts, then Browse's: the cards still apply when
    /// they finish last, while the older Browse load does not.
    /// </summary>
    [Fact]
    public void A_page_still_applies_an_older_load_of_another_kind()
    {
        List<object> applied = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-reload-kinds");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using SlowPage page = new(services);
            TaskCompletionSource cards = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource browse = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource search = new(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Gates.Enqueue(("cards", cards.Task));
            page.Gates.Enqueue((2, browse.Task));
            page.Gates.Enqueue((3, search.Task));
            page.Applied = applied;

            // Each load takes its gate before the next starts.
            page.Reload();
            Task cardsLoad = page.Loading;
            Settle(() => page.GateCount == 2);
            page.Reload();
            Task browseLoad = page.Loading;
            Settle(() => page.GateCount == 1);
            page.Reload();
            Task searchLoad = page.Loading;
            Settle(() => page.GateCount == 0);

            search.SetResult();
            ScreenRenderer.WaitFor(searchLoad);
            cards.SetResult();
            ScreenRenderer.WaitFor(cardsLoad);
            browse.SetResult();
            ScreenRenderer.WaitFor(browseLoad);
        });

        Assert.Equal(new object[] { 3, "cards" }, applied);
    }

    /// <summary>A load that fails for a reason the page does not handle reaches the error handler instead of an unobserved task.</summary>
    [Fact]
    public void A_reload_failure_the_page_does_not_handle_reaches_the_error_handler()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-reload-fail");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using SlowPage page = new(services) { Failure = new NotSupportedException("load failed") };
            page.Reload();
            ScreenRenderer.WaitFor(page.Loading);
            Application.DoEvents();
        }));

        Assert.Equal("load failed", error.Message);
    }

    /// <summary>Subplan 08, Accessibility: the rail exposes its items with their names and the open page selected.</summary>
    [Fact]
    public void The_rail_exposes_its_items_to_screen_readers_and_names_them_when_collapsed()
    {
        List<string?> names = [];
        string? selected = null;
        string tip = string.Empty;
        Sta.Run(() =>
        {
            using Form host = new() { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false, Size = new Size(400, 700) };
            using NavigationRail rail = new() { Bounds = new Rectangle(0, 0, 200, 640) };
            host.Controls.Add(rail);
            rail.CreateControl();
            rail.SetIssuesBadge(96, Severity.High);
            rail.SelectedPage = PageId.Issues;
            AccessibleObject list = rail.AccessibilityObject;
            for (int i = 0; i < list.GetChildCount(); i++)
            {
                names.Add(list.GetChild(i)?.Name);
            }

            selected = list.GetSelected()?.Name;

            // Collapsed, the rail draws icons only: the item under the pointer names itself.
            rail.Collapsed = true;
            rail.Width = 56;
            Rectangle library = rail.ItemBounds(2);
            Mouse(rail, new Point(library.X + (library.Width / 2), library.Y + (library.Height / 2)));
            tip = KitToolTip.Get(rail);
            KitToolTip.Hide();
        });

        Assert.Equal([Strings.Pages.Dashboard, "Issues · 96 · High", Strings.Pages.Library, Strings.Pages.Scans, Strings.Pages.Schedule, Strings.Pages.Tools, Strings.Pages.Settings], names);
        Assert.Equal("Issues · 96 · High", selected);
        Assert.Equal(Strings.Pages.Library, tip);
    }

    [Theory]
    [InlineData(59.4, "59 s")]
    [InlineData(59.6, "1 min")]
    [InlineData(3_560, "59 min")]
    [InlineData(3_590, "1 h 00 min")]
    [InlineData(6_480, "1 h 48 min")]
    [InlineData(7_190, "2 h 00 min")]
    public void Durations_pick_their_unit_after_rounding(double seconds, string expected)
    {
        Assert.Equal(expected, UiFormat.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(1_023L, "1023 bytes")]
    [InlineData(421_888L, "412 KB")]
    [InlineData(1_048_500L, "1.0 MB")]
    [InlineData(32_715_571L, "31.2 MB")]
    [InlineData(1_073_700_000L, "1.0 GB")]
    public void Sizes_pick_their_unit_after_rounding(long bytes, string expected)
    {
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
            Assert.Equal(expected, UiFormat.Size(bytes));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>
    /// A library database from a newer version (after a downgrade, or a dev build's migration on the shared profile)
    /// ended the start with no window and no message. The failure is one the start-up catches and words.
    /// </summary>
    [Fact]
    public void A_library_database_that_cannot_be_opened_is_reported_with_its_reason()
    {
        using TempDirectory temp = new("shell-newer");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        string file = state.Paths.ResolveDatabaseFile(state.Settings.DatabasePath);
        Database.Open(file).Dispose();
        using (SqliteConnection connection = new($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        Exception error = Assert.ThrowsAny<Exception>(() => UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: "MediaVerifier.Tests.Engine." + Guid.NewGuid().ToString("N")));

        Assert.True(Program.IsLibraryOpenFailure(error), error.GetType().Name);
        Assert.Contains("99", Program.StartupFailureText(error), StringComparison.Ordinal);
        Assert.Equal(Strings.UnhandledError.StartupLocked, Program.StartupFailureText(new SqliteException("database is locked", 5)));
        Assert.True(Program.IsLibraryOpenFailure(new IOException("in use")));
        Assert.False(Program.IsLibraryOpenFailure(new NullReferenceException()));
    }

    /// <summary>
    /// Plan 01: services are disposed in reverse order of creation. The database went first, so a scan running at exit
    /// could not write its end and was recorded as interrupted, with the next start as its end.
    /// </summary>
    [Fact]
    public void The_database_is_disposed_last()
    {
        using TempDirectory temp = new("shell-owned");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: "MediaVerifier.Tests.Engine." + Guid.NewGuid().ToString("N"));
        List<IDisposable> owned = (List<IDisposable>)typeof(UiServices).GetField("_owned", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(services)!;

        Assert.Equal(
            [typeof(Database), typeof(FfRunner), typeof(EngineScanControl), typeof(UpdateService)],
            owned.Select(o => o.GetType()));
    }

    [Fact]
    public async Task A_failing_start_up_step_does_not_skip_the_steps_after_it()
    {
        using TempDirectory temp = new("shell-steps");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        List<string> ran = [];
        await UiServices.RunStepsAsync(state.Log,
        [
            ("crash recovery", () =>
            {
                ran.Add("recovery");
                return Task.CompletedTask;
            }),
            ("Start with Windows", () => throw new System.Security.SecurityException("blocked")),
            ("scheduled tasks", () => Task.FromException(new Microsoft.Data.Sqlite.SqliteException("busy", 5))),
            ("version record", () =>
            {
                ran.Add("version");
                return Task.CompletedTask;
            }),
        ]);

        Assert.Equal(["recovery", "version"], ran);
    }

    /// <summary>A release whose signed files are missing or invalid showed Update available, leading to a disabled button.</summary>
    [Fact]
    public async Task An_update_that_cannot_be_verified_is_not_offered()
    {
        using RSA key = RSA.Create(2048);
        using RSA other = RSA.Create(2048);
        using UpdateService updates = UpdateCheckTests.Service(UpdateCheckTests.Release(other, _ => { }), key, new Version(0, 1, 0, 1));
        UpdateCheckResult result = await updates.CheckAsync();
        Assert.True(result.IsUpdateAvailable);
        Assert.False(result.CanDownload);

        using TempDirectory temp = new("shell-update");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        using UiServices services = Live(state, temp, new FakeScans(), updates: updates);
        Assert.False(services.UpdateAvailable);
    }

    /// <summary>Per-user registrations point at the installed copy when there is one, never at whichever copy started.</summary>
    [Fact]
    public void Registrations_point_at_the_installed_copy_when_it_exists()
    {
        const string Running = @"D:\Dev\MediaVerifier\bin\Debug\MediaVerifier.exe";
        const string Installed = @"C:\Program Files\MediaVerifier\MediaVerifier.exe";
        Assert.Equal(Installed, RegistrationTarget.Choose(Running, [null, @"C:\Program Files\MediaVerifier\"], path => path == Installed));
        Assert.Equal(Installed, RegistrationTarget.Choose(Running, ["\"C:\\Program Files\\MediaVerifier\""], path => path == Installed));
        Assert.Equal(Running, RegistrationTarget.Choose(Running, [@"C:\Program Files\MediaVerifier\"], _ => false));
        Assert.Equal(Running, RegistrationTarget.Choose(Running, [null, "  "], _ => true));
    }

    /// <summary>Open file on a file that is gone did nothing at all; now a toast says so and offers its folder.</summary>
    [Fact]
    public void Open_file_on_a_file_that_is_gone_says_so()
    {
        ToastContent? toast = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-open");
            Action<ToastContent>? previous = ShellActions.Toast;
            ShellActions.Toast = content => toast = content;
            try
            {
                ShellActions.OpenFile(temp.Combine("Music", "gone.flac"));
                Settle(() => toast is not null);
            }
            finally
            {
                ShellActions.Toast = previous;
            }
        });

        Assert.NotNull(toast);
        Assert.Equal(Strings.Shell.FileNotFound, toast.Title);
        Assert.Equal("gone.flac", toast.Text);
        Assert.Equal(Strings.Menu.ShowInFolder, toast.ActionText);
    }

    /// <summary>The tray's Scan now was greyed with no reason; it now says why as its tooltip.</summary>
    [Fact]
    public void The_trays_Scan_now_says_why_it_is_disabled()
    {
        (bool Enabled, string? Tip) disabled = default;
        (bool Enabled, string? Tip) enabled = default;
        Sta.Run(() =>
        {
            using TrayIcon tray = new(SystemIcons.Application);
            string? reason = Strings.Shell.EngineMissing;
            tray.CannotScanReason = () => reason;
            tray.OnOpening();
            disabled = (tray.ScanNowItem.Enabled, tray.ScanNowItem.ToolTipText);
            reason = null;
            tray.OnOpening();
            enabled = (tray.ScanNowItem.Enabled, tray.ScanNowItem.ToolTipText);
        });

        Assert.Equal((false, Strings.Shell.EngineMissing), disabled);
        Assert.Equal((true, string.Empty), enabled);
    }

    /// <summary>
    /// The command pipe listened from the start of Main, before anything handled its messages, so a link or a scheduled
    /// scan forwarded while the window was starting was dropped (and the launcher logged it as forwarded). The window
    /// now starts the pipe itself, once it can act on what arrives.
    /// </summary>
    [Fact]
    public void A_scheduled_scan_forwarded_while_the_window_starts_is_not_dropped()
    {
        using TempDirectory temp = new("shell-ipc");
        bool sent = false;
        Sta.Run(() =>
        {
            string pipe = "MediaVerifier.Tests.Ipc." + Guid.NewGuid().ToString("N");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp, new FakeScans());
            using IpcServer ipc = new(pipe);
            using MainForm form = new(services, new LaunchOptions(), ipc);

            // The window exists, its message loop has not run yet: a scheduled launch forwards its plan now. (Plan 999
            // does not exist, so nothing runs and nothing asks the window to come forward.)
            sent = Task.Run(() => IpcClient.TrySend(pipe, IpcMessage.ForRunPlan(999), TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();

            // Showing it off-screen recreates the handle (ShowInTaskbar), the moment a message used to be lost.
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Settle(() => false, TimeSpan.FromSeconds(1));
            form.Close();
        });

        Assert.True(sent);
        string log = File.ReadAllText(Path.Combine(temp.CreateAppPaths().LogsDirectory, "app.log"));
        Assert.Contains("Plan 999 was forwarded by a scheduled launch", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Start with Windows (--minimized, tray on) must keep the window hidden. The check waited for the handle to be
    /// missing, but the constructor's layout creates it, so the window showed at every Windows start.
    /// </summary>
    [Fact]
    public void Started_hidden_in_the_notification_area_the_window_stays_hidden_until_opened()
    {
        bool shownAtStart = true;
        bool shownWhenOpened = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("shell-hidden");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            ScreenRenderer.Offscreen = true;
            using MainForm form = new(services);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false;

            // As --minimized with the tray on, without putting an icon in the test machine's notification area.
            typeof(MainForm).GetField("_startHidden", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, true);
            form.Visible = true;
            shownAtStart = form.Visible;
            form.Visible = true;
            shownWhenOpened = form.Visible;
            form.Close();
        });

        Assert.False(shownAtStart);
        Assert.True(shownWhenOpened);
    }

    private static UiServices Live(AppState state, TempDirectory temp, FakeScans scans, bool live = false, UpdateService? updates = null, Func<bool>? background = null)
    {
        Database database = Database.Open(temp.Combine("shell.db"));
        UiServices services = new()
        {
            State = state,
            Database = database,
            Clock = SystemClock.Instance,
            Scans = scans,
            MediaEngine = new MediaEngineInfo(new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), new BundleCheck(BundleState.Ok, null, [], [], null)),
            Data = live ? new LiveUiData(database, () => state.Settings.Scanning, SystemClock.Instance) : new DemoUiData(),
            Updates = updates,
            BackgroundProbe = background ?? (static () => false),
        };
        services.Own(database);
        return services;
    }

    private static long AddRoot(UiServices services, string path, string label, RootRole role = RootRole.Library, bool enabled = true) =>
        services.Database.Roots.AddAsync(new RootRecord { Path = path, Label = label, Role = role, Enabled = enabled }).GetAwaiter().GetResult();

    /// <summary>A key as the window's command keys see it (the modifiers come from the key data, not the keyboard).</summary>
    private static bool Press(MainForm form, Keys keys)
    {
        Message message = Message.Create(form.Handle, WmKeyDown, (IntPtr)(int)(keys & Keys.KeyCode), IntPtr.Zero);
        object[] arguments = [message, keys];
        return (bool)typeof(MainForm).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, arguments)!;
    }

    private static void Key(PopupList list, Keys key)
    {
        Message message = Message.Create(list.Handle, WmKeyDown, (IntPtr)(int)key, IntPtr.Zero);
        _ = list.PreFilterMessage(ref message);
        Application.DoEvents();
    }

    private static void Mouse(Control control, Point point)
    {
        MethodInfo move = typeof(Control).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _ = move.Invoke(control, [new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0)]);
    }

    private static void Settle(Func<bool> done, TimeSpan? limit = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < (limit ?? TimeSpan.FromSeconds(10)))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Application.DoEvents();
    }

    /// <summary>Records the scans the shell starts; its status is whatever the test sets.</summary>
    private sealed class FakeScans : IScanControl
    {
        public EngineStatus Status { get; set; } = EngineStatus.Idle;

        public string ScopeText { get; private set; } = string.Empty;

        public List<ScanRequest> Started { get; } = [];

        public event Action<RunSummary>? RunCompleted
        {
            add { }
            remove { }
        }

        public Task<long> StartAsync(ScanRequest request, string scopeText)
        {
            Started.Add(request);
            ScopeText = scopeText;
            Status = DemoScanControl.Running;
            return Task.FromResult((long)Started.Count);
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Cancel() => Status = EngineStatus.Idle;

        public Task<ReevaluationResult> ReevaluateAsync(IReadOnlyCollection<string> ruleIds) => Task.FromResult(new ReevaluationResult(0, 0, 0));
    }

    /// <summary>A page whose loads wait for the test, so they can finish in any order; a value's type is its kind of data.</summary>
    private sealed class SlowPage(UiServices services) : PageBase(services)
    {
        public Queue<(object Value, Task Gate)> Gates { get; } = new();

        public int GateCount
        {
            get
            {
                lock (Gates)
                {
                    return Gates.Count;
                }
            }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public List<object>? Applied { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Exception? Failure { get; init; }

        public override PageId Id => PageId.Tools;

        protected override object? LoadData()
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            (object value, Task gate) = Dequeue();
            gate.GetAwaiter().GetResult();
            return value;
        }

        protected override void ApplyData(object data) => Applied?.Add(data);

        private (object, Task) Dequeue()
        {
            lock (Gates)
            {
                return Gates.Dequeue();
            }
        }
    }
}
