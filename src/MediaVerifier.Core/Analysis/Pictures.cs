using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Analysis;

/// <summary>
/// Pictures for the drawer, made on demand by the bundled FFmpeg and kept in a disk cache keyed by file id and
/// fingerprint (subplan 09, Detail drawer header; subplan 11, Evidence views; task V-05): the header thumbnail,
/// the frame strip of the sampled frames, and the compare image (the original next to the copy rebuilt from the
/// effective resolution). Images are PNG bytes. Never call it on the UI thread.
/// </summary>
public sealed class PictureRenderer(FfRunner runner, string cacheDirectory)
{
    public const int ThumbnailSize = 128;
    public const int StripWidth = 192;
    public const int StripHeight = 108;
    public const int CompareWidth = 384;
    public const int CompareHeight = 216;

    /// <summary>The thumbnail's filter: fill a <see cref="ThumbnailSize"/> square, then crop the overflow.</summary>
    public static readonly string ThumbnailFilter = string.Create(
        CultureInfo.InvariantCulture,
        $"scale={ThumbnailSize}:{ThumbnailSize}:force_original_aspect_ratio=increase,crop={ThumbnailSize}:{ThumbnailSize}");
    private const int MaxCachedFiles = 400;

    /// <summary>The time the last fresh compare image took (the acceptance asks for under 2 s).</summary>
    public TimeSpan LastCompareTime { get; private set; }

    /// <summary>
    /// A square thumbnail: the photo itself, a video's frame at 10 % of its length, or an audio file's cover art.
    /// Null when there is nothing to show (audio without cover art) or the file cannot be read.
    /// </summary>
    public Task<byte[]?> ThumbnailAsync(string path, string cacheKey, MediaKind kind, double durationSeconds, CancellationToken token)
    {
        List<string> args = [];
        if (kind == MediaKind.Video && durationSeconds > 0)
        {
            args.AddRange(["-noaccurate_seek", "-ss", (durationSeconds * 0.1).ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(FfmpegArgs.Input(path, kind, null));
        args.AddRange(["-map", "0:v:0", "-frames:v", "1"]);
        args.AddRange(["-vf", ThumbnailFilter]);
        args.AddRange(["-c:v", "png", "-f", "image2pipe", "-"]);
        return CachedAsync(cacheKey + "-thumb", args, token);
    }

    /// <summary>One PNG holding the sampled frames side by side (each <see cref="StripWidth"/> × <see cref="StripHeight"/>, letterboxed).</summary>
    public Task<byte[]?> FrameStripAsync(string path, string cacheKey, IReadOnlyList<double> times, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.Count == 0)
        {
            return Task.FromResult<byte[]?>(null);
        }

        List<string> args = [];
        foreach (double at in times)
        {
            args.AddRange(["-noaccurate_seek", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", path]);
        }

        args.AddRange(["-filter_complex", StripGraph(times.Count), "-map", "[out]", "-frames:v", "1", "-c:v", "png", "-f", "image2pipe", "-"]);
        return CachedAsync(cacheKey + "-strip-" + times.Count.ToString(CultureInfo.InvariantCulture), args, token);
    }

    /// <summary>
    /// The compare image for one frame: the centre <see cref="CompareWidth"/> × <see cref="CompareHeight"/> pixels of the
    /// original at full resolution, and beside it the same pixels after shrinking the frame to
    /// <paramref name="effectiveHeight"/> and enlarging it back (bicubic both ways, as the test did).
    /// </summary>
    public async Task<byte[]?> CompareAsync(string path, string cacheKey, MediaKind kind, double time, int width, int height, int effectiveHeight, CancellationToken token)
    {
        List<string> args = kind == MediaKind.Video ? ["-noaccurate_seek", "-ss", time.ToString("0.###", CultureInfo.InvariantCulture)] : [];
        args.AddRange(FfmpegArgs.Input(path, kind, null));
        args.AddRange(["-filter_complex", CompareGraph(width, height, effectiveHeight), "-map", "[out]", "-frames:v", "1", "-c:v", "png", "-f", "image2pipe", "-"]);
        Stopwatch watch = Stopwatch.StartNew();
        byte[]? image = await CachedAsync(string.Create(CultureInfo.InvariantCulture, $"{cacheKey}-cmp-{time:0.###}-{effectiveHeight}"), args, token).ConfigureAwait(false);
        LastCompareTime = watch.Elapsed;
        return image;
    }

    /// <summary>The compare image's filter graph: the centre crop of the frame beside the same crop of its down-up copy.</summary>
    public static string CompareGraph(int width, int height, int effectiveHeight)
    {
        int small = Even((double)effectiveHeight / height * width);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[0:v]trim=end_frame=1,setpts=PTS-STARTPTS,split[a][b];[b]scale={small}:{effectiveHeight}:flags=bicubic,scale={width}:{height}:flags=bicubic[r];" +
            $"[a]crop='min(iw,{CompareWidth})':'min(ih,{CompareHeight})'[c1];[r]crop='min(iw,{CompareWidth})':'min(ih,{CompareHeight})'[c2];[c1][c2]hstack[out]");
    }

    /// <summary>The frame strip's filter graph: first frame of each input, letterboxed, tiled in one row.</summary>
    public static string StripGraph(int inputs)
    {
        StringBuilder graph = new();
        for (int i = 0; i < inputs; i++)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[{i}:v]trim=end_frame=1,setpts=PTS-STARTPTS,scale={StripWidth}:{StripHeight}:force_original_aspect_ratio=decrease,pad={StripWidth}:{StripHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1[f{i}];");
        }

        for (int i = 0; i < inputs; i++)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[f{i}]");
        }

        graph.Append(CultureInfo.InvariantCulture, $"concat=n={inputs}:v=1:a=0,tile={inputs}x1[out]");
        return graph.ToString();
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    private async Task<byte[]?> CachedAsync(string key, IReadOnlyList<string> args, CancellationToken token)
    {
        string file = Path.Combine(cacheDirectory, Safe(key) + ".png");
        try
        {
            if (File.Exists(file))
            {
                return await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }

        using MemoryStream output = new();
        FfResult result = await runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = args,
                Stdout = StdoutMode.Binary,
                Priority = ProcessPriorityClass.Normal,
                IdleTimeout = TimeSpan.FromSeconds(30),
                HardTimeout = TimeSpan.FromMinutes(2),
                BinaryConsumer = (stream, t) => stream.CopyToAsync(output, t),
            },
            token).ConfigureAwait(false);
        byte[] image = output.ToArray();
        if (image.Length < 8 || image[0] != 0x89 || image[1] != (byte)'P')
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllBytesAsync(file, image, token).ConfigureAwait(false);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation.
        }

        _ = result;
        return image;
    }

    private void Prune()
    {
        FileInfo[] files = new DirectoryInfo(cacheDirectory).GetFiles("*.png");
        foreach (FileInfo old in files.OrderBy(f => f.LastWriteTimeUtc).Take(Math.Max(0, files.Length - MaxCachedFiles)))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Safe(string key)
    {
        StringBuilder safe = new(key.Length);
        foreach (char c in key)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        }

        return safe.ToString();
    }
}
