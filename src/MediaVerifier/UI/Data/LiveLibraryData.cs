using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.UI.Data;

/// <summary>Library snapshots from the library database (off the UI thread).</summary>
internal sealed partial class LiveUiData
{
    /// <summary>How long a load waits, in all, for watched folders to answer before showing them as not connected.</summary>
    private static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(2);

    /// <summary>Folder checks still waiting for an answer, reused by the next load instead of starting another.</summary>
    private static readonly Dictionary<string, Task<bool>> Probes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock ProbesLock = new();

    public LibraryData LoadLibrary()
    {
        List<RootRecord> roots = database.Roots.List();
        Dictionary<long, RootSummary> summaries = database.Queries.RootSummaries().ToDictionary(s => s.RootId);
        Dictionary<string, bool> connected = AreConnected(roots.Select(r => r.Path));
        List<FolderCard> cards = [];
        foreach (RootRecord root in roots)
        {
            RootSummary summary = summaries.GetValueOrDefault(root.Id) ?? new RootSummary(root.Id, 0, 0, null, null, SeverityCounts.Zero);
            cards.Add(new FolderCard(
                root.Id,
                RootName(root),
                root.Path,
                root.Role,
                connected[root.Path],
                summary.Files,
                summary.Bytes,
                summary.Open,
                summary.HealthyFiles,
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
        Dictionary<long, string> rootPaths = roots.ToDictionary(r => r.Id, r => r.Path);
        List<BrowseFolder> tree = [];
        Dictionary<long, (FolderNode Node, RootRecord Root)> byDir = [];
        foreach (RootRecord root in roots)
        {
            foreach (FolderNode node in database.Queries.FolderRollup(root.Id))
            {
                tree.Add(ToBrowse(root, node, RootName(root), byDir));
            }
        }

        // The header's search (subplan 09, Shell): the files of every watched folder whose name holds the text.
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string text = query.Search.Trim();
            FileFilter search = new() { DirectOnly = false, Text = text };
            long[] found = database.Queries.FileIds(WithState(search, query.State), query.Sort, query.Descending);
            string crumb = string.Format(CultureInfo.CurrentCulture, Strings.Library.SearchResults, text);
            return new BrowseData(query with { RootId = null, DirId = null }, tree, [crumb], found, ChipCounts(search), rootPaths);
        }

        // The folder asked for. Without one (a card, the first visit), or when it is gone (its watched folder was
        // removed), the top folder of the watched folder asked for, else of the first one.
        if (query.DirId is not long dir || !byDir.ContainsKey(dir))
        {
            RootRecord? asked = query.RootId is long rootId ? roots.Find(r => r.Id == rootId) : null;
            BrowseFolder? top = asked is null ? tree.FirstOrDefault() : tree.Find(t => t.RootId == asked.Id);
            if (top is null)
            {
                // A watched folder with nothing indexed yet (never scanned): its name and no files, never another
                // folder's files under its name.
                return new BrowseData(query with { RootId = asked?.Id, DirId = null }, tree, asked is null ? [] : [RootName(asked)], [], new FolderStateCounts(0, 0, 0), rootPaths);
            }

            dir = top.DirId;
        }

        (FolderNode shown, RootRecord shownRoot) = byDir[dir];
        FileFilter folder = new() { RootId = shownRoot.Id, DirId = dir, DirectOnly = true };
        long[] ids = database.Queries.FileIds(WithState(folder, query.State), query.Sort, query.Descending);
        List<string> crumbs = [RootName(shownRoot), .. shown.RelPath.Split('\\', StringSplitOptions.RemoveEmptyEntries)];
        return new BrowseData(query with { RootId = shownRoot.Id, DirId = dir }, tree, crumbs, ids, ChipCounts(folder), rootPaths);
    }

    public IReadOnlyList<FileRow> LoadFileRows(IReadOnlyList<long> ids) => database.Queries.FileRows(ids);

    /// <summary>
    /// The chip's files: all present files, OK (scanned, no finding above Low) or with problems (Medium and above). A
    /// missing file is in none of them, as in the chip counts.
    /// </summary>
    private static FileFilter WithState(FileFilter filter, BrowseState state) => filter with
    {
        States = state == BrowseState.Ok ? [FileState.Ok, FileState.Issues] : [FileState.Pending, FileState.Ok, FileState.Issues, FileState.Skipped, FileState.Error],
        MaxSeverity = state == BrowseState.Ok ? Severity.Low : null,
        MinSeverity = state == BrowseState.Problems ? Severity.Medium : null,
    };

    /// <summary>The three chips' counts, from the same conditions as their lists (subplan 09, Pitfalls).</summary>
    private FolderStateCounts ChipCounts(FileFilter scope)
    {
        int[] counts = database.Queries.FileCounts(scope, [WithState(scope, BrowseState.All), WithState(scope, BrowseState.Ok), WithState(scope, BrowseState.Problems)]);
        return new FolderStateCounts(counts[0], counts[1], counts[2]);
    }

    private static BrowseFolder ToBrowse(RootRecord root, FolderNode node, string? name, Dictionary<long, (FolderNode, RootRecord)> byDir)
    {
        byDir[node.Id] = (node, root);
        List<BrowseFolder> children = [.. node.Children.Select(c => ToBrowse(root, c, null, byDir))];
        return new BrowseFolder(root.Id, node.Id, name ?? node.Name, node.RelPath, node.Worst, node.Files, node.Bytes, children);
    }

    private static string RootName(RootRecord root) => root.Label ?? Path.GetFileName(root.Path.TrimEnd('\\'));

    /// <summary>
    /// Whether each folder answers, all asked at once with one shared wait: a share that is gone can take minutes, so
    /// a folder that has not answered within <see cref="ProbeWait"/> counts as not connected. Its check keeps running
    /// on its pool thread and the next load waits on that one instead of starting another.
    /// </summary>
    internal static Dictionary<string, bool> AreConnected(IEnumerable<string> paths)
    {
        Dictionary<string, Task<bool>> probes = new(StringComparer.OrdinalIgnoreCase);
        lock (ProbesLock)
        {
            foreach (string path in paths)
            {
                if (!probes.ContainsKey(path))
                {
                    if (!Probes.TryGetValue(path, out Task<bool>? probe))
                    {
                        probe = Task.Run(() => Directory.Exists(path));
                        Probes[path] = probe;
                    }

                    probes[path] = probe;
                }
            }
        }

        _ = Task.WaitAll([.. probes.Values], ProbeWait);
        Dictionary<string, bool> connected = new(StringComparer.OrdinalIgnoreCase);
        lock (ProbesLock)
        {
            foreach ((string path, Task<bool> probe) in probes)
            {
                connected[path] = probe.IsCompletedSuccessfully && probe.Result;
                if (probe.IsCompleted && Probes.TryGetValue(path, out Task<bool>? kept) && ReferenceEquals(kept, probe))
                {
                    // Answered: the next load asks again.
                    Probes.Remove(path);
                }
            }
        }

        return connected;
    }
}
