using MediaVerifier.Core.Model;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Data;

namespace MediaVerifier.Tests.UI;

/// <summary>Library snapshots from a real database (task UI-06).</summary>
public sealed class LibraryDataTests
{
    [Fact]
    public async Task FoldersBrowseAndStateChipsComeFromTheDatabase()
    {
        using TestDatabase db = new();
        using TempDirectory folder = new("library-root");
        long root = await db.Database.Roots.AddAsync(new RootRecord { Path = folder.Path, Label = "Music" });
        long missing = await db.Database.Roots.AddAsync(new RootRecord { Path = Path.Combine(folder.Path, "gone"), Label = "Backup", Role = RootRole.Backup });
        FileUpsertResult[] files = await db.Database.Files.UpsertAsync(
        [
            new(root, "Album", "01.flac", "flac", MediaKind.Audio, 100, 1, null, 1),
            new(root, "Album", "02.flac", "flac", MediaKind.Audio, 200, 1, null, 1),
            new(root, "Album", "03.flac", "flac", MediaKind.Audio, 300, 1, null, 1),
            new(root, "Album", "04.flac", "flac", MediaKind.Audio, 400, 1, null, 1),
        ]);
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[0].FileId) { State = FileState.Ok });
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[1].FileId) { State = FileState.Ok });
        await db.Database.Files.ApplyUpdateAsync(new FileUpdate(files[2].FileId) { State = FileState.Ok });
        await db.Database.Findings.ReplaceForFileAsync(files[1].FileId, 1, ["AUD-CLIPPING"], [new Finding("AUD-CLIPPING", Severity.High, 1f, "clipping", Finding.NoEvidence)], false);
        await db.Database.Findings.ReplaceForFileAsync(files[2].FileId, 1, ["AUD-DC"], [new Finding("AUD-DC", Severity.Low, 1f, "dc", Finding.NoEvidence)], false);
        LiveUiData data = new(db.Database, () => new ScanSettings(), db.Clock);

        LibraryData library = data.LoadLibrary();
        FolderCard music = library.Folders.Single(f => f.RootId == root);
        Assert.True(music.Connected);
        Assert.Equal(4, music.Files);
        Assert.Equal(1000, music.Bytes);
        Assert.Equal(1, music.Problems.High);
        Assert.Equal(1, music.Problems.Low);

        // A folder that is not there is shown as not connected, never as missing files.
        Assert.False(library.Folders.Single(f => f.RootId == missing).Connected);

        BrowseData all = data.LoadBrowse(new BrowseQuery(null, null));
        Assert.Equal(["Music"], all.Tree.Select(t => t.Name));
        BrowseFolder album = all.Tree[0].Children.Single();
        Assert.Equal(Severity.High, album.Worst);

        BrowseData shown = data.LoadBrowse(new BrowseQuery(root, album.DirId));
        Assert.Equal(["Music", "Album"], shown.Crumbs);
        Assert.Equal(new FolderStateCounts(4, 2, 1), shown.Counts);
        Assert.Equal(4, shown.Ids.Length);
        Assert.Equal(2, data.LoadBrowse(new BrowseQuery(root, album.DirId, BrowseState.Ok)).Ids.Length);
        long problem = Assert.Single(data.LoadBrowse(new BrowseQuery(root, album.DirId, BrowseState.Problems)).Ids);
        Assert.Equal(files[1].FileId, problem);

        BrowseData bySize = data.LoadBrowse(new BrowseQuery(root, album.DirId, Sort: FileSort.Size, Descending: true));
        Assert.Equal(["04.flac", "03.flac", "02.flac", "01.flac"], data.LoadFileRows(bySize.Ids).Select(r => r.Name));
        Assert.Equal("Not scanned yet", UiFormat.FileFormat(data.LoadFileRows([files[3].FileId])[0]));
    }
}
