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
        _executablePath = executablePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MediaVerifier.exe");
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
