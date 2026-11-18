using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Analysis;

/// <summary>
/// Tasks T-09 and VP-01 (subplan 15, Calibration, corpus b and c): the thresholds checked on real material. Reads the
/// labelled folder named by <c>MEDIAVERIFIER_CALIBRATION_DIR</c> (its <c>calibration.json</c>;
/// <c>scripts/Get-CalibrationMedia.ps1</c> fills one with freely licensed films, photos and recordings), derives
/// labelled variants from every trusted original with the tooling FFmpeg, scans everything at Standard depth with the
/// product's own providers and detectors, and fails when an original is accused of anything. Misses on the variants
/// are reported, not failed: real content can hide what the method looks for (a dark scene has no detail to lose).
/// With <c>MEDIAVERIFIER_UPDATE_DOCS=1</c> it writes docs/calibration/real-material.md.
/// </summary>
[Trait("Category", "Calibration")]
public sealed class RealMaterialCalibration(ITestOutputHelper output)
{
    public const string FolderVariable = "MEDIAVERIFIER_CALIBRATION_DIR";

    /// <summary>
    /// Findings a trusted original may carry: facts about the content, not accusations. Padded bits are counted, not
    /// guessed (the Musopen Goldberg recordings are 16-bit audio in 24-bit FLAC files).
    /// </summary>
    private static readonly HashSet<string> ContentFacts = new(StringComparer.Ordinal) { "AUD-LONG-SILENCE", "AUD-PADDED-BITS" };

    /// <summary>True for a finding written as <c>RULE/Severity</c> whose rule is a content fact.</summary>
    private static bool IsContentFact(string finding) => ContentFacts.Contains(finding.Split('/')[0]);

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    [RealMaterialFact]
    public async Task Originals_raise_nothing_and_the_variants_are_found()
    {
        string folder = Environment.GetEnvironmentVariable(FolderVariable)!;
        JsonArray files = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "calibration.json")))!["files"]!.AsArray();
        using TempDirectory temp = new("real-calibration");
        string work = Path.Combine(temp.Path, "variants");
        Directory.CreateDirectory(work);
        List<Variant> variants = [];
        List<(string Name, string Kind, string Licence)> corpus = [];
        foreach (JsonNode? file in files)
        {
            string name = (string)file!["name"]!;
            string kind = (string)file["kind"]!;
            string source = Path.Combine(folder, name);
            corpus.Add((name, kind, (string?)file["licence"] ?? ""));
            variants.AddRange(kind switch
            {
                "video" => VideoVariants(source, work),
                "photo" => PhotoVariants(source, work),
                "audio" => AudioVariants(source, work),
                _ => throw new InvalidDataException($"calibration.json: unknown kind '{kind}' for {name}"),
            });
        }

        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        foreach (Variant variant in variants)
        {
            File.Copy(variant.Path, Path.Combine(harness.Library, variant.Name));
        }

        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Standard);
        List<Row> rows = [.. variants.Select(v => Measure(harness, v))];
        CalibrationDump.Append(harness, variants.Select(v => (v.Name, v.Truth, v.Video, v.Original)), "real");
        foreach (Row row in rows)
        {
            output.WriteLine($"{row.Variant.Name,-44} {row.Variant.Truth,-26} {row.Measures,-60} {(row.Findings.Count == 0 ? "nothing" : string.Join(" ", row.Findings))}");
        }

        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_UPDATE_DOCS") == "1")
        {
            await File.WriteAllTextAsync(RepositoryPaths.Combine("docs", "calibration", "real-material.md"), Report(rows, corpus));
        }

        List<string> falsePositives = [.. rows
            .Where(r => r.Variant.Original)
            .SelectMany(r => r.Findings.Where(f => !IsContentFact(f)).Select(f => $"{r.Variant.Name}: {f}"))];
        Assert.True(falsePositives.Count == 0, "Trusted originals accused:\n" + string.Join('\n', falsePositives));
    }

    private static IEnumerable<Variant> VideoVariants(string source, string work)
    {
        (int width, int height, double fps) = ProbeVideo(source);
        string name = Path.GetFileNameWithoutExtension(source);
        string[] crf18 = ["-c:v", "libx264", "-crf", "18", "-preset", "veryfast", "-g", "48", "-pix_fmt", "yuv420p", "-an"];
        yield return Copy(source, work, name + "-original" + Path.GetExtension(source), "native", video: true, original: true);
        yield return Encode(work, name + "-crf18.mp4", "native", true, ["-i", source, .. crf18], original: true);
        foreach (int rung in FrameSampling.VideoCandidates(height).Take(2))
        {
            foreach (string scaler in new[] { "bilinear", "bicubic", "lanczos" })
            {
                yield return Encode(work, $"{name}-up{rung}-{scaler}.mp4", $"upscaled-from-{rung}", true, ["-i", source, "-vf", $"scale=-2:{rung}:flags=area,scale={width}:{height}:flags={scaler}", .. crf18]);
            }
        }

        // The whole film made half as big again: its original height becomes the first candidate rung.
        yield return Encode(work, $"{name}-x1.5-lanczos.mp4", $"upscaled-from-{height}", true, ["-i", source, "-vf", $"scale={Even(width * 1.5)}:{Even(height * 1.5)}:flags=lanczos", .. crf18]);
        foreach (double bpp in new[] { 0.008, 0.016, 0.024, 0.06, 0.15 })
        {
            int kbps = (int)Math.Round(bpp * width * height * fps / 1000);
            yield return Encode(work, string.Create(C, $"{name}-bpp{bpp:0.000}.mp4"), bpp <= 0.024 ? "starved" : "native", true, ["-i", source, "-c:v", "libx264", "-b:v", kbps.ToString(C) + "k", "-preset", "veryfast", "-g", "48", "-pix_fmt", "yuv420p", "-an"]);
        }
    }

    private static IEnumerable<Variant> PhotoVariants(string source, string work)
    {
        (int width, int height, _) = ProbeVideo(source);
        string name = Path.GetFileNameWithoutExtension(source);
        yield return Copy(source, work, name + "-original.jpg", "native", video: false, original: true);
        yield return Encode(work, name + "-q2.jpg", "native", false, ["-i", source, "-q:v", "2"], original: true);
        foreach ((string label, int numerator, int denominator) in new[] { ("2-3", 2, 3), ("1-2", 1, 2) })
        {
            foreach (string scaler in new[] { "bilinear", "bicubic", "lanczos" })
            {
                yield return Encode(work, $"{name}-up{label}-{scaler}.jpg", $"upscaled-from-{label}", false, ["-i", source, "-vf", $"scale=trunc(iw*{numerator}/{denominator}/2)*2:-2:flags=area,scale={width}:{height}:flags={scaler}", "-q:v", "2"]);
            }
        }

        yield return Encode(work, name + "-q15.jpg", "heavily-compressed", false, ["-i", source, "-q:v", "15"]);
    }

    private static IEnumerable<Variant> AudioVariants(string source, string work)
    {
        string name = Path.GetFileNameWithoutExtension(source);
        (int sampleRate, int bits) = ProbeAudio(source);
        yield return Copy(source, work, name + "-original.flac", "lossless", video: false, original: true);
        string mp3 = Intermediate(work, name + "-128.mp3", ["-i", source, "-c:a", "libmp3lame", "-b:a", "128k", "-f", "mp3"]);
        yield return Encode(work, name + "-fake128.flac", "fake-lossless", false, ["-i", mp3, "-sample_fmt", "s16", "-f", "flac"]);
        string mp3High = Intermediate(work, name + "-320.mp3", ["-i", source, "-c:a", "libmp3lame", "-b:a", "320k", "-f", "mp3"]);
        yield return Encode(work, name + "-fake320.flac", "fake-lossless (320 kbit/s)", false, ["-i", mp3High, "-sample_fmt", "s16", "-f", "flac"]);
        string aac = Intermediate(work, name + "-128.m4a", ["-i", source, "-c:a", "aac", "-b:a", "128k", "-f", "ipod"]);
        yield return Encode(work, name + "-fakeaac.flac", "fake-lossless", false, ["-i", aac, "-sample_fmt", "s16", "-f", "flac"]);
        string opus = Intermediate(work, name + "-128.opus", ["-i", source, "-c:a", "libopus", "-b:a", "128k", "-f", "opus"]);
        yield return Encode(work, name + "-fakeopus.flac", "fake-lossless", false, ["-i", opus, "-sample_fmt", "s16", "-f", "flac"]);
        yield return Encode(work, name + "-inflated320.mp3", "inflated", false, ["-i", mp3, "-c:a", "libmp3lame", "-b:a", "320k", "-f", "mp3"]);
        if (sampleRate <= 48000)
        {
            yield return Encode(work, name + "-up96.flac", "upsampled", false, ["-i", source, "-ar", "96000", "-sample_fmt", "s32", "-f", "flac"]);
        }

        if (bits <= 16)
        {
            yield return Encode(work, name + "-pad24.flac", "padded", false, ["-i", source, "-sample_fmt", "s32", "-f", "flac"]);
        }

        yield return Encode(work, name + "-clip.flac", "clipped", false, ["-i", source, "-af", "volume=14dB", "-sample_fmt", "s16", "-f", "flac"]);
    }

    private static Variant Copy(string source, string work, string name, string truth, bool video, bool original)
    {
        string path = Path.Combine(work, name);
        File.Copy(source, path, overwrite: true);
        return new Variant(name, path, truth, video, original);
    }

    private static Variant Encode(string work, string name, string truth, bool video, string[] arguments, bool original = false)
    {
        string path = Path.Combine(work, name);
        string[] single = name.EndsWith(".jpg", StringComparison.Ordinal) ? ["-frames:v", "1", "-update", "1"] : [];
        Tool(FfmpegEnvironment.Tooling!, [.. arguments, .. single, path]);
        return new Variant(name, path, truth, video, original);
    }

    private static string Intermediate(string work, string name, string[] arguments)
    {
        string path = Path.Combine(work, "intermediate-" + name);
        Tool(FfmpegEnvironment.Tooling!, [.. arguments, path]);
        return path;
    }

    private static (int Width, int Height, double Fps) ProbeVideo(string source)
    {
        string[] fields = Tool(ToolingProbe, ["-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate", "-of", "csv=p=0", source]).Trim().Split(',');
        string[] rate = fields[2].Split('/');
        double fps = rate.Length == 2 && double.Parse(rate[1], C) > 0 ? double.Parse(rate[0], C) / double.Parse(rate[1], C) : 24;
        return (int.Parse(fields[0], C), int.Parse(fields[1], C), fps);
    }

    private static (int SampleRate, int Bits) ProbeAudio(string source)
    {
        string[] fields = Tool(ToolingProbe, ["-select_streams", "a:0", "-show_entries", "stream=sample_rate,bits_per_raw_sample", "-of", "csv=p=0", source]).Trim().Split(',');
        return (int.Parse(fields[0], C), int.TryParse(fields[1], NumberStyles.Integer, C, out int bits) ? bits : 16);
    }

    private static string ToolingProbe => Path.Combine(Path.GetDirectoryName(FfmpegEnvironment.Tooling)!, "ffprobe.exe");

    private static string Tool(string exe, string[] arguments)
    {
        ProcessStartInfo start = new(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in (string[])["-hide_banner", "-v", "error", .. exe.EndsWith("ffprobe.exe", StringComparison.OrdinalIgnoreCase) ? [] : (string[])["-nostdin", "-y"], .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(exe)} {string.Join(' ', arguments)}: {error.Result}");
        return text;
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    private static Row Measure(EngineHarness harness, Variant variant)
    {
        FileRecord file = harness.AllFiles().Single(f => f.Name == variant.Name);
        Dictionary<ArtifactKind, (int ProviderVersion, string Data)> stored = harness.Database.Artifacts.ForFile(file.Id);
        List<string> findings = [.. harness.Database.Findings.ForFile(file.Id)
            .Where(f => f.Status == FindingStatus.Open && (f.RuleId.StartsWith("VID-", StringComparison.Ordinal) || f.RuleId.StartsWith("IMG-", StringComparison.Ordinal) || f.RuleId.StartsWith("AUD-", StringComparison.Ordinal)))
            .Select(f => f.RuleId + "/" + f.Severity)];
        List<string> damage = [.. harness.Database.Findings.ForFile(file.Id).Where(f => f.Status == FindingStatus.Open && !findings.Contains(f.RuleId + "/" + f.Severity)).Select(f => f.RuleId)];

        List<string> measures = [];
        if (stored.TryGetValue(ArtifactKind.FrameSamples, out (int, string Data) samplesData) && new FrameSampleProvider().Deserialize(samplesData.Data) is FrameSamplesArtifact samples && samples.Candidates.Length > 0)
        {
            double minSpatial = RuleSettings.Defaults.Value(variant.Video ? "VID-UPSCALED" : "IMG-UPSCALED", "minSpatialInfo");
            SampledFrame[] usable = [.. samples.Frames.Where(f => f.SpatialInfo >= minSpatial && f.Ssim.Length == samples.Candidates.Length)];
            measures.Add(usable.Length == 0
                ? "no usable frames"
                : "SSIM " + string.Join(", ", samples.Candidates.Select((rung, k) => string.Create(C, $"{rung}: {UpscaleDecision.Percentile([.. usable.Select(f => f.Ssim[k])], 0.10):0.0000}"))));
            if (samples.Frames.Length > 0)
            {
                measures.Add(string.Create(C, $"SI {UpscaleDecision.Percentile([.. samples.Frames.Select(f => f.SpatialInfo)], 0.5):0.0} ({usable.Length}/{samples.Frames.Length} usable)"));
            }

            if (variant.Video && samples.Frames.Length > 0)
            {
                measures.Add(string.Create(C, $"block {UpscaleDecision.Percentile([.. samples.Frames.Select(f => f.Block)], 0.5):0.00}"));
            }
        }

        if (variant.Video && stored.TryGetValue(ArtifactKind.Probe, out (int, string Data) probeData) && new ProbeArtifactProvider().Deserialize(probeData.Data) is ProbeArtifact probe && probe.Video is StreamInfo video
            && VideoBitrate.EquivalentBpp(probe, video, out _, out _) is double bpp)
        {
            measures.Add(string.Create(C, $"bpp {bpp:0.000}"));
        }

        if (!variant.Video && stored.TryGetValue(ArtifactKind.Structure, out (int, string Data) structureData)
            && new StructureProvider().Deserialize(structureData.Data) is StructureArtifact { Jpeg: { QuantTables.Count: > 0 } jpeg } && jpeg.QuantTables[0].Length == 64)
        {
            measures.Add(string.Create(C, $"quality {JpegQuality.Estimate(jpeg.QuantTables[0]).Quality:0}"));
        }

        if (stored.TryGetValue(ArtifactKind.AudioDecode, out (int, string Data) audioData) && new AudioDecodeProvider().Deserialize(audioData.Data) is AudioDecodeArtifact { Stats: PcmStats stats })
        {
            SpectrumStats s = stats.Spectrum;
            measures.Add(string.Create(C, $"knee {((s.Sufficient && s.DropDb >= 30) ? (s.KneeHz / 1000).ToString("0.0", C) + " kHz" : "none")}, drop {s.DropDb:0.0} dB, bits {stats.EffectiveBits}, clipped runs {stats.Clipping.Runs}"));
        }

        if (damage.Count > 0)
        {
            measures.Add("also " + string.Join(" ", damage));
        }

        return new Row(variant, measures.Count == 0 ? "—" : string.Join("; ", measures), findings);
    }

    private static string Report(IReadOnlyList<Row> rows, IReadOnlyList<(string Name, string Kind, string Licence)> corpus)
    {
        StringBuilder text = new();
        text.AppendLine("# Calibration on real material (subplan 15, corpus b; tasks T-09 and VP-01)");
        text.AppendLine();
        text.AppendLine("Run: `.\\scripts\\Get-CalibrationMedia.ps1`, then with `MEDIAVERIFIER_CALIBRATION_DIR` set to its folder and");
        text.AppendLine("`MEDIAVERIFIER_UPDATE_DOCS=1`: `dotnet test -c Release --filter FullyQualifiedName~RealMaterialCalibration`");
        text.AppendLine(string.Create(C, $"({DateTime.Now:yyyy-MM-dd}, {FfmpegEnvironment.EngineDescription} as the engine, the tooling FFmpeg for the variants). Every original is trusted"));
        text.AppendLine("(`native` or `lossless`) and must raise nothing; each variant is made from it with the tooling FFmpeg and labelled");
        text.AppendLine("with what was done to it. Every number comes from the product's own providers and detectors at Standard depth.");
        text.AppendLine();
        text.AppendLine("Corpus (downloaded at calibration time, never committed):");
        text.AppendLine();
        foreach (IGrouping<string, (string Name, string Kind, string Licence)> group in corpus.GroupBy(c => c.Licence))
        {
            text.Append("- ").Append(string.Join(", ", group.Select(c => "`" + c.Name + "`"))).Append(": ").AppendLine(group.Key);
        }

        text.AppendLine();
        foreach ((string kind, string title) in new[] { ("video", "Video"), ("photo", "Photos"), ("audio", "Audio") })
        {
            List<Row> part = [.. rows.Where(r => Kind(r.Variant) == kind)];
            if (part.Count == 0)
            {
                continue;
            }

            text.AppendLine("## " + title);
            text.AppendLine();
            text.AppendLine("| Variant | Truth | Measures | Raised |");
            text.AppendLine("|---------|-------|----------|--------|");
            foreach (Row row in part)
            {
                text.Append("| `").Append(row.Variant.Name).Append("` | ").Append(row.Variant.Truth).Append(" | ").Append(row.Measures).Append(" | ")
                    .Append(row.Findings.Count == 0 ? "nothing" : string.Join(" ", row.Findings)).AppendLine(" |");
            }

            text.AppendLine();
            text.AppendLine(Summary(part));
            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Kind(Variant variant) => variant.Video ? "video" : variant.Name.EndsWith(".jpg", StringComparison.Ordinal) ? "photo" : "audio";

    /// <summary>False positives on the originals and the share of each kind of variant that was found.</summary>
    private static string Summary(IReadOnlyList<Row> rows)
    {
        StringBuilder text = new();
        List<Row> originals = [.. rows.Where(r => r.Variant.Original)];
        int accused = originals.Count(r => r.Findings.Any(f => !IsContentFact(f)));
        text.AppendLine(string.Create(C, $"- Originals: {originals.Count}, accused: {accused}."));
        foreach (IGrouping<string, Row> group in rows.Where(r => !r.Variant.Original).GroupBy(r => Expected(r.Variant.Truth)).Where(g => g.Key.Length > 0))
        {
            int found = group.Count(r => r.Findings.Any(f => f.StartsWith(group.Key, StringComparison.Ordinal) || (group.Key == "-UPSCALED" && f.Contains("-UPSCALED", StringComparison.Ordinal))));
            text.AppendLine(string.Create(C, $"- {Describe(group.Key)}: {found} of {group.Count()} found."));
        }

        List<Row> adequate = [.. rows.Where(r => !r.Variant.Original && Expected(r.Variant.Truth).Length == 0)];
        if (adequate.Count > 0)
        {
            text.AppendLine(string.Create(C, $"- Adequate re-encodes (expected to raise nothing): {adequate.Count}, accused: {adequate.Count(r => r.Findings.Count > 0)}."));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The rule (or rule suffix) a variant's truth should raise; empty when it should raise nothing.</summary>
    private static string Expected(string truth) => truth switch
    {
        _ when truth.StartsWith("upscaled-from-", StringComparison.Ordinal) => "-UPSCALED",
        "starved" => "VID-STARVED",
        "heavily-compressed" => "IMG-HEAVY-COMPRESSION",
        "fake-lossless" => "AUD-FAKE-LOSSLESS",
        "fake-lossless (320 kbit/s)" => "AUD-FAKE-LOSSLESS",
        "inflated" => "AUD-BITRATE-INFLATED",
        "upsampled" => "AUD-UPSAMPLED",
        "padded" => "AUD-PADDED-BITS",
        "clipped" => "AUD-CLIPPING",
        _ => "",
    };

    private static string Describe(string rule) => rule == "-UPSCALED" ? "Upscales (`VID-UPSCALED` or `IMG-UPSCALED`)" : "`" + rule + "`";

    private sealed record Variant(string Name, string Path, string Truth, bool Video, bool Original);

    private sealed record Row(Variant Variant, string Measures, List<string> Findings);
}

/// <summary>Runs only when MEDIAVERIFIER_CALIBRATION_DIR names a folder with calibration.json and both FFmpeg builds exist.</summary>
public sealed class RealMaterialFactAttribute : FactAttribute
{
    public RealMaterialFactAttribute()
    {
        string? folder = Environment.GetEnvironmentVariable(RealMaterialCalibration.FolderVariable);
        if (string.IsNullOrWhiteSpace(folder) || !File.Exists(Path.Combine(folder, "calibration.json")))
        {
            Skip = "Set MEDIAVERIFIER_CALIBRATION_DIR to a folder with calibration.json (scripts/Get-CalibrationMedia.ps1 makes one).";
        }
        else if (FfmpegEnvironment.Tooling is null || FfmpegEnvironment.Engine is null)
        {
            Skip = "Needs the tooling FFmpeg on PATH and the FFmpeg engine.";
        }
    }
}
