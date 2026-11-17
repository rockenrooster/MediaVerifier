using System.Globalization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.UI.Kit;
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

    /// <summary>The rules, in catalog order, stacked; the panel scrolls them.</summary>
    private sealed class RuleList : KitControl
    {
        public RuleList(RulesView view)
        {
            TabStop = false;
            Rows = [.. RuleCatalog.All.Select((r, i) => new RuleRow(view, r) { TabIndex = i })];
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

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnPaintBackground(PaintEventArgs pevent) => pevent.Graphics.Clear(Tokens.Surface);
    }

    /// <summary>One rule: switch, title and meta line, severity cap, and its parameters when expanded.</summary>
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
            Core.Rules.RuleSettings settings = view.Services.Rules;
            _binding = true;
            _enabled.Checked = settings.IsEnabled(rule);
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

            _cap.SelectedIndex = Math.Max(0, Array.IndexOf(_caps, settings.Cap(rule)));
            _cap.AccessibleName = rule.Title;
            _binding = false;
            _enabled.CheckedChanged += (_, _) => UiTask.Run(ChangedAsync(() => _view.Services.Rules.SetEnabled(Rule.Id, _enabled.Checked)));
            _cap.SelectedIndexChanged += (_, _) => UiTask.Run(ChangedAsync(() => _view.Services.Rules.SetSeverityCap(Rule.Id, _caps[Math.Max(0, _cap.SelectedIndex)])));
            _expand.ToolTipText = rule.Title;
            _expand.Click += (_, _) =>
            {
                Expanded = !Expanded;
                (Parent as RuleList)?.Relayout();
            };
            Controls.AddRange([_enabled, _cap, _expand, _effect]);
            foreach (RuleParameter parameter in RuleDefaults.ForRule(rule.Id))
            {
                NumberBox box = new() { Minimum = parameter.Min, Maximum = parameter.Max, Decimals = parameter.Decimals, Step = Math.Pow(10, -parameter.Decimals), Number = settings.Value(rule.Id, parameter.Name), AccessibleName = parameter.Label, Inset = true, Visible = false };
                KitButton reset = new() { Kind = ButtonKind.Subtle, Text = Strings.Kit.Reset, Visible = false };
                RuleParameter p = parameter;
                box.ValueChanged += (_, _) =>
                {
                    if (!_binding)
                    {
                        _debounce.Tag = (p, box.Number);
                        _debounce.Stop();
                        _debounce.Start();
                    }
                };
                reset.Click += (_, _) => UiTask.Run(SetParameterAsync(p.Name, p.Default));
                _parameters.Add((parameter, box, reset));
                Controls.Add(box);
                Controls.Add(reset);
            }

            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                if (_debounce.Tag is (RuleParameter parameter, double value))
                {
                    UiTask.Run(SetParameterAsync(parameter.Name, value));
                }
            };
            _expand.Visible = _parameters.Count > 0;
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
                _expanded = value && _parameters.Count > 0;
                _expand.Icon = _expanded ? IconName.ChevronUp : IconName.ChevronDown;
                foreach ((_, NumberBox box, KitButton reset) in _parameters)
                {
                    box.Visible = _expanded;
                    reset.Visible = _expanded && IsChanged(box);
                }

                _effect.Visible = _expanded && _effect.Text.Length > 0;
                PerformLayout();
                Invalidate();
            }
        }

        // The effect line counts when it has text, not when _effect.Visible: a resize while the Rules section is hidden
        // lays the list out with every row reading hidden, and the open rule lost the line's 48 px (clipped on return).
        public int LogicalHeight => !_expanded ? 64 : 64 + 29 + ((_parameters.Count - 1) * 40) + 20 + (_effect.Text.Length > 0 ? 48 : 0) + 16;

        public async Task SetParameterAsync(string name, double value)
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
            await ChangedAsync(() => _view.Services.Rules.SetParameter(Rule.Id, name, Math.Abs(clamped - parameter.Default) < 1e-9 ? null : clamped)).ConfigureAwait(true);
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

            int effectTop = S(64 + 29 + ((_parameters.Count - 1) * 40) + 20 + 8);
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

        private async Task ChangedAsync(Action apply)
        {
            if (_binding)
            {
                return;
            }

            apply();
            Invalidate();
            if (!_view.Services.IsDemo)
            {
                await _view.Services.Database.RuleSettings.SaveAsync(_view.Services.Rules).ConfigureAwait(true);
            }

            ReevaluationResult result = await _view.Services.Scans.ReevaluateAsync([Rule.Id]).ConfigureAwait(true);
            _effect.Text = result switch
            {
                { ChangedTier: > 0 } => string.Format(CultureInfo.CurrentCulture, Strings.Settings.ChangedTier, UiFormat.Count(result.ChangedTier)),
                { Appeared: > 0 } => string.Format(CultureInfo.CurrentCulture, Strings.Settings.Appeared, UiFormat.Count(result.Appeared)),
                { NoLongerApply: > 0 } => string.Format(CultureInfo.CurrentCulture, Strings.Settings.NoLongerApply, UiFormat.Count(result.NoLongerApply)),
                _ => Strings.Settings.NoFindingsChanged,
            };
            _effect.Visible = _expanded;
            foreach ((_, NumberBox box, KitButton reset) in _parameters)
            {
                reset.Visible = _expanded && IsChanged(box);
            }

            (Parent as RuleList)?.Relayout();
            PerformLayout();
            Invalidate();
        }
    }
}
