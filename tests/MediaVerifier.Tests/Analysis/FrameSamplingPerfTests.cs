using System.Diagnostics;
using System.Globalization;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Analysis;

/// <summary>
/// Subplan 11, task VP-02: sampling adds under 5 s per film at 2160p (24 samples, the most a film gets) and under
/// 0.3 s per photo. Runs the same FFmpeg command the frame-sample provider builds.
/// </summary>
[Trait("Category", "Perf")]
public sealed class FrameSamplingPerfTests(ITestOutputHelper output)
{
    [FixtureFact]
    public async Task A_2160p_film_is_sampled_in_under_five_seconds()
    {
        string film = FixtureFactory.Get("uhd10m.mp4");
        double[] times = FrameSampling.SampleTimes(600);
        TimeSpan best = await BestOfAsync(film, times, 3840, 2160, photo: false);
        double perSample = best.TotalSeconds / times.Length;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"2160p: {times.Length} samples in {best.TotalMilliseconds:0} ms; 24 samples about {perSample * 24:0.0} s"));
        PerfReport.Add("Frame sampling", "2160p film, 24 samples", string.Create(CultureInfo.InvariantCulture, $"about {perSample * 24:0.0} s"), "subplan 11, VP-02");
        Assert.True(perSample * 24 < 5, $"{perSample * 24:0.0} s for 24 samples");
    }

    [FixtureFact]
    public async Task A_4K_photo_is_sampled_in_under_a_third_of_a_second()
    {
        TimeSpan best = await BestOfAsync(FixtureFactory.Get("img2x.jpg"), [0], 3840, 2160, photo: true);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"3840 x 2160 photo: {best.TotalMilliseconds:0} ms"));
        PerfReport.Add("Photo sampling", "3840 x 2160 photo", string.Create(CultureInfo.InvariantCulture, $"{best.TotalMilliseconds:0} ms"), "subplan 11, VP-02");
        Assert.True(best < TimeSpan.FromSeconds(0.3), $"{best.TotalMilliseconds:0} ms");
    }

    private static async Task<TimeSpan> BestOfAsync(string path, double[] times, int width, int height, bool photo)
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using TempDirectory temp = new();
        (int cw, int ch) = FrameSampling.Crop(width, height, photo ? 0.8 : 0.6);
        int[] candidates = photo ? FrameSampling.PhotoCandidates(height) : FrameSampling.VideoCandidates(height);
        (int, int)[] sizes = [.. candidates.Select(c => FrameSampling.Scaled(cw, ch, c, height))];
        List<string> args = [];
        foreach (double t in times)
        {
            if (photo)
            {
                args.AddRange(["-f", "jpeg_pipe", "-i", path]);
            }
            else
            {
                args.AddRange(["-noaccurate_seek", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", path]);
            }
        }

        args.AddRange(["-filter_complex", FrameSampling.Graph(times.Length, cw, ch, candidates, sizes, false, FrameSampling.Window(width, height, cw, ch, photo))]);
        args.AddRange(FrameSampling.Outputs(candidates.Length));
        TimeSpan best = TimeSpan.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            FfResult result = await runner.RunAsync(new FfRequest { Tool = FfTool.Ffmpeg, Arguments = args, WorkingDirectory = temp.Path, Priority = ProcessPriorityClass.Normal });
            watch.Stop();
            Assert.True(result.Completed);
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        return best;
    }
}
