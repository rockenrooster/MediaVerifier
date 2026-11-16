using MediaVerifier.Core.Logging;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Engine;

/// <summary>
/// Runs a scan request without any UI (task E-13) and turns the outcome into an exit code: 0 success,
/// 1 failure (3, forwarded to a running window, is decided by the launcher). Used by scheduled plans
/// (subplan 13) and integration tests.
/// </summary>
public static class HeadlessScan
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitForwarded = 3;

    public static async Task<(int ExitCode, RunSummary? Summary)> RunAsync(ScanEngine engine, ScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        TaskCompletionSource<RunSummary> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(RunSummary summary) => completed.TrySetResult(summary);
        engine.RunCompleted += OnCompleted;
        try
        {
            using CancellationTokenRegistration cancel = cancellationToken.Register(engine.Cancel);
            try
            {
                await engine.StartAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                AppLog.Shared?.Warning("Headless scan not started: " + ex.Message);
                return (ExitFailure, null);
            }

            RunSummary summary = await completed.Task.ConfigureAwait(false);
            int code = summary.Status is RunStatus.Completed or RunStatus.BudgetReached ? ExitSuccess : ExitFailure;
            return (code, summary);
        }
        finally
        {
            engine.RunCompleted -= OnCompleted;
        }
    }
}
