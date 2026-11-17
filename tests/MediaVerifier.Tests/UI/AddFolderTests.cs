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
