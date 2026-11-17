using MediaVerifier.App;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Task H-05 (subplan 16, Accessibility pass), the automated half: on every page of the demo library, every control
/// the keyboard can reach has a name and a role that Narrator can read, and lists name their rows. Reading order and
/// announcements are checked by hand (docs/test-procedures.md, P-6).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AccessibilityTests
{
    [Fact]
    public void Every_reachable_control_has_a_name_and_a_role()
    {
        List<string> problems = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("a11y");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            foreach (PageId page in PageInfo.All)
            {
                form.Navigate(page);
                Settle(form.CurrentPageLoading);
                if (form.CurrentPageControl is IssuesPage issues)
                {
                    Settle(issues.ShowDemoDetailAsync());
                }

                Check(form, page.ToString(), problems);
                if (form.CurrentPageControl is SettingsPage settings)
                {
                    for (int section = 1; section < 8; section++)
                    {
                        settings.ShowSection(section);
                        Application.DoEvents();
                        Check(form, $"Settings section {section}", problems);
                    }
                }

                form.SetDrawerContent(null);
                Application.DoEvents();
            }

            form.Close();
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
    }

    private static void Check(Control root, string where, List<string> problems)
    {
        foreach (Control control in Descendants(root))
        {
            if (!control.Visible || !control.TabStop || !control.CanSelect)
            {
                continue;
            }

            AccessibleObject accessible = control.AccessibilityObject;
            string? name = accessible.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"{where}: {control.GetType().Name} \"{control.Text}\" in {control.Parent?.GetType().Name} has no accessible name");
            }

            if (accessible.Role is AccessibleRole.None or AccessibleRole.Default or AccessibleRole.Client)
            {
                problems.Add($"{where}: {control.GetType().Name} \"{name}\" has role {accessible.Role}");
            }

            if (accessible.Role is AccessibleRole.List or AccessibleRole.Table or AccessibleRole.Outline && accessible.GetChildCount() > 0
                && string.IsNullOrWhiteSpace(accessible.GetChild(0)?.Name))
            {
                problems.Add($"{where}: {control.GetType().Name} \"{name}\" has unnamed rows");
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control inner in Descendants(child))
            {
                yield return inner;
            }
        }
    }

    private static void Settle(Task task)
    {
        System.Diagnostics.Stopwatch limit = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && limit.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }
}
