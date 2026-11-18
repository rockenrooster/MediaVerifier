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

/// <summary>The Dashboard's <c>Create schedule</c> (subplan 13): adds the first two templates when there is no plan yet.</summary>
internal sealed record CreateScheduleRequest;

/// <summary>
/// Schedule (subplan 09, Schedule; tasks UI-08 and SCH-07): the plans with their switch, next run and last
/// result, Run now, Edit and more; the verification card. Geometry from docs/design/schedule-dark.svg with the
/// content origin at the page's top left.
/// </summary>
internal sealed class SchedulePage : PageBase
{
    private const int RowHeight = 68;

    /// <summary>The empty state's column: icon, headline, body and its button (150 px), centred in 200 px.</summary>
    private const int EmptyHeight = 200;

    /// <summary>The empty state with the preset buttons 12 px under its button.</summary>
    private const int EmptyArea = 232;

    private readonly KitButton _new = new() { Kind = ButtonKind.Secondary, Icon = IconName.Add, Text = Strings.Schedule.NewSchedule, TabIndex = 0 };
    private readonly EmptyState _empty = new() { Icon = IconName.Schedule, Headline = Strings.Schedule.EmptyHeadline, Body = Strings.Schedule.EmptyText, Visible = false, TabIndex = 1 };
    private readonly KitButton _nightly = new() { Kind = ButtonKind.Secondary, Text = Strings.Schedule.Nightly, Visible = false, TabIndex = 2 };
    private readonly KitButton _weekly = new() { Kind = ButtonKind.Secondary, Text = Strings.Schedule.Weekly, Visible = false, TabIndex = 3 };
    private readonly NumberBox _scrubDays = new() { Minimum = 1, Maximum = 3650, Inset = false, AccessibleName = Strings.Schedule.ReReadEvery, TabIndex = 1000 };
    private readonly NumberBox _budget = new() { Minimum = 1, Maximum = 1440, Inset = false, AccessibleName = Strings.Schedule.SpendUpTo, TabIndex = 1001 };
    private readonly InfoBar _pass = new() { Tier = Severity.Low };
    private readonly KitLink _verifyNow = new() { Text = Strings.Schedule.VerifyNowLink, Visible = false, TabIndex = 1002 };
    private readonly System.Windows.Forms.Timer _commit = new() { Interval = 500 };
    private readonly List<PlanControls> _rows = [];
    private ScheduleData? _data;
    private bool _binding;
    private bool _verificationPending;
    private bool _adding;

    public SchedulePage(UiServices services)
        : base(services)
    {
        _new.Click += (_, _) => UiTask.Run(EditAsync(null));
        _empty.SetButton(Strings.Schedule.NewSchedule).Click += (_, _) => UiTask.Run(EditAsync(null));

        // Subplan 09, Schedule: the empty state's presets create those plans (subplan 13 templates). They exist only
        // while there is no plan: a second click before the page hid them added the plan twice.
        _nightly.Click += (_, _) => UiTask.Run(AddPlansAsync([(Strings.Schedule.Nightly, PlanDefinition.Templates[0].Definition)], onlyIntoEmpty: true));
        _weekly.Click += (_, _) => UiTask.Run(AddPlansAsync([(Strings.Schedule.Weekly, PlanDefinition.Templates[1].Definition)], onlyIntoEmpty: true));

        // The boxes save once the typing stops or focus leaves them: each save flushes settings.json to disk, and it
        // ran on every key and every arrow step.
        _scrubDays.ValueChanged += (_, _) => VerificationEdited();
        _budget.ValueChanged += (_, _) => VerificationEdited();
        _scrubDays.Leave += (_, _) => CommitVerification();
        _budget.Leave += (_, _) => CommitVerification();
        _commit.Tick += (_, _) => CommitVerification();
        _verifyNow.Click += (_, _) => UiTask.Run(VerifyNowAsync());
        Controls.AddRange([_new, _empty, _nightly, _weekly, _scrubDays, _budget, _pass, _verifyNow]);
    }

    public override PageId Id => PageId.Schedule;

    /// <summary>
    /// <see cref="CreateScheduleRequest"/>: the Dashboard's Create schedule adds the first two templates (subplan 13); it
    /// only opened this page. Pressed again, or with plans already there, it adds nothing.
    /// </summary>
    public override void HandleRequest(object request)
    {
        if (request is CreateScheduleRequest)
        {
            UiTask.Run(AddPlansAsync([(Strings.Schedule.Nightly, PlanDefinition.Templates[0].Definition), (Strings.Schedule.Weekly, PlanDefinition.Templates[1].Definition)], onlyIntoEmpty: true));
        }
    }

    /// <summary>The page scrolls when its plans and the verification card do not fit (they were cut off).</summary>
    public override int MeasureContentHeight(int width) => VerificationTop(width) + S(VerificationHeight(width)) + S(24);

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override void OnDeactivated()
    {
        CommitVerification();
        base.OnDeactivated();
    }

    protected override object? LoadData() => Services.Data.LoadSchedule();

    protected override void ApplyData(object data)
    {
        _data = (ScheduleData)data;
        _binding = true;

        // A box being typed in keeps what is typed: the settings already hold it. The focus is on the box's edit
        // control, so Focused never applied, and the reload after each commit rewrote the text under the caret.
        if (!_scrubDays.ContainsFocus)
        {
            _scrubDays.Value = State.Settings.Scanning.ScrubPeriodDays;
        }

        if (!_budget.ContainsFocus)
        {
            _budget.Value = State.Settings.Scanning.ScrubBudgetMinutes;
        }

        _binding = false;
        UpdatePassText();
        BindRows();
        bool empty = _data.Plans.Count == 0;
        _empty.Visible = _nightly.Visible = _weekly.Visible = empty;
        _verifyNow.Visible = _data.OverdueFiles > 0;
        OnSurface();
        PerformLayout();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int w = Width;
        bool stacked = Stacked(w);
        _new.Bounds = new Rectangle(w - S(_new.PreferredLogicalWidth), S(1), S(_new.PreferredLogicalWidth), S(32));
        int top = S(52);
        for (int i = 0; i < _rows.Count; i++)
        {
            int rowTop = top + S(RowPitch(w) * i);
            PlanControls row = _rows[i];
            row.Toggle.Bounds = new Rectangle(S(16), rowTop + S(24), S(40), S(20));
            row.Run.Bounds = new Rectangle(w - S(180), rowTop + S(20), S(74), S(28));
            row.Edit.Bounds = new Rectangle(w - S(99), rowTop + S(20), S(47), S(28));
            row.More.Bounds = new Rectangle(w - S(44), rowTop + S(20), S(28), S(28));
        }

        _empty.Bounds = new Rectangle(0, S(48), w, S(EmptyHeight));
        int presetsTop = S(48) + ((S(EmptyHeight) - S(150)) / 2) + S(150) + S(12);
        int nightly = S(_nightly.PreferredLogicalWidth);
        int weekly = S(_weekly.PreferredLogicalWidth);
        int left = (w - (nightly + S(8) + weekly)) / 2;
        _nightly.Bounds = new Rectangle(left, presetsTop, nightly, S(32));
        _weekly.Bounds = new Rectangle(left + nightly + S(8), presetsTop, weekly, S(32));
        int card = VerificationTop(w);
        _scrubDays.Bounds = new Rectangle(S(323), card + S(105), S(64), S(32));
        _budget.Bounds = new Rectangle(S(208), card + S(145), S(63), S(32));
        _pass.Bounds = stacked
            ? new Rectangle(S(132), card + S(196), Math.Min(S(376), w - S(148)), S(40))
            : new Rectangle(w - S(392), card + S(104), S(376), S(40));
        if (_data is { OverdueFiles: > 0 } d)
        {
            // "verify now" is a link after the caption's text (subplan 09): it was painted as plain text.
            (int x, int centre) = OverdueCaptionAt(w, card);
            int caption = KitPaint.MeasureWidth(OverdueText(d), Type.Caption);
            _verifyNow.Bounds = new Rectangle(x + caption - S(2), centre - S(16), S(_verifyNow.PreferredLogicalWidth), S(32));
        }
    }

    /// <summary>
    /// Too narrow for the pass note beside the settings (the smallest window): it goes under them, so the card's text
    /// keeps its width. Side by side, the text was cut to "Every file should be re-read and compared with i…".
    /// </summary>
    private bool Stacked(int width) => width < S(980);

    /// <summary>A plan row's height: on a narrow page the summary may take a second line (it was cut: "…no time li…").</summary>
    private int RowPitch(int width) => Stacked(width) ? 84 : RowHeight;

    private int VerificationHeight(int width) => Stacked(width) ? 288 : 196;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ThemeTokens t = Tokens;
        TypeRamp type = Type;
        Graphics g = e.Graphics;
        int w = Width;
        bool stacked = Stacked(w);
        int pitch = RowPitch(w);
        KitPaint.DrawText(g, Strings.Schedule.Intro, type.Body, t.Text2, 0, S(17), w - S(160));
        if (_data is null)
        {
            return;
        }

        if (_data.Plans.Count > 0)
        {
            int cardHeight = S((pitch * _data.Plans.Count) + 8);
            Card.Draw(g, new Rectangle(0, S(48), w, cardHeight), null, VisualState.Rest, false, DeviceDpi);
            for (int i = 0; i < _data.Plans.Count; i++)
            {
                PlanRow plan = _data.Plans[i];
                int rowTop = S(52) + S(pitch * i);
                if (i > 0)
                {
                    KitPaint.HorizontalLine(g, t.Stroke, S(16), w - S(16), rowTop, SF(1));
                }

                KitPaint.DrawText(g, plan.Name, type.BodyStrong, t.Text, S(72), rowTop + S(25), w - S(520));
                if (stacked)
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
                if (plan.ResultTier is Severity tier)
                {
                    BadgePainter.PaintStatusPill(g, w - S(302), rowTop + S(37), plan.LastResult, tier, DeviceDpi);
                }
                else
                {
                    BadgePainter.PaintPill(g, w - S(302), rowTop + S(37), plan.LastResult, DeviceDpi);
                }
            }
        }

        // Verification card.
        int card = VerificationTop(w);
        int textWidth = stacked ? w - S(148) : w - S(560);
        Card.Draw(g, new Rectangle(0, card, w, S(VerificationHeight(w))), null, VisualState.Rest, false, DeviceDpi);
        KitPaint.DrawText(g, Strings.Schedule.Verification, type.CaptionStrong, t.Text3, S(16), card + S(23), S(200));
        DataPaint.Donut(g, new PointF(S(64), card + S(109)), SF(40), SF(9), _data.VerifiedShare, t.Accent);
        string percent = (_data.VerifiedShare * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
        KitPaint.DrawTextCentred(g, percent, type.BodyStrong, t.Text, S(64), card + S(109));
        KitPaint.DrawText(g, Strings.Schedule.VerificationLine1, type.Body, t.Text, S(132), card + S(57), textWidth);
        KitPaint.DrawText(g, Strings.Schedule.VerificationLine2, type.Body, t.Text, S(132), card + S(77), textWidth);
        KitPaint.DrawText(g, Strings.Schedule.ReReadEvery, type.Body, t.Text, S(132), card + S(121), S(190));
        KitPaint.DrawText(g, Strings.Schedule.Days, type.Body, t.Text, S(395), card + S(121), S(80));
        KitPaint.DrawText(g, Strings.Schedule.SpendUpTo, type.Body, t.Text, S(132), card + S(161), S(80));
        KitPaint.DrawText(g, Strings.Schedule.MinutesEachRun, type.Body, t.Text, S(279), card + S(161), stacked ? w - S(295) : w - S(700));
        if (_data.OverdueFiles > 0)
        {
            (int x, int centre) = OverdueCaptionAt(w, card);
            KitPaint.DrawText(g, OverdueText(_data), type.Caption, t.Text3, x, centre, stacked ? w - S(148) : S(376));
            KitPaint.DrawText(g, ".", type.Caption, t.Text3, _verifyNow.Right - S(2), centre, S(8));
        }
    }

    protected override void OnThemeChanged()
    {
        base.OnThemeChanged();
        OnSurface();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_verificationPending)
            {
                _verificationPending = false;
                State.SaveSettings();
            }

            _commit.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string OverdueText(ScheduleData data) => string.Format(CultureInfo.CurrentCulture, Strings.Schedule.OverdueCaption, UiFormat.Count(data.OverdueFiles));

    /// <summary>Where the overdue caption starts and its centre line: under the pass note, or beside the budget.</summary>
    private (int X, int Centre) OverdueCaptionAt(int width, int card) => Stacked(width) ? (S(132), card + S(253)) : (width - S(392), card + S(161));

    /// <summary>
    /// The row controls follow the plans: kept across reloads (a switch or button the keyboard just used keeps its
    /// focus; they were all recreated after every change), added or removed only when the number of plans changes.
    /// Their tab order follows the rows, before the verification card.
    /// </summary>
    private void BindRows()
    {
        IReadOnlyList<PlanRow> plans = _data?.Plans ?? [];
        while (_rows.Count > plans.Count)
        {
            PlanControls last = _rows[^1];
            _rows.RemoveAt(_rows.Count - 1);
            last.Remove(Controls);
        }

        while (_rows.Count < plans.Count)
        {
            PlanControls row = new(_rows.Count);
            int index = row.Index;
            row.Toggle.CheckedChanged += (_, _) =>
            {
                if (!_binding)
                {
                    UiTask.Run(SetEnabledAsync(row));
                }
            };
            row.Run.Click += (_, _) => PlanAt(index, plan => UiTask.Run(RunNowAsync(plan)));
            row.Edit.Click += (_, _) => PlanAt(index, plan => UiTask.Run(EditAsync(plan)));
            row.More.Click += (_, _) => PlanAt(index, plan => ShowMoreMenu(plan, row.More));
            _rows.Add(row);
            Controls.AddRange([row.Toggle, row.Run, row.Edit, row.More]);
        }

        _binding = true;
        for (int i = 0; i < plans.Count; i++)
        {
            // A switch whose change is still being stored keeps what the user set; the store catches up with it.
            if (!_rows[i].Changing)
            {
                _rows[i].Toggle.Checked = plans[i].Enabled;
            }

            _rows[i].Toggle.AccessibleName = plans[i].Name;
        }

        _binding = false;
    }

    /// <summary>Runs <paramref name="action"/> on the plan shown in a row now.</summary>
    private void PlanAt(int index, Action<PlanRow> action)
    {
        if (_data is not null && index < _data.Plans.Count)
        {
            action(_data.Plans[index]);
        }
    }

    /// <summary>The row controls sit on the plans card, so their corners blend with Surface, not the page.</summary>
    private void OnSurface()
    {
        foreach (PlanControls row in _rows)
        {
            row.Toggle.BackColor = Tokens.Surface;
            row.Run.BackColor = Tokens.Surface;
            row.Edit.BackColor = Tokens.Surface;
            row.More.BackColor = Tokens.Surface;
        }

        _scrubDays.BackColor = Tokens.Surface;
        _budget.BackColor = Tokens.Surface;
        _pass.BackColor = Tokens.Surface;
        _verifyNow.BackColor = Tokens.Surface;
    }

    private int VerificationTop(int width) => S(48) + (_data is { Plans.Count: > 0 } d ? S((RowPitch(width) * d.Plans.Count) + 8) : S(EmptyArea)) + S(12);

    /// <summary>
    /// The full-pass note at the budget as it stands (it kept the day count of the previous budget while a new one
    /// was typed), "1 day" for one day; hidden while no file has a fingerprint, when there is nothing to estimate.
    /// </summary>
    private void UpdatePassText()
    {
        if (_data is null)
        {
            return;
        }

        int minutes = State.Settings.Scanning.ScrubBudgetMinutes;
        string budget = minutes % 60 == 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Schedule.Hours, minutes / 60) : string.Format(CultureInfo.CurrentCulture, Strings.Schedule.MinutesShort, minutes);
        int days = _data.FullPassDays(minutes);
        _pass.Text = days == 1
            ? string.Format(CultureInfo.CurrentCulture, Strings.Schedule.FullPassOneDay, budget)
            : string.Format(CultureInfo.CurrentCulture, Strings.Schedule.FullPass, budget, UiFormat.Count(days));
        _pass.Visible = _data.BaselineFiles > 0;
    }

    /// <summary>A verification box changed: the settings and the note follow at once; the file is written once the typing stops.</summary>
    private void VerificationEdited()
    {
        if (_binding || Services.IsDemo)
        {
            return;
        }

        State.Settings.Scanning.ScrubPeriodDays = _scrubDays.Value;
        State.Settings.Scanning.ScrubBudgetMinutes = _budget.Value;
        _verificationPending = true;
        UpdatePassText();
        _commit.Stop();
        _commit.Start();
    }

    /// <summary>Writes a pending change and reloads: the donut and the overdue count depend on the period.</summary>
    private void CommitVerification()
    {
        _commit.Stop();
        if (!_verificationPending)
        {
            return;
        }

        _verificationPending = false;
        State.SaveSettings();
        Reload();
    }

    /// <summary>The overdue caption's link: the same verify-only Quick scan of every folder as the Dashboard's Verify now.</summary>
    private async Task VerifyNowAsync()
    {
        if (Services.IsDemo)
        {
            return;
        }

        List<long> roots = await Task.Run(() => Services.Database.Roots.List().Select(r => r.Id).ToList()).ConfigureAwait(true);
        if (roots.Count == 0)
        {
            Services.ShowScanNotStarted(Strings.Shell.AddFolderFirst);
            return;
        }

        await Services.StartScanAsync([.. roots.Select(id => new ScanScope(id))], ScanDepth.Quick, Strings.Recommendations.VerifyNow, new ScanOptions { VerifyOnly = true }).ConfigureAwait(true);
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

        // The next run counts from the plan's first day, as the task and the row do (a new plan's is today).
        DateOnly anchor = plan?.Anchor ?? DateOnly.FromDateTime(DateTime.Now);

        // The other plans: the editor warns when one of them fires at the same minute (only one scan runs at a time).
        List<PlanRow> others = [.. (_data?.Plans ?? []).Where(p => p.Id != plan?.Id)];
        _ = ScheduleEditorDialog.Ask(
            owner,
            plan?.Name ?? Strings.Schedule.Weekly,
            start,
            roots,
            anchor,
            plan is not null,
            async (name, edited) =>
            {
                (long id, string? refused) = await StorePlanAsync(planId, name, edited, enabled).ConfigureAwait(true);
                planId = id;
                return refused;
            },
            others);
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
                await SwitchOffAsync(database, [planId], now).ConfigureAwait(false);
            }

            return (planId, refused);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Makes the scheduled tasks match the plans; Windows' message when it refused, otherwise null. The late binder
    /// turns Windows' errors into .NET exceptions of several kinds; each is a refusal, never an unhandled error that
    /// leaves a plan switched on without a task.
    /// </summary>
    private async Task<string?> RegisterAsync(Database database)
    {
        try
        {
            await PlanHost.ReconcileAsync(database).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or IOException)
        {
            State.Log.Warning("Windows would not accept the scheduled tasks", ex);
            return ex.Message;
        }
    }

    /// <summary>Plans whose tasks Windows refused stay switched off; their tasks, if any are left, go.</summary>
    private async Task SwitchOffAsync(Database database, IReadOnlyList<long> planIds, long now)
    {
        bool changed = false;
        foreach (long planId in planIds)
        {
            if (database.Plans.Get(planId) is PlanRecord record)
            {
                await database.Plans.UpdateAsync(record with { Enabled = false, UpdatedUtc = now }).ConfigureAwait(false);
                changed = true;
            }
        }

        if (changed)
        {
            _ = await RegisterAsync(database).ConfigureAwait(false);
        }
    }

    /// <summary>The row's more menu (subplan 09): Duplicate, and Delete… which asks first.</summary>
    private void ShowMoreMenu(PlanRow plan, Control anchor)
    {
        PopupItem[] items =
        [
            new(Strings.Schedule.Duplicate, IconName.Copy, Enabled: !Services.IsDemo, Tag: "duplicate"),
            PopupItem.Separator,
            new(Strings.Schedule.DeleteEllipsis, IconName.Trash, Enabled: !Services.IsDemo, Tag: "delete"),
        ];
        KitMenu.ShowBelow(anchor, items, item =>
        {
            if (item.Tag is "duplicate")
            {
                // The copy starts switched off: on at once, it would fire at the original's minute, and only one scan runs.
                UiTask.Run(DuplicateAsync(plan));
            }

            // Deleting cannot be undone: it asks first, like Stop watching a folder (it went at the first click).
            else if (item.Tag is "delete" && FindForm() is Form owner
                && MessageDialog.Confirm(owner, string.Format(CultureInfo.CurrentCulture, Strings.Schedule.DeleteTitle, plan.Name), Strings.Schedule.DeleteText, Strings.Kit.Cancel, Strings.Schedule.DeleteSchedule, destructive: true))
            {
                UiTask.Run(ChangePlansAsync(db => db.Plans.RemoveAsync(plan.Id)));
            }
        });
    }

    private async Task DuplicateAsync(PlanRow plan)
    {
        _ = await StorePlanAsync(null, string.Format(CultureInfo.CurrentCulture, Strings.Schedule.CopyName, plan.Name), plan.Definition, enabled: false).ConfigureAwait(true);
        Reload();
    }

    /// <summary>
    /// Adds plans switched on and registers their tasks. When Windows refuses, they stay switched off and a toast says
    /// why (a preset stayed on without a task, and nothing was said). <paramref name="onlyIntoEmpty"/> adds them only
    /// while there is no plan at all.
    /// </summary>
    private async Task AddPlansAsync(IReadOnlyList<(string Name, PlanDefinition Definition)> plans, bool onlyIntoEmpty = false)
    {
        // One addition at a time: a double click on a preset made the plan twice.
        if (Services.IsDemo || _adding)
        {
            return;
        }

        _adding = true;
        Database database = Services.Database;
        long now = Services.Clock.UtcNow.UtcTicks;
        string? refused;
        try
        {
            refused = await Task.Run(async () =>
            {
                if (onlyIntoEmpty && database.Plans.List().Count > 0)
                {
                    return null;
                }

                List<long> added = [];
                foreach ((string name, PlanDefinition definition) in plans)
                {
                    added.Add(await database.Plans.AddAsync(new PlanRecord { Name = name, Enabled = true, DefinitionJson = definition.ToJson(), CreatedUtc = now, UpdatedUtc = now }).ConfigureAwait(false));
                }

                string? message = await RegisterAsync(database).ConfigureAwait(false);
                if (message is not null)
                {
                    await SwitchOffAsync(database, added, now).ConfigureAwait(false);
                }

                return message;
            }).ConfigureAwait(true);
        }
        finally
        {
            _adding = false;
        }

        if (refused is not null)
        {
            Services.ShowToast?.Invoke(new ToastContent(Severity.Critical, plans.Count == 1 ? plans[0].Name : Strings.Pages.Schedule, string.Format(CultureInfo.CurrentCulture, Strings.Schedule.ErrorWindows, refused)));
        }

        Reload();
    }

    /// <summary>
    /// The plan's switch; when Windows refuses the task, the plan stays switched off and a toast says why. One change
    /// per row at a time: flipped again while its change is stored, the switch's newest state is stored next (two
    /// updates raced, and one could delete the task the other had just made). The switch stays enabled meanwhile:
    /// disabled, it sent the keyboard focus on to Run now, and the next Space started a scan.
    /// </summary>
    private async Task SetEnabledAsync(PlanControls row)
    {
        if (Services.IsDemo || row.Changing)
        {
            return;
        }

        Database database = Services.Database;
        row.Changing = true;
        try
        {
            while (!row.Toggle.IsDisposed && _data is not null && row.Index < _data.Plans.Count)
            {
                PlanRow plan = _data.Plans[row.Index];
                bool enabled = row.Toggle.Checked;
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

                    await SwitchOffAsync(database, [plan.Id], now).ConfigureAwait(false);
                    return message;
                }).ConfigureAwait(true);

                if (refused is not null)
                {
                    Services.ShowToast?.Invoke(new ToastContent(Severity.Critical, plan.Name, string.Format(CultureInfo.CurrentCulture, Strings.Schedule.ErrorWindows, refused)));
                    break;
                }

                if (row.Toggle.Checked == enabled)
                {
                    break;
                }
            }
        }
        finally
        {
            row.Changing = false;
        }

        Reload();
    }

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

    /// <summary>
    /// Run now: the plan's folders at its depth, named by their folders on Scans (not by the plan's name). With no
    /// folder left, it says so instead of doing nothing.
    /// </summary>
    private async Task RunNowAsync(PlanRow plan)
    {
        if (Services.IsDemo)
        {
            return;
        }

        List<RootRecord> roots = await Task.Run(Services.Database.Roots.List).ConfigureAwait(true);
        List<RootRecord> chosen = plan.Definition.Roots is { Length: > 0 } ids ? [.. roots.Where(r => ids.Contains(r.Id))] : roots;
        if (chosen.Count == 0)
        {
            Services.ShowScanNotStarted(Strings.Shell.AddFolderFirst);
            return;
        }

        string folders = plan.Definition.Roots is { Length: > 0 }
            ? UiFormat.JoinNames([.. chosen.Select(r => r.Label ?? Path.GetFileName(r.Path.TrimEnd('\\')))])
            : Strings.Scans.AllFolders;
        await Services.StartScanAsync([.. chosen.Select(r => new ScanScope(r.Id))], plan.Definition.Depth, folders).ConfigureAwait(true);
    }

    /// <summary>The switch, Run now, Edit and more of one plan row.</summary>
    private sealed class PlanControls(int index)
    {
        public int Index { get; } = index;

        /// <summary>True while the switch's change is being stored.</summary>
        public bool Changing { get; set; }

        public ToggleSwitch Toggle { get; } = new() { TabIndex = 10 + (index * 4) };

        public KitButton Run { get; } = new() { Kind = ButtonKind.Secondary, Text = Strings.Schedule.RunNow, TabIndex = 11 + (index * 4) };

        public KitButton Edit { get; } = new() { Kind = ButtonKind.Secondary, Text = Strings.Schedule.Edit, TabIndex = 12 + (index * 4) };

        public IconButton More { get; } = new() { Icon = IconName.More, ToolTipText = Strings.Kit.MoreActions, TabIndex = 13 + (index * 4) };

        public void Remove(Control.ControlCollection controls)
        {
            foreach (Control control in new Control[] { Toggle, Run, Edit, More })
            {
                controls.Remove(control);
                control.Dispose();
            }
        }
    }
}
