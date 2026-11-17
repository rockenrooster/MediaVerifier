using System.ComponentModel;
using System.Reflection;
using MediaVerifier.App;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// No control the user can press does nothing: on every page of the main window, and in the detail drawer, each
/// visible, enabled button, link, chip, switch, check box, option, segmented control and drop-down has a handler.
/// (The drawer's More button, fix button and finding buttons on the Library page had none until 0.1.0.6.)
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DeadControlTests
{
    private static readonly object ClickKey = typeof(Control).GetField("s_clickEvent", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        ?? throw new InvalidOperationException("Control's click event key was not found.");

    private static readonly PropertyInfo EventsProperty = typeof(Component).GetProperty("Events", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void Every_control_the_user_can_press_has_a_handler()
    {
        List<string> dead = [];
        int checkedControls = 0;
        int drawers = 0;
        Sta.Run(() =>
        {
            // The census itself works: a button without a handler is caught, one with a handler is not.
            using (KitButton bare = new())
            {
                Assert.False(HasClick(bare));
                bare.Click += (_, _) => { };
                Assert.True(HasClick(bare));
            }

            using TempDirectory temp = new("dead-controls");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            foreach (PageId page in Enum.GetValues<PageId>())
            {
                form.Navigate(page);
                ScreenRenderer.WaitFor(form.CurrentPageLoading);
                Application.DoEvents();
                checkedControls += Collect(form, page.ToString(), dead);
            }

            // Every Settings section (one shows at a time).
            form.Navigate(PageId.Settings);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            SettingsPage settings = (SettingsPage)form.CurrentPageControl!;
            for (int section = 0; section < 7; section++)
            {
                settings.ShowSection(section);
                Application.DoEvents();
                checkedControls += Collect(form, $"Settings section {section}", dead);
            }

            // Tools' views: Quarantine, Operations, (2 is the report dialog), Media engine, Database.
            form.Navigate(PageId.Tools);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            MethodInfo showView = form.CurrentPageControl!.GetType().GetMethod("ShowView", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (int view in new[] { 0, 1, 3, 4 })
            {
                showView.Invoke(form.CurrentPageControl, [view]);
                Application.DoEvents();
                checkedControls += Collect(form, $"Tools view {view}", dead);
            }

            // The detail drawer, opened on a finding (Issues) and on a file (Library).
            foreach (PageId page in new[] { PageId.Issues, PageId.Library })
            {
                form.Navigate(page);
                ScreenRenderer.WaitFor(form.CurrentPageLoading);
                // The drawer joins the window only while it shows: take the page's own detail view.
                FileDetailView view = (FileDetailView)form.CurrentPageControl!.GetType().GetField("_detail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form.CurrentPageControl)!;
                FileDetail detail = services.Data.LoadFileDetail(4) ?? throw new InvalidOperationException("No demo file detail.");
                view.Bind(detail, services.Clock.UtcNow, detail.RecommendedAction);
                Assert.True(HasEvent(view, "MoreRequested"), page + ": the drawer's More button has no handler");
                Assert.True(HasEvent(view, "PrimaryRequested"), page + ": the drawer's main button has no handler");
                Assert.True(HasEvent(view, "FindingAction"), page + ": the drawer's finding buttons have no handler");
                Assert.True(HasEvent(view, "ShowInFolderRequested"), page + ": the drawer's Show in folder has no handler");
                checkedControls += Collect(view, page + " drawer", dead, includeHidden: true);
                drawers++;
            }

            form.Close();
        });

        Assert.True(checkedControls > 60, $"Only {checkedControls} controls were checked.");
        Assert.Equal(2, drawers);
        Assert.True(dead.Count == 0, "Controls without a handler:" + Environment.NewLine + string.Join(Environment.NewLine, dead.Distinct()));
    }

    /// <summary>Records the pressable controls under <paramref name="root"/> that have no handler; returns how many it checked.</summary>
    private static int Collect(Control root, string where, List<string> dead, bool includeHidden = false)
    {
        int count = 0;
        foreach (Control control in Descendants(root))
        {
            if ((!includeHidden && !control.Visible) || !control.Enabled)
            {
                continue;
            }

            if (control is KitButton or IconButton or KitLink or SplitButton or Chip or ToggleSwitch or KitCheckBox or OptionRow or SegmentedControl or DropDown)
            {
                count++;
            }

            string? missing = control switch
            {
                SplitButton split => HasClick(split) || HasEvent(split, "DropDownClick") ? null : "click",
                KitButton or IconButton or KitLink => HasClick(control) ? null : "click",
                Chip chip => HasEvent(chip, "Toggled") || HasClick(chip) ? null : "toggle",
                ToggleSwitch or KitCheckBox or OptionRow => HasEvent(control, "CheckedChanged") || HasClick(control) ? null : "checked",
                SegmentedControl or DropDown => HasEvent(control, "SelectedIndexChanged") ? null : "selection",
                _ => null,
            };
            if (missing is not null)
            {
                dead.Add($"{where}: {control.GetType().Name} \"{control.Text}\" ({control.AccessibleName}) in {control.Parent?.GetType().Name} has no {missing} handler");
            }
        }

        return count;
    }

    private static bool HasClick(Control control) => ((EventHandlerList)EventsProperty.GetValue(control)!)[ClickKey] is not null;

    private static bool HasEvent(Control control, string name)
    {
        for (Type? type = control.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) is FieldInfo field)
            {
                return field.GetValue(control) is Delegate;
            }
        }

        return false;
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
}
