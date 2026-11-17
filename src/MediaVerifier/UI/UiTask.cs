namespace MediaVerifier.UI;

/// <summary>
/// Starts a task from the UI thread without waiting for it: a button, a menu item, a toast action. A failure is
/// not lost in an unobserved task: it is raised on the UI thread, where the app writes it to the error log and
/// tells the user once per session (subplan 01). A cancelled task is not a failure. UI thread only.
/// </summary>
internal static class UiTask
{
    public static async void Run(Task task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled on purpose: the page was left, or a newer request replaced this one.
        }
    }
}
