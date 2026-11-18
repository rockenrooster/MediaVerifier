using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis.Providers;

/// <summary>One sampled frame: its time, spatial information, blockiness, blur and SSIM-Y for each candidate.</summary>
public sealed record SampledFrame(double Time, double SpatialInfo, double Block, double Blur, double[] Ssim);

/// <summary>
/// The down-up test of subplan 11 on a handful of frames (video) or the image itself (photo): for each
/// candidate height, how closely the frame is rebuilt after shrinking to it and enlarging back.
/// </summary>
public sealed record FrameSamplesArtifact(
    RunOutcome Outcome,
    LogDigest Log,
    int Width,
    int Height,
    int CropWidth,
    int CropHeight,
    int[] Candidates,
    SampledFrame[] Frames,

    // Why nothing was sampled ("small", "palette", "disabled", "no-video"); null when frames were sampled.
    string? Skipped = null) : IArtifact
{
    [JsonIgnore]
    public bool Sampled => Frames.Length > 0;

    public static FrameSamplesArtifact Skip(string reason, int width = 0, int height = 0) =>
        new(RunOutcome.Completed, LogDigest.Empty, width, height, 0, 0, [], [], reason);
}

/// <summary>The part of the crop the per-frame measures look at (see <see cref="FrameSampling.Window"/>).</summary>
public sealed record MetricsWindow(int X, int Y, int Width, int Height, bool SpatialOnly);

/// <summary>Sample times, crops, candidates, the filter graph and the log parsers (subplan 11, task V-01).</summary>
public static class FrameSampling
{
    /// <summary>Video: 6 samples under 60 s, else one a minute between 8 and 24, spread from 5 % to 95 %.</summary>
    public static double[] SampleTimes(double duration)
    {
        if (duration <= 0)
        {
            return [0];
        }

        int count = duration < 60 ? 6 : Math.Clamp((int)(duration / 60), 8, 24);
        double[] times = new double[count];
        for (int i = 0; i < count; i++)
        {
            times[i] = Math.Round(duration * (0.05 + (0.9 * i / (count - 1))), 3);
        }

        return times;
    }

    /// <summary>The candidate heights for a video by its container height (subplan 11 table).</summary>
    public static int[] VideoCandidates(int height) => height switch
    {
        2160 => [1440, 1080, 720],
        1440 => [1080, 720, 540],
        1080 => [720, 540, 360],
        720 => [540, 480, 360],
        576 or 480 => [360, 240],
        _ => [Even(height * 2 / 3.0), Even(height / 2.0), Even(height / 3.0)],
    };

    /// <summary>Photo candidates: 2/3, 1/2, 1/3 and 1/4 of the size.</summary>
    public static int[] PhotoCandidates(int height) => [Even(height * 2 / 3.0), Even(height / 2.0), Even(height / 3.0), Even(height / 4.0)];

    /// <summary>The centred crop holding <paramref name="share"/> of each dimension, rounded to even numbers.</summary>
    public static (int Width, int Height) Crop(int width, int height, double share) => (Even(width * share), Even(height * share));

    /// <summary>The crop shrunk to a candidate height: the same factor on both sides, rounded to even numbers.</summary>
    public static (int Width, int Height) Scaled(int cropWidth, int cropHeight, int candidate, int height) =>
        (Even(cropWidth * (double)candidate / height), Even(cropHeight * (double)candidate / height));

    /// <summary>
    /// The filter graph: the first frame of each input, concatenated, cropped, then for each candidate a
    /// down-up copy compared by <c>ssim</c> (stats file <c>c{height}.log</c>), and <c>siti</c>,
    /// <c>blockdetect</c> and <c>blurdetect</c> printed to <c>frame.log</c>. Stats files are relative names,
    /// so the graph holds no paths.
    /// </summary>
    public static string Graph(int inputs, int cropWidth, int cropHeight, IReadOnlyList<int> candidates, IReadOnlyList<(int Width, int Height)> sizes, bool deinterlace, MetricsWindow? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(sizes);
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder graph = new();
        for (int i = 0; i < inputs; i++)
        {
            graph.Append(c, $"[{i}:v]{(deinterlace ? "yadif=deint=all," : string.Empty)}trim=end_frame=1,setpts=PTS-STARTPTS[s{i}];");
        }

        for (int i = 0; i < inputs; i++)
        {
            graph.Append(c, $"[s{i}]");
        }

        string[] labels = [.. Enumerable.Range(0, candidates.Count).Select(Label)];
        graph.Append(c, $"concat=n={inputs}:v=1:a=0,crop={cropWidth}:{cropHeight},split={candidates.Count + 2}[o]");
        foreach (string label in labels)
        {
            graph.Append(c, $"[{label}]");
        }

        graph.Append("[m];");
        for (int k = 0; k < candidates.Count; k++)
        {
            string l = labels[k];
            graph.Append(c, $"[{l}]split[{l}1][{l}2];[{l}2]scale={sizes[k].Width}:{sizes[k].Height}:flags=bicubic,scale={cropWidth}:{cropHeight}:flags=bicubic[{l}3];");
            graph.Append(c, $"[{l}1][{l}3]ssim=stats_file=c{candidates[k]}.log[{l}o];");
        }

        string window = metrics is { } m ? string.Create(c, $"crop={m.Width}:{m.Height}:{m.X}:{m.Y},") : string.Empty;
        string measures = metrics is { SpatialOnly: true } ? "siti=print_summary=0" : "siti=print_summary=0,blockdetect,blurdetect";
        graph.Append(c, $"[m]{window}{measures},metadata=mode=print:file=frame.log[mo]");
        return graph.ToString();
    }

    /// <summary>
    /// Where the per-frame measures run: the centre quarter of the crop, placed so that its top-left corner sits on
    /// the frame's 16-pixel grid (block edges keep their phase); photos need only spatial information.
    /// </summary>
    public static MetricsWindow Window(int frameWidth, int frameHeight, int cropWidth, int cropHeight, bool spatialOnly)
    {
        int width = Even(cropWidth / 2.0);
        int height = Even(cropHeight / 2.0);
        int originX = (frameWidth - cropWidth) / 2;
        int originY = (frameHeight - cropHeight) / 2;
        int x = Aligned(originX, (cropWidth - width) / 2, cropWidth - width);
        int y = Aligned(originY, (cropHeight - height) / 2, cropHeight - height);
        return new MetricsWindow(x, y, width, height, spatialOnly);
    }

    private static int Aligned(int origin, int wanted, int max)
    {
        int absolute = (origin + wanted) / 16 * 16;
        return Math.Clamp(absolute - origin, 0, Math.Max(0, max));
    }

    /// <summary>The output maps for <see cref="Graph"/>: every branch to the null muxer.</summary>
    public static IReadOnlyList<string> Outputs(int candidates)
    {
        List<string> args = ["-map", "[o]", "-f", "null", "-"];
        for (int k = 0; k < candidates; k++)
        {
            args.AddRange(["-map", $"[{Label(k)}o]", "-f", "null", "-"]);
        }

        args.AddRange(["-map", "[mo]", "-f", "null", "-"]);
        return args;
    }

    /// <summary>SSIM-Y per frame from a stats file: <c>n:1 Y:0.994236 U:… V:… All:… (dB)</c>.</summary>
    public static List<double> ParseSsim(string text)
    {
        List<double> values = [];
        foreach (string line in (text ?? string.Empty).Split('\n'))
        {
            int y = line.IndexOf(" Y:", StringComparison.Ordinal);
            if (y < 0)
            {
                continue;
            }

            int end = line.IndexOf(' ', y + 3);
            string number = end < 0 ? line[(y + 3)..] : line[(y + 3)..end];
            if (double.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                values.Add(double.IsFinite(value) ? value : 1);
            }
            else if (number.Trim() is "inf" or "nan")
            {
                // A flat frame (black, one colour) is rebuilt exactly.
                values.Add(1);
            }
        }

        return values;
    }

    /// <summary>Per frame <c>lavfi.siti.si</c>, <c>lavfi.block</c> and <c>lavfi.blur</c> from the metadata print.</summary>
    public static List<(double SpatialInfo, double Block, double Blur)> ParseFrameLog(string text)
    {
        List<(double, double, double)> frames = [];
        double si = 0;
        double block = 0;
        double blur = 0;
        bool open = false;
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("frame:", StringComparison.Ordinal))
            {
                if (open)
                {
                    frames.Add((si, block, blur));
                }

                (si, block, blur, open) = (0, 0, 0, true);
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            // Flat frames (black, a single colour) give inf or nan; they carry no detail, so they count as 0.
            if (!double.TryParse(line[(equals + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            {
                value = 0;
            }

            switch (line[..equals])
            {
                case "lavfi.siti.si":
                    si = value;
                    break;
                case "lavfi.block":
                    block = value;
                    break;
                case "lavfi.blur":
                    blur = value;
                    break;
            }
        }

        if (open)
        {
            frames.Add((si, block, blur));
        }

        return frames;
    }

    private static string Label(int k) => ((char)('a' + k)).ToString();

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}

/// <summary>
/// Runs the down-up test (subplan 11, task V-01): one FFmpeg process per file at Standard depth, frames
/// sampled at key frames by fast seeking. Photos use the image itself with an 80 % crop.
/// </summary>
public sealed class FrameSampleProvider() : ArtifactProvider<FrameSamplesArtifact>(new ArtifactCodec<FrameSamplesArtifact>(ArtifactKind.FrameSamples, ProviderJsonContext.Default.FrameSamplesArtifact))
{
    /// <summary>Photos below this many pixels are not tested (subplan 11, IMG-UPSCALED).</summary>
    public const int MinPhotoPixels = 1_000_000;

    /// <summary>Version 2 measures spatial information, blocks and blur on the centre quarter of the crop.</summary>
    public override int Version => 2;

    public override ScanDepth MinDepth => ScanDepth.Standard;

    public override bool Applies(FileContext file) =>
        file.Kind == MediaKind.Video || (file.Kind == MediaKind.Photo && !MediaClassifier.IsRaw(file.Extension));

    /// <summary>
    /// A skip that rests on something that can change is decided again, which runs no FFmpeg: "disabled" once "Check
    /// photos for upscaling" is on again, and "damaged" because the decode it judged may have been redone since.
    /// </summary>
    public override bool CanReuse(IArtifact stored, FileContext file) => stored switch
    {
        FrameSamplesArtifact { Skipped: "damaged" } => false,
        FrameSamplesArtifact { Skipped: "disabled" } => !file.Settings.CheckPhotosForUpscaling,
        _ => true,
    };

    protected override async Task<FrameSamplesArtifact> ProduceTypedAsync(FileContext file)
    {
        ProbeArtifact? probe = await file.GetAsync<ProbeArtifact>().ConfigureAwait(false);
        StreamInfo? video = probe?.Video;
        if (video is not { Width: > 0 and int width, Height: > 0 and int height })
        {
            return FrameSamplesArtifact.Skip("no-video");
        }

        bool photo = file.Kind == MediaKind.Photo;
        if (photo && !file.Settings.CheckPhotosForUpscaling)
        {
            return FrameSamplesArtifact.Skip("disabled", width, height);
        }

        // Quality is judged on whole files: a damaged decode is the corruption rules' business. For a photo that is what
        // the photo rules count (an unknown error line beside a decoded picture is not damage, subplan 07).
        bool whole = photo
            ? await file.GetAsync<ImageDecodeArtifact>().ConfigureAwait(false) is { Outcome: RunOutcome.Completed } image && image.FramesRead != 0 && !Damaged(image.Log.Counts with { Unknown = 0 })
            : await file.GetAsync<VideoDecodeArtifact>().ConfigureAwait(false) is { Outcome: RunOutcome.Completed } decoded && !Damaged(decoded.Video.Counts) && !Damaged(decoded.Container.Counts);
        if (!whole)
        {
            return FrameSamplesArtifact.Skip("damaged", width, height);
        }

        if (photo && (long)width * height < MinPhotoPixels)
        {
            return FrameSamplesArtifact.Skip("small", width, height);
        }

        if (photo && video.PixFmt is "pal8" or "monob" or "monow")
        {
            return FrameSamplesArtifact.Skip("palette", width, height);
        }

        (int cropWidth, int cropHeight) = FrameSampling.Crop(width, height, photo ? 0.8 : 0.6);
        int[] candidates = photo ? FrameSampling.PhotoCandidates(height) : FrameSampling.VideoCandidates(height);
        (int Width, int Height)[] sizes = [.. candidates.Select(c => FrameSampling.Scaled(cropWidth, cropHeight, c, height))];
        double[] times = photo ? [0] : FrameSampling.SampleTimes(probe!.Duration ?? video.Duration ?? 0);
        bool interlaced = !photo && video.FieldOrder is not (null or "progressive" or "unknown");

        List<string> args = [];
        if (photo)
        {
            string? sniffed = await StructureProvider.SniffedImageFormatAsync(file).ConfigureAwait(false);
            args.AddRange(FfmpegArgs.Input(file.FullPath, MediaKind.Photo, sniffed));
        }
        else
        {
            foreach (double time in times)
            {
                args.AddRange(["-noaccurate_seek", "-ss", time.ToString("0.###", CultureInfo.InvariantCulture), "-i", file.FullPath]);
            }
        }

        MetricsWindow window = FrameSampling.Window(width, height, cropWidth, cropHeight, spatialOnly: photo);
        args.AddRange(["-filter_complex", FrameSampling.Graph(times.Length, cropWidth, cropHeight, candidates, sizes, interlaced, window)]);
        args.AddRange(FrameSampling.Outputs(candidates.Length));
        Directory.CreateDirectory(file.TempDirectory);
        foreach (string stale in candidates.Select(c => $"c{c}.log").Append("frame.log"))
        {
            File.Delete(Path.Combine(file.TempDirectory, stale));
        }

        FfResult result = await file.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = args,
                WorkingDirectory = file.TempDirectory,
                IdleTimeout = TimeSpan.FromSeconds(file.Settings.StallSeconds),
                HardTimeout = TimeSpan.FromMinutes(5),
            },
            file.Cancellation).ConfigureAwait(false);

        List<double>[] ssim = [.. candidates.Select(c => FrameSampling.ParseSsim(ReadLog(file.TempDirectory, $"c{c}.log")))];
        List<(double SpatialInfo, double Block, double Blur)> metrics = FrameSampling.ParseFrameLog(ReadLog(file.TempDirectory, "frame.log"));
        int count = Math.Min(metrics.Count, ssim.Length == 0 ? 0 : ssim.Min(s => s.Count));
        SampledFrame[] frames = new SampledFrame[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = new SampledFrame(i < times.Length ? times[i] : 0, metrics[i].SpatialInfo, metrics[i].Block, metrics[i].Blur, [.. ssim.Select(s => s[i])]);
        }

        return new FrameSamplesArtifact(OutcomeMapping.From(result.Outcome), LogDigest.From(result.Log), width, height, cropWidth, cropHeight, candidates, frames);
    }

    private static bool Damaged(SignalCounts c) => c.OpenFailure + c.Truncation + c.DecodeError + c.Concealed + c.Checksum + c.Unknown > 0;

    private static string ReadLog(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
