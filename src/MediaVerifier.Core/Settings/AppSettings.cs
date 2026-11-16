using System.Text.Json.Serialization;

namespace MediaVerifier.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// User settings, persisted as %APPDATA%\MediaVerifier\settings.json by <see cref="SettingsStore"/>.
/// Plain data: later phases add properties with defaults, and the upgrade hook moves old values.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Bump when a stored value changes meaning; <see cref="SettingsStore"/> upgrades older files.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Empty means the default location (AppPaths.DefaultDatabaseFile).</summary>
    public string DatabasePath { get; set; } = "";

    public WindowPlacement WindowPlacement { get; set; } = new();

    /// <summary>Restores invariants after loading a hand-edited or older file.</summary>
    internal AppSettings Normalize()
    {
        DatabasePath ??= "";
        WindowPlacement ??= new WindowPlacement();
        WindowPlacement.Normalize();
        if (!Enum.IsDefined(Theme))
        {
            Theme = ThemeMode.System;
        }

        return this;
    }
}

/// <summary>The main window's last bounds, maximised state and page.</summary>
public sealed class WindowPlacement
{
    public const int Unset = int.MinValue;

    public int Left { get; set; } = Unset;
    public int Top { get; set; } = Unset;
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public string LastPage { get; set; } = "";

    [JsonIgnore]
    public bool HasBounds => Left != Unset && Top != Unset && Width > 0 && Height > 0;

    internal void Normalize()
    {
        LastPage ??= "";
        if (Width is < 0 or > 32768 || Height is < 0 or > 32768)
        {
            Width = 0;
            Height = 0;
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
