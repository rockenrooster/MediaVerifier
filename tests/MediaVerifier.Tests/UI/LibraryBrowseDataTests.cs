using System.Diagnostics;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Scheduling;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>
/// Library → Folders and Browse from a real database: the folder a card opens, the chips against their lists, the
/// header's search, the name order, the tree, the card's severity bar, the empty-table message, the folder checks,
/// dropped paths and the schedules of a removed folder.
/// </summary>
public sealed class LibraryBrowseDataTests
{
    [Fact]
    public async Task A_card_opens_its_own_watched_folder_not_the_first_one()
    {
        using TestDatabase db = new();
        long music = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        long photos = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Photos", Label = "Photos" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(music, "", "song.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(photos, "", "a.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
            new(photos, "2019", "b.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
        ]);
        LiveUiData data = Data(db);

        BrowseData shown = data.LoadBrowse(new BrowseQuery(photos, null));

        Assert.Equal(photos, shown.Query.RootId);
        Assert.Equal(shown.Tree.Single(t => t.RootId == photos).DirId, shown.Query.DirId);
        Assert.Equal(["Photos"], shown.Crumbs);
        Assert.Equal([files[1].FileId], shown.Ids);
        Assert.Equal(new FolderStateCounts(1, 0, 0), shown.Counts);
        Assert.Equal(@"D:\Photos", shown.RootPaths[photos]);
        Assert.Equal("2019", shown.Tree.Single(t => t.RootId == photos).Children.Single().RelPath);

        // Without a watched folder named: the first one.
        Assert.Equal(music, data.LoadBrowse(new BrowseQuery(null, null)).Query.RootId);
    }

    [Fact]
    public async Task A_folder_never_scanned_shows_its_own_name_and_no_other_folders_files()
    {
        using TestDatabase db = new();
        long music = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        long fresh = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\New", Label = "New" });
        await db.Database.Files.UpsertAsync([new(music, "", "song.flac", "flac", MediaKind.Audio, 100, 1, null, 1)]);

        BrowseData shown = Data(db).LoadBrowse(new BrowseQuery(fresh, null));

        Assert.Equal(fresh, shown.Query.RootId);
        Assert.Null(shown.Query.DirId);
        Assert.Equal(["New"], shown.Crumbs);
        Assert.Empty(shown.Ids);
        Assert.Equal(new FolderStateCounts(0, 0, 0), shown.Counts);
        Assert.True(shown.NotScanned);
    }

    [Fact]
    public async Task Without_files_anywhere_Browse_names_the_first_watched_folder_and_whether_a_scan_reached_it()
    {
        using TestDatabase db = new();
        long music = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Photos", Label = "Photos" });
        LiveUiData data = Data(db);

        // The view switch asks for no folder: the first watched one, never "Add a folder" while folders are watched.
        BrowseData fresh = data.LoadBrowse(new BrowseQuery(null, null));

        // Scanned, but it holds no media: no folder rows are written, and it is not "Not scanned yet".
        await db.Database.Roots.MarkOnlineAsync(music, db.Clock.UtcNow, null, StorageClass.Ssd);
        BrowseData scanned = data.LoadBrowse(new BrowseQuery(null, null));

        Assert.Equal(music, fresh.Query.RootId);
        Assert.Equal(["Music"], fresh.Crumbs);
        Assert.Equal(Strings.Library.NotScannedYet, LibraryPage.EmptyMessage(fresh).Headline);
        Assert.Equal(["Music"], scanned.Crumbs);
        Assert.Equal(Strings.Library.NoFilesHere, LibraryPage.EmptyMessage(scanned).Headline);

        using TestDatabase none = new();
        Assert.Equal(Strings.Issues.NoFoldersHeadline, LibraryPage.EmptyMessage(Data(none).LoadBrowse(new BrowseQuery(null, null))).Headline);
    }

    [Fact]
    public void The_empty_table_says_why_it_is_empty()
    {
        Dictionary<long, string> watched = new() { [1] = @"D:\Music" };
        string Why(BrowseQuery query, int all = 0, bool notScanned = false, Dictionary<long, string>? roots = null) =>
            LibraryPage.EmptyMessage(new BrowseData(query, [], [], [], new FolderStateCounts(all, 0, 0), roots ?? watched, notScanned)).Headline;

        // Names match (or the folder has files), and the chip leaves them out.
        Assert.Equal(Strings.Issues.FilteredHeadline, Why(new BrowseQuery(null, null, BrowseState.Ok, Search: "beach"), all: 3));
        Assert.Equal(Strings.Issues.FilteredHeadline, Why(new BrowseQuery(1, 10, BrowseState.Problems), all: 2));

        // Nothing there for any chip.
        Assert.Equal("No file names match \u201Cbeach\u201D", Why(new BrowseQuery(null, null, BrowseState.Ok, Search: "beach")));
        Assert.Equal(Strings.Library.NoFilesHere, Why(new BrowseQuery(1, 10, BrowseState.Problems)));
        Assert.Equal(Strings.Library.NotScannedYet, Why(new BrowseQuery(1, null), notScanned: true));
        Assert.Equal(Strings.Issues.NoFoldersHeadline, Why(new BrowseQuery(null, null), roots: new Dictionary<long, string>()));
    }

    [Fact]
    public async Task A_folder_that_is_gone_falls_back_to_a_watched_folder()
    {
        using TestDatabase db = new();
        long music = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        long photos = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Photos", Label = "Photos" });
        await db.Database.Files.UpsertAsync(
        [
            new(music, "", "song.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(photos, "Trip", "a.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
        ]);
        LiveUiData data = Data(db);
        long trip = data.LoadBrowse(new BrowseQuery(photos, null)).Tree.Single(t => t.RootId == photos).Children.Single().DirId;

        // The browsed watched folder stops being watched: Browse shows the first one left, not a blank page.
        await db.Database.Roots.RemoveAsync(photos);
        BrowseData shown = data.LoadBrowse(new BrowseQuery(photos, trip));

        Assert.Equal(music, shown.Query.RootId);
        Assert.Equal(["Music"], shown.Crumbs);
        Assert.Single(shown.Ids);
    }

    [Fact]
    public async Task Chip_counts_equal_their_lists_and_leave_out_missing_files()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Music", Label = "Music" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, "", "clean.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, "", "low.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, "", "medium.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, "", "gone.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, "", "waiting.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
        ]);
        for (int i = 0; i < 4; i++)
        {
            await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[i].FileId) { State = FileState.Ok });
        }

        await Raise(db, files[1].FileId, "AUD-DC", Severity.Low);
        await Raise(db, files[2].FileId, "AUD-CLIPPING", Severity.Medium);
        await Raise(db, files[3].FileId, "AUD-CLIPPING", Severity.Medium);

        // Deleted on disk and found missing by a scan: its finding stays open, but Browse lists it under no chip.
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[3].FileId) { State = FileState.Missing });
        LiveUiData data = Data(db);
        long dir = data.LoadBrowse(new BrowseQuery(root, null)).Query.DirId!.Value;

        BrowseData all = data.LoadBrowse(new BrowseQuery(root, dir));
        int ok = data.LoadBrowse(new BrowseQuery(root, dir, BrowseState.Ok)).Ids.Length;
        long[] problems = data.LoadBrowse(new BrowseQuery(root, dir, BrowseState.Problems)).Ids;

        Assert.Equal(new FolderStateCounts(4, 2, 1), all.Counts);
        Assert.Equal(all.Counts.All, all.Ids.Length);
        Assert.Equal(all.Counts.Ok, ok);
        Assert.Equal([files[2].FileId], problems);
    }

    [Fact]
    public async Task The_header_search_matches_file_names_and_counts_its_chips()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Photos", Label = "Photos" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, "Trip", "2019 beach.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
            new(root, "2019", "dog.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
            new(root, "2019", "Party 2019.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
        ]);
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[0].FileId) { State = FileState.Ok });
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[2].FileId) { State = FileState.Ok });
        await Raise(db, files[2].FileId, "COR-DECODE", Severity.High);

        BrowseData found = Data(db).LoadBrowse(new BrowseQuery(null, null, Search: " 2019 "));

        // Subplan 09: the search box searches file names; dog.jpg is only in a folder called 2019.
        Assert.Equal([files[0].FileId, files[2].FileId], found.Ids);
        Assert.Equal(new FolderStateCounts(2, 1, 1), found.Counts);
        Assert.Equal(["Files matching \u201C2019\u201D"], found.Crumbs);
        Assert.Null(found.Query.RootId);
    }

    [Fact]
    public async Task Names_sort_without_case_as_the_tree_does()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Photos", Label = "Photos" });
        await db.Database.Files.UpsertAsync(
        [
            new(root, "", "Zebra.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
            new(root, "", "apple.jpg", "jpg", MediaKind.Photo, 20, 1, null, 1),
            new(root, "", "Mango.jpg", "jpg", MediaKind.Photo, 20, 1, null, 1),
        ]);
        LiveUiData data = Data(db);

        BrowseData up = data.LoadBrowse(new BrowseQuery(root, null));
        BrowseData down = data.LoadBrowse(new BrowseQuery(root, null, Descending: true));
        BrowseData bySize = data.LoadBrowse(new BrowseQuery(root, null, Sort: FileSort.Size));

        Assert.Equal(["apple.jpg", "Mango.jpg", "Zebra.jpg"], data.LoadFileRows(up.Ids).Select(r => r.Name));
        Assert.Equal(["Zebra.jpg", "Mango.jpg", "apple.jpg"], data.LoadFileRows(down.Ids).Select(r => r.Name));
        Assert.Equal(["Zebra.jpg", "apple.jpg", "Mango.jpg"], data.LoadFileRows(bySize.Ids).Select(r => r.Name));
    }

    [Fact]
    public async Task A_folder_without_files_leaves_the_tree()
    {
        using TestDatabase db = new();
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = @"D:\Photos", Label = "Photos" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, @"Holiday 2019\Day 1", "a.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
            new(root, "Kept", "b.jpg", "jpg", MediaKind.Photo, 10, 1, null, 1),
        ]);

        // Renamed on disk: the scan finds its files missing, the folder rows stay.
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[0].FileId) { State = FileState.Missing });
        BrowseFolder top = Assert.Single(Data(db).LoadBrowse(new BrowseQuery(root, null)).Tree);

        Assert.Equal(["Kept"], top.Children.Select(c => c.Name));
        Assert.Equal(1, top.Files);
    }

    [Fact]
    public async Task The_card_bar_counts_files_without_problems_not_files_minus_findings()
    {
        using TestDatabase db = new();
        using TempDirectory folder = new("library-card");
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = folder.Path, Label = "Music" });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, "", "1.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, "", "2.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, "", "3.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, "", "4.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
        ]);
        for (int i = 0; i < 3; i++)
        {
            await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[i].FileId) { State = FileState.Ok });
        }

        // One file with three findings; one file not scanned yet (not known to be healthy).
        HashSet<string> rules = ["AUD-CLIPPING", "AUD-DC", "AUD-FAKE-LOSSLESS"];
        await db.Database.Findings.ReplaceForFileAsync(files[2].FileId, 1, rules, [.. rules.Select(r => new Finding(r, Severity.Medium, 1f, r, Finding.NoEvidence))], false);

        FolderCard card = Data(db).LoadLibrary().Folders.Single();

        Assert.Equal(3, card.Problems.Total);
        Assert.Equal(2, card.OkFiles);
        Assert.Equal(2, db.Database.Queries.KindSummaries().Single(k => k.Kind == MediaKind.Audio).HealthyFiles);
    }

    [Fact]
    public void Folders_answer_together_within_one_wait()
    {
        using TempDirectory here = new("library-probe");
        string gone = Path.Combine(here.Path, "gone");

        Dictionary<string, bool> connected = LiveUiData.AreConnected([here.Path, gone, here.Path]);

        Assert.True(connected[here.Path]);
        Assert.False(connected[gone]);
        Assert.Equal(2, connected.Count);
    }

    /// <summary>
    /// Folders that do not answer cost one wait together, not one each. Asked again before they answer, their checks
    /// are taken over (a check that would answer at once is not started) and not waited for again. Once they have
    /// answered, the next load asks afresh instead of taking that answer, however old it is.
    /// </summary>
    [Fact]
    public void Folders_that_do_not_answer_share_one_wait_and_are_asked_afresh_once_answered()
    {
        TimeSpan wait = TimeSpan.FromSeconds(1);
        using ManualResetEventSlim gate = new();
        using CountdownEvent answered = new(2);
        string[] shares = [$@"\\offline-{Guid.NewGuid():N}\a", $@"\\offline-{Guid.NewGuid():N}\b"];
        bool Offline(string path)
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            answered.Signal();
            return false;
        }

        Stopwatch first = Stopwatch.StartNew();
        Dictionary<string, bool> waited = LiveUiData.AreConnected(shares, Offline, wait);
        first.Stop();
        Stopwatch second = Stopwatch.StartNew();
        Dictionary<string, bool> taken = LiveUiData.AreConnected(shares, _ => true, wait);
        second.Stop();

        // They answer "not there"; later the shares are back.
        gate.Set();
        Assert.True(answered.Wait(TimeSpan.FromSeconds(10)));
        Thread.Sleep(200);
        Dictionary<string, bool> fresh = LiveUiData.AreConnected(shares, _ => true, TimeSpan.FromSeconds(10));

        Assert.Equal([false, false], waited.Values);
        Assert.InRange(first.ElapsedMilliseconds, 900, 1900);
        Assert.Equal([false, false], taken.Values);
        Assert.InRange(second.ElapsedMilliseconds, 0, 500);
        Assert.Equal([true, true], fresh.Values);
    }

    [Fact]
    public async Task Dropped_files_are_left_out_unless_nothing_dropped_is_a_folder()
    {
        using TempDirectory temp = new("library-drop");
        string album = Directory.CreateDirectory(temp.Combine("Album")).FullName;
        string photo = temp.Combine("photo.jpg");
        string notes = temp.Combine("notes.txt");
        await File.WriteAllTextAsync(photo, "x");
        await File.WriteAllTextAsync(notes, "x");

        Assert.Equal([album], await LibraryPage.FoldersAmongAsync([photo, album, notes]));

        // Nothing but files: the first, so its dialog says it is not a folder.
        Assert.Equal([photo], await LibraryPage.FoldersAmongAsync([photo, notes]));
    }

    [Fact]
    public void A_removed_folder_leaves_the_schedules_that_named_it()
    {
        PlanRecord both = Plan(new PlanDefinition { Roots = [1, 2] });
        PlanRecord only = Plan(new PlanDefinition { Roots = [1] });

        PlanRecord? rest = LibraryPage.WithoutFolder(both, 1, 99);
        PlanRecord? none = LibraryPage.WithoutFolder(only, 1, 99);

        Assert.Equal([2L], PlanDefinition.FromJson(rest!.DefinitionJson)!.Roots!);
        Assert.True(rest.Enabled);
        Assert.Equal(99, rest.UpdatedUtc);

        // Left without a folder: switched off, and its definition asks for one instead of meaning "all folders".
        Assert.False(none!.Enabled);
        PlanDefinition empty = PlanDefinition.FromJson(none.DefinitionJson)!;
        Assert.Empty(empty.Roots!);
        Assert.Contains("Choose at least one folder.", empty.Validate());

        // All folders, or other folders: unchanged.
        Assert.Null(LibraryPage.WithoutFolder(Plan(new PlanDefinition()), 1, 99));
        Assert.Null(LibraryPage.WithoutFolder(Plan(new PlanDefinition { Roots = [3] }), 1, 99));
    }

    [Fact]
    public void A_subfolders_scan_estimate_is_its_share_of_the_watched_folder()
    {
        ScanEstimate whole = new(40_000, 400L << 30, "Photos", TimeSpan.FromHours(1), TimeSpan.FromHours(4), null);

        ScanEstimate part = LibraryPage.PartOf(whole, "2019", 20_000, 100L << 30);

        Assert.Equal(new ScanEstimate(20_000, 100L << 30, "2019", TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), null), part);
        Assert.True(part.IsLarge);
    }

    private static LiveUiData Data(TestDatabase db) => new(db.Database, () => new ScanSettings(), db.Clock);

    private static Task Raise(TestDatabase db, long fileId, string rule, Severity severity) =>
        db.Database.Findings.ReplaceForFileAsync(fileId, 1, [rule], [new Finding(rule, severity, 1f, rule, Finding.NoEvidence)], false);

    private static PlanRecord Plan(PlanDefinition definition) => new() { Id = 5, Name = "Weekly", Enabled = true, DefinitionJson = definition.ToJson() };
}
