using System.Reflection;
using System.Runtime.InteropServices;
using MediaVerifier.App;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;
using static MediaVerifier.Tests.TestSupport.Ui;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Whole-window checks that need no eyes: test windows leave the user's window in front (they made the screen
/// flicker), and on every page, view and drawer state, at the smallest and the default window, no control runs off
/// the page or covers another.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ScreenCensusTests
{
    private static readonly PropertyInfo InteractiveProperty = typeof(KitControl).GetProperty("Interactive", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The main window opening, a page loading and a modal question each leave the foreground window as it was.</summary>
    [Fact]
    public void Test_windows_leave_the_users_foreground_window_alone()
    {
        IntPtr before = GetForegroundWindow();
        List<string> stolen = [];
        string? asked = null;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("focus");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Check("the main window opening");
            form.Navigate(PageId.Issues);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            Check("a page loading");

            using System.Windows.Forms.Timer timer = new() { Interval = 100 };
            timer.Tick += (_, _) =>
            {
                if (asked is null && Application.OpenForms.OfType<MessageDialog>().FirstOrDefault() is MessageDialog open)
                {
                    asked = open.Text;
                    Check("a modal question");
                    open.DialogResult = DialogResult.Cancel;
                }
            };
            timer.Start();
            _ = MessageDialog.Confirm(form, "Focus check", "This window must not take the focus.", "Cancel", "OK", destructive: false);
            timer.Stop();
            Check("the question closing");

            Invoke(form.CurrentPageControl!, "OpenDetail", 3);
            Check("the drawer opening");

            using (ScheduleEditorDialog editor = ScheduleEditorDialog.Create("Weekly check", MediaVerifier.Core.Scheduling.PlanDefinition.Templates[1].Definition, [], new DateOnly(2026, 11, 1)))
            using (Form stage = DialogStage.Show(editor))
            {
                Check("a dialog on its test stage");
                editor.Close();
            }

            form.Close();
        });

        Assert.NotNull(asked);
        Assert.True(stolen.Count == 0, "Took the focus from the user's window: " + string.Join(", ", stolen));

        // Only a window of this (test) process in front counts: the user may switch windows meanwhile.
        void Check(string what)
        {
            IntPtr now = GetForegroundWindow();
            if (now != before && now != IntPtr.Zero && GetWindowThreadProcessId(now, out uint process) != 0 && process == (uint)Environment.ProcessId)
            {
                stolen.Add(what);
            }
        }
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>
    /// Settings → Rules opened from the section list shows its rules: the list was laid out while the section was
    /// hidden, when WinForms reads every row as hidden, and the card stayed blank.
    /// </summary>
    [Fact]
    public void Rules_opened_from_the_section_list_shows_the_rules()
    {
        Rectangle clipping = Rectangle.Empty;
        Rectangle card = Rectangle.Empty;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("rules-shown");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Open(form, PageId.Settings);
            SettingsPage settings = (SettingsPage)form.CurrentPageControl!;
            settings.ShowSection(2);
            Pump();
            string title = MediaVerifier.Core.Rules.RuleCatalog.All[0].Title;
            ToggleSwitch first = Descendants(settings).OfType<ToggleSwitch>().First(t => t.AccessibleName == title && t.Visible);
            clipping = first.RectangleToScreen(first.ClientRectangle);
            Control rulesCard = first.Parent!.Parent!.Parent!;
            card = rulesCard.RectangleToScreen(rulesCard.ClientRectangle);
            form.Close();
        });

        Assert.True(clipping.Height > 0 && card.Contains(clipping), $"The first rule's switch {clipping} is not inside the rules card {card}.");
    }

    /// <summary>A resize while another section is shown lays the hidden rule list out; the open rule keeps its effect line.</summary>
    [Fact]
    public void An_open_rule_keeps_its_effect_line_after_a_resize_while_rules_were_hidden()
    {
        int before = 0;
        int after = 0;
        Rectangle effect = Rectangle.Empty;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("rules-effect");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Open(form, PageId.Settings);
            SettingsPage settings = (SettingsPage)form.CurrentPageControl!;
            ScreenRenderer.WaitFor(settings.ShowDemoRulesAsync());
            Pump();
            before = OpenRule(settings).Height;
            settings.ShowSection(0);
            Pump();
            form.ClientSize = new Size(form.ClientSize.Width - 200, form.ClientSize.Height);
            Pump();
            settings.ShowSection(2);
            Pump();
            Control open = OpenRule(settings);
            after = open.Height;
            effect = Descendants(open).OfType<InfoBar>().First(b => b.Visible).Bounds;
            form.Close();
        });

        Assert.Equal(before, after);
        Assert.True(effect.Bottom <= after, $"The effect line {effect} runs past the bottom of its rule ({after} px).");
    }

    /// <summary>The rule row that shows an effect line (the demo opens Clipping and changes it).</summary>
    private static Control OpenRule(Control root) =>
        Descendants(root).Where(c => c.GetType().Name == "RuleRow").Single(r => Descendants(r).OfType<InfoBar>().Any(b => b.Visible));

    [Theory]
    [InlineData(960, 600)]
    [InlineData(1280, 800)]
    public void No_control_runs_off_its_page_or_covers_another(int width, int height)
    {
        List<string> problems = [];
        List<string> states = [];
        Sta.Run(() =>
        {
            using TempDirectory temp = new("census");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(width, height));

            foreach (PageId page in PageInfo.All)
            {
                Open(form, page);
                Census(form, page.ToString(), problems, states);
            }

            Open(form, PageId.Library);
            ((LibraryPage)form.CurrentPageControl!).ShowDemoBrowse();
            Pump();
            Census(form, "Library browse", problems, states);
            Invoke(form.CurrentPageControl!, "OpenDetails", 0);
            Census(form, "Library browse with the drawer", problems, states);
            services.ShowDrawer?.Invoke(null);

            Open(form, PageId.Issues);
            Invoke(form.CurrentPageControl!, "OpenDetail", 3);
            Census(form, "Issues with the drawer", problems, states);
            services.ShowDrawer?.Invoke(null);

            Open(form, PageId.Settings);
            SettingsPage settings = (SettingsPage)form.CurrentPageControl!;
            for (int section = 0; section < 8; section++)
            {
                settings.ShowSection(section);
                Pump();
                Census(form, $"Settings section {section}", problems, states);
            }

            Open(form, PageId.Tools);
            foreach (int view in new[] { 0, 1, 3, 4 })
            {
                Invoke(form.CurrentPageControl!, "ShowView", view);
                Census(form, $"Tools view {view}", problems, states);
            }

            form.Close();
        });

        Assert.True(states.Count >= 22, $"Only {states.Count} states were checked.");
        Assert.True(problems.Count == 0, $"{width} × {height}:{Environment.NewLine}" + string.Join(Environment.NewLine, problems));
    }

    /// <summary>The page (and the drawer when it is open): every visible control a user can press stays inside, apart from the others.</summary>
    private static void Census(MainForm form, string state, List<string> problems, List<string> states)
    {
        states.Add(state);
        PageBase page = form.CurrentPageControl!;
        Control viewport = page.Parent ?? page;
        Check(page, viewport.RectangleToScreen(viewport.ClientRectangle), state, problems);
        if (form.Shell.Drawer is { Visible: true } drawer && drawer.Controls.Count > 0)
        {
            Check(drawer, drawer.RectangleToScreen(drawer.ClientRectangle), state + " (drawer)", problems);
        }
    }

    private static void Check(Control root, Rectangle area, string state, List<string> problems)
    {
        List<(Control Control, Rectangle Bounds)> shown = [];
        Collect(root, shown);
        foreach ((Control control, Rectangle bounds) in shown)
        {
            if (bounds.Left < area.Left - 1 || bounds.Right > area.Right + 1)
            {
                problems.Add($"{state}: {Describe(control)} runs off the edge ({bounds.Left - area.Left}..{bounds.Right - area.Left} of {area.Width})");
            }
        }

        for (int i = 0; i < shown.Count; i++)
        {
            for (int j = i + 1; j < shown.Count; j++)
            {
                Rectangle overlap = Rectangle.Intersect(shown[i].Bounds, shown[j].Bounds);
                if (overlap.Width > 2 && overlap.Height > 2 && !Related(shown[i].Control, shown[j].Control))
                {
                    problems.Add($"{state}: {Describe(shown[i].Control)} and {Describe(shown[j].Control)} overlap by {overlap.Width} × {overlap.Height} ({Relative(shown[i].Bounds, area)} and {Relative(shown[j].Bounds, area)})");
                }
            }
        }
    }

    /// <summary>The visible controls under <paramref name="root"/> that take clicks or keys, with their screen bounds.</summary>
    private static void Collect(Control root, List<(Control, Rectangle)> shown)
    {
        foreach (Control child in root.Controls)
        {
            if (!child.Visible || child.Width <= 0 || child.Height <= 0)
            {
                continue;
            }

            if (child is KitControl kit && (bool)InteractiveProperty.GetValue(kit)!)
            {
                shown.Add((child, child.RectangleToScreen(child.ClientRectangle)));
            }

            Collect(child, shown);
        }
    }

    private static bool Related(Control a, Control b)
    {
        for (Control? p = a.Parent; p is not null; p = p.Parent)
        {
            if (p == b)
            {
                return true;
            }
        }

        for (Control? p = b.Parent; p is not null; p = p.Parent)
        {
            if (p == a)
            {
                return true;
            }
        }

        return false;
    }

    private static string Relative(Rectangle bounds, Rectangle area) => $"x {bounds.Left - area.Left}..{bounds.Right - area.Left}, y {bounds.Top - area.Top}..{bounds.Bottom - area.Top}";

    private static string Describe(Control control) =>
        $"{control.GetType().Name} '{(control.Text.Length > 0 ? control.Text : control.AccessibleName)}'";

    private static void Open(MainForm form, PageId page)
    {
        form.Navigate(page);
        ScreenRenderer.WaitFor(form.CurrentPageLoading);
        Pump();
    }

    private static void Invoke(Control target, string method, int argument)
    {
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, [argument]);
        Pump();
    }

    private static void Pump()
    {
        for (int i = 0; i < 20; i++)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetForegroundWindow();
}
