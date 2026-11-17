using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// Schedule (subplan 09, Schedule; tasks UI-08 and SCH-07): the plans with their switch, next run and last
/// result, Run now, Edit and more; the verification card. Geometry from docs/design/schedule-dark.svg with the
/// content origin at the page's top left.
/// </summary>
internal sealed class SchedulePage : PageBase
{
    private const int RowHeight = 68;
    private readonly KitButton _new = new() { Kind = ButtonKind.Secondary, Icon = IconName.Add, Text = Strings.Schedule.NewSchedule };
    private readonly EmptyState _empty = new() { Icon = IconName.Schedule, Headline = Strings.Schedule.EmptyHeadline, Body = Strings.Schedule.EmptyText, Visible = false };
    private readonly NumberBox _scrubDays = new() { Minimum = 1, Maximum = 3650, Inset = false, AccessibleName = Strings.Schedule.ReReadEvery };
    private readonly NumberBox _budget = new() { Minimum = 1, Maximum = 1440, Inset = false, AccessibleName = Strings.Schedule.SpendUpTo };
    private readonly InfoBar _pass = new() { Tier = Severity.Low };
    private readonly List<(ToggleSwitch Toggle, KitButton Run, KitButton Edit, IconButton More)> _rows = [];
    private ScheduleData? _data;
    private bool _binding;

    public SchedulePage(UiServices services)
        : base(services)
    {
        _new.Click += (_, _) => UiTask.Run(EditAsync(null));
        _scrubDays.ValueChanged += (_, _) => SaveVerification();
        _budget.ValueChanged += (_, _) => SaveVerification();
        Controls.AddRange([_new, _empty, _scrubDays, _budget, _pass]);
    }

    public override PageId Id => PageId.Schedule;

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override object? LoadData() => Services.Data.LoadSchedule();

    protected override void ApplyData(object data)
    {
        _data = (ScheduleData)data;
        _binding = true;
        _scrubDays.Value = State.Settings.Scanning.ScrubPeriodDays;
        _budget.Value = State.Settings.Scanning.ScrubBudgetMinutes;
        _binding = false;
        UpdatePassText();
        foreach ((ToggleSwitch toggle, KitButton run, KitButton edit, IconButton more) in _rows)
        {
            Controls.Remove(toggle);
            Controls.Remove(run);
            Controls.Remove(edit);
            Controls.Remove(more);
            toggle.Dispose();
            run.Dispose();
            edit.Dispose();
            more.Dispose();
        }

        _rows.Clear();
        foreach (PlanRow plan in _data.Plans)
        {
            ToggleSwitch toggle = new() { Checked = plan.Enabled, AccessibleName = plan.Name };
            KitButton run = new() { Kind = ButtonKind.Secondary, Text = Strings.Schedule.RunNow };
            KitButton edit = new() { Kind = ButtonKind.Secondary, Text = Strings.Schedule.Edit };
            IconButton more = new() { Icon = IconName.More, ToolTipText = Strings.Kit.MoreActions };
            toggle.CheckedChanged += (_, _) => UiTask.Run(SetEnabledAsync(plan, toggle.Checked));
            run.Click += (_, _) => UiTask.Run(RunNowAsync(plan));
            edit.Click += (_, _) => UiTask.Run(EditAsync(plan));
            more.Click += (_, _) => ShowMoreMenu(plan, more);
            _rows.Add((toggle, run, edit, more));
            Controls.AddRange([toggle, run, edit, more]);
        }

        OnSurface();

        PerformLayout();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int w = Width;
        _new.Bounds = new Rectangle(w - S(_new.PreferredLogicalWidth), S(1), S(_new.PreferredLogicalWidth), S(32));
        int top = S(52);
        for (int i = 0; i < _rows.Count; i++)
        {
            int rowTop = top + S(RowPitch * i);
            (ToggleSwitch toggle, KitButton run, KitButton edit, IconButton more) = _rows[i];
            toggle.Bounds = new Rectangle(S(16), rowTop + S(24), S(40), S(20));
            run.Bounds = new Rectangle(w - S(180), rowTop + S(20), S(74), S(28));
            edit.Bounds = new Rectangle(w - S(99), rowTop + S(20), S(47), S(28));
            more.Bounds = new Rectangle(w - S(44), rowTop + S(20), S(28), S(28));
        }

        _empty.Visible = _data is { Plans.Count: 0 };
        _empty.Bounds = new Rectangle(0, S(48), w, S(200));
        int card = VerificationTop();
        _scrubDays.Bounds = new Rectangle(S(323), card + S(105), S(64), S(32));
        _budget.Bounds = new Rectangle(S(208), card + S(145), S(63), S(32));
        _pass.Bounds = Stacked
            ? new Rectangle(S(132), card + S(196), Math.Min(S(376), w - S(148)), S(40))
            : new Rectangle(w - S(392), card + S(104), S(376), S(40));
        Height = card + VerificationHeight + S(24);
    }

    /// <summary>
    /// Too narrow for the pass note beside the settings (the smallest window): it goes under them, so the card's text
    /// keeps its width. Side by side, the text was cut to "Every file should be re-read and compared with i…".
    /// </summary>
    private bool Stacked => Width < S(980);

    /// <summary>A plan row's height: on a narrow page the summary may take a second line (it was cut: "…no time li…").</summary>
    private int RowPitch => Stacked ? 84 : RowHeight;

    private int VerificationHeight => S(Stacked ? 288 : 196);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ThemeTokens t = Tokens;
        TypeRamp type = Type;
        Graphics g = e.Graphics;
        int w = Width;
        KitPaint.DrawText(g, Strings.Schedule.Intro, type.Body, t.Text2, 0, S(17), w - S(160));
        if (_data is null)
        {
            return;
        }

        if (_data.Plans.Count > 0)
        {
            int cardHeight = S((RowPitch * _data.Plans.Count) + 8);
            Card.Draw(g, new Rectangle(0, S(48), w, cardHeight), null, VisualState.Rest, false, DeviceDpi);
            for (int i = 0; i < _data.Plans.Count; i++)
            {
                PlanRow plan = _data.Plans[i];
                int rowTop = S(52) + S(RowPitch * i);
                if (i > 0)
                {
                    KitPaint.HorizontalLine(g, t.Stroke, S(16), w - S(16), rowTop, SF(1));
                }

                KitPaint.DrawText(g, plan.Name, type.BodyStrong, t.Text, S(72), rowTop + S(25), w - S(520));
                if (Stacked)
                {
                    TextWrap.Draw(g, plan.Summary, type.Body, t.Text2, S(72), rowTop + S(36), w - S(520), S(19), maxLines: 2);
                }
                else
                {
                    KitPaint.DrawText(g, plan.Summary, type.Body, t.Text2, S(72), rowTop + S(46), w - S(520));
                }
                KitPaint.DrawText(g, Strings.Schedule.NextRun, type.Caption, t.Text3, w - S(432), rowTop + S(25), S(120));
                KitPaint.DrawText(g, plan.NextRun, type.Body, plan.Enabled ? t.Text : t.Text3, w - S(432), rowTop + S(46), S(124));
                KitPaint.DrawText(g, Strings.Schedule.LastResult, type.Caption, t.Text3, w - S(302), rowTop + S(25), S(120));
                if (plan.LastResult == Strings.Schedule.NeverRun)
                {
                    BadgePainter.PaintPill(g, w - S(302), rowTop + S(37), plan.LastResult, DeviceDpi);
                }
                else
                {
                    BadgePainter.PaintStatusPill(g, w - S(302), rowTop + S(37), plan.LastResult, plan.ResultTier, DeviceDpi);
                }
            }
        }

        // Verification card.
        int card = VerificationTop();
        bool stacked = Stacked;
        int textWidth = stacked ? w - S(148) : w - S(560);
        Card.Draw(g, new Rectangle(0, card, w, VerificationHeight), null, VisualState.Rest, false, DeviceDpi);
        KitPaint.DrawText(g, Strings.Schedule.Verification, type.CaptionStrong, t.Text3, S(16), card + S(23), S(200));
        DataPaint.Donut(g, new PointF(S(64), card + S(109)), SF(40), SF(9), _data.VerifiedShare, t.Accent);
        string percent = (_data.VerifiedShare * 100).ToString("0", CultureInfo.CurrentCulture) + " %";
        KitPaint.DrawTextCentred(g, percent.Replace(" %", "%", StringComparison.Ordinal), type.BodyStrong, t.Text, S(64), card + S(109));
        KitPaint.DrawText(g, Strings.Schedule.VerificationLine1, type.Body, t.Text, S(132), card + S(57), textWidth);
        KitPaint.DrawText(g, Strings.Schedule.VerificationLine2, type.Body, t.Text, S(132), card + S(77), textWidth);
        KitPaint.DrawText(g, Strings.Schedule.ReReadEvery, type.Body, t.Text, S(132), card + S(121), S(190));
        KitPaint.DrawText(g, Strings.Schedule.Days, type.Body, t.Text, S(395), card + S(121), S(80));
        KitPaint.DrawText(g, Strings.Schedule.SpendUpTo, type.Body, t.Text, S(132), card + S(161), S(80));
        KitPaint.DrawText(g, Strings.Schedule.MinutesEachRun, type.Body, t.Text, S(279), card + S(161), stacked ? w - S(295) : w - S(700));
        if (_data.OverdueFiles > 0)
        {
            string overdue = string.Format(CultureInfo.CurrentCulture, Strings.Schedule.OverdueCaption, UiFormat.Count(_data.OverdueFiles)) + Strings.Schedule.VerifyNowLink + ".";
            if (stacked)
            {
                KitPaint.DrawText(g, overdue, type.Caption, t.Text3, S(132), card + S(253), w - S(148));
            }
            else
            {
                KitPaint.DrawText(g, overdue, type.Caption, t.Text3, w - S(392), card + S(161), S(376));
            }
        }
    }

    protected override void OnThemeChanged()
    {
        base.OnThemeChanged();
        OnSurface();
    }

    /// <summary>The row controls sit on the plans card, so their corners blend with Surface, not the page.</summary>
    private void OnSurface()
    {
        foreach ((ToggleSwitch toggle, KitButton run, KitButton edit, IconButton more) in _rows)
        {
            toggle.BackColor = Tokens.Surface;
            run.BackColor = Tokens.Surface;
            edit.BackColor = Tokens.Surface;
            more.BackColor = Tokens.Surface;
        }

        _scrubDays.BackColor = Tokens.Surface;
        _budget.BackColor = Tokens.Surface;
        _pass.BackColor = Tokens.Surface;
    }

    private int VerificationTop() => S(48) + (_data is { Plans.Count: > 0 } d ? S((RowPitch * d.Plans.Count) + 8) : S(200)) + S(12);

    private void UpdatePassText()
    {
        if (_data is null)
        {
            return;
        }

        int minutes = State.Settings.Scanning.ScrubBudgetMinutes;
        string budget = minutes % 60 == 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Schedule.Hours, minutes / 60) : string.Format(CultureInfo.CurrentCulture, Strings.Schedule.MinutesShort, minutes);
        _pass.Text = string.Format(CultureInfo.CurrentCulture, Strings.Schedule.FullPass, budget, UiFormat.Count(_data.FullPassDays));
    }

    private void SaveVerification()
    {
        if (_binding || Services.IsDemo)
        {
            return;
        }

        State.Settings.Scanning.ScrubPeriodDays = _scrubDays.Value;
        State.Settings.Scanning.ScrubBudgetMinutes = _budget.Value;
        State.SaveSettings();
        UpdatePassText();
    }

    /// <summary>The plan editor: a new plan starts from the weekly template; saving writes the plan and updates the tasks.</summary>
    private async Task EditAsync(PlanRow? plan)
    {
        if (Services.IsDemo || FindForm() is not Form owner)
        {
            return;
        }

        List<RootRecord> roots = await Task.Run(Services.Database.Roots.List).ConfigureAwait(true);
        PlanDefinition start = plan?.Definition ?? PlanDefinition.Templates[1].Definition;

        // Each Save of the open editor stores the same plan: the first one of a new plan creates it.
        long? planId = plan?.Id;
        bool enabled = plan?.Enabled ?? true;
        _ = ScheduleEditorDialog.Ask(owner, plan?.Name ?? Strings.Schedule.Weekly, start, roots, DateOnly.FromDateTime(DateTime.Now), plan is not null, async (name, edited) =>
        {
            (long id, string? refused) = await StorePlanAsync(planId, name, edited, enabled).ConfigureAwait(true);
            planId = id;
            return refused;
        });
        Reload();
    }

    /// <summary>
    /// Stores a plan and brings the scheduled tasks up to date. When Windows refuses the task, the plan is stored
    /// switched off and Windows' message is returned (subplan 09, Schedule).
    /// </summary>
    private async Task<(long Id, string? Refused)> StorePlanAsync(long? id, string name, PlanDefinition definition, bool enabled)
    {
        Database database = Services.Database;
        long now = Services.Clock.UtcNow.UtcTicks;
        return await Task.Run(async () =>
        {
            long planId;
            if (id is long existing && database.Plans.Get(existing) is PlanRecord record)
            {
                planId = existing;
                await database.Plans.UpdateAsync(record with { Name = name, DefinitionJson = definition.ToJson(), Enabled = enabled, UpdatedUtc = now }).ConfigureAwait(false);
            }
            else
            {
                planId = await database.Plans.AddAsync(new PlanRecord { Name = name, Enabled = enabled, DefinitionJson = definition.ToJson(), CreatedUtc = now, UpdatedUtc = now }).ConfigureAwait(false);
            }

            string? refused = await RegisterAsync(database).ConfigureAwait(false);
            if (refused is not null && enabled)
            {
                await SwitchOffAsync(database, planId, now).ConfigureAwait(false);
            }

            return (planId, refused);
        }).ConfigureAwait(true);
    }

    /// <summary>Makes the scheduled tasks match the plans; Windows' message when it refused, otherwise null.</summary>
    private async Task<string?> RegisterAsync(Database database)
    {
        try
        {
            await PlanHost.ReconcileAsync(database).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            State.Log.Warning("Windows would not accept the scheduled tasks", ex);
            return ex.Message;
        }
    }

    /// <summary>A plan whose task Windows refused stays switched off; its task, if one is left, goes.</summary>
    private async Task SwitchOffAsync(Database database, long planId, long now)
    {
        if (database.Plans.Get(planId) is PlanRecord record)
        {
            await database.Plans.UpdateAsync(record with { Enabled = false, UpdatedUtc = now }).ConfigureAwait(false);
            _ = await RegisterAsync(database).ConfigureAwait(false);
        }
    }

    private void ShowMoreMenu(PlanRow plan, Control anchor)
    {
        PopupItem[] items = [new(Strings.Schedule.DeleteSchedule, IconName.Trash, Enabled: !Services.IsDemo, Tag: "delete")];
        KitMenu.ShowBelow(anchor, items, item =>
        {
            // Deleting cannot be undone: it asks first, like Stop watching a folder (it went at the first click).
            if (item.Tag is "delete" && FindForm() is Form owner
                && MessageDialog.Confirm(owner, string.Format(CultureInfo.CurrentCulture, Strings.Schedule.DeleteTitle, plan.Name), Strings.Schedule.DeleteText, Strings.Kit.Cancel, Strings.Schedule.DeleteSchedule, destructive: true))
            {
                UiTask.Run(ChangePlansAsync(db => db.Plans.RemoveAsync(plan.Id)));
            }
        });
    }

    private Task AddPlanAsync(string name, PlanDefinition definition) =>
        ChangePlansAsync(db => db.Plans.AddAsync(new PlanRecord { Name = name, DefinitionJson = definition.ToJson(), CreatedUtc = Services.Clock.UtcNow.UtcTicks, UpdatedUtc = Services.Clock.UtcNow.UtcTicks }));

    /// <summary>The plan's switch; when Windows refuses the task, the plan stays switched off and a toast says why.</summary>
    private async Task SetEnabledAsync(PlanRow plan, bool enabled)
    {
        if (Services.IsDemo)
        {
            return;
        }

        Database database = Services.Database;
        long now = Services.Clock.UtcNow.UtcTicks;
        string? refused = await Task.Run(async () =>
        {
            if (database.Plans.Get(plan.Id) is not PlanRecord record)
            {
                return null;
            }

            await database.Plans.UpdateAsync(record with { Enabled = enabled, UpdatedUtc = now }).ConfigureAwait(false);
            string? message = await RegisterAsync(database).ConfigureAwait(false);
            if (message is null || !enabled)
            {
                return null;
            }

            await SwitchOffAsync(database, plan.Id, now).ConfigureAwait(false);
            return message;
        }).ConfigureAwait(true);
        if (refused is not null)
        {
            Services.ShowToast?.Invoke(new ToastContent(Severity.Critical, plan.Name, string.Format(CultureInfo.CurrentCulture, Strings.Schedule.ErrorWindows, refused)));
        }

        Reload();
    }

    private Task SaveDefinitionAsync(long planId, PlanDefinition definition) =>
        ChangePlansAsync(db => db.Plans.Get(planId) is PlanRecord plan ? db.Plans.UpdateAsync(plan with { DefinitionJson = definition.ToJson(), UpdatedUtc = Services.Clock.UtcNow.UtcTicks }) : Task.CompletedTask);

    /// <summary>
    /// Writes off the UI thread, makes the scheduled tasks match, then reloads. A task Windows would not remove (a
    /// deleted plan's) is retried at the next start and never runs a scan.
    /// </summary>
    private async Task ChangePlansAsync(Func<Database, Task> change)
    {
        if (Services.IsDemo)
        {
            return;
        }

        Database database = Services.Database;
        await Task.Run(async () =>
        {
            await change(database).ConfigureAwait(false);
            _ = await RegisterAsync(database).ConfigureAwait(false);
        }).ConfigureAwait(true);
        Reload();
    }

    private async Task RunNowAsync(PlanRow plan)
    {
        List<long> roots = plan.Definition.Roots is { Length: > 0 } chosen ? [.. chosen] : await Task.Run(() => Services.Database.Roots.List().Select(r => r.Id).ToList()).ConfigureAwait(true);
        await Services.StartScanAsync([.. roots.Select(id => new ScanScope(id))], plan.Definition.Depth, plan.Name).ConfigureAwait(true);
    }
}
