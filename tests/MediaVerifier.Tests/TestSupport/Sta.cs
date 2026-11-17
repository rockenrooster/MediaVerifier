using System.Runtime.ExceptionServices;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Runs a whole-window test on its own STA thread with a WinForms synchronization context, like the app's UI thread.
/// xUnit's threads are MTA, where OLE drag-and-drop registration (the Library page accepts dropped folders) and UI
/// Automation fail inside window creation and take the test host down. An exception that reaches the thread's message
/// loop (a failed UI action, see <c>UiTask</c>) fails the test after the body, instead of opening the Windows Forms
/// error dialog that nobody closes.
/// </summary>
public static class Sta
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        ExceptionDispatchInfo? uiFailure = null;
        Thread thread = new(() =>
        {
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.ThreadException += (_, e) => uiFailure ??= ExceptionDispatchInfo.Capture(e.Exception);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                WindowsFormsSynchronizationContext.AutoInstall = true;
            }
        })
        {
            IsBackground = true,
            Name = "STA test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(5)), "the STA test did not finish in 5 minutes");
        failure?.Throw();
        uiFailure?.Throw();
    }
}
