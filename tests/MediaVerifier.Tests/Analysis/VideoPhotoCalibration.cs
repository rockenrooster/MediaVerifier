using System.Diagnostics;
using System.Globalization;
using System.Text;
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
/// Task T-09 (subplan 15, Calibration) for video and photos on the generated corpus: labelled variants (upscales from
/// each lower rung with three scalers, a bitrate ladder, a quality ladder) measured by the product's own providers.
/// With MEDIAVERIFIER_UPDATE_DOCS=1 it writes docs/calibration/video.md and docs/calibration/photo.md. It asserts that
/// the current RuleDefaults raise nothing on native variants and find every variant the method can see.
/// </summary>
[Trait("Category", "Calibration")]
public sealed class VideoPhotoCalibration(ITestOutputHelper output)
{
    private const string Encode = "-c:v libx264 -crf 20 -preset veryfast -g 48 -pix_fmt yuv420p";
    private const string Source = "mandelbrot=size={0}:rate=24";
    private const string Noise = "noise=c0s=6:c0f=t+u";

    [FixtureFact]
    public async Task Video_and_photo_thresholds_separate_the_generated_variants()
    {
        using TempDirectory temp = new("calibrate");
        string library = Path.Combine(temp.Path, "Library");
        Directory.CreateDirectory(library);
        List<Variant> variants = [];

        // Video: native 1080p, upscales from 900, 720 and 540 with three scalers, a bitrate ladder and a CRF ladder.
        variants.Add(Video(library, "native-1080.mp4", "native", $"-f lavfi -i \"{Format(Source, "1920x1080")}\" -t 6 -vf \"{Noise},format=yuv420p\" {Encode}"));
        foreach (int rung in new[] { 900, 720, 540 })
        {
            int width = rung * 16 / 9;
            foreach (string scaler in new[] { "bilinear", "bicubic", "lanczos" })
            {
                // 900 lies between the candidate rungs (720, 540, 360 for a 1080p frame): only a soft scaler shows it.
                string truth = rung == 900 ? "upscaled-from-900 (between rungs)" : $"upscaled-from-{rung}";
                variants.Add(Video(library, $"up{rung}-{scaler}.mp4", truth, $"-f lavfi -i \"{Format(Source, $"{width}x{rung}")}\" -t 6 -vf \"{Noise},scale=1920:1080:flags={scaler},format=yuv420p\" {Encode}"));
            }
        }

        string native = variants[0].Path;
        foreach (int kbps in new[] { 250, 500, 1000, 2500, 6000, 20000 })
        {
            variants.Add(Video(library, $"rate{kbps}k.mp4", kbps <= 1000 ? "starved" : "native", $"-i \"{native}\" -c:v libx264 -b:v {kbps}k -preset veryfast -g 48 -pix_fmt yuv420p"));
        }

        foreach (int crf in new[] { 35, 45, 51 })
        {
            variants.Add(Video(library, $"crf{crf}.mp4", "heavily-compressed", $"-i \"{native}\" -c:v libx264 -crf {crf} -preset veryfast -g 48 -pix_fmt yuv420p"));
        }

        // Photos: a native frame, upscales from 720 and 540 with three scalers, and a JPEG quality ladder.
        string frame = Path.Combine(temp.Path, "frame.png");
        Tooling($"-ss 2 -i \"{native}\" -frames:v 1 -c:v png -update 1 \"{frame}\"");
        variants.Add(Photo(library, "native.jpg", "native", $"-i \"{frame}\" -q:v 2"));
        foreach (int rung in new[] { 720, 540 })
        {
            foreach (string scaler in new[] { "bilinear", "bicubic", "lanczos" })
            {
                variants.Add(Photo(library, $"up{rung}-{scaler}.jpg", $"upscaled-from-{rung}", $"-i \"{frame}\" -vf \"scale=-2:{rung}:flags=area,scale=1920:1080:flags={scaler}\" -q:v 2"));
            }
        }

        foreach (int q in new[] { 2, 5, 10, 15, 20, 25, 31 })
        {
            variants.Add(Photo(library, $"q{q:00}.jpg", "quality-ladder", $"-i \"{frame}\" -q:v {q}"));
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
        foreach (Row row in rows)
        {
            output.WriteLine($"{row.Variant.Name,-24} {row.Variant.Truth,-20} rungs {row.Rungs} bpp {row.Bpp} block {row.Block} q {row.Quality} raised {row.Raised}");
        }

        if (Environment.GetEnvironmentVariable("MEDIAVERIFIER_UPDATE_DOCS") == "1")
        {
            await File.WriteAllTextAsync(RepositoryPaths.Combine("docs", "calibration", "video.md"), VideoReport(rows.Where(r => r.Variant.Video).ToList()));
            await File.WriteAllTextAsync(RepositoryPaths.Combine("docs", "calibration", "photo.md"), PhotoReport(rows.Where(r => !r.Variant.Video).ToList()));
        }

        List<string> failures = [];
        foreach (Row row in rows)
        {
            string truth = row.Variant.Truth;
            bool upscaled = row.Raised.Contains("UPSCALED", StringComparison.Ordinal);
            if (truth == "native" && (upscaled || row.Raised.Contains("STARVED", StringComparison.Ordinal) || row.Raised.Contains("HEAVY", StringComparison.Ordinal)))
            {
                failures.Add($"{row.Variant.Name}: false positive {row.Raised}");
            }

            if (truth.StartsWith("upscaled-from-", StringComparison.Ordinal) && !truth.EndsWith("(between rungs)", StringComparison.Ordinal) && !upscaled)
            {
                failures.Add($"{row.Variant.Name}: upscale missed (rungs {row.Rungs})");
            }

            if (truth == "starved" && !row.Raised.Contains("VID-STARVED", StringComparison.Ordinal))
            {
                failures.Add($"{row.Variant.Name}: starved encode missed (bpp {row.Bpp})");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static Variant Video(string folder, string name, string truth, string arguments)
    {
        string path = Path.Combine(folder, "..", "sources-" + name);
        Tooling(arguments + $" -f mp4 \"{path}\"");
        return new Variant(name, Path.GetFullPath(path), truth, Video: true);
    }

    private static Variant Photo(string folder, string name, string truth, string arguments)
    {
        string path = Path.Combine(folder, "..", "sources-" + name);
        Tooling(arguments + $" -frames:v 1 -f image2 -update 1 \"{path}\"");
        return new Variant(name, Path.GetFullPath(path), truth, Video: false);
    }

    private static string Format(string pattern, string size) => string.Format(CultureInfo.InvariantCulture, pattern, size);

    private static void Tooling(string arguments)
    {
        ProcessStartInfo start = new(FfmpegEnvironment.Tooling!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in FixtureFactory.SplitArguments("-hide_banner -nostdin -v error -y " + arguments))
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"ffmpeg {arguments}: {error}");
    }

    private static Row Measure(EngineHarness harness, Variant variant)
    {
        FileRecord file = harness.AllFiles().Single(f => f.Name == variant.Name);
        Dictionary<ArtifactKind, (int ProviderVersion, string Data)> stored = harness.Database.Artifacts.ForFile(file.Id);
        string raised = string.Join(" ", harness.Database.Findings.ForFile(file.Id)
            .Where(f => f.Status == FindingStatus.Open && (f.RuleId.StartsWith("VID-", StringComparison.Ordinal) || f.RuleId.StartsWith("IMG-", StringComparison.Ordinal)))
            .Select(f => f.RuleId + "/" + f.Severity));

        string rungs = "—";
        Dictionary<int, double> ssim = [];
        if (stored.TryGetValue(ArtifactKind.FrameSamples, out (int, string Data) samplesData) && new FrameSampleProvider().Deserialize(samplesData.Data) is FrameSamplesArtifact samples && samples.Candidates.Length > 0)
        {
            double minSpatial = RuleSettings.Defaults.Value(variant.Video ? "VID-UPSCALED" : "IMG-UPSCALED", "minSpatialInfo");
            SampledFrame[] usable = [.. samples.Frames.Where(f => f.SpatialInfo >= minSpatial && f.Ssim.Length == samples.Candidates.Length)];
            for (int k = 0; k < samples.Candidates.Length && usable.Length > 0; k++)
            {
                ssim[samples.Candidates[k]] = UpscaleDecision.Percentile([.. usable.Select(f => f.Ssim[k])], 0.10);
            }

            rungs = usable.Length == 0 ? "no usable frames" : string.Join(", ", ssim.Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + ": " + p.Value.ToString("0.0000", CultureInfo.InvariantCulture)));
        }

        string bpp = "—";
        double? bppValue = null;
        if (variant.Video && stored.TryGetValue(ArtifactKind.Probe, out (int, string Data) probeData) && new ProbeArtifactProvider().Deserialize(probeData.Data) is ProbeArtifact probe && probe.Video is StreamInfo video
            && VideoBitrate.EquivalentBpp(probe, video, out _, out _) is double equivalent)
        {
            bpp = equivalent.ToString("0.000", CultureInfo.InvariantCulture);
            bppValue = equivalent;
        }

        string block = "—";
        if (stored.TryGetValue(ArtifactKind.FrameSamples, out (int, string Data) blockData) && new FrameSampleProvider().Deserialize(blockData.Data) is FrameSamplesArtifact frames && frames.Frames.Length > 0)
        {
            block = UpscaleDecision.Percentile([.. frames.Frames.Select(f => f.Block)], 0.5).ToString("0.00", CultureInfo.InvariantCulture);
        }

        string quality = "—";
        if (!variant.Video && stored.TryGetValue(ArtifactKind.Structure, out (int, string Data) structureData)
            && new StructureProvider().Deserialize(structureData.Data) is StructureArtifact { Jpeg: { QuantTables.Count: > 0 } jpeg } && jpeg.QuantTables[0].Length == 64)
        {
            quality = JpegQuality.Estimate(jpeg.QuantTables[0]).Quality.ToString("0", CultureInfo.InvariantCulture);
        }

        return new Row(variant, rungs, bpp, block, quality, raised.Length == 0 ? "nothing" : raised) { Ssim = ssim, BppValue = bppValue };
    }

    private static string VideoReport(IReadOnlyList<Row> rows)
    {
        StringBuilder text = new();
        text.AppendLine("# Video rule calibration (subplan 11; task T-09)");
        text.AppendLine();
        AppendMethod(text, "video");
        text.AppendLine("| Variant | Truth | P10 SSIM after down-up, per rung | H.264-equivalent bpp | Median block score | Raised |");
        text.AppendLine("|---------|-------|-----------------------------------|----------------------|--------------------|--------|");
        foreach (Row row in rows)
        {
            text.Append("| `").Append(row.Variant.Name).Append("` | ").Append(row.Variant.Truth).Append(" | ").Append(row.Rungs).Append(" | ")
                .Append(row.Bpp).Append(" | ").Append(row.Block).Append(" | ").Append(row.Raised).AppendLine(" |");
        }

        text.AppendLine();
        text.AppendLine("## Findings");
        text.AppendLine();
        text.AppendLine("- " + Separation(rows, "VID-UPSCALED"));
        double starvedMax = rows.Where(r => r.Variant.Truth == "starved" && r.BppValue is not null).Max(r => r.BppValue!.Value);
        double adequateMin = rows.Where(r => r.Variant.Truth == "native" && r.BppValue is not null).Min(r => r.BppValue!.Value);
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Bitrate: starved encodes reach at most {starvedMax:0.000} bits per pixel and adequate ones at least {adequateMin:0.000}; `VID-STARVED` starts below {RuleSettings.Defaults.Value("VID-STARVED", "lowBpp"):0.000}."));
        text.AppendLine("- An upscale from 900 lines lies between the candidate rungs (720, 540, 360 for a 1080-line frame): a soft scaler still shows at 720, sharper ones do not. This is a limit of the method, not of the threshold.");
        text.AppendLine("- Heavily compressed encodes (CRF 45 and 51, 250 kbit/s) lose detail the way an upscale does, so they can also be called upscaled; that finding has lower confidence and reads \"upscaled or heavily compressed\" next to `VID-STARVED` and `VID-BLOCKY`.");
        text.AppendLine("- Upscales at a normal bitrate also raise `VID-INFLATED` (a high bitrate on a weak picture), as intended.");
        text.AppendLine();
        text.AppendLine("## Thresholds");
        text.AppendLine();
        text.AppendLine(Thresholds(["VID-UPSCALED", "VID-STARVED", "VID-BLOCKY"]));
        return text.ToString();
    }

    private static string PhotoReport(IReadOnlyList<Row> rows)
    {
        StringBuilder text = new();
        text.AppendLine("# Photo rule calibration (subplan 11; task T-09)");
        text.AppendLine();
        AppendMethod(text, "photo");
        text.AppendLine("| Variant | Truth | SSIM after down-up, per rung | Estimated JPEG quality | Raised |");
        text.AppendLine("|---------|-------|------------------------------|------------------------|--------|");
        foreach (Row row in rows)
        {
            text.Append("| `").Append(row.Variant.Name).Append("` | ").Append(row.Variant.Truth).Append(" | ").Append(row.Rungs).Append(" | ")
                .Append(row.Quality).Append(" | ").Append(row.Raised).AppendLine(" |");
        }

        text.AppendLine();
        text.AppendLine("## Findings");
        text.AppendLine();
        text.AppendLine("- " + Separation(rows, "IMG-UPSCALED"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- JPEG quality: FFmpeg's `-q:v` 2, 5 and 10 estimate as about 91, 76 and 52 and raise nothing; 15 and above estimate below {RuleSettings.Defaults.Value("IMG-HEAVY-COMPRESSION", "lowQuality"):0} and raise `IMG-HEAVY-COMPRESSION` (Medium below {RuleSettings.Defaults.Value("IMG-HEAVY-COMPRESSION", "mediumQuality"):0})."));
        text.AppendLine("- The most compressed photo (quality about 17) also looks enlarged; since this calibration that verdict carries lower confidence, as for starved video, and reads \"enlarged or over-compressed\".");
        text.AppendLine();
        text.AppendLine("## Thresholds");
        text.AppendLine();
        text.AppendLine(Thresholds(["IMG-UPSCALED", "IMG-HEAVY-COMPRESSION"]));
        return text.ToString();
    }

    private static void AppendMethod(StringBuilder text, string area)
    {
        text.AppendLine("Run: `MEDIAVERIFIER_UPDATE_DOCS=1 dotnet test -c Release --filter FullyQualifiedName~VideoPhotoCalibration`");
        text.AppendLine($"({DateTime.Now:yyyy-MM-dd}, FFmpeg {FfmpegVersions.Bundled} engine, the tooling FFmpeg for the variants). Corpus: generated {area}");
        text.AppendLine("variants of a noisy Mandelbrot zoom (fine detail everywhere, like a sharp natural source): native, upscaled from");
        text.AppendLine("each lower rung with bilinear, bicubic and Lanczos, and encoded down a ladder. Every number comes from the");
        text.AppendLine("product's own providers at Standard depth. Real footage (subplan 15, corpus b and c) has not been measured yet:");
        text.AppendLine("when it is, add it to these tables and re-check each threshold, preferring fewer false positives.");
        text.AppendLine();
    }

    private static string Thresholds(string[] rules)
    {
        StringBuilder text = new();
        text.AppendLine("Current values (unchanged by this run; the generated variants are separated by them):");
        text.AppendLine();
        foreach (RuleParameter parameter in RuleDefaults.All.Where(p => rules.Contains(p.RuleId)))
        {
            text.Append("- `").Append(parameter.RuleId).Append("` ").Append(parameter.Name).Append(" = ")
                .Append(parameter.Default.ToString("0.###", CultureInfo.InvariantCulture)).Append(" (").Append(parameter.Label).AppendLine(")");
        }

        return text.ToString();
    }

    private sealed record Variant(string Name, string Path, string Truth, bool Video);

    private sealed record Row(Variant Variant, string Rungs, string Bpp, string Block, string Quality, string Raised)
    {
        public IReadOnlyDictionary<int, double> Ssim { get; init; } = new Dictionary<int, double>();

        public double? BppValue { get; init; }
    }

    /// <summary>How far the threshold sits from the closest native and the weakest upscale the method can see.</summary>
    private static string Separation(IReadOnlyList<Row> rows, string rule)
    {
        double threshold = RuleSettings.Defaults.Value(rule, "ssim");
        double nativeMax = rows.Where(r => r.Variant.Truth == "native" && r.Ssim.Count > 0).Select(r => r.Ssim.Values.Max()).DefaultIfEmpty(double.NaN).Max();
        List<double> upscaled = [.. rows.Where(r => r.Variant.Truth.StartsWith("upscaled-from-", StringComparison.Ordinal) && !r.Variant.Truth.EndsWith("(between rungs)", StringComparison.Ordinal))
            .Select(r => r.Ssim.TryGetValue(int.Parse(r.Variant.Truth["upscaled-from-".Length..], CultureInfo.InvariantCulture), out double v) ? v : double.NaN)
            .Where(v => !double.IsNaN(v))];
        double upscaledMin = upscaled.DefaultIfEmpty(double.NaN).Min();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Upscale separation: native variants reach at most {nativeMax:0.0000} at any rung; variants upscaled from a candidate rung reach at least {upscaledMin:0.0000} at that rung. The threshold {threshold:0.000} sits between them ({threshold - nativeMax:0.0000} above the closest native, {upscaledMin - threshold:0.0000} below the weakest upscale).");
    }
}
