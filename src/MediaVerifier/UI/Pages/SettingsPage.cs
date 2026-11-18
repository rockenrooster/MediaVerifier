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
    private readonly SettingRow _updateRow;
    private readonly KitLink _releaseNotes = new() { Text = Strings.Settings.ReleaseNotes };
    private readonly KitButton _checkNow = new() { Kind = ButtonKind.Secondary, Text = Strings.Settings.CheckNow };
    private readonly KitButton _install = new() { Kind = ButtonKind.Primary, Text = Strings.Settings.DownloadInstall };
    private int _current;
    private bool _portable;
    private bool _saveFailureShown;
    private KitTextBox? _webhookBox;

    /// <summary>Puts each row's control back to the value in effect (the Schedule page edits two of them).</summary>
    private readonly List<Action> _reloaders = [];

    /// <summary>True while the rows are reloaded: their change events are not edits.</summary>
    private bool _loading;

    public SettingsPage(UiServices services)
        : base(services)
    {
        _nav = new SectionList(this) { TabIndex = 0 };
        _rules = new RulesView(this) { TabIndex = 1 };
        ScanSettings scan = services.State.Settings.Scanning;
        SettingRow quietFrom = Time(Strings.Settings.From, () => scan.QuietFrom, v => scan.QuietFrom = v);
        SettingRow quietTo = Time(Strings.Settings.To, () => scan.QuietTo, v => scan.QuietTo = v);
        quietFrom.Collapsed = quietTo.Collapsed = !scan.QuietHours;
        _updateRow = new SettingRow(string.Format(CultureInfo.CurrentCulture, Strings.Settings.VersionLine, AppInfo.VersionText), Strings.Settings.NotChecked, [_releaseNotes, _checkNow, _install]);
        _releaseNotes.Click += (_, _) => ShellActions.OpenUrl(ReleaseNotesUrl());
        _checkNow.Click += (_, _) => UiTask.Run(CheckForUpdatesAsync());
        _install.Click += (_, _) => UiTask.Run(InstallUpdateAsync());
        if (services.Updates is not null)
        {
            services.Updates.StatusChanged += OnUpdateStatusChanged;
        }

        _sections =
        [
            Section(Strings.Settings.General, Strings.Settings.GeneralIntro,
            [
                Segmented(Strings.Settings.Theme, null, [Strings.Settings.ThemeSystem, Strings.Settings.ThemeLight, Strings.Settings.ThemeDark], () => (int)State.Settings.Theme, i =>
                {
                    State.Settings.Theme = (ThemeMode)i;
                    ThemeService.Apply(State.Settings.Theme);
                }),
                Toggle(Strings.Settings.StartWithWindows, Strings.Settings.StartWithWindowsHelper, () => State.Settings.StartWithWindows, v =>
                {
                    // The setting alone did nothing: the Run value is what starts MediaVerifier with Windows.
                    State.Settings.StartWithWindows = v;
                    if (!Services.IsDemo)
                    {
                        new StartupRegistration().Apply(v);
                    }

                    (FindForm() as MainForm)?.UpdateTray();
                }),
                Toggle(Strings.Settings.KeepRunning, null, () => State.Settings.MinimizeToTray, v =>
                {
                    State.Settings.MinimizeToTray = v;
                    (FindForm() as MainForm)?.UpdateTray();
                }),
                Segmented(Strings.Settings.DefaultDepth, null, [Strings.Depths.Quick, Strings.Depths.Standard, Strings.Depths.Deep], () => DepthIndex(scan.DefaultDepth), i => scan.DefaultDepth = i switch { 0 => ScanDepth.Quick, 2 => ScanDepth.Deep, _ => ScanDepth.Standard }),
                Toggle(Strings.Settings.AskLarge, Strings.Settings.AskLargeHelper, () => scan.AskBeforeLargeScans, v => scan.AskBeforeLargeScans = v),
            ]),
            Section(Strings.Settings.Scanning, Strings.Settings.ScanningIntro,
            [
                Segmented(Strings.Settings.Speed, Strings.Settings.SpeedHelper, [Strings.Settings.SpeedLow, Strings.Settings.SpeedBalanced, Strings.Settings.SpeedHigh], () => (int)scan.Speed, i => scan.Speed = (PerformanceLevel)i),
                Number(Strings.Settings.ReadersHdd, null, null, 1, 16, () => scan.ReadersPerHdd, v => scan.ReadersPerHdd = v),
                Number(Strings.Settings.ReadersSsd, null, null, 1, 32, () => scan.ReadersPerSsd, v => scan.ReadersPerSsd = v),
                Number(Strings.Settings.ReadersNetwork, null, null, 1, 16, () => scan.ReadersPerNetwork, v => scan.ReadersPerNetwork = v),
                Button(Strings.Settings.FileTypes, null, Strings.Settings.EditEllipsis, _ => EditFileTypes(scan)),
                Button(Strings.Settings.AlwaysSkip, null, Strings.Settings.EditEllipsis, _ => EditAlwaysSkip(scan)),
                Toggle(Strings.Settings.CloudFiles, Strings.Settings.CloudFilesHelper, () => scan.ScanCloudFiles, v => scan.ScanCloudFiles = v),
                Drop(Strings.Settings.SkipLarger, null, [Strings.Settings.NoLimit, Strings.Settings.Gb10, Strings.Settings.Gb50, Strings.Settings.Gb100], () => SizeIndex(scan.SkipFilesLargerThan), i => scan.SkipFilesLargerThan = i switch { 1 => 10L << 30, 2 => 50L << 30, 3 => 100L << 30, _ => null }),
                Number(Strings.Settings.StallSeconds, null, Strings.Units.SecondsUnit, ScanSettings.MinStallSeconds, ScanSettings.MaxStallSeconds, () => scan.StallSeconds, v => scan.StallSeconds = v),
                Toggle(Strings.Settings.Upscaling, null, () => scan.CheckPhotosForUpscaling, v => scan.CheckPhotosForUpscaling = v),
                Toggle(Strings.Settings.Strict, Strings.Settings.StrictHelper, () => scan.StrictDecoding, v => scan.StrictDecoding = v),
            ]),
            _rules,
            Section(Strings.Settings.Integrity, Strings.Settings.IntegrityIntro,
            [
                // The Schedule page edits the same two values: one range for both (7 and 0 here disagreed with its 1).
                Number(Strings.Settings.ReReadEvery, null, Strings.Units.Days, ScanSettings.MinScrubPeriodDays, ScanSettings.MaxScrubPeriodDays, () => scan.ScrubPeriodDays, v => scan.ScrubPeriodDays = v),
                Number(Strings.Settings.TimeEachRun, null, Strings.Units.MinutesUnit, ScanSettings.MinScrubBudgetMinutes, ScanSettings.MaxScrubBudgetMinutes, () => scan.ScrubBudgetMinutes, v => scan.ScrubBudgetMinutes = v),
                Toggle(Strings.Settings.VerifyOnAnalysis, null, () => scan.VerifyOnAnalysis, v => scan.VerifyOnAnalysis = v),

                // Originals always go to the root's quarantine folder: "Keep originals after a repair in" was saved but
                // never read, so Recycle Bin and Another folder… did nothing (deviation logged; subplan 12).
                Number(Strings.Settings.EmptyQuarantine, null, Strings.Units.Days, ScanSettings.MinQuarantineDays, ScanSettings.MaxQuarantineDays, () => scan.EmptyQuarantineAfterDays, v => scan.EmptyQuarantineAfterDays = v),
            ]),
            Section(Strings.Settings.Notifications, Strings.Settings.NotificationsIntro,
            [
                Toggle(Strings.Settings.ShowWindowsNotifications, null, () => scan.ShowWindowsNotifications, v => scan.ShowWindowsNotifications = v),
                Drop(Strings.Settings.NotifyAbout, null, [Strings.Settings.CriticalOnly, Strings.Settings.HighAndAbove, Strings.Settings.MediumAndAbove, Strings.Settings.Everything], () => (int)scan.NotifyAbout, i => scan.NotifyAbout = (NotifyThreshold)i),
                Toggle(Strings.Settings.QuietHours, null, () => scan.QuietHours, v =>
                {
                    scan.QuietHours = v;
                    ShowQuietTimes(quietFrom, quietTo, v);
                }),
                quietFrom,
                quietTo,
                Button(Strings.Settings.TestNotificationLabel, null, Strings.Settings.TestNotification, _ => UiTask.Run(SendTestNotificationAsync())),
                Webhook(scan),
                Button(Strings.Settings.WebhookTestLabel, null, Strings.Settings.SendTest, b => UiTask.Run(WhileBusyAsync(b, Strings.Settings.WebhookSending, SendWebhookTestAsync))),
            ]),
            Section(Strings.Settings.Updates, Strings.Settings.UpdatesIntro,
            [
                _updateRow,
                Toggle(Strings.Settings.CheckOnStart, null, () => State.Settings.CheckForUpdatesOnStartup, v => State.Settings.CheckForUpdatesOnStartup = v),
            ]),
            Section(Strings.Settings.Advanced, Strings.Settings.AdvancedIntro,
            [
                // Change… led to Tools, where the database cannot be moved yet (Move… waits for phase 16): the button
                // shows where it is instead.
                Button(Strings.Settings.DatabaseLocation, State.Paths.ResolveDatabaseFile(State.Settings.DatabasePath), Strings.Tools.OpenFolder, _ => ShellActions.ShowInFolder(State.Paths.ResolveDatabaseFile(State.Settings.DatabasePath))),
                Button(Strings.Settings.Logs, null, Strings.Settings.OpenLogs, _ => ShellActions.OpenFolder(State.Paths.LogsDirectory)),
                Buttons(Strings.Settings.Diagnostics, null,
                [
                    (Strings.Settings.RunSelfCheck, b => UiTask.Run(WhileBusyAsync(b, Strings.Settings.SelfCheckRunning, RunSelfCheckAsync))),
                    (Strings.Settings.CopyReport, _ => UiTask.Run(CopySystemReportAsync())),
                ]),
            ]),
            Section(Strings.Settings.About, string.Empty, [new AboutRow(this)]),
        ];
        _reloaders.Add(() => ShowQuietTimes(quietFrom, quietTo, scan.QuietHours));
        Controls.Add(_nav);
        for (int i = 0; i < _sections.Length; i++)
        {
            _sections[i].Visible = i == 0;
            Controls.Add(_sections[i]);
        }
    }

    public override PageId Id => PageId.Settings;

    public override Control? FilterBox => _current == RulesSection ? _rules.Filter : null;

    /// <summary>F1 on a rule opens that rule's help (subplan 09, Keyboard); elsewhere the page's.</summary>
    public override string? HelpRule => _current == RulesSection ? _rules.FocusedRuleId : null;

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
        _nav.AnnounceSelection();
        if (Viewport is Shell.PageViewport viewport)
        {
            viewport.ScrollOffset = 0;
        }

        RelayoutPage();
    }

    /// <summary>The viewport while it shows this page (another page's scroll position is left alone).</summary>
    private Shell.PageViewport? Viewport => Parent is Shell.PageViewport viewport && ReferenceEquals(viewport.Page, this) ? viewport : null;

    /// <summary>
    /// A section taller than the content area scrolls with the page (Scanning has 11 rows): the page is as tall as the
    /// shown section. Rules fills the area and scrolls its own list.
    /// </summary>
    public override int MeasureContentHeight(int width) => _sections[_current] is SettingsSection section ? section.ContentHeight : 0;

    /// <summary>Sizes the page to the shown section again (another section, quiet-hours rows shown or hidden).</summary>
    private void RelayoutPage()
    {
        Viewport?.LayoutPage();
        PerformLayout();
    }

    private void ShowQuietTimes(SettingRow from, SettingRow to, bool shown)
    {
        if (from.Collapsed != shown)
        {
            return;
        }

        from.Collapsed = to.Collapsed = !shown;
        from.Parent?.Parent?.PerformLayout();
        RelayoutPage();
    }

    /// <summary>Saves the settings off the UI thread (every change applies at once; there is no Save button).</summary>
    internal void Save() => UiTask.Run(SaveAsync());

    /// <summary>
    /// The save's outcome: a change that could not be written would be lost at exit, so a failure says so in a Critical
    /// toast, once until a save works again (a number typed into a box saves at each key).
    /// </summary>
    private async Task SaveAsync()
    {
        bool saved = await State.SaveSettings().ConfigureAwait(true);
        if (saved)
        {
            _saveFailureShown = false;
            return;
        }

        if (!_saveFailureShown && !IsDisposed)
        {
            _saveFailureShown = true;
            Services.ShowToast?.Invoke(new Kit.Popups.ToastContent(Severity.Critical, Strings.Pages.Settings, Strings.Settings.SaveFailed, Strings.Settings.OpenLogs, () => ShellActions.OpenFolder(State.Paths.LogsDirectory)));
        }
    }

    protected override object? LoadData() => Services.Data.LoadShell();

    protected override void ApplyData(object data)
    {
        UpdateAvailable = ((Data.ShellCounts)data).UpdateAvailable || Services.UpdateAvailable;
        _nav.Invalidate();
        RefreshUpdateUi();
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        ReloadValues();
        Reload();
        UiTask.Run(DetectInstallAsync());
    }

    /// <summary>
    /// Shows the values in effect: the rows were built once, so a value changed on the Schedule page showed stale here,
    /// and the next arrow key saved over it.
    /// </summary>
    internal void ReloadValues()
    {
        _loading = true;
        try
        {
            foreach (Action reload in _reloaders)
            {
                reload();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        _rules.ClearEffects();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Services.Updates is not null)
        {
            Services.Updates.StatusChanged -= OnUpdateStatusChanged;
        }

        base.Dispose(disposing);
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

    private SettingsSection Section(string title, string intro, IReadOnlyList<SectionRow> rows) => new(this, title, intro, rows);

    private SettingRow Toggle(string label, string? helper, Func<bool> get, Action<bool> set)
    {
        ToggleSwitch toggle = new() { Checked = get(), AccessibleName = label };
        toggle.CheckedChanged += (_, _) =>
        {
            if (!_loading)
            {
                set(toggle.Checked);
                Save();
            }
        };
        _reloaders.Add(() => toggle.Checked = get());
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
            // The selected segment's label is bold: the row measures the control again.
            control.Parent?.PerformLayout();
            if (!_loading)
            {
                set(control.SelectedIndex);
                Save();
            }
        };
        _reloaders.Add(() => control.SelectedIndex = Math.Clamp(get(), 0, options.Length - 1));
        return new SettingRow(label, helper, control, null);
    }

    private SettingRow Number(string label, string? helper, string? unit, int min, int max, Func<int> get, Action<int> set)
    {
        NumberBox box = new() { Minimum = min, Maximum = max, Value = get(), AccessibleName = label, Inset = true };
        box.ValueChanged += (_, _) =>
        {
            if (!_loading)
            {
                set(box.Value);
                Save();
            }
        };
        _reloaders.Add(() => box.Value = get());
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
            if (!_loading)
            {
                set(drop.SelectedIndex);
                Save();
            }
        };
        _reloaders.Add(() => drop.SelectedIndex = Math.Clamp(get(), 0, options.Length - 1));
        return new SettingRow(label, helper, drop, null);
    }

    /// <summary>The release page of the latest check, or the releases list.</summary>
    private string ReleaseNotesUrl() => Services.Updates?.LastResult?.ReleaseUrl ?? ShellActions.ReleasesUrl;

    private void OnUpdateStatusChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(RefreshUpdateUi);
        }
        catch (InvalidOperationException)
        {
            // The page is closing.
        }
    }

    /// <summary>
    /// The update card (subplan 14; task U-08): the status line for every state, Check now while nothing runs, and
    /// Download and install (installed) or Install MediaVerifier (portable) exactly when the check allows it.
    /// </summary>
    internal void RefreshUpdateUi()
    {
        Core.Updates.UpdateService? updates = Services.Updates;
        bool checking = updates?.IsChecking == true;
        bool downloading = updates?.IsDownloading == true;
        Core.Updates.UpdateCheckResult? result = updates?.LastResult;
        _updateRow.Helper = UpdateStatusText.For(checking, downloading, updates?.DownloadProgress ?? 0, result);
        _checkNow.Enabled = updates is not null && !checking && !downloading;
        _install.Text = _portable ? Strings.Settings.InstallApp : Strings.Settings.DownloadInstall;
        _install.Enabled = updates is not null && !checking && !downloading && (_portable ? result?.CanInstall == true : result?.CanDownload == true);

        // A disabled button says why (subplan 09, Pitfalls): the status line is the reason ("Checking…", up to date…).
        KitToolTip.Set(_checkNow, _checkNow.Enabled ? string.Empty : _updateRow.Helper ?? string.Empty);
        KitToolTip.Set(_install, _install.Enabled ? string.Empty : _updateRow.Helper ?? string.Empty);
        bool available = Services.UpdateAvailable;
        if (available != UpdateAvailable)
        {
            UpdateAvailable = available;
            _nav.Invalidate();
        }

        _updateRow.PerformLayout();
    }

    private async Task DetectInstallAsync()
    {
        if (Services.IsDemo)
        {
            return;
        }

        bool installed = await Task.Run(InstallDetection.IsInstalledCopy).ConfigureAwait(true);
        _portable = !installed;
        RefreshUpdateUi();
    }

    /// <summary><c>Check now</c>: always allowed (subplan 14); the card follows the service's status events.</summary>
    private async Task CheckForUpdatesAsync()
    {
        if (Services.Updates is not Core.Updates.UpdateService updates)
        {
            return;
        }

        Core.Updates.UpdateCheckResult result = await updates.CheckAsync().ConfigureAwait(true);
        State.Log.Info("Update check: " + result.Message);
        RefreshUpdateUi();
        if (FindForm() is MainForm main)
        {
            main.RefreshUpdateIndicator();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (Services.IsDemo || Services.Updates is not Core.Updates.UpdateService updates || FindForm() is not Form owner)
        {
            return;
        }

        await UpdateInstaller.RunAsync(owner, Services, updates, _portable).ConfigureAwait(true);
        RefreshUpdateUi();
    }

    /// <summary><c>Copy system report</c>: the facts a bug report needs, gathered off the UI thread (it reads the database).</summary>
    private async Task CopySystemReportAsync()
    {
        try
        {
            string report = await Task.Run(() => Diagnostics.SystemReport.Build(Services, State)).ConfigureAwait(true);
            ShellActions.CopyText([report.TrimEnd()]);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            // A database or clipboard failure said where it happened, instead of the once-per-session error dialog.
            State.Log.Warning("The system report could not be copied", ex);
            Services.ShowToast?.Invoke(new Kit.Popups.ToastContent(Severity.Critical, string.Format(CultureInfo.CurrentCulture, Strings.Tools.ActionFailed, Strings.Settings.CopyReport), ex.Message));
            return;
        }

        // The copy is invisible otherwise: the button looked as if it did nothing.
        Services.ShowToast?.Invoke(new Kit.Popups.ToastContent(Severity.None, Strings.Settings.Diagnostics, Strings.Settings.ReportCopied));
    }

    /// <summary><c>Run self-check</c>: runs <c>--self-check</c> as its own process and reports the result.</summary>
    private async Task RunSelfCheckAsync()
    {
        if (Services.IsDemo)
        {
            return;
        }

        int exit = await Task.Run(() =>
        {
            System.Diagnostics.ProcessStartInfo start = new(Environment.ProcessPath ?? Application.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--self-check");
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return -1;
            }

            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                // Disposing the handle leaves the process running: a hung self-check is stopped.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    State.Log.Warning("The timed-out self-check could not be stopped", ex);
                }

                return -1;
            }

            return process.ExitCode;
        }).ConfigureAwait(true);
        Services.ShowToast?.Invoke(exit == 0
            ? new Kit.Popups.ToastContent(Severity.None, Strings.Settings.Diagnostics, Strings.Settings.SelfCheckPassed)
            : new Kit.Popups.ToastContent(Severity.High, Strings.Settings.Diagnostics, Strings.Settings.SelfCheckFailed, Strings.Settings.OpenLogs, () => ShellActions.OpenFolder(State.Paths.LogsDirectory)));
    }

    /// <summary><c>File types</c>: the photo, video and music extensions scans look at.</summary>
    private void EditFileTypes(ScanSettings scan)
    {
        if (FindForm() is not Form owner)
        {
            return;
        }

        List<string>[]? lists = ListEditDialog.Ask(owner, Strings.Settings.FileTypes, Strings.Settings.FileTypesHelper,
        [
            (Strings.Kinds.Photos, scan.PhotoExtensions, Core.Engine.MediaClassifier.DefaultPhoto),
            (Strings.Kinds.Videos, scan.VideoExtensions, Core.Engine.MediaClassifier.DefaultVideo),
            (Strings.Kinds.Music, scan.AudioExtensions, Core.Engine.MediaClassifier.DefaultAudio),
        ]);
        if (lists is null)
        {
            return;
        }

        // Normalised before the fallback: a list whose entries were all unusable ("jpg png gif" as one entry) became empty,
        // and the next scan indexed nothing of that kind.
        scan.PhotoExtensions = ScanSettings.ExtensionsOrDefaults(lists[0], Core.Engine.MediaClassifier.DefaultPhoto);
        scan.VideoExtensions = ScanSettings.ExtensionsOrDefaults(lists[1], Core.Engine.MediaClassifier.DefaultVideo);
        scan.AudioExtensions = ScanSettings.ExtensionsOrDefaults(lists[2], Core.Engine.MediaClassifier.DefaultAudio);
        Save();
    }

    /// <summary>
    /// <c>Always skip</c>: folder-name patterns no scan enters. The quarantine, the Recycle Bin and System Volume
    /// Information are always skipped and are not in the list to edit, so they cannot be removed from it.
    /// </summary>
    private void EditAlwaysSkip(ScanSettings scan)
    {
        if (FindForm() is not Form owner)
        {
            return;
        }

        List<string>[]? lists = ListEditDialog.Ask(owner, Strings.Settings.AlwaysSkip, Strings.Settings.AlwaysSkipHelper,
        [
            (Strings.Settings.FolderNames, [.. scan.AlwaysSkip.Where(p => !ScanSettings.IsProtectedSkip(p))], [.. Core.Engine.EnumerationRules.DefaultExcludedDirectories.Where(p => !ScanSettings.IsProtectedSkip(p))]),
        ]);
        if (lists is null)
        {
            return;
        }

        scan.AlwaysSkip = ScanSettings.WithProtectedSkip(lists[0]);
        Save();
    }

    /// <summary>A 24-hour time box ("HH:mm"); an unreadable time is put back when the box loses focus.</summary>
    private SettingRow Time(string label, Func<string> get, Action<string> set)
    {
        KitTextBox box = new() { Text = get(), AccessibleName = label, Inset = true, MaxLength = 5 };
        box.TextCommitted += (_, _) =>
        {
            if (NotificationPolicy.TryParseTime(box.Text, out TimeOnly time) && time.ToString("HH:mm", CultureInfo.InvariantCulture) is string value
                && !string.Equals(value, get(), StringComparison.Ordinal))
            {
                set(value);
                Save();
            }
        };
        box.Leave += (_, _) => box.Text = NotificationPolicy.TryParseTime(box.Text, out TimeOnly time) ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : get();
        _reloaders.Add(() => box.Text = get());
        return new SettingRow(label, null, box, null);
    }

    /// <summary>
    /// The webhook address (task N-06), checked when the box loses focus and before <c>Send test</c> (kit buttons take no
    /// focus, so a click there never left the box): HTTPS is taken as it is, plain HTTP to a private address after a
    /// confirmation, anything else is put back with a message. Empty turns the webhook off. An HTTPS address is also
    /// taken quietly once typing pauses, so closing the window from the box keeps it.
    /// </summary>
    private SettingRow Webhook(ScanSettings scan)
    {
        KitTextBox box = new() { Text = scan.WebhookUrl ?? string.Empty, AccessibleName = Strings.Settings.Webhook, Placeholder = Strings.Settings.WebhookPlaceholder, Inset = true, MaxLength = 2048 };
        box.Leave += (_, _) => BeginInvoke(() => CommitWebhook(box, scan));
        box.TextCommitted += (_, _) =>
        {
            if (WebhookAddress.Check(box.Text.Trim(), out _) == WebhookAddressVerdict.Ok)
            {
                CommitWebhook(box, scan);
            }
        };
        _webhookBox = box;
        _reloaders.Add(() => box.Text = scan.WebhookUrl ?? string.Empty);
        return new SettingRow(Strings.Settings.Webhook, Strings.Settings.WebhookHelper, box, null, width: 280);
    }

    private void CommitWebhook(KitTextBox box, ScanSettings scan)
    {
        string text = box.Text.Trim();
        if (string.Equals(text, scan.WebhookUrl ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        switch (WebhookAddress.Check(text, out _))
        {
            case WebhookAddressVerdict.Ok:
                scan.WebhookAllowHttp = false;
                break;
            case WebhookAddressVerdict.NeedsConfirmation when FindForm() is Form owner
                && MessageDialog.Confirm(owner, Strings.Settings.WebhookHttpTitle, Strings.Settings.WebhookHttpText, Strings.Kit.Cancel, Strings.Settings.WebhookHttpConfirm, destructive: false):
                scan.WebhookAllowHttp = true;
                break;
            case WebhookAddressVerdict.Invalid when text.Length == 0:
                scan.WebhookAllowHttp = false;
                break;
            default:
                box.Text = scan.WebhookUrl ?? string.Empty;
                if (WebhookAddress.Check(text, out _) == WebhookAddressVerdict.Invalid)
                {
                    Services.ShowToast?.Invoke(new Kit.Popups.ToastContent(Severity.Medium, Strings.Settings.Webhook, Strings.Settings.WebhookInvalid));
                }

                return;
        }

        scan.WebhookUrl = text.Length == 0 ? null : text;
        Save();
    }

    /// <summary>
    /// <c>Send test</c>: takes the address in the box first (it used to test the previously saved one and call a valid
    /// https address invalid), then posts the test payload and says whether it arrived.
    /// </summary>
    private async Task SendWebhookTestAsync()
    {
        ScanSettings scan = State.Settings.Scanning;
        if (_webhookBox is KitTextBox box)
        {
            string typed = box.Text.Trim();
            CommitWebhook(box, scan);
            if (!string.Equals(typed, scan.WebhookUrl ?? string.Empty, StringComparison.Ordinal))
            {
                // Not taken: CommitWebhook said why, or plain HTTP was declined.
                return;
            }
        }

        if (Services.IsDemo)
        {
            return;
        }

        WebhookAddressVerdict verdict = WebhookAddress.Check(scan.WebhookUrl, out Uri? address);
        if (address is null || (verdict == WebhookAddressVerdict.NeedsConfirmation && !scan.WebhookAllowHttp))
        {
            Services.ShowToast?.Invoke(new Kit.Popups.ToastContent(Severity.Medium, Strings.Settings.Webhook, Strings.Settings.WebhookInvalid));
            return;
        }

        bool sent = await new WebhookSender().SendAsync(address, WebhookPayload.Test(DateTimeOffset.UtcNow), message => State.Log.Warning(message)).ConfigureAwait(true);
        Services.ShowToast?.Invoke(sent
            ? new Kit.Popups.ToastContent(Severity.None, Strings.Settings.Webhook, Strings.Settings.WebhookSent)
            : new Kit.Popups.ToastContent(Severity.Medium, Strings.Settings.Webhook, Strings.Settings.WebhookFailed));
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

    private static SettingRow Buttons(string label, string? helper, IReadOnlyList<(string Text, Action<KitButton> Click)> buttons)
    {
        Control[] controls = new Control[buttons.Count];
        for (int i = 0; i < buttons.Count; i++)
        {
            (string text, Action<KitButton> click) = buttons[i];
            KitButton button = new() { Kind = ButtonKind.Secondary, Text = text };
            button.Click += (_, _) => click(button);
            controls[i] = button;
        }

        return new SettingRow(label, helper, controls);
    }

    private static SettingRow Button(string label, string? helper, string text, Action<KitButton> click)
    {
        KitButton button = new() { Kind = ButtonKind.Secondary, Text = text };
        button.Click += (_, _) => click(button);
        return new SettingRow(label, helper, button, null);
    }

    /// <summary>
    /// Runs a button's slow work (the self-check, a webhook test) with the button disabled and a tooltip saying why, so
    /// the click shows it was taken and a second click cannot start the work again.
    /// </summary>
    internal static async Task WhileBusyAsync(KitButton button, string reason, Func<Task> work)
    {
        if (!button.Enabled)
        {
            return;
        }

        button.Enabled = false;
        KitToolTip.Set(button, reason);
        try
        {
            await work().ConfigureAwait(true);
        }
        finally
        {
            if (!button.IsDisposed)
            {
                button.Enabled = true;
                KitToolTip.Set(button, string.Empty);
            }
        }
    }

    /// <summary>The section list at the left (168 px), drawn like the navigation rail's items.</summary>
    private sealed class SectionList : KitControl
    {
        private readonly SettingsPage _page;
        private int _hover = -1;
        private int _pressed = -1;

        public SectionList(SettingsPage page)
        {
            _page = page;
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = Strings.Pages.Settings;
        }

        protected override bool Interactive => true;

        /// <summary>Tells Narrator which section is now selected.</summary>
        public void AnnounceSelection() => AccessibilityNotifyClients(AccessibleEvents.Focus, _page.Current);

        protected override AccessibleObject CreateAccessibilityInstance() => new SectionListAccessibleObject(this);

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
                bool pressed = i == _pressed && i == _hover;
                if (selected || pressed || i == _hover)
                {
                    KitPaint.FillRoundedRect(g, row, SF(4), pressed ? t.Pressed : selected ? t.Selected : t.Hover);
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

        /// <summary>A click takes the focus (so the arrows then move here) and shows the pressed item.</summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = IndexAt(e.Location);
                Focus();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int index = IndexAt(e.Location);
            bool chosen = e.Button == MouseButtons.Left && index >= 0 && index == _pressed;
            _pressed = -1;
            Invalidate();
            if (chosen)
            {
                _page.ShowSection(index);
            }
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int? target = e.KeyCode switch
            {
                Keys.Up => _page.Current - 1,
                Keys.Down => _page.Current + 1,
                Keys.Home => 0,
                Keys.End => Names.Length - 1,
                _ => null,
            };
            if (target is int section)
            {
                _page.ShowSection(section);
                e.Handled = true;
            }
        }

        private Rectangle ItemBounds(int index) => new(0, S(index * 36), Width, S(32));

        /// <summary>The section list as a list whose items Narrator reads by name, with the selected one marked.</summary>
        private sealed class SectionListAccessibleObject(SectionList owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleRole Role => AccessibleRole.List;

            public override string? Name => owner.AccessibleName;

            public override int GetChildCount() => Names.Length;

            public override AccessibleObject? GetChild(int index) => index >= 0 && index < Names.Length ? new SectionItemAccessibleObject(owner, this, index) : null;

            public override AccessibleObject? GetFocused() => owner.Focused ? GetChild(owner._page.Current) : base.GetFocused();

            public override AccessibleObject? GetSelected() => GetChild(owner._page.Current);
        }

        private sealed class SectionItemAccessibleObject(SectionList list, AccessibleObject parent, int index) : AccessibleObject
        {
            public override AccessibleObject Parent => parent;

            public override AccessibleRole Role => AccessibleRole.ListItem;

            public override string? Name => index == UpdatesSection && list._page.UpdateAvailable ? Names[index] + ", " + Strings.Pages.UpdateAvailable : Names[index];

            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                    if (index == list._page.Current)
                    {
                        state |= AccessibleStates.Selected | (list.Focused ? AccessibleStates.Focused : AccessibleStates.None);
                    }

                    return state;
                }
            }

            public override Rectangle Bounds => list.RectangleToScreen(list.ItemBounds(index));

            public override void DoDefaultAction() => list._page.ShowSection(index);
        }

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
        private readonly IReadOnlyList<SectionRow> _rows;

        public SettingsSection(SettingsPage page, string title, string intro, IReadOnlyList<SectionRow> rows)
        {
            _title = title;
            _intro = intro;
            _rows = rows;
            TabStop = false;
            foreach (SectionRow row in rows)
            {
                _card.Controls.Add(row);
            }

            _card.Visible = rows.Count > 0;
            Controls.Add(_card);
        }

        /// <summary>Height of the heading and the card with every row shown, as <see cref="OnLayout"/> places them.</summary>
        public int ContentHeight
        {
            get
            {
                int height = S(64) + S(1) + S(1);
                foreach (SectionRow row in _rows)
                {
                    if (!row.Collapsed)
                    {
                        height += S(row.LogicalHeight);
                    }
                }

                return height;
            }
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
                SectionRow row = _rows[i];
                row.Visible = !row.Collapsed;
                if (row.Collapsed)
                {
                    continue;
                }

                row.Last = i == last;
                row.Bounds = new Rectangle(S(1), y, Width - S(2), S(row.LogicalHeight));
                y += S(row.LogicalHeight);
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

    /// <summary>A row of a section's card: its height and whether it is left out for now.</summary>
    private abstract class SectionRow : KitControl
    {
        public bool Last { get; set; }

        /// <summary>Left out of the section (the quiet-hours times while quiet hours are off).</summary>
        public bool Collapsed { get; set; }

        public virtual int LogicalHeight => 56;

        protected void PaintSeparator(Graphics g)
        {
            if (!Last)
            {
                KitPaint.HorizontalLine(g, Tokens.Stroke, S(16), Width - S(16), Height - SF(1), SF(1));
            }
        }

        protected override void OnThemeChanged()
        {
            BackColor = Tokens.Surface;
            base.OnThemeChanged();
        }
    }

    /// <summary>A 56 px setting row: label (and helper) on the left, its controls right-aligned 8 px apart.</summary>
    private sealed class SettingRow : SectionRow
    {
        private readonly string _label;
        private readonly Control[] _controls;
        private readonly string? _unit;
        private readonly int? _width;
        private string? _helper;

        public SettingRow(string label, string? helper, Control? control, string? unit, int? width = null)
            : this(label, helper, control is null ? [] : [control], unit, width)
        {
        }

        public SettingRow(string label, string? helper, Control[] controls, string? unit = null, int? width = null)
        {
            _label = label;
            _helper = helper;
            _controls = controls;
            _unit = unit;
            _width = width;
            TabStop = false;
            foreach (Control control in controls)
            {
                Controls.Add(control);
            }
        }

        /// <summary>The second line (the update card's status changes it).</summary>
        public string? Helper
        {
            get => _helper;
            set
            {
                if (!string.Equals(_helper, value, StringComparison.Ordinal))
                {
                    _helper = value;
                    UpdateCutTextTip();
                    Invalidate();
                }
            }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_controls.Length > 0)
            {
                int unitWidth = _unit is null ? 0 : KitPaint.MeasureWidth(_unit, Type.Body) + S(12);
                int right = Width - S(16) - unitWidth;
                for (int i = _controls.Length - 1; i >= 0; i--)
                {
                    Control control = _controls[i];
                    Size size = _width is int logical && _controls.Length == 1 ? new Size(S(logical), S(32)) : SizeOf(control);
                    control.Bounds = new Rectangle(right - size.Width, (Height - size.Height) / 2, size.Width, size.Height);
                    right = control.Left - S(8);
                }
            }

            UpdateCutTextTip();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            TypeRamp type = Type;
            int labelWidth = (_controls.Length > 0 ? _controls[0].Left : Width) - S(32);
            if (_helper is null)
            {
                KitPaint.DrawText(g, _label, type.Body, t.Text, S(16), Height / 2, labelWidth);
            }
            else
            {
                KitPaint.DrawText(g, _label, type.Body, t.Text, S(16), S(19), labelWidth);
                KitPaint.DrawText(g, _helper, type.Caption, t.Text3, S(16), S(38), labelWidth);
            }

            if (_unit is not null && _controls.Length > 0)
            {
                Control last = _controls[^1];
                KitPaint.DrawText(g, _unit, type.Body, t.Text2, last.Right + S(12), Height / 2, Width - last.Right - S(16));
            }

            PaintSeparator(g);
        }

        /// <summary>
        /// Text cut short gets a tooltip with all of it (subplan 09, tooltips): the database path, or an update error at the
        /// minimum width, could not be read.
        /// </summary>
        private void UpdateCutTextTip()
        {
            if (Width <= 0)
            {
                return;
            }

            int labelWidth = (_controls.Length > 0 ? _controls[0].Left : Width) - S(32);
            string tip = _helper is not null && KitPaint.MeasureWidth(_helper, Type.Caption) > labelWidth ? _helper
                : KitPaint.MeasureWidth(_label, Type.Body) > labelWidth ? _label
                : string.Empty;
            if (tip.Length > 0 || KitToolTip.Get(this).Length > 0)
            {
                KitToolTip.Set(this, tip);
            }
        }

        private Size SizeOf(Control control) => control switch
        {
            ToggleSwitch toggle => toggle.GetPreferredSize(Size.Empty),
            SegmentedControl segmented => new Size(S(segmented.PreferredLogicalWidth), S(32)),
            NumberBox => new Size(S(72), S(32)),
            DropDown => new Size(S(180), S(32)),
            KitButton button => new Size(S(button.PreferredLogicalWidth), S(32)),
            KitLink link => new Size(S(link.PreferredLogicalWidth), S(32)),
            KitTextBox => new Size(S(80), S(32)),
            _ => new Size(S(200), S(32)),
        };
    }

    /// <summary>
    /// About (subplan 09, Settings): the app icon at 48 px, <c>MediaVerifier</c> in Subtitle, <c>Version {v}</c> in Body
    /// Text2, and the links <c>Website</c>, <c>Release notes</c> and <c>Licences</c> 16 px apart.
    /// </summary>
    private sealed class AboutRow : SectionRow
    {
        private readonly KitLink[] _links;

        public AboutRow(SettingsPage page)
        {
            TabStop = false;
            _links =
            [
                Link(Strings.Settings.Website, () => ShellActions.OpenUrl(ShellActions.WebsiteUrl)),
                Link(Strings.Settings.ReleaseNotes, () => ShellActions.OpenUrl(page.ReleaseNotesUrl())),
                Link(Strings.Settings.Licences, OpenLicences),
            ];
            Controls.AddRange(_links);
        }

        public override int LogicalHeight => 112;

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int x = S(80);
            foreach (KitLink link in _links)
            {
                int width = S(link.PreferredLogicalWidth);
                link.Bounds = new Rectangle(x - S(2), S(68), width, S(28));
                x += width + S(16);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            ThemeTokens t = Tokens;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            IconRenderer.DrawAppIcon(g, new RectangleF(S(16), S(16), S(48), S(48)), small: false);
            TypeRamp type = Type;
            KitPaint.DrawText(g, AppInfo.ProductName, type.Subtitle, t.Text, S(80), S(28), Width - S(96));
            KitPaint.DrawText(g, string.Format(CultureInfo.CurrentCulture, Strings.Settings.VersionLine, AppInfo.VersionText), type.Body, t.Text2, S(80), S(52), Width - S(96));
            PaintSeparator(g);
        }

        private static KitLink Link(string text, Action click)
        {
            KitLink link = new() { Text = text };
            link.Click += (_, _) => click();
            return link;
        }

        /// <summary>The notices shipped next to the exe; the repository's copy when running from a build folder.</summary>
        private static void OpenLicences()
        {
            string notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
            ShellActions.OpenFileOr(notices, ShellActions.NoticesUrl);
        }
    }
}
