using System.Globalization;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaVerifier.Tests.TestSupport;

public sealed record SyntheticLibraryOptions
{
    public int Seed { get; init; } = 1;
    public int Roots { get; init; } = 2;
    public int Files { get; init; } = 10_000;
    public int FilesPerDirectory { get; init; } = 100;
    public int DirectoriesPerParent { get; init; } = 10;

    /// <summary>Share of files that get at least one open finding.</summary>
    public double FindingRate { get; init; } = 0.02;

    /// <summary>Weights for Critical, High, Medium, Low.</summary>
    public double[] SeverityWeights { get; init; } = [0.01, 0.05, 0.30, 0.64];
}

/// <summary>
/// Deterministic synthetic library for performance tests and query tests (task S-10, T-10): roots,
/// a directory tree, files of every kind and findings by distribution. Writes through the writer in
/// large operations, so a million files take well under a minute.
/// </summary>
public static class SyntheticLibrary
{
    private static readonly string[] PhotoExt = ["jpg", "png", "heic", "webp"];
    private static readonly string[] VideoExt = ["mp4", "mkv", "mov"];
    private static readonly string[] AudioExt = ["flac", "mp3", "m4a"];

    public static async Task<List<long>> GenerateAsync(Database database, SyntheticLibraryOptions options)
    {
        Random random = new(options.Seed);
        List<long> roots = [];
        for (int r = 0; r < options.Roots; r++)
        {
            roots.Add(await database.Roots.AddAsync(new RootRecord
            {
                Path = string.Create(CultureInfo.InvariantCulture, $@"X:\Synthetic{r}"),
                Label = "Synthetic " + r.ToString(CultureInfo.InvariantCulture),
                AddedUtc = database.Clock.UtcNow,
            }));
        }

        RuleDefinition[] rules = [.. RuleCatalog.All.Where(x => x.Milestone is Milestone.M1 or Milestone.M2)];
        const int chunk = 20_000;
        int written = 0;
        while (written < options.Files)
        {
            int count = Math.Min(chunk, options.Files - written);
            int start = written;
            int seed = random.Next();
            await database.Writer.ExecuteAsync(context => WriteChunk(context, roots, start, count, options, rules, new Random(seed)), ChangeFlags.Files | ChangeFlags.Findings);
            written += count;
        }

        await database.Writer.FlushAsync();
        return roots;
    }

    private static int WriteChunk(WriteContext context, List<long> roots, int start, int count, SyntheticLibraryOptions options, RuleDefinition[] rules, Random random)
    {
        long now = context.NowTicks;
        SqliteCommand insertFile = context.Command(
            "INSERT INTO files(root_id, dir_id, name, ext, media_kind, size, mtime_utc, state, worst_severity, open_findings, scanned_depth, first_seen_utc, last_seen_run, " +
            "hash, hash_size, hash_mtime_utc, hash_utc, last_verified_utc, verify_count) " +
            "VALUES ($root, $dir, $name, $ext, $kind, $size, $mtime, $state, $worst, $open, 2, $now, 1, $hash, $size, $mtime, $now, $verified, 1)");
        SqliteCommand insertFinding = context.Command(
            "INSERT INTO findings(file_id, rule_id, category, severity, confidence, summary, evidence_json, status, first_run, last_run, first_utc, last_utc) " +
            "VALUES ($file, $rule, $category, $severity, 1.0, $summary, '{}', 0, 1, 1, $now, $now)");
        byte[] hash = new byte[16];
        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            long root = roots[index % roots.Count];
            int dirIndex = index / options.FilesPerDirectory;
            string relDir = DirectoryPath(dirIndex, options.DirectoriesPerParent);
            long dirId = FileIndex.InternDir(context, root, relDir);
            (MediaKind kind, string ext) = (index % 3) switch
            {
                0 => (MediaKind.Photo, PhotoExt[random.Next(PhotoExt.Length)]),
                1 => (MediaKind.Video, VideoExt[random.Next(VideoExt.Length)]),
                _ => (MediaKind.Audio, AudioExt[random.Next(AudioExt.Length)]),
            };
            bool hasFinding = random.NextDouble() < options.FindingRate;
            Severity severity = hasFinding ? PickSeverity(random, options.SeverityWeights) : Severity.None;
            random.NextBytes(hash);
            insertFile.Parameters.Clear();
            insertFile.Add("$root", root);
            insertFile.Add("$dir", dirId);
            insertFile.Add("$name", string.Create(CultureInfo.InvariantCulture, $"file {index:D7}.{ext}"));
            insertFile.Add("$ext", ext);
            insertFile.Add("$kind", (int)kind);
            insertFile.Add("$size", 100_000L + random.Next(50_000_000));
            insertFile.Add("$mtime", now - random.Next(1_000_000) * TimeSpan.TicksPerSecond);
            insertFile.Add("$state", (int)(hasFinding ? FileState.Issues : FileState.Ok));
            insertFile.Add("$worst", (int)severity);
            insertFile.Add("$open", hasFinding ? 1 : 0);
            insertFile.Add("$now", now);
            insertFile.Add("$hash", hash.ToArray());
            insertFile.Add("$verified", now - random.Next(200) * TimeSpan.TicksPerDay);
            long fileId = context.Insert(insertFile);
            if (hasFinding)
            {
                RuleDefinition rule = PickRule(random, rules, kind, severity);
                insertFinding.Parameters.Clear();
                insertFinding.Add("$file", fileId);
                insertFinding.Add("$rule", rule.Id);
                insertFinding.Add("$category", (int)rule.Category);
                insertFinding.Add("$severity", (int)severity);
                insertFinding.Add("$summary", rule.Title);
                insertFinding.Add("$now", now);
                insertFinding.ExecuteNonQuery();
            }
        }

        return count;
    }

    /// <summary>A balanced tree: directory n lives under directory (n - 1) / fanOut.</summary>
    private static string DirectoryPath(int dirIndex, int fanOut)
    {
        List<string> parts = [];
        int n = dirIndex;
        while (n > 0)
        {
            parts.Add("d" + n.ToString(CultureInfo.InvariantCulture));
            n = (n - 1) / fanOut;
        }

        parts.Reverse();
        return string.Join('\\', parts);
    }

    private static Severity PickSeverity(Random random, double[] weights)
    {
        double roll = random.NextDouble() * weights.Sum();
        Severity[] tiers = [Severity.Critical, Severity.High, Severity.Medium, Severity.Low];
        for (int i = 0; i < tiers.Length; i++)
        {
            roll -= weights[i];
            if (roll <= 0)
            {
                return tiers[i];
            }
        }

        return Severity.Low;
    }

    private static RuleDefinition PickRule(Random random, RuleDefinition[] rules, MediaKind kind, Severity severity)
    {
        RuleDefinition[] fitting = [.. rules.Where(r => r.AppliesTo.Includes(kind) && r.CanReach(severity))];
        return fitting.Length > 0 ? fitting[random.Next(fitting.Length)] : RuleCatalog.Get("COR-DECODE");
    }
}
