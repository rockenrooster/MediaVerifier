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

/// <summary>
/// What Library → Browse shows: a folder (with only <paramref name="RootId"/>, that watched folder's top folder), a state
/// chip and a sort; or with <paramref name="Search"/> the files of every watched folder whose name holds the text.
/// </summary>
internal sealed record BrowseQuery(long? RootId, long? DirId, BrowseState State = BrowseState.All, FileSort Sort = FileSort.Name, bool Descending = false, string? Search = null);

/// <summary>
/// A folder of the Browse tree: name, path below its watched folder (empty for the watched folder itself), worst open
/// tier, present files and their size below it, children.
/// </summary>
internal sealed record BrowseFolder(long RootId, long DirId, string Name, string RelPath, Severity Worst, long Files, long Bytes, IReadOnlyList<BrowseFolder> Children);

/// <summary>
/// Library → Browse: the tree, the shown folder's path, its files in order, the chip counts and every watched folder's
/// path (file actions build full paths from it, so a folder added while browsing works at once). <paramref name="NotScanned"/>:
/// the watched folder shown has nothing indexed because no scan has reached it yet.
/// </summary>
internal sealed record BrowseData(
    BrowseQuery Query,
    IReadOnlyList<BrowseFolder> Tree,
    IReadOnlyList<string> Crumbs,
    long[] Ids,
    FolderStateCounts Counts,
    IReadOnlyDictionary<long, string> RootPaths,
    bool NotScanned = false);

internal partial interface IUiData
{
    LibraryData LoadLibrary();

    /// <summary>
    /// The Browse view. A query without a folder (or with one that is gone) shows the top folder of the watched folder
    /// it names, else of the first one.
    /// </summary>
    BrowseData LoadBrowse(BrowseQuery query);

    IReadOnlyList<FileRow> LoadFileRows(IReadOnlyList<long> ids);
}
