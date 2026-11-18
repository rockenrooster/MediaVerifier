using System.Diagnostics;
using System.Text.Json;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Remediation;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Remediation;

/// <summary>Lossless repairs keep what makes a file the user's: its tags, its format, and the repair itself.</summary>
[Trait("Category", "Integration")]
public sealed class RemediationMediaTests
{
    private static readonly string[] AppleKeys =
    [
        "com.apple.quicktime.make", "com.apple.quicktime.model", "com.apple.quicktime.location.ISO6709", "com.apple.quicktime.creationdate",
    ];

    [FixtureFact]
    public async Task Rebuild_keeps_the_capture_date_place_and_camera_of_a_phone_video()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness h = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string path = Make(
            h.Library,
            "IMG_0001.mov",
            "-f", "lavfi", "-i", "testsrc2=s=320x240:r=24", "-f", "lavfi", "-i", "sine=f=440:r=48000", "-t", "2", "-c:v", "libx264", "-preset", "veryfast", "-c:a", "aac",
            "-movflags", "use_metadata_tags",
            "-metadata", "com.apple.quicktime.make=Apple", "-metadata", "com.apple.quicktime.model=iPhone 15",
            "-metadata", "com.apple.quicktime.location.ISO6709=+51.5074-000.1278+011.000/", "-metadata", "com.apple.quicktime.creationdate=2026-01-02T03:04:05+0000");
        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Quick);

        OperationOutcome done = await Runner(h, runner).RunAsync(h.File1("IMG_0001.mov").Id, new RemuxAction(), 1, CancellationToken.None);
        Assert.True(done.Status == OperationStatus.Done, done.Reason);
        Dictionary<string, string> tags = FormatTags(path);
        Assert.All(AppleKeys, key => Assert.True(tags.ContainsKey(key), key + " was lost: " + string.Join(", ", tags.Keys)));
        Assert.Equal("qt  ", tags["major_brand"]);
    }

    [FixtureFact]
    public async Task Rebuild_keeps_an_m4a_title_where_players_read_it()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness h = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string path = Make(h.Library, "song.m4a", "-i", FixtureFactory.Get("a128.m4a"), "-c", "copy", "-metadata", "title=Song", "-f", "ipod");
        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Quick);

        OperationOutcome done = await Runner(h, runner).RunAsync(h.File1("song.m4a").Id, new RemuxAction(), 1, CancellationToken.None);
        Assert.True(done.Status == OperationStatus.Done, done.Reason);
        Assert.Equal("Song", FormatTags(path)["title"]);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        byte[] titleAtom = [0xA9, (byte)'n', (byte)'a', (byte)'m'];
        Assert.True(bytes.AsSpan().IndexOf(titleAtom) >= 0, "The iTunes title atom is gone.");
        Assert.True(bytes.AsSpan().IndexOf("mdta"u8) < 0, "The tags were moved to QuickTime keys.");
    }

    [FixtureFact]
    public async Task Storing_a_padded_wav_at_16_bits_keeps_it_a_pcm_wav_and_the_repair_sticks()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness h = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string path = Make(h.Library, "take.wav", "-i", FixtureFactory.Get("pad24.flac"), "-c:a", "pcm_s24le", "-metadata", "title=Take", "-f", "wav");
        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Standard);
        FileRecord file = h.File1("take.wav");
        Assert.Contains(h.Database.Findings.ForFile(file.Id), f => f.RuleId == "AUD-PADDED-BITS" && f.Status == FindingStatus.Open);

        OperationOutcome done = await Runner(h, runner).RunAsync(file.Id, new FlacRewriteAction(shrinkTo16: true), 1, CancellationToken.None);
        Assert.True(done.Status == OperationStatus.Done, done.Reason);
        Assert.Equal("pcm_s16le", AudioCodec(path));
        Assert.Equal("Take", FormatTags(path)["title"]);
        byte[] header = (await File.ReadAllBytesAsync(path))[..12];
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));

        // The next scan analyses the 16-bit file instead of re-raising the finding from the 24-bit analysis.
        await h.ScanAsync(ScanDepth.Standard);
        Assert.DoesNotContain(h.Database.Findings.ForFile(file.Id), f => f.RuleId == "AUD-PADDED-BITS" && f.Status == FindingStatus.Open);
        Assert.Equal(16, h.Database.Files.Get(file.Id)!.Media.BitDepth);
    }

    [FixtureFact]
    public async Task A_flac_stored_at_16_bits_stays_repaired_after_the_next_scan()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness h = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        FixtureFactory.CopyTo("pad24.flac", h.Library);
        File.SetLastWriteTimeUtc(Path.Combine(h.Library, "pad24.flac"), DateTime.UtcNow.AddHours(-1));
        await h.AddRootAsync(h.Library);
        await h.ScanAsync(ScanDepth.Standard);
        FileRecord file = h.File1("pad24.flac");
        Assert.Contains(h.Database.Findings.ForFile(file.Id), f => f.RuleId == "AUD-PADDED-BITS" && f.Status == FindingStatus.Open);

        Assert.Equal(OperationStatus.Done, (await Runner(h, runner).RunAsync(file.Id, new FlacRewriteAction(shrinkTo16: true), 1, CancellationToken.None)).Status);
        await h.ScanAsync(ScanDepth.Standard);
        Assert.DoesNotContain(h.Database.Findings.ForFile(file.Id), f => f.RuleId == "AUD-PADDED-BITS" && f.Status == FindingStatus.Open);
    }

    private static OperationRunner Runner(EngineHarness h, FfRunner runner) => new(h.Database, runner, WindowsFileSystem.Instance, h.Database.Clock);

    /// <summary>Makes a test file with the tooling FFmpeg; it is dated an hour back so the scan does not skip it as still being written.</summary>
    private static string Make(string folder, string name, params string[] args)
    {
        string target = Path.Combine(folder, name);
        ProcessStartInfo start = new(FfmpegEnvironment.Tooling!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in (string[])["-hide_banner", "-nostdin", "-v", "error", "-y", .. args, target])
        {
            start.ArgumentList.Add(arg);
        }

        using (Process process = Process.Start(start)!)
        {
            string error = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }

        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddHours(-1));
        return target;
    }

    private static Dictionary<string, string> FormatTags(string path)
    {
        using JsonDocument json = Probe(path, "format_tags");
        Dictionary<string, string> tags = new(StringComparer.Ordinal);
        if (json.RootElement.TryGetProperty("format", out JsonElement format) && format.TryGetProperty("tags", out JsonElement found))
        {
            foreach (JsonProperty tag in found.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.GetString() ?? string.Empty;
            }
        }

        return tags;
    }

    private static string? AudioCodec(string path)
    {
        using JsonDocument json = Probe(path, "stream=codec_type,codec_name");
        return json.RootElement.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "audio").GetProperty("codec_name").GetString();
    }

    private static JsonDocument Probe(string path, string entries)
    {
        ProcessStartInfo start = new(FfmpegEnvironment.Engine!.FfprobeExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in (string[])["-v", "error", "-show_entries", entries, "-of", "json", path])
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return JsonDocument.Parse(output);
    }
}
