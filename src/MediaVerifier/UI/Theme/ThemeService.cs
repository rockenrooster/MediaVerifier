using System.Runtime.InteropServices;
using MediaVerifier.Core.Settings;
using Microsoft.Win32;

namespace MediaVerifier.UI.Theme;

/// <summary>
/// The active token set (subplan 08, Theme service and high contrast). One per process: the settings
/// choose System, Light or Dark; System follows the Windows app theme; a Windows contrast theme always
/// wins and maps every token to system colours. Controls read <see cref="Tokens"/> while painting and
/// repaint on <see cref="Changed"/>.
/// </summary>
internal static class ThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;

    private static ThemeMode s_mode = ThemeMode.System;
    private static ThemeTokens s_tokens = ResolveTokens(ThemeMode.System);
    private static bool s_listening;

    public static ThemeTokens Tokens => s_tokens;

    public static ThemeMode Mode => s_mode;

    /// <summary>Raised on the thread that changed the theme (the UI thread), after the caches were reset.</summary>
    public static event EventHandler? Changed;

    /// <summary>Applies the user's choice and starts following Windows (app theme and contrast themes).</summary>
    public static void Apply(ThemeMode mode)
    {
        s_mode = mode;
        if (!s_listening)
        {
            s_listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        Set(ResolveTokens(mode));
    }

    /// <summary>Forces a token set (the screen renderer uses this for dark, light and contrast renders).</summary>
    public static void Set(ThemeTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (ReferenceEquals(tokens, s_tokens))
        {
            return;
        }

        s_tokens = tokens;
        ThemeResources.Reset();
        foreach (Form form in Application.OpenForms)
        {
            ApplyTitleBar(form);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void StopListening()
    {
        if (s_listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            s_listening = false;
        }
    }

    public static ThemeTokens ResolveTokens(ThemeMode mode)
    {
        if (SystemInformation.HighContrast)
        {
            return ThemeTokens.HighContrast();
        }

        return mode switch
        {
            ThemeMode.Dark => ThemeTokens.Dark,
            ThemeMode.Light => ThemeTokens.Light,
            _ => SystemUsesDarkTheme() ? ThemeTokens.Dark : ThemeTokens.Light,
        };
    }

    /// <summary>Windows' app theme: AppsUseLightTheme = 0 means dark.</summary>
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The standard title bar takes the theme's dark or light caption (DWM attribute 20).</summary>
    public static void ApplyTitleBar(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!form.IsHandleCreated)
        {
            return;
        }

        int dark = s_tokens.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility))
        {
            return;
        }

        if (s_mode != ThemeMode.System && !SystemInformation.HighContrast && !s_tokens.IsHighContrast)
        {
            return;
        }

        ThemeTokens tokens = ResolveTokens(s_mode);
        if (Application.OpenForms.Count > 0 && Application.OpenForms[0] is { IsHandleCreated: true } form)
        {
            form.BeginInvoke(() => Set(tokens));
        }
        else
        {
            Set(tokens);
        }
    }
}
