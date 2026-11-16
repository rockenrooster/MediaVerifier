using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

/// <summary>What one <see cref="FindingStore.ReplaceForFile"/> call changed, for run counters.</summary>
public readonly record struct FindingChange(int New, int Updated, int Resolved, int NewCritical = 0, int NewHigh = 0, int NewMedium = 0, int NewLow = 0);

/// <summary>
/// Findings with the lifecycle of subplan 02 (task S-05): unique per (file, rule); re-detection updates
/// evidence and severity; evaluated rules that no longer fire resolve as "not reproduced"; ignored
/// findings stay ignored until the file's content changes; a resolved finding that fires again reopens.
/// </summary>
public sealed class FindingStore(Database database)
{
    public const string NotReproduced = "not reproduced";
    public const string IgnoredByUser = "ignored";
    public const string IgnoredScopePrefix = "ignore:";

    private const string Columns =
        "id, file_id, rule_id, category, severity, confidence, summary, evidence_json, status, first_run, last_run, first_utc, last_utc, resolved_utc, resolution";

    public Task<FindingChange> ReplaceForFileAsync(long fileId, long runId, IReadOnlyCollection<string> evaluatedRules, IReadOnlyList<Finding> raised, bool contentChanged, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(c => ReplaceForFile(c, fileId, runId, evaluatedRules, raised, contentChanged), ChangeFlags.Findings | ChangeFlags.Files, cancellationToken);

    /// <summary>
    /// Applies one evaluation of a file in a single operation: upserts <paramref name="raised"/>, resolves
    /// open or ignored findings of <paramref name="evaluatedRules"/> that were not raised, leaves other
    /// rules alone (a shallower scan cannot judge them), then recomputes the file's summary columns.
    /// </summary>
    public static FindingChange ReplaceForFile(WriteContext context, long fileId, long runId, IReadOnlyCollection<string> evaluatedRules, IReadOnlyList<Finding> raised, bool contentChanged)
    {
        long now = context.NowTicks;
        Dictionary<string, (long Id, FindingStatus Status, string? Resolution)> existing = new(StringComparer.Ordinal);
        SqliteCommand select = context.Command("SELECT id, rule_id, status, resolution FROM findings WHERE file_id = $file");
        select.Add("$file", fileId);
        using (SqliteDataReader reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                existing[reader.GetString(1)] = (reader.GetInt64(0), (FindingStatus)reader.GetInt32(2), reader.GetStringOrNull(3));
            }
        }

        ScopeIgnores scope = LoadScopeIgnores(context, fileId);
        int added = 0;
        int updated = 0;
        int resolved = 0;
        int[] newBySeverity = new int[5];
        HashSet<string> raisedRules = new(StringComparer.Ordinal);
        foreach (Finding finding in raised)
        {
            if (!raisedRules.Add(finding.RuleId))
            {
                continue;
            }

            RuleDefinition rule = RuleCatalog.Get(finding.RuleId);
            string evidence = EvidenceJson.Serialize(finding.Evidence);
            (long ScopeId, bool Ignored) scoped = scope.Match(finding.RuleId);
            if (existing.TryGetValue(finding.RuleId, out (long Id, FindingStatus Status, string? Resolution) row))
            {
                FindingStatus status = row.Status switch
                {
                    FindingStatus.Ignored when !contentChanged => FindingStatus.Ignored,
                    _ => scoped.Ignored ? FindingStatus.Ignored : FindingStatus.Open,
                };
                bool reopened = row.Status == FindingStatus.Resolved || (row.Status == FindingStatus.Ignored && status == FindingStatus.Open);
                string? resolution = status == FindingStatus.Ignored
                    ? (row.Status == FindingStatus.Ignored ? row.Resolution : IgnoredScopePrefix + scoped.ScopeId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : null;
                SqliteCommand update = context.Command(
                    "UPDATE findings SET category = $category, severity = $severity, confidence = $confidence, summary = $summary, evidence_json = $evidence, " +
                    "status = $status, last_run = $run, last_utc = $now, resolved_utc = NULL, resolution = $resolution, " +
                    "first_run = CASE WHEN $reopened THEN $run ELSE first_run END, first_utc = CASE WHEN $reopened THEN $now ELSE first_utc END " +
                    "WHERE id = $id");
                update.Add("$category", (int)rule.Category);
                update.Add("$severity", (int)finding.Severity);
                update.Add("$confidence", (double)finding.Confidence);
                update.Add("$summary", finding.Summary);
                update.Add("$evidence", evidence);
                update.Add("$status", (int)status);
                update.Add("$run", runId);
                update.Add("$now", now);
                update.Add("$resolution", resolution);
                update.Add("$reopened", reopened ? 1 : 0);
                update.Add("$id", row.Id);
                update.ExecuteNonQuery();
                if (reopened && status == FindingStatus.Open)
                {
                    added++;
                    newBySeverity[(int)finding.Severity]++;
                }
                else
                {
                    updated++;
                }
            }
            else
            {
                FindingStatus status = scoped.Ignored ? FindingStatus.Ignored : FindingStatus.Open;
                SqliteCommand insert = context.Command(
                    "INSERT INTO findings(file_id, rule_id, category, severity, confidence, summary, evidence_json, status, first_run, last_run, first_utc, last_utc, resolution) " +
                    "VALUES ($file, $rule, $category, $severity, $confidence, $summary, $evidence, $status, $run, $run, $now, $now, $resolution)");
                insert.Add("$file", fileId);
                insert.Add("$rule", finding.RuleId);
                insert.Add("$category", (int)rule.Category);
                insert.Add("$severity", (int)finding.Severity);
                insert.Add("$confidence", (double)finding.Confidence);
                insert.Add("$summary", finding.Summary);
                insert.Add("$evidence", evidence);
                insert.Add("$status", (int)status);
                insert.Add("$run", runId);
                insert.Add("$now", now);
                insert.Add("$resolution", scoped.Ignored ? IgnoredScopePrefix + scoped.ScopeId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
                insert.ExecuteNonQuery();
                if (status == FindingStatus.Open)
                {
                    added++;
                    newBySeverity[(int)finding.Severity]++;
                }
            }
        }

        foreach ((string ruleId, (long Id, FindingStatus Status, string? Resolution) row) in existing)
        {
            if (raisedRules.Contains(ruleId) || row.Status == FindingStatus.Resolved || !evaluatedRules.Contains(ruleId))
            {
                continue;
            }

            SetResolved(context, row.Id, NotReproduced, now);
            if (row.Status == FindingStatus.Open)
            {
                resolved++;
            }
        }

        RecomputeFileSummary(context, fileId);
        return new FindingChange(added, updated, resolved, newBySeverity[4], newBySeverity[3], newBySeverity[2], newBySeverity[1]);
    }

    /// <summary>Resolves one finding with a reason (an operation id after a repair).</summary>
    public static void Resolve(WriteContext context, long fileId, string ruleId, string resolution)
    {
        SqliteCommand command = context.Command(
            "UPDATE findings SET status = $resolved, resolved_utc = $now, resolution = $resolution WHERE file_id = $file AND rule_id = $rule AND status <> $resolved");
        command.Add("$resolved", (int)FindingStatus.Resolved);
        command.Add("$now", context.NowTicks);
        command.Add("$resolution", resolution);
        command.Add("$file", fileId);
        command.Add("$rule", ruleId);
        command.ExecuteNonQuery();
        RecomputeFileSummary(context, fileId);
    }

    /// <summary>Ignores or reopens findings chosen by the user. Ignored findings leave the file's open counts.</summary>
    public Task SetStatusAsync(IReadOnlyCollection<long> findingIds, FindingStatus status, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => SetStatus(context, findingIds, status), ChangeFlags.Findings | ChangeFlags.Files, cancellationToken);

    public static void SetStatus(WriteContext context, IReadOnlyCollection<long> findingIds, FindingStatus status)
    {
        HashSet<long> files = [];
        SqliteCommand owner = context.Command("SELECT file_id FROM findings WHERE id = $id");
        SqliteCommand update = context.Command(
            "UPDATE findings SET status = $status, resolution = $resolution, resolved_utc = CASE WHEN $status = 2 THEN $now ELSE NULL END WHERE id = $id");
        foreach (long id in findingIds)
        {
            owner.Parameters.Clear();
            owner.Add("$id", id);
            if (owner.ExecuteScalar() is not long fileId)
            {
                continue;
            }

            update.Parameters.Clear();
            update.Add("$status", (int)status);
            update.Add("$resolution", status == FindingStatus.Ignored ? IgnoredByUser : null);
            update.Add("$now", context.NowTicks);
            update.Add("$id", id);
            update.ExecuteNonQuery();
            files.Add(fileId);
        }

        foreach (long fileId in files)
        {
            RecomputeFileSummary(context, fileId);
        }
    }

    /// <summary>Keeps files.worst_severity, open_findings and the Ok/Issues state in step with the findings.</summary>
    public static void RecomputeFileSummary(WriteContext context, long fileId)
    {
        SqliteCommand command = context.Command(
            "UPDATE files SET " +
            "worst_severity = COALESCE((SELECT MAX(severity) FROM findings WHERE file_id = $id AND status = 0), 0), " +
            "open_findings = (SELECT COUNT(*) FROM findings WHERE file_id = $id AND status = 0), " +
            "state = CASE WHEN state IN (1, 2) THEN CASE WHEN EXISTS (SELECT 1 FROM findings WHERE file_id = $id AND status = 0) THEN 2 ELSE 1 END ELSE state END " +
            "WHERE id = $id");
        command.Add("$id", fileId);
        command.ExecuteNonQuery();
    }

    public List<FindingRecord> ForFile(long fileId) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM findings WHERE file_id = $file ORDER BY status, severity DESC, rule_id";
        command.Add("$file", fileId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<FindingRecord> rows = [];
        while (reader.Read())
        {
            rows.Add(Read(reader));
        }

        return rows;
    });

    public FindingRecord? Get(long findingId) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM findings WHERE id = $id";
        command.Add("$id", findingId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    });

    internal static FindingRecord Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        FileId = r.GetInt64(1),
        RuleId = r.GetString(2),
        Category = (FindingCategory)r.GetInt32(3),
        Severity = (Severity)r.GetInt32(4),
        Confidence = (float)r.GetDouble(5),
        Summary = r.GetString(6),
        EvidenceJson = r.GetStringOrNull(7),
        Status = (FindingStatus)r.GetInt32(8),
        FirstRun = r.GetInt64(9),
        LastRun = r.GetInt64(10),
        FirstUtc = r.GetInt64(11),
        LastUtc = r.GetInt64(12),
        ResolvedUtc = r.GetInt64OrNull(13),
        Resolution = r.GetStringOrNull(14),
    };

    private static void SetResolved(WriteContext context, long id, string resolution, long now)
    {
        SqliteCommand command = context.Command("UPDATE findings SET status = $resolved, resolved_utc = $now, resolution = $resolution WHERE id = $id");
        command.Add("$resolved", (int)FindingStatus.Resolved);
        command.Add("$now", now);
        command.Add("$resolution", resolution);
        command.Add("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Folder-scoped ignores that cover a file: by rule, or for all rules (rule_id NULL).</summary>
    private static ScopeIgnores LoadScopeIgnores(WriteContext context, long fileId)
    {
        SqliteCommand command = context.Command(
            "WITH RECURSIVE anc(id, parent) AS (" +
            "  SELECT d.id, d.parent_id FROM dirs d JOIN files f ON f.dir_id = d.id WHERE f.id = $file " +
            "  UNION ALL SELECT d.id, d.parent_id FROM dirs d JOIN anc ON d.id = anc.parent) " +
            "SELECT i.id, i.rule_id FROM ignores i JOIN files f ON f.root_id = i.root_id " +
            "WHERE f.id = $file AND (i.dir_id IS NULL OR i.dir_id IN (SELECT id FROM anc))");
        command.Add("$file", fileId);
        ScopeIgnores scope = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            scope.Add(reader.GetInt64(0), reader.GetStringOrNull(1));
        }

        return scope;
    }

    private sealed class ScopeIgnores
    {
        private readonly Dictionary<string, long> _byRule = new(StringComparer.Ordinal);
        private long? _all;

        public void Add(long id, string? ruleId)
        {
            if (ruleId is null)
            {
                _all ??= id;
            }
            else
            {
                _byRule.TryAdd(ruleId, id);
            }
        }

        public (long ScopeId, bool Ignored) Match(string ruleId)
        {
            if (_byRule.TryGetValue(ruleId, out long id))
            {
                return (id, true);
            }

            return _all is long all ? (all, true) : (0, false);
        }
    }
}
