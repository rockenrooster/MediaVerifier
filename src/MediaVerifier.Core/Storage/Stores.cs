using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

/// <summary>Library and backup folders (task S-04).</summary>
public sealed class RootStore(Database database)
{
    private const string Columns =
        "id, path, label, role, media_mask, default_depth, include_patterns, exclude_patterns, volume_id, storage_class, enabled, added_utc, last_online_utc";

    public Task<long> AddAsync(RootRecord root, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command(
                    "INSERT INTO roots(path, label, role, media_mask, default_depth, include_patterns, exclude_patterns, volume_id, storage_class, enabled, added_utc, last_online_utc) " +
                    "VALUES ($path, $label, $role, $mask, $depth, $include, $exclude, $volume, $class, $enabled, $added, $online)");
                Bind(command, root);
                command.Add("$added", root.AddedUtc == default ? context.NowTicks : root.AddedUtc.UtcTicks);
                return context.Insert(command);
            },
            ChangeFlags.Roots,
            cancellationToken);

    public Task UpdateAsync(RootRecord root, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command(
                    "UPDATE roots SET path = $path, label = $label, role = $role, media_mask = $mask, default_depth = $depth, include_patterns = $include, " +
                    "exclude_patterns = $exclude, volume_id = $volume, storage_class = $class, enabled = $enabled, last_online_utc = $online WHERE id = $id");
                Bind(command, root);
                command.Add("$id", root.Id);
                command.ExecuteNonQuery();
            },
            ChangeFlags.Roots,
            cancellationToken);

    /// <summary>Removes the root; its directories, files, findings, artifacts, events and ignores go with it.</summary>
    public Task RemoveAsync(long rootId, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command("DELETE FROM roots WHERE id = $id");
                command.Add("$id", rootId);
                command.ExecuteNonQuery();
                context.Caches.Clear();
            },
            ChangeFlags.All,
            cancellationToken);

    public Task MarkOnlineAsync(long rootId, DateTimeOffset utc, string? volumeId, StorageClass storageClass, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command(
                    "UPDATE roots SET last_online_utc = $utc, volume_id = COALESCE($volume, volume_id), storage_class = $class WHERE id = $id");
                command.Add("$utc", utc.UtcTicks);
                command.Add("$volume", volumeId);
                command.Add("$class", (int)storageClass);
                command.Add("$id", rootId);
                command.ExecuteNonQuery();
            },
            ChangeFlags.Roots,
            cancellationToken);

    public List<RootRecord> List() => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM roots ORDER BY role, path";
        using SqliteDataReader reader = command.ExecuteReader();
        List<RootRecord> roots = [];
        while (reader.Read())
        {
            roots.Add(Read(reader));
        }

        return roots;
    });

    public RootRecord? Get(long id) => List().FirstOrDefault(r => r.Id == id);

    private static void Bind(SqliteCommand command, RootRecord root)
    {
        command.Add("$path", root.Path);
        command.Add("$label", root.Label);
        command.Add("$role", (int)root.Role);
        command.Add("$mask", (int)root.MediaMask);
        command.Add("$depth", (int)root.DefaultDepth);
        command.Add("$include", root.IncludePatternsJson);
        command.Add("$exclude", root.ExcludePatternsJson);
        command.Add("$volume", root.VolumeId);
        command.Add("$class", (int)root.StorageClass);
        command.Add("$enabled", root.Enabled ? 1 : 0);
        command.Add("$online", root.LastOnlineUtc?.UtcTicks);
    }

    private static RootRecord Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Path = r.GetString(1),
        Label = r.GetStringOrNull(2),
        Role = (RootRole)r.GetInt32(3),
        MediaMask = (MediaKindMask)r.GetInt32(4),
        DefaultDepth = (ScanDepth)r.GetInt32(5),
        IncludePatternsJson = r.GetStringOrNull(6),
        ExcludePatternsJson = r.GetStringOrNull(7),
        VolumeId = r.GetStringOrNull(8),
        StorageClass = (StorageClass)r.GetInt32(9),
        Enabled = r.GetInt32(10) != 0,
        AddedUtc = Ticks.To(r.GetInt64(11)),
        LastOnlineUtc = Ticks.To(r.GetInt64OrNull(12)),
    };
}

/// <summary>Scan runs (task S-06).</summary>
public sealed class RunStore(Database database)
{
    private const string Columns =
        "id, plan_id, trigger, depth, scope_json, status, started_utc, ended_utc, files_seen, files_analysed, files_verified, bytes_read, new_findings, resolved_findings, errors, alerts_json, summary_json";

    public Task<long> BeginAsync(RunTrigger trigger, ScanDepth depth, string scopeJson, long? planId, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command(
                    "INSERT INTO runs(plan_id, trigger, depth, scope_json, status, started_utc) VALUES ($plan, $trigger, $depth, $scope, 0, $now)");
                command.Add("$plan", planId);
                command.Add("$trigger", (int)trigger);
                command.Add("$depth", (int)depth);
                command.Add("$scope", scopeJson);
                command.Add("$now", context.NowTicks);
                long id = context.Insert(command);
                if (planId is long plan)
                {
                    SqliteCommand link = context.Command("UPDATE plans SET last_run_id = $run WHERE id = $plan");
                    link.Add("$run", id);
                    link.Add("$plan", plan);
                    link.ExecuteNonQuery();
                }

                return id;
            },
            ChangeFlags.Runs,
            cancellationToken);

    public ValueTask CheckpointAsync(long runId, RunCounters counters, CancellationToken cancellationToken = default) =>
        database.Writer.PostAsync(context => Checkpoint(context, runId, counters), ChangeFlags.Runs, cancellationToken);

    public static void Checkpoint(WriteContext context, long runId, RunCounters counters)
    {
        SqliteCommand command = context.Command(
            "UPDATE runs SET files_seen = $seen, files_analysed = $analysed, files_verified = $verified, bytes_read = $bytes, " +
            "new_findings = $new, resolved_findings = $resolved, errors = $errors WHERE id = $id");
        command.Add("$seen", counters.FilesSeen);
        command.Add("$analysed", counters.FilesAnalysed);
        command.Add("$verified", counters.FilesVerified);
        command.Add("$bytes", counters.BytesRead);
        command.Add("$new", counters.NewFindings);
        command.Add("$resolved", counters.ResolvedFindings);
        command.Add("$errors", counters.Errors);
        command.Add("$id", runId);
        command.ExecuteNonQuery();
    }

    public Task EndAsync(long runId, RunStatus status, RunCounters counters, string? alertsJson, string? summaryJson, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                Checkpoint(context, runId, counters);
                SqliteCommand command = context.Command(
                    "UPDATE runs SET status = $status, ended_utc = MAX($now, started_utc), alerts_json = $alerts, summary_json = $summary WHERE id = $id");
                command.Add("$status", (int)status);
                command.Add("$now", context.NowTicks);
                command.Add("$alerts", alertsJson);
                command.Add("$summary", summaryJson);
                command.Add("$id", runId);
                command.ExecuteNonQuery();
            },
            ChangeFlags.Runs,
            cancellationToken);

    public RunRecord? Get(long runId) => List(int.MaxValue, runId).FirstOrDefault();

    public List<RunRecord> List(int limit = 50) => List(limit, null);

    /// <summary>The most recent run left Interrupted, which the Scans page offers to resume.</summary>
    public RunRecord? LatestInterrupted() => List(1, null, RunStatus.Interrupted).FirstOrDefault();

    /// <summary>The most recent run still marked Running (a background run's checkpoints, subplan 13).</summary>
    public RunRecord? LatestRunning() => List(1, null, RunStatus.Running).FirstOrDefault();

    private List<RunRecord> List(int limit, long? id, RunStatus? status = null) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM runs WHERE ($id IS NULL OR id = $id) AND ($status IS NULL OR status = $status) ORDER BY id DESC LIMIT $limit";
        command.Add("$id", id);
        command.Add("$status", status is null ? null : (int)status);
        command.Add("$limit", limit);
        using SqliteDataReader r = command.ExecuteReader();
        List<RunRecord> runs = [];
        while (r.Read())
        {
            runs.Add(new RunRecord
            {
                Id = r.GetInt64(0),
                PlanId = r.GetInt64OrNull(1),
                Trigger = (RunTrigger)r.GetInt32(2),
                Depth = (ScanDepth)r.GetInt32(3),
                ScopeJson = r.GetString(4),
                Status = (RunStatus)r.GetInt32(5),
                StartedUtc = r.GetInt64(6),
                EndedUtc = r.GetInt64OrNull(7),
                FilesSeen = r.GetInt64(8),
                FilesAnalysed = r.GetInt64(9),
                FilesVerified = r.GetInt64(10),
                BytesRead = r.GetInt64(11),
                NewFindings = r.GetInt64(12),
                ResolvedFindings = r.GetInt64(13),
                Errors = r.GetInt64(14),
                AlertsJson = r.GetStringOrNull(15),
                SummaryJson = r.GetStringOrNull(16),
            });
        }

        return runs;
    });
}

/// <summary>Saved scan plans (task S-06; definitions in subplan 13).</summary>
public sealed class PlanStore(Database database)
{
    public Task<long> AddAsync(PlanRecord plan, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command(
                    "INSERT INTO plans(name, enabled, definition_json, task_name, created_utc, updated_utc) VALUES ($name, $enabled, $definition, $task, $now, $now)");
                command.Add("$name", plan.Name);
                command.Add("$enabled", plan.Enabled ? 1 : 0);
                command.Add("$definition", plan.DefinitionJson);
                command.Add("$task", plan.TaskName);
                command.Add("$now", context.NowTicks);
                return context.Insert(command);
            },
            ChangeFlags.Plans,
            cancellationToken);

    public Task UpdateAsync(PlanRecord plan, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command(
                    "UPDATE plans SET name = $name, enabled = $enabled, definition_json = $definition, task_name = $task, updated_utc = $now WHERE id = $id");
                command.Add("$name", plan.Name);
                command.Add("$enabled", plan.Enabled ? 1 : 0);
                command.Add("$definition", plan.DefinitionJson);
                command.Add("$task", plan.TaskName);
                command.Add("$now", context.NowTicks);
                command.Add("$id", plan.Id);
                command.ExecuteNonQuery();
            },
            ChangeFlags.Plans,
            cancellationToken);

    public Task RemoveAsync(long planId, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command("DELETE FROM plans WHERE id = $id");
                command.Add("$id", planId);
                command.ExecuteNonQuery();
            },
            ChangeFlags.Plans,
            cancellationToken);

    public List<PlanRecord> List() => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, enabled, definition_json, task_name, created_utc, updated_utc, last_run_id FROM plans ORDER BY id";
        using SqliteDataReader r = command.ExecuteReader();
        List<PlanRecord> plans = [];
        while (r.Read())
        {
            plans.Add(new PlanRecord
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Enabled = r.GetInt32(2) != 0,
                DefinitionJson = r.GetString(3),
                TaskName = r.GetStringOrNull(4),
                CreatedUtc = r.GetInt64(5),
                UpdatedUtc = r.GetInt64(6),
                LastRunId = r.GetInt64OrNull(7),
            });
        }

        return plans;
    });

    public PlanRecord? Get(long planId) => List().FirstOrDefault(p => p.Id == planId);
}

/// <summary>The remediation journal (task S-06; protocol in subplan 12).</summary>
public sealed class Journal(Database database)
{
    private const string Columns = "id, batch_id, utc, action_id, file_id, status, source_path, result_path, backup_path, undo_json, detail_json";

    public Task<long> NextBatchIdAsync(CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command("SELECT COALESCE(MAX(batch_id), 0) + 1 FROM operations");
                return context.Insert(command);
            },
            ChangeFlags.None,
            cancellationToken);

    public static long Add(WriteContext context, OperationRecord operation)
    {
        SqliteCommand command = context.Command(
            "INSERT INTO operations(batch_id, utc, action_id, file_id, status, source_path, result_path, backup_path, undo_json, detail_json) " +
            "VALUES ($batch, $utc, $action, $file, $status, $source, $result, $backup, $undo, $detail)");
        command.Add("$batch", operation.BatchId);
        command.Add("$utc", operation.Utc == 0 ? context.NowTicks : operation.Utc);
        command.Add("$action", operation.ActionId);
        command.Add("$file", operation.FileId);
        command.Add("$status", (int)operation.Status);
        command.Add("$source", operation.SourcePath);
        command.Add("$result", operation.ResultPath);
        command.Add("$backup", operation.BackupPath);
        command.Add("$undo", operation.UndoJson);
        command.Add("$detail", operation.DetailJson);
        return context.Insert(command);
    }

    public Task<long> AddAsync(OperationRecord operation, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => Add(context, operation), ChangeFlags.Operations, cancellationToken);

    public static void Update(WriteContext context, long operationId, OperationStatus status, string? resultPath, string? backupPath, string? undoJson, string? detailJson)
    {
        SqliteCommand command = context.Command(
            "UPDATE operations SET status = $status, result_path = COALESCE($result, result_path), backup_path = COALESCE($backup, backup_path), " +
            "undo_json = COALESCE($undo, undo_json), detail_json = COALESCE($detail, detail_json) WHERE id = $id");
        command.Add("$status", (int)status);
        command.Add("$result", resultPath);
        command.Add("$backup", backupPath);
        command.Add("$undo", undoJson);
        command.Add("$detail", detailJson);
        command.Add("$id", operationId);
        command.ExecuteNonQuery();
    }

    public Task UpdateAsync(long operationId, OperationStatus status, string? resultPath = null, string? backupPath = null, string? undoJson = null, string? detailJson = null, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => Update(context, operationId, status, resultPath, backupPath, undoJson, detailJson), ChangeFlags.Operations, cancellationToken);

    public List<OperationRecord> List(int limit = 500, long? batchId = null, OperationStatus? status = null) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT " + Columns + " FROM operations WHERE ($batch IS NULL OR batch_id = $batch) AND ($status IS NULL OR status = $status) ORDER BY id DESC LIMIT $limit";
        command.Add("$batch", batchId);
        command.Add("$status", status is null ? null : (int)status);
        command.Add("$limit", limit);
        using SqliteDataReader r = command.ExecuteReader();
        List<OperationRecord> rows = [];
        while (r.Read())
        {
            rows.Add(new OperationRecord
            {
                Id = r.GetInt64(0),
                BatchId = r.GetInt64(1),
                Utc = r.GetInt64(2),
                ActionId = r.GetString(3),
                FileId = r.GetInt64OrNull(4),
                Status = (OperationStatus)r.GetInt32(5),
                SourcePath = r.GetString(6),
                ResultPath = r.GetStringOrNull(7),
                BackupPath = r.GetStringOrNull(8),
                UndoJson = r.GetStringOrNull(9),
                DetailJson = r.GetStringOrNull(10),
            });
        }

        return rows;
    });
}

/// <summary>Compact per-file analysis summaries for re-evaluation without I/O (subplan 05).</summary>
public sealed class ArtifactStore(Database database)
{
    public static void Put(WriteContext context, long fileId, ArtifactKind kind, int providerVersion, string json)
    {
        SqliteCommand command = context.Command(
            "INSERT INTO artifacts(file_id, kind, provider_version, data) VALUES ($file, $kind, $version, $data) " +
            "ON CONFLICT(file_id, kind) DO UPDATE SET provider_version = excluded.provider_version, data = excluded.data");
        command.Add("$file", fileId);
        command.Add("$kind", (int)kind);
        command.Add("$version", providerVersion);
        command.Add("$data", json);
        command.ExecuteNonQuery();
    }

    public static void DeleteAll(WriteContext context, long fileId)
    {
        SqliteCommand command = context.Command("DELETE FROM artifacts WHERE file_id = $file");
        command.Add("$file", fileId);
        command.ExecuteNonQuery();
    }

    public Task PutAsync(long fileId, ArtifactKind kind, int providerVersion, string json, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => Put(context, fileId, kind, providerVersion, json), ChangeFlags.Artifacts, cancellationToken);

    public (int ProviderVersion, string Data)? Get(long fileId, ArtifactKind kind) => database.Read<(int, string)?>(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT provider_version, data FROM artifacts WHERE file_id = $file AND kind = $kind";
        command.Add("$file", fileId);
        command.Add("$kind", (int)kind);
        using SqliteDataReader r = command.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetString(1)) : null;
    });

    /// <summary>All artifacts of one file, keyed by kind.</summary>
    public Dictionary<ArtifactKind, (int ProviderVersion, string Data)> ForFile(long fileId) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind, provider_version, data FROM artifacts WHERE file_id = $file";
        command.Add("$file", fileId);
        using SqliteDataReader r = command.ExecuteReader();
        Dictionary<ArtifactKind, (int, string)> rows = [];
        while (r.Read())
        {
            rows[(ArtifactKind)r.GetInt32(0)] = (r.GetInt32(1), r.GetString(2));
        }

        return rows;
    });
}

/// <summary>The in-app notification centre (subplans 03 and 13).</summary>
public sealed class NotificationStore(Database database)
{
    public static long Add(WriteContext context, NotificationRecord notification)
    {
        SqliteCommand command = context.Command(
            "INSERT INTO notifications(utc, level, title, body, action_uri, is_read) VALUES ($utc, $level, $title, $body, $uri, 0)");
        command.Add("$utc", notification.Utc == 0 ? context.NowTicks : notification.Utc);
        command.Add("$level", (int)notification.Level);
        command.Add("$title", notification.Title);
        command.Add("$body", notification.Body);
        command.Add("$uri", notification.ActionUri);
        return context.Insert(command);
    }

    public Task<long> AddAsync(NotificationRecord notification, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => Add(context, notification), ChangeFlags.Notifications, cancellationToken);

    public Task MarkAllReadAsync(CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => context.Exec("UPDATE notifications SET is_read = 1 WHERE is_read = 0"), ChangeFlags.Notifications, cancellationToken);

    public Task MarkReadAsync(long id, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command("UPDATE notifications SET is_read = 1 WHERE id = $id");
                command.Add("$id", id);
                command.ExecuteNonQuery();
            },
            ChangeFlags.Notifications,
            cancellationToken);

    public Task ClearAllAsync(CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(context => context.Exec("DELETE FROM notifications"), ChangeFlags.Notifications, cancellationToken);

    public List<NotificationRecord> List(int limit = 50) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, utc, level, title, body, action_uri, is_read FROM notifications ORDER BY id DESC LIMIT $limit";
        command.Add("$limit", limit);
        using SqliteDataReader r = command.ExecuteReader();
        List<NotificationRecord> rows = [];
        while (r.Read())
        {
            rows.Add(new NotificationRecord
            {
                Id = r.GetInt64(0),
                Utc = r.GetInt64(1),
                Level = (Severity)r.GetInt32(2),
                Title = r.GetString(3),
                Body = r.GetStringOrNull(4),
                ActionUri = r.GetStringOrNull(5),
                IsRead = r.GetInt32(6) != 0,
            });
        }

        return rows;
    });

    public int UnreadCount() => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notifications WHERE is_read = 0";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });
}

/// <summary>Persists the user's rule overrides (task M-04 through S-06).</summary>
public sealed class RuleSettingsStore(Database database)
{
    public RuleSettings Load() => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT rule_id, enabled, severity_cap, params_json FROM rule_settings";
        using SqliteDataReader r = command.ExecuteReader();
        Dictionary<string, RuleOverride> overrides = new(StringComparer.Ordinal);
        while (r.Read())
        {
            RuleOverride value = new()
            {
                Enabled = r.IsDBNull(1) ? null : r.GetInt32(1) != 0,
                SeverityCap = r.IsDBNull(2) ? null : (Severity)r.GetInt32(2),
            };
            Dictionary<string, object> parameters = EvidenceJson.Deserialize(r.GetStringOrNull(3));
            foreach (string name in parameters.Keys)
            {
                if (EvidenceJson.TryGetDouble(parameters, name, out double parsed))
                {
                    value.Parameters[name] = parsed;
                }
            }

            overrides[r.GetString(0)] = value;
        }

        return RuleSettings.FromOverrides(overrides);
    });

    public Task SaveAsync(RuleSettings settings, CancellationToken cancellationToken = default)
    {
        Dictionary<string, RuleOverride> snapshot = settings.Overrides.ToDictionary(
            p => p.Key,
            p => new RuleOverride { Enabled = p.Value.Enabled, SeverityCap = p.Value.SeverityCap, Parameters = new Dictionary<string, double>(p.Value.Parameters, StringComparer.Ordinal) },
            StringComparer.Ordinal);
        return database.Writer.ExecuteAsync(
            context =>
            {
                context.Exec("DELETE FROM rule_settings");
                SqliteCommand insert = context.Command("INSERT INTO rule_settings(rule_id, enabled, severity_cap, params_json) VALUES ($rule, $enabled, $cap, $params)");
                foreach ((string ruleId, RuleOverride value) in snapshot)
                {
                    insert.Parameters.Clear();
                    insert.Add("$rule", ruleId);
                    insert.Add("$enabled", value.Enabled is bool enabled ? (enabled ? 1 : 0) : null);
                    insert.Add("$cap", value.SeverityCap is Severity cap ? (int)cap : null);
                    insert.Add("$params", value.Parameters.Count == 0 ? null : EvidenceJson.Serialize(value.Parameters.ToDictionary(p => p.Key, p => (object)p.Value, StringComparer.Ordinal)));
                    insert.ExecuteNonQuery();
                }
            },
            ChangeFlags.RuleSettings,
            cancellationToken);
    }
}

/// <summary>Folder-scoped ignores: a rule (or every rule) hidden within a folder subtree.</summary>
public sealed class IgnoreStore(Database database)
{
    /// <summary>Adds the ignore and hides matching open findings in its scope.</summary>
    public Task<long> AddAsync(long rootId, long? dirId, string? ruleId, string? reason, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand insert = context.Command("INSERT INTO ignores(root_id, dir_id, rule_id, reason, created_utc) VALUES ($root, $dir, $rule, $reason, $now)");
                insert.Add("$root", rootId);
                insert.Add("$dir", dirId);
                insert.Add("$rule", ruleId);
                insert.Add("$reason", reason);
                insert.Add("$now", context.NowTicks);
                long id = context.Insert(insert);
                List<long> files = ScopeFiles(context, rootId, dirId);
                SqliteCommand hide = context.Command(
                    "UPDATE findings SET status = 1, resolution = $resolution WHERE file_id = $file AND status = 0 AND ($rule IS NULL OR rule_id = $rule)");
                foreach (long fileId in files)
                {
                    hide.Parameters.Clear();
                    hide.Add("$resolution", FindingStore.IgnoredScopePrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    hide.Add("$file", fileId);
                    hide.Add("$rule", ruleId);
                    if (hide.ExecuteNonQuery() > 0)
                    {
                        FindingStore.RecomputeFileSummary(context, fileId);
                    }
                }

                return id;
            },
            ChangeFlags.Ignores | ChangeFlags.Findings | ChangeFlags.Files,
            cancellationToken);

    /// <summary>Removes the ignore and reopens the findings it hid.</summary>
    public Task RemoveAsync(long ignoreId, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                string marker = FindingStore.IgnoredScopePrefix + ignoreId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                SqliteCommand affected = context.Command("SELECT DISTINCT file_id FROM findings WHERE status = 1 AND resolution = $resolution");
                affected.Add("$resolution", marker);
                HashSet<long> files = [];
                using (SqliteDataReader reader = affected.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        files.Add(reader.GetInt64(0));
                    }
                }

                SqliteCommand reopen = context.Command("UPDATE findings SET status = 0, resolution = NULL WHERE status = 1 AND resolution = $resolution");
                reopen.Add("$resolution", marker);
                reopen.ExecuteNonQuery();

                SqliteCommand delete = context.Command("DELETE FROM ignores WHERE id = $id");
                delete.Add("$id", ignoreId);
                delete.ExecuteNonQuery();
                foreach (long fileId in files)
                {
                    FindingStore.RecomputeFileSummary(context, fileId);
                }
            },
            ChangeFlags.Ignores | ChangeFlags.Findings | ChangeFlags.Files,
            cancellationToken);

    public List<IgnoreRecord> List() => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, root_id, dir_id, rule_id, reason, created_utc FROM ignores ORDER BY id";
        using SqliteDataReader r = command.ExecuteReader();
        List<IgnoreRecord> rows = [];
        while (r.Read())
        {
            rows.Add(new IgnoreRecord(r.GetInt64(0), r.GetInt64(1), r.GetInt64OrNull(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetInt64(5)));
        }

        return rows;
    });

    private static List<long> ScopeFiles(WriteContext context, long rootId, long? dirId)
    {
        SqliteCommand command;
        if (dirId is null)
        {
            command = context.Command("SELECT id FROM files WHERE root_id = $root");
            command.Add("$root", rootId);
        }
        else
        {
            command = context.Command(
                "SELECT f.id FROM files f JOIN dirs d ON d.id = f.dir_id, dirs top WHERE top.id = $dir AND d.root_id = top.root_id " +
                "AND (d.rel_path = top.rel_path OR (top.rel_path = '' ) OR (d.rel_path >= top.rel_path || '\\' AND d.rel_path < top.rel_path || ']'))");
            command.Add("$dir", dirId);
        }

        List<long> ids = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }
}

/// <summary>The per-file history timeline.</summary>
public sealed class FileEventStore(Database database)
{
    public static void Add(WriteContext context, long fileId, long? runId, FileEventKind kind, string? detailJson = null)
    {
        SqliteCommand command = context.Command("INSERT INTO file_events(file_id, utc, run_id, kind, detail_json) VALUES ($file, $utc, $run, $kind, $detail)");
        command.Add("$file", fileId);
        command.Add("$utc", context.NowTicks);
        command.Add("$run", runId);
        command.Add("$kind", (int)kind);
        command.Add("$detail", detailJson);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records a verification, replacing the file's previous "verified" event: the history shows the
    /// latest one and the count lives in files.verify_count.
    /// </summary>
    public static void ReplaceVerified(WriteContext context, long fileId, long? runId)
    {
        SqliteCommand delete = context.Command("DELETE FROM file_events WHERE file_id = $file AND kind = $kind");
        delete.Add("$file", fileId);
        delete.Add("$kind", (int)FileEventKind.Verified);
        delete.ExecuteNonQuery();
        Add(context, fileId, runId, FileEventKind.Verified);
    }

    public List<FileEventRecord> ForFile(long fileId) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, file_id, utc, run_id, kind, detail_json FROM file_events WHERE file_id = $file ORDER BY utc DESC, id DESC";
        command.Add("$file", fileId);
        using SqliteDataReader r = command.ExecuteReader();
        List<FileEventRecord> rows = [];
        while (r.Read())
        {
            rows.Add(new FileEventRecord(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64OrNull(3), (FileEventKind)r.GetInt32(4), r.GetStringOrNull(5)));
        }

        return rows;
    });
}

/// <summary>Small key/value settings kept with the library (estimate averages).</summary>
public sealed class MetaStore(Database database)
{
    public string? Get(string key) => database.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Add("$key", key);
        return command.ExecuteScalar() as string;
    });

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        database.Writer.ExecuteAsync(
            context =>
            {
                SqliteCommand command = context.Command("INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value");
                command.Add("$key", key);
                command.Add("$value", value);
                command.ExecuteNonQuery();
            },
            ChangeFlags.None,
            cancellationToken);
}
