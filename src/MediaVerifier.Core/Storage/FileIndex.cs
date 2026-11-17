using MediaVerifier.Core.Model;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

/// <summary>What the enumerator knows about one file: enough to insert or refresh its row.</summary>
public sealed record FileUpsert(
    long RootId,
    string RelDir,
    string Name,
    string Ext,
    MediaKind MediaKind,
    long Size,
    long MtimeUtc,
    long? NtfsFileId,
    long RunId);

/// <summary>Result of one upsert: the row id and whether it was new.</summary>
public readonly record struct FileUpsertResult(long FileId, bool Inserted);

/// <summary>Fields a scan writes back after analysing a file. Null means "leave unchanged".</summary>
public sealed record FileUpdate(long FileId)
{
    public FileState? State { get; init; }
    public SkipReason? SkipReason { get; init; }
    public ScanDepth? ScannedDepth { get; init; }
    public int? DetectorSet { get; init; }
    public long? LastScannedUtc { get; init; }
    public long? Size { get; init; }
    public long? MtimeUtc { get; init; }
    public MediaKind? MediaKind { get; init; }
    public MediaSummary? Media { get; init; }

    /// <summary>A new baseline: fingerprint with the size and modified time it was taken at.</summary>
    public Baseline? Baseline { get; init; }

    public byte[]? StreamHash { get; init; }

    /// <summary>The NTFS file id, for rename detection on the next run.</summary>
    public long? NtfsFileId { get; init; }

    /// <summary>The fingerprint was re-read and matched: bump the verification count.</summary>
    public long? VerifiedUtc { get; init; }
}

public sealed record Baseline(byte[] Hash, long Size, long MtimeUtc, long HashUtc);

/// <summary>
/// The file and directory index (task S-04). Directories are interned per root by exact relative path
/// (case-sensitive, no NOCASE); file rows are keyed by (directory, name).
/// </summary>
public sealed class FileIndex(Database database)
{
    private const string DirCacheKey = "dirs";
    private const string FileColumns =
        "f.id, f.root_id, f.dir_id, f.name, f.ext, f.media_kind, f.size, f.mtime_utc, f.ntfs_file_id, f.state, f.skip_reason, " +
        "f.worst_severity, f.open_findings, f.scanned_depth, f.detector_set, f.first_seen_utc, f.last_seen_run, f.last_scanned_utc, " +
        "f.hash, f.hash_size, f.hash_mtime_utc, f.hash_utc, f.stream_hash, f.last_verified_utc, f.verify_count, " +
        "f.container, f.video_codec, f.audio_codec, f.width, f.height, f.duration_ms, f.bitrate, f.sample_rate, f.channels, f.bit_depth, f.fps_milli, d.rel_path";

    /// <summary>Inserts new rows and refreshes existing ones (size, time, last seen). One operation per call.</summary>
    public Task<FileUpsertResult[]> UpsertAsync(IReadOnlyList<FileUpsert> files, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => Upsert(context, files), ChangeFlags.Files, cancellationToken);

    public static FileUpsertResult[] Upsert(WriteContext context, IReadOnlyList<FileUpsert> files)
    {
        FileUpsertResult[] results = new FileUpsertResult[files.Count];
        long now = context.NowTicks;
        for (int i = 0; i < files.Count; i++)
        {
            FileUpsert file = files[i];
            long dirId = InternDir(context, file.RootId, file.RelDir);
            SqliteCommand find = context.Command("SELECT id, media_kind FROM files WHERE dir_id = $dir AND name = $name");
            find.Add("$dir", dirId);
            find.Add("$name", file.Name);
            long id = 0;
            long oldKind = 0;
            using (SqliteDataReader row = find.ExecuteReader())
            {
                if (row.Read())
                {
                    id = row.GetInt64(0);
                    oldKind = row.GetInt64(1);
                }
            }

            if (id != 0)
            {
                if (oldKind != (long)file.MediaKind)
                {
                    SetFindingsKind(context, id, file.MediaKind);
                }

                SqliteCommand update = context.Command(
                    "UPDATE files SET size = $size, mtime_utc = $mtime, ntfs_file_id = COALESCE($ntfs, ntfs_file_id), last_seen_run = $run, " +
                    "media_kind = $kind, ext = $ext WHERE id = $id");
                update.Add("$size", file.Size);
                update.Add("$mtime", file.MtimeUtc);
                update.Add("$ntfs", file.NtfsFileId);
                update.Add("$run", file.RunId);
                update.Add("$kind", (int)file.MediaKind);
                update.Add("$ext", file.Ext);
                update.Add("$id", id);
                update.ExecuteNonQuery();
                results[i] = new FileUpsertResult(id, Inserted: false);
            }
            else
            {
                SqliteCommand insert = context.Command(
                    "INSERT INTO files(root_id, dir_id, name, ext, media_kind, size, mtime_utc, ntfs_file_id, state, first_seen_utc, last_seen_run) " +
                    "VALUES ($root, $dir, $name, $ext, $kind, $size, $mtime, $ntfs, 0, $now, $run)");
                insert.Add("$root", file.RootId);
                insert.Add("$dir", dirId);
                insert.Add("$name", file.Name);
                insert.Add("$ext", file.Ext);
                insert.Add("$kind", (int)file.MediaKind);
                insert.Add("$size", file.Size);
                insert.Add("$mtime", file.MtimeUtc);
                insert.Add("$ntfs", file.NtfsFileId);
                insert.Add("$now", now);
                insert.Add("$run", file.RunId);
                long newId = context.Insert(insert);
                results[i] = new FileUpsertResult(newId, Inserted: true);
            }
        }

        return results;
    }

    /// <summary>Returns the directory's id, creating it and any missing parents. Case-exact.</summary>
    public static long InternDir(WriteContext context, long rootId, string relDir)
    {
        relDir = NormalizeRelDir(relDir);
        Dictionary<(long, string), long> cache = DirCache(context);
        if (cache.TryGetValue((rootId, relDir), out long cached))
        {
            return cached;
        }

        SqliteCommand find = context.Command("SELECT id FROM dirs WHERE root_id = $root AND rel_path = $rel");
        find.Add("$root", rootId);
        find.Add("$rel", relDir);
        if (find.ExecuteScalar() is long existing)
        {
            cache[(rootId, relDir)] = existing;
            return existing;
        }

        long? parentId = null;
        string name = relDir;
        if (relDir.Length > 0)
        {
            int slash = relDir.LastIndexOf('\\');
            string parent = slash < 0 ? "" : relDir[..slash];
            name = slash < 0 ? relDir : relDir[(slash + 1)..];
            parentId = InternDir(context, rootId, parent);
        }

        SqliteCommand insert = context.Command("INSERT INTO dirs(root_id, parent_id, rel_path, name) VALUES ($root, $parent, $rel, $name)");
        insert.Add("$root", rootId);
        insert.Add("$parent", parentId);
        insert.Add("$rel", relDir);
        insert.Add("$name", name);
        long id = context.Insert(insert);
        cache[(rootId, relDir)] = id;
        return id;
    }

    /// <summary>Directory paths use backslashes, no leading or trailing separator; "" is the root itself.</summary>
    public static string NormalizeRelDir(string relDir) =>
        relDir.Replace('/', '\\').Trim('\\');

    public Task ApplyUpdateAsync(FileUpdate update, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => ApplyUpdate(context, update), ChangeFlags.Files, cancellationToken);

    public static void ApplyUpdate(WriteContext context, FileUpdate update)
    {
        SqliteCommand command = context.Command(
            "UPDATE files SET " +
            "state = COALESCE($state, state), skip_reason = COALESCE($skip, skip_reason), scanned_depth = COALESCE($depth, scanned_depth), " +
            "detector_set = COALESCE($detectors, detector_set), last_scanned_utc = COALESCE($scanned, last_scanned_utc), " +
            "size = COALESCE($size, size), mtime_utc = COALESCE($mtime, mtime_utc), media_kind = COALESCE($kind, media_kind), ntfs_file_id = COALESCE($ntfs, ntfs_file_id), " +
            "hash = CASE WHEN $hasBaseline THEN $hash ELSE hash END, hash_size = CASE WHEN $hasBaseline THEN $hashSize ELSE hash_size END, " +
            "hash_mtime_utc = CASE WHEN $hasBaseline THEN $hashMtime ELSE hash_mtime_utc END, hash_utc = CASE WHEN $hasBaseline THEN $hashUtc ELSE hash_utc END, " +
            "stream_hash = COALESCE($streamHash, stream_hash), " +
            "last_verified_utc = COALESCE($verified, last_verified_utc), verify_count = verify_count + CASE WHEN $verified IS NULL THEN 0 ELSE 1 END, " +
            "container = CASE WHEN $hasMedia THEN $container ELSE container END, video_codec = CASE WHEN $hasMedia THEN $vcodec ELSE video_codec END, " +
            "audio_codec = CASE WHEN $hasMedia THEN $acodec ELSE audio_codec END, width = CASE WHEN $hasMedia THEN $width ELSE width END, " +
            "height = CASE WHEN $hasMedia THEN $height ELSE height END, duration_ms = CASE WHEN $hasMedia THEN $duration ELSE duration_ms END, " +
            "bitrate = CASE WHEN $hasMedia THEN $bitrate ELSE bitrate END, sample_rate = CASE WHEN $hasMedia THEN $rate ELSE sample_rate END, " +
            "channels = CASE WHEN $hasMedia THEN $channels ELSE channels END, bit_depth = CASE WHEN $hasMedia THEN $bits ELSE bit_depth END, " +
            "fps_milli = CASE WHEN $hasMedia THEN $fps ELSE fps_milli END " +
            "WHERE id = $id");
        command.Add("$state", update.State is null ? null : (int)update.State);
        command.Add("$skip", update.SkipReason is null ? null : (int)update.SkipReason);
        command.Add("$depth", update.ScannedDepth is null ? null : (int)update.ScannedDepth);
        command.Add("$detectors", update.DetectorSet);
        command.Add("$scanned", update.LastScannedUtc);
        command.Add("$size", update.Size);
        command.Add("$mtime", update.MtimeUtc);
        command.Add("$kind", update.MediaKind is null ? null : (int)update.MediaKind);
        command.Add("$ntfs", update.NtfsFileId);
        command.Add("$hasBaseline", update.Baseline is not null ? 1 : 0);
        command.Add("$hash", update.Baseline?.Hash);
        command.Add("$hashSize", update.Baseline?.Size);
        command.Add("$hashMtime", update.Baseline?.MtimeUtc);
        command.Add("$hashUtc", update.Baseline?.HashUtc);
        command.Add("$streamHash", update.StreamHash);
        command.Add("$verified", update.VerifiedUtc);
        MediaSummary? media = update.Media;
        command.Add("$hasMedia", media is not null ? 1 : 0);
        command.Add("$container", media?.Container);
        command.Add("$vcodec", media?.VideoCodec);
        command.Add("$acodec", media?.AudioCodec);
        command.Add("$width", media?.Width);
        command.Add("$height", media?.Height);
        command.Add("$duration", media?.DurationMs);
        command.Add("$bitrate", media?.Bitrate);
        command.Add("$rate", media?.SampleRate);
        command.Add("$channels", media?.Channels);
        command.Add("$bits", media?.BitDepth);
        command.Add("$fps", media?.FpsMilli);
        command.Add("$id", update.FileId);
        command.ExecuteNonQuery();
        if (update.MediaKind is MediaKind kind)
        {
            SetFindingsKind(context, update.FileId, kind);
        }
    }

    /// <summary>Moves a row to a new directory and name (rename or move detection); baseline and findings stay.</summary>
    public static void Move(WriteContext context, long fileId, long rootId, string newRelDir, string newName)
    {
        long dirId = InternDir(context, rootId, newRelDir);
        SqliteCommand command = context.Command("UPDATE files SET dir_id = $dir, name = $name, root_id = $root WHERE id = $id");
        command.Add("$dir", dirId);
        command.Add("$name", newName);
        command.Add("$root", rootId);
        command.Add("$id", fileId);
        command.ExecuteNonQuery();

        // The findings' copy of the name orders the Issues list (schema v2).
        SqliteCommand findings = context.Command("UPDATE findings SET file_name = $name WHERE file_id = $id AND file_name <> $name");
        findings.Add("$name", newName);
        findings.Add("$id", fileId);
        findings.ExecuteNonQuery();
    }

    /// <summary>Keeps the findings' copy of the media kind (schema v2) equal to the file's.</summary>
    private static void SetFindingsKind(WriteContext context, long fileId, MediaKind kind)
    {
        SqliteCommand command = context.Command("UPDATE findings SET media_kind = $kind WHERE file_id = $id AND media_kind <> $kind");
        command.Add("$kind", (int)kind);
        command.Add("$id", fileId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks rows of an online root that this run did not see as Missing, except in directories that
    /// failed to list. Returns the ids that went missing.
    /// </summary>
    public static List<long> MarkMissing(WriteContext context, long rootId, long runId, IReadOnlyCollection<long> unlistedDirIds)
    {
        List<long> missing = [];
        SqliteCommand select = context.Command(
            "SELECT id, dir_id FROM files WHERE root_id = $root AND last_seen_run <> $run AND state <> $missing");
        select.Add("$root", rootId);
        select.Add("$run", runId);
        select.Add("$missing", (int)FileState.Missing);
        using (SqliteDataReader reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!unlistedDirIds.Contains(reader.GetInt64(1)))
                {
                    missing.Add(reader.GetInt64(0));
                }
            }
        }

        SqliteCommand mark = context.Command("UPDATE files SET state = $missing WHERE id = $id");
        foreach (long id in missing)
        {
            mark.Parameters.Clear();
            mark.Add("$missing", (int)FileState.Missing);
            mark.Add("$id", id);
            mark.ExecuteNonQuery();
        }

        return missing;
    }

    /// <summary>Records that the run saw these rows (missing-file detection relies on it).</summary>
    public static void MarkSeen(WriteContext context, IReadOnlyList<long> fileIds, long runId)
    {
        SqliteCommand command = context.Command("UPDATE files SET last_seen_run = $run WHERE id = $id");
        foreach (long id in fileIds)
        {
            command.Parameters.Clear();
            command.Add("$run", runId);
            command.Add("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Sets the Skipped state and reason (subplan 05: a skipped file keeps its row; it is not a finding).</summary>
    public static void MarkSkipped(WriteContext context, long fileId, SkipReason reason, long runId)
    {
        SqliteCommand command = context.Command("UPDATE files SET state = $skipped, skip_reason = $reason, last_seen_run = $run WHERE id = $id");
        command.Add("$skipped", (int)FileState.Skipped);
        command.Add("$reason", (int)reason);
        command.Add("$run", runId);
        command.Add("$id", fileId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks rows inside the scope that this run did not see as Missing (subplan 05): only within
    /// <paramref name="scopeRelDir"/> (the whole root when empty), never inside a directory that failed to
    /// list. Directories that vanished entirely count as seen and empty. Returns the ids that went missing.
    /// </summary>
    public static List<long> MarkMissingInScope(WriteContext context, long rootId, long runId, string scopeRelDir, IReadOnlyCollection<string> unlistedRelDirs)
    {
        scopeRelDir = NormalizeRelDir(scopeRelDir);
        SqliteCommand select = context.Command(
            "SELECT f.id, d.rel_path FROM files f JOIN dirs d ON d.id = f.dir_id WHERE f.root_id = $root AND f.last_seen_run <> $run AND f.state <> $missing");
        select.Add("$root", rootId);
        select.Add("$run", runId);
        select.Add("$missing", (int)FileState.Missing);
        List<long> missing = [];
        using (SqliteDataReader reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                string rel = reader.GetString(1);
                if (Within(rel, scopeRelDir) && !unlistedRelDirs.Any(u => Within(rel, NormalizeRelDir(u))))
                {
                    missing.Add(reader.GetInt64(0));
                }
            }
        }

        SqliteCommand mark = context.Command("UPDATE files SET state = $missing WHERE id = $id");
        foreach (long id in missing)
        {
            mark.Parameters.Clear();
            mark.Add("$missing", (int)FileState.Missing);
            mark.Add("$id", id);
            mark.ExecuteNonQuery();
        }

        return missing;
    }

    /// <summary>True when <paramref name="relDir"/> is <paramref name="scope"/> or below it (case-exact).</summary>
    public static bool Within(string relDir, string scope) =>
        scope.Length == 0 || string.Equals(relDir, scope, StringComparison.Ordinal) || relDir.StartsWith(scope + "\\", StringComparison.Ordinal);

    /// <summary>Rows of a root not seen by a run, with what move detection needs.</summary>
    public List<FileRecord> NotSeen(long rootId, long runId) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + FileColumns + " FROM files f JOIN dirs d ON d.id = f.dir_id WHERE f.root_id = $root AND f.last_seen_run <> $run AND f.state <> $missing";
        command.Add("$root", rootId);
        command.Add("$run", runId);
        command.Add("$missing", (int)FileState.Missing);
        using SqliteDataReader reader = command.ExecuteReader();
        List<FileRecord> rows = [];
        while (reader.Read())
        {
            rows.Add(ReadFile(reader));
        }

        return rows;
    });

    /// <summary>Rows first seen at or after <paramref name="sinceUtc"/> with a given fingerprint (fingerprint move fallback).</summary>
    public List<FileRecord> NewWithHash(long rootId, byte[] hash, long sinceUtc) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + FileColumns + " FROM files f JOIN dirs d ON d.id = f.dir_id WHERE f.root_id = $root AND f.hash = $hash AND f.first_seen_utc >= $since";
        command.Add("$root", rootId);
        command.Add("$hash", hash);
        command.Add("$since", sinceUtc);
        using SqliteDataReader reader = command.ExecuteReader();
        List<FileRecord> rows = [];
        while (reader.Read())
        {
            rows.Add(ReadFile(reader));
        }

        return rows;
    });

    /// <summary>Number of rows in a root that are not Missing (the base for RUN-MASS-MISSING).</summary>
    public long CountPresent(long rootId) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files WHERE root_id = $root AND state <> $missing";
        command.Add("$root", rootId);
        command.Add("$missing", (int)FileState.Missing);
        return (long)command.ExecuteScalar()!;
    });

    /// <summary>Deletes a row (the duplicate left by a fingerprint-detected move).</summary>
    public static void Delete(WriteContext context, long fileId)
    {
        SqliteCommand command = context.Command("DELETE FROM files WHERE id = $id");
        command.Add("$id", fileId);
        command.ExecuteNonQuery();
    }

    public FileRecord? Get(long fileId) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + FileColumns + " FROM files f JOIN dirs d ON d.id = f.dir_id WHERE f.id = $id";
        command.Add("$id", fileId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    });

    public FileRecord? Find(long rootId, string relDir, string name) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + FileColumns + " FROM files f JOIN dirs d ON d.id = f.dir_id WHERE d.root_id = $root AND d.rel_path = $rel AND f.name = $name";
        command.Add("$root", rootId);
        command.Add("$rel", NormalizeRelDir(relDir));
        command.Add("$name", name);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    });

    /// <summary>All rows of one directory, for reconciliation (one lookup query per directory).</summary>
    public List<FileRecord> InDirectory(long rootId, string relDir) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + FileColumns + " FROM files f JOIN dirs d ON d.id = f.dir_id WHERE d.root_id = $root AND d.rel_path = $rel";
        command.Add("$root", rootId);
        command.Add("$rel", NormalizeRelDir(relDir));
        using SqliteDataReader reader = command.ExecuteReader();
        List<FileRecord> rows = [];
        while (reader.Read())
        {
            rows.Add(ReadFile(reader));
        }

        return rows;
    });

    public List<FileRecord> FindByHash(byte[] hash) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + FileColumns + " FROM files f JOIN dirs d ON d.id = f.dir_id WHERE f.hash = $hash";
        command.Add("$hash", hash);
        using SqliteDataReader reader = command.ExecuteReader();
        List<FileRecord> rows = [];
        while (reader.Read())
        {
            rows.Add(ReadFile(reader));
        }

        return rows;
    });

    public List<FileRecord> GetMany(IReadOnlyCollection<long> ids) => database.Read(connection =>
    {
        List<FileRecord> rows = new(ids.Count);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + FileColumns + " FROM files f JOIN dirs d ON d.id = f.dir_id WHERE f.id IN (" + SqlHelpers.InList() + ")";
        foreach (ArraySegment<long> chunk in SqlHelpers.Chunks([.. ids]))
        {
            command.Parameters.Clear();
            SqlHelpers.BindChunk(command, chunk);
            using SqliteDataReader reader = command.ExecuteReader();
            HashSet<long> seen = [];
            while (reader.Read())
            {
                FileRecord row = ReadFile(reader);
                if (seen.Add(row.Id))
                {
                    rows.Add(row);
                }
            }
        }

        return rows;
    });

    public long Count() => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files";
        return (long)command.ExecuteScalar()!;
    });

    internal static FileRecord ReadFile(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        RootId = r.GetInt64(1),
        DirId = r.GetInt64(2),
        Name = r.GetString(3),
        Ext = r.GetString(4),
        MediaKind = (MediaKind)r.GetInt32(5),
        Size = r.GetInt64(6),
        MtimeUtc = r.GetInt64(7),
        NtfsFileId = r.GetInt64OrNull(8),
        State = (FileState)r.GetInt32(9),
        SkipReason = (SkipReason)r.GetInt32(10),
        WorstSeverity = (Severity)r.GetInt32(11),
        OpenFindings = r.GetInt32(12),
        ScannedDepth = (ScanDepth)r.GetInt32(13),
        DetectorSet = r.GetInt32(14),
        FirstSeenUtc = r.GetInt64(15),
        LastSeenRun = r.GetInt64(16),
        LastScannedUtc = r.GetInt64OrNull(17),
        Hash = r.GetBlobOrNull(18),
        HashSize = r.GetInt64OrNull(19),
        HashMtimeUtc = r.GetInt64OrNull(20),
        HashUtc = r.GetInt64OrNull(21),
        StreamHash = r.GetBlobOrNull(22),
        LastVerifiedUtc = r.GetInt64OrNull(23),
        VerifyCount = r.GetInt32(24),
        Media = new MediaSummary(
            r.GetStringOrNull(25), r.GetStringOrNull(26), r.GetStringOrNull(27), r.GetInt32OrNull(28), r.GetInt32OrNull(29),
            r.GetInt64OrNull(30), r.GetInt64OrNull(31), r.GetInt32OrNull(32), r.GetInt32OrNull(33), r.GetInt32OrNull(34), r.GetInt32OrNull(35)),
        RelDir = r.GetString(36),
    };

    private static Dictionary<(long, string), long> DirCache(WriteContext context)
    {
        if (!context.Caches.TryGetValue(DirCacheKey, out object? cache))
        {
            cache = new Dictionary<(long, string), long>();
            context.Caches[DirCacheKey] = cache;
        }

        return (Dictionary<(long, string), long>)cache;
    }
}
