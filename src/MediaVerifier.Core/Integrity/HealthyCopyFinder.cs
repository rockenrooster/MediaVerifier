using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Integrity;

/// <summary>A possible source to restore a damaged file from.</summary>
public sealed record CopyCandidate(FileRecord File, RootRecord Root, string FullPath, bool FingerprintMatch)
{
    /// <summary>Same name and size only: shown, labelled unverified, never used without a check.</summary>
    public bool Unverified => !FingerprintMatch;
}

/// <summary>
/// Finds healthy copies of a damaged file (task I-09): rows in any root whose fingerprint equals the
/// damaged file's old baseline, that exist, are online and still match their own baseline. A weaker list
/// by name and size under backup roots is offered as unverified. Before use a copy is re-read and its
/// fingerprint compared again.
/// </summary>
public sealed class HealthyCopyFinder(Database database, IMediaFileSystem fileSystem)
{
    public IReadOnlyList<CopyCandidate> Find(FileRecord damaged)
    {
        ArgumentNullException.ThrowIfNull(damaged);
        Dictionary<long, RootRecord> roots = database.Roots.List().ToDictionary(r => r.Id);
        List<CopyCandidate> candidates = [];
        if (damaged.Hash is { Length: > 0 } baseline)
        {
            foreach (FileRecord row in database.Files.FindByHash(baseline))
            {
                if (row.Id == damaged.Id || row.State == FileState.Missing || !roots.TryGetValue(row.RootId, out RootRecord? root))
                {
                    continue;
                }

                string path = FullPath(root, row);
                FileStat? stat = fileSystem.Stat(path);
                if (stat is null || row.HashSize is not long size || row.HashMtimeUtc is not long mtime)
                {
                    continue;
                }

                // The copy must still match its own baseline: a copy that itself changed is no source.
                if (!ChangeClassifier.StatSame(size, mtime, stat.Value.Size, stat.Value.LastWriteUtcTicks, FileSystemFormat.IsFatLike(path)))
                {
                    continue;
                }

                candidates.Add(new CopyCandidate(row, root, path, FingerprintMatch: true));
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

        // Backup roots first: they exist to serve as sources.
        return [.. candidates.OrderByDescending(c => c.FingerprintMatch).ThenBy(c => c.Root.Role == RootRole.Backup ? 0 : 1).ThenBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Re-reads a candidate and confirms its fingerprint equals <paramref name="expectedHash"/>.</summary>
    public async Task<bool> VerifyAsync(CopyCandidate candidate, byte[] expectedHash, CancellationToken cancellationToken = default)
    {
        IntegrityResult result = await IntegrityReader.ReadAsync(candidate.FullPath, fileSystem, cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.Hash!.AsSpan().SequenceEqual(expectedHash);
    }

    public static string FullPath(RootRecord root, FileRecord row) =>
        string.IsNullOrEmpty(row.RelDir) ? Path.Combine(root.Path, row.Name) : Path.Combine(root.Path, row.RelDir, row.Name);
}
