using System.Globalization;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ipc;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Settings;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>A request to show one Settings section (Update available, rule links).</summary>
internal sealed record SettingsSectionRequest(int Section);

/// <summary>
/// Settings (subplan 09, Settings; task UI-10): a section list and the section's rows; every change
/// applies and is saved at once. Rule changes re-judge stored analysis and report what changed.
/// </summary>
internal sealed class SettingsPage : PageBase
{
    public const int RulesSection = 2;
    public const int UpdatesSection = 5;

    private static readonly string[] Names =
    [
        Strings.Settings.General, Strings.Settings.Scanning, Strings.Settings.Rules, Strings.Settings.Integrity,
        Strings.Settings.Notifications, Strings.Settings.Updates, Strings.Settings.Advanced, Strings.Settings.About,
    ];

    private readonly SectionList _nav;
    private readonly KitControl[] _sections;
    private readonly RulesView _rules;
    private int _current;

    public SettingsPage(UiServices services)
        : base(services)
    {
        _nav = new SectionList(this) { TabIndex = 0 };
        _rules = new RulesView(this) { TabIndex = 1 };
        ScanSettings scan = services.State.Settings.Scanning;
        SettingRow quietFrom = Time(Strings.Settings.From, () => scan.QuietFrom, v => scan.QuietFrom = v);
        SettingRow quietTo = Time(Strings.Settings.To, () => scan.QuietTo, v => scan.QuietTo = v);
        quietFrom.Collapsed = quietTo.Collapsed = !scan.QuietHours;
        _sections =
        [
            Section(Strings.Settings.General, Strings.Settings.GeneralIntro,
            [
                Segmented(Strings.Settings.Theme, null, [Strings.Settings.ThemeSystem, Strings.Settings.ThemeLight, Strings.Settings.ThemeDark], () => (int)State.Settings.Theme, i =>
                {
                    State.Settings.Theme = (ThemeMode)i;
                    ThemeService.Apply(State.Settings.Theme);
                }),
                Toggle(Strings.Settings.StartWithWindows, Strings.Settings.StartWithWindowsHelper, () => State.Settings.StartWithWindows, v => State.Settings.StartWithWindows = v),
                Toggle(Strings.Settings.KeepRunning, null, () => State.Settings.MinimizeToTray, v => State.Settings.MinimizeToTray = v),
                Segmented(Strings.Settings.DefaultDepth, null, [Strings.Depths.Quick, Strings.Depths.Standard, Strings.Depths.Deep], () => DepthIndex(scan.DefaultDepth), i => scan.DefaultDepth = i switch { 0 => ScanDepth.Quick, 2 => ScanDepth.Deep, _ => ScanDepth.Standard }),
                Toggle(Strings.Settings.AskLarge, Strings.Settings.AskLargeHelper, () => scan.AskBeforeLargeScans, v => scan.AskBeforeLargeScans = v),
            ]),
            Section(Strings.Settings.Scanning, Strings.Settings.ScanningIntro,
            [
                Segmented(Strings.Settings.Speed, Strings.Settings.SpeedHelper, [Strings.Settings.SpeedLow, Strings.Settings.SpeedBalanced, Strings.Settings.SpeedHigh], () => (int)scan.Speed, i => scan.Speed = (PerformanceLevel)i),
                Number(Strings.Settings.ReadersHdd, null, null, 1, 16, () => scan.ReadersPerHdd, v => scan.ReadersPerHdd = v),
                Number(Strings.Settings.ReadersSsd, null, null, 1, 32, () => scan.ReadersPerSsd, v => scan.ReadersPerSsd = v),
                Number(Strings.Settings.ReadersNetwork, null, null, 1, 16, () => scan.ReadersPerNetwork, v => scan.ReadersPerNetwork = v),
                Toggle(Strings.Settings.CloudFiles, Strings.Settings.CloudFilesHelper, () => scan.ScanCloudFiles, v => scan.ScanCloudFiles = v),
                Drop(Strings.Settings.SkipLarger, null, [Strings.Settings.NoLimit, Strings.Settings.Gb10, Strings.Settings.Gb50, Strings.Settings.Gb100], () => SizeIndex(scan.SkipFilesLargerThan), i => scan.SkipFilesLargerThan = i switch { 1 => 10L << 30, 2 => 50L << 30, 3 => 100L << 30, _ => null }),
                Number(Strings.Settings.StallSeconds, null, Strings.Units.SecondsUnit, 10, 3600, () => scan.StallSeconds, v => scan.StallSeconds = v),
                Toggle(Strings.Settings.Upscaling, null, () => scan.CheckPhotosForUpscaling, v => scan.CheckPhotosForUpscaling = v),
                Toggle(Strings.Settings.Strict, Strings.Settings.StrictHelper, () => scan.StrictDecoding, v => scan.StrictDecoding = v),
            ]),
            _rules,
            Section(Strings.Settings.Integrity, Strings.Settings.IntegrityIntro,
            [
                Number(Strings.Settings.ReReadEvery, null, Strings.Units.Days, 7, 3650, () => scan.ScrubPeriodDays, v => scan.ScrubPeriodDays = v),
                Number(Strings.Settings.TimeEachRun, null, Strings.Units.MinutesUnit, 0, 1440, () => scan.ScrubBudgetMinutes, v => scan.ScrubBudgetMinutes = v),
                Toggle(Strings.Settings.VerifyOnAnalysis, null, () => scan.VerifyOnAnalysis, v => scan.VerifyOnAnalysis = v),
                Drop(Strings.Settings.KeepOriginals, null, [Strings.Settings.QuarantineFolder, Strings.Settings.RecycleBin, Strings.Settings.AnotherFolder], () => (int)scan.KeepOriginalsIn, i => scan.KeepOriginalsIn = (OriginalsDestination)i),
                Number(Strings.Settings.EmptyQuarantine, null, Strings.Units.Days, 1, 3650, () => scan.EmptyQuarantineAfterDays, v => scan.EmptyQuarantineAfterDays = v),
            ]),
            Section(Strings.Settings.Notifications, Strings.Settings.NotificationsIntro,
            [
                Toggle(Strings.Settings.ShowWindowsNotifications, null, () => scan.ShowWindowsNotifications, v => scan.ShowWindowsNotifications = v),
                Drop(Strings.Settings.NotifyAbout, null, [Strings.Settings.CriticalOnly, Strings.Settings.HighAndAbove, Strings.Settings.MediumAndAbove, Strings.Settings.Everything], () => (int)scan.NotifyAbout, i => scan.NotifyAbout = (NotifyThreshold)i),
                Toggle(Strings.Settings.QuietHours, null, () => scan.QuietHours, v =>
                {
                    scan.QuietHours = v;
                    quietFrom.Collapsed = quietTo.Collapsed = !v;
                    quietFrom.Parent?.Parent?.PerformLayout();
                }),
                quietFrom,
                quietTo,
                Button(Strings.Settings.TestNotificationLabel, null, Strings.Settings.SendTestNotification, () => _ = SendTestNotificationAsync()),
            ]),
            Section(Strings.Settings.Updates, Strings.Settings.UpdatesIntro,
            [
                Info(string.Format(CultureInfo.CurrentCulture, Strings.Settings.VersionLine, AppInfo.VersionText), Strings.Settings.NotChecked),
                Toggle(Strings.Settings.CheckOnStart, null, () => State.Settings.CheckForUpdatesOnStartup, v => State.Settings.CheckForUpdatesOnStartup = v),
                Button(Strings.Settings.CheckNowLabel, null, Strings.Settings.CheckNow, () => _ = CheckForUpdatesAsync()),
            ]),
            Section(Strings.Settings.Advanced, Strings.Settings.AdvancedIntro,
            [
                Button(Strings.Settings.DatabaseLocation, State.Paths.ResolveDatabaseFile(State.Settings.DatabasePath), Strings.Settings.Change, () => Services.Navigate?.Invoke(PageId.Tools, null)),
                Button(Strings.Settings.Logs, null, Strings.Settings.OpenLogs, () => ShellActions.OpenFolder(State.Paths.LogsDirectory)),
                Button(Strings.Settings.Diagnostics, null, Strings.Settings.CopyReport, () => ShellActions.CopyText([AppInfo.WindowTitle, Environment.OSVersion.VersionString, ".NET " + Environment.Version])),
            ]),
            Section(Strings.Settings.About, UiFormat.Facts(AppInfo.ProductName, string.Format(CultureInfo.CurrentCulture, Strings.Settings.VersionLine, AppInfo.VersionText)), []),
        ];
        Controls.Add(_nav);
        for (int i = 0; i < _sections.Length; i++)
        {
            _sections[i].Visible = i == 0;
            Controls.Add(_sections[i]);
        }
    }

    public override PageId Id => PageId.Settings;

    public override Control? FilterBox => _current == RulesSection ? _rules.Filter : null;

    internal int Current => _current;

    internal bool UpdateAvailable { get; private set; }

    internal UiServices PageServices => Services;

    public override void HandleRequest(object request)
    {
        if (request is SettingsSectionRequest section)
        {
            ShowSection(section.Section);
        }
    }

    /// <summary>The render harness's settings-rules state: Rules with Clipping open and High from 300.</summary>
    internal Task ShowDemoRulesAsync()
    {
        ShowSection(RulesSection);
        return _rules.ShowDemoAsync();
    }

    internal void ShowSection(int index)
    {
        _current = Math.Clamp(index, 0, _sections.Length - 1);
        for (int i = 0; i < _sections.Length; i++)
        {
            _sections[i].Visible = i == _current;
        }

        _nav.Invalidate();
        PerformLayout();
    }

    internal void Save() => State.SaveSettings();

    protected override object? LoadData() => Services.Data.LoadShell();

    protected override void ApplyData(object data)
    {
        UpdateAvailable = ((Data.ShellCounts)data).UpdateAvailable;
        _nav.Invalidate();
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        Reload();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        _nav.Bounds = new Rectangle(0, 0, S(168), Height);
        foreach (KitControl section in _sections)
        {
            section.Bounds = new Rectangle(S(192), 0, Math.Max(0, Width - S(192)), Height);
        }
    }

    private static int DepthIndex(ScanDepth depth) => depth switch { ScanDepth.Quick => 0, ScanDepth.Deep => 2, _ => 1 };

    private static int SizeIndex(long? limit) => limit switch { null => 0, <= 10L << 30 => 1, <= 50L << 30 => 2, _ => 3 };

    private SettingsSection Section(string title, string intro, IReadOnlyList<SettingRow> rows) => new(this, title, intro, rows);

    private SettingRow Toggle(string label, string? helper, Func<bool> get, Action<bool> set)
    {
        ToggleSwitch toggle = new() { Checked = get(), AccessibleName = label };
        toggle.CheckedChanged += (_, _) =>
        {
            set(toggle.Checked);
            Save();
        };
        return new SettingRow(label, helper, toggle, null);
    }

    private SettingRow Segmented(string label, string? helper, string[] options, Func<int> get, Action<int> set)
    {
        SegmentedControl control = new() { AccessibleName = label };
        foreach (string option in options)
        {
            control.Segments.Add(option);
        }

        control.SelectedIndex = Math.Clamp(get(), 0, options.Length - 1);
        control.SelectedIndexChanged += (_, _) =>
        {
            set(control.SelectedIndex);
            Save();
        };
        return new SettingRow(label, helper, control, null);
    }

    private SettingRow Number(string label, string? helper, string? unit, int min, int max, Func<int> get, Action<int> set)
    {
        NumberBox box = new() { Minimum = min, Maximum = max, Value = get(), AccessibleName = label, Inset = true };
        box.ValueChanged += (_, _) =>
        {
            set(box.Value);
            Save();
        };
        return new SettingRow(label, helper, box, unit);
    }

    private SettingRow Drop(string label, string? helper, string[] options, Func<int> get, Action<int> set)
    {
        DropDown drop = new() { AccessibleName = label };
        foreach (string option in options)
        {
            drop.Items.Add(option);
        }

        drop.SelectedIndex = Math.Clamp(get(), 0, options.Length - 1);
        drop.SelectedIndexChanged += (_, _) =>
        {
            set(drop.SelectedIndex);
            Save();
        };
        return new SettingRow(label, helper, drop, null);
    }

    /// <summary>
    /// Checks the signed release (subplan 14); when a newer verified installer exists, asks, downloads it
    /// (size and hash checked) and starts it silently. Fails closed: an unverified manifest only reports.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (Services.IsDemo || FindForm() is not Form owner)
        {
            return;
        }

        using Core.Updates.UpdateService updates = new();
        Core.Updates.UpdateCheckResult result = await updates.CheckAsync().ConfigureAwait(true);
        if (!result.CanDownload)
        {
            Services.ShowToast?.Invoke(new Kit.Popups.ToastContent(Severity.None, Strings.Settings.Updates, result.Message));
            return;
        }

        string title = string.Format(CultureInfo.CurrentCulture, Strings.Settings.UpdateAvailableTitle, result.LatestVersionText);
        if (!MessageDialog.Confirm(owner, title, Strings.Settings.UpdateInstallText, Strings.Kit.Cancel, Strings.Dialogs.Install, destructive: false))
        {
            return;
        }

        try
        {
            string setup = await updates.DownloadInstallerAsync().ConfigureAwait(true);
            updates.InstallDownloadedUpdate(setup);
            owner.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or System.Security.Cryptography.CryptographicException)
        {
            Services.ShowToast?.Invoke(new Kit.Popups.ToastContent(Severity.High, Strings.Settings.Updates, ex.Message));
        }
    }

    /// <summary>A 24-hour time box ("HH:mm"); an unreadable time is put back when the box loses focus.</summary>
    private SettingRow Time(string label, Func<string> get, Action<string> set)
    {
        KitTextBox box = new() { Text = get(), AccessibleName = label, Inset = true, MaxLength = 5 };
        box.TextCommitted += (_, _) =>
        {
            if (NotificationPolicy.TryParseTime(box.Text, out TimeOnly time))
            {
                set(time.ToString("HH:mm", CultureInfo.InvariantCulture));
                Save();
            }
        };
        box.Leave += (_, _) => box.Text = NotificationPolicy.TryParseTime(box.Text, out TimeOnly time) ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : get();
        return new SettingRow(label, null, box, null);
    }

    /// <summary>
    /// Settings → Notifications, <c>Send a test notification</c> (task N-05): a Windows notification when they are on and
    /// the channel works, otherwise the in-app toast; the bell keeps an entry either way.
    /// </summary>
    private async Task SendTestNotificationAsync()
    {
        if (Services.IsDemo)
        {
            return;
        }

        bool system = State.Settings.Scanning.ShowWindowsNotifications;
        bool shown = system && await Task.Run(() => ToastRegistration.Ensure(State)
            && SystemToast.Show(CoreStrings.Notify.TestTitle, CoreStrings.Notify.TestBody, new ActivationUri(ActivationTarget.Issues, 0), silent: false, allowBalloon: false, tag: "test", group: "test")).ConfigureAwait(true);
        try
        {
            await Services.Database.Notifications.AddAsync(new Core.Storage.NotificationRecord { Utc = DateTime.UtcNow.Ticks, Level = Severity.None, Title = CoreStrings.Notify.TestTitle, Body = CoreStrings.Notify.TestBody }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            State.Log.Warning("The test notification could not be stored", ex);
        }

        if (!shown)
        {
            Services.ShowToast?.Invoke(new Kit.Popups.ToastContent(Severity.None, CoreStrings.Notify.TestTitle, CoreStrings.Notify.TestBody));
        }

        if (FindForm() is MainForm main)
        {
            main.RefreshAll();
        }
    }

    private static SettingRow Button(string label, string? helper, string text, Action click)
    {
        KitButton button = new() { Kind = ButtonKind.Secondary, Text = text };
        button.Click += (_, _) => click();
        return new SettingRow(label, helper, button, null);
    }

    private static SettingRow Info(string label, string helper) => new(label, helper, null, null);

    /// <summary>The section list at the left (168 px), drawn like the navigation rail's items.</summary>
    private sealed class SectionList : KitControl
    {
        private readonly SettingsPage _page;
        private int _hover = -1;

        public SectionList(SettingsPage page)
        {
            _page = page;
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
        }

        protected override bool Interactive => true;

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Bg);
            TypeRamp type = Type;
            for (int i = 0; i < Names.Length; i++)
            {
                Rectangle row = ItemBounds(i);
                bool selected = i == _page.Current;
                if (selected || i == _hover)
                {
                    KitPaint.FillRoundedRect(g, row, SF(4), selected ? t.Selected : t.Hover);
                    if (selected)
                    {
                        KitPaint.FillRoundedRect(g, new RectangleF(row.X, row.Y + SF(8), SF(3), SF(16)), SF(1.5f), t.Accent);
                    }
                }

                KitPaint.DrawText(g, Names[i], selected ? type.BodyStrong : type.Body, t.Text, row.X + S(16), row.Y + (row.Height / 2), row.Width - S(24));
                if (i == UpdatesSection && _page.UpdateAvailable)
                {
                    int dotX = row.X + S(16) + KitPaint.MeasureWidth(Names[i], type.Body) + S(8);
                    KitPaint.FillRoundedRect(g, new RectangleF(dotX - SF(3), row.Y + (row.Height / 2f) - SF(3), SF(6), SF(6)), SF(3), t.Accent);
                }
            }

            if (KeyboardFocused)
            {
                KitPaint.DrawFocusRing(g, ItemBounds(_page.Current), SF(4), DeviceDpi);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hover = IndexAt(e.Location);
            if (hover != _hover)
            {
                _hover = hover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int index = IndexAt(e.Location);
            if (index >= 0)
            {
                _page.ShowSection(index);
            }
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Up or Keys.Down)
            {
                _page.ShowSection(_page.Current + (e.KeyCode == Keys.Up ? -1 : 1));
                e.Handled = true;
            }
        }

        private Rectangle ItemBounds(int index) => new(0, S(index * 36), Width, S(32));

        private int IndexAt(Point point)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                if (ItemBounds(i).Contains(point))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>A section: name in Subtitle, one line of Body Text2, then its rows in one card.</summary>
    private sealed class SettingsSection : KitControl
    {
        private readonly string _title;
        private readonly string _intro;
        private readonly Card _card = new();
        private readonly IReadOnlyList<SettingRow> _rows;

        public SettingsSection(SettingsPage page, string title, string intro, IReadOnlyList<SettingRow> rows)
        {
            _title = title;
            _intro = intro;
            _rows = rows;
            TabStop = false;
            foreach (SettingRow row in rows)
            {
                _card.Controls.Add(row);
            }

            _card.Visible = rows.Count > 0;
            Controls.Add(_card);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int y = S(1);
            int last = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!_rows[i].Collapsed)
                {
                    last = i;
                }
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                SettingRow row = _rows[i];
                row.Visible = !row.Collapsed;
                if (row.Collapsed)
                {
                    continue;
                }

                row.Last = i == last;
                row.Bounds = new Rectangle(S(1), y, Width - S(2), S(56));
                y += S(56);
            }

            _card.Bounds = new Rectangle(0, S(64), Width, y + S(1));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            e.Graphics.Clear(t.Bg);
            TypeRamp type = Type;
            KitPaint.DrawText(e.Graphics, _title, type.Subtitle, t.Text, 0, S(16), Width);
            KitPaint.DrawText(e.Graphics, _intro, type.Body, t.Text2, 0, S(41), Width);
        }
    }

    /// <summary>A 56 px setting row: label (and helper) on the left, its control right-aligned.</summary>
    private sealed class SettingRow : KitControl
    {
        private readonly string _label;
        private readonly string? _helper;
        private readonly Control? _control;
        private readonly string? _unit;

        public SettingRow(string label, string? helper, Control? control, string? unit)
        {
            _label = label;
            _helper = helper;
            _control = control;
            _unit = unit;
            TabStop = false;
            if (control is not null)
            {
                Controls.Add(control);
            }
        }

        public bool Last { get; set; }

        /// <summary>Left out of the section (the quiet-hours times while quiet hours are off).</summary>
        public bool Collapsed { get; set; }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_control is null)
            {
                return;
            }

            int unitWidth = _unit is null ? 0 : KitPaint.MeasureWidth(_unit, Type.Body) + S(12);
            Size size = _control switch
            {
                ToggleSwitch toggle => toggle.GetPreferredSize(Size.Empty),
                SegmentedControl segmented => new Size(S(segmented.PreferredLogicalWidth), S(32)),
                NumberBox => new Size(S(72), S(32)),
                DropDown => new Size(S(180), S(32)),
                KitButton button => new Size(S(button.PreferredLogicalWidth), S(32)),
                KitTextBox => new Size(S(80), S(32)),
                _ => new Size(S(200), S(32)),
            };
            _control.Bounds = new Rectangle(Width - S(16) - unitWidth - size.Width, (Height - size.Height) / 2, size.Width, size.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            TypeRamp type = Type;
            int labelWidth = (_control?.Left ?? Width) - S(32);
            if (_helper is null)
            {
                KitPaint.DrawText(g, _label, type.Body, t.Text, S(16), Height / 2, labelWidth);
            }
            else
            {
                KitPaint.DrawText(g, _label, type.Body, t.Text, S(16), S(19), labelWidth);
                KitPaint.DrawText(g, _helper, type.Caption, t.Text3, S(16), S(38), labelWidth);
            }

            if (_unit is not null && _control is not null)
            {
                KitPaint.DrawText(g, _unit, type.Body, t.Text2, _control.Right + S(12), Height / 2, Width - _control.Right - S(16));
            }

            if (!Last)
            {
                KitPaint.HorizontalLine(g, t.Stroke, S(16), Width - S(16), Height - SF(1), SF(1));
            }
        }

        protected override void OnThemeChanged()
        {
            BackColor = Tokens.Surface;
            base.OnThemeChanged();
        }
    }
}
