using MediaVerifier.Core.Scheduling;

namespace MediaVerifier.Tests.Scheduling;

/// <summary>
/// The real Task Scheduler, read-only (tests never register tasks). Before any schedule exists there is no
/// <c>\MediaVerifier</c> folder, and the late-bound COM call reports that as a FileNotFoundException, not a
/// COMException: listing failed and start-up reconciliation logged "Start-up maintenance failed" on every launch.
/// </summary>
[Trait("Category", "System")]
public sealed class TaskSchedulerBackendTests
{
    [Fact]
    public void Listing_works_whether_or_not_the_folder_exists()
    {
        IReadOnlyList<string> names = new TaskSchedulerBackend().List();
        Assert.All(names, name => Assert.True(name.StartsWith("MediaVerifier", StringComparison.Ordinal), name));
    }
}
