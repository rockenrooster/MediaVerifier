using System.Diagnostics;
using MediaVerifier.App;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Every way into "Add folder" opens the dialog: the Library page's button and the first-run Dashboard button. Each
/// opens a modal dialog with its own message loop, so a timer running inside that loop records the dialog and
/// closes it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AddFolderTests
{
    [Fact]
    public void The_library_button_opens_the_add_folder_dialog()
    {
        string? opened = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("add-folder");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            form.Navigate(PageId.Library);
            Settle(form.CurrentPageLoading);
            KitButton add = Find<KitButton>(form.CurrentPageControl!, b => b.Text == Strings.Library.AddFolder);
            opened = ClickAndCatchDialog(add.PerformClick);
            form.Close();
        });
        Assert.Equal(Strings.Library.AddTitle, opened);
    }

    [Fact]
    public void Showing_the_dialog_does_not_fail()
    {
        Exception? failure = null;
        string? opened = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("add-folder");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Task shown = Task.CompletedTask;
            opened = ClickAndCatchDialog(() => shown = AddFolderDialog.ShowAsync(form, services, null, null));
            Settle(shown);
            failure = shown.Exception?.GetBaseException();
            form.Close();
        });
        Assert.True(failure is null, failure?.ToString());
        Assert.Equal(Strings.Library.AddTitle, opened);
    }

    /// <summary>
    /// A network folder that is offline: Directory.Exists on it took 136 s on the owner's PC. Typing it into Add folder
    /// must not wait for that on the UI thread (it did, at every key), and the picker gives up on it within 2 seconds.
    /// 192.0.2.1 is reserved for documentation, so nothing answers there.
    /// </summary>
    [Fact]
    public async Task An_offline_network_folder_never_holds_the_window()
    {
        const string Offline = @"\\192.0.2.1\share\Music";
        long typing = -1;
        bool addEnabled = true;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("add-folder");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            using System.Windows.Forms.Timer timer = new() { Interval = 50 };
            timer.Tick += (_, _) =>
            {
                if (Application.OpenForms.OfType<AddFolderDialog>().FirstOrDefault() is AddFolderDialog dialog && typing < 0)
                {
                    PathBox box = Find<PathBox>(dialog, _ => true);
                    Stopwatch watch = Stopwatch.StartNew();
                    box.Text = Offline;
                    typing = watch.ElapsedMilliseconds;
                    addEnabled = Find<KitButton>(dialog, b => b.Text == Strings.Library.AddFolder).Enabled;
                    dialog.Close();
                }
            };
            timer.Start();
            Task shown = AddFolderDialog.ShowAsync(form, services, null, null);
            Settle(shown);
            timer.Stop();
            form.Close();
        });

        Assert.InRange(typing, 0, 500);
        Assert.False(addEnabled);

        Stopwatch picker = Stopwatch.StartNew();
        string start = await PathBox.StartFolderAsync(Offline, TimeSpan.FromSeconds(2));
        Assert.Equal(string.Empty, start);
        Assert.InRange(picker.ElapsedMilliseconds, 0, 5000);

        using TempDirectory folder = new("picker");
        Assert.Equal(folder.Path, await PathBox.StartFolderAsync(folder.Path, TimeSpan.FromSeconds(2)));
    }

    private static string? ClickAndCatchDialog(Action click)
    {
        string? title = null;
        using System.Windows.Forms.Timer timer = new() { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            foreach (Form open in Application.OpenForms.Cast<Form>().ToList())
            {
                if (open is AddFolderDialog dialog)
                {
                    title = dialog.Text;
                    dialog.Close();
                }
            }
        };
        timer.Start();
        click();
        Pump(TimeSpan.FromSeconds(2));
        timer.Stop();
        return title;
    }

    private static T Find<T>(Control root, Func<T, bool> match)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T found && match(found))
            {
                return found;
            }

            if (child.HasChildren && FindOrNull(child, match) is T nested)
            {
                return nested;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} matched in {root.GetType().Name}.");
    }

    private static T? FindOrNull<T>(Control root, Func<T, bool> match)
        where T : Control
    {
        try
        {
            return Find(root, match);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void Settle(Task task)
    {
        Stopwatch limit = Stopwatch.StartNew();
        while (!task.IsCompleted && limit.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private static void Pump(TimeSpan time)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < time)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }
}
