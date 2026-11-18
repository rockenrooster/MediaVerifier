using System.Diagnostics;
using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Pages;
using MediaVerifier.UI.Shell;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// The Schedule page through the real window on live services in a temporary profile. Plans saved here reach the
/// in-memory Task Scheduler of the tests (<see cref="TestScheduler"/>), never Windows.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SchedulePageTests
{
    /// <summary>Typing 9, 90, 900 in "Spend up to" writes settings.json once, after the typing stops (it flushed the file at every key).</summary>
    [Fact]
    public void The_verification_boxes_save_once_the_typing_stops()
    {
        int whileTyping = -1;
        int afterwards = -1;
        int budget = 0;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("schedule-boxes");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            int writes = 0;
            state.SettingsStore.BeforeReplace = () => writes++;
            using UiServices services = Live(state, temp);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            SchedulePage page = Open(form);
            NumberBox box = Find<NumberBox>(page, b => b.AccessibleName == Strings.Schedule.SpendUpTo);
            foreach (string text in new[] { "9", "90", "900" })
            {
                box.Text = text;
            }

            whileTyping = writes;
            Settle(() => writes > 0, TimeSpan.FromSeconds(3));
            Pump(TimeSpan.FromMilliseconds(700));
            afterwards = writes;
            budget = state.Settings.Scanning.ScrubBudgetMinutes;
            form.Close();
        });

        Assert.Equal(0, whileTyping);
        Assert.Equal(1, afterwards);
        Assert.Equal(900, budget);
    }

    /// <summary>With more plans than fit, the page scrolls to the verification card (it was cut off and out of reach).</summary>
    [Fact]
    public void The_page_scrolls_to_the_verification_card()
    {
        int scrollMax = -1;
        int pageHeight = 0;
        int cardBottom = int.MaxValue;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("schedule-scroll");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = Live(state, temp);
            for (int i = 0; i < 9; i++)
            {
                Task<long> add = services.Database.Plans.AddAsync(new PlanRecord { Name = "Plan " + i.ToString(CultureInfo.InvariantCulture), Enabled = false, DefinitionJson = PlanDefinition.Templates[0].Definition.ToJson() });
                Settle(() => add.IsCompleted);
            }

            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            SchedulePage page = Open(form);
            scrollMax = ((PageViewport)page.Parent!).ScrollMax;
            pageHeight = page.Height;
            cardBottom = Find<NumberBox>(page, b => b.AccessibleName == Strings.Schedule.SpendUpTo).Bottom;
            form.Close();
        });

        Assert.True(scrollMax > 0, "The page does not scroll.");
        Assert.True(cardBottom <= pageHeight, $"The verification boxes end at {cardBottom}, below the page's {pageHeight}.");
    }

    /// <summary>The empty state's "Nightly verification" creates that plan and registers its task (the presets were unreachable).</summary>
    [Fact]
    public void The_empty_state_presets_create_their_plans()
    {
        PlanRecord? created = null;
        bool registered = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("schedule-presets");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = Live(state, temp);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            SchedulePage page = Open(form);
            KitButton nightly = Find<KitButton>(page, b => b.Text == Strings.Schedule.Nightly && b.Visible);
            nightly.PerformClick();
            Settle(() => Db(services.Database.Plans.List) is [PlanRecord one] && one.TaskName is not null);
            created = Db(services.Database.Plans.List).SingleOrDefault();
            registered = created is not null && TestScheduler.Backend.Tasks.ContainsKey(created.TaskName ?? string.Empty);
            form.Close();
        });

        Assert.NotNull(created);
        Assert.Equal(Strings.Schedule.Nightly, created.Name);
        Assert.True(created.Enabled);
        Assert.Equal(PlanDefinition.Templates[0].Definition.ToJson(), created.DefinitionJson);
        Assert.True(registered, "The preset's task was not registered.");
    }

    /// <summary>
    /// A second click on a preset after its plan was stored, while the page reloads with the presets still showing,
    /// adds nothing (the plan was added twice).
    /// </summary>
    [Fact]
    public void A_preset_clicked_again_before_the_page_hides_it_adds_nothing()
    {
        int plans = -1;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("schedule-preset-twice");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = Live(state, temp);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            SchedulePage page = Open(form);
            KitButton nightly = Find<KitButton>(page, b => b.Text == Strings.Schedule.Nightly && b.Visible);
            nightly.PerformClick();
            Settle(() => Db(services.Database.Plans.List).Count == 1 && !page.Loading.IsCompleted);
            nightly.PerformClick();
            Pump(TimeSpan.FromMilliseconds(300));
            Settle(() => page.Loading.IsCompleted);
            plans = Db(services.Database.Plans.List).Count;
            form.Close();
        });

        Assert.Equal(1, plans);
    }

    /// <summary>
    /// Space twice on a plan's switch, the second while the first change is stored: the switch stays enabled (disabled,
    /// it sent the keyboard focus on to the row's Run now, and the next Space started a scan), and the plan ends as the
    /// switch shows, with its task (the second Space was lost).
    /// </summary>
    [Fact]
    public void The_switch_stays_enabled_and_its_last_state_is_stored()
    {
        bool enabledWhileStoring = false;
        PlanRecord? stored = null;
        bool registered = false;
        bool shownOn = false;
        long before = 0;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("schedule-switch");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = Live(state, temp);
            Task<long> add = services.Database.Plans.AddAsync(new PlanRecord { Name = "Weekly check", Enabled = true, DefinitionJson = PlanDefinition.Templates[1].Definition.ToJson() });
            Settle(() => add.IsCompleted);
            long id = add.Result;
            string task = "MediaVerifier\\" + TaskSchedulerBackend.TaskName(id);
            before = Db(() => services.Database.Plans.Get(id)!.UpdatedUtc);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            SchedulePage page = Open(form);
            ToggleSwitch toggle = Find<ToggleSwitch>(page, t => t.AccessibleName == "Weekly check");
            PressSpace(toggle);
            enabledWhileStoring = toggle.Enabled;
            PressSpace(toggle);
            Settle(() => Db(() => services.Database.Plans.Get(id)) is { Enabled: true } plan && plan.UpdatedUtc != before && TestScheduler.Backend.Tasks.ContainsKey(task));
            Pump(TimeSpan.FromMilliseconds(300));
            Settle(() => page.Loading.IsCompleted);
            stored = Db(() => services.Database.Plans.Get(id));
            registered = TestScheduler.Backend.Tasks.ContainsKey(task);
            shownOn = toggle.Checked;
            form.Close();
        });

        Assert.True(enabledWhileStoring, "The switch was disabled while its change was stored.");
        Assert.NotNull(stored);
        Assert.True(stored.Enabled && stored.UpdatedUtc != before, "The second Space was lost: the plan stayed switched off.");
        Assert.True(registered, "The plan's task is missing.");
        Assert.True(shownOn, "The switch does not show the plan's state.");
    }

    /// <summary>A reload keeps the row controls (the switch the keyboard just used lost its focus when they were all recreated).</summary>
    [Fact]
    public void A_reload_keeps_the_row_controls()
    {
        bool same = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("schedule-rows");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = Live(state, temp);
            Task<long> add = services.Database.Plans.AddAsync(new PlanRecord { Name = "Weekly check", Enabled = false, DefinitionJson = PlanDefinition.Templates[1].Definition.ToJson() });
            Settle(() => add.IsCompleted);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            SchedulePage page = Open(form);
            ToggleSwitch before = Find<ToggleSwitch>(page, t => t.AccessibleName == "Weekly check");
            page.Reload();
            Settle(() => page.Loading.IsCompleted);
            Pump(TimeSpan.FromMilliseconds(100));
            ToggleSwitch after = Find<ToggleSwitch>(page, t => t.AccessibleName == "Weekly check");
            same = ReferenceEquals(before, after) && !before.IsDisposed;
            form.Close();
        });

        Assert.True(same, "The row's switch was recreated.");
    }

    /// <summary>Last result shows the pill Scans shows for the same run: an interrupted run read Failed in High colours.</summary>
    [Fact]
    public void Last_result_matches_the_scans_page()
    {
        Assert.Equal((Strings.Schedule.NeverRun, (Severity?)null), ScheduleText.Result(null));
        Assert.Equal((Strings.Schedule.Running, (Severity?)null), ScheduleText.Result(RunStatus.Running));
        foreach (RunStatus status in new[] { RunStatus.Completed, RunStatus.Cancelled, RunStatus.Failed, RunStatus.Interrupted, RunStatus.BudgetReached })
        {
            Assert.Equal((ScansPage.ResultText(status), ScansPage.ResultTier(status)), ScheduleText.Result(status));
        }

        Assert.Equal(Strings.Scans.Interrupted, ScheduleText.Result(RunStatus.Interrupted).Text);
        Assert.Equal(Strings.Scans.TimeLimit, ScheduleText.Result(RunStatus.BudgetReached).Text);
    }

    /// <summary>Subplan 09's when phrases: several weekdays read "Every Mon and Thu at 03:00".</summary>
    [Fact]
    public void Several_weekdays_read_as_a_sentence_of_short_names()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("Every Mon and Thu at 03:00", ScheduleText.When(new PlanTrigger { Kind = TriggerKind.Weekly, DaysOfWeek = [DayOfWeek.Thursday, DayOfWeek.Monday] }));
            Assert.Equal("Every Mon, Wed and Sun at 21:30", ScheduleText.When(new PlanTrigger { Kind = TriggerKind.Weekly, Time = "21:30", DaysOfWeek = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Wednesday] }));
            Assert.Equal("Every Sunday at 03:00", ScheduleText.When(PlanDefinition.Templates[1].Definition.Trigger));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>The full-pass estimate follows the budget it is given, and is 0 (hidden) before any file has a fingerprint.</summary>
    [Fact]
    public void The_full_pass_estimate_follows_the_budget()
    {
        ScheduleData data = new([], 0.5, 10, 100, 2 * 720_000_000_000L, ScheduleData.AssumedReadRate);
        Assert.Equal(2, data.FullPassDays(120));
        Assert.Equal(4, data.FullPassDays(60));
        Assert.Equal(1, data.FullPassDays(1440));
        Assert.Equal(0, (data with { BaselineFiles = 0 }).FullPassDays(120));
        Assert.Equal(4, (data with { ReadRate = ScheduleData.AssumedReadRate / 2 }).FullPassDays(120));
    }

    /// <summary>
    /// The Dashboard's Create schedule adds the first two templates and registers their tasks (it only opened the page);
    /// asked again, with plans there, it adds nothing more.
    /// </summary>
    [Fact]
    public void Create_schedule_adds_the_first_two_templates_once()
    {
        List<PlanRecord> plans = [];
        bool registered = false;
        Sta.Run(() =>
        {
            using TempDirectory temp = new("schedule-create");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UiServices services = Live(state, temp);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            services.Navigate!(PageId.Schedule, new CreateScheduleRequest());
            Settle(() => Db(services.Database.Plans.List) is { Count: 2 } two && two.All(p => p.TaskName is not null));
            SchedulePage page = (SchedulePage)form.CurrentPageControl!;
            Settle(() => page.Loading.IsCompleted);
            page.HandleRequest(new CreateScheduleRequest());
            Pump(TimeSpan.FromMilliseconds(300));
            Settle(() => page.Loading.IsCompleted);
            plans = Db(services.Database.Plans.List);
            registered = plans.All(p => TestScheduler.Backend.Tasks.ContainsKey(p.TaskName ?? string.Empty));
            form.Close();
        });

        Assert.Equal([Strings.Schedule.Nightly, Strings.Schedule.Weekly], plans.OrderBy(p => p.Id).Select(p => p.Name));
        Assert.All(plans, p => Assert.True(p.Enabled));
        Assert.True(registered, "A template's task was not registered.");
    }

    /// <summary>
    /// The editor warns when a switched-on plan fires at the same minute (only one scan runs at a time, so one of them was
    /// skipped); another time, or that plan switched off, clears it. Save stays enabled: it is a warning.
    /// </summary>
    [Fact]
    public void The_editor_warns_about_a_plan_at_the_same_minute()
    {
        string? atThree = null;
        string? atFive = null;
        string? otherOff = null;
        bool saveEnabled = false;
        Sta.Run(() =>
        {
            DateOnly anchor = new(2026, 11, 1);
            PlanDefinition nightly = PlanDefinition.Templates[0].Definition;
            PlanRow on = new(1, Strings.Schedule.Nightly, true, string.Empty, string.Empty, string.Empty, null, nightly, anchor);
            using (ScheduleEditorDialog editor = ScheduleEditorDialog.Create(Strings.Schedule.Weekly, PlanDefinition.Templates[1].Definition, [], anchor, [on]))
            {
                atThree = editor.SameTimeWarning;
                saveEnabled = Find<KitButton>(editor, b => b.Text == Strings.Kit.Save).Enabled;
                Find<KitTextBox>(editor, b => b.Text == "03:00").Text = "05:00";
                atFive = editor.SameTimeWarning;
            }

            using ScheduleEditorDialog off = ScheduleEditorDialog.Create(Strings.Schedule.Weekly, PlanDefinition.Templates[1].Definition, [], anchor, [on with { Enabled = false }]);
            otherOff = off.SameTimeWarning;
        });

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Schedule.SameTime, Strings.Schedule.Nightly), atThree);
        Assert.True(saveEnabled, "The warning disabled Save.");
        Assert.Null(atFive);
        Assert.Null(otherOff);
    }

    private static UiServices Live(AppState state, TempDirectory temp) =>
        UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), startupMaintenance: false, engineMutexName: @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N"));

    private static SchedulePage Open(MainForm form)
    {
        form.Navigate(PageId.Schedule);
        Settle(() => form.CurrentPageLoading.IsCompleted);
        Pump(TimeSpan.FromMilliseconds(100));
        return (SchedulePage)form.CurrentPageControl!;
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

    /// <summary>The test's own database reads, made on a pool thread: on the UI thread they would trip the guard that keeps the app off it.</summary>
    private static T Db<T>(Func<T> read) => Task.Run(read).GetAwaiter().GetResult();

    /// <summary>A Space key sent to the control itself, as the keyboard would (nothing is focused or activated).</summary>
    /// <summary>Space pressed and released: kit controls act on the release, as Windows buttons do.</summary>
    private static void PressSpace(Control control)
    {
        _ = SendMessage(control.Handle, 0x0100, (IntPtr)Keys.Space, (IntPtr)1);
        _ = SendMessage(control.Handle, 0x0101, (IntPtr)Keys.Space, unchecked((IntPtr)0xC0000001));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
