using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace MediaVerifier.Diagnostics;

/// <summary>
/// Task H-02 (subplan 16, Performance pass): notices when the UI thread is blocked for more than 50 ms. A background
/// thread posts a heartbeat to the UI thread every 100 ms and reports how long it waited, with the most recent UI
/// activity marks (page shown, data applied). On in Debug builds, and in any build with MEDIAVERIFIER_UI_WATCHDOG=1.
/// The stack of the blocked thread cannot be captured from inside the process, so the marks say where it was.
/// </summary>
internal sealed class UiWatchdog : IDisposable
{
    public static readonly TimeSpan StallLimit = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentQueue<string> s_marks = new();
    private static volatile bool s_marking;
    private readonly SynchronizationContext _ui;
    private readonly Action<TimeSpan, string> _onStall;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();

    public UiWatchdog(SynchronizationContext ui, Action<TimeSpan, string> onStall)
    {
        _ui = ui;
        _onStall = onStall;
        s_marking = true;
        _thread = new Thread(Watch) { IsBackground = true, Name = "UI watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>True in Debug builds, or when MEDIAVERIFIER_UI_WATCHDOG is 1.</summary>
    public static bool Enabled =>
#if DEBUG
        true;
#else
        Environment.GetEnvironmentVariable("MEDIAVERIFIER_UI_WATCHDOG") == "1";
#endif

    /// <summary>The longest wait seen so far (tests read it).</summary>
    public TimeSpan Longest { get; private set; }

    /// <summary>Records what the UI thread is doing; free when no watchdog runs.</summary>
    public static void Mark(string what)
    {
        if (!s_marking)
        {
            return;
        }

        s_marks.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + what);
        while (s_marks.Count > 8 && s_marks.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
        s_marking = false;
    }

    private void Watch()
    {
        using ManualResetEventSlim answered = new();
        while (!_stop.Wait(Interval))
        {
            answered.Reset();
            long posted = Stopwatch.GetTimestamp();
            _ui.Post(
                _ =>
                {
                    TimeSpan waited = Stopwatch.GetElapsedTime(posted);
                    if (waited > Longest)
                    {
                        Longest = waited;
                    }

                    if (waited > StallLimit)
                    {
                        _onStall(waited, string.Join("; ", s_marks));
                    }

                    answered.Set();
                },
                null);

            // Wait for the heartbeat before posting the next, so one long block is reported once.
            while (!answered.Wait(Interval) && !_stop.IsSet)
            {
            }
        }
    }
}
