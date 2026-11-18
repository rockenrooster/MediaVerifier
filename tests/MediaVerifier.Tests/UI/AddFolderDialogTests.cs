using System.Diagnostics;
using System.Reflection;
using MediaVerifier.App;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The Add and Edit folder dialog's checks on live services in a temporary profile: what it accepts, what it counts,
/// and how it stores a folder. A timer inside the dialog's own message loop drives it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AddFolderDialogTests
{
    /// <summary>"E:" is the current folder on drive E; a drive's root keeps its separator (a whole-drive library scanned the app's folder).</summary>
    [Fact]
    public void A_drive_root_keeps_its_separator()
    {
        Assert.Equal(@"E:\", AddFolderDialog.Normalize("E:"));
        Assert.Equal(@"E:\", AddFolderDialog.Normalize(@"E:\"));
        Assert.Equal(@"D:\Media", AddFolderDialog.Normalize(@"D:\Media\"));
        Assert.Equal(@"\\server\share", AddFolderDialog.Normalize(@"\\server\share\"));
    }

    /// <summary>A folder that is not connected can still be renamed: Save waited for the folder and then said it did not exist.</summary>
    [Fact]
    public void Editing_a_folder_that_is_not_connected_can_be_saved()
    {
        bool saveEnabled = false;
        string? label = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("edit-offline");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Task<long> add = services.Database.Roots.AddAsync(new RootRecord { Path = temp.Combine("Unplugged"), Label = "Backup drive", Role = RootRole.Backup, MediaMask = MediaKindMask.All });
            Settle(() => add.IsCompleted);

            Drive(AddFolderDialog.ShowAsync(form, services, add.Result, null), dialog =>
            {
                KitButton save = Find<KitButton>(dialog, b => b.Text == Strings.Kit.Save);
                Settle(() => save.Enabled, TimeSpan.FromSeconds(5));
                saveEnabled = save.Enabled;
                Find<KitTextBox>(dialog, b => b.Text == "Backup drive").Text = "Old backups";
                save.PerformClick();
            });
            label = Task.Run(() => services.Database.Roots.Get(add.Result)?.Label).GetAwaiter().GetResult();
            form.Close();
        });

        Assert.True(saveEnabled, "Save stayed disabled for a folder that is not connected.");
        Assert.Equal("Old backups", label);
    }

    /// <summary>A folder holding a watched folder is refused: that folder's files were indexed and scanned twice.</summary>
    [Fact]
    public void A_folder_that_contains_a_watched_folder_is_refused()
    {
        string? error = null;
        bool addEnabled = true;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("add-contains");
            string media = Directory.CreateDirectory(temp.Combine("Media")).FullName;
            string music = Directory.CreateDirectory(Path.Combine(media, "Music")).FullName;
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Task<long> add = services.Database.Roots.AddAsync(new RootRecord { Path = music, Label = "Music", Role = RootRole.Library, MediaMask = MediaKindMask.All });
            Settle(() => add.IsCompleted);

            Drive(AddFolderDialog.ShowAsync(form, services, null, media), dialog =>
            {
                Settle(() => Field<string>(dialog, "_error") is not null, TimeSpan.FromSeconds(5));
                error = Field<string>(dialog, "_error");
                addEnabled = Find<KitButton>(dialog, b => b.Text == Strings.Library.AddFolder).Enabled;
                dialog.Close();
            });
            form.Close();
        });

        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Library.ErrorContains, "Music"), error);
        Assert.False(addEnabled);
    }

    /// <summary>The count follows the kinds ticked: with Photos unticked, the photo is not counted (it still was).</summary>
    [Fact]
    public void The_count_follows_the_kinds_ticked()
    {
        string? all = null;
        string? withoutPhotos = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("add-kinds");
            string folder = Directory.CreateDirectory(temp.Combine("Mixed")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "song.flac"), new byte[10]);
            File.WriteAllBytes(Path.Combine(folder, "photo.jpg"), new byte[10]);
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = Live(state, temp);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);

            Drive(AddFolderDialog.ShowAsync(form, services, null, folder), dialog =>
            {
                InfoBar bar = Find<InfoBar>(dialog, _ => true);
                string two = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Library.Found, UiFormat.Count(2), UiFormat.Size(20));
                Settle(() => bar.Text == two, TimeSpan.FromSeconds(5));
                all = bar.Text;
                Find<KitCheckBox>(dialog, b => b.Text == Strings.Kinds.Photos).Checked = false;
                string one = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Library.Found, UiFormat.Count(1), UiFormat.Size(10));
                Settle(() => bar.Text == one, TimeSpan.FromSeconds(5));
                withoutPhotos = bar.Text;
                dialog.Close();
            });
            form.Close();
        });

        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Library.Found, UiFormat.Count(2), UiFormat.Size(20)), all);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Library.Found, UiFormat.Count(1), UiFormat.Size(10)), withoutPhotos);
    }

    private static UiServices Live(AppState state, TempDirectory temp) =>
        UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N"));

    /// <summary>Shows the dialog and runs <paramref name="act"/> inside its message loop once it is visible.</summary>
    private static void Drive(Task shown, Action<AddFolderDialog> act)
    {
        bool done = false;
        using System.Windows.Forms.Timer timer = new() { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            if (!done && Application.OpenForms.OfType<AddFolderDialog>().FirstOrDefault() is AddFolderDialog dialog && dialog.Visible)
            {
                done = true;
                timer.Stop();
                act(dialog);
            }
        };
        timer.Start();
        Settle(() => shown.IsCompleted, TimeSpan.FromSeconds(30));
        timer.Stop();
        if (shown.Exception is AggregateException failure)
        {
            throw failure.GetBaseException();
        }
    }

    private static T? Field<T>(object target, string name) => (T?)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

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

    private static void Settle(Func<bool> done, TimeSpan? limit = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < (limit ?? TimeSpan.FromSeconds(10)))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
