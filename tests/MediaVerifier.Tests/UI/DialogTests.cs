using System.Diagnostics;
using MediaVerifier.App;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Every dialog opens through its real entry point over the main window and closes with the answer given: a dialog
/// that throws while it is built (as Add folder did in v0.1.0.1) shows nothing and fails here.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DialogTests
{
    [Fact]
    public void Every_dialog_opens_and_cancels()
    {
        List<string> titles = [];
        Sta.Run(() => WithWindow((form, services) =>
        {
            titles.Add(Answer(() => MessageDialog.Confirm(form, "Remove Music?", "The files stay on disk.", Strings.Kit.Cancel, "Remove", destructive: true), DialogResult.Cancel, out object? removed));
            Assert.Equal(false, removed);
            titles.Add(Answer(() => { MessageDialog.Show(form, "Report saved", "The report is in Documents.", "OK"); return null; }, DialogResult.OK, out _));
            titles.Add(Answer(() => AcceptDialog.Ask(form), DialogResult.Cancel, out object? accepted));
            Assert.Equal(false, accepted);
            ScanEstimate estimate = new(120_000, 900L * 1024 * 1024 * 1024, "Music, Videos", TimeSpan.FromMinutes(4), TimeSpan.FromHours(2), TimeSpan.FromHours(9));
            titles.Add(Answer(() => LargeScanDialog.Ask(form, estimate, ScanDepth.Standard), DialogResult.Cancel, out object? depth));
            Assert.Null(depth);
            titles.Add(Answer(() => ListEditDialog.Ask(form, "File types", "One per line", [("Photos", ["jpg"], ["jpg", "png"])]), DialogResult.Cancel, out object? lists));
            Assert.Null(lists);
            RepairPreview preview = new("Rebuild the index of 2 videos?", "Each file is rewritten.", IconName.Video, "2 videos, 1.2 GB", "About a minute", "Quarantine", true, 0, [], false, "Rebuild");
            titles.Add(Answer(() => RepairDialog.Ask(form, preview), DialogResult.Cancel, out object? repaired));
            Assert.Equal(false, repaired);
            titles.Add(Answer(() => ScheduleEditorDialog.Ask(form, "Weekly", new PlanDefinition(), [], DateOnly.FromDateTime(DateTime.Now), editing: false, (_, _) => Task.FromResult<string?>(null)), DialogResult.Cancel, out object? stored));
            Assert.Equal(false, stored);
            titles.Add(Answer(() => { CreateReportDialog.Show(form, services); return null; }, DialogResult.Cancel, out _));
        }));

        Assert.Equal(8, titles.Count);
        Assert.All(titles, title => Assert.False(string.IsNullOrEmpty(title)));
    }

    [Fact]
    public void A_confirmation_returns_true_when_its_action_is_chosen()
    {
        object? confirmed = null;
        Sta.Run(() => WithWindow((form, _) => Answer(() => MessageDialog.Confirm(form, "Install version 0.1.0.3?", "MediaVerifier closes while it installs.", Strings.Kit.Cancel, "Install", destructive: false), DialogResult.OK, out confirmed)));
        Assert.Equal(true, confirmed);
    }

    [Fact]
    public void The_list_editor_returns_what_was_typed()
    {
        object? lists = null;
        Sta.Run(() => WithWindow((form, _) => Answer(
            () => ListEditDialog.Ask(form, "File types", "One per line", [("Photos", ["jpg"], ["jpg", "png"])]),
            dialog =>
            {
                Find<KitTextBox>(dialog).Text = "jpg\r\nheic";
                Find<KitButton>(dialog, b => b.Kind == ButtonKind.Primary).PerformClick();
            },
            out lists)));
        List<string>[] result = Assert.IsType<List<string>[]>(lists);
        Assert.Equal(["jpg", "heic"], result[0]);
    }

    [Fact]
    public void The_schedule_editor_stays_open_with_the_reason_Windows_refused_and_closes_once_stored()
    {
        List<string> stores = [];
        string? reason = null;
        object? stored = null;
        Sta.Run(() => WithWindow((form, _) => Answer(
            () => ScheduleEditorDialog.Ask(form, "Weekly", PlanDefinition.Templates[1].Definition, [], DateOnly.FromDateTime(DateTime.Now), editing: false, (name, _) =>
            {
                stores.Add(name);
                return Task.FromResult<string?>(stores.Count == 1 ? "Access is denied." : null);
            }),
            dialog =>
            {
                KitButton save = Find<KitButton>(dialog, b => b.Kind == ButtonKind.Primary);
                save.PerformClick();
                reason = Find<InfoBar>(dialog, b => b.Visible).Text;
                if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
                {
                    ScreenRenderer.Save(dialog, Path.Combine(folder, "schedule-refused.png"));
                }

                save.PerformClick();
            },
            out stored)));

        Assert.Equal(["Weekly", "Weekly"], stores);
        Assert.Equal("Windows would not accept this schedule: Access is denied.", reason);
        Assert.Equal(true, stored);
    }

    private static void WithWindow(Action<MainForm, UiServices> body)
    {
        using TempDirectory temp = new("dialogs");
        using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        using UiServices services = DemoUiData.CreateServices(state, temp.Path);
        using MainForm form = new(services);
        ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
        body(form, services);
        form.Close();
    }

    private static string Answer(Func<object?> show, DialogResult answer, out object? result) =>
        Answer(show, dialog => dialog.DialogResult = answer, out result);

    /// <summary>Runs <paramref name="show"/>; a timer in the dialog's message loop answers it. Returns the dialog's title.</summary>
    private static string Answer(Func<object?> show, Action<Form> answer, out object? result)
    {
        string? title = null;
        using System.Windows.Forms.Timer timer = new() { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            if (title is null && Application.OpenForms.OfType<KitDialog>().FirstOrDefault(d => d.Visible) is KitDialog dialog)
            {
                title = dialog.Text;
                if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_SAVE_RENDERS") is string folder)
                {
                    ScreenRenderer.Save(dialog, Path.Combine(folder, "dialog-" + string.Concat(title.Where(char.IsLetterOrDigit)) + ".png"));
                }

                answer(dialog);
            }
        };
        timer.Start();
        result = show();
        timer.Stop();
        Stopwatch limit = Stopwatch.StartNew();
        while (Application.OpenForms.OfType<KitDialog>().Any() && limit.Elapsed < TimeSpan.FromSeconds(5))
        {
            Application.DoEvents();
        }

        return title ?? throw new InvalidOperationException("No dialog opened.");
    }

    private static T Find<T>(Control root, Func<T, bool>? match = null)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T found && (match is null || match(found)))
            {
                return found;
            }

            if (child.HasChildren && FindOrNull(child, match) is T nested)
            {
                return nested;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} in {root.GetType().Name}.");
    }

    private static T? FindOrNull<T>(Control root, Func<T, bool>? match)
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
}
