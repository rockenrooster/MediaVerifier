namespace MediaVerifier.Core;

/// <summary>Time source. Production uses <see cref="SystemClock"/>; tests inject a fake (subplan 15).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic milliseconds for measuring intervals.</summary>
    long TickCount64 { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long TickCount64 => Environment.TickCount64;
}
