using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>One watched folder's card on Library → Folders.</summary>
internal sealed record FolderCard(
    long RootId,
    string Name,
    string Path,
    RootRole Role,
    bool Connected,
    long Files,
    long Bytes,
    SeverityCounts Problems,
    long OkFiles,
    StorageClass Storage,
    DateTimeOffset? LastScanned,
    DateTimeOffset? LastSeen,
    DateTimeOffset? FingerprintsRecorded);

/// <summary>Library → Folders.</summary>
internal sealed record LibraryData(IReadOnlyList<FolderCard> Folders);

/// <summary>The exclusive state chips of Library → Browse.</summary>
internal enum BrowseState
{
    All,
    Ok,
    Problems,
}

/// <summary>What Library → Browse shows: a folder (or a whole root), a state chip and a sort.</summary>
/// <summary>What Browse shows: a folder, or with <paramref name="Search"/> the files of every watched folder matching it.</summary>
internal sealed record BrowseQuery(long? RootId, long? DirId, BrowseState State = BrowseState.All, FileSort Sort = FileSort.Name, bool Descending = false, string? Search = null);

/// <summary>A folder of the Browse tree: name, worst open tier below it, children.</summary>
internal sealed record BrowseFolder(long RootId, long DirId, string Name, Severity Worst, IReadOnlyList<BrowseFolder> Children);

/// <summary>Library → Browse: the tree, the shown folder's path, its files in order and the chip counts.</summary>
internal sealed record BrowseData(
    BrowseQuery Query,
    IReadOnlyList<BrowseFolder> Tree,
    IReadOnlyList<string> Crumbs,
    long[] Ids,
    FolderStateCounts Counts);

/// <summary>Library data (separate interface so pages depend only on what they read).</summary>
internal interface ILibraryData
{
    LibraryData LoadLibrary();

    /// <summary>The Browse view; a query without a folder shows the first root's top folder.</summary>
    BrowseData LoadBrowse(BrowseQuery query);

    IReadOnlyList<FileRow> LoadFileRows(IReadOnlyList<long> ids);
}
