using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Set before any test runs: tests never write to the user's own error log, because the static error log points to a
/// folder in the temporary directory; and every window a test opens is off-screen and never activated, so it cannot
/// take the focus from the window the user is working in (test runs made the screen flicker).
/// </summary>
internal static class TestEnvironment
{
    public static string ErrorLogDirectory { get; } = Path.Combine(Path.GetTempPath(), "MediaVerifier.Tests", "logs");

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "The test assembly is the only user; this must happen before any test runs.")]
    internal static void Initialize()
    {
        ErrorLog.Configure(ErrorLogDirectory);
        MediaVerifier.Diagnostics.ScreenRenderer.Offscreen = true;

        // A scan that ends in a test window notifies as the app's own scans do: never on the user's desktop.
        MediaVerifier.App.ToastInterop.AppUserModelId = "MediaVerifier.Tests.NeverRegistered";
    }
}
