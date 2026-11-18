using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Integrity;

/// <summary>A possible source to restore a damaged file from.</summary>
public sealed record CopyCandidate(FileRecord File, RootRecord Root, string FullPath, bool FingerprintMatch);

/// <summary>
/// Finds healthy copies of a damaged file (task I-09): rows in any root whose fingerprint equals the
/// damaged file's old baseline, that exist, are online and still match their own baseline. A weaker list
/// by name and size under backup roots is offered as unverified. Before use a copy is re-read and its
/// fingerprint compared again. An empty baseline has no copies: every empty file shares it.
/// </summary>
public sealed class HealthyCopyFinder(Database database, IMediaFileSystem fileSystem)
{
    public IReadOnlyList<CopyCandidate> Find(FileRecord damaged)
    {
        ArgumentNullException.ThrowIfNull(damaged);
        if (IsEmptyBaseline(damaged))
        {
            return [];
        }

        Dictionary<long, RootRecord> roots = database.Roots.List().ToDictionary(r => r.Id);
        List<CopyCandidate> candidates = [];
        foreach (CopyCandidate recorded in Recorded(damaged, roots))
        {
            // The copy must still match its own baseline on disk: a copy that itself changed is no source.
            FileStat? stat = fileSystem.Stat(recorded.FullPath);
            if (stat is not null && ChangeClassifier.StatSame(recorded.File.HashSize!.Value, recorded.File.HashMtimeUtc!.Value, stat.Value.Size, stat.Value.LastWriteUtcTicks, FileSystemFormat.IsFatLike(recorded.FullPath)))
            {
                candidates.Add(recorded);
            }
        }

        if (candidates.Count == 0)
        {
            // Weaker: same name and size under backup roots, unverified.
            foreach (RootRecord root in roots.Values.Where(r => r.Role == RootRole.Backup))
            {
                long[] ids = database.Queries.FileIds(new FileFilter { RootId = root.Id, Text = damaged.Name });
                foreach (FileRecord row in database.Files.GetMany(ids))
                {
                    if (row.Id != damaged.Id && string.Equals(row.Name, damaged.Name, StringComparison.OrdinalIgnoreCase) && row.Size == (damaged.HashSize ?? damaged.Size))
                    {
                        string path = FullPath(root, row);
                        if (fileSystem.FileExists(path))
                        {
                            candidates.Add(new CopyCandidate(row, root, path, FingerprintMatch: false));
                        }
                    }
                }
            }
        }

        return Ordered(candidates);
    }

    /// <summary>
    /// The copies the index knows of, without touching any disk (the Dashboard asks for many files, and an offline
    /// network folder makes every look-up wait): rows with the damaged file's fingerprint that are not missing and
    /// whose last known size and time still match their own baseline. A restore re-reads the copy before using it.
    /// </summary>
    public IReadOnlyList<CopyCandidate> FindRecorded(FileRecord damaged)
    {
        ArgumentNullException.ThrowIfNull(damaged);
        return IsEmptyBaseline(damaged) ? [] : Ordered(Recorded(damaged, database.Roots.List().ToDictionary(r => r.Id)));
    }

    /// <summary>Re-reads a candidate and confirms its fingerprint equals <paramref name="expectedHash"/>.</summary>
    public async Task<bool> VerifyAsync(CopyCandidate candidate, byte[] expectedHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        IntegrityResult result = await IntegrityReader.ReadAsync(candidate.FullPath, fileSystem, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Hash!.AsSpan().SequenceEqual(expectedHash);
    }

    public static string FullPath(RootRecord root, FileRecord row)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(row);
        return string.IsNullOrEmpty(row.RelDir) ? Path.Combine(root.Path, row.Name) : Path.Combine(root.Path, row.RelDir, row.Name);
    }

    /// <summary>A fingerprint of no bytes, or none at all: nothing can be restored from it.</summary>
    private static bool IsEmptyBaseline(FileRecord file) => file.Hash is not { Length: > 0 } || file.HashSize is not > 0;

    private List<CopyCandidate> Recorded(FileRecord damaged, Dictionary<long, RootRecord> roots)
    {
        List<CopyCandidate> found = [];
        foreach (FileRecord row in database.Files.FindByHash(damaged.Hash!))
        {
            if (row.Id == damaged.Id || row.State == FileState.Missing || !roots.TryGetValue(row.RootId, out RootRecord? root)
                || IsEmptyBaseline(row) || row.HashMtimeUtc is not long mtime)
            {
                continue;
            }

            // The index's own record of the copy must still match the copy's baseline (an hour apart is a FAT time shift).
            if (ChangeClassifier.StatSame(row.HashSize!.Value, mtime, row.Size, row.MtimeUtc, fatLikeVolume: true))
            {
                found.Add(new CopyCandidate(row, root, FullPath(root, row), FingerprintMatch: true));
            }
        }

        return found;
    }

    /// <summary>Verified copies first, then backup roots (they exist to serve as sources), then by path.</summary>
    private static List<CopyCandidate> Ordered(IEnumerable<CopyCandidate> candidates) =>
        [.. candidates.OrderByDescending(c => c.FingerprintMatch).ThenBy(c => c.Root.Role == RootRole.Backup ? 0 : 1).ThenBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase)];
}
