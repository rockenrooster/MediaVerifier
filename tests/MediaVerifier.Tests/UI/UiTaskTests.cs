using System.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// A UI action started without waiting (a button's async handler) reports its failure to the UI thread's error
/// handler, which the app logs and shows, instead of losing it in an unobserved task; a cancelled one reports nothing.
/// </summary>
public sealed class UiTaskTests
{
    [Fact]
    public void A_failed_action_reaches_the_ui_error_handler_and_a_cancelled_one_does_not()
    {
        List<Exception> raised = [];
        Sta.Run(() =>
        {
            // Replaces the handler Sta.Run installs on this thread: this test expects the failure.
            Application.ThreadException += (_, e) => raised.Add(e.Exception);
            UiTask.Run(Task.FromCanceled(new CancellationToken(canceled: true)));
            UiTask.Run(FailLaterAsync());
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(1) || (raised.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(10)))
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        });

        Exception failure = Assert.Single(raised);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("The action failed.", failure.Message);
    }

    private static async Task FailLaterAsync()
    {
        await Task.Delay(10).ConfigureAwait(true);
        throw new InvalidOperationException("The action failed.");
    }
}
