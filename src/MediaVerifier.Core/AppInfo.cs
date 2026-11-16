using System.Reflection;

namespace MediaVerifier.Core;

/// <summary>Product identity shared by the app, the engine and the tests.</summary>
public static class AppInfo
{
    public const string ProductName = "MediaVerifier";

    /// <summary>The four-part version from Directory.Build.props; the exe and Core always agree.</summary>
    public static Version Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public static string VersionText { get; } = Version.ToString(4);

    /// <summary>Window title, also used to find the running instance's window.</summary>
    public static string WindowTitle { get; } = $"{ProductName} {VersionText}";

    /// <summary>Informational, for logs: version plus the build's file version.</summary>
    public static string FileVersion { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? VersionText;
}
