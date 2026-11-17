using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Data;

namespace MediaVerifier.Diagnostics;

/// <summary>The Library folders and the Browse view of the mockups (docs/design/library*.svg).</summary>
internal sealed partial class DemoUiData
{
    private const long Gib = 1024L * 1024 * 1024;

    private static readonly FileRow[] OverpassFiles =
    [
        File(101, "01 - Slip Road.flac", 29.8, FileState.Issues, Severity.High, verifiedDaysAgo: 0),
        File(102, "02 - Sodium.flac", 33.1, FileState.Ok, Severity.None, verifiedDaysAgo: 0),
        File(103, "03 - Lane Change.flac", 27.4, FileState.Ok, Severity.None, verifiedDaysAgo: 35),
        File(104, "04 - Median.flac", 35.0, FileState.Issues, Severity.Low, verifiedDaysAgo: 35),
        File(105, "05 - Cat's Eyes.flac", 30.2, FileState.Ok, Severity.None, verifiedDaysAgo: 35),
        File(106, "06 - Underpass.flac", 26.6, FileState.Ok, Severity.None, verifiedDaysAgo: 35),
        File(107, "07 - Overpass.flac", 31.2, FileState.Issues, Severity.High, verifiedDaysAgo: 35),
        File(108, "08 - Hard Shoulder.flac", 34.8, FileState.Pending, Severity.None, verifiedDaysAgo: null),
        File(109, "09 - Services.flac", 31.9, FileState.Skipped, Severity.None, verifiedDaysAgo: null, skip: SkipReason.Locked),
        new FileRow(110, 1, 14, "cover.jpg", @"Night Drive\Overpass", MediaKind.Photo, 412 * 1024, 0, FileState.Ok, SkipReason.None, Severity.None, 0, At(16, 3, 12).UtcTicks, 2, At(16, 3, 12).UtcTicks, "jpeg", null, null, 1400, 1400),
    ];

    public LibraryData LoadLibrary() => new(
    [
        new FolderCard(1, "Music", @"D:\Music", RootRole.Library, true, 3_201, 96 * Gib, new SeverityCounts(2, 7, 76, 5), 77, StorageClass.Ssd, At(16, 3, 12), At(16, 3, 12), At(16, 3, 12)),
        new FolderCard(2, "Photos", @"D:\Photos", RootRole.Library, true, 8_201, 212 * Gib, new SeverityCounts(1, 1, 2, 0), 404, StorageClass.Ssd, At(16, 3, 12), At(16, 3, 12), At(16, 3, 12)),
        new FolderCard(3, "Videos", @"\\nas\media\Videos", RootRole.Library, true, 1_105, (long)(3.2 * 1024 * Gib), new SeverityCounts(0, 6, 18, 7), 27, StorageClass.Network, At(14, 3, 40), At(14, 3, 40), At(14, 3, 40)),
        new FolderCard(4, "Backup", @"E:\Backup", RootRole.Backup, false, 12_410, (long)(3.5 * 1024 * Gib), SeverityCounts.Zero, 12_410, StorageClass.Removable, null, At(12, 21, 5), At(12, 21, 5)),
    ]);

    public BrowseData LoadBrowse(BrowseQuery query)
    {
        BrowseFolder overpass = new(1, 14, "Overpass", Severity.High, []);
        BrowseFolder music = new(1, 10, "Music", Severity.Critical,
        [
            new BrowseFolder(1, 11, "Glasshouse", Severity.Medium, []),
            new BrowseFolder(1, 12, "Harbor Lights", Severity.Critical, []),
            new BrowseFolder(1, 13, "Night Drive", Severity.High, [overpass, new BrowseFolder(1, 15, "Singles", Severity.None, [])]),
            new BrowseFolder(1, 16, "Static Bloom", Severity.Medium, []),
            new BrowseFolder(1, 17, "Sunday Session", Severity.High, []),
        ]);
        BrowseFolder photos = new(2, 20, "Photos", Severity.Critical, [new BrowseFolder(2, 21, "2019", Severity.Critical, [])]);
        BrowseFolder videos = new(3, 30, "Videos", Severity.High, [new BrowseFolder(3, 31, "Family", Severity.Critical, [])]);
        IEnumerable<FileRow> files = query.DirId is null or 14 ? OverpassFiles : [];
        files = query.State switch
        {
            BrowseState.Ok => files.Where(f => f.State is FileState.Ok or FileState.Issues && f.WorstSeverity <= Severity.Low),
            BrowseState.Problems => files.Where(f => f.WorstSeverity >= Severity.Medium),
            _ => files,
        };
        files = query.Sort switch
        {
            FileSort.Size => files.OrderBy(f => f.Size),
            _ => files.OrderBy(f => f.Name, StringComparer.Ordinal),
        };
        if (query.Descending)
        {
            files = files.Reverse();
        }

        return new BrowseData(query with { RootId = 1, DirId = 14 }, [music, photos, videos], ["Music", "Night Drive", "Overpass"], [.. files.Select(f => f.Id)], new FolderStateCounts(10, 6, 2));
    }

    public IReadOnlyList<FileRow> LoadFileRows(IReadOnlyList<long> ids) => [.. ids.Select(id => OverpassFiles.First(f => f.Id == id))];

    private static FileRow File(long id, string name, double megabytes, FileState state, Severity worst, int? verifiedDaysAgo, SkipReason skip = SkipReason.None)
    {
        bool scanned = state is FileState.Ok or FileState.Issues;
        long? verified = verifiedDaysAgo switch { null => null, 0 => At(16, 3, 12).UtcTicks, _ => Local(10, 12, 3, 0).UtcTicks };
        return new FileRow(
            id, 1, 14, name, @"Night Drive\Overpass", MediaKind.Audio, (long)(megabytes * 1024 * 1024), 0, state, skip, worst, worst == Severity.None ? 0 : 1,
            verified, verified is null ? 0 : 2, scanned ? At(16, 3, 12).UtcTicks : null, "flac", null, "flac", null, null, scanned ? 44100 : null, scanned ? 16 : null);
    }
}
