using System.Text;
using MediaVerifier.Core.Model;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

public enum FindingSort
{
    Severity,
    Name,
    Folder,
    Found,
    Rule,
    Size,
}

public enum FileSort
{
    Name,
    Severity,
    Size,
    Modified,
    State,
}

/// <summary>Which findings an Issues view shows. Null members mean "no restriction".</summary>
public sealed record FindingFilter
{
    public FindingStatus Status { get; init; } = FindingStatus.Open;
    public IReadOnlyCollection<Severity>? Severities { get; init; }
    public IReadOnlyCollection<MediaKind>? Kinds { get; init; }
    public IReadOnlyCollection<FindingCategory>? Categories { get; init; }
    public IReadOnlyCollection<string>? RuleIds { get; init; }
    public long? RootId { get; init; }

    /// <summary>A directory; findings in it and below it match.</summary>
    public long? DirId { get; init; }

    /// <summary>Case-insensitive (ASCII) substring of the file name or its folder path.</summary>
    public string? Text { get; init; }

    /// <summary>Only findings first raised in this run ("new since last scan").</summary>
    public long? FirstRun { get; init; }

    public static FindingFilter OpenAll { get; } = new();
}

/// <summary>Which files a Library → Browse view shows.</summary>
public sealed record FileFilter
{
    public long? RootId { get; init; }
    public long? DirId { get; init; }

    /// <summary>Only the directory itself (true) or its whole subtree (false).</summary>
    public bool DirectOnly { get; init; } = true;

    public IReadOnlyCollection<FileState>? States { get; init; }
    public IReadOnlyCollection<MediaKind>? Kinds { get; init; }
    public Severity? MinSeverity { get; init; }
    public string? Text { get; init; }
}

/// <summary>One Issues list row: the finding plus what the row shows about its file.</summary>
public sealed record FindingRow(
    long Id,
    long FileId,
    string RuleId,
    FindingCategory Category,
    Severity Severity,
    float Confidence,
    string Summary,
    FindingStatus Status,
    long FirstUtc,
    long LastUtc,
    string FileName,
    string RelDir,
    long RootId,
    string RootPath,
    MediaKind Kind,
    long Size,
    string? EvidenceJson);

public sealed record FileRow(
    long Id,
    long RootId,
    long DirId,
    string Name,
    string RelDir,
    MediaKind Kind,
    long Size,
    long MtimeUtc,
    FileState State,
    SkipReason SkipReason,
    Severity WorstSeverity,
    int OpenFindings,
    long? LastVerifiedUtc,
    int VerifyCount,
    long? LastScannedUtc);

/// <summary>Open findings per tier.</summary>
public sealed record SeverityCounts(int Critical, int High, int Medium, int Low)
{
    public static SeverityCounts Zero { get; } = new(0, 0, 0, 0);

    public int Total => Critical + High + Medium + Low;

    /// <summary>Open findings at Medium or above (the Issues badge).</summary>
    public int MediumOrAbove => Critical + High + Medium;

    public Severity Worst => Critical > 0 ? Severity.Critical : High > 0 ? Severity.High : Medium > 0 ? Severity.Medium : Low > 0 ? Severity.Low : Severity.None;

    public int this[Severity severity] => severity switch
    {
        Severity.Critical => Critical,
        Severity.High => High,
        Severity.Medium => Medium,
        Severity.Low => Low,
        _ => 0,
    };
}

/// <summary>Per media kind: files, bytes and open findings, plus open findings per tier for the bar.</summary>
public sealed record KindSummary(MediaKind Kind, long Files, long Bytes, int Problems, SeverityCounts Tiers, long HealthyFiles);

/// <summary>The dashboard's health and protection numbers (subplan 02, Health metrics).</summary>
public sealed record HealthSnapshot(
    long FilesIndexed,
    long FilesScanned,
    long FilesHealthy,
    long FilesNeedingAttention,
    long FilesWithBaseline,
    long FilesVerifiedInPeriod,
    SeverityCounts Open)
{
    /// <summary>Files with no open finding at Medium or above ÷ files scanned.</summary>
    public double HealthyPercent => FilesScanned == 0 ? 0 : 100.0 * FilesHealthy / FilesScanned;

    /// <summary>Files verified within the scrub period ÷ files with a baseline.</summary>
    public double ProtectedPercent => FilesWithBaseline == 0 ? 0 : 100.0 * FilesVerifiedInPeriod / FilesWithBaseline;

    public long FilesOverdue => FilesWithBaseline - FilesVerifiedInPeriod;
}

/// <summary>A folder with its subtree's roll-up (Library → Browse tree).</summary>
public sealed class FolderNode
{
    public required long Id { get; init; }
    public required long? ParentId { get; init; }
    public required string Name { get; init; }
    public required string RelPath { get; init; }
    public long Files { get; set; }
    public long Bytes { get; set; }
    public int OpenFindings { get; set; }
    public Severity Worst { get; set; }
    public List<FolderNode> Children { get; } = [];
}

/// <summary>
/// Read-side queries for the UI (task S-07): ID snapshots that filter and sort without loading rows,
/// row fetch by IDs in chunks, grouped counts and the folder roll-up. Always called off the UI thread.
/// </summary>
public sealed class LibraryQueries(Database database)
{
    public long[] FindingIds(FindingFilter filter, FindingSort sort = FindingSort.Severity, bool descending = false) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        bool needsDirs = filter.DirId is not null || !string.IsNullOrWhiteSpace(filter.Text) || sort == FindingSort.Folder;
        StringBuilder sql = new("SELECT fi.id FROM findings fi JOIN files f ON f.id = fi.file_id");
        if (needsDirs)
        {
            sql.Append(" JOIN dirs d ON d.id = f.dir_id");
        }

        AppendFindingWhere(sql, command, filter);
        sql.Append(" ORDER BY ").Append(FindingOrder(sort, descending));
        command.CommandText = sql.ToString();
        return ReadIds(command);
    });

    public List<FindingRow> FindingRows(IReadOnlyList<long> ids) => database.Read(connection =>
    {
        Dictionary<long, FindingRow> byId = new(ids.Count);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT fi.id, fi.file_id, fi.rule_id, fi.category, fi.severity, fi.confidence, fi.summary, fi.status, fi.first_utc, fi.last_utc, " +
            "f.name, d.rel_path, f.root_id, r.path, f.media_kind, f.size, fi.evidence_json " +
            "FROM findings fi JOIN files f ON f.id = fi.file_id JOIN dirs d ON d.id = f.dir_id JOIN roots r ON r.id = f.root_id " +
            "WHERE fi.id IN (" + SqlHelpers.InList() + ")";
        foreach (ArraySegment<long> chunk in SqlHelpers.Chunks([.. ids]))
        {
            command.Parameters.Clear();
            SqlHelpers.BindChunk(command, chunk);
            using SqliteDataReader r = command.ExecuteReader();
            while (r.Read())
            {
                FindingRow row = new(
                    r.GetInt64(0), r.GetInt64(1), r.GetString(2), (FindingCategory)r.GetInt32(3), (Severity)r.GetInt32(4), (float)r.GetDouble(5),
                    r.GetString(6), (FindingStatus)r.GetInt32(7), r.GetInt64(8), r.GetInt64(9), r.GetString(10), r.GetString(11), r.GetInt64(12),
                    r.GetString(13), (MediaKind)r.GetInt32(14), r.GetInt64(15), r.GetStringOrNull(16));
                byId[row.Id] = row;
            }
        }

        List<FindingRow> rows = new(ids.Count);
        foreach (long id in ids)
        {
            if (byId.TryGetValue(id, out FindingRow? row))
            {
                rows.Add(row);
            }
        }

        return rows;
    });

    /// <summary>Open findings per tier within a filter (the severity chips and dashboard tiles).</summary>
    public SeverityCounts SeverityCounts(FindingFilter? filter = null) => database.Read(connection =>
    {
        filter ??= FindingFilter.OpenAll;
        using SqliteCommand command = connection.CreateCommand();
        StringBuilder sql = new("SELECT fi.severity, COUNT(*) FROM findings fi");
        if (NeedsFileJoin(filter))
        {
            sql.Append(" JOIN files f ON f.id = fi.file_id");
        }

        if (filter.DirId is not null || !string.IsNullOrWhiteSpace(filter.Text))
        {
            sql.Append(" JOIN dirs d ON d.id = f.dir_id");
        }

        AppendFindingWhere(sql, command, filter with { Severities = null });
        sql.Append(" GROUP BY fi.severity");
        command.CommandText = sql.ToString();
        int[] counts = new int[5];
        using SqliteDataReader r = command.ExecuteReader();
        while (r.Read())
        {
            int tier = r.GetInt32(0);
            if (tier is >= 0 and <= 4)
            {
                counts[tier] = r.GetInt32(1);
            }
        }

        return new SeverityCounts(counts[4], counts[3], counts[2], counts[1]);
    });

    /// <summary>Open findings per rule within a filter (the rule drop-down and grouping headers).</summary>
    public Dictionary<string, int> CountsByRule(FindingFilter? filter = null) => database.Read(connection =>
    {
        filter ??= FindingFilter.OpenAll;
        using SqliteCommand command = connection.CreateCommand();
        StringBuilder sql = new("SELECT fi.rule_id, COUNT(*) FROM findings fi JOIN files f ON f.id = fi.file_id");
        if (filter.DirId is not null || !string.IsNullOrWhiteSpace(filter.Text))
        {
            sql.Append(" JOIN dirs d ON d.id = f.dir_id");
        }

        AppendFindingWhere(sql, command, filter with { RuleIds = null });
        sql.Append(" GROUP BY fi.rule_id");
        command.CommandText = sql.ToString();
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        using SqliteDataReader r = command.ExecuteReader();
        while (r.Read())
        {
            counts[r.GetString(0)] = r.GetInt32(1);
        }

        return counts;
    });

    public HealthSnapshot Health(TimeSpan scrubPeriod, DateTimeOffset nowUtc) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), " +
            "SUM(CASE WHEN state IN (1, 2) THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN state IN (1, 2) AND worst_severity < 2 THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN state IN (1, 2) AND worst_severity >= 2 THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN hash IS NOT NULL AND state <> 3 THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN hash IS NOT NULL AND state <> 3 AND COALESCE(last_verified_utc, hash_utc) >= $since THEN 1 ELSE 0 END) " +
            "FROM files WHERE state <> 3";
        command.Add("$since", (nowUtc - scrubPeriod).UtcTicks);
        long indexed = 0, scanned = 0, healthy = 0, attention = 0, baseline = 0, verified = 0;
        using (SqliteDataReader r = command.ExecuteReader())
        {
            if (r.Read())
            {
                indexed = r.GetInt64(0);
                scanned = r.GetInt64OrNull(1) ?? 0;
                healthy = r.GetInt64OrNull(2) ?? 0;
                attention = r.GetInt64OrNull(3) ?? 0;
                baseline = r.GetInt64OrNull(4) ?? 0;
                verified = r.GetInt64OrNull(5) ?? 0;
            }
        }

        return new HealthSnapshot(indexed, scanned, healthy, attention, baseline, verified, CountOpen(connection));
    });

    /// <summary>Files, bytes and open findings per media kind (dashboard "By media type").</summary>
    public List<KindSummary> KindSummaries() => database.Read(connection =>
    {
        Dictionary<MediaKind, (long Files, long Bytes, long Healthy)> files = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT media_kind, COUNT(*), SUM(size), SUM(CASE WHEN worst_severity = 0 THEN 1 ELSE 0 END) FROM files WHERE state <> 3 GROUP BY media_kind";
            using SqliteDataReader r = command.ExecuteReader();
            while (r.Read())
            {
                files[(MediaKind)r.GetInt32(0)] = (r.GetInt64(1), r.GetInt64OrNull(2) ?? 0, r.GetInt64OrNull(3) ?? 0);
            }
        }

        Dictionary<MediaKind, int[]> tiers = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT f.media_kind, fi.severity, COUNT(*) FROM findings fi JOIN files f ON f.id = fi.file_id WHERE fi.status = 0 AND f.state <> 3 GROUP BY f.media_kind, fi.severity";
            using SqliteDataReader r = command.ExecuteReader();
            while (r.Read())
            {
                MediaKind kind = (MediaKind)r.GetInt32(0);
                if (!tiers.TryGetValue(kind, out int[]? counts))
                {
                    counts = new int[5];
                    tiers[kind] = counts;
                }

                int tier = r.GetInt32(1);
                if (tier is >= 0 and <= 4)
                {
                    counts[tier] += r.GetInt32(2);
                }
            }
        }

        List<KindSummary> result = [];
        foreach (MediaKind kind in new[] { MediaKind.Photo, MediaKind.Video, MediaKind.Audio })
        {
            (long Files, long Bytes, long Healthy) f = files.TryGetValue(kind, out var v) ? v : (0, 0, 0);
            int[] t = tiers.TryGetValue(kind, out int[]? c) ? c : new int[5];
            SeverityCounts counts = new(t[4], t[3], t[2], t[1]);
            result.Add(new KindSummary(kind, f.Files, f.Bytes, counts.Total, counts, f.Healthy));
        }

        return result;
    });

    public long[] FileIds(FileFilter filter, FileSort sort = FileSort.Name, bool descending = false) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        bool needsDirs = (filter.DirId is not null && !filter.DirectOnly) || !string.IsNullOrWhiteSpace(filter.Text);
        StringBuilder sql = new(needsDirs ? "SELECT f.id FROM files f JOIN dirs d ON d.id = f.dir_id WHERE 1 = 1" : "SELECT f.id FROM files f WHERE 1 = 1");
        AppendFileWhere(sql, command, filter);
        string direction = descending ? "DESC" : "ASC";
        sql.Append(" ORDER BY ").Append(sort switch
        {
            FileSort.Severity => $"f.worst_severity {(descending ? "ASC" : "DESC")}, f.name",
            FileSort.Size => $"f.size {direction}, f.name",
            FileSort.Modified => $"f.mtime_utc {direction}, f.name",
            FileSort.State => $"f.state {direction}, f.name",
            _ => $"f.name {direction}",
        });
        command.CommandText = sql.ToString();
        return ReadIds(command);
    });

    public List<FileRow> FileRows(IReadOnlyList<long> ids) => database.Read(connection =>
    {
        Dictionary<long, FileRow> byId = new(ids.Count);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT f.id, f.root_id, f.dir_id, f.name, d.rel_path, f.media_kind, f.size, f.mtime_utc, f.state, f.skip_reason, f.worst_severity, " +
            "f.open_findings, f.last_verified_utc, f.verify_count, f.last_scanned_utc FROM files f JOIN dirs d ON d.id = f.dir_id WHERE f.id IN (" + SqlHelpers.InList() + ")";
        foreach (ArraySegment<long> chunk in SqlHelpers.Chunks([.. ids]))
        {
            command.Parameters.Clear();
            SqlHelpers.BindChunk(command, chunk);
            using SqliteDataReader r = command.ExecuteReader();
            while (r.Read())
            {
                FileRow row = new(
                    r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), (MediaKind)r.GetInt32(5), r.GetInt64(6), r.GetInt64(7),
                    (FileState)r.GetInt32(8), (SkipReason)r.GetInt32(9), (Severity)r.GetInt32(10), r.GetInt32(11), r.GetInt64OrNull(12), r.GetInt32(13), r.GetInt64OrNull(14));
                byId[row.Id] = row;
            }
        }

        List<FileRow> rows = new(ids.Count);
        foreach (long id in ids)
        {
            if (byId.TryGetValue(id, out FileRow? row))
            {
                rows.Add(row);
            }
        }

        return rows;
    });

    /// <summary>
    /// The folder tree of one root with counts and the worst open tier rolled up from the files below
    /// each folder (one grouped query, then summed in memory).
    /// </summary>
    public List<FolderNode> FolderRollup(long rootId) => database.Read(connection =>
    {
        Dictionary<long, FolderNode> nodes = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, parent_id, name, rel_path FROM dirs WHERE root_id = $root";
            command.Add("$root", rootId);
            using SqliteDataReader r = command.ExecuteReader();
            while (r.Read())
            {
                FolderNode node = new() { Id = r.GetInt64(0), ParentId = r.GetInt64OrNull(1), Name = r.GetString(2), RelPath = r.GetString(3) };
                nodes[node.Id] = node;
            }
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT dir_id, COUNT(*), SUM(size), SUM(open_findings), MAX(worst_severity) FROM files WHERE root_id = $root AND state <> 3 GROUP BY dir_id";
            command.Add("$root", rootId);
            using SqliteDataReader r = command.ExecuteReader();
            while (r.Read())
            {
                if (nodes.TryGetValue(r.GetInt64(0), out FolderNode? node))
                {
                    node.Files = r.GetInt64(1);
                    node.Bytes = r.GetInt64OrNull(2) ?? 0;
                    node.OpenFindings = r.GetInt32(3);
                    node.Worst = (Severity)r.GetInt32(4);
                }
            }
        }

        List<FolderNode> roots = [];
        foreach (FolderNode node in nodes.Values)
        {
            if (node.ParentId is long parent && nodes.TryGetValue(parent, out FolderNode? parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        foreach (FolderNode node in roots)
        {
            Accumulate(node);
        }

        return roots;
    });

    /// <summary>Plan of a hot query, for tests that check indexes are used.</summary>
    internal string ExplainFindingIds(FindingFilter filter) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        StringBuilder sql = new("EXPLAIN QUERY PLAN SELECT fi.id FROM findings fi JOIN files f ON f.id = fi.file_id JOIN dirs d ON d.id = f.dir_id");
        AppendFindingWhere(sql, command, filter);
        command.CommandText = sql.ToString();
        StringBuilder plan = new();
        using SqliteDataReader r = command.ExecuteReader();
        while (r.Read())
        {
            plan.AppendLine(r.GetString(3));
        }

        return plan.ToString();
    });

    private static void Accumulate(FolderNode node)
    {
        foreach (FolderNode child in node.Children)
        {
            Accumulate(child);
            node.Files += child.Files;
            node.Bytes += child.Bytes;
            node.OpenFindings += child.OpenFindings;
            if (child.Worst > node.Worst)
            {
                node.Worst = child.Worst;
            }
        }

        node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    private static SeverityCounts CountOpen(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT severity, COUNT(*) FROM findings WHERE status = 0 GROUP BY severity";
        int[] counts = new int[5];
        using SqliteDataReader r = command.ExecuteReader();
        while (r.Read())
        {
            int tier = r.GetInt32(0);
            if (tier is >= 0 and <= 4)
            {
                counts[tier] = r.GetInt32(1);
            }
        }

        return new SeverityCounts(counts[4], counts[3], counts[2], counts[1]);
    }

    private static bool NeedsFileJoin(FindingFilter filter) =>
        filter.Kinds is not null || filter.RootId is not null || filter.DirId is not null || !string.IsNullOrEmpty(filter.Text);

    private static void AppendFindingWhere(StringBuilder sql, SqliteCommand command, FindingFilter filter)
    {
        sql.Append(" WHERE fi.status = $status");
        command.Add("$status", (int)filter.Status);
        AppendIn(sql, command, "fi.severity", "$sev", filter.Severities?.Select(s => (long)s));
        AppendIn(sql, command, "fi.category", "$cat", filter.Categories?.Select(c => (long)c));
        if (filter.RuleIds is { Count: > 0 } rules)
        {
            sql.Append(" AND fi.rule_id IN (");
            int i = 0;
            foreach (string rule in rules)
            {
                string name = "$rule" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                sql.Append(i == 0 ? "" : ",").Append(name);
                command.Add(name, rule);
                i++;
            }

            sql.Append(')');
        }

        if (filter.FirstRun is long run)
        {
            sql.Append(" AND fi.first_run = $firstRun");
            command.Add("$firstRun", run);
        }

        AppendIn(sql, command, "f.media_kind", "$kind", filter.Kinds?.Select(k => (long)k));
        if (filter.RootId is long root)
        {
            sql.Append(" AND f.root_id = $root");
            command.Add("$root", root);
        }

        if (filter.DirId is long dir)
        {
            AppendSubtree(sql, command, dir);
        }

        AppendText(sql, command, filter.Text);
    }

    private static void AppendFileWhere(StringBuilder sql, SqliteCommand command, FileFilter filter)
    {
        if (filter.RootId is long root)
        {
            sql.Append(" AND f.root_id = $root");
            command.Add("$root", root);
        }

        if (filter.DirId is long dir)
        {
            if (filter.DirectOnly)
            {
                sql.Append(" AND f.dir_id = $dir");
                command.Add("$dir", dir);
            }
            else
            {
                AppendSubtree(sql, command, dir);
            }
        }

        AppendIn(sql, command, "f.state", "$state", filter.States?.Select(s => (long)s));
        AppendIn(sql, command, "f.media_kind", "$kind", filter.Kinds?.Select(k => (long)k));
        if (filter.MinSeverity is Severity min)
        {
            sql.Append(" AND f.worst_severity >= $minSev");
            command.Add("$minSev", (int)min);
        }

        AppendText(sql, command, filter.Text);
    }

    private static void AppendSubtree(StringBuilder sql, SqliteCommand command, long dirId)
    {
        sql.Append(" AND d.root_id = (SELECT root_id FROM dirs WHERE id = $subDir) AND (d.id = $subDir OR (SELECT rel_path FROM dirs WHERE id = $subDir) = '' " +
                   "OR (d.rel_path >= (SELECT rel_path FROM dirs WHERE id = $subDir) || '\\' AND d.rel_path < (SELECT rel_path FROM dirs WHERE id = $subDir) || ']'))");
        command.Add("$subDir", dirId);
    }

    private static void AppendText(StringBuilder sql, SqliteCommand command, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string escaped = text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        sql.Append(" AND (f.name LIKE $text ESCAPE '\\' OR d.rel_path LIKE $text ESCAPE '\\')");
        command.Add("$text", "%" + escaped + "%");
    }

    private static void AppendIn(StringBuilder sql, SqliteCommand command, string column, string prefix, IEnumerable<long>? values)
    {
        if (values is null)
        {
            return;
        }

        long[] list = [.. values];
        if (list.Length == 0)
        {
            sql.Append(" AND 0 = 1");
            return;
        }

        sql.Append(" AND ").Append(column).Append(" IN (");
        for (int i = 0; i < list.Length; i++)
        {
            string name = prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sql.Append(i == 0 ? "" : ",").Append(name);
            command.Add(name, list[i]);
        }

        sql.Append(')');
    }

    private static string FindingOrder(FindingSort sort, bool descending)
    {
        string direction = descending ? "DESC" : "ASC";
        return sort switch
        {
            FindingSort.Name => $"f.name {direction}, fi.severity DESC",
            FindingSort.Folder => $"d.rel_path {direction}, f.name",
            FindingSort.Found => $"fi.first_utc {(descending ? "ASC" : "DESC")}, fi.id DESC",
            FindingSort.Rule => $"fi.rule_id {direction}, f.name",
            FindingSort.Size => $"f.size {(descending ? "ASC" : "DESC")}, f.name",
            _ => $"fi.severity {(descending ? "ASC" : "DESC")}, fi.first_utc DESC, fi.id DESC",
        };
    }

    private static long[] ReadIds(SqliteCommand command)
    {
        List<long> ids = [];
        using SqliteDataReader r = command.ExecuteReader();
        while (r.Read())
        {
            ids.Add(r.GetInt64(0));
        }

        return [.. ids];
    }
}
