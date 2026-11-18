using System.Globalization;
using System.Security;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Updates;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using Microsoft.Win32;

namespace MediaVerifier.App;

/// <summary>
/// Installed or portable (subplan 14, Installed versus portable): installed when the exe's folder equals the
/// <c>InstallLocation</c> of the installer's uninstall key, per user (HKCU) or for all users (HKLM).
/// </summary>
internal static class InstallDetection
{
    /// <summary>Inno Setup's uninstall key for the AppId in installer/MediaVerifier.iss.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AE2FDF62-8004-4807-BFF0-83A2C996BB18}_is1";

    public static bool IsInstalledCopy() => IsInstalledCopy(Environment.ProcessPath ?? Application.ExecutablePath, ReadLocations());

    /// <summary>True when the folder of <paramref name="exePath"/> is one of the recorded install locations.</summary>
    public static bool IsInstalledCopy(string exePath, IEnumerable<string?> installLocations)
    {
        string? folder = Normalize(Path.GetDirectoryName(exePath));
        return folder is not null && installLocations.Any(location => string.Equals(Normalize(location), folder, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string?> ReadLocations()
    {
        List<string?> locations = [];
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(UninstallKey);
                locations.Add(key?.GetValue("InstallLocation") as string);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                locations.Add(null);
            }
        }

        return locations;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>The update card's status line (subplan 09, Settings; task U-08), from the service's state.</summary>
internal static class UpdateStatusText
{
    public static string For(bool checking, bool downloading, double progress, UpdateCheckResult? result)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (downloading)
        {
            return string.Format(culture, Strings.Settings.Downloading, Math.Clamp((int)Math.Floor(progress), 0, 100));
        }

        if (checking)
        {
            return Strings.Settings.Checking;
        }

        if (result is null)
        {
            return Strings.Settings.NotChecked;
        }

        if (result.Failure is string failure)
        {
            // A download or install that failed after the check: say so until the next check.
            return failure;
        }

        if (result.CanDownload)
        {
            return string.Format(culture, Strings.Settings.Available, result.LatestVersionText.TrimStart('v', 'V'));
        }

        if (result.IsUpdateAvailable)
        {
            // A newer release whose signed metadata is missing or invalid: nothing can be installed.
            return Strings.Settings.NotVerified;
        }

        if (result.LatestVersion is null)
        {
            const string Prefix = "Update check failed: ";
            string reason = result.Message.StartsWith(Prefix, StringComparison.Ordinal) ? result.Message[Prefix.Length..] : result.Message;
            return string.Format(culture, Strings.Settings.CheckFailed, reason);
        }

        return Strings.Settings.UpToDate;
    }
}

/// <summary>
/// Download and install, or Install MediaVerifier on a portable copy (subplan 14; tasks U-08, U-09): one question
/// (subplan 09 words it differently while a scan runs), download and verify, stop a running scan, start the installer
/// silently and close. The download, the hashing and the installer start run off the UI thread.
/// </summary>
internal static class UpdateInstaller
{
    public static async Task RunAsync(Form owner, UiServices services, UpdateService updates, bool portable)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(updates);
        UpdateCheckResult? result = updates.LastResult;
        if (result is null || (portable ? !result.CanInstall : !result.CanDownload))
        {
            services.State.Log.Info("Update install asked for, but the last check offers nothing to install");
            return;
        }

        if (UI.Remediator.IsRunning)
        {
            // The installer closes the app: a repair would be cut short in the middle of a file.
            services.ShowToast?.Invoke(new UI.Kit.Popups.ToastContent(Core.Model.Severity.Medium, Strings.Shell.RepairRunning, Strings.Repair.TryAfterScan));
            return;
        }

        // Every step is logged and every failure shown: a failure that is only swallowed looks like a dead button.
        bool scanStopped = false;
        try
        {
            bool ownScan = services.Scans.Status.IsActive;
            bool backgroundScan = !ownScan && services.BackgroundRunActive();
            bool confirmed = ownScan || backgroundScan
                ? MessageDialog.Confirm(owner, Strings.Dialogs.InstallTitle, Strings.Dialogs.InstallText, Strings.Dialogs.NotNow, Strings.Dialogs.Install, destructive: false)
                : MessageDialog.Confirm(owner, string.Format(CultureInfo.CurrentCulture, Strings.Settings.UpdateAvailableTitle, result.LatestVersionText.TrimStart('v', 'V')), Strings.Settings.UpdateInstallText, Strings.Kit.Cancel, Strings.Dialogs.Install, destructive: false);
            if (!confirmed)
            {
                return;
            }

            services.State.Log.Info($"Update to {result.LatestVersionText}: downloading");
            string setup = await Task.Run(() => portable ? updates.DownloadInstallerAsync() : updates.DownloadLatestAsync()).ConfigureAwait(true);
            services.State.Log.Info($"Update to {result.LatestVersionText}: downloaded and verified");
            if (ownScan)
            {
                // Stop cleanly as interrupted (plan 09: it can be resumed after the update); the run is checkpointed
                // before the installer closes the app.
                services.Scans.Interrupt();
                await WaitIdleAsync(services, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                scanStopped = true;
            }
            else if (backgroundScan)
            {
                _ = PlanHost.StopBackgroundRun();
                scanStopped = true;
            }

            // Re-hashes the whole installer and starts it (antivirus scans it on open and on start).
            await Task.Run(() => updates.InstallDownloadedUpdate(setup)).ConfigureAwait(true);
            services.State.Log.Info($"Installer for {result.LatestVersionText} started; closing");
            if (owner is MainForm main)
            {
                main.ExitForUpdate();
            }
            else
            {
                owner.Close();
            }
        }
        catch (Exception ex)
        {
            services.State.Log.Warning("The update could not be installed", ex);
            string message = FailureText(ex, scanStopped);
            updates.RecordFailure(message);
            services.ShowToast?.Invoke(new UI.Kit.Popups.ToastContent(Core.Model.Severity.High, Strings.Settings.Updates, message));
        }
    }

    /// <summary>
    /// What went wrong, in the words of the step that failed (the check itself had succeeded): verification, the
    /// download, or starting the installer, which includes saying no to Windows' permission prompt.
    /// </summary>
    internal static string FailureText(Exception error, bool scanStopped)
    {
        ArgumentNullException.ThrowIfNull(error);
        string text = error switch
        {
            System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } => Strings.Settings.InstallCancelled,
            System.ComponentModel.Win32Exception start => string.Format(CultureInfo.CurrentCulture, Strings.Settings.InstallFailed, start.Message),
            InvalidOperationException or System.Security.Cryptography.CryptographicException => Strings.Settings.NotVerified,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.Settings.DownloadFailed, error.Message),
        };
        return scanStopped ? text + " " + Strings.Settings.ScanStopped : text;
    }

    private static async Task WaitIdleAsync(UiServices services, TimeSpan limit)
    {
        DateTime until = DateTime.UtcNow + limit;
        while (services.Scans.Status.Phase != EnginePhase.Idle && DateTime.UtcNow < until)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }
    }
}
