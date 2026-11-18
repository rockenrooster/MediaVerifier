using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// The plan editor (subplan 09, Schedule; reference <c>schedule-edit</c>; task SCH-07): name, folders, depth, repeat,
/// days or interval, time, time limit, conditions and a live summary with the next run from the calculator. Save
/// stores the plan and registers it with Windows while the editor is open; when Windows refuses the task, the plan is
/// saved switched off and a Critical info bar above the buttons says why.
/// </summary>
internal sealed class ScheduleEditorDialog : KitDialog
{
    private const int FormHeight = 507;
    private static readonly int?[] Limits = [null, 30, 60, 120, 240, 480];
    private static readonly DayOfWeek[] Week = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
    private readonly IReadOnlyList<RootRecord> _roots;
    private readonly KitTextBox _name = new() { Inset = true, TabIndex = 0 };
    private readonly DropDown _folders = new() { TabIndex = 1 };
    private readonly SegmentedControl _depth = new() { TabIndex = 2 };
    private readonly SegmentedControl _repeat = new() { TabIndex = 3 };
    private readonly Chip[] _days;
    private readonly NumberBox _number = new() { Inset = true, Minimum = 1, Maximum = 365, TabIndex = 11 };
    private readonly KitTextBox _time = new() { Inset = true, MaxLength = 5, TabIndex = 12 };
    private readonly DropDown _limit = new() { TabIndex = 13 };
    private readonly KitCheckBox _plugged = new() { Text = Strings.Schedule.PluggedIn, TabIndex = 14 };
    private readonly KitCheckBox _fullScreen = new() { Text = Strings.Schedule.WaitFullScreen, TabIndex = 15 };
    private readonly KitCheckBox _wake = new() { Text = Strings.Schedule.Wake, TabIndex = 16 };
    private readonly InfoBar _refused = new() { Tier = Severity.Critical, Visible = false, TabIndex = 17 };
    private readonly KitButton _save;
    private readonly KitButton _cancel;
    private readonly DateOnly _anchor;
    private readonly Func<string, PlanDefinition, Task<string?>>? _store;

    /// <summary>Why Save is disabled, shown in the summary box; null when the form is valid.</summary>
    private string? _problem;

    /// <summary>While Save stores the plan and registers it, the editor cannot be closed (Esc closed it and the save went on unseen).</summary>
    private bool _saving;

    private ScheduleEditorDialog(string name, PlanDefinition plan, IReadOnlyList<RootRecord> roots, DateOnly anchor, bool editing, Func<string, PlanDefinition, Task<string?>>? store)
        : base(editing ? Strings.Schedule.EditSchedule : Strings.Schedule.NewSchedule, DialogWidth.Form, closeButton: true)
    {
        _roots = roots;
        _anchor = anchor;
        _store = store;
        CloseToolTip = Strings.Kit.CloseEsc;
        _name.Text = name;
        foreach (string folder in new[] { Strings.Schedule.AllFolders }.Concat(roots.Select(r => r.Label ?? Path.GetFileName(r.Path.TrimEnd('\\')))))
        {
            _folders.Items.Add(folder);
        }

        int rootIndex = plan.Roots is { Length: 1 } one ? roots.ToList().FindIndex(r => r.Id == one[0]) : -1;
        _folders.SelectedIndex = rootIndex + 1;
        foreach (string segment in new[] { Strings.Schedule.Quick, Strings.Schedule.Standard, Strings.Schedule.Deep })
        {
            _depth.Segments.Add(segment);
        }

        _depth.SelectedIndex = plan.Depth switch { ScanDepth.Quick => 0, ScanDepth.Deep => 2, _ => 1 };
        foreach (string segment in new[] { Strings.Schedule.Daily, Strings.Schedule.Weekly2, Strings.Schedule.Monthly, Strings.Schedule.EveryDays })
        {
            _repeat.Segments.Add(segment);
        }

        _repeat.SelectedIndex = plan.Trigger.Kind switch { TriggerKind.Daily => 0, TriggerKind.Monthly => 2, TriggerKind.EveryDays => 3, _ => 1 };
        _days = [.. Week.Select((d, i) => new Chip { Text = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d), On = plan.Trigger.DaysOfWeek.Contains(d), TabIndex = 4 + i })];
        foreach (Chip chip in _days)
        {
            chip.Toggled += (_, _) => Changed();
        }

        _number.Value = plan.Trigger.Kind == TriggerKind.EveryDays ? plan.Trigger.EveryDays ?? 2 : Math.Clamp(plan.Trigger.DayOfMonth ?? 1, 1, 28);
        _time.Text = plan.Trigger.Time;
        foreach (string limit in new[] { Strings.Schedule.NoLimit, Strings.Schedule.Minutes30, Strings.Schedule.Hour1, Strings.Schedule.Hours2, Strings.Schedule.Hours4, Strings.Schedule.Hours8 })
        {
            _limit.Items.Add(limit);
        }

        _limit.SelectedIndex = Math.Max(0, Array.IndexOf(Limits, plan.BudgetMinutes));
        _plugged.Checked = plan.Conditions.OnlyOnAcPower;
        _fullScreen.Checked = plan.Conditions.SkipWhenFullScreen;
        _wake.Checked = plan.Conditions.WakeComputer;
        Result = plan;

        LimitNumber();
        _name.TextChanged += (_, _) => Changed();
        _folders.SelectedIndexChanged += (_, _) => Changed();
        _depth.SelectedIndexChanged += (_, _) => Changed();
        _repeat.SelectedIndexChanged += (_, _) =>
        {
            LimitNumber();
            Relayout();
            Changed();
        };
        _number.ValueChanged += (_, _) => Changed();
        _time.TextChanged += (_, _) => Changed();

        // "3:00" is taken as 03:00 and shown so when the box loses focus.
        _time.Leave += (_, _) =>
        {
            if (NotificationPolicy.TryParseTime(_time.Text, out TimeOnly time))
            {
                _time.Text = time.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
        };
        _limit.SelectedIndexChanged += (_, _) => Changed();
        Body.Controls.AddRange([_name, _folders, _depth, _repeat, .. _days, _number, _time, _limit, _plugged, _fullScreen, _wake, _refused]);
        Body.Paint += (_, e) => PaintBody(e.Graphics);
        _cancel = AddButton(Strings.Kit.Cancel, ButtonKind.Secondary, DialogResult.Cancel);
        _save = AddButton(Strings.Kit.Save, ButtonKind.Primary, DialogResult.None);

        // An async void handler: a failure reaches the app's error handler (logged and shown), never a dead button.
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        BodyHeight = FormHeight;
        Changed();
    }

    /// <summary>The definition as edited (valid when Save was chosen).</summary>
    public PlanDefinition Result { get; private set; }

    public string PlanName => _name.Text.Trim();

    /// <summary>
    /// Shows the editor. Save calls <paramref name="store"/> with the name and definition; it stores the plan, registers
    /// it with Windows and returns null, or Windows' message when the task was refused (the plan is then stored
    /// switched off), which keeps the editor open with the message. True when the plan was stored and registered.
    /// </summary>
    public static bool Ask(Form owner, string name, PlanDefinition plan, IReadOnlyList<RootRecord> roots, DateOnly anchor, bool editing, Func<string, PlanDefinition, Task<string?>> store)
    {
        using ScheduleEditorDialog dialog = new(name, plan, roots, anchor, editing, store);
        return dialog.ShowOver(owner) == DialogResult.OK;
    }

    /// <summary>The dialog without showing it (render tests).</summary>
    internal static ScheduleEditorDialog Create(string name, PlanDefinition plan, IReadOnlyList<RootRecord> roots, DateOnly anchor) => new(name, plan, roots, anchor, true, null);

    protected override Control? InitialFocus() => _name;

    protected override void Cancel()
    {
        if (!_saving)
        {
            base.Cancel();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_saving && e.CloseReason == CloseReason.UserClosing && DialogResult != DialogResult.OK)
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    protected override void LayoutBody()
    {
        int x = S(120);
        int width = Math.Max(0, Body.Width - x);
        _name.Bounds = new Rectangle(x, S(5), width, S(32));
        _folders.Bounds = new Rectangle(x, S(49), width, S(32));
        _depth.Bounds = new Rectangle(x, S(93), S(196), S(32));
        _repeat.Bounds = new Rectangle(x, S(173), S(306), S(32));
        int kind = _repeat.SelectedIndex;
        int dx = x;
        foreach (Chip chip in _days)
        {
            int w = chip.GetPreferredSize(Size.Empty).Width;
            chip.Bounds = new Rectangle(dx, S(219), w, S(28));
            chip.Visible = kind == 1;
            dx += w + S(8);
        }

        // Monthly reads "Day [n]", Every … days "[n] days" (subplan 09: the box has its unit).
        int dayLabel = KitPaint.MeasureWidth(Strings.Schedule.Day, TypeRamp.For(DeviceDpi).Body) + S(8);
        _number.Bounds = new Rectangle(kind == 2 ? x + dayLabel : x, S(217), S(80), S(32));
        _number.Visible = kind is 2 or 3;
        _time.Bounds = new Rectangle(x, S(261), S(80), S(32));
        _limit.Bounds = new Rectangle(x, S(305), S(160), S(32));
        _plugged.Bounds = new Rectangle(new Point(x, S(366)), _plugged.GetPreferredSize(Size.Empty));
        _fullScreen.Bounds = new Rectangle(new Point(x, S(396)), _fullScreen.GetPreferredSize(Size.Empty));
        _wake.Bounds = new Rectangle(new Point(x, S(426)), _wake.GetPreferredSize(Size.Empty));
        _refused.Bounds = new Rectangle(0, S(FormHeight + 16), Body.Width, S(40));
    }

    private async Task SaveAsync()
    {
        Changed();
        if (!_save.Enabled || _store is null)
        {
            return;
        }

        // One save at a time; Save comes back afterwards when the form is still valid.
        _save.Enabled = false;
        _cancel.Enabled = false;
        _saving = true;
        string? refused;
        try
        {
            refused = await _store(PlanName, Result).ConfigureAwait(true);
        }
        finally
        {
            _saving = false;
            _cancel.Enabled = true;
            Changed();
        }

        if (refused is null)
        {
            Finish(DialogResult.OK);
            return;
        }

        _refused.Text = string.Format(CultureInfo.CurrentCulture, Strings.Schedule.ErrorWindows, refused);
        _refused.Visible = true;
        BodyHeight = FormHeight + 16 + 40;
    }

    private PlanDefinition Build()
    {
        TriggerKind kind = _repeat.SelectedIndex switch { 0 => TriggerKind.Daily, 2 => TriggerKind.Monthly, 3 => TriggerKind.EveryDays, _ => TriggerKind.Weekly };
        PlanTrigger trigger = new()
        {
            Kind = kind,
            Time = NotificationPolicy.TryParseTime(_time.Text, out TimeOnly time) ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : _time.Text.Trim(),
            DaysOfWeek = kind == TriggerKind.Weekly ? [.. Week.Where((_, i) => _days[i].On)] : [],
            DayOfMonth = kind == TriggerKind.Monthly ? _number.Value : null,
            EveryDays = kind == TriggerKind.EveryDays ? _number.Value : null,
        };
        return Result with
        {
            Roots = _folders.SelectedIndex > 0 ? [_roots[_folders.SelectedIndex - 1].Id] : null,
            Depth = _depth.SelectedIndex switch { 0 => ScanDepth.Quick, 2 => ScanDepth.Deep, _ => ScanDepth.Standard },
            Trigger = trigger,
            BudgetMinutes = Limits[Math.Clamp(_limit.SelectedIndex, 0, Limits.Length - 1)],
            Conditions = Result.Conditions with { OnlyOnAcPower = _plugged.Checked, SkipWhenFullScreen = _fullScreen.Checked, WakeComputer = _wake.Checked },
        };
    }

    private void Changed()
    {
        if (_save is null)
        {
            return;
        }

        PlanDefinition plan = Build();

        // What keeps Save disabled, in the words of subplan 09; the summary box shows it instead of a stale summary.
        _problem = PlanName.Length == 0 ? Strings.Schedule.ErrorName
            : plan.Trigger.Kind == TriggerKind.Weekly && plan.Trigger.DaysOfWeek.Length == 0 ? Strings.Schedule.ErrorDays
            : !NotificationPolicy.TryParseTime(plan.Trigger.Time, out _) ? Strings.Schedule.ErrorTime
            : plan.Validate() is { Count: > 0 } problems ? problems[0].TrimEnd('.')
            : null;
        bool valid = _problem is null;
        if (valid)
        {
            Result = plan;
        }

        _save.Enabled = valid;
        Body.Invalidate();
    }

    /// <summary>A month has a day 1 to 28 here (no 29th to 31st in every month); every N days goes up to 365.</summary>
    private void LimitNumber()
    {
        _number.Maximum = _repeat.SelectedIndex == 2 ? 28 : 365;
        if (_number.Value > _number.Maximum)
        {
            _number.Value = (int)_number.Maximum;
        }
    }

    private void PaintBody(Graphics g)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(DeviceDpi);
        (string Label, int Y)[] labels =
        [
            (Strings.Schedule.Name, 21), (Strings.Schedule.Folders, 65), (Strings.Schedule.Depth, 109), (Strings.Schedule.Repeat, 189),
            (Strings.Schedule.On, 233), (Strings.Schedule.At, 277), (Strings.Schedule.TimeLimitLabel, 321), (Strings.Schedule.Conditions, 374),
        ];
        foreach ((string label, int y) in labels)
        {
            if (label == Strings.Schedule.On && _repeat.SelectedIndex == 0)
            {
                continue;
            }

            KitPaint.DrawText(g, label, type.Body, t.Text, 0, S(y), S(112));
        }

        if (_repeat.SelectedIndex == 2)
        {
            KitPaint.DrawText(g, Strings.Schedule.Day, type.Body, t.Text, S(120), S(233), S(60));
        }
        else if (_repeat.SelectedIndex == 3)
        {
            KitPaint.DrawText(g, Strings.Schedule.Days, type.Body, t.Text, _number.Right + S(8), S(233), S(80));
        }

        string helper = _depth.SelectedIndex switch { 0 => Strings.Schedule.QuickHelper, 2 => Strings.Schedule.DeepHelper, _ => Strings.Schedule.StandardHelper };
        KitPaint.DrawText(g, helper, type.Caption, t.Text2, S(120), S(136), Body.Width - S(120));
        KitPaint.HorizontalLine(g, t.Stroke, 0, Body.Width, S(156), SF(1));
        KitPaint.HorizontalLine(g, t.Stroke, 0, Body.Width, S(348), SF(1));

        // The summary box: what will happen and when next.
        Rectangle box = new(0, S(461), Body.Width, S(46));
        KitPaint.FillRoundedRect(g, box, SF(4), t.Bg);
        KitPaint.StrokeRoundedRect(g, box, SF(4), t.Stroke, SF(1));
        if (_problem is not null)
        {
            // Why Save is disabled: the Critical shape with the message in its colour (never colour alone).
            Color critical = t.ForSeverity(Severity.Critical).Text;
            IconRenderer.DrawSeverity(g, Severity.Critical, SF(12), SF(476), 16, DeviceDpi, critical, t.Bg);
            KitPaint.DrawText(g, _problem, type.Body, critical, S(36), S(484), Body.Width - S(48));
            return;
        }

        IconRenderer.Draw(g, IconName.Clock, S(12), S(476), 16, DeviceDpi, t.Text2);
        PlanDefinition plan = Result;
        string scope = plan.Roots is { Length: 1 } one && _roots.FirstOrDefault(r => r.Id == one[0]) is RootRecord root
            ? root.Label ?? Path.GetFileName(root.Path.TrimEnd('\\'))
            : Strings.Schedule.AllFoldersLower;
        string when = ScheduleText.When(plan.Trigger);
        string line = string.Format(CultureInfo.CurrentCulture, Strings.Schedule.Summary, when, UiFormat.DepthName(plan.Depth), scope);
        KitPaint.DrawText(g, line, type.Body, t.Text, S(36), S(477), Body.Width - S(48));
        if (ScheduleCalculator.Next(plan.Trigger, DateTime.Now, _anchor) is DateTime next)
        {
            string nextLine = string.Format(CultureInfo.CurrentCulture, Strings.Schedule.NextRunLine, next.ToString("dddd d MMMM, HH:mm", CultureInfo.CurrentCulture));
            KitPaint.DrawText(g, nextLine, type.Caption, t.Text2, S(36), S(494), Body.Width - S(48));
        }
    }

    private float SF(int logical) => Dpi.ScaleF(logical, DeviceDpi);
}
