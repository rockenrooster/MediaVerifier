using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis.Providers;

/// <summary><c>idet</c> on 200 frames at three positions (subplan 11, VID-INTERLACE; task V-06).</summary>
public sealed record InterlaceArtifact(RunOutcome Outcome, int TopFirst, int BottomFirst, int Progressive, int Undetermined, bool FlaggedInterlaced, string? Skipped = null) : IArtifact
{
    public int Determined => TopFirst + BottomFirst + Progressive;

    public int Interlaced => TopFirst + BottomFirst;
}

/// <summary>
/// Counts interlaced and progressive frames with <c>idet</c> (one FFmpeg process, three fast seeks at 20, 50 and
/// 80 % of the duration, 200 frames each). Runs at Standard depth on whole, undamaged videos.
/// </summary>
public sealed partial class InterlaceProvider() : ArtifactProvider<InterlaceArtifact>(new ArtifactCodec<InterlaceArtifact>(ArtifactKind.Interlace, ProviderJsonContext.Default.InterlaceArtifact))
{
    public const int FramesPerPosition = 200;

    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Standard;

    public override bool Applies(FileContext file) => file.Kind == MediaKind.Video;

    /// <summary>The filter graph for <paramref name="inputs"/> seeks: 200 frames from each, concatenated into one <c>idet</c>.</summary>
    public static string Graph(int inputs)
    {
        StringBuilder graph = new();
        for (int i = 0; i < inputs; i++)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[{i}:v]trim=end_frame={FramesPerPosition},setpts=PTS-STARTPTS[s{i}];");
        }

        for (int i = 0; i < inputs; i++)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[s{i}]");
        }

        graph.Append(CultureInfo.InvariantCulture, $"concat=n={inputs}:v=1:a=0,idet[v]");
        return graph.ToString();
    }

    /// <summary>Reads <c>Multi frame detection: TFF: n BFF: n Progressive: n Undetermined: n</c>.</summary>
    public static (int Tff, int Bff, int Progressive, int Undetermined)? Parse(string line)
    {
        Match m = MultiFrame().Match(line ?? string.Empty);
        return m.Success
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture))
            : null;
    }

    protected override async Task<InterlaceArtifact> ProduceTypedAsync(FileContext file)
    {
        ProbeArtifact? probe = await file.GetAsync<ProbeArtifact>().ConfigureAwait(false);
        if (probe?.Video is not StreamInfo video)
        {
            return new InterlaceArtifact(RunOutcome.Completed, 0, 0, 0, 0, false, "no-video");
        }

        VideoDecodeArtifact? decode = await file.GetAsync<VideoDecodeArtifact>().ConfigureAwait(false);
        if (decode is not { Outcome: RunOutcome.Completed } || decode.Video.Counts.DecodeError + decode.Video.Counts.Truncation + decode.Container.Counts.OpenFailure > 0)
        {
            return new InterlaceArtifact(RunOutcome.Completed, 0, 0, 0, 0, false, "damaged");
        }

        bool flagged = video.FieldOrder is "tt" or "bb" or "tb" or "bt";
        double duration = probe.Duration ?? video.Duration ?? 0;
        double[] positions = duration >= 30 ? [duration * 0.2, duration * 0.5, duration * 0.8] : [0];
        List<string> args = [];
        foreach (double at in positions)
        {
            args.AddRange(["-noaccurate_seek", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", file.FullPath]);
        }

        args.AddRange(["-filter_complex", Graph(positions.Length), "-map", "[v]", "-f", "null", "-"]);
        (int, int, int, int)? counts = null;
        FfResult result = await file.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = args,
                WorkingDirectory = file.TempDirectory,
                LogLevel = "info",
                IdleTimeout = TimeSpan.FromSeconds(file.Settings.StallSeconds),
                HardTimeout = TimeSpan.FromMinutes(5),
                OnLogLine = line => counts = Parse(line.Raw) ?? counts,
            },
            file.Cancellation).ConfigureAwait(false);
        (int tff, int bff, int progressive, int undetermined) = counts ?? (0, 0, 0, 0);
        return new InterlaceArtifact(OutcomeMapping.From(result.Outcome), tff, bff, progressive, undetermined, flagged);
    }

    [GeneratedRegex(@"Multi frame detection:\s*TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)\s*Undetermined:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MultiFrame();
}
