using MediaVerifier.Core;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>A database in a temp folder, with a controllable clock.</summary>
public sealed class TestDatabase : IDisposable
{
    public TestDatabase(string? prefix = null)
    {
        Folder = new TempDirectory(prefix ?? "db");
        Clock = new FakeClock(new DateTimeOffset(2026, 11, 16, 14, 20, 0, TimeSpan.Zero));
        FilePath = Folder.Combine("library.db");
        Database = Database.Open(FilePath, Clock);
    }

    public TempDirectory Folder { get; }

    public FakeClock Clock { get; }

    public string FilePath { get; }

    public Database Database { get; private set; }

    public Database Reopen()
    {
        Database.Dispose();
        Database = Database.Open(FilePath, Clock);
        return Database;
    }

    public void Dispose()
    {
        Database.Dispose();
        Folder.Dispose();
    }
}

/// <summary>A clock tests move by hand (subplan 15: time is injected, no test sleeps).</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private long _tick;

    public DateTimeOffset UtcNow { get; set; } = start;

    public long TickCount64 => Interlocked.Read(ref _tick);

    public void Advance(TimeSpan by)
    {
        UtcNow += by;
        Interlocked.Add(ref _tick, (long)by.TotalMilliseconds);
    }
}
