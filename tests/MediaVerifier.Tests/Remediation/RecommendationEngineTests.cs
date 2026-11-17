using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Remediation;

/// <summary>Recommendations from synthetic libraries (subplan 12, task R-11).</summary>
public sealed class RecommendationEngineTests
{
    [Fact]
    public async Task Findings_become_grouped_advice_ordered_by_severity_then_count()
    {
        using TestDatabase db = new();
        long music = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        long videos = await db.Database.Roots.AddAsync(new RootRecord { Path = @"E:\Videos", Label = "Videos" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(videos, "a", "1.mp4", "mp4", MediaKind.Video, 1000, 1, null, 1),
            new(videos, "a", "2.mp4", "mp4", MediaKind.Video, 1000, 1, null, 1),
            new(videos, "a", "3.mp4", "mp4", MediaKind.Video, 1000, 1, null, 1),
            new(music, "x", "1.jpg", "png", MediaKind.Photo, 10, 1, null, 1),
            new(music, "album1", "1.flac", "flac", MediaKind.Audio, 30, 1, null, 1),
            new(music, "album1", "2.flac", "flac", MediaKind.Audio, 30, 1, null, 1),
            new(music, "album2", "1.flac", "flac", MediaKind.Audio, 30, 1, null, 1),
            new(music, "x", "rot.flac", "flac", MediaKind.Audio, 30, 1, null, 1),
        ]);
        string[] rules = [.. RuleCatalog.All.Select(r => r.Id)];
        for (int i = 0; i < 3; i++)
        {
            await db.Database.Findings.ReplaceForFileAsync(files[i].FileId, 1, rules, [new Finding("COR-INDEX", Severity.Medium, 1f, "no index", Finding.NoEvidence)], false);
        }

        await db.Database.Findings.ReplaceForFileAsync(files[3].FileId, 1, rules, [new Finding("GEN-EXT-MISMATCH", Severity.Low, 1f, "JPEG named .png", Finding.NoEvidence)], false);
        for (int i = 4; i < 7; i++)
        {
            await db.Database.Findings.ReplaceForFileAsync(files[i].FileId, 1, rules, [new Finding("AUD-FAKE-LOSSLESS", Severity.High, 0.95f, "cutoff", Finding.NoEvidence)], false);
        }

        await db.Database.Findings.ReplaceForFileAsync(files[7].FileId, 1, rules, [new Finding("INT-ROT", Severity.Critical, 1f, "changed", Finding.NoEvidence)], false);

        IReadOnlyList<Recommendation> advice = RecommendationEngine.Build(db.Database, new ScanSettings(), WindowsFileSystem.Instance, DateTimeOffset.UtcNow, max: 10);
        Assert.Equal(
            [RecommendationKind.RotWithoutCopies, RecommendationKind.FakeLossless, RecommendationKind.MissingIndex, RecommendationKind.NoBackupFolder, RecommendationKind.WrongExtension],
            advice.Select(a => a.Kind));
        Recommendation fake = advice.Single(a => a.Kind == RecommendationKind.FakeLossless);
        Assert.Equal(2, fake.FileCount);
        Assert.Equal(3, fake.FileIds.Count);
        Recommendation index = advice.Single(a => a.Kind == RecommendationKind.MissingIndex);
        Assert.Equal(ActionIds.Remux, index.ActionId);
        Assert.Equal(3, index.FileCount);
        Assert.Equal(ActionIds.FixExtension, advice.Single(a => a.Kind == RecommendationKind.WrongExtension).ActionId);
        Assert.Equal(5, RecommendationEngine.Build(db.Database, new ScanSettings(), WindowsFileSystem.Instance, DateTimeOffset.UtcNow).Count);

        // A backup folder takes the "no backup" advice away; resolved findings take theirs away.
        await db.Database.Roots.AddAsync(new RootRecord { Path = @"F:\Backup", Label = "Backup", Role = RootRole.Backup });
        await db.Database.Findings.ReplaceForFileAsync(files[3].FileId, 2, rules, [], false);
        IReadOnlyList<Recommendation> later = RecommendationEngine.Build(db.Database, new ScanSettings(), WindowsFileSystem.Instance, DateTimeOffset.UtcNow, max: 10);
        Assert.DoesNotContain(later, a => a.Kind is RecommendationKind.NoBackupFolder or RecommendationKind.WrongExtension);
    }

    [Fact]
    public async Task Library_state_gives_schedule_and_interrupted_scan_advice()
    {
        using TestDatabase db = new();
        await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        RunCounters counters = new(10, 10, 0, 1000, 0, 0, 0);
        long run = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Quick, "[]", null);
        await db.Database.Runs.EndAsync(run, RunStatus.Completed, counters, null, null);
        Assert.Contains(RecommendationEngine.Build(db.Database, new ScanSettings(), WindowsFileSystem.Instance, DateTimeOffset.UtcNow), a => a.Kind == RecommendationKind.NoSchedule);

        await db.Database.Plans.AddAsync(new PlanRecord { Name = "Weekly check", DefinitionJson = PlanDefinition.Templates[1].Definition.ToJson() });
        long interrupted = await db.Database.Runs.BeginAsync(RunTrigger.Manual, ScanDepth.Quick, "[]", null);
        await db.Database.Runs.EndAsync(interrupted, RunStatus.Interrupted, counters with { FilesAnalysed = 4 }, null, null);
        IReadOnlyList<Recommendation> advice = RecommendationEngine.Build(db.Database, new ScanSettings(), WindowsFileSystem.Instance, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(advice, a => a.Kind == RecommendationKind.NoSchedule);
        Assert.Contains(advice, a => a.Kind == RecommendationKind.InterruptedScan);
    }
}
