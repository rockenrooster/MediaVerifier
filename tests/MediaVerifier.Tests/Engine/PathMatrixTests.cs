using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Engine;

/// <summary>
/// Task T-12 (subplan 15, Path matrix): Unicode, emoji, a path over 300 characters, <c>%</c> in names, a folder name
/// with a trailing space, a read-only file and a file locked by another program, scanned by the real engine on NTFS.
/// Every healthy file is analysed without a damage finding; the awkward paths are flagged by GEN-PATH, the locked file
/// is reported and analysed once it is free. FAT and exFAT exceptions are in docs/path-matrix.md.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PathMatrixTests(ITestOutputHelper output)
{
    private static readonly string[] DamageRules = ["COR-DECODE", "COR-TRUNCATED", "COR-CONTAINER", "COR-CHECKSUM", "COR-PARTIAL-IMAGE", "GEN-UNREADABLE", "GEN-UNRECOGNIZED", "GEN-EMPTY"];

    [FixtureFact]
    public async Task Awkward_paths_scan_like_any_other()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string root = harness.Library;
        string longFolder = Path.Combine(root, new string('l', 120), new string('m', 120));
        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            ["unicode"] = Place("src16.flac", Path.Combine(root, "Ünïcödé 日本語 Café"), "Мелодия ñ.flac"),
            ["emoji"] = Place("m128.mp3", Path.Combine(root, "Emoji 🎵"), "🎶 song 👍🏽.mp3"),
            ["long"] = Place("img.jpg", longFolder, new string('n', 60) + ".jpg"),
            ["percent"] = Place("img.png", Path.Combine(root, "100% done"), "a%20b %d %s.png"),
            ["trailing"] = PlaceLiteral("av.mp4", Path.Combine(root, "Trailing "), "clip.mp4"),
            ["readonly"] = Place("a128.m4a", Path.Combine(root, "Read only"), "locked down.m4a"),
            ["locked"] = Place("img.webp", Path.Combine(root, "Busy"), "in use.webp"),
        };
        Assert.True(files["long"].Length > 300, $"{files["long"].Length} characters");
        File.SetAttributes(files["readonly"], FileAttributes.ReadOnly);
        await harness.AddRootAsync(root);

        try
        {
            using (FileStream holder = new(files["locked"], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                RunSummary first = await harness.ScanAsync(ScanDepth.Standard);
                output.WriteLine($"First scan: {first.Status}, {first.Counters.FilesSeen} seen, {first.Counters.Errors} errors");
                Report(harness);
                Assert.Equal(files.Count, (int)first.Counters.FilesSeen);
                FileRecord busy = harness.File1("in use.webp");
                Assert.NotEqual(ScanDepth.Standard, busy.ScannedDepth);
            }

            RunSummary second = await harness.ScanAsync(ScanDepth.Standard);
            output.WriteLine($"Second scan: {second.Status}");
            Report(harness);
            foreach ((string name, string path) in files)
            {
                FileRecord record = harness.AllFiles().Single(f => string.Equals(f.Name, Path.GetFileName(path), StringComparison.Ordinal));
                Assert.True(record.ScannedDepth >= ScanDepth.Standard, $"{name}: scanned to {record.ScannedDepth}");
                List<FindingRecord> findings = harness.Database.Findings.ForFile(record.Id);
                Assert.DoesNotContain(findings, f => DamageRules.Contains(f.RuleId));
            }

            // The long path and the folder with a trailing space are worth a warning; the others are fine.
            Assert.Contains(harness.Database.Findings.ForFile(harness.File1(Path.GetFileName(files["long"])).Id), f => f.RuleId == "GEN-PATH");
            Assert.Contains(harness.Database.Findings.ForFile(harness.File1("clip.mp4").Id), f => f.RuleId == "GEN-PATH");
            Assert.DoesNotContain(harness.Database.Findings.ForFile(harness.File1("Мелодия ñ.flac").Id), f => f.RuleId == "GEN-PATH");
            Assert.DoesNotContain(harness.Database.Findings.ForFile(harness.File1("🎶 song 👍🏽.mp3").Id), f => f.RuleId == "GEN-PATH");
        }
        finally
        {
            File.SetAttributes(files["readonly"], FileAttributes.Normal);
            DeleteLiteral(Path.Combine(root, "Trailing "));
        }
    }

    private void Report(EngineHarness harness)
    {
        foreach (FileRecord file in harness.AllFiles())
        {
            output.WriteLine($"  {file.Name}: depth {file.ScannedDepth}, {string.Join(", ", harness.Database.Findings.ForFile(file.Id).Select(f => f.RuleId))}");
        }
    }

    private static string Place(string fixture, string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.Copy(FixtureFactory.Get(fixture), path);
        return path;
    }

    /// <summary>A folder whose name ends in a space: only reachable through the \\?\ form, which skips normalisation.</summary>
    private static string PlaceLiteral(string fixture, string folder, string name)
    {
        string literal = @"\\?\" + folder;
        Directory.CreateDirectory(literal);
        string path = Path.Combine(literal, name);
        File.Copy(FixtureFactory.Get(fixture), path);
        return folder + Path.DirectorySeparatorChar + name;
    }

    private static void DeleteLiteral(string folder)
    {
        string literal = @"\\?\" + folder;
        if (Directory.Exists(literal))
        {
            Directory.Delete(literal, recursive: true);
        }
    }
}

/// <summary>The extended form is used only where Win32 normalisation would change the path (task T-12).</summary>
public sealed class ExtendedPathTests
{
    [Theory]
    [InlineData(@"C:\Music\a.flac", @"C:\Music\a.flac")]
    [InlineData(@"C:\Music\Trailing \a.flac", @"\\?\C:\Music\Trailing \a.flac")]
    [InlineData(@"C:\Music\Demo.\a.flac", @"\\?\C:\Music\Demo.\a.flac")]
    [InlineData(@"C:/Music/Demo./a.flac", @"\\?\C:\Music\Demo.\a.flac")]
    [InlineData(@"\\nas\share\Live \a.flac", @"\\?\UNC\nas\share\Live \a.flac")]
    [InlineData(@"\\?\C:\Music\Trailing \a.flac", @"\\?\C:\Music\Trailing \a.flac")]
    [InlineData(@"C:\Music\..\Music\.\a.flac", @"C:\Music\..\Music\.\a.flac")]
    [InlineData("-i", "-i")]
    [InlineData("volume=-6dB,adeclip", "volume=-6dB,adeclip")]
    [InlineData("scale=iw/2:ih/2 ", "scale=iw/2:ih/2 ")]
    [InlineData(@"relative\Trailing \a.flac", @"relative\Trailing \a.flac")]
    public void Only_rooted_paths_with_a_trimmed_segment_change(string path, string expected) =>
        Assert.Equal(expected, MediaVerifier.Core.Engine.ExtendedPath.For(path));
}
