using Microsoft.Win32;

namespace MediaVerifier.App;

/// <summary>
/// The Start-with-Windows toggle (task F-12): a value under HKCU\...\Run that starts the app hidden
/// with <c>--minimized</c>. The key path is overridable so tests never touch the real Run key.
/// </summary>
internal sealed class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "MediaVerifier";

    private readonly string _keyPath;
    private readonly string _executablePath;

    public StartupRegistration(string? keyPath = null, string? executablePath = null)
    {
        _keyPath = keyPath ?? RunKeyPath;
        _executablePath = executablePath ?? RegistrationTarget.ExePath();
    }

    public string Command => "\"" + _executablePath + "\" " + CommandLineParser.Minimized;

    public bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>True when the value exists and points at this executable.</summary>
    public bool IsCurrent
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Apply(bool enabled)
    {
        if (enabled)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            Remove();
        }
    }

    /// <summary>
    /// Makes the Run value match the setting (start-up maintenance): written when it should start with Windows and
    /// is missing or points elsewhere (an earlier version never wrote it; a reinstall moves the app), removed
    /// otherwise.
    /// </summary>
    public void Reconcile(bool enabled)
    {
        if (enabled ? !IsCurrent : IsEnabled)
        {
            Apply(enabled);
        }
    }

    public void Remove()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// The executable that the per-user registrations point at: the Run value, the notification and link handler, and the
/// scheduled tasks. That is the installed copy when there is one, otherwise the copy that runs. Every start used to
/// point them at whichever copy started, so a development build (or a second portable copy) took over the installed
/// app's notification links, and a schedule could run a copy without its media engine. A reinstall to another folder
/// still moves them, as subplan 14 asks after an update.
/// </summary>
internal static class RegistrationTarget
{
    private const string ExeName = "MediaVerifier.exe";

    public static string ExePath() =>
        Choose(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName), InstallLocations(), File.Exists);

    /// <summary>The first install location that holds the executable, otherwise <paramref name="running"/>.</summary>
    internal static string Choose(string running, IEnumerable<string?> installLocations, Func<string, bool> exists)
    {
        foreach (string? location in installLocations)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            try
            {
                string exe = Path.Combine(Path.GetFullPath(location.Trim().Trim('"')), ExeName);
                if (exists(exe))
                {
                    return exe;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed InstallLocation: not a copy to point at.
            }
        }

        return running;
    }

    /// <summary>Inno Setup's recorded install folders (per user, then per machine).</summary>
    private static List<string?> InstallLocations()
    {
        List<string?> locations = [];
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(InstallDetection.UninstallKey);
                locations.Add(key?.GetValue("InstallLocation") as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable: treated as not installed there.
            }
        }

        return locations;
    }
}
