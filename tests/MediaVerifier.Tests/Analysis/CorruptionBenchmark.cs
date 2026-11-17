using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Analysis;

/// <summary>
/// Task T-05 (subplan 15, Detection-rate benchmark): every corruption-matrix cell repeated at 50 seeded positions,
/// the detection rate per depth and through the fingerprint, written to docs/detection-rates.md
/// (MEDIAVERIFIER_UPDATE_DOCS=1). Targets from the plan are asserted; every other rate may not fall more than five
/// points below the committed report. Healthy fixtures staying clean is CorruptionMatrixTests' job.
/// </summary>
[Trait("Category", "Perf")]
public sealed partial class CorruptionBenchmark(ITestOutputHelper output)
{
    private const int Seeds = 50;
    private const double Tolerance = 5.0;

    /// <summary>Fixture, mutation, a truncation, Quick must catch it (declared length), checksummed format.</summary>
    private static readonly Cell[] Cells =
    [
        new("src16.flac", "flip", Truncation: false, QuickTarget: false, Checksummed: true),
        new("src16.flac", "zero", false, false, true),
        new("src16.flac", "truncate 0.6", true, false, true),
        new("m128.mp3", "random 3000", false, false, false),
        new("m128.mp3", "truncate 0.55", true, true, false),
        new("a128.m4a", "random 1500", false, false, false),
        new("o128.opus", "flip2", false, false, true),
        new("native1080.mp4", "truncate 0.6", true, true, false),
        new("fs.mp4", "truncate 0.6", true, true, false),
        new("av.mp4", "random 2000", false, false, false),
        new("fs.mp4", "random 2000", false, false, false),
        new("fs.mp4", "flip", false, false, false),
        new("t.mkv", "truncate 0.6", true, true, false),
        new("img.jpg", "truncate 0.6", true, true, false),
        new("img.jpg", "zero", false, false, false),
        new("img.jpg", "flip", false, false, false),
        new("img.png", "flip", false, false, true),
        new("img.png", "truncate 0.6", true, true, true),
        new("img.webp", "flip3", false, false, false),
        new("img.webp", "truncate 0.6", true, true, false),
    ];

    private static readonly ScanDepth[] Depths = [ScanDepth.Quick, ScanDepth.Standard, ScanDepth.Deep];

    [FixtureFact]
    public async Task Detection_rates_meet_the_targets_and_do_not_regress()
    {
        List<Rates> measured = [];
        foreach (Cell cell in Cells)
        {
            Rates rates = await MeasureAsync(cell);
            output.WriteLine($"{cell.Fixture} {cell.Mutation}: {rates.Quick:0} / {rates.Standard:0} / {rates.Deep:0} / {(rates.Fingerprint is double f ? f.ToString("0", CultureInfo.InvariantCulture) : "-")}");
            measured.Add(rates);
        }

        string path = RepositoryPaths.Combine("docs", "detection-rates.md");
        Dictionary<(string, string), Rates> committed = File.Exists(path) ? Parse(await File.ReadAllTextAsync(path)) : [];
        string report = Report(measured);
        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_UPDATE_DOCS") == "1")
        {
            await File.WriteAllTextAsync(path, report);
        }

        List<string> failures = [];
        foreach (Rates rates in measured)
        {
            Cell cell = rates.Cell;
            string name = cell.Fixture + " " + cell.Mutation;
            if (cell.Truncation && rates.Standard < 100)
            {
                failures.Add($"{name}: truncation at Standard {rates.Standard:0} %, target 100 %");
            }

            if (cell.QuickTarget && rates.Quick < 100)
            {
                failures.Add($"{name}: truncation with a declared length at Quick {rates.Quick:0} %, target 100 %");
            }

            if (cell.Checksummed && rates.Standard < 100)
            {
                failures.Add($"{name}: checksummed format at Standard {rates.Standard:0} %, target 100 %");
            }

            if (rates.Fingerprint is double fingerprint && fingerprint < 100)
            {
                failures.Add($"{name}: fingerprint {fingerprint:0} %, target 100 %");
            }

            if (committed.TryGetValue((cell.Fixture, cell.Mutation), out Rates? before))
            {
                foreach ((string column, double now, double was) in new[] { ("Quick", rates.Quick, before.Quick), ("Standard", rates.Standard, before.Standard), ("Deep", rates.Deep, before.Deep) })
                {
                    if (now < was - Tolerance)
                    {
                        failures.Add($"{name}: {column} fell from {was:0} % to {now:0} %");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>50 damaged copies in one library, scanned at each depth in turn; then the fingerprint path on 50 more.</summary>
    private async Task<Rates> MeasureAsync(Cell cell)
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        double[] byDepth = new double[Depths.Length];
        using (EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner))
        {
            for (int seed = 1; seed <= Seeds; seed++)
            {
                Apply(FixtureFactory.CopyTo(cell.Fixture, harness.Library, Name(cell, seed)), cell.Mutation, seed);
            }

            await harness.AddRootAsync(harness.Library);
            for (int d = 0; d < Depths.Length; d++)
            {
                await harness.ScanAsync(Depths[d]);
                byDepth[d] = Percent(harness, IsDamage);
            }
        }

        double? fingerprint = null;
        if (!cell.Truncation)
        {
            using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
            List<string> paths = [.. Enumerable.Range(1, Seeds).Select(seed => FixtureFactory.CopyTo(cell.Fixture, harness.Library, Name(cell, seed)))];
            await harness.AddRootAsync(harness.Library);
            await harness.ScanAsync(ScanDepth.Quick);
            for (int i = 0; i < paths.Count; i++)
            {
                int seed = i + 1;
                Mutate.PreserveStat(paths[i], path => Apply(path, cell.Mutation, seed));
            }

            harness.Db.Clock.Advance(TimeSpan.FromDays(3));
            await harness.ScanAsync(ScanDepth.Quick, new ScanOptions { VerifyEverything = true });
            fingerprint = Percent(harness, rule => rule == "INT-ROT");
        }

        return new Rates(cell, byDepth[0], byDepth[1], byDepth[2], fingerprint);
    }

    private static bool IsDamage(string rule) =>
        rule.StartsWith("COR-", StringComparison.Ordinal) || rule is "GEN-UNREADABLE" or "GEN-EMPTY" or "GEN-ZEROFILL" or "GEN-UNRECOGNIZED" or "INT-ROT";

    private static double Percent(EngineHarness harness, Func<string, bool> counts)
    {
        List<FileRecord> files = harness.AllFiles();
        int detected = files.Count(f => harness.Database.Findings.ForFile(f.Id).Any(g => g.Status == FindingStatus.Open && counts(g.RuleId)));
        return 100.0 * detected / Math.Max(1, files.Count);
    }

    private static string Name(Cell cell, int seed) => "s" + seed.ToString("00", CultureInfo.InvariantCulture) + Path.GetExtension(cell.Fixture);

    /// <summary>The mutation at the seed's position in 0.30–0.70; multi-byte flips spread around it.</summary>
    private static void Apply(string path, string mutation, int seed)
    {
        double at = Mutate.Position(seed);
        string[] parts = mutation.Split(' ');
        switch (parts[0])
        {
            case "flip":
                Mutate.FlipByte(path, at);
                break;
            case "flip2":
                Mutate.FlipByte(path, at - 0.05);
                Mutate.FlipByte(path, at + 0.05);
                break;
            case "flip3":
                Mutate.FlipByte(path, at - 0.1);
                Mutate.FlipByte(path, at);
                Mutate.FlipByte(path, at + 0.1);
                break;
            case "zero":
                Mutate.ZeroBlock(path, at);
                break;
            case "random":
                Mutate.RandomBlock(path, at, int.Parse(parts[1], CultureInfo.InvariantCulture), seed);
                break;
            case "truncate":
                // Truncation keeps a fraction around the listed one, so each seed cuts at a different byte.
                Mutate.Truncate(path, double.Parse(parts[1], CultureInfo.InvariantCulture) + ((at - 0.5) / 4));
                break;
            default:
                throw new ArgumentException("Unknown mutation " + mutation);
        }
    }

    private static string Report(IReadOnlyList<Rates> rows)
    {
        StringBuilder text = new();
        text.AppendLine("# Detection rates");
        text.AppendLine();
        text.AppendLine("Generated by `CorruptionBenchmark` (subplan 15, Detection-rate benchmark; task T-05) with");
        text.AppendLine("`MEDIAVERIFIER_UPDATE_DOCS=1 dotnet test --filter FullyQualifiedName~CorruptionBenchmark`. Each cell of the");
        text.AppendLine("corruption matrix is repeated at 50 seeded positions between 30 % and 70 % of the file (truncations cut");
        text.AppendLine("within 5 points of the listed fraction). A file counts as detected when it has an open damage finding");
        text.AppendLine("(`COR-*`, `GEN-UNREADABLE`, `GEN-EMPTY`, `GEN-ZEROFILL`, `GEN-UNRECOGNIZED`) after a scan at that depth;");
        text.AppendLine("depths are cumulative. **Fingerprint** damages a file that already has a baseline, keeps its modified");
        text.AppendLine("time and counts `INT-ROT` after a verification (size-neutral mutations only; a truncation changes the size,");
        text.AppendLine("which is reported as a modified file instead).");
        text.AppendLine();
        text.AppendLine("Targets: truncation 100 % at Standard, and at Quick for formats with a declared length; checksummed");
        text.AppendLine("formats (FLAC, PNG, Ogg) 100 % at Standard; fingerprint 100 %. Every other cell may not fall more than");
        text.AppendLine("five points below this table.");
        text.AppendLine();
        text.AppendLine("| Fixture | Mutation | Quick | Standard | Deep | Fingerprint |");
        text.AppendLine("|---------|----------|------:|---------:|-----:|------------:|");
        foreach (Rates row in rows)
        {
            text.Append("| ").Append(row.Cell.Fixture).Append(" | ").Append(row.Cell.Mutation)
                .Append(" | ").Append(Format(row.Quick)).Append(" | ").Append(Format(row.Standard)).Append(" | ").Append(Format(row.Deep))
                .Append(" | ").Append(row.Fingerprint is double f ? Format(f) : "—").AppendLine(" |");
        }

        return text.ToString();
    }

    private static string Format(double rate) => rate.ToString("0", CultureInfo.InvariantCulture) + " %";

    private static Dictionary<(string, string), Rates> Parse(string report)
    {
        Dictionary<(string, string), Rates> rows = [];
        foreach (Match match in Row().Matches(report))
        {
            Cell cell = Cells.FirstOrDefault(c => c.Fixture == match.Groups[1].Value && c.Mutation == match.Groups[2].Value) ?? new Cell(match.Groups[1].Value, match.Groups[2].Value, false, false, false);
            rows[(cell.Fixture, cell.Mutation)] = new Rates(cell, Value(match.Groups[3]), Value(match.Groups[4]), Value(match.Groups[5]), null);
        }

        return rows;

        static double Value(Group group) => double.Parse(group.Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\| (\S+) \| ([a-z0-9. ]+?) \| (\d+) % \| (\d+) % \| (\d+) % \|", RegexOptions.Multiline)]
    private static partial Regex Row();

    private sealed record Cell(string Fixture, string Mutation, bool Truncation, bool QuickTarget, bool Checksummed);

    private sealed record Rates(Cell Cell, double Quick, double Standard, double Deep, double? Fingerprint);
}
