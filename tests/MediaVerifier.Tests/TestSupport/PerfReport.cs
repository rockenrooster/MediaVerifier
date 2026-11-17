using System.Globalization;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Collects performance measurements for scripts/Measure-Performance.ps1 (task T-11): when MEDIAVERIFIER_PERF_REPORT
/// names a file, each measurement is appended to it as a Markdown table row. Without it nothing is written.
/// </summary>
public static class PerfReport
{
    private static readonly Lock Gate = new();

    public static void Add(string measurement, string setup, string result, string budget)
    {
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_PERF_REPORT") is not { Length: > 0 } path)
        {
            return;
        }

        string row = string.Create(CultureInfo.InvariantCulture, $"| {measurement} | {setup} | {result} | {budget} |{Environment.NewLine}");
        lock (Gate)
        {
            File.AppendAllText(path, row);
        }
    }
}
