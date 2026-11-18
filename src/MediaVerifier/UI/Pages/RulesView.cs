using System.Globalization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// Settings → Rules (subplan 09): filter box, category chips and one row per rule with its switch,
/// severity cap and parameters. Every change is saved, stored analysis is judged again and the
/// effect is reported under the rule.
/// </summary>
internal sealed class RulesView : KitControl
{
    private static readonly (string Label, FindingCategory[]? Categories)[] Categories =
    [
        (Strings.Settings.ChipAll, null),
        (Strings.Settings.ChipDamage, [FindingCategory.Integrity, FindingCategory.Corruption]),
        (Strings.Settings.ChipAuthenticity, [FindingCategory.Authenticity]),
        (Strings.Settings.ChipQuality, [FindingCategory.Quality]),
        (Strings.Settings.ChipStructure, [FindingCategory.Structure]),
    ];

    private readonly SettingsPage _page;
    private readonly SearchBox _filter = new() { Placeholder = Strings.Settings.FilterRules, TabIndex = 0 };
    private readonly Chip[] _chips;
    private readonly Card _card = new() { TabIndex = 6 };
    private readonly RuleList _list;
    private readonly ScrollPanel _scroll;

    /// <summary>The last re-evaluation queued: each reads the whole library, so they run one after another (UI thread only).</summary>
    private Task _reevaluations = Task.CompletedTask;

    public RulesView(SettingsPage page)
    {
        _page = page;
        TabStop = false;
        _chips = [.. Categories.Select((c, i) => new Chip { Text = c.Label, On = i == 0, TabIndex = 1 + i })];
        for (int i = 0; i < _chips.Length; i++)
        {
            int index = i;
            _chips[i].Toggled += (_, _) =>
            {
                for (int j = 0; j < _chips.Length; j++)
                {
                    _chips[j].On = j == index;
                }

                // The selected chip's label is bold, so the chips are measured again.
                PerformLayout();
                ApplyFilter();
            };
        }

        _filter.TextCommitted += (_, _) => ApplyFilter();
        _list = new RuleList(this);
        _scroll = new ScrollPanel(_list);
        _card.Controls.Add(_scroll);
        Controls.Add(_filter);
        Controls.AddRange(_chips);
        Controls.Add(_card);
        ApplyFilter();
    }

    internal App.UiServices Services => _page.PageServices;

    internal Control Filter => _filter;

    /// <summary>The rule of the row that has the keyboard focus (F1 opens its help), or null.</summary>
    internal string? FocusedRuleId => RuleIdOf(FindForm()?.ActiveControl);

    /// <summary>The rule whose row holds <paramref name="control"/> (its switch, cap, a limit), or null.</summary>
    internal static string? RuleIdOf(Control? control)
    {
        for (; control is not null; control = control.Parent)
        {
            if (control is RuleRow row)
            {
                return row.Rule.Id;
            }
        }

        return null;
    }

    /// <summary>Where a list key moves from row <paramref name="index"/> of <paramref name="count"/> shown rows.</summary>
    internal static int MoveTarget(int index, int count, int page, Keys key) => Math.Clamp(key switch
    {
        Keys.Up => index - 1,
        Keys.Down => index + 1,
        Keys.PageUp => index - page,
        Keys.PageDown => index + page,
        Keys.Home => 0,
        _ => count - 1,
    }, 0, Math.Max(0, count - 1));

    /// <summary>The settings-rules mockup: Clipping open, High from 300, and its effect reported.</summary>
    internal async Task ShowDemoAsync()
    {
        if (_list.Rows.FirstOrDefault(r => r.Rule.Id == "AUD-CLIPPING") is RuleRow clipping)
        {
            clipping.Expanded = true;
            await clipping.SetParameterAsync("highPerMinute", 300).ConfigureAwait(true);
            _list.Relayout();
            List<RuleRow> shown = [.. _list.Rows.Where(r => r.Matches)];
            int index = shown.IndexOf(clipping);
            _scroll.ScrollOffset = index > 0 ? shown[index - 1].Top : clipping.Top;
        }
    }

    /// <summary>Removes the effect lines (the page was left), so none is shown out of date later.</summary>
    internal void ClearEffects()
    {
        foreach (RuleRow row in _list.Rows)
        {
            row.ClearEffect();
        }

        _list.Relayout();
    }

    /// <summary>Re-judges one rule after the re-evaluations queued before it (they used to run in parallel).</summary>
    internal async Task<ReevaluationResult> ReevaluateInTurnAsync(string ruleId)
    {
        Task previous = _reevaluations;
        TaskCompletionSource turn = new();
        _reevaluations = turn.Task;
        try
        {
            await previous.ConfigureAwait(true);
            return await Services.Scans.ReevaluateAsync([ruleId]).ConfigureAwait(true);
        }
        finally
        {
            turn.SetResult();
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        // The filter box gives up width (320 down to 160) so the category chips stay in view in a narrow window.
        int chips = _chips.Sum(c => S(c.PreferredLogicalWidth) + S(8)) - S(8);
        int filterWidth = Math.Clamp(Width - chips - S(16), S(160), S(320));
        _filter.Bounds = new Rectangle(0, S(64), Math.Min(filterWidth, Width), S(32));
        int x = filterWidth + S(16);
        foreach (Chip chip in _chips)
        {
            int width = S(chip.PreferredLogicalWidth);
            chip.Bounds = new Rectangle(x, S(66), width, S(Chip.LogicalHeight));
            x += width + S(8);
        }

        _card.Bounds = new Rectangle(0, S(112), Width, Math.Max(S(64), Height - S(112)));
        _scroll.Bounds = Rectangle.Inflate(new Rectangle(Point.Empty, _card.Size), -S(1), -S(1));
        _list.Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemeTokens t = Tokens;
        e.Graphics.Clear(t.Bg);
        TypeRamp type = Type;
        KitPaint.DrawText(e.Graphics, Strings.Settings.Rules, type.Subtitle, t.Text, 0, S(16), Width);
        KitPaint.DrawText(e.Graphics, Strings.Settings.RulesIntro, type.Body, t.Text2, 0, S(41), Width);
    }

    private void ApplyFilter()
    {
        string text = _filter.Text.Trim();
        FindingCategory[]? categories = Categories[Math.Max(0, Array.FindIndex(_chips, c => c.On))].Categories;
        foreach (RuleRow row in _list.Rows)
        {
            RuleDefinition rule = row.Rule;
            bool show = (categories is null || categories.Contains(rule.Category))
                && (text.Length == 0 || rule.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || rule.Id.Contains(text, StringComparison.OrdinalIgnoreCase));
            row.Matches = show;
            row.Visible = show;
        }

        _list.Relayout();
        _scroll.ScrollToTop();
    }

    /// <summary>
    /// The rules, in catalog order, stacked; the panel scrolls them. Backlog rules have no detector yet (plans 02 and 16):
    /// their switches did nothing, and two of them read on, so they are not listed.
    /// </summary>
    private sealed class RuleList : KitControl
    {
        public RuleList(RulesView view)
        {
            TabStop = false;
            Rows = [.. RuleCatalog.All.Where(r => r.Milestone != Milestone.Backlog).Select((r, i) => new RuleRow(view, r) { TabIndex = i })];
            Controls.AddRange([.. Rows]);
        }

        public IReadOnlyList<RuleRow> Rows { get; }

        public void Relayout()
        {
            int y = 0;
            RuleRow? last = null;
            foreach (RuleRow row in Rows)
            {
                // Matches, not Visible: while the Rules section is hidden WinForms reports every row hidden, so the list
                // was laid out empty and opening Rules from the section list showed a blank card.
                if (!row.Matches)
                {
                    continue;
                }

                row.Bounds = new Rectangle(0, y, Width, S(row.LogicalHeight));
                row.Divider = true;
                last = row;
                y += row.Height;
            }

            if (last is not null)
            {
                last.Divider = false;
            }

            Height = Math.Max(1, y);
        }

        /// <summary>
        /// Up, Down, Page Up, Page Down, Home and End from a rule's switch or chevron move to another shown rule's switch
        /// (subplan 09, Keyboard: move in lists); the list scrolls to it. Controls that use the arrows keep them.
        /// </summary>
        public bool MoveFocus(RuleRow from, Keys key)
        {
            List<RuleRow> shown = [.. Rows.Where(r => r.Matches)];
            int index = shown.IndexOf(from);
            if (index < 0)
            {
                return false;
            }

            int page = Math.Max(1, ((Parent?.Height ?? Height) / Math.Max(1, S(64))) - 1);
            int target = MoveTarget(index, shown.Count, page, key);
            if (target != index)
            {
                shown[target].FocusSwitch();
            }

            return true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Tokens.Surface);
    }

    /// <summary>
    /// One rule: switch, title and meta line, severity cap, and its parameters when expanded. Changes wait 400 ms for the
    /// next key or click, then apply together: one save and one re-evaluation, which runs after any other and is run
    /// again when more changes arrive meanwhile. The effect line shows under the rule, open or not.
    /// </summary>
    private sealed class RuleRow : KitControl
    {
        private static readonly Severity[] Caps = [Severity.Critical, Severity.High, Severity.Medium, Severity.Low];
        private readonly RulesView _view;
        private readonly ToggleSwitch _enabled = new();
        private readonly DropDown _cap = new();
        private readonly IconButton _expand = new() { Icon = IconName.ChevronDown, Compact = true };
        private readonly List<(RuleParameter Parameter, NumberBox Box, KitButton Reset)> _parameters = [];
        private readonly InfoBar _effect = new() { Tier = Severity.Low, Visible = false };
        private readonly Severity[] _caps;
        private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 400 };

        /// <summary>Limits typed or reset since the last apply, by name (null: back to the default).</summary>
        private readonly Dictionary<string, double?> _pendingParameters = new(StringComparer.Ordinal);
        private bool _pendingSwitch;
        private bool _pendingCap;
        private bool _applying;
        private bool _checking;
        private bool _checkAgain;
        private bool _expanded;
        private bool _binding;

        /// <summary>The parameter labels' column: as wide as the longest label, within the room the row leaves (at least 120 px).</summary>
        private int _labelWidth;

        /// <summary>The units' column: as wide as the longest unit.</summary>
        private int _unitWidth;

        public RuleRow(RulesView view, RuleDefinition rule)
        {
            _view = view;
            Rule = rule;
            TabStop = false;
            AccessibleName = rule.Title;
            _enabled.AccessibleName = rule.Title;
            _caps = [.. Caps.Where(rule.CanReach)];
            foreach (Severity cap in _caps)
            {
                _cap.Items.Add(cap switch
                {
                    Severity.Critical => Strings.Settings.UpToCritical,
                    Severity.High => Strings.Settings.UpToHigh,
                    Severity.Medium => Strings.Settings.UpToMedium,
                    _ => Strings.Settings.UpToLow,
                });
            }

            _cap.AccessibleName = rule.Title;
            _enabled.CheckedChanged += (_, _) => Queue(() => _pendingSwitch = true);
            _cap.SelectedIndexChanged += (_, _) => Queue(() => _pendingCap = true);
            _expand.ToolTipText = Strings.Settings.ShowLimits;
            _expand.Click += (_, _) =>
            {
                Expanded = !Expanded;
                (Parent as RuleList)?.Relayout();
            };
            Controls.AddRange([_enabled, _cap, _expand, _effect]);
            foreach (RuleParameter parameter in RuleDefaults.ForRule(rule.Id))
            {
                NumberBox box = new() { Minimum = parameter.Min, Maximum = parameter.Max, Decimals = parameter.Decimals, Step = Math.Pow(10, -parameter.Decimals), AccessibleName = parameter.Label, Inset = true, Visible = false };
                KitButton reset = new() { Kind = ButtonKind.Subtle, Text = Strings.Kit.Reset, Visible = false };
                RuleParameter p = parameter;
                box.ValueChanged += (_, _) => Queue(() => _pendingParameters[p.Name] = box.Number);
                reset.Click += (_, _) => UiTask.Run(SetParameterAsync(p.Name, p.Default));
                _parameters.Add((parameter, box, reset));
                Controls.Add(box);
                Controls.Add(reset);
            }

            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                UiTask.Run(ApplyPendingAsync());
            };
            _expand.Visible = _parameters.Count > 0;
            Bind();
        }

        public RuleDefinition Rule { get; }

        /// <summary>The filter and the category chips let the row show (its own Visible reads false while the section is hidden).</summary>
        public bool Matches { get; set; } = true;

        public bool Divider { get; set; } = true;

        public bool Expanded
        {
            get => _expanded;
            set
            {
                bool wasExpanded = _expanded;
                _expanded = value && _parameters.Count > 0;
                _expand.Icon = _expanded ? IconName.ChevronUp : IconName.ChevronDown;
                _expand.ToolTipText = _expanded ? Strings.Settings.HideLimits : Strings.Settings.ShowLimits;
                foreach ((_, NumberBox box, KitButton reset) in _parameters)
                {
                    box.Visible = _expanded;
                    reset.Visible = _expanded && IsChanged(box);
                }

                if (wasExpanded && !_expanded && !_checking)
                {
                    // Closing a rule puts its effect away, so it never reappears out of date.
                    _effect.Text = string.Empty;
                }

                _effect.Visible = _effect.Text.Length > 0;
                PerformLayout();
                Invalidate();
            }
        }

        // The effect line counts when it has text, not when _effect.Visible: a resize while the Rules section is hidden
        // lays the list out with every row reading hidden, and the open rule lost the line's 48 px (clipped on return).
        // A closed rule shows it under its 64 px header (8 px above, 16 below), so a switch or cap change is answered.
        public int LogicalHeight => _expanded
            ? 64 + 29 + ((_parameters.Count - 1) * 40) + 20 + (_effect.Text.Length > 0 ? 48 : 0) + 16
            : 64 + (_effect.Text.Length > 0 ? 8 + 32 + 16 : 0);

        /// <summary>Sets a limit as if typed and applies it now (Reset, and the render harness).</summary>
        public Task SetParameterAsync(string name, double value)
        {
            RuleParameter parameter = RuleDefaults.Get(Rule.Id, name);
            double clamped = parameter.Clamp(value);
            _binding = true;
            foreach ((RuleParameter p, NumberBox box, _) in _parameters)
            {
                if (p.Name == name)
                {
                    box.Number = clamped;
                }
            }

            _binding = false;
            _pendingParameters[name] = Math.Abs(clamped - parameter.Default) < 1e-9 ? null : clamped;
            return ApplyPendingAsync();
        }

        public void FocusSwitch() => _enabled.Focus();

        /// <summary>Removes the effect line unless a re-evaluation is still running.</summary>
        public void ClearEffect()
        {
            if (!_checking && _effect.Text.Length > 0)
            {
                _effect.Text = string.Empty;
                _effect.Visible = false;
                PerformLayout();
            }
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
                && Parent is RuleList list
                && list.MoveFocus(this, keyData))
            {
                return true;
            }

            return base.ProcessDialogKey(keyData);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            Size toggle = _enabled.GetPreferredSize(Size.Empty);
            _enabled.Bounds = new Rectangle(S(16), (S(64) - toggle.Height) / 2, toggle.Width, toggle.Height);
            _expand.Bounds = new Rectangle(Width - S(16) - S(28), S(18), S(28), S(28));
            _cap.Bounds = new Rectangle(Width - S(48) - S(150), S(16), S(150), S(32));

            // Labels such as "Report from this many clipped peaks" were cut at 120 px: the column takes the longest
            // one, as far as the box, the unit, "Default is {n}" and Reset leave room in a narrow window.
            int label = S(120);
            int defaultWidth = 0;
            int resetRoom = 0;
            _unitWidth = 0;
            foreach ((RuleParameter parameter, _, KitButton reset) in _parameters)
            {
                label = Math.Max(label, KitPaint.MeasureWidth(parameter.Label, Type.Body) + S(8));
                _unitWidth = Math.Max(_unitWidth, KitPaint.MeasureWidth(parameter.Unit, Type.Body));
                defaultWidth = Math.Max(defaultWidth, KitPaint.MeasureWidth(string.Format(CultureInfo.CurrentCulture, Strings.Settings.DefaultIs, parameter.Format(parameter.Default)), Type.Caption));
                resetRoom = S(reset.PreferredLogicalWidth);
            }

            int room = Width - S(72) - S(8) - S(72) - S(12) - _unitWidth - S(16) - defaultWidth - S(12) - resetRoom - S(16);
            _labelWidth = Math.Clamp(label, S(120), Math.Max(S(120), Math.Min(S(360), room)));
            int boxX = S(72) + _labelWidth + S(8);
            for (int i = 0; i < _parameters.Count; i++)
            {
                (_, NumberBox box, KitButton reset) = _parameters[i];
                int centre = S(64 + 29 + (40 * i));
                box.Bounds = new Rectangle(boxX, centre - S(16), S(72), S(32));
                int resetWidth = S(reset.PreferredLogicalWidth);
                reset.Bounds = new Rectangle(Width - S(16) - resetWidth, centre - S(16), resetWidth, S(32));
            }

            int effectTop = _expanded ? S(64 + 29 + ((_parameters.Count - 1) * 40) + 20 + 8) : S(64 + 8);
            _effect.Bounds = new Rectangle(S(56), effectTop, Width - S(72), S(32));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            TypeRamp type = Type;
            int textWidth = _cap.Left - S(88);
            KitPaint.DrawText(g, Rule.Title, type.BodyStrong, _enabled.Checked ? t.Text : t.Text2, S(72), S(23), textWidth);
            string kind = Rule.AppliesTo switch
            {
                MediaKindMask.Audio => Strings.Settings.KindAudio,
                MediaKindMask.Video => Strings.Settings.KindVideo,
                MediaKindMask.Photo => Strings.Settings.KindPhoto,
                _ => Strings.Settings.KindAll,
            };
            KitPaint.DrawText(g, string.Format(CultureInfo.CurrentCulture, Strings.Settings.RuleMeta, Rule.Id, kind, UiFormat.DepthName(Rule.MinDepth)), type.Caption, t.Text3, S(72), S(43), textWidth);
            if (_expanded)
            {
                for (int i = 0; i < _parameters.Count; i++)
                {
                    (RuleParameter parameter, NumberBox box, _) = _parameters[i];
                    int centre = S(64 + 29 + (40 * i));
                    int unitX = box.Right + S(12);
                    if (KitPaint.MeasureWidth(parameter.Label, type.Body) > _labelWidth)
                    {
                        // A narrow window: two lines centred on the row rather than a cut name.
                        TextWrap.Draw(g, parameter.Label, type.Body, t.Text, S(72), centre - S(19), _labelWidth, S(19), maxLines: 2);
                    }
                    else
                    {
                        KitPaint.DrawText(g, parameter.Label, type.Body, t.Text, S(72), centre, _labelWidth);
                    }
                    KitPaint.DrawText(g, parameter.Unit, type.Body, t.Text2, unitX, centre, _unitWidth + 1);
                    if (IsChanged(box))
                    {
                        KitPaint.DrawText(g, string.Format(CultureInfo.CurrentCulture, Strings.Settings.DefaultIs, parameter.Format(parameter.Default)), type.Caption, t.Text3, unitX + _unitWidth + S(16), centre, S(160));
                    }
                }
            }

            if (Divider)
            {
                KitPaint.HorizontalLine(g, t.Stroke, S(16), Width - S(16), Height - SF(1), SF(1));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _debounce.Dispose();
            }

            base.Dispose(disposing);
        }

        private bool IsChanged(NumberBox box)
        {
            foreach ((RuleParameter parameter, NumberBox b, _) in _parameters)
            {
                if (ReferenceEquals(b, box))
                {
                    return Math.Abs(box.Number - parameter.Default) > 1e-9;
                }
            }

            return false;
        }

        /// <summary>Shows the values in effect without treating them as changes.</summary>
        private void Bind()
        {
            Core.Rules.RuleSettings settings = _view.Services.Rules;
            _binding = true;
            _enabled.Checked = settings.IsEnabled(Rule);
            _cap.SelectedIndex = Math.Max(0, Array.IndexOf(_caps, settings.Cap(Rule)));
            foreach ((RuleParameter parameter, NumberBox box, _) in _parameters)
            {
                box.Number = settings.Value(Rule.Id, parameter.Name);
            }

            _binding = false;
            RefreshResets();
            Invalidate();
        }

        private void RefreshResets()
        {
            foreach ((_, NumberBox box, KitButton reset) in _parameters)
            {
                reset.Visible = _expanded && IsChanged(box);
            }
        }

        /// <summary>Notes a change and waits for the next one (arrowing through the caps or typing a limit is one change).</summary>
        private void Queue(Action note)
        {
            if (_binding)
            {
                return;
            }

            note();
            Invalidate();
            _debounce.Stop();
            _debounce.Start();
        }

        /// <summary>
        /// Applies the noted changes, saves them and judges the library again. A save that fails is undone, on screen
        /// too, and said in a Critical toast: the switch used to stay flipped while nothing was kept.
        /// </summary>
        private async Task ApplyPendingAsync()
        {
            _debounce.Stop();
            if (!_pendingSwitch && !_pendingCap && _pendingParameters.Count == 0)
            {
                return;
            }

            if (_applying)
            {
                // The previous change is still being saved: this one follows it.
                _debounce.Start();
                return;
            }

            _applying = true;
            try
            {
                Core.Rules.RuleSettings rules = _view.Services.Rules;
                RuleOverride? before = rules.Snapshot(Rule.Id);
                if (_pendingSwitch)
                {
                    rules.SetEnabled(Rule.Id, _enabled.Checked);
                }

                if (_pendingCap)
                {
                    rules.SetSeverityCap(Rule.Id, _caps[Math.Max(0, _cap.SelectedIndex)]);
                }

                foreach ((string name, double? value) in _pendingParameters)
                {
                    rules.SetParameter(Rule.Id, name, value);
                }

                _pendingSwitch = false;
                _pendingCap = false;
                _pendingParameters.Clear();
                RefreshResets();
                Invalidate();
                if (!_view.Services.IsDemo)
                {
                    try
                    {
                        await _view.Services.Database.RuleSettings.SaveAsync(rules).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
                    {
                        _view.Services.State.Log.Warning("Rule settings could not be saved", ex);
                        rules.Restore(Rule.Id, before);
                        if (!IsDisposed)
                        {
                            Bind();
                        }

                        _view.Services.ShowToast?.Invoke(new ToastContent(Severity.Critical, Rule.Title, string.Format(CultureInfo.CurrentCulture, Strings.Settings.RuleNotSaved, ex.Message)));
                        return;
                    }
                }
            }
            finally
            {
                _applying = false;
            }

            await CheckAgainAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// Judges the stored analysis again and reports the effect. While it runs the effect line says so; changes made
        /// meanwhile run it once more instead of starting another in parallel, and the answer is the last run's.
        /// </summary>
        private async Task CheckAgainAsync()
        {
            if (_checking)
            {
                _checkAgain = true;
                return;
            }

            _checking = true;
            ShowEffect(Strings.Settings.CheckingFindings);
            try
            {
                ReevaluationResult result;
                do
                {
                    _checkAgain = false;
                    result = await _view.ReevaluateInTurnAsync(Rule.Id).ConfigureAwait(true);
                }
                while (_checkAgain);

                ShowEffect(result switch
                {
                    { ChangedTier: > 0 } => string.Format(CultureInfo.CurrentCulture, Strings.Settings.ChangedTier, UiFormat.Count(result.ChangedTier)),
                    { Appeared: > 0 } => string.Format(CultureInfo.CurrentCulture, Strings.Settings.Appeared, UiFormat.Count(result.Appeared)),
                    { NoLongerApply: > 0 } => string.Format(CultureInfo.CurrentCulture, Strings.Settings.NoLongerApply, UiFormat.Count(result.NoLongerApply)),
                    _ => Strings.Settings.NoFindingsChanged,
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                _view.Services.State.Log.Warning($"Findings of {Rule.Id} could not be judged again", ex);
                ShowEffect(string.Empty);
                _view.Services.ShowToast?.Invoke(new ToastContent(Severity.Medium, Rule.Title, string.Format(CultureInfo.CurrentCulture, Strings.Settings.RecheckFailed, ex.Message)));
            }
            finally
            {
                _checking = false;
            }
        }

        private void ShowEffect(string text)
        {
            if (IsDisposed)
            {
                return;
            }

            _effect.Text = text;
            _effect.Visible = text.Length > 0;
            (Parent as RuleList)?.Relayout();
            PerformLayout();
            Invalidate();
        }
    }
}
