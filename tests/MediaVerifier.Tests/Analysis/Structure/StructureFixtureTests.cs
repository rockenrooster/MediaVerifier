using System.Diagnostics;
using System.Globalization;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>
/// A test over generated media that needs only the tooling FFmpeg: the structure reader itself runs no
/// FFmpeg, so the engine bundle is not required. Skipped, never failed, without it.
/// </summary>
public sealed class StructureFixtureFactAttribute : FactAttribute
{
    public StructureFixtureFactAttribute()
    {
        if (FfmpegEnvironment.Tooling is null)
        {
            Skip = "No tooling FFmpeg (a full build with encoders) on PATH to generate fixtures.";
        }
    }
}

/// <summary>Tasks C-02 to C-05 and C-13 against the generated fixture set (subplan 15), healthy and damaged.</summary>
public sealed class StructureFixtureTests : IDisposable
{
    private readonly TempDirectory _temp = new("structure");

    public void Dispose() => _temp.Dispose();

    [StructureFixtureFact]
    public void FlacStreamInfoMatchesFfprobeAndTheDecodedMd5()
    {
        foreach (string name in new[] { "src16.flac", "pad24.flac" })
        {
            string path = FixtureFactory.Get(name);
            StructureArtifact artifact = ReadFile(path);
            Assert.Empty(artifact.Issues);
            FlacInfo flac = artifact.Flac!;
            Dictionary<string, string> probe = Probe(path, "stream=sample_rate,channels,bits_per_raw_sample,duration_ts");
            Assert.Equal(int.Parse(probe["sample_rate"], CultureInfo.InvariantCulture), flac.SampleRate);
            Assert.Equal(int.Parse(probe["channels"], CultureInfo.InvariantCulture), flac.Channels);
            Assert.Equal(int.Parse(probe["bits_per_raw_sample"], CultureInfo.InvariantCulture), flac.BitsPerSample);
            Assert.Equal(long.Parse(probe["duration_ts"], CultureInfo.InvariantCulture), flac.TotalSamples);
            Assert.Equal(8, flac.StreamInfoOffset);

            // The MD5 read at file offset 26 is the MD5 of the decoded samples.
            string format = flac.BitsPerSample == 16 ? "pcm_s16le" : "pcm_s24le";
            string md5 = RunTooling("ffmpeg.exe", "-v", "error", "-i", path, "-map", "0:a:0", "-c:a", format, "-f", "md5", "-").Trim();
            Assert.Equal("MD5=" + flac.Md5Hex, md5);
            byte[] head = File.ReadAllBytes(path)[..42];
            Assert.Equal(Convert.ToHexStringLower(head.AsSpan(26, 16)), flac.Md5Hex);

            // FFmpeg's FLAC muxer writes an 8 KiB PADDING block.
            Assert.Contains(artifact.Padding, p => p.Length == 8192);
        }
    }

    [StructureFixtureFact]
    public void Mp3InfoHeader()
    {
        string path = FixtureFactory.Get("m128.mp3");
        StructureArtifact artifact = ReadFile(path);
        Assert.Empty(artifact.Issues);
        Mp3Info mp3 = artifact.Mp3!;
        Assert.Equal("Info", mp3.VbrHeader);
        Assert.Equal(mp3.AudioBytes, mp3.DeclaredBytes);
        Assert.Equal(artifact.Sniff.Id3v2Length, mp3.AudioStart);
        Assert.Equal(128_000, mp3.Bitrate);
        Assert.Equal(44100, mp3.SampleRate);
        Assert.Equal(new FileInfo(path).Length, artifact.DeclaredEnd);

        StructureArtifact cover = ReadFile(FixtureFactory.Get("cover.mp3"));
        Assert.Empty(cover.Issues);
        Assert.True(cover.Sniff.Id3v2Length > 100_000, "the cover art lives in the ID3v2 tag");
    }

    [StructureFixtureFact]
    public void Mp3TruncatedAtFiftyFivePercent()
    {
        string path = Damaged("m128.mp3", 0.55);
        StructureIssue issue = Assert.Single(ReadFile(path).Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("Info", issue.Element);
    }

    [StructureFixtureFact]
    public void HealthyMp4AndM4a()
    {
        foreach (string name in new[] { "fs.mp4", "native1080.mp4", "av.mp4", "a128.m4a" })
        {
            string path = FixtureFactory.Get(name);
            StructureArtifact artifact = ReadFile(path);
            Assert.True(artifact.Issues.Count == 0, name + ": " + string.Join("; ", artifact.Issues.Select(i => i.Detail)));
            Assert.Equal(new FileInfo(path).Length, artifact.DeclaredEnd);
        }

        SniffResult m4a = ReadFile(FixtureFactory.Get("a128.m4a")).Sniff;
        Assert.Equal("M4A ", m4a.Brand);
        Assert.Equal(MediaKind.Audio, m4a.KindHint);
    }

    [StructureFixtureFact]
    public void Mp4TruncatedAtSixtyPercent()
    {
        long fullLength = new FileInfo(FixtureFactory.Get("fs.mp4")).Length;
        string front = Damaged("fs.mp4", 0.6);
        StructureIssue issue = Assert.Single(ReadFile(front).Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("mdat", issue.Element);
        Assert.Equal(fullLength - new FileInfo(front).Length, issue.MissingBytes);

        // Index at the end (the matrix row "MP4, index at end"): the moov is gone.
        StructureArtifact end = ReadFile(Damaged("native1080.mp4", 0.6));
        Assert.Equal("moov box not found", Assert.Single(end.Issues, i => i.Kind == StructureIssueKind.ContainerBroken).Detail);
        Assert.Equal("mdat", Assert.Single(end.Issues, i => i.Kind == StructureIssueKind.Truncated).Element);
    }

    [StructureFixtureFact]
    public void Matroska()
    {
        string path = FixtureFactory.Get("t.mkv");
        StructureArtifact artifact = ReadFile(path);
        Assert.Empty(artifact.Issues);
        Assert.Equal("matroska", artifact.Matroska!.DocType);
        Assert.True(artifact.Matroska.HasCues);
        Assert.True(artifact.Matroska.HasVideoTrack);
        Assert.Equal(new FileInfo(path).Length, artifact.DeclaredEnd);

        long fullLength = new FileInfo(path).Length;
        string cut = Damaged("t.mkv", 0.6);
        StructureIssue issue = Assert.Single(ReadFile(cut).Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("Segment", issue.Element);
        Assert.Equal(fullLength - new FileInfo(cut).Length, issue.MissingBytes);
    }

    [StructureFixtureFact]
    public void MatroskaRemuxedWithoutCues()
    {
        // Written to a pipe, the muxer cannot go back: unknown Segment size and no Cues.
        string path = _temp.Combine("nocues.mkv");
        RunToolingToFile(path, "-v", "error", "-i", FixtureFactory.Get("fs.mp4"), "-c", "copy", "-f", "matroska", "-");
        StructureArtifact artifact = ReadFile(path);
        Assert.Null(artifact.Matroska!.SegmentSize);
        Assert.False(artifact.Matroska.HasCues);
        StructureIssue issue = Assert.Single(artifact.Issues);
        Assert.Equal(StructureIssueKind.IndexMissingOrWrong, issue.Kind);
        Assert.Equal("Cues", issue.Element);
    }

    [StructureFixtureFact]
    public void Jpeg()
    {
        string path = FixtureFactory.Get("img.jpg");
        StructureArtifact artifact = ReadFile(path);
        Assert.Empty(artifact.Issues);
        JpegInfo jpeg = artifact.Jpeg!;
        Assert.Equal(1920, jpeg.Width);
        Assert.Equal(1080, jpeg.Height);
        Assert.True(jpeg.HasEoi);
        Assert.Equal(0, jpeg.BytesAfterEoi);
        Assert.Equal(1, jpeg.ScanCount);

        // FFmpeg's MJPEG encoder writes one 8-bit table that all three components share.
        byte[] bytes = File.ReadAllBytes(path);
        int dqt = bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xDB]);
        int[] stored = [.. bytes.AsSpan(dqt + 5, 64).ToArray().Select(b => (int)b)];
        Assert.Equal(stored, Assert.Single(jpeg.QuantTables));

        StructureIssue issue = Assert.Single(ReadFile(Damaged("img.jpg", 0.6)).Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("EOI", issue.Element);
    }

    [StructureFixtureFact]
    public void Png()
    {
        string path = FixtureFactory.Get("img.png");
        Assert.Empty(ReadFile(path).Issues);

        long fullLength = new FileInfo(path).Length;
        string cut = Damaged("img.png", 0.6);
        StructureIssue issue = Assert.Single(ReadFile(cut).Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal("IDAT", issue.Element);

        // FFmpeg writes many IDAT chunks, so only the cut chunk and IEND are known to be missing.
        Assert.InRange(issue.MissingBytes!.Value, 13, fullLength - new FileInfo(cut).Length);
    }

    [StructureFixtureFact]
    public void WebP()
    {
        string path = FixtureFactory.Get("img.webp");
        StructureArtifact artifact = ReadFile(path);
        Assert.Equal(ContainerFormat.WebP, artifact.Sniff.Format);
        Assert.Empty(artifact.Issues);

        long fullLength = new FileInfo(path).Length;
        string cut = Damaged("img.webp", 0.6);
        StructureIssue issue = Assert.Single(ReadFile(cut).Issues);
        Assert.Equal(StructureIssueKind.Truncated, issue.Kind);
        Assert.Equal(fullLength - new FileInfo(cut).Length, issue.MissingBytes);
    }

    [StructureFixtureFact]
    public void ExtensionsOfTheFixtureSetMatch()
    {
        foreach (string name in new[] { "src16.flac", "m128.mp3", "fs.mp4", "a128.m4a", "t.mkv", "img.jpg", "img.png", "img.webp" })
        {
            string extension = Path.GetExtension(name).TrimStart('.');
            SniffResult sniff = ReadFile(FixtureFactory.Get(name)).Sniff;
            Assert.Equal(ExtensionVerdict.Match, ExtensionFamilies.Check(extension, sniff.Format, sniff.Brand, probeOpened: true, sniff.KindHint));
            Assert.Equal(ExtensionVerdict.Match, ExtensionFamilies.Check(ExtensionFamilies.CanonicalExtension(sniff.Format, sniff.Brand, sniff.KindHint)!, sniff.Format, sniff.Brand, true, sniff.KindHint));
        }

        // A FLAC renamed to .mp3 and a PNG renamed to .jpg.
        SniffResult flac = ReadFile(FixtureFactory.CopyTo("src16.flac", _temp.Path, "renamed.mp3")).Sniff;
        Assert.Equal(ExtensionVerdict.MismatchMedium, ExtensionFamilies.Check("mp3", flac.Format, flac.Brand, true, MediaKind.Audio));
        SniffResult png = ReadFile(FixtureFactory.CopyTo("img.png", _temp.Path, "renamed.jpg")).Sniff;
        Assert.Equal(ExtensionVerdict.MismatchLow, ExtensionFamilies.Check("jpg", png.Format, png.Brand, true, MediaKind.Photo));
    }

    private static StructureArtifact ReadFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return StructureReader.Read(stream, Path.GetExtension(path).TrimStart('.'));
    }

    private string Damaged(string fixture, double keep)
    {
        string path = FixtureFactory.CopyTo(fixture, _temp.Path);
        Mutate.Truncate(path, keep);
        return path;
    }

    private static Dictionary<string, string> Probe(string path, string entries)
    {
        string output = RunTooling("ffprobe.exe", "-v", "error", "-select_streams", "a:0", "-show_entries", entries, "-of", "default=noprint_wrappers=1", path);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
    }

    private static string RunTooling(string tool, params string[] arguments)
    {
        using Process process = Process.Start(ToolingStart(tool, arguments))!;
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static void RunToolingToFile(string target, params string[] arguments)
    {
        using Process process = Process.Start(ToolingStart("ffmpeg.exe", arguments))!;
        using (FileStream file = new(target, FileMode.Create, FileAccess.Write))
        {
            Task error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.BaseStream.CopyTo(file);
            error.GetAwaiter().GetResult();
        }

        process.WaitForExit();
    }

    private static ProcessStartInfo ToolingStart(string tool, string[] arguments)
    {
        string directory = Path.GetDirectoryName(FfmpegEnvironment.Tooling)!;
        ProcessStartInfo start = new(Path.Combine(directory, tool))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-hide_banner");
        if (tool == "ffmpeg.exe")
        {
            start.ArgumentList.Add("-nostdin");
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
