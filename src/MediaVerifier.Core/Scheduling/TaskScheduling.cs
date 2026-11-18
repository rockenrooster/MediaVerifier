using System.Globalization;
using System.Runtime.InteropServices;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Scheduling;

/// <summary>Per-user scheduled tasks (subplan 13, task SCH-04); tests use a fake, never the real scheduler.</summary>
public interface IScheduleBackend
{
    /// <summary>Creates or updates a task; returns the name it was registered under.</summary>
    string Register(long planId, string xml);

    void Delete(string taskName);

    /// <summary>The names of every MediaVerifier task.</summary>
    IReadOnlyList<string> List();
}

/// <summary>
/// The Task Scheduler through its COM automation object <c>Schedule.Service</c>, late bound (no extra
/// package). Tasks live in <c>\MediaVerifier</c>; when creating that folder is refused they go to the root
/// folder as <c>MediaVerifier Plan {id}</c>. Never call it on the UI thread.
/// </summary>
public sealed class TaskSchedulerBackend : IScheduleBackend
{
    private const string FolderName = "MediaVerifier";
    private const int CreateOrUpdate = 6;
    private const int InteractiveToken = 3;

    public string Register(long planId, string xml)
    {
        dynamic service = Connect();
        try
        {
            dynamic folder;
            string name;
            try
            {
                folder = Folder(service, create: true);
                name = TaskName(planId);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // Subplan 13: when creating the folder is refused, the task goes to the root folder. The late binder
                // reports a refusal (E_ACCESSDENIED) as UnauthorizedAccessException, which skipped this fallback.
                folder = service.GetFolder("\\");
                name = "MediaVerifier Plan " + planId.ToString(CultureInfo.InvariantCulture);
            }

            folder.RegisterTask(name, xml, CreateOrUpdate, null, null, InteractiveToken);
            return folder.Path == "\\" ? name : FolderName + "\\" + name;
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    public void Delete(string taskName)
    {
        dynamic service = Connect();
        try
        {
            int slash = taskName.LastIndexOf('\\');
            dynamic folder = service.GetFolder(slash < 0 ? "\\" : "\\" + taskName[..slash]);
            folder.DeleteTask(slash < 0 ? taskName : taskName[(slash + 1)..], 0);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException)
        {
            // Already gone.
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    public IReadOnlyList<string> List()
    {
        dynamic service = Connect();
        List<string> names = [];
        try
        {
            try
            {
                dynamic folder = service.GetFolder("\\" + FolderName);
                foreach (dynamic task in folder.GetTasks(1))
                {
                    names.Add(FolderName + "\\" + (string)task.Name);
                }
            }
            catch (Exception ex) when (ex is COMException or FileNotFoundException)
            {
                // No folder yet (the late-bound call reports it as FileNotFoundException).
            }

            dynamic root = service.GetFolder("\\");
            foreach (dynamic task in root.GetTasks(1))
            {
                string name = task.Name;
                if (name.StartsWith("MediaVerifier Plan ", StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }

        return names;
    }

    public static string TaskName(long planId) => "Plan " + planId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Deletes the <c>\MediaVerifier</c> folder once it holds no task or subfolder, so that uninstalling (which runs
    /// <c>--cleanup</c>) leaves nothing behind in the Task Scheduler.
    /// </summary>
    public static void RemoveFolderIfEmpty()
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\" + FolderName);
            if ((int)folder.GetTasks(1).Count == 0 && (int)folder.GetFolders(0).Count == 0)
            {
                dynamic root = service.GetFolder("\\");
                root.DeleteFolder(FolderName, 0);
            }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException)
        {
            // No folder, or it cannot be deleted: nothing more to clean up.
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    private static dynamic Connect()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic Folder(dynamic service, bool create)
    {
        try
        {
            return service.GetFolder("\\" + FolderName);
        }
        catch (Exception ex) when (create && ex is COMException or FileNotFoundException)
        {
            // No folder yet: the late-bound call reports a missing folder as FileNotFoundException.
            dynamic root = service.GetFolder("\\");
            return root.CreateFolder(FolderName, null);
        }
    }
}

/// <summary>
/// Makes the scheduled tasks match the plans (subplan 13, Reconciliation): every enabled plan has its task with
/// the current XML and executable path; tasks of removed or disabled plans are deleted.
/// </summary>
public static class PlanReconciler
{
    /// <summary>
    /// One reconciliation at a time: each deletes every task outside its own snapshot of the plans, so two at once
    /// (a switch turned off and on again quickly) could delete the task the other had just registered.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// The first day of a plan's rhythm: the local day it was created. The task's start boundary and the next run the
    /// window shows both count from it; an every-n-days plan anchored at each reconcile (every app start) kept moving
    /// its next run forward and, started daily, never fired. <paramref name="fallback"/> serves a row without a date.
    /// </summary>
    public static DateOnly AnchorOf(PlanRecord plan, DateOnly fallback)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.CreatedUtc > new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks && plan.CreatedUtc <= DateTime.MaxValue.Ticks
            ? DateOnly.FromDateTime(new DateTime(plan.CreatedUtc, DateTimeKind.Utc).ToLocalTime())
            : fallback;
    }

    public static async Task<int> ReconcileAsync(Database database, IScheduleBackend backend, string exePath, DateOnly today, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(backend);
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            List<PlanRecord> plans = await Task.Run(database.Plans.List, token).ConfigureAwait(false);
            HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);
            int changed = 0;
            foreach (PlanRecord plan in plans.Where(p => p.Enabled))
            {
                if (PlanDefinition.FromJson(plan.DefinitionJson) is not PlanDefinition definition || definition.Validate().Count > 0)
                {
                    continue;
                }

                string name = backend.Register(plan.Id, ScheduleXmlBuilder.Build(plan.Name, definition, plan.Id, exePath, AnchorOf(plan, today)));
                wanted.Add(name);
                if (plan.TaskName != name)
                {
                    // Written onto the row as it is now, not the snapshot: registering takes a while, and an edit the
                    // user saved meanwhile was overwritten with the snapshot's name, switch and definition.
                    if (await Task.Run(() => database.Plans.Get(plan.Id), token).ConfigureAwait(false) is PlanRecord current)
                    {
                        await database.Plans.UpdateAsync(current with { TaskName = name }, token).ConfigureAwait(false);
                    }
                }

                changed++;
            }

            foreach (string stale in backend.List().Where(n => !wanted.Contains(n)))
            {
                backend.Delete(stale);
                changed++;
            }

            return changed;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary><c>--cleanup</c>: removes every MediaVerifier task.</summary>
    public static void RemoveAll(IScheduleBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        foreach (string name in backend.List())
        {
            backend.Delete(name);
        }
    }
}

/// <summary>
/// One notification decided for a run (subplan 13, Notification policy). <paramref name="Silent"/> posts the toast
/// straight to the notification centre without a pop-up (quiet hours).
/// </summary>
public sealed record RunNotification(Severity Level, string Title, string Body, bool Toast, bool Silent = false);

/// <summary>Who to tell what after a run (subplan 13, task N-01); pure.</summary>
public static class NotificationPolicy
{
    /// <summary>
    /// Zero or one notification. <paramref name="offlineNotified"/> says when each root last had an offline toast;
    /// <paramref name="quietHours"/> posts silently.
    /// </summary>
    public static RunNotification? Decide(RunSummary run, PlanNotify notify, IReadOnlyDictionary<long, DateTimeOffset> offlineNotified, DateTimeOffset now, bool quietHours, string finishedText)
    {
        ArgumentNullException.ThrowIfNull(run);
        RunNotification? decided = DecideLoud(run, notify, offlineNotified, now, finishedText);
        return quietHours && decided is { Toast: true } ? decided with { Silent = true } : decided;
    }

    /// <summary>
    /// True when <paramref name="now"/> falls in the quiet hours from <paramref name="from"/> to <paramref name="to"/>
    /// ("HH:mm", local); a span that passes midnight wraps. Unreadable times mean no quiet hours.
    /// </summary>
    public static bool InQuietHours(bool enabled, string? from, string? to, TimeOnly now)
    {
        if (!enabled || !TryParseTime(from, out TimeOnly start) || !TryParseTime(to, out TimeOnly end) || start == end)
        {
            return false;
        }

        return start < end ? now >= start && now < end : now >= start || now < end;
    }

    /// <summary>Reads a 24-hour "H:mm" or "HH:mm" time.</summary>
    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>The stricter of a plan's minimum tier and the global <c>Notify about</c> setting.</summary>
    public static PlanNotify WithThreshold(PlanNotify notify, Settings.NotifyThreshold threshold)
    {
        ArgumentNullException.ThrowIfNull(notify);
        Severity global = threshold switch
        {
            Settings.NotifyThreshold.CriticalOnly => Severity.Critical,
            Settings.NotifyThreshold.HighAndAbove => Severity.High,
            Settings.NotifyThreshold.MediumAndAbove => Severity.Medium,
            _ => Severity.Low,
        };
        return global > notify.MinSeverity ? notify with { MinSeverity = global } : notify;
    }

    private static RunNotification? DecideLoud(RunSummary run, PlanNotify notify, IReadOnlyDictionary<long, DateTimeOffset> offlineNotified, DateTimeOffset now, string finishedText)
    {
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(offlineNotified);
        bool toast = notify.When != NotifyWhen.Never;
        if (run.Status == RunStatus.Failed)
        {
            return new RunNotification(Severity.High, CoreStrings.Notify.CouldNotRun, run.Error ?? string.Empty, toast);
        }

        SeverityCounts n = run.NewFindings;
        int atOrAbove = notify.MinSeverity switch
        {
            Severity.Critical => n.Critical,
            Severity.High => n.Critical + n.High,
            Severity.Medium => n.Critical + n.High + n.Medium,
            _ => n.Critical + n.High + n.Medium + n.Low,
        };
        if (n.Critical > 0 || atOrAbove > 0)
        {
            string title = string.Format(CultureInfo.InvariantCulture, atOrAbove == 1 ? CoreStrings.Notify.NewProblem : CoreStrings.Notify.NewProblems, Math.Max(atOrAbove, n.Critical));
            return new RunNotification(n.Critical > 0 ? Severity.Critical : n.High > 0 ? Severity.High : Severity.Medium, title, Counts(n) + " " + finishedText, toast);
        }

        RunAlert? alert = run.Alerts.FirstOrDefault(a => a.Id is "RUN-READ-ERRORS" or "RUN-MASS-MISSING");
        if (alert is not null)
        {
            return new RunNotification(Severity.High, alert.Detail, finishedText, toast);
        }

        RunAlert? offline = run.Alerts.FirstOrDefault(a => a.Id == "RUN-ROOT-OFFLINE");
        if (offline is not null)
        {
            bool recent = offline.RootId is long root && offlineNotified.TryGetValue(root, out DateTimeOffset last) && now - last < TimeSpan.FromDays(7);
            return new RunNotification(Severity.Medium, offline.Detail, finishedText, toast && !recent);
        }

        return notify.When == NotifyWhen.Always
            ? new RunNotification(Severity.None, CoreStrings.Notify.NothingNew, finishedText, toast)
            : null;
    }

    private static string Counts(SeverityCounts n)
    {
        List<string> parts = [];
        if (n.Critical > 0)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, CoreStrings.Notify.CriticalCount, n.Critical));
        }

        if (n.High > 0)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, CoreStrings.Notify.HighCount, n.High));
        }

        if (n.Medium > 0)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, CoreStrings.Notify.MediumCount, n.Medium));
        }

        if (n.Low > 0)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, CoreStrings.Notify.LowCount, n.Low));
        }

        return string.Join(", ", parts) + ".";
    }
}
