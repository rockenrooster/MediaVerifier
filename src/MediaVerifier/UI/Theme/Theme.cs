using MediaVerifier.Core.Settings;
using Microsoft.Win32;

namespace MediaVerifier.UI.Theme;

/// <summary>
/// The active token set. One per process: the settings choose System, Light or Dark, and System
/// follows the Windows app theme. Controls read <see cref="Tokens"/> while painting and repaint on
/// <see cref="Changed"/>.
/// </summary>
internal static class Theme
{
    private static ThemeMode s_mode = ThemeMode.System;
    private static ThemeTokens s_tokens = ResolveTokens(ThemeMode.System);
    private static bool s_listening;

    public static ThemeTokens Tokens => s_tokens;

    public static ThemeMode Mode => s_mode;

    /// <summary>Raised on the thread that changed the theme (the UI thread).</summary>
    public static event EventHandler? Changed;

    /// <summary>Applies the user's choice and starts following Windows when it is System.</summary>
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

    /// <summary>Forces a token set (the screen renderer uses this for dark and light renders).</summary>
    public static void Set(ThemeTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (ReferenceEquals(tokens, s_tokens))
        {
            return;
        }

        s_tokens = tokens;
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

    public static ThemeTokens ResolveTokens(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => ThemeTokens.Dark,
        ThemeMode.Light => ThemeTokens.Light,
        _ => SystemUsesDarkTheme() ? ThemeTokens.Dark : ThemeTokens.Light,
    };

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

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (s_mode != ThemeMode.System || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        ThemeTokens tokens = ResolveTokens(ThemeMode.System);
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
