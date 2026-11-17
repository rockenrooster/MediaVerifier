using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Integrity;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Settings;
using MediaVerifier.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Remediation;

public enum RecommendationKind
{
    RotWithCopies,
    RotWithoutCopies,
    MissingIndex,
    WrongExtension,
    FakeLossless,
    UpscaledVideo,
    VerificationOverdue,
    NoBackupFolder,
    NoSchedule,
    ReadErrors,
    InterruptedScan,
}

/// <summary>
/// One piece of advice (subplan 12, Recommendation engine). The window words it; <see cref="FileIds"/> are the files a
/// "Fix all" acts on, <see cref="ActionId"/> the action (null for advice only).
/// </summary>
public sealed record Recommendation(
    RecommendationKind Kind,
    Severity Severity,
    int FileCount,
    long TotalBytes,
    string? ActionId,
    IReadOnlyList<long> FileIds,
    string? Place = null,
    int Covered = 0,
    double Percent = 0,
    long Done = 0,
    long Total = 0);

/// <summary>
/// Turns open findings and library state into at most a handful of recommendations, ordered by severity and then
/// by file count (subplan 12, task R-11). Runs off the UI thread.
/// </summary>
public static class RecommendationEngine
{
    /// <summary>Healthy-copy lookups are capped; the count says "for m of n" when some were not checked.</summary>
    public const int MaxCopyLookups = 200;

    private static readonly string[] Rules = ["INT-ROT", "COR-INDEX", "GEN-EXT-MISMATCH", "AUD-FAKE-LOSSLESS", "VID-UPSCALED", "GEN-UNREADABLE", "GEN-ZEROFILL"];

    public static IReadOnlyList<Recommendation> Build(Database database, ScanSettings settings, IMediaFileSystem fileSystem, DateTimeOffset now, int max = 5)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        List<Recommendation> list = [];
        List<(string Rule, long FileId, long Size, long RootId, long DirId, Severity Severity, MediaKind Kind)> open = OpenFindings(database);
        List<RootRecord> roots = database.Roots.List();
        ILookup<string, (string Rule, long FileId, long Size, long RootId, long DirId, Severity Severity, MediaKind Kind)> byRule = open.ToLookup(o => o.Rule);

        var rot = byRule["INT-ROT"].DistinctBy(o => o.FileId).ToList();
        if (rot.Count > 0)
        {
            int covered = 0;
            string? place = null;
            HealthyCopyFinder finder = new(database, fileSystem);
            foreach (var item in rot.Take(MaxCopyLookups))
            {
                if (database.Files.Get(item.FileId) is FileRecord file && file.Hash is not null
                    && finder.Find(file).FirstOrDefault(c => c.FingerprintMatch) is CopyCandidate copy)
                {
                    covered++;
                    place ??= copy.Root.Label ?? Path.GetFileName(copy.Root.Path.TrimEnd('\\'));
                }
            }

            list.Add(new Recommendation(
                covered > 0 ? RecommendationKind.RotWithCopies : RecommendationKind.RotWithoutCopies,
                Severity.Critical,
                rot.Count,
                rot.Sum(r => r.Size),
                covered == rot.Count ? ActionIds.RestoreCopy : null,
                [.. rot.Select(r => r.FileId)],
                place,
                covered));
        }

        AddForRule(list, byRule["COR-INDEX"].Where(o => o.Kind == MediaKind.Video), RecommendationKind.MissingIndex, ActionIds.Remux);
        AddForRule(list, byRule["GEN-EXT-MISMATCH"], RecommendationKind.WrongExtension, ActionIds.FixExtension);
        var fake = byRule["AUD-FAKE-LOSSLESS"].DistinctBy(o => o.FileId).ToList();
        if (fake.Count > 0)
        {
            // Counted as albums: the folders they are in.
            list.Add(new Recommendation(RecommendationKind.FakeLossless, fake.Max(f => f.Severity), fake.Select(f => f.DirId).Distinct().Count(), fake.Sum(f => f.Size), null, [.. fake.Select(f => f.FileId)]));
        }

        AddForRule(list, byRule["VID-UPSCALED"], RecommendationKind.UpscaledVideo, null);

        var unreadable = byRule["GEN-UNREADABLE"].DistinctBy(o => o.FileId).ToList();
        foreach (var drive in unreadable.GroupBy(u => Drive(roots.FirstOrDefault(r => r.Id == u.RootId)?.Path)))
        {
            list.Add(new Recommendation(RecommendationKind.ReadErrors, Severity.High, drive.Count(), drive.Sum(d => d.Size), ActionIds.CheckDrive, [.. drive.Select(d => d.FileId)], drive.Key));
        }

        bool integrityProblems = rot.Count > 0 || unreadable.Count > 0 || byRule["GEN-ZEROFILL"].Any();
        if (integrityProblems && !roots.Any(r => r.Role == RootRole.Backup))
        {
            list.Add(new Recommendation(RecommendationKind.NoBackupFolder, Severity.Medium, 0, 0, null, []));
        }

        HealthSnapshot health = database.Queries.Health(TimeSpan.FromDays(settings.ScrubPeriodDays), now);
        if (health.FilesWithBaseline > 0)
        {
            double overdue = 1 - ((double)health.FilesVerifiedInPeriod / health.FilesWithBaseline);
            if (overdue >= 0.10)
            {
                list.Add(new Recommendation(RecommendationKind.VerificationOverdue, Severity.Low, (int)(health.FilesWithBaseline - health.FilesVerifiedInPeriod), 0, ActionIds.Verify, [], Percent: overdue));
            }
        }

        List<RunRecord> runs = database.Runs.List(5);
        RunRecord? last = runs.FirstOrDefault(r => r.Status != RunStatus.Running);
        if (last?.Status == RunStatus.Interrupted)
        {
            list.Add(new Recommendation(RecommendationKind.InterruptedScan, Severity.Medium, 0, 0, null, [], Done: last.FilesAnalysed + last.FilesVerified, Total: last.FilesSeen));
        }

        if (database.Plans.List().Count == 0 && runs.Any(r => r.Status is RunStatus.Completed or RunStatus.BudgetReached))
        {
            list.Add(new Recommendation(RecommendationKind.NoSchedule, Severity.Low, 0, 0, null, []));
        }

        return [.. list.OrderByDescending(r => r.Severity).ThenByDescending(r => r.FileCount).Take(max)];
    }

    private static void AddForRule(List<Recommendation> list, IEnumerable<(string Rule, long FileId, long Size, long RootId, long DirId, Severity Severity, MediaKind Kind)> items, RecommendationKind kind, string? action)
    {
        var files = items.DistinctBy(i => i.FileId).ToList();
        if (files.Count > 0)
        {
            list.Add(new Recommendation(kind, files.Max(f => f.Severity), files.Count, files.Sum(f => f.Size), action, [.. files.Select(f => f.FileId)]));
        }
    }

    private static string Drive(string? path) => path is { Length: >= 2 } p && p[1] == ':' ? p[..1].ToUpperInvariant() : path ?? string.Empty;

    private static List<(string, long, long, long, long, Severity, MediaKind)> OpenFindings(Database database) => database.Read(connection =>
    {
        List<(string, long, long, long, long, Severity, MediaKind)> rows = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT f.rule_id, f.file_id, fl.size, fl.root_id, fl.dir_id, f.severity, fl.media_kind FROM findings f JOIN files fl ON fl.id = f.file_id " +
            "WHERE f.status = 0 AND fl.state <> 3 AND f.rule_id IN (" + string.Join(", ", Rules.Select((_, i) => "$r" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")";
        for (int i = 0; i < Rules.Length; i++)
        {
            command.Parameters.AddWithValue("$r" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Rules[i]);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), (Severity)reader.GetInt32(5), (MediaKind)reader.GetInt32(6)));
        }

        return rows;
    });
}
