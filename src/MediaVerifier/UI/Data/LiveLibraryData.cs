using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Library snapshots from the library database (off the UI thread).</summary>
internal sealed partial class LiveUiData
{
    public LibraryData LoadLibrary()
    {
        List<RootRecord> roots = database.Roots.List();
        Dictionary<long, RootSummary> summaries = database.Queries.RootSummaries().ToDictionary(s => s.RootId);
        List<FolderCard> cards = [];
        foreach (RootRecord root in roots)
        {
            RootSummary summary = summaries.GetValueOrDefault(root.Id) ?? new RootSummary(root.Id, 0, 0, null, null, SeverityCounts.Zero);
            cards.Add(new FolderCard(
                root.Id,
                RootName(root),
                root.Path,
                root.Role,
                IsConnected(root.Path),
                summary.Files,
                summary.Bytes,
                summary.Open,
                Math.Max(0, summary.Files - summary.Open.Total),
                root.StorageClass,
                summary.LastScannedUtc is long scanned ? Ticks(scanned) : null,
                root.LastOnlineUtc,
                summary.FingerprintsUtc is long hashed ? Ticks(hashed) : null));
        }

        return new LibraryData(cards);
    }

    public BrowseData LoadBrowse(BrowseQuery query)
    {
        List<RootRecord> roots = database.Roots.List();
        List<BrowseFolder> tree = [];
        Dictionary<long, (FolderNode Node, RootRecord Root)> byDir = [];
        foreach (RootRecord root in roots)
        {
            foreach (FolderNode node in database.Queries.FolderRollup(root.Id))
            {
                tree.Add(ToBrowse(root, node, RootName(root), byDir));
            }
        }

        // The header's search (subplan 09, Shell): every watched folder's files whose name or folder holds the text.
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            FileFilter search = new() { DirectOnly = false, Text = query.Search.Trim() };
            long[] found = database.Queries.FileIds(WithState(search, query.State), query.Sort, query.Descending);
            FolderStateCounts counts = new(
                database.Queries.FileIds(WithState(search, BrowseState.All)).Length,
                database.Queries.FileIds(WithState(search, BrowseState.Ok)).Length,
                database.Queries.FileIds(WithState(search, BrowseState.Problems)).Length);
            string crumb = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Library.SearchResults, query.Search.Trim());
            return new BrowseData(query with { RootId = null, DirId = null }, tree, [crumb], found, counts);
        }

        // No folder chosen yet: the first root's top folder.
        long? dirId = query.DirId ?? (tree.Count > 0 ? tree[0].DirId : null);
        if (dirId is not long dir || !byDir.TryGetValue(dir, out (FolderNode Node, RootRecord Root) shown))
        {
            return new BrowseData(query, tree, [], [], new FolderStateCounts(0, 0, 0));
        }

        FileFilter filter = WithState(new FileFilter { RootId = shown.Root.Id, DirId = dir, DirectOnly = true }, query.State);
        long[] ids = database.Queries.FileIds(filter, query.Sort, query.Descending);
        List<string> crumbs = [RootName(shown.Root)];
        crumbs.AddRange(shown.Node.RelPath.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        return new BrowseData(query with { RootId = shown.Root.Id, DirId = dir }, tree, crumbs, ids, database.Queries.FolderStates(dir));
    }

    public IReadOnlyList<FileRow> LoadFileRows(IReadOnlyList<long> ids) => database.Queries.FileRows(ids);

    /// <summary>The chip's files: all, OK (no finding above Low) or with problems (Medium and above).</summary>
    private static FileFilter WithState(FileFilter filter, BrowseState state) => filter with
    {
        States = state == BrowseState.All ? [FileState.Pending, FileState.Ok, FileState.Issues, FileState.Skipped, FileState.Error] : state == BrowseState.Ok ? [FileState.Ok, FileState.Issues] : null,
        MaxSeverity = state == BrowseState.Ok ? Severity.Low : null,
        MinSeverity = state == BrowseState.Problems ? Severity.Medium : null,
    };

    private static BrowseFolder ToBrowse(RootRecord root, FolderNode node, string? name, Dictionary<long, (FolderNode, RootRecord)> byDir)
    {
        byDir[node.Id] = (node, root);
        List<BrowseFolder> children = [.. node.Children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => ToBrowse(root, c, null, byDir))];
        return new BrowseFolder(root.Id, node.Id, name ?? node.Name, node.Worst, children);
    }

    private static string RootName(RootRecord root) => root.Label ?? Path.GetFileName(root.Path.TrimEnd('\\'));

    /// <summary>A share that is gone can take many seconds to answer; after two it counts as not connected.</summary>
    private static bool IsConnected(string path)
    {
        Task<bool> probe = Task.Run(() => Directory.Exists(path));
        return probe.Wait(TimeSpan.FromSeconds(2)) && probe.Result;
    }
}
