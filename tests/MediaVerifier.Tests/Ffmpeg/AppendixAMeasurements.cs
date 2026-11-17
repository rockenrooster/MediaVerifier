using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Ffmpeg;

/// <summary>
/// Task FFB-05: repeats the experiments of appendix A (sections 2–4 and 7–12) with the FFmpeg engine and writes what it
/// printed to a Markdown report, from which the appendix is updated whenever the bundled FFmpeg changes. Does nothing
/// unless <c>MEDIAVERIFIER_APPENDIX_REPORT</c> names the report file; a manual CI run with "appendix" set runs it
/// against the bundle and prints the report in the log.
/// </summary>
public sealed partial class AppendixAMeasurements
{
    [FixtureFact]
    public async Task Measure()
    {
        string? report = Environment.GetEnvironmentVariable("MEDIAVERIFIER_APPENDIX_REPORT");
        if (string.IsNullOrWhiteSpace(report))
        {
            return;
        }

        using TempDirectory temp = new("appendix");
        Lab lab = new(FfmpegEnvironment.Engine!, temp.Path);
        await lab.HeaderAsync();
        await lab.VisibilityAsync();
        await lab.MessagesAsync();
        await lab.StandardPassAsync();
        await lab.WavPipeAsync();
        await lab.ProbeJsonAsync();
        await lab.StillImagesAsync();
        await lab.QualityFiltersAsync();
        await lab.DownUpSsimAsync();
        await File.WriteAllTextAsync(report, lab.Text);
    }

    [GeneratedRegex(@"(?<=@ |\()(?:0x)?[0-9A-Fa-f]{12,16}(?=[\])])", RegexOptions.CultureInvariant)]
    private static partial Regex Address();

    [GeneratedRegex(@"lavfi\.(block|blur)=([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BlockOrBlur();

    private sealed record Run(int ExitCode, byte[] Stdout, string[] Stderr, TimeSpan Elapsed)
    {
        public string StdoutText => Encoding.UTF8.GetString(Stdout);
    }

    private sealed class Lab(FfmpegLocation engine, string work)
    {
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;
        private readonly StringBuilder text = new();

        public string Text => text.ToString();

        public async Task HeaderAsync()
        {
            Run version = await RunAsync(true, "-hide_banner", "-version");
            string bundle = File.Exists(engine.BundleManifest) && JsonNode.Parse(File.ReadAllText(engine.BundleManifest)) is JsonObject manifest
                ? string.Join(", ", manifest.Where(p => p.Value is JsonValue).Select(p => $"{p.Key} {p.Value}"))
                : "none (not the bundle)";
            text.AppendLine("# Appendix A measurements")
                .AppendLine()
                .AppendLine(C, $"Engine: `{version.StdoutText.Split('\n')[0].Trim()}`; bundle: {bundle}; {DateTime.UtcNow:yyyy-MM-dd} UTC, {Environment.OSVersion}.")
                .AppendLine("Pointers in log lines are replaced by `X`. Repeated lines are shown once with their count.")
                .AppendLine();
        }

        // Section 2: what the log level and the error-detection flags let through.
        public async Task VisibilityAsync()
        {
            string bad1 = Prepare("src16.flac", "bad1.flac", p => Mutate.FlipByte(p, 0.5));
            string badmid = Prepare("fs.mp4", "badmid.mp4", p => Mutate.RandomBlock(p, 0.5, 2000, 1));
            string flip30 = Prepare("fs.mp4", "flip30.mp4", p => Mutate.FlipByte(p, 0.3));
            string png = Prepare("img.png", "bad1.png", p => Mutate.FlipByte(p, 0.5));
            Section("2. Log level and error-detection flags");
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "error", "-i", bad1, "-f", "null", "-");
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "error", "-err_detect", "crccheck", "-i", bad1, "-f", "null", "-");
            Run help = await RunAsync(true, "-hide_banner", "-h", "full");
            Lines("`ffmpeg -h full`, the err_detect options", help.StdoutText.Split('\n').Where(l => l.TrimStart().StartsWith("-err_detect", StringComparison.Ordinal)));
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "error", "-i", badmid, "-map", "0:v:0", "-f", "null", "-");
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "repeat+level+warning", "-i", badmid, "-map", "0:v:0", "-f", "null", "-");
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "repeat+level+warning", "-i", flip30, "-map", "0:v:0", "-f", "null", "-");
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "repeat+level+warning", "-skip_frame", "nokey", "-i", badmid, "-map", "0:v:0", "-f", "null", "-");
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "repeat+level+warning", "-skip_frame", "nokey", "-i", flip30, "-map", "0:v:0", "-f", "null", "-");
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "repeat+level+warning", "-i", png, "-f", "null", "-");
            await ShowAsync(true, "-hide_banner", "-nostdin", "-v", "repeat+level+warning", "-max_error_rate", "1", "-err_detect", "crccheck+bitstream+buffer", "-i", png, "-f", "null", "-");
        }

        // Sections 3 and 4: every message per format and damage, with the app's own decode flags.
        public async Task MessagesAsync()
        {
            Section("3–4. Messages per format and damage (app flags: Standard + Decode)");
            await CaseAsync("FLAC, one byte", Prepare("src16.flac", "flac1.flac", p => Mutate.FlipByte(p, 0.5)));
            await CaseAsync("FLAC, 4 KiB zeros", Prepare("src16.flac", "flaczero.flac", p => Mutate.ZeroBlock(p, 0.5)));
            await CaseAsync("MP3, truncated at 55 %", Prepare("m128.mp3", "trunc55.mp3", p => Mutate.Truncate(p, 0.55)));
            await CaseAsync("MP3, 3,000 random bytes", Prepare("m128.mp3", "rand.mp3", p => Mutate.RandomBlock(p, 0.5, 3000, 1)));
            await CaseAsync("AAC in M4A, 1,500 random bytes", Prepare("a128.m4a", "rand.m4a", p => Mutate.RandomBlock(p, 0.5, 1500, 1)));
            await CaseAsync("Opus in Ogg, two bytes", Prepare("o128.opus", "two.opus", p => { Mutate.FlipByte(p, 0.4); Mutate.FlipByte(p, 0.6); }));
            await CaseAsync("MP4, index at end, truncated at 60 %", Prepare("native1080.mp4", "trunc_nomoov.mp4", p => Mutate.Truncate(p, 0.6)));
            await CaseAsync("MP4, index at front, truncated at 60 %", Prepare("fs.mp4", "trunc_front.mp4", p => Mutate.Truncate(p, 0.6)));
            string av = Prepare("av.mp4", "avbad.mp4", p => Mutate.RandomBlock(p, 0.5, 2000, 1));
            await CaseAsync("MP4 with AAC audio, 2,000 random bytes at 50 % (Deep: every frame)", av);
            await StandardAsync("MP4 with AAC audio, 2,000 random bytes at 50 % (Standard: key frames and audio)", av);
            await CaseAsync("MKV, truncated at 60 %", Prepare("t.mkv", "trunc.mkv", p => Mutate.Truncate(p, 0.6)));
            await CaseAsync("JPEG, truncated at 60 %", Prepare("img.jpg", "trunc.jpg", p => Mutate.Truncate(p, 0.6)));
            await CaseAsync("JPEG, 4 KiB zeros", Prepare("img.jpg", "zero.jpg", p => Mutate.ZeroBlock(p, 0.5)));
            await CaseAsync("JPEG, one byte", Prepare("img.jpg", "one.jpg", p => Mutate.FlipByte(p, 0.5)));
            await CaseAsync("PNG, one byte", Prepare("img.png", "one.png", p => Mutate.FlipByte(p, 0.5)));
            await CaseAsync("PNG, truncated", Prepare("img.png", "trunc.png", p => Mutate.Truncate(p, 0.6)));
            await CaseAsync("WebP (lossy), three bytes", Prepare("img.webp", "three.webp", p => { Mutate.FlipByte(p, 0.4); Mutate.FlipByte(p, 0.5); Mutate.FlipByte(p, 0.6); }));
            await CaseAsync("WebP, truncated", Prepare("img.webp", "trunc.webp", p => Mutate.Truncate(p, 0.6)));
            string textMp3 = Path.Combine(work, "text.mp3");
            await File.WriteAllTextAsync(textMp3, "This is not an MP3 file, just text with a misleading name.\n");
            await CaseAsync("Text named .mp3", textMp3);
            string empty = Path.Combine(work, "empty.flac");
            await File.WriteAllBytesAsync(empty, []);
            await CaseAsync("Empty .flac", empty);
        }

        // Section 7: the combined Standard video pass on a healthy file.
        public async Task StandardPassAsync()
        {
            Section("7. Combined Standard video pass (healthy av.mp4)");
            await StandardAsync("Healthy av.mp4", FixtureFactory.Get("av.mp4"), fullProgress: true);
        }

        // Section 8: decoded audio through a WAV pipe.
        public async Task WavPipeAsync()
        {
            Section("8. Audio through a WAV pipe (pad24.flac)");
            string pad24 = FixtureFactory.Get("pad24.flac");
            Run plain = await RunAsync(true, [.. FfmpegArgs.Standard(FfTool.Ffmpeg), "-i", pad24, "-map", "0:a:0", "-c:a", "pcm_f32le", "-f", "wav", "-"]);
            Run bare = await RunAsync(true, [.. FfmpegArgs.Standard(FfTool.Ffmpeg), "-i", pad24, "-map", "0:a:0", "-map_metadata", "-1", "-fflags", "+bitexact", "-c:a", "pcm_f32le", "-f", "wav", "-"]);
            Lines("Default: first 96 bytes", [Hex(plain.Stdout, 96), "LIST chunk text: " + Printable(plain.Stdout, 160)]);
            Lines("`-map_metadata -1 -fflags +bitexact`: first 64 bytes", [Hex(bare.Stdout, 64)]);
        }

        // Section 9: ffprobe's JSON.
        public async Task ProbeJsonAsync()
        {
            Section("9. ffprobe JSON");
            JsonNode av = await ProbeJsonAsync(FixtureFactory.Get("av.mp4"));
            JsonObject format = av["format"]!.AsObject();
            JsonObject video = av["streams"]!.AsArray().First(s => (string?)s!["codec_type"] == "video")!.AsObject();
            JsonNode pad24 = await ProbeJsonAsync(FixtureFactory.Get("pad24.flac"));
            JsonNode mp3 = await ProbeJsonAsync(FixtureFactory.Get("m128.mp3"));
            JsonObject flac = pad24["streams"]![0]!.AsObject();
            Lines("av.mp4", [
                "format keys: " + string.Join(", ", format.Select(p => p.Key).Order(StringComparer.Ordinal)),
                "video stream keys: " + string.Join(", ", video.Select(p => p.Key).Order(StringComparer.Ordinal)),
                $"format.duration is a JSON {format["duration"]!.GetValueKind()}; width is a JSON {video["width"]!.GetValueKind()}",
            ]);
            Lines("pad24.flac and m128.mp3", [
                $"sample_fmt={flac["sample_fmt"]}, bits_per_sample={flac["bits_per_sample"]}, bits_per_raw_sample={flac["bits_per_raw_sample"]}",
                $"m128.mp3 probe_score={mp3["format"]?["probe_score"]}",
            ]);
        }

        // Section 10: still-image demuxers and awkward names.
        public async Task StillImagesAsync()
        {
            Section("10. Still-image demuxers and awkward names");
            string jpg = FixtureFactory.Get("img.jpg");
            string png = FixtureFactory.Get("img.png");
            List<string> demuxers = [];
            foreach ((string name, string source) in new[] { ("img.jpg", jpg), ("img.png", png), ("img.webp", FixtureFactory.Get("img.webp")), ("pngnamed.jpg", png) })
            {
                string copy = Path.Combine(work, name);
                File.Copy(source, copy, true);
                Run probe = await RunAsync(false, "-hide_banner", "-loglevel", "error", "-show_entries", "format=format_name:stream=codec_name", "-of", "compact=p=0:nk=1", "-i", copy);
                demuxers.Add($"{name}: {probe.StdoutText.Replace('\n', ' ').Trim()} (exit {probe.ExitCode})");
            }

            Lines("Demuxer and codec by name", demuxers);
            List<string> names = [];
            foreach ((string name, string source) in new[] { ("a%b.jpg", jpg), ("100% real.jpg", jpg), ("a%d.jpg", jpg), ("a%03d.jpg", jpg), ("x%d.png", png) })
            {
                string copy = Path.Combine(work, name);
                File.Copy(source, copy, true);
                Run plain = await RunAsync(false, "-hide_banner", "-loglevel", "error", "-show_entries", "format=format_name", "-of", "compact=p=0:nk=1", "-i", copy);
                Run none = await RunAsync(false, "-hide_banner", "-loglevel", "error", "-pattern_type", "none", "-show_entries", "format=format_name", "-of", "compact=p=0:nk=1", "-i", copy);
                names.Add($"{name}: plain exit {plain.ExitCode} {First(plain)}; -pattern_type none exit {none.ExitCode} {First(none)}");
            }

            string mp3 = FixtureFactory.Get("m128.mp3");
            Run probeMp3 = await RunAsync(false, "-hide_banner", "-loglevel", "error", "-pattern_type", "none", "-show_entries", "format=format_name", "-of", "compact=p=0:nk=1", "-i", mp3);
            Run decodeMp3 = await RunAsync(true, "-hide_banner", "-nostdin", "-loglevel", "error", "-pattern_type", "none", "-i", mp3, "-f", "null", "-");
            string percent = Path.Combine(work, "a%d.jpg");
            Run pipe = await RunAsync(true, "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "jpeg_pipe", "-i", percent, "-f", "null", "-");
            Run image2 = await RunAsync(true, "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "image2", "-pattern_type", "none", "-i", percent, "-f", "null", "-");
            names.Add($"-pattern_type none before an MP3: ffprobe exit {probeMp3.ExitCode} {First(probeMp3)}; ffmpeg exit {decodeMp3.ExitCode} {First(decodeMp3)}");
            names.Add($"a%d.jpg forced: -f jpeg_pipe exit {pipe.ExitCode} {First(pipe)}; -f image2 -pattern_type none exit {image2.ExitCode} {First(image2)}");
            Lines("Names with %", names);

            string folder = work;
            string[] parts = ["Été à Málaga – Ünïcödé", "日本語のフォルダー名", "symbols ~!@#$&()[]{}'+,;=", "naïve café crème brûlée"];
            for (int i = 0; folder.Length < 370; i++)
            {
                folder = Path.Combine(folder, parts[i % parts.Length]);
            }

            Directory.CreateDirectory(folder);
            string longPath = Path.Combine(folder, "写真 – photo.jpg");
            File.Copy(jpg, longPath, true);
            Run longProbe = await RunAsync(false, "-hide_banner", "-loglevel", "error", "-show_entries", "format=format_name", "-of", "compact=p=0:nk=1", "-i", longPath);
            Run whitelist = await RunAsync(true, [.. FfmpegArgs.Standard(FfTool.Ffmpeg), "-i", png, "-c:v", "png", "-f", "image2pipe", "-"]);
            Lines("Long path and protocol whitelist", [
                $"{longPath.Length}-character path (accented Latin, Japanese, symbols): exit {longProbe.ExitCode} {First(longProbe)}",
                $"-protocol_whitelist file,pipe, file in, pipe out: exit {whitelist.ExitCode}, {whitelist.Stdout.Length} bytes, {whitelist.Stderr.Length} stderr lines",
            ]);
        }

        // Section 11: where each quality filter reports.
        public async Task QualityFiltersAsync()
        {
            Section("11. Quality filters");
            string native = FixtureFactory.Get("native1080.mp4");
            string[] info = ["-hide_banner", "-nostdin", "-loglevel", "repeat+level+info"];
            Run ssim = await RunAsync(true, [.. info, "-i", native, "-i", FixtureFactory.Get("up720.mp4"), "-filter_complex", "[0:v][1:v]ssim=stats_file=ssim.log", "-frames:v", "3", "-f", "null", "-"]);
            Lines("ssim: stats file, then the summary line", [.. ReadLines("ssim.log", 2), .. Matching(ssim, "SSIM")]);
            await RunAsync(true, [.. info, "-i", native, "-vf", "siti=print_summary=0,blockdetect,blurdetect,metadata=mode=print:file=frame.log", "-frames:v", "1", "-f", "null", "-"]);
            Lines("siti, blockdetect, blurdetect + metadata print", ReadLines("frame.log", 8));
            Lines("idet", Matching(await RunAsync(true, [.. info, "-i", FixtureFactory.Get("interlaced.mp4"), "-vf", "idet", "-f", "null", "-"]), "detection"));
            Lines("freezedetect", Matching(await RunAsync(true, [.. info, "-i", FixtureFactory.Get("frozen.mp4"), "-vf", "freezedetect=n=0.003:d=10", "-f", "null", "-"]), "freeze"));
            Lines("blackdetect", Matching(await RunAsync(true, [.. info, "-i", FixtureFactory.Get("black.mp4"), "-vf", "blackdetect=d=30:pix_th=0.10", "-f", "null", "-"]), "black_"));
            Run crop = await RunAsync(true, [.. info, "-i", native, "-vf", "cropdetect", "-frames:v", "1", "-f", "null", "-"]);
            Lines("cropdetect (the app does not use it)", [$"exit {crop.ExitCode}", .. Matching(crop, "crop=", "filter", "Error")]);
            Lines("volumedetect", Matching(await RunAsync(true, [.. info, "-i", FixtureFactory.Get("src16.flac"), "-af", "volumedetect", "-f", "null", "-"]), "mean_volume", "max_volume"));
            Lines("ebur128=peak=true:framelog=quiet", Matching(await RunAsync(true, [.. info, "-i", FixtureFactory.Get("clip.flac"), "-af", "ebur128=peak=true:framelog=quiet", "-f", "null", "-"]), "I:", "LRA:", "Peak:"));
            Lines("astats (overall, pad24.flac)", Matching(await RunAsync(true, [.. info, "-i", FixtureFactory.Get("pad24.flac"), "-af", "astats=measure_perchannel=none", "-f", "null", "-"]), "DC offset", "Peak level dB", "Flat factor", "Peak count", "Bit depth"));
            List<string> means = [];
            foreach (string name in new[] { "native1080.mp4", "starved.mp4" })
            {
                File.Delete(Path.Combine(work, "bb.log"));
                await RunAsync(true, [.. info, "-i", FixtureFactory.Get(name), "-vf", "blockdetect,blurdetect,metadata=mode=print:file=bb.log", "-f", "null", "-"]);
                MatchCollection values = BlockOrBlur().Matches(string.Join('\n', ReadLines("bb.log", int.MaxValue)));
                double Mean(string key) => values.Where(m => m.Groups[1].Value == key).Select(m => double.Parse(m.Groups[2].Value, C)).DefaultIfEmpty(double.NaN).Average();
                means.Add(string.Create(C, $"{name}: blockiness {Mean("block"):0.00}, blur {Mean("blur"):0.00} (mean of {values.Count / 2} frames)"));
            }

            Lines("Blockiness and blur, CRF 20 versus starved", means);
        }

        // Section 12: down-up SSIM with the frame-sampling provider's own graph.
        public async Task DownUpSsimAsync()
        {
            Section("12. Down-up SSIM (the frame-sampling provider's graph)");
            List<string> rows = [];
            foreach (string name in new[] { "native1080.mp4", "up720.mp4", "up540.mp4" })
            {
                (int cropWidth, int cropHeight) = FrameSampling.Crop(1920, 1080, 0.6);
                int[] candidates = FrameSampling.VideoCandidates(1080);
                (int Width, int Height)[] sizes = [.. candidates.Select(c => FrameSampling.Scaled(cropWidth, cropHeight, c, 1080))];
                double[] times = FrameSampling.SampleTimes(6.0);
                List<string> args = [.. FfmpegArgs.Standard(FfTool.Ffmpeg)];
                foreach (double time in times)
                {
                    args.AddRange(["-noaccurate_seek", "-ss", time.ToString("0.###", C), "-i", FixtureFactory.Get(name)]);
                }

                args.AddRange(["-filter_complex", FrameSampling.Graph(times.Length, cropWidth, cropHeight, candidates, sizes, false, FrameSampling.Window(1920, 1080, cropWidth, cropHeight, false))]);
                args.AddRange(FrameSampling.Outputs(candidates.Length));
                Run run = await RunAsync(true, [.. args]);
                IEnumerable<string> perCandidate = candidates.Select(c =>
                    $"{c}p: " + string.Join(", ", FrameSampling.ParseSsim(string.Join('\n', ReadLines($"c{c}.log", int.MaxValue))).Select(v => v.ToString("0.000", C))));
                rows.Add(string.Create(C, $"{name} ({times.Length} frames, crop {cropWidth}x{cropHeight}, {run.Elapsed.TotalSeconds:0.00} s, exit {run.ExitCode}, {run.Stderr.Length} stderr lines): {string.Join("; ", perCandidate)}"));
            }

            Lines("SSIM-Y per frame", rows);
        }

        private async Task CaseAsync(string title, string file)
        {
            Run decode = await RunAsync(true, [.. FfmpegArgs.Standard(FfTool.Ffmpeg), .. FfmpegArgs.Decode(), "-i", file, "-map", "0", "-f", "null", "-", "-progress", "pipe:1", "-nostats"]);
            Run probe = await RunAsync(false, [.. FfmpegArgs.Standard(FfTool.Ffprobe), "-count_frames", "-show_entries", "format=format_name,duration:stream=codec_name,duration,nb_frames,nb_read_frames", "-of", "compact", "-i", file]);
            text.AppendLine(C, $"#### {title} (`{Path.GetFileName(file)}`)").AppendLine();
            Block($"decode: exit {decode.ExitCode}, {Progress(decode.StdoutText, false)}", decode.Stderr);
            Block($"ffprobe -count_frames: exit {probe.ExitCode}", [.. probe.StdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), .. probe.Stderr]);
        }

        private async Task StandardAsync(string title, string file, bool fullProgress = false)
        {
            File.Delete(Path.Combine(work, "streamhash.txt"));
            Run run = await RunAsync(true, [
                .. FfmpegArgs.Standard(FfTool.Ffmpeg), .. FfmpegArgs.Decode(), "-threads", "4", "-skip_frame", "nokey", "-i", file,
                "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-",
                "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-f", "streamhash", "-hash", "md5", "streamhash.txt",
                "-progress", "pipe:1", "-nostats"]);
            text.AppendLine(C, $"#### {title} (`{Path.GetFileName(file)}`)").AppendLine();
            Block(string.Create(C, $"Standard pass: exit {run.ExitCode}, {run.Elapsed.TotalMilliseconds:0} ms, {Progress(run.StdoutText, fullProgress)}"), run.Stderr);
            Block("streamhash.txt", ReadLines("streamhash.txt", 4));
        }

        private async Task<JsonNode> ProbeJsonAsync(string file)
        {
            Run run = await RunAsync(false, [.. FfmpegArgs.Standard(FfTool.Ffprobe), "-show_format", "-show_streams", "-of", "json", "-i", file]);
            return JsonNode.Parse(run.StdoutText) ?? new JsonObject();
        }

        private async Task ShowAsync(bool ffmpeg, params string[] args)
        {
            Run run = await RunAsync(ffmpeg, args);
            string command = (ffmpeg ? "ffmpeg " : "ffprobe ") + string.Join(' ', args.Select(a => a.StartsWith(work, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(a) : a));
            Block($"`{command}`: exit {run.ExitCode}", run.Stderr);
        }

        private async Task<Run> RunAsync(bool ffmpeg, params string[] args)
        {
            ProcessStartInfo start = new(ffmpeg ? engine.FfmpegExe : engine.FfprobeExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = work,
            };
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            Stopwatch watch = Stopwatch.StartNew();
            using Process process = Process.Start(start)!;
            process.StandardInput.Close();
            using MemoryStream stdout = new();
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                throw new TimeoutException("FFmpeg did not finish: " + string.Join(' ', args));
            }

            await copy;
            string[] lines = Address().Replace(Folders(await stderr), "X").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new Run(process.ExitCode, stdout.ToArray(), lines, watch.Elapsed);
        }

        /// <summary>Folder names in messages, replaced so the report shows no machine's paths.</summary>
        private string Folders(string message) => message
            .Replace(work + Path.DirectorySeparatorChar, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(FixtureFactory.Root + Path.DirectorySeparatorChar, string.Empty, StringComparison.OrdinalIgnoreCase);

        private string Prepare(string fixture, string name, Action<string> damage)
        {
            string path = Path.Combine(work, name);
            File.Copy(FixtureFactory.Get(fixture), path, true);
            damage(path);
            return path;
        }

        private void Section(string title) => text.AppendLine(C, $"## {title}").AppendLine();

        private void Lines(string title, IEnumerable<string> lines) => Block(title, [.. lines]);

        private void Block(string title, IReadOnlyList<string> lines)
        {
            text.AppendLine(title).AppendLine().AppendLine("```");
            if (lines.Count == 0)
            {
                text.AppendLine("(no output)");
            }

            foreach (IGrouping<string, string> line in lines.GroupBy(l => l, StringComparer.Ordinal).Take(40))
            {
                text.AppendLine(line.Count() > 1 ? string.Create(C, $"{line.Key}   (×{line.Count()})") : line.Key);
            }

            text.AppendLine("```").AppendLine();
        }

        private string[] ReadLines(string name, int count)
        {
            string path = Path.Combine(work, name);
            return File.Exists(path) ? [.. File.ReadLines(path).Take(count)] : [$"({name} was not written)"];
        }

        private static string[] Matching(Run run, params string[] words) =>
            [.. run.Stderr.Where(l => words.Any(w => l.Contains(w, StringComparison.Ordinal)))];

        private static string First(Run run) => run.Stderr.Length > 0 ? "— " + run.Stderr[0] : run.StdoutText.Split('\n')[0].Trim();

        private static string Progress(string stdout, bool full)
        {
            string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int end = Array.FindLastIndex(lines, l => l.StartsWith("progress=", StringComparison.Ordinal));
            if (end < 0)
            {
                return "no progress block";
            }

            int start = end == 0 ? 0 : Array.FindLastIndex(lines, end - 1, l => l.StartsWith("progress=", StringComparison.Ordinal)) + 1;
            string[] block = lines[start..(end + 1)];
            return full
                ? "last progress block: " + string.Join(' ', block)
                : "last progress: " + string.Join(' ', block.Where(l => l.StartsWith("frame=", StringComparison.Ordinal) || l.StartsWith("out_time=", StringComparison.Ordinal)));
        }

        private static string Hex(byte[] data, int count) =>
            string.Join(' ', data.Take(count).Chunk(4).Select(Convert.ToHexStringLower));

        private static string Printable(byte[] data, int count) =>
            new([.. data.Take(count).Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.')]);
    }
}
