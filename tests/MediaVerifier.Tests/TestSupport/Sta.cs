using System.Runtime.ExceptionServices;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// Runs a whole-window test on its own STA thread with a WinForms synchronization context, like the app's UI thread.
/// xUnit's threads are MTA, where OLE drag-and-drop registration (the Library page accepts dropped folders) and UI
/// Automation fail inside window creation and take the test host down.
/// </summary>
public static class Sta
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
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
    }
}
