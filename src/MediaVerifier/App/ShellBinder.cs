using System.Globalization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Pages;
using MediaVerifier.UI.Shell;

namespace MediaVerifier.App;

/// <summary>
/// The shell's bindings (subplan 09, Shell; task UI-02): the Issues badge, the bell, the Scan split
/// button with its menu and disabled reasons, the search box, and the status strip in every scan phase.
/// Polls the engine's status snapshot at 4 Hz while a scan runs and every 2 s otherwise. A scheduled scan in another
/// process (subplan 13, task SCH-06) shows in the strip with its checkpointed counts, polled every second, and its
/// Stop signals that process. A scan started here shows in the strip at once, and adding, editing or removing a
/// folder anywhere reloads the counts that decide whether Scan is enabled.
/// </summary>
internal sealed class ShellBinder : IDisposable
{
    private readonly MainForm _form;
    private readonly UiServices _services;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 250 };
    private ShellCounts? _counts;
    private bool _wasActive;
    private bool _wasBackground;
    private bool _loading;
    private bool _readingRun;
    private RunRecord? _backgroundRun;

    public ShellBinder(MainForm form, UiServices services)
    {
        _form = form;
        _services = services;
        ShellHeader header = form.Header;
        header.Scan.Click += (_, _) => StartScan(_services.State.Settings.Scanning.DefaultDepth, null);
        header.Scan.DropDownClick += (_, _) => ShowScanMenu();
        header.Search.KeyDown += OnSearchKey;
        header.Bell.Click += (_, _) => UiTask.Run(ShowNotificationsAsync());
        ScanStatusStrip strip = form.StatusStrip;
        strip.Pause.Click += (_, _) => TogglePause();
        strip.Stop.Click += (_, _) => AskStop();
        strip.Click += (_, _) => _services.Navigate?.Invoke(PageId.Scans, null);
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        _services.ScanStarted += Poll;
        _services.Database.LibraryChanged += OnLibraryChanged;
        Refresh();
        Poll();
    }

    /// <summary>Reads the scan status now (the render harness switches the demo scan between pages).</summary>
    internal void PollNow() => Poll();

    /// <summary>Reloads the badge counts (after scans, repairs and settings changes).</summary>
    public void Refresh()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        UiTask.Run(LoadAsync());
    }

    public void Dispose()
    {
        _services.ScanStarted -= Poll;
        _services.Database.LibraryChanged -= OnLibraryChanged;
        _poll.Dispose();
    }

    /// <summary>The database writer's thread: a change of the watched folders reloads the counts on the UI thread.</summary>
    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        if ((e.Changes & ChangeFlags.Roots) == 0 || !_form.IsHandleCreated || _form.IsDisposed)
        {
            return;
        }

        try
        {
            _form.BeginInvoke(Refresh);
        }
        catch (InvalidOperationException)
        {
            // The window closed in between.
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            ShellCounts counts = await Task.Run(_services.Data.LoadShell).ConfigureAwait(true);
            if (_form.IsDisposed)
            {
                return;
            }

            _counts = counts;
            _form.Rail.SetIssuesBadge(counts.OpenMediumOrAbove, counts.Worst);
            _form.Header.Bell.Badge = counts.UnreadNotifications;
            _form.Rail.UpdateAvailable = counts.UpdateAvailable || _services.UpdateAvailable;
            UpdateScanButton();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            _services.State.Log.Warning("Shell counts could not be loaded", ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Poll()
    {
        EngineStatus status = _services.Scans.Status;
        bool active = status.IsActive;
        bool background = !active && _services.BackgroundRunActive();
        _poll.Interval = active ? 250 : background ? 1000 : 2000;
        // The Scans page shows the run itself, so the strip hides there (as in the scans mockup). A background run
        // has no card of its own, so the strip shows it everywhere.
        _form.Shell.StatusStripVisible = (active && _form.CurrentPageControl?.Id != UI.Pages.PageId.Scans) || background;
        _form.StatusStrip.Pause.Visible = !background;
        if (active)
        {
            UpdateStrip(status);
        }
        else if (background)
        {
            UpdateBackgroundStrip();
        }

        if (_wasActive != active || _wasBackground != background)
        {
            bool backgroundEnded = _wasBackground && !background;
            _wasActive = active;
            _wasBackground = background;
            UpdateScanButton();
            if (backgroundEnded)
            {
                _backgroundRun = null;
                _form.RefreshAll();
            }
            else if (!active && !background)
            {
                Refresh();
            }
        }
    }

    /// <summary>The strip for a scheduled scan in another process: its counts as last checkpointed (every 5 s).</summary>
    private void UpdateBackgroundStrip()
    {
        if (!_readingRun)
        {
            _readingRun = true;
            UiTask.Run(ReadBackgroundRunAsync());
        }

        string facts = Strings.Shell.ScheduledRunning;
        string details = _backgroundRun is RunRecord run
            ? string.Format(CultureInfo.CurrentCulture, Strings.Shell.BackgroundChecked, UiFormat.Count(run.FilesAnalysed + run.FilesVerified))
            : string.Empty;
        ScanStatusStrip strip = _form.StatusStrip;
        strip.Progress = null;
        strip.SetText(facts, details, facts);
        strip.PerformLayout();
    }

    private async Task ReadBackgroundRunAsync()
    {
        try
        {
            RunRecord? run = await Task.Run(_services.Database.Runs.LatestRunning).ConfigureAwait(true);
            if (!_form.IsDisposed && _wasBackground)
            {
                _backgroundRun = run;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _services.State.Log.Warning("The background run could not be read", ex);
        }
        finally
        {
            _readingRun = false;
        }
    }

    private void UpdateStrip(EngineStatus status)
    {
        ScanStatusStrip strip = _form.StatusStrip;
        CultureInfo culture = CultureInfo.CurrentCulture;
        string done = UiFormat.Count(status.FilesDone);
        string total = UiFormat.Count(status.Total);
        string facts;
        string details = string.Empty;
        string shortText;
        switch (status.Phase)
        {
            case EnginePhase.Enumerating:
                facts = string.Format(culture, Strings.Shell.Finding, UiFormat.Count(status.FilesSeen));
                shortText = facts;
                break;
            case EnginePhase.Pausing:
                facts = string.Format(culture, Strings.Shell.Pausing, UiFormat.Count(status.PausingFiles));
                shortText = facts;
                break;
            case EnginePhase.Paused:
                facts = string.Format(culture, Strings.Shell.Paused, done, total);
                shortText = facts;
                break;
            case EnginePhase.Finishing:
                facts = Strings.Shell.Finishing;
                shortText = facts;
                break;
            default:
                facts = string.Format(culture, Strings.Shell.Scanning, done, total);
                string rate = UiFormat.Rate(status.BytesPerSecond);
                if (status.Remaining is TimeSpan left && !status.Estimating)
                {
                    details = string.Format(culture, Strings.Shell.ScanningDetails, rate, UiFormat.Duration(left));
                    shortText = string.Format(culture, Strings.Shell.ShortScanning, done, total, UiFormat.Duration(left));
                }
                else
                {
                    details = string.Format(culture, Strings.Shell.ScanningEstimating, rate);
                    shortText = string.Format(culture, Strings.Shell.ShortEstimating, done, total);
                }

                break;
        }

        strip.Progress = status.Phase == EnginePhase.Enumerating ? null : status.Fraction;
        strip.SetText(facts, details, shortText);
        bool paused = status.Phase is EnginePhase.Paused or EnginePhase.Pausing;
        strip.Pause.Text = paused ? Strings.Shell.Resume : Strings.Shell.Pause;
        strip.Pause.Icon = paused ? IconName.Play : IconName.Pause;
        strip.PerformLayout();
    }

    private void UpdateScanButton()
    {
        string? reason = _services.CannotScanReason(_counts?.HasFolders ?? true);
        SplitButton scan = _form.Header.Scan;
        scan.Enabled = reason is null;
        KitToolTip.Set(scan, reason ?? string.Empty);
    }

    private void ShowScanMenu()
    {
        PopupItem[] items =
        [
            new(Strings.Shell.QuickScan, IconName.Play, Tag: ScanDepth.Quick),
            new(Strings.Shell.StandardScan, IconName.Play, Tag: ScanDepth.Standard),
            new(Strings.Shell.DeepScan, IconName.Play, Tag: ScanDepth.Deep),
            PopupItem.Separator,
            new(Strings.Shell.VerifyOnly, IconName.ShieldCheck, Tag: "verify"),
            new(Strings.Shell.ScanFolder, IconName.Folder, Tag: "folder"),
        ];
        KitMenu.ShowBelow(_form.Header.Scan, items, item =>
        {
            switch (item.Tag)
            {
                case ScanDepth depth:
                    StartScan(depth, null);
                    break;
                case "verify":
                    StartScan(ScanDepth.Quick, new ScanOptions { VerifyOnly = true, VerifyEverything = true });
                    break;
                case "folder":
                    _services.Navigate?.Invoke(PageId.Library, null);
                    break;
            }
        });
    }

    /// <summary>Ctrl+Shift+S: scan now at the default depth.</summary>
    internal void ScanNow() => StartScan(_services.State.Settings.Scanning.DefaultDepth, null);

    /// <summary>Opens the notifications flyout under the bell (subplan 09, H).</summary>
    private async Task ShowNotificationsAsync()
    {
        List<NotificationRecord> items = await Task.Run(() => _services.Database.Notifications.List(50)).ConfigureAwait(true);
        NotificationsFlyout flyout = NotificationsFlyout.Show(_form.Header.Bell, items, _services.Clock.UtcNow);
        flyout.MarkAllRead += async (_, _) =>
        {
            await _services.Database.Notifications.MarkAllReadAsync().ConfigureAwait(true);
            Refresh();
        };
        flyout.ClearAll += async (_, _) =>
        {
            await _services.Database.Notifications.ClearAllAsync().ConfigureAwait(true);
            Refresh();
        };
        flyout.ItemClicked += async (_, item) =>
        {
            await _services.Database.Notifications.MarkReadAsync(item.Id).ConfigureAwait(true);
            Refresh();
            if (item.ActionUri is string uri && uri.StartsWith("page:", StringComparison.Ordinal) && Enum.TryParse(uri[5..], out PageId page))
            {
                _services.Navigate?.Invoke(page, null);
            }
        };
    }

    /// <summary>Header Scan and its menu; the launcher says why when a scan cannot start.</summary>
    private async void StartScan(ScanDepth depth, ScanOptions? options)
    {
        if (_services.IsDemo)
        {
            return;
        }

        await UI.Pages.ScanLauncher.StartLibraryScanAsync(_form, _services, depth, options).ConfigureAwait(true);
        Poll();
    }

    private void TogglePause()
    {
        EngineStatus status = _services.Scans.Status;
        if (status.Phase is EnginePhase.Paused or EnginePhase.Pausing)
        {
            _services.Scans.Resume();
        }
        else
        {
            _services.Scans.Pause();
        }

        Poll();
    }

    private void AskStop()
    {
        bool background = !_services.Scans.Status.IsActive && _services.BackgroundRunActive();
        if (MessageDialog.Confirm(_form, Strings.Shell.StopTitle, Strings.Shell.StopText, Strings.Shell.KeepScanning, Strings.Shell.StopButton, destructive: false))
        {
            if (background)
            {
                // The headless run treats its stop event as Cancel (subplan 13, GUI opened during a headless run).
                _ = PlanHost.StopBackgroundRun();
            }
            else
            {
                _services.Scans.Cancel();
            }

            Poll();
        }
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && _form.Header.Search.Text.Length > 0)
        {
            e.Handled = true;
            _services.Navigate?.Invoke(PageId.Library, new LibrarySearchRequest(_form.Header.Search.Text));
        }
    }
}

/// <summary>Search text from the header: Library → Browse filtered by it.</summary>
internal sealed record LibrarySearchRequest(string Text);
