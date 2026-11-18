using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Pages;
using static MediaVerifier.Tests.TestSupport.Ui;

namespace MediaVerifier.Tests.UI;

/// <summary>Settings → Rules (subplan 09): what a change does, how it is reported, and what happens when it fails.</summary>
[Collection(UiCollection.Name)]
public sealed class RulesViewTests
{
    /// <summary>Backlog rules have no detector: their switches did nothing, and two read on.</summary>
    [Fact]
    public void Backlog_rules_are_not_listed()
    {
        List<string> listed = [];
        Run((page, _, _) => listed.AddRange(Descendants(page).OfType<ToggleSwitch>().Select(t => t.AccessibleName ?? string.Empty)));

        foreach (RuleDefinition rule in RuleCatalog.All)
        {
            Assert.Equal(rule.Milestone != Milestone.Backlog, listed.Contains(rule.Title));
        }
    }

    /// <summary>A rule without limits never opens, so its effect line never showed: a switch change now answers under it.</summary>
    [Fact]
    public void Switching_a_closed_rule_reports_its_effect_under_it()
    {
        string effect = string.Empty;
        int closedHeight = 0;
        int answeredHeight = 0;
        Run((page, _, _) =>
        {
            ToggleSwitch dualMono = Switch(page, "AUD-DUAL-MONO");
            Control row = dualMono.Parent!;
            closedHeight = row.Height;
            dualMono.Checked = !dualMono.Checked;
            Settle(() => VisibleEffect(row) is { Length: > 0 } text && text != Strings.Settings.CheckingFindings);
            effect = VisibleEffect(row) ?? string.Empty;
            answeredHeight = row.Height;
        });

        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Settings.Appeared, "2"), effect);
        Assert.True(answeredHeight > closedHeight, $"The row stayed {closedHeight} px high, so the effect line was cut off.");
    }

    /// <summary>
    /// Arrowing through the cap is one change, a run in progress shows that it is checking, and changes made meanwhile run
    /// once more after it instead of in parallel; the last answer is the one shown.
    /// </summary>
    [Fact]
    public void Changes_are_judged_one_run_at_a_time_and_say_they_are_checking()
    {
        int runsAfterArrows = -1;
        string? whileRunning = null;
        int mostAtOnce = -1;
        int runsAtEnd = -1;
        string? final = null;
        Run((page, scans, _) =>
        {
            scans.Gate = new TaskCompletionSource<ReevaluationResult>();
            DropDown cap = Cap(page, "AUD-CLIPPING");
            Control row = cap.Parent!;
            cap.SelectedIndex = 1;
            cap.SelectedIndex = 2;
            cap.SelectedIndex = 3;
            Settle(() => scans.Runs > 0);
            Pump(TimeSpan.FromMilliseconds(500));
            runsAfterArrows = scans.Runs;
            whileRunning = VisibleEffect(row);

            ToggleSwitch clipping = Switch(page, "AUD-CLIPPING");
            clipping.Checked = !clipping.Checked;
            Pump(TimeSpan.FromMilliseconds(600));
            TaskCompletionSource<ReevaluationResult> first = scans.Gate;
            scans.Gate = null;
            first.SetResult(new ReevaluationResult(0, 0, 9));
            Settle(() => scans.Runs == 2 && scans.Running == 0 && VisibleEffect(row) != Strings.Settings.CheckingFindings);
            mostAtOnce = scans.MostAtOnce;
            runsAtEnd = scans.Runs;
            final = VisibleEffect(row);
        });

        Assert.Equal(1, runsAfterArrows);
        Assert.Equal(Strings.Settings.CheckingFindings, whileRunning);
        Assert.Equal(1, mostAtOnce);
        Assert.Equal(2, runsAtEnd);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Settings.Appeared, "2"), final);
    }

    /// <summary>Two limits typed within 400 ms both apply (the second used to replace the first, which reverted at restart).</summary>
    [Fact]
    public void Two_limits_typed_quickly_both_apply()
    {
        double low = 0;
        double medium = 0;
        Run((page, _, services) =>
        {
            Control row = Switch(page, "AUD-CLIPPING").Parent!;
            NumberBox lowBox = Descendants(row).OfType<NumberBox>().First(b => b.AccessibleName == RuleDefaults.Get("AUD-CLIPPING", "lowPerMinute").Label);
            NumberBox mediumBox = Descendants(row).OfType<NumberBox>().First(b => b.AccessibleName == RuleDefaults.Get("AUD-CLIPPING", "mediumPerMinute").Label);
            lowBox.Number = 7;
            mediumBox.Number = 70;
            Settle(() => services.Rules.IsOverridden("AUD-CLIPPING", "lowPerMinute") && services.Rules.IsOverridden("AUD-CLIPPING", "mediumPerMinute"));
            low = services.Rules.Value("AUD-CLIPPING", "lowPerMinute");
            medium = services.Rules.Value("AUD-CLIPPING", "mediumPerMinute");
        });

        Assert.Equal(7, low);
        Assert.Equal(70, medium);
    }

    /// <summary>A change the database refuses is undone, on screen too, and said in a Critical toast.</summary>
    [Fact]
    public void A_rule_change_that_cannot_be_saved_is_undone_and_reported()
    {
        bool stillOn = false;
        bool switchShowsOn = false;
        List<ToastContent> toasts = [];
        Run((page, scans, services) =>
        {
            services.ShowToast = toasts.Add;
            services.Database.Writer.Dispose();
            ToggleSwitch empty = Switch(page, "GEN-EMPTY");
            empty.Checked = false;
            Settle(() => toasts.Count > 0);
            Pump(TimeSpan.FromMilliseconds(100));
            stillOn = services.Rules.IsEnabled(RuleCatalog.Get("GEN-EMPTY"));
            switchShowsOn = empty.Checked;
            Assert.Equal(0, scans.Runs);
        });

        Assert.True(stillOn, "The rule stayed off although nothing was saved.");
        Assert.True(switchShowsOn, "The switch stayed flipped although nothing was saved.");
        ToastContent toast = Assert.Single(toasts);
        Assert.Equal(Severity.Critical, toast.Level);
        Assert.Equal(RuleCatalog.Get("GEN-EMPTY").Title, toast.Title);
    }

    /// <summary>
    /// Up, Down, Page Up, Page Down, Home and End on a rule's switch go to another rule (about 150 Tab presses reached the
    /// last one); a drop-down and a number box keep their arrows. F1 names the rule of the control in focus.
    /// </summary>
    [Fact]
    public void Arrows_move_between_rules_and_F1_names_the_rule_in_focus()
    {
        bool switchTakesDown = false;
        bool capTakesDown = true;
        string? ruleOfLimit = null;
        Run((page, _, _) =>
        {
            ToggleSwitch first = Switch(page, "GEN-UNREADABLE");
            Message down = Message.Create(first.Handle, 0x0100, (nint)Keys.Down, 0);
            switchTakesDown = first.PreProcessMessage(ref down);
            DropDown cap = Cap(page, "AUD-CLIPPING");
            Message capDown = Message.Create(cap.Handle, 0x0100, (nint)Keys.Down, 0);
            capTakesDown = cap.PreProcessMessage(ref capDown);
            NumberBox limit = Descendants(cap.Parent!).OfType<NumberBox>().First();
            ruleOfLimit = RulesView.RuleIdOf(limit);
        });

        Assert.True(switchTakesDown, "Down on a rule's switch did not move to the next rule.");
        Assert.False(capTakesDown, "The cap drop-down lost its arrows.");
        Assert.Equal("AUD-CLIPPING", ruleOfLimit);
        Assert.Equal(1, RulesView.MoveTarget(0, 46, 8, Keys.Down));
        Assert.Equal(0, RulesView.MoveTarget(0, 46, 8, Keys.Up));
        Assert.Equal(13, RulesView.MoveTarget(5, 46, 8, Keys.PageDown));
        Assert.Equal(0, RulesView.MoveTarget(5, 46, 8, Keys.PageUp));
        Assert.Equal(45, RulesView.MoveTarget(5, 46, 8, Keys.End));
        Assert.Equal(0, RulesView.MoveTarget(45, 46, 8, Keys.Home));
    }

    /// <summary>A Settings page on Rules with a stand-in scan control, live (not demo) services and its own database.</summary>
    private static void Run(Action<SettingsPage, CountingScans, UiServices> body)
    {
        Sta.Run(() =>
        {
            using TempDirectory temp = new("rules-view");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices demo = DemoUiData.CreateServices(state, temp.Path);
            CountingScans scans = new();
            using UiServices services = new()
            {
                State = state,
                Database = demo.Database,
                Clock = demo.Clock,
                Scans = scans,
                MediaEngine = demo.MediaEngine,
                Data = demo.Data,
                Rules = new RuleSettings(),
            };
            using SettingsPage page = new(services) { Size = new Size(1100, 760) };
            page.ShowSection(SettingsPage.RulesSection);
            page.PerformLayout();
            body(page, scans, services);
            Settle(() => scans.Running == 0, TimeSpan.FromSeconds(2));
        });
    }

    private static ToggleSwitch Switch(Control page, string ruleId) =>
        Find<ToggleSwitch>(page, t => t.AccessibleName == RuleCatalog.Get(ruleId).Title);

    private static DropDown Cap(Control page, string ruleId) =>
        Find<DropDown>(page, d => d.AccessibleName == RuleCatalog.Get(ruleId).Title);

    private static string? VisibleEffect(Control row) =>
        row.Controls.OfType<InfoBar>().FirstOrDefault(b => b.Visible && b.Bounds.Bottom <= row.Height)?.Text;

    /// <summary>Counts re-evaluations and how many run at once; a gate holds them until the test answers.</summary>
    private sealed class CountingScans : IScanControl
    {
        public int Runs { get; private set; }

        public int Running { get; private set; }

        public int MostAtOnce { get; private set; }

        public TaskCompletionSource<ReevaluationResult>? Gate { get; set; }

        public EngineStatus Status => EngineStatus.Idle;

        public string ScopeText => string.Empty;

        public event Action<RunSummary>? RunCompleted
        {
            add { }
            remove { }
        }

        public Task<long> StartAsync(ScanRequest request, string scopeText) => Task.FromResult(0L);

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Cancel()
        {
        }

        public void Interrupt()
        {
        }

        public async Task<ReevaluationResult> ReevaluateAsync(IReadOnlyCollection<string> ruleIds)
        {
            Runs++;
            Running++;
            MostAtOnce = Math.Max(MostAtOnce, Running);
            try
            {
                return Gate is TaskCompletionSource<ReevaluationResult> gate ? await gate.Task : new ReevaluationResult(0, 2, 0);
            }
            finally
            {
                Running--;
            }
        }
    }
}
