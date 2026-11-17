using System.Diagnostics;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Storage;

public sealed class QueryTests
{
    [Fact]
    public async Task FiltersSortsAndFetchesRowsInSnapshotOrder()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Media" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, @"Music\Album", "b song.flac", "flac", MediaKind.Audio, 300, 1, null, 1),
            new(root, @"Music\Album", "a song.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, @"Video", "film.mkv", "mkv", MediaKind.Video, 900, 1, null, 1),
            new(root, @"Musical", "other.mp3", "mp3", MediaKind.Audio, 50, 1, null, 1),
        ]);
        foreach (FileUpsertResult file in files)
        {
            await db.Database.Files.ApplyUpdateAsync(new FileUpdate(file.FileId) { State = FileState.Ok });
        }

        HashSet<string> all = ["COR-DECODE", "AUD-CLIPPING", "COR-INDEX"];
        await db.Database.Findings.ReplaceForFileAsync(files[0].FileId, 1, all, [Raise("COR-DECODE", Severity.Critical)], false);
        await db.Database.Findings.ReplaceForFileAsync(files[1].FileId, 1, all, [Raise("AUD-CLIPPING", Severity.Medium)], false);
        await db.Database.Findings.ReplaceForFileAsync(files[2].FileId, 1, all, [Raise("COR-INDEX", Severity.Medium)], false);
        await db.Database.Findings.ReplaceForFileAsync(files[3].FileId, 2, all, [Raise("AUD-CLIPPING", Severity.Low)], false);
        LibraryQueries q = db.Database.Queries;

        long[] bySeverity = q.FindingIds(FindingFilter.OpenAll);
        Assert.Equal(4, bySeverity.Length);
        Assert.Equal(Severity.Critical, q.FindingRows(bySeverity)[0].Severity);

        long[] byName = q.FindingIds(FindingFilter.OpenAll, FindingSort.Name);
        Assert.Equal(["a song.flac", "b song.flac", "film.mkv", "other.mp3"], q.FindingRows(byName).Select(r => r.FileName));

        Assert.Equal(2, q.FindingIds(new FindingFilter { Kinds = [MediaKind.Audio], Severities = [Severity.Critical, Severity.Medium] }).Length);
        Assert.Single(q.FindingIds(new FindingFilter { RuleIds = ["COR-INDEX"] }));
        Assert.Equal(2, q.FindingIds(new FindingFilter { Categories = [FindingCategory.Quality] }).Length);
        Assert.Single(q.FindingIds(new FindingFilter { FirstRun = 2 }));
        Assert.Single(q.FindingIds(new FindingFilter { Text = "FILM" }));
        Assert.Equal(2, q.FindingIds(new FindingFilter { Text = "album" }).Length);
        Assert.Empty(q.FindingIds(new FindingFilter { Text = "100%" }));

        // Folder subtree: Music covers Music\Album but not the sibling Musical.
        long musicDir = q.FolderRollup(root).Single().Children.Single(c => c.Name == "Music").Id;
        Assert.Equal(2, q.FindingIds(new FindingFilter { DirId = musicDir }).Length);

        SeverityCounts counts = q.SeverityCounts();
        Assert.Equal(new SeverityCounts(1, 0, 2, 1), counts);
        Assert.Equal(3, counts.MediumOrAbove);
        Assert.Equal(Severity.Critical, counts.Worst);
        Assert.Equal(2, q.CountsByRule()["AUD-CLIPPING"]);

        // Issues grouping: rows come out group by group, in the order of the group counts.
        List<FindingGroupCount> byRule = q.FindingGroupCounts(FindingFilter.OpenAll, FindingGroup.Rule);
        Assert.Equal(["AUD-CLIPPING", "COR-DECODE", "COR-INDEX"], byRule.Select(g => g.Key));
        Assert.Equal([2, 1, 1], byRule.Select(g => g.Count));
        long[] ruleOrder = q.FindingIds(FindingFilter.OpenAll, FindingSort.Severity, false, FindingGroup.Rule);
        Assert.Equal(["AUD-CLIPPING", "AUD-CLIPPING", "COR-DECODE", "COR-INDEX"], q.FindingRows(ruleOrder).Select(r => r.RuleId));
        List<FindingGroupCount> byFolder = q.FindingGroupCounts(FindingFilter.OpenAll, FindingGroup.Folder);
        Assert.Equal([@"D:\Media\Music\Album", @"D:\Media\Musical", @"D:\Media\Video"], byFolder.Select(g => g.Key));
        long[] folderOrder = q.FindingIds(FindingFilter.OpenAll, FindingSort.Severity, false, FindingGroup.Folder);
        Assert.Equal(["b song.flac", "a song.flac", "other.mp3", "film.mkv"], q.FindingRows(folderOrder).Select(r => r.FileName));
        List<FindingGroupCount> byTier = q.FindingGroupCounts(new FindingFilter { Kinds = [MediaKind.Audio] }, FindingGroup.Severity);
        Assert.Equal([Severity.Critical, Severity.Medium, Severity.Low], byTier.Select(g => g.Tier!.Value));

        List<FindingRow> fetched = q.FindingRows([bySeverity[3], bySeverity[0]]);
        Assert.Equal([bySeverity[3], bySeverity[0]], fetched.Select(r => r.Id));
        Assert.Equal(@"D:\Media", fetched[0].RootPath);
    }

    [Fact]
    public async Task FolderRollupSumsTheSubtreeAndCarriesTheWorstTier()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\M" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, @"A\B", "1.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
            new(root, @"A", "2.jpg", "jpg", MediaKind.Photo, 20, 1, null, 1),
            new(root, @"C", "3.jpg", "jpg", MediaKind.Photo, 40, 1, null, 1),
        ]);
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[0].FileId) { State = FileState.Ok });
        await db.Database.Findings.ReplaceForFileAsync(files[0].FileId, 1, ["COR-DECODE"], [Raise("COR-DECODE", Severity.High)], false);

        FolderNode top = Assert.Single(db.Database.Queries.FolderRollup(root));
        Assert.Equal(3, top.Files);
        Assert.Equal(70, top.Bytes);
        Assert.Equal(Severity.High, top.Worst);
        FolderNode a = top.Children.Single(c => c.Name == "A");
        Assert.Equal(2, a.Files);
        Assert.Equal(Severity.High, a.Worst);
        Assert.Equal(Severity.None, top.Children.Single(c => c.Name == "C").Worst);
    }

    [Fact]
    public async Task HealthAndKindSummaries()
    {
        using TestDatabase db = new();
        await SyntheticLibrary.GenerateAsync(db.Database, new SyntheticLibraryOptions { Files = 3_000, FindingRate = 0.1 });

        HealthSnapshot health = db.Database.Queries.Health(TimeSpan.FromDays(90), db.Clock.UtcNow);

        Assert.Equal(3_000, health.FilesIndexed);
        Assert.Equal(3_000, health.FilesScanned);
        Assert.InRange(health.HealthyPercent, 90, 100);
        Assert.InRange(health.ProtectedPercent, 30, 60);
        Assert.Equal(health.FilesWithBaseline - health.FilesVerifiedInPeriod, health.FilesOverdue);
        List<KindSummary> kinds = db.Database.Queries.KindSummaries();
        Assert.Equal(3_000, kinds.Sum(k => k.Files));
        Assert.Equal(health.Open.Total, kinds.Sum(k => k.Problems));
    }

    [Fact]
    public async Task HotFindingQueryUsesAnIndex()
    {
        using TestDatabase db = new();
        await SyntheticLibrary.GenerateAsync(db.Database, new SyntheticLibraryOptions { Files = 500 });
        string plan = db.Database.Queries.ExplainFindingIds(new FindingFilter { Severities = [Severity.High] });
        Assert.Contains("INDEX ix_findings_", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN fi", plan, StringComparison.Ordinal);
    }

    private static Finding Raise(string rule, Severity severity) => new(rule, severity, 1f, rule, Finding.NoEvidence);
}

[Trait("Category", "Perf")]
public sealed class QueryPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MillionFileLibraryMeetsTheQueryBudgets()
    {
        using TestDatabase db = new("perf");
        Stopwatch generate = Stopwatch.StartNew();
        await SyntheticLibrary.GenerateAsync(db.Database, new SyntheticLibraryOptions { Files = 1_000_000, Roots = 4 });
        generate.Stop();
        output.WriteLine($"generate 1,000,000 files: {generate.Elapsed.TotalSeconds:N1} s");
        PerfReport.Add("Generate a library", "1,000,000 files with findings", $"{generate.Elapsed.TotalSeconds:N1} s", "under 60 s (T-10)");
        Assert.True(generate.Elapsed < TimeSpan.FromSeconds(60), "generator budget (T-10)");
        LibraryQueries q = db.Database.Queries;

        _ = q.FindingIds(FindingFilter.OpenAll);
        Stopwatch snapshot = Stopwatch.StartNew();
        long[] findingIds = q.FindingIds(FindingFilter.OpenAll);
        snapshot.Stop();

        // Library → Browse lists a folder; the biggest is a top-level folder holding a tenth of a root.
        FolderNode top = q.FolderRollup(1).Single().Children.OrderByDescending(c => c.Files).First();
        Stopwatch folderSnapshot = Stopwatch.StartNew();
        long[] folderIds = q.FileIds(new FileFilter { RootId = 1, DirId = top.Id, DirectOnly = false }, FileSort.Name);
        folderSnapshot.Stop();

        // Search box: name or folder text over the whole library.
        Stopwatch searchSnapshot = Stopwatch.StartNew();
        long[] searchIds = q.FileIds(new FileFilter { Text = "0001" }, FileSort.Name);
        searchSnapshot.Stop();

        Stopwatch fileSnapshot = Stopwatch.StartNew();
        long[] fileIds = q.FileIds(new FileFilter(), FileSort.Size);
        fileSnapshot.Stop();

        Stopwatch fetch = Stopwatch.StartNew();
        List<FileRow> rows = q.FileRows(fileIds.AsSpan(400_000, 200).ToArray());
        fetch.Stop();

        Stopwatch counts = Stopwatch.StartNew();
        SeverityCounts tiers = q.SeverityCounts();
        counts.Stop();

        // Issues page (UI-04): a filtered, grouped list and its group headers.
        FindingFilter audioHigh = new() { Kinds = [MediaKind.Audio], Severities = [Severity.High, Severity.Medium] };
        Stopwatch grouped = Stopwatch.StartNew();
        long[] groupedIds = q.FindingIds(audioHigh, FindingSort.Severity, false, FindingGroup.Severity);
        List<FindingGroupCount> groupCounts = q.FindingGroupCounts(audioHigh, FindingGroup.Severity);
        grouped.Stop();
        output.WriteLine($"grouped finding filter ({groupedIds.Length:N0} ids, {groupCounts.Count} groups): {grouped.ElapsedMilliseconds} ms");
        Assert.Equal(groupedIds.Length, groupCounts.Sum(g => g.Count));
        Assert.True(grouped.ElapsedMilliseconds < 300, $"grouped finding filter {grouped.ElapsedMilliseconds} ms");

        output.WriteLine($"finding snapshot ({findingIds.Length:N0} ids): {snapshot.ElapsedMilliseconds} ms");
        output.WriteLine($"folder subtree snapshot ({folderIds.Length:N0} ids, by name): {folderSnapshot.ElapsedMilliseconds} ms");
        output.WriteLine($"search snapshot ({searchIds.Length:N0} ids): {searchSnapshot.ElapsedMilliseconds} ms");
        output.WriteLine($"whole-library snapshot ({fileIds.Length:N0} ids, by size): {fileSnapshot.ElapsedMilliseconds} ms");
        output.WriteLine($"200-row fetch: {fetch.ElapsedMilliseconds} ms");
        PerfReport.Add("Issues snapshot", $"all open findings ({findingIds.Length:N0} ids) of 1,000,000 files", $"{snapshot.ElapsedMilliseconds} ms", "under 300 ms");
        PerfReport.Add("Library folder snapshot", $"a folder subtree ({folderIds.Length:N0} ids)", $"{folderSnapshot.ElapsedMilliseconds} ms", "under 300 ms");
        PerfReport.Add("Search snapshot", $"names and folders of 1,000,000 files ({searchIds.Length:N0} ids)", $"{searchSnapshot.ElapsedMilliseconds} ms", "under 300 ms");
        PerfReport.Add("Fetch rows", "200 rows by id", $"{fetch.ElapsedMilliseconds} ms", "under 20 ms");
        output.WriteLine($"severity counts ({tiers.Total:N0}): {counts.ElapsedMilliseconds} ms");

        Assert.Equal(200, rows.Count);
        Assert.True(folderSnapshot.ElapsedMilliseconds < 300, $"folder snapshot {folderSnapshot.ElapsedMilliseconds} ms");
        Assert.True(searchSnapshot.ElapsedMilliseconds < 300, $"search snapshot {searchSnapshot.ElapsedMilliseconds} ms");
        Assert.True(snapshot.ElapsedMilliseconds < 300, $"finding snapshot {snapshot.ElapsedMilliseconds} ms");
        Assert.True(fetch.ElapsedMilliseconds < 20, $"fetch {fetch.ElapsedMilliseconds} ms");
        Assert.True(counts.ElapsedMilliseconds < 100, $"counts {counts.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task MillionFindingsFilterAndGroupWithinTheIssuesBudget()
    {
        using TestDatabase db = new("perf-findings");
        await SyntheticLibrary.GenerateAsync(db.Database, new SyntheticLibraryOptions { Files = 1_000_000, Roots = 4, FindingRate = 1.0 });
        MediaVerifier.UI.Data.LiveUiData data = new(db.Database, () => new MediaVerifier.Core.Settings.ScanSettings(), db.Clock);
        _ = data.LoadIssues(new MediaVerifier.UI.Data.IssuesQuery(FindingFilter.OpenAll));

        // UI-04: the Issues page filters 1,000,000 findings in under 300 ms: the list in order, its group
        // headers, and the chip and drop-down counts, as the page loads them.
        foreach ((string name, FindingFilter filter, FindingGroup group) in new[]
        {
            ("all, by severity", FindingFilter.OpenAll, FindingGroup.Severity),
            ("audio, high and medium", new FindingFilter { Kinds = [MediaKind.Audio], Severities = [Severity.High, Severity.Medium] }, FindingGroup.Severity),
            ("quality", new FindingFilter { Categories = [FindingCategory.Quality] }, FindingGroup.Severity),
            ("all, by rule", FindingFilter.OpenAll, FindingGroup.Rule),
        })
        {
            Stopwatch watch = Stopwatch.StartNew();
            MediaVerifier.UI.Data.IssuesData issues = data.LoadIssues(new MediaVerifier.UI.Data.IssuesQuery(filter, Group: group));
            watch.Stop();
            output.WriteLine($"{name}: {issues.Ids.Length:N0} ids, {issues.Groups.Count} groups, {watch.ElapsedMilliseconds} ms");
            Assert.Equal(issues.Ids.Length, issues.Groups.Sum(g => g.Count));

            // Grouping by rule or folder sorts the whole list again; only the tier grouping has the budget.
            if (group == FindingGroup.Severity)
            {
                Assert.True(watch.ElapsedMilliseconds < 300, $"{name}: {watch.ElapsedMilliseconds} ms");
            }
        }
    }
}
