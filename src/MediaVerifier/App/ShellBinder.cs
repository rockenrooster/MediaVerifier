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
/// Stop signals that process. A scan started here shows in the strip at once. A change of the folders, findings,
/// ignores, notifications or repairs anywhere reloads the badge, the bell and the counts that decide whether Scan is
/// enabled; a request that waited for this window's scan starts when it ends, unless Stop withdrew it.
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
    private bool _reloadPending;
    private bool _starting;
    private bool _readingRun;
    private RunRecord? _backgroundRun;

    /// <summary>Changes that alter the badge, the bell or the Scan button: folders, findings and their ignores, notifications, repairs.</summary>
    private const ChangeFlags ShellChanges = ChangeFlags.Roots | ChangeFlags.Findings | ChangeFlags.Ignores | ChangeFlags.Notifications | ChangeFlags.Operations;

    public ShellBinder(MainForm form, UiServices services)
    {
        _form = form;
        _services = services;
        ShellHeader header = form.Header;
        header.Scan.Click += (_, _) => ScanAtDefaultDepth();
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

    /// <summary>
    /// Reloads the badge counts (after scans, repairs and settings changes). A call while a load runs loads once more
    /// when it ends, so the latest change is never lost.
    /// </summary>
    public void Refresh()
    {
        if (_form.IsDisposed)
        {
            return;
        }

        if (_loading)
        {
            _reloadPending = true;
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

    /// <summary>
    /// The database writer's thread (at most twice a second): a change of the folders, findings, ignores, notifications
    /// or repairs reloads the counts on the UI thread. The badge used to follow only folder changes, so it kept counting
    /// problems the user had just ignored or fixed.
    /// </summary>
    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        if ((e.Changes & ShellChanges) == 0 || !_form.IsHandleCreated || _form.IsDisposed)
        {
            return;
        }

        try
        {
            _form.BeginInvoke(() => OnShellDataChanged(e.Changes));
        }
        catch (InvalidOperationException)
        {
            // The window closed in between.
        }
    }

    private void OnShellDataChanged(ChangeFlags changes)
    {
        if (ReloadsCounts(changes, _wasActive))
        {
            Refresh();
        }
    }

    /// <summary>
    /// Whether a change reloads the counts. A scan of this window writes findings twice a second, and its end reloads
    /// everything, so findings that change while it runs do not reload the counts each time.
    /// </summary>
    internal static bool ReloadsCounts(ChangeFlags changes, bool scanning) =>
        (changes & (ShellChanges & ~ChangeFlags.Findings)) != 0 || ((changes & ChangeFlags.Findings) != 0 && !scanning);

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
            if (_reloadPending)
            {
                _reloadPending = false;
                Refresh();
            }
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
                // A scan of this window ended: the page on show reloads too, or Issues and the Dashboard kept
                // showing what was there before the scan until the user went to another page.
                _form.RefreshAll();
            }

            if (!active && !background && _services.QueuedScanCount > 0)
            {
                // No scan runs any more, here or in the background: a request that waited for one starts.
                UiTask.Run(_services.StartQueuedScanAsync());
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

        // The reason also for screen readers: a disabled button gets no mouse or keyboard input of its own.
        KitToolTip.Set(scan, reason ?? string.Empty);
        scan.AccessibleDescription = reason;
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
                    UiTask.Run(ScanFolderAsync());
                    break;
            }
        });
    }

    /// <summary>
    /// Scan a folder…: the enabled Library folders in a second menu below Scan, and the chosen one is scanned at the
    /// default depth, after the large-scan question when it applies. With one folder there is nothing to choose; with
    /// none it says to add one.
    /// (It only opened the Library page, and on the Library page it did nothing at all.)
    /// </summary>
    internal async Task ScanFolderAsync()
    {
        if (_services.IsDemo)
        {
            return;
        }

        List<RootRecord> roots = await Task.Run(_services.Database.Roots.List).ConfigureAwait(true);
        List<RootRecord> library = [.. roots.Where(r => r.Role == RootRole.Library && r.Enabled)];
        if (library.Count == 0)
        {
            _services.ShowScanNotStarted(Strings.Shell.AddFolderFirst);
            return;
        }

        if (library.Count == 1)
        {
            await ScanRootAsync(library[0]).ConfigureAwait(true);
            return;
        }

        if (_form.IsDisposed || !_form.Header.Scan.IsHandleCreated)
        {
            return;
        }

        PopupItem[] items = [.. library.Select(r => new PopupItem(FolderName(r), IconName.Folder, Tag: r))];
        KitMenu.ShowBelow(_form.Header.Scan, items, item =>
        {
            if (item.Tag is RootRecord root)
            {
                UiTask.Run(ScanRootAsync(root));
            }
        });
    }

    /// <summary>
    /// Scans one Library folder, asking first when it is large, as Scan does (subplan 09, Before a large scan): with one
    /// Library folder this is the whole library, and it started without the question.
    /// </summary>
    private async Task ScanRootAsync(RootRecord root)
    {
        ScanDepth depth = _services.State.Settings.Scanning.DefaultDepth;
        if (_services.State.Settings.Scanning.AskBeforeLargeScans)
        {
            ScanEstimate estimate = await Task.Run(() => _services.Data.LoadScanEstimate([root.Id])).ConfigureAwait(true);
            if (estimate.IsLarge)
            {
                if (_form.IsDisposed || LargeScanDialog.Ask(_form, estimate, depth) is not ScanDepth chosen)
                {
                    return;
                }

                depth = chosen;
            }
        }

        await _services.StartScanAsync([new ScanScope(root.Id)], depth, FolderName(root)).ConfigureAwait(true);
        Poll();
    }

    private static string FolderName(RootRecord root) =>
        root.Label ?? (Path.GetFileName(root.Path.TrimEnd('\\')) is { Length: > 0 } name ? name : root.Path);

    /// <summary>The Scan button, Ctrl+Shift+S and the tray's Scan now: every Library folder at the default depth.</summary>
    internal void ScanAtDefaultDepth() => StartScan(_services.State.Settings.Scanning.DefaultDepth, null);

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

    /// <summary>
    /// Header Scan and its menu; the launcher says why when a scan cannot start. A second press while the first is still
    /// starting (a double-click, a held Enter) is ignored, instead of being refused with "A scan is already running".
    /// </summary>
    private async void StartScan(ScanDepth depth, ScanOptions? options)
    {
        if (_services.IsDemo || _starting)
        {
            return;
        }

        _starting = true;
        try
        {
            await UI.Pages.ScanLauncher.StartLibraryScanAsync(_form, _services, depth, options).ConfigureAwait(true);
        }
        finally
        {
            _starting = false;
            if (!_form.IsDisposed)
            {
                Poll();
            }
        }
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
            // Stop means no more scanning: a request queued behind this scan, which nothing shows, started the moment
            // the stopped scan ended.
            _services.ClearQueuedScans();
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

    /// <summary>
    /// Enter searches Library → Browse. The key press is suppressed too, empty box included, or the single-line box
    /// beeped. (Esc, which clears the box, is the search box's own.)
    /// </summary>
    internal void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        if (_form.Header.Search.Text.Trim() is { Length: > 0 } text)
        {
            _services.Navigate?.Invoke(PageId.Library, new LibrarySearchRequest(text));
        }
    }
}

/// <summary>Search text from the header: Library → Browse filtered by it.</summary>
internal sealed record LibrarySearchRequest(string Text);
