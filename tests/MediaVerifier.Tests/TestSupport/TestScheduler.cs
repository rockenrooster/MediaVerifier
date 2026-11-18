using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MediaVerifier.Core.Scheduling;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// The Task Scheduler every test reaches through the app (<c>PlanHost.Scheduler</c>): in memory, set before any test
/// runs. Nothing a test does (a plan saved on the Schedule page, a plan run that removes its orphan task) registers or
/// deletes a task in Windows. Tests share it, so they look only at the tasks of their own plans.
/// </summary>
internal static class TestScheduler
{
    public static InMemoryScheduler Backend { get; } = new();

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "The test assembly is the only user; this must happen before any test runs.")]
    internal static void Initialize() => MediaVerifier.App.PlanHost.Scheduler = () => Backend;
}

/// <summary>Task names and their XML, as the real backend would register them.</summary>
internal sealed class InMemoryScheduler : IScheduleBackend
{
    public ConcurrentDictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Register(long planId, string xml)
    {
        string name = "MediaVerifier\\" + TaskSchedulerBackend.TaskName(planId);
        Tasks[name] = xml;
        return name;
    }

    public void Delete(string taskName) => Tasks.TryRemove(taskName, out _);

    public IReadOnlyList<string> List() => [.. Tasks.Keys];
}
