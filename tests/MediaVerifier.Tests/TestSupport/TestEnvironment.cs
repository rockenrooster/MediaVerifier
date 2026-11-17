using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Set before any test runs: tests never write to the user's own error log, because the static error log points to a
/// folder in the temporary directory.
/// </summary>
internal static class TestEnvironment
{
    public static string ErrorLogDirectory { get; } = Path.Combine(Path.GetTempPath(), "MediaVerifier.Tests", "logs");

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "The test assembly is the only user; this must happen before any test runs.")]
    internal static void Initialize()
        => ErrorLog.Configure(ErrorLogDirectory);
}
