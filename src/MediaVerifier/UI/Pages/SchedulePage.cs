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
        _new.Click += (_, _) => ShowTemplates();
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
            long id = plan.Id;
            toggle.CheckedChanged += (_, _) => _ = SetEnabledAsync(id, toggle.Checked);
            run.Click += (_, _) => _ = RunNowAsync(plan);
            edit.Click += (_, _) => ShowEditMenu(plan, edit);
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
            int rowTop = top + S(RowHeight * i);
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
        _pass.Bounds = new Rectangle(w - S(392), card + S(104), S(376), S(40));
        Height = card + S(196) + S(24);
    }

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
            int cardHeight = S((RowHeight * _data.Plans.Count) + 8);
            Card.Draw(g, new Rectangle(0, S(48), w, cardHeight), null, VisualState.Rest, false, DeviceDpi);
            for (int i = 0; i < _data.Plans.Count; i++)
            {
                PlanRow plan = _data.Plans[i];
                int rowTop = S(52) + S(RowHeight * i);
                if (i > 0)
                {
                    KitPaint.HorizontalLine(g, t.Stroke, S(16), w - S(16), rowTop, SF(1));
                }

                KitPaint.DrawText(g, plan.Name, type.BodyStrong, t.Text, S(72), rowTop + S(25), w - S(520));
                KitPaint.DrawText(g, plan.Summary, type.Body, t.Text2, S(72), rowTop + S(46), w - S(520));
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
        Card.Draw(g, new Rectangle(0, card, w, S(196)), null, VisualState.Rest, false, DeviceDpi);
        KitPaint.DrawText(g, Strings.Schedule.Verification, type.CaptionStrong, t.Text3, S(16), card + S(23), S(200));
        DataPaint.Donut(g, new PointF(S(64), card + S(109)), SF(40), SF(9), _data.VerifiedShare, t.Accent);
        string percent = (_data.VerifiedShare * 100).ToString("0", CultureInfo.CurrentCulture) + " %";
        KitPaint.DrawTextCentred(g, percent.Replace(" %", "%", StringComparison.Ordinal), type.BodyStrong, t.Text, S(64), card + S(109));
        KitPaint.DrawText(g, Strings.Schedule.VerificationLine1, type.Body, t.Text, S(132), card + S(57), w - S(560));
        KitPaint.DrawText(g, Strings.Schedule.VerificationLine2, type.Body, t.Text, S(132), card + S(77), w - S(560));
        KitPaint.DrawText(g, Strings.Schedule.ReReadEvery, type.Body, t.Text, S(132), card + S(121), S(190));
        KitPaint.DrawText(g, Strings.Schedule.Days, type.Body, t.Text, S(395), card + S(121), S(80));
        KitPaint.DrawText(g, Strings.Schedule.SpendUpTo, type.Body, t.Text, S(132), card + S(161), S(80));
        KitPaint.DrawText(g, Strings.Schedule.MinutesEachRun, type.Body, t.Text, S(279), card + S(161), w - S(700));
        if (_data.OverdueFiles > 0)
        {
            string overdue = string.Format(CultureInfo.CurrentCulture, Strings.Schedule.OverdueCaption, UiFormat.Count(_data.OverdueFiles)) + Strings.Schedule.VerifyNowLink + ".";
            KitPaint.DrawText(g, overdue, type.Caption, t.Text3, w - S(392), card + S(161), S(376));
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

    private int VerificationTop() => S(48) + (_data is { Plans.Count: > 0 } d ? S((RowHeight * d.Plans.Count) + 8) : S(200)) + S(12);

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

    private void ShowTemplates()
    {
        HashSet<string> existing = [.. _data?.Plans.Select(p => p.Name) ?? []];
        PopupItem[] items = [.. PlanDefinition.Templates.Select(tp => new PopupItem(TemplateName(tp.Name), IconName.Schedule, Enabled: !existing.Contains(TemplateName(tp.Name)) && !Services.IsDemo, Tag: tp))];
        KitMenu.ShowBelow(_new, items, item =>
        {
            if (item.Tag is ValueTuple<string, PlanDefinition> template)
            {
                _ = AddPlanAsync(TemplateName(template.Item1), template.Item2);
            }
        });
    }

    private static string TemplateName(string name) => name switch
    {
        "Nightly verification" => Strings.Schedule.Nightly,
        "Weekly check" => Strings.Schedule.Weekly,
        _ => Strings.Schedule.MonthlyDeep,
    };

    private void ShowEditMenu(PlanRow plan, Control anchor)
    {
        PopupItem[] items =
        [
            new(Strings.Schedule.Quick, plan.Definition.Depth == ScanDepth.Quick ? IconName.Check : null, Tag: ScanDepth.Quick),
            new(Strings.Schedule.Standard, plan.Definition.Depth == ScanDepth.Standard ? IconName.Check : null, Tag: ScanDepth.Standard),
            new(Strings.Schedule.Deep, plan.Definition.Depth == ScanDepth.Deep ? IconName.Check : null, Tag: ScanDepth.Deep),
        ];
        KitMenu.ShowBelow(anchor, items, item =>
        {
            if (item.Tag is ScanDepth depth && depth != plan.Definition.Depth)
            {
                _ = SaveDefinitionAsync(plan.Id, plan.Definition with { Depth = depth });
            }
        });
    }

    private void ShowMoreMenu(PlanRow plan, Control anchor)
    {
        PopupItem[] items = [new(Strings.Schedule.DeleteSchedule, IconName.Trash, Enabled: !Services.IsDemo, Tag: "delete")];
        KitMenu.ShowBelow(anchor, items, item =>
        {
            if (item.Tag is "delete")
            {
                _ = ChangePlansAsync(db => db.Plans.RemoveAsync(plan.Id));
            }
        });
    }

    private Task AddPlanAsync(string name, PlanDefinition definition) =>
        ChangePlansAsync(db => db.Plans.AddAsync(new PlanRecord { Name = name, DefinitionJson = definition.ToJson(), CreatedUtc = Services.Clock.UtcNow.UtcTicks, UpdatedUtc = Services.Clock.UtcNow.UtcTicks }));

    private Task SetEnabledAsync(long planId, bool enabled) =>
        ChangePlansAsync(db => db.Plans.Get(planId) is PlanRecord plan ? db.Plans.UpdateAsync(plan with { Enabled = enabled, UpdatedUtc = Services.Clock.UtcNow.UtcTicks }) : Task.CompletedTask);

    private Task SaveDefinitionAsync(long planId, PlanDefinition definition) =>
        ChangePlansAsync(db => db.Plans.Get(planId) is PlanRecord plan ? db.Plans.UpdateAsync(plan with { DefinitionJson = definition.ToJson(), UpdatedUtc = Services.Clock.UtcNow.UtcTicks }) : Task.CompletedTask);

    /// <summary>Writes off the UI thread, makes the scheduled tasks match, then reloads.</summary>
    private async Task ChangePlansAsync(Func<Database, Task> change)
    {
        if (Services.IsDemo)
        {
            return;
        }

        try
        {
            await Task.Run(async () =>
            {
                await change(Services.Database).ConfigureAwait(false);
                await PlanHost.ReconcileAsync(Services.Database).ConfigureAwait(false);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            State.Log.Warning("Scheduled tasks could not be updated", ex);
        }

        Reload();
    }

    private async Task RunNowAsync(PlanRow plan)
    {
        List<long> roots = plan.Definition.Roots is { Length: > 0 } chosen ? [.. chosen] : await Task.Run(() => Services.Database.Roots.List().Select(r => r.Id).ToList()).ConfigureAwait(true);
        await Services.StartScanAsync([.. roots.Select(id => new ScanScope(id))], plan.Definition.Depth, plan.Name).ConfigureAwait(true);
    }
}
