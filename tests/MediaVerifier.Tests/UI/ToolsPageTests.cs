using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MediaVerifier.App;
using MediaVerifier.Diagnostics;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Lists;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Tools → Quarantine and Operations through the real page on live services in a temporary profile: what the
/// selection actions act on, and what they report. The journal rows are written directly, so no FFmpeg is needed.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ToolsPageTests
{
    /// <summary>
    /// The audit's trace: with a row chosen in Quarantine, a newer original arrives and the page reloads. The selection
    /// stays on the original the user chose (it was kept by row number and moved to another file), and Delete for good
    /// deletes that one alone and says so.
    /// </summary>
    [Fact]
    public void Delete_for_good_after_a_reload_deletes_only_the_chosen_original()
    {
        List<string> chosen = [];
        bool aGone = false;
        bool othersKept = false;
        string? question = null;
        List<string> toasts = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("tools-selection");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            services.ShowToast = toast => toasts.Add(toast.Title + " | " + toast.Text);
            string a = Kept(services, temp, "a.flac");
            string b = Kept(services, temp, "b.flac");

            ToolsPage tools = OpenView(form, 0);
            VirtualList table = Table(tools);
            Assert.Equal(2, table.Count);

            // Rows are newest first: b.flac, a.flac. Choose a.flac (row 1).
            table.ApplyClickSelection(1, Keys.None);
            Assert.Equal("a.flac", Path.GetFileName(Assert.Single(tools.ChosenKept()).Operation.SourcePath));

            // A newer original arrives (Move to quarantine on Issues), and the page reloads: a.flac is now row 2.
            string c = Kept(services, temp, "c.flac");
            tools.Reload();
            Settle(() => tools.Loading.IsCompleted && table.Count == 3);
            chosen.AddRange(tools.ChosenKept().Select(e => Path.GetFileName(e.Operation.SourcePath)));

            question = Answer<MessageDialog>(() => Find<KitButton>(tools, x => x.Text == Strings.Tools.DeleteForGood && x.Visible).PerformClick(), d => d.DialogResult = DialogResult.OK);
            Settle(() => toasts.Count > 0);
            Settle(() => tools.Loading.IsCompleted);
            aGone = !File.Exists(a);
            othersKept = File.Exists(b) && File.Exists(c);
            form.Close();
        });

        Assert.Equal(["a.flac"], chosen);
        Assert.Equal("Delete a.flac for good?", question);
        Assert.True(aGone, "The chosen original is still there.");
        Assert.True(othersKept, "An original the user did not choose was deleted.");
        Assert.Contains(toasts, t => t.StartsWith(Strings.Tools.DeletedForGood, StringComparison.Ordinal));
    }

    /// <summary>A selection made in Quarantine does not follow the user into Operations (Undo was enabled for a batch never picked).</summary>
    [Fact]
    public void Switching_views_clears_the_selection()
    {
        int selectedAfterSwitch = -1;
        bool undoEnabled = true;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("tools-views");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            services.ShowToast = _ => { };
            _ = Kept(services, temp, "a.flac");
            _ = Kept(services, temp, "b.flac");

            ToolsPage tools = OpenView(form, 0);
            VirtualList table = Table(tools);
            table.ApplyClickSelection(1, Keys.None);
            Invoke(tools, "ShowView", -1);
            Invoke(tools, "ShowView", 1);
            selectedAfterSwitch = table.SelectedIds.Count;
            undoEnabled = Find<KitButton>(tools, x => x.Text == Strings.Tools.Undo).Enabled;
            form.Close();
        });

        Assert.Equal(0, selectedAfterSwitch);
        Assert.False(undoEnabled, "Undo is enabled although no operation was chosen.");
    }

    /// <summary>
    /// Restore tries every chosen original: the first one cannot come back (its name is taken again), the second still
    /// does. It stopped at the first failure, and the rest stayed in quarantine.
    /// </summary>
    [Fact]
    public void Restore_tries_every_chosen_original_and_reports_the_failure()
    {
        bool olderBack = false;
        List<ToastContent> toasts = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("tools-restore");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: OwnEngine());
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            services.ShowToast = toasts.Add;
            string older = Kept(services, temp, "older.flac");
            string newer = Kept(services, temp, "newer.flac");

            // The newer original's name is taken again, so it cannot come back; it is listed (and tried) first.
            string taken = Path.Combine(temp.Combine("Music"), "newer.flac");
            File.WriteAllText(taken, "a new file");

            ToolsPage tools = OpenView(form, 0);
            VirtualList table = Table(tools);
            table.SelectAll();
            Assert.Equal(2, tools.ChosenKept().Count);
            Find<KitButton>(tools, x => x.Text == Strings.Tools.Restore && x.Visible).PerformClick();
            Settle(() => toasts.Count >= 2, TimeSpan.FromSeconds(15));
            olderBack = File.Exists(Path.Combine(temp.Combine("Music"), "older.flac")) && !File.Exists(older);
            Assert.True(File.Exists(newer), "The original that could not come back is gone from quarantine.");
            form.Close();
        });

        Assert.True(olderBack, "Restore stopped at the first failure: " + string.Join(" | ", toasts.Select(t => t.Title)));
        Assert.Contains(toasts, t => t.Level == MediaVerifier.Core.Model.Severity.None && t.Title == "Restored for 1 file");
        Assert.Contains(toasts, t => t.Level == MediaVerifier.Core.Model.Severity.Critical && t.Title == "Restore failed for 1 file" && t.Text.StartsWith("newer.flac: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// An original moved to quarantine: the file in the root's quarantine folder and its Done journal row (as
    /// ACT-QUARANTINE writes them). Returns the kept file's path.
    /// </summary>
    private static string Kept(UiServices services, TempDirectory temp, string name)
    {
        string music = Directory.CreateDirectory(temp.Combine("Music")).FullName;
        string source = Path.Combine(music, name);
        string folder = Directory.CreateDirectory(Path.Combine(music, OperationRunner.QuarantineFolder, Guid.NewGuid().ToString("N"))).FullName;
        string backup = Path.Combine(folder, name);
        File.WriteAllText(backup, name);
        string undo = JsonSerializer.Serialize(new UndoData("Move", source, null, backup, 0, 0, null, null, null, null), RemediationJsonContext.Default.UndoData);
        OperationRecord record = new() { BatchId = DateTime.UtcNow.Ticks, Utc = DateTime.UtcNow.Ticks, ActionId = "ACT-QUARANTINE", Status = OperationStatus.Done, SourcePath = source, BackupPath = backup, UndoJson = undo };
        Task<long> add = services.Database.Journal.AddAsync(record);
        Settle(() => add.IsCompleted);
        Thread.Sleep(2);
        return backup;
    }

    private static ToolsPage OpenView(MainForm form, int view)
    {
        form.Navigate(PageId.Tools);
        Settle(() => form.CurrentPageLoading.IsCompleted);
        ToolsPage tools = (ToolsPage)form.CurrentPageControl!;
        Settle(() => tools.Data is not null);
        Invoke(tools, "ShowView", view);
        return tools;
    }

    private static VirtualList Table(ToolsPage tools) => Descendants(tools).OfType<VirtualList>().First(l => l.Visible);

    private static void Invoke(Control target, string method, int argument)
    {
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, [argument]);
        Pump(TimeSpan.FromMilliseconds(100));
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
