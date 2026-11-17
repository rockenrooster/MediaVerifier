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

            // As in the app's debug build: a database read on this (UI) thread throws, so a page that waits on the
            // database fails its test instead of freezing the window for the owner.
            int self = Environment.CurrentManagedThreadId;
            MediaVerifier.Core.Storage.Database.UiThreadId = self;
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
                if (MediaVerifier.Core.Storage.Database.UiThreadId == self)
                {
                    MediaVerifier.Core.Storage.Database.UiThreadId = 0;
                }

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
