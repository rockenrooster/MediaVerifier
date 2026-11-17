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

        // No folder chosen yet: the first root's top folder.
        long? dirId = query.DirId ?? (tree.Count > 0 ? tree[0].DirId : null);
        if (dirId is not long dir || !byDir.TryGetValue(dir, out (FolderNode Node, RootRecord Root) shown))
        {
            return new BrowseData(query, tree, [], [], new FolderStateCounts(0, 0, 0));
        }

        FileFilter filter = new()
        {
            RootId = shown.Root.Id,
            DirId = dir,
            DirectOnly = true,
            States = query.State == BrowseState.All ? [FileState.Pending, FileState.Ok, FileState.Issues, FileState.Skipped, FileState.Error] : query.State == BrowseState.Ok ? [FileState.Ok, FileState.Issues] : null,
            MaxSeverity = query.State == BrowseState.Ok ? Severity.Low : null,
            MinSeverity = query.State == BrowseState.Problems ? Severity.Medium : null,
        };
        long[] ids = database.Queries.FileIds(filter, query.Sort, query.Descending);
        List<string> crumbs = [RootName(shown.Root)];
        crumbs.AddRange(shown.Node.RelPath.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        return new BrowseData(query with { RootId = shown.Root.Id, DirId = dir }, tree, crumbs, ids, database.Queries.FolderStates(dir));
    }

    public IReadOnlyList<FileRow> LoadFileRows(IReadOnlyList<long> ids) => database.Queries.FileRows(ids);

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
