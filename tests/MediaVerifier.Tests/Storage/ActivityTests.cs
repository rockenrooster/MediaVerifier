using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;

namespace MediaVerifier.Tests.Storage;

/// <summary>
/// The Dashboard's Recent activity lists what subplan 09 names, newest first and four at most: folders added and
/// removed, restores and repairs (not undo records or a quarantine), updates and scans. It listed scans only.
/// </summary>
public sealed class ActivityTests
{
    [Fact]
    public async Task Recent_activity_lists_folders_restores_repairs_and_updates_newest_first()
    {
        using TestDatabase db = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long Ago(int hours) => now.AddHours(-hours).UtcTicks;
        await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music", AddedUtc = now.AddHours(-9) });
        await db.Database.Journal.AddAsync(new OperationRecord { BatchId = 1, Utc = Ago(8), ActionId = ActionIds.RestoreCopy, Status = OperationStatus.Done, SourcePath = "a" });
        await db.Database.Journal.AddAsync(new OperationRecord { BatchId = 1, Utc = Ago(8), ActionId = ActionIds.RestoreCopy, Status = OperationStatus.Done, SourcePath = "b" });
        await db.Database.Journal.AddAsync(new OperationRecord { BatchId = 2, Utc = Ago(6), ActionId = ActionIds.Remux, Status = OperationStatus.Done, SourcePath = "c" });
        await db.Database.Journal.AddAsync(new OperationRecord { BatchId = 3, Utc = Ago(5), ActionId = ActionIds.Quarantine, Status = OperationStatus.Done, SourcePath = "d" });
        await db.Database.Journal.AddAsync(new OperationRecord { BatchId = 4, Utc = Ago(4), ActionId = ActionIds.Remux, Status = OperationStatus.Done, SourcePath = "c", DetailJson = "undo:3" });
        await db.Database.Meta.SetAsync(LiveUiData.RemovedFolderKey, $"{Ago(3)}|Old Photos");
        await db.Database.Meta.SetAsync(UiServices.VersionKey, "0.0.0.1");
        await UiServices.RecordUpdateAsync(db.Database);

        LiveUiData data = new(db.Database, () => new ScanSettings(), new FakeClock(now));
        IReadOnlyList<ActivityItem> activity = data.LoadDashboard().Activity;

        Assert.Equal(
            [
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Activity.Updated, AppInfo.VersionText),
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Activity.FolderRemoved, "Old Photos"),
                Strings.Activity.RepairedOne,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Activity.Restored, "2"),
            ],
            activity.Select(a => a.Text).ToArray());

        // The first start of all records the version without claiming an update.
        using TestDatabase fresh = new();
        await UiServices.RecordUpdateAsync(fresh.Database);
        Assert.Null(fresh.Database.Meta.Get(LiveUiData.UpdatedKey));
        Assert.Equal(AppInfo.VersionText, fresh.Database.Meta.Get(UiServices.VersionKey));
    }
}
