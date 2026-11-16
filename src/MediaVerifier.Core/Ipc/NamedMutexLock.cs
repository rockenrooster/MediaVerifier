namespace MediaVerifier.Core.Ipc;

/// <summary>
/// Holds a named mutex on a dedicated thread. A Windows mutex must be released by the thread that
/// acquired it; keeping it on its own thread lets any thread take or release the lock (the engine
/// starts and stops on pool threads) while the OS still frees it if the process dies.
/// </summary>
public sealed class NamedMutexLock : IDisposable
{
    public const string SingleInstanceName = @"Local\MediaVerifier.SingleInstance";
    public const string EngineName = @"Local\MediaVerifier.Engine";

    private readonly ManualResetEventSlim _release = new(false);
    private readonly Thread _thread;
    private int _disposed;

    private NamedMutexLock(string name, Thread thread)
    {
        Name = name;
        _thread = thread;
    }

    public string Name { get; }

    /// <summary>True when the previous owner died without releasing it.</summary>
    public bool WasAbandoned { get; private set; }

    /// <summary>
    /// Tries to take the mutex within <paramref name="timeout"/>. Returns null when another process
    /// holds it. An abandoned mutex (its owner crashed) counts as acquired.
    /// </summary>
    public static NamedMutexLock? TryAcquire(string name, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        TaskCompletionSource decided = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool acquired = false;
        bool abandoned = false;
        Exception? failure = null;
        NamedMutexLock? result = null;

        Thread thread = new(() =>
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: false, name);
                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                    abandoned = true;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                failure = ex;
            }

            decided.SetResult();
            if (!acquired || mutex is null)
            {
                mutex?.Dispose();
                return;
            }

            // Wait until the owner of the lock object asks for release, then release on this thread.
            NamedMutexLock owner = Volatile.Read(ref result)!;
            owner._release.Wait();
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            mutex.Dispose();
        })
        {
            IsBackground = true,
            Name = "Mutex " + name,
        };

        result = new NamedMutexLock(name, thread);
        thread.Start();
        decided.Task.Wait();
        if (failure is not null || !acquired)
        {
            result._release.Dispose();
            return null;
        }

        result.WasAbandoned = abandoned;
        return result;
    }

    /// <summary>True when some process (this one included) currently holds the mutex.</summary>
    public static bool IsHeldElsewhere(string name)
    {
        using NamedMutexLock? probe = TryAcquire(name, TimeSpan.Zero);
        return probe is null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _release.Dispose();
    }
}
