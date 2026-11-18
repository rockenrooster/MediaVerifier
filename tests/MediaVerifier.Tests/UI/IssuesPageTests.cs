using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MediaVerifier.App;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Pages;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The Issues page on a live library in a temporary profile, off-screen: what its actions act on, the filters its
/// controls show, the drawer's keys, and the repair and undo results (subplans 09 and 12).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class IssuesPageTests
{
    /// <summary>
    /// Ctrl+A over more findings than the list has painted: Quarantine asked about the ~250 cached files while the bar
    /// said every finding was selected. Every action now resolves the whole selection.
    /// </summary>
    [Fact]
    public void Select_all_acts_on_every_selected_finding()
    {
        int targets = 0;
        int cached = 0;
        string? title = null;
        WithIssues(
            database =>
            {
                long root = IssueFixtures.Root(database, @"Q:\Photos", "Photos");
                Many(database, root, 1_200, MediaKind.Photo, "GEN-ZEROFILL");
            },
            (form, services, page) =>
            {
                VirtualList list = Field<VirtualList>(page, "_list");
                list.SelectAll();
                Pump(TimeSpan.FromMilliseconds(100));
                targets = page.TargetIds().Length;
                cached = page.Rows.Count;
                title = Answer<RepairDialog>(Field<SelectionBar>(page, "_selectionBar").Buttons[3].PerformClick, dialog => dialog.DialogResult = DialogResult.Cancel);
            });

        Assert.Equal(1_200, targets);
        Assert.True(cached < 1_200, $"all {cached} rows were cached; the test needs rows that were never painted");
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Repair.TitleQuarantine, string.Format(CultureInfo.CurrentCulture, Strings.Repair.ManyPhotos, UiFormat.Count(1_200))), title);
    }

    /// <summary>
    /// A filter change clears the selection (the bar kept "3 selected" for hidden rows, and Ignore ignored them); Esc
    /// clears it; Up and Down browse files only while the drawer is open (a closed drawer slid open again on every
    /// arrow); leaving the page with the drawer closed does not replay its slide-out over the next page.
    /// </summary>
    [Fact]
    public void Selection_and_drawer_follow_the_filters_and_the_keys()
    {
        bool barAfterFilter = true;
        int selectedAfterFilter = -1;
        bool reopenedClosed = true;
        bool browsedOpen = false;
        int selectedAfterEsc = -1;
        float drawerAfterLeaving = -1;
        List<string> trace = [];
        WithIssues(
            database =>
            {
                long root = IssueFixtures.Root(database, @"Q:\Media", "Media");
                Many(database, root, 30, MediaKind.Video, "COR-INDEX");
            },
            (form, services, page) =>
            {
                VirtualList list = Field<VirtualList>(page, "_list");
                SelectionBar bar = Field<SelectionBar>(page, "_selectionBar");
                for (int i = 1; i <= 3; i++)
                {
                    list.ApplyClickSelection(i, i == 1 ? Keys.None : Keys.Control);
                }

                Assert.True(bar.Visible);
                page.HandleRequest(new IssuesFilterRequest(Severity.Medium, null));
                Settle(() => form.CurrentPageLoading.IsCompleted);
                barAfterFilter = bar.Visible;
                selectedAfterFilter = list.SelectedIds.Count;

                // Open the drawer on a row, close it, then press Down. The filtered list starts with its summary row and
                // the tier's header.
                object source = Field<object>(page, "_source");
                MethodInfo rowAt = source.GetType().GetMethod("RowAt")!;
                int row = Enumerable.Range(0, list.Count).First(i => rowAt.Invoke(source, [i]) is not null);
                FileDetailView view = Field<FileDetailView>(page, "_detail");
                list.FocusRow(row);
                Invoke(page, "OpenDetail", row);
                Settle(() => form.Shell.Drawer.IsOpen && view.Detail is not null);
                long first = view.Detail!.FileId;
                form.SetDrawerContent(null);
                Settle(() => form.Shell.Drawer.Progress == 0);
                PressKey(list, Keys.Down);
                Pump(TimeSpan.FromMilliseconds(400));
                reopenedClosed = form.Shell.Drawer.IsOpen;

                // With the drawer open, Down shows the next file.
                Invoke(page, "OpenDetail", list.FocusedIndex);
                Settle(() => form.Shell.Drawer.IsOpen && view.Detail!.FileId != first);
                long second = view.Detail!.FileId;
                PressKey(list, Keys.Down);
                Settle(() => view.Detail!.FileId != second, TimeSpan.FromSeconds(3));
                browsedOpen = view.Detail!.FileId != second && form.Shell.Drawer.IsOpen;
                form.SetDrawerContent(null);
                Settle(() => form.Shell.Drawer.Progress == 0);
                trace.Add($"closed: progress {form.Shell.Drawer.Progress} open {form.Shell.Drawer.IsOpen}");

                list.ApplyClickSelection(row, Keys.None);
                list.ApplyClickSelection(row + 1, Keys.Control);
                PressKey(list, Keys.Escape);
                selectedAfterEsc = list.SelectedIds.Count;
                trace.Add($"before leaving: progress {form.Shell.Drawer.Progress} open {form.Shell.Drawer.IsOpen}");

                form.Navigate(PageId.Dashboard);
                drawerAfterLeaving = form.Shell.Drawer.Progress;
            });

        Assert.False(barAfterFilter, "the selection bar stayed after the filter changed");
        Assert.Equal(0, selectedAfterFilter);
        Assert.False(reopenedClosed, "Down opened the drawer the user had closed");
        Assert.True(browsedOpen, "Down did not show the next file in the open drawer");
        Assert.Equal(0, selectedAfterEsc);
        Assert.True(drawerAfterLeaving == 0, string.Join(" | ", trace));
    }

    /// <summary>
    /// The kind chips and the narrow layout's Kind drop-down show one filter: opening the drawer (the narrow layout)
    /// showed "Kind" while photos were filtered, and the next choice dropped the filter.
    /// </summary>
    [Fact]
    public void Kind_chips_and_the_kind_drop_down_show_the_same_filter()
    {
        int dropAfterChip = -1;
        bool[] chipsAfterDrop = [];
        IReadOnlyCollection<MediaKind>? kinds = null;
        WithIssues(
            database =>
            {
                long root = IssueFixtures.Root(database, @"Q:\Media", "Media");
                Many(database, root, 5, MediaKind.Photo, "GEN-ZEROFILL");
                Many(database, root, 5, MediaKind.Video, "COR-INDEX");
            },
            (form, services, page) =>
            {
                page.HandleRequest(new IssuesFilterRequest(null, MediaKind.Photo));
                Settle(() => form.CurrentPageLoading.IsCompleted);
                DropDown drop = Field<DropDown>(page, "_kindDrop");
                dropAfterChip = drop.SelectedIndex;
                drop.SelectedIndex = 2;
                Settle(() => form.CurrentPageLoading.IsCompleted);
                chipsAfterDrop = [.. Field<Chip[]>(page, "_kindChips").Select(c => c.On)];
                kinds = Query(page).Filter.Kinds;
            });

        Assert.Equal(1, dropAfterChip);
        Assert.Equal([false, true, false], chipsAfterDrop);
        Assert.Equal([MediaKind.Video], kinds);
    }

    /// <summary>
    /// The Rule list keeps a chosen rule that has no findings under the other filters, as "{title} (0)": it fell back to
    /// "Rule" while the list was still filtered by the rule ("Nothing matches these filters" with no way to see why).
    /// </summary>
    [Fact]
    public void The_rule_list_keeps_a_chosen_rule_with_no_findings_left()
    {
        string? item = null;
        bool highlighted = false;
        IReadOnlyCollection<string>? rules = null;
        WithIssues(
            database =>
            {
                long root = IssueFixtures.Root(database, @"Q:\Media", "Media");
                Many(database, root, 5, MediaKind.Video, "COR-INDEX");
            },
            (form, services, page) =>
            {
                page.HandleRequest(new IssuesFilterRequest(null, null, RuleId: "COR-TRUNCATED"));
                Settle(() => form.CurrentPageLoading.IsCompleted);
                DropDown rule = Field<DropDown>(page, "_rule");
                item = rule.SelectedItem;
                highlighted = rule.Highlighted;
                rules = Query(page).Filter.RuleIds;
            });

        Assert.Equal(RuleCatalog.Get("COR-TRUNCATED").Title + " (0)", item);
        Assert.True(highlighted);
        Assert.Equal(["COR-TRUNCATED"], rules);
    }

    /// <summary>
    /// The Folder list is rebuilt with the watched folders and keeps the choice by folder: filled once, a folder added
    /// with an earlier path shifted every item, and "Music" filtered another folder.
    /// </summary>
    [Fact]
    public void The_folder_list_follows_the_watched_folders()
    {
        string? shown = null;
        long? filtered = null;
        long music = 0;
        WithIssues(
            database =>
            {
                long photos = IssueFixtures.Root(database, @"Q:\Photos", "Photos");
                music = IssueFixtures.Root(database, @"Q:\Zmusic", "Music");
                Many(database, photos, 3, MediaKind.Photo, "GEN-ZEROFILL");
                Many(database, music, 3, MediaKind.Audio, "COR-TRUNCATED");
            },
            (form, services, page) =>
            {
                DropDown folder = Field<DropDown>(page, "_folder");
                folder.SelectedIndex = folder.Items.IndexOf("Music");
                Settle(() => form.CurrentPageLoading.IsCompleted);
                Settle(() => Task.Run(() => IssueFixtures.Root(services.Database, @"Q:\Albums", "Albums")).IsCompleted);
                page.Reload();
                Settle(() => form.CurrentPageLoading.IsCompleted && folder.Items.Count == 4);
                shown = folder.SelectedItem;
                filtered = Query(page).Filter.RootId;
            });

        Assert.Equal("Music", shown);
        Assert.Equal(music, filtered);
    }

    /// <summary>"Show problems found in this scan" for a scan that found nothing says the filter matches nothing and offers Clear filters.</summary>
    [Fact]
    public void Problems_of_one_scan_show_as_a_filter_that_can_be_cleared()
    {
        string? headline = null;
        WithIssues(
            database =>
            {
                long root = IssueFixtures.Root(database, @"Q:\Media", "Media");
                Many(database, root, 3, MediaKind.Video, "COR-INDEX");
            },
            (form, services, page) =>
            {
                page.HandleRequest(new IssuesFilterRequest(null, null, FirstRun: 999));
                Settle(() => form.CurrentPageLoading.IsCompleted);
                headline = Field<EmptyState>(page, "_empty").Headline;
            });

        Assert.Equal(Strings.Issues.FilteredHeadline, headline);
    }

    /// <summary>
    /// Fix… is enabled when the selection shares a fix, with no "need different fixes" tooltip, and lists only those
    /// fixes; otherwise it is disabled with that tooltip.
    /// </summary>
    [Fact]
    public void Fix_is_enabled_only_for_a_shared_fix_and_says_why_not()
    {
        bool sharedEnabled = false;
        string sharedTip = "unset";
        bool mixedEnabled = true;
        string mixedTip = string.Empty;
        WithIssues(
            database =>
            {
                long root = IssueFixtures.Root(database, @"Q:\Media", "Media");
                Many(database, root, 3, MediaKind.Video, "COR-INDEX", Severity.High);
                Many(database, root, 3, MediaKind.Photo, "GEN-ZEROFILL", Severity.Low);
            },
            (form, services, page) =>
            {
                VirtualList list = Field<VirtualList>(page, "_list");
                KitButton fix = Field<SelectionBar>(page, "_selectionBar").Buttons[0];

                // The High group holds the three videos (rows 1-3 after its header).
                list.ApplyClickSelection(1, Keys.None);
                list.ApplyClickSelection(2, Keys.Control);
                Settle(() => fix.Enabled, TimeSpan.FromSeconds(3));
                sharedEnabled = fix.Enabled;
                sharedTip = KitToolTip.Get(fix);

                list.ApplyClickSelection(list.Count - 1, Keys.Control);
                Settle(() => !fix.Enabled, TimeSpan.FromSeconds(3));
                Pump(TimeSpan.FromMilliseconds(300));
                mixedEnabled = fix.Enabled;
                mixedTip = KitToolTip.Get(fix);
            });

        Assert.True(sharedEnabled, "Fix… stayed off for two findings that share Rebuild index");
        Assert.Equal(string.Empty, sharedTip);
        Assert.False(mixedEnabled, "Fix… was on for a video and a photo");
        Assert.Equal(Strings.Issues.NeedDifferentFixes, mixedTip);
    }

    /// <summary>
    /// The new-problems pill counts only the tiers the list shows (with Critical chosen, new Low problems raised a pill
    /// whose Show changed nothing), and one new problem reads "1 new problem".
    /// </summary>
    [Fact]
    public void The_new_problems_pill_counts_only_the_shown_tiers()
    {
        bool lowShown = true;
        string? critical = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("issues-pill");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(1280, 800));
            form.Navigate(PageId.Issues);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            IssuesPage page = Assert.IsType<IssuesPage>(form.CurrentPageControl);
            page.HandleRequest(new IssuesFilterRequest(Severity.Critical, null));
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            FieldInfo data = typeof(IssuesPage).GetField("_data", BindingFlags.NonPublic | BindingFlags.Instance)!;
            IssuesData shown = (IssuesData)data.GetValue(page)!;
            KitButton pill = Field<KitButton>(page, "_newResults");

            data.SetValue(page, shown with { TierCounts = shown.TierCounts with { Low = shown.TierCounts.Low - 3 } });
            ScreenRenderer.WaitFor(page.CheckNewResultsAsync());
            lowShown = pill.Visible;

            IssuesData lowered = (IssuesData)data.GetValue(page)!;
            data.SetValue(page, lowered with { TierCounts = lowered.TierCounts with { Critical = lowered.TierCounts.Critical - 1 } });
            ScreenRenderer.WaitFor(page.CheckNewResultsAsync());
            critical = pill.Visible ? pill.Text : null;
            form.Close();
        });

        Assert.False(lowShown, "new Low problems raised the pill on a Critical-only list");
        Assert.Equal(Strings.Issues.NewResultOne, critical);
    }

    /// <summary>
    /// Dialog A skips the files that changed since the scan by file, not by name (every other selected cover.jpg was
    /// skipped with the one that changed), the result toast names the things, and Undo goes on after a file it cannot
    /// bring back (`??=` stopped at the first failure and left the rest undone for good).
    /// </summary>
    [Fact]
    public void Changed_files_are_skipped_by_file_and_undo_goes_on_after_a_failure()
    {
        int skipped = -1;
        bool unchangedMoved = false;
        bool changedKept = false;
        List<string> titles = [];
        bool restored = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("issues-repair");
            string library = Directory.CreateDirectory(temp.Combine("Library")).FullName;
            DateTime written = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            string[] paths =
            [
                Write(Path.Combine(library, "Album A", "cover.jpg"), written),
                Write(Path.Combine(library, "Album B", "cover.jpg"), written),
                Write(Path.Combine(library, "Album C", "front.jpg"), written),
                Write(Path.Combine(library, "Album D", "back.jpg"), written),
            ];
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: OwnEngine());
            long[] files = Db(() =>
            {
                long root = IssueFixtures.Root(services.Database, library, "Library");
                return paths.Select(p => IssueFixtures.File(services.Database, root, Path.GetFileName(Path.GetDirectoryName(p)!), Path.GetFileName(p), MediaKind.Photo, new FileInfo(p).Length, written.Ticks)).ToArray();
            });

            // The cover in Album B was edited after the scan.
            File.SetLastWriteTimeUtc(paths[1], written.AddMinutes(5));
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            List<ToastContent> toasts = [];
            services.ShowToast = toasts.Add;

            Answer<RepairDialog>(() => UiTask.Run(Remediator.RunAsync(services, form, files[..2], ActionIds.Quarantine)), dialog =>
            {
                skipped = Descendants(dialog).OfType<InfoBar>().Count(b => b.Text == Strings.Repair.ChangedSkippedOne);
                dialog.DialogResult = DialogResult.OK;
            });
            Settle(() => toasts.Any(t => t.Action is not null && t.ActionText == Strings.Dialogs.Undo), TimeSpan.FromSeconds(20));
            unchangedMoved = !File.Exists(paths[0]);
            changedKept = File.Exists(paths[1]);
            titles.AddRange(toasts.Select(t => t.Title));

            // A batch of two; the kept original of one is deleted before Undo.
            toasts.Clear();
            Answer<RepairDialog>(() => UiTask.Run(Remediator.RunAsync(services, form, files[2..], ActionIds.Quarantine)), dialog => dialog.DialogResult = DialogResult.OK);
            Settle(() => toasts.Any(t => t.ActionText == Strings.Dialogs.Undo), TimeSpan.FromSeconds(20));
            OperationRecord gone = Db(() => services.Database.Journal.List(10)).First(o => o.FileId == files[3] && o.Status == OperationStatus.Done);
            File.Delete(gone.BackupPath!);
            ToastContent result = toasts.First(t => t.ActionText == Strings.Dialogs.Undo);
            toasts.Clear();
            result.Action!();
            Settle(() => toasts.Count >= 2, TimeSpan.FromSeconds(20));
            restored = File.Exists(paths[2]);
            titles.AddRange(toasts.Select(t => t.Title));
            form.Close();
        });

        Assert.Equal(1, skipped);
        Assert.True(unchangedMoved, "the unchanged cover.jpg was skipped with the changed one");
        Assert.True(changedKept, "the changed file was moved although the dialog said it is skipped");
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Strings.Repair.ResultQuarantine, Strings.Repair.OnePhoto), titles);
        Assert.True(restored, "Undo stopped at the file it could not bring back: " + string.Join(" | ", titles));
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Strings.Repair.Undone, Strings.Repair.OneFile), titles);
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, Strings.Repair.FailedFor, Strings.Dialogs.Undo, Strings.Repair.OneFile), titles);
    }

    /// <summary>When every chosen file changed since the scan, dialog A keeps its action off (it ran nothing and said nothing).</summary>
    [Fact]
    public void Dialog_a_has_nothing_to_run_when_every_file_changed()
    {
        bool enabled = true;
        Sta.Run(() =>
        {
            RepairPreview preview = new("Move 1 photo to quarantine?", "p", IconName.Photo, "1 photo, 4 KB in total", "About 1 second", null, true, 1, ["cover.jpg"], false, Strings.Repair.VerbQuarantine, NothingToRun: true);
            using RepairDialog dialog = RepairDialog.Create(preview);
            enabled = Descendants(dialog).OfType<KitButton>().Single(b => b.Text == Strings.Repair.VerbQuarantine).Enabled;
        });

        Assert.False(enabled);
    }

    /// <summary>
    /// Open file on a file that is gone says so (it did nothing at all), and Ctrl+Z with nothing to undo says that.
    /// Neither opens a program or touches the clipboard.
    /// </summary>
    [Fact]
    public void Open_file_and_undo_say_when_there_is_nothing_to_do()
    {
        List<ToastContent> toasts = [];
        WithIssues(
            database => IssueFixtures.Root(database, @"Q:\Media", "Media"),
            (form, services, page) =>
            {
                services.ShowToast = toasts.Add;
                Task open = DrawerActions.OpenFileAsync(services, Path.Combine(Path.GetTempPath(), "mv-gone-" + Guid.NewGuid().ToString("N") + ".mp4"));
                Settle(() => open.IsCompleted);
                Task undo = Remediator.UndoLatestAsync(services, form);
                Settle(() => undo.IsCompleted);
            });

        Assert.Equal([Strings.Drawer.OpenFailed, Strings.Repair.NothingToUndo], toasts.Select(t => t.Title));
        Assert.Equal(MediaVerifier.Core.CoreStrings.Remediation.Missing, toasts[0].Text);
        Assert.Equal(Strings.Drawer.PathCopied, DrawerActions.PathsCopied(1));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Drawer.PathsCopied, "3"), DrawerActions.PathsCopied(3));
    }

    /// <summary>
    /// A file stopped with the start toast's Stop is skipped, not reported as a failure; a second batch while one runs
    /// is refused with a word instead of putting the same files through twice.
    /// </summary>
    [Fact]
    public void Stopped_files_are_skipped_and_a_second_batch_waits_its_turn()
    {
        List<ToastContent> toasts = [];
        bool second = true;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("issues-report");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using Form owner = new();
            services.ShowToast = toasts.Add;
            FileRecord[] files = [.. Enumerable.Range(1, 3).Select(i => new FileRecord { Id = i, Name = $"IMG_{i}.jpg", Ext = "jpg", MediaKind = MediaKind.Photo })];
            OperationOutcome[] outcomes = [new(1, 1, OperationStatus.Done, null, null), new(2, 2, OperationStatus.Cancelled, MediaVerifier.Core.CoreStrings.Remediation.Cancelled, null)];
            Remediator.Report(services, owner, ActionCatalog.Create(ActionIds.Quarantine, MediaVerifier.Core.Engine.WindowsFileSystem.Instance)!, files, outcomes, 1);

            // A batch in progress: a second one is refused before anything is asked.
            FieldInfo running = typeof(Remediator).GetField("s_running", BindingFlags.NonPublic | BindingFlags.Static)!;
            using CancellationTokenSource busy = new();
            running.SetValue(null, busy);
            try
            {
                using UiServices live = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: OwnEngine());
                live.ShowToast = toasts.Add;
                Task<bool> run = Remediator.RunAsync(live, owner, [1], ActionIds.Quarantine);
                Settle(() => run.IsCompleted);
                second = run.Result;
            }
            finally
            {
                running.SetValue(null, null);
            }
        });

        Assert.Equal(
            [
                string.Format(CultureInfo.CurrentCulture, Strings.Repair.ResultFor, Strings.Repair.ResultQuarantine, Strings.Repair.OnePhoto),
                Strings.Repair.AlreadyRunning,
            ],
            toasts.Select(t => t.Title));
        Assert.StartsWith(string.Format(CultureInfo.CurrentCulture, Strings.Repair.Skipped, "2"), toasts[0].Text, StringComparison.Ordinal);
        Assert.False(second);
    }

    private static void WithIssues(Action<Database> fill, Action<MainForm, UiServices, IssuesPage> body)
    {
        Sta.Run(() =>
        {
            using TempDirectory temp = new("issues-page");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: OwnEngine());
            Db(() =>
            {
                fill(services.Database);
                return true;
            });
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            services.ShowToast = _ => { };
            form.Navigate(PageId.Issues);
            Settle(() => form.CurrentPageLoading.IsCompleted);
            Pump(TimeSpan.FromMilliseconds(200));
            body(form, services, Assert.IsType<IssuesPage>(form.CurrentPageControl));
            form.Close();
        });
    }

    /// <summary>Files of one kind, each with one finding of <paramref name="ruleId"/>, written in one operation.</summary>
    private static void Many(Database database, long root, int count, MediaKind kind, string ruleId, Severity severity = Severity.Medium)
    {
        string ext = kind switch { MediaKind.Photo => "jpg", MediaKind.Video => "mp4", _ => "flac" };
        RuleDefinition rule = RuleCatalog.Get(ruleId);
        database.Writer.ExecuteAsync(
            context =>
            {
                long dir = FileIndex.InternDir(context, root, kind.ToString());
                SqliteCommand file = context.Command(
                    "INSERT INTO files(root_id, dir_id, name, ext, media_kind, size, mtime_utc, state, worst_severity, open_findings, scanned_depth, first_seen_utc, last_seen_run) " +
                    "VALUES ($root, $dir, $name, $ext, $kind, 4096, 0, $state, $severity, 1, 2, $now, 1)");
                SqliteCommand finding = context.Command(
                    "INSERT INTO findings(file_id, rule_id, category, severity, confidence, summary, evidence_json, status, first_run, last_run, first_utc, last_utc, media_kind, file_name) " +
                    "VALUES ($file, $rule, $category, $severity, 1.0, $summary, '{}', 0, 1, 1, $now, $now, $kind, $name)");
                for (int i = 0; i < count; i++)
                {
                    string name = string.Create(CultureInfo.InvariantCulture, $"{kind} {i:D5}.{ext}");
                    file.Parameters.Clear();
                    file.Parameters.AddWithValue("$root", root);
                    file.Parameters.AddWithValue("$dir", dir);
                    file.Parameters.AddWithValue("$name", name);
                    file.Parameters.AddWithValue("$ext", ext);
                    file.Parameters.AddWithValue("$kind", (int)kind);
                    file.Parameters.AddWithValue("$severity", (int)severity);
                    file.Parameters.AddWithValue("$state", (int)FileState.Issues);
                    file.Parameters.AddWithValue("$now", context.NowTicks);
                    long id = context.Insert(file);
                    finding.Parameters.Clear();
                    finding.Parameters.AddWithValue("$file", id);
                    finding.Parameters.AddWithValue("$rule", ruleId);
                    finding.Parameters.AddWithValue("$category", (int)rule.Category);
                    finding.Parameters.AddWithValue("$severity", (int)severity);
                    finding.Parameters.AddWithValue("$summary", rule.Title);
                    finding.Parameters.AddWithValue("$now", context.NowTicks);
                    finding.Parameters.AddWithValue("$kind", (int)kind);
                    finding.Parameters.AddWithValue("$name", name);
                    finding.ExecuteNonQuery();
                }
            },
            ChangeFlags.Files | ChangeFlags.Findings).GetAwaiter().GetResult();
    }

    private static string Write(string path, DateTime writtenUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private static IssuesQuery Query(IssuesPage page) => Field<IssuesQuery>(page, "_query");

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static void Invoke(object owner, string method, params object[] args) =>
        owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, args);

    /// <summary>A key on a control, through its own key handling (the keyboard's path).</summary>
    private static void PressKey(Control control, Keys key) =>
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, [new KeyEventArgs(key)]);

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

    /// <summary>Database work made on a pool thread: on the UI thread it would trip the guard that keeps the app off it.</summary>
    private static T Db<T>(Func<T> work) => Task.Run(work).GetAwaiter().GetResult();
}
