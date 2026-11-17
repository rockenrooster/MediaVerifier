using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MediaVerifier.Core.Logging;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Tests never write to the user's own error log: before any test runs, the static error log points to a folder
/// in the temporary directory.
/// </summary>
internal static class TestLogs
{
    public static string Directory { get; } = Path.Combine(Path.GetTempPath(), "MediaVerifier.Tests", "logs");

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "The test assembly is the only user; it must redirect the log before any test runs.")]
    internal static void Initialize() => ErrorLog.Configure(Directory);
}
