using System.Diagnostics;
using System.Globalization;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Tests.TestSupport;
using Xunit.Abstractions;

namespace MediaVerifier.Tests.Analysis.Audio;

/// <summary>
/// Performance targets of subplan 10 (tasks A-09 and A-11): evidence for a 5-minute track renders in under
/// a second; the analyzers get through a 5-minute stereo track in under half a second on one core (at
/// least 600 times real time; FFmpeg decodes FLAC at about 2,000 times, see docs/perf.md); their memory
/// does not grow with the length.
/// </summary>
[Trait("Category", "Perf")]
public sealed class AudioPerfTests(ITestOutputHelper output)
{
    private const int BlockFrames = 4096;

    [FixtureFact]
    public async Task Evidence_for_a_five_minute_track_renders_in_under_a_second()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using TempDirectory temp = new();
        string track = FixtureFactory.Get("long5m.flac");
        EvidenceRenderer renderer = new(runner, temp.Combine("cache"));
        await renderer.RenderAsync(track, "warm-up", [], CancellationToken.None);
        TimeSpan best = TimeSpan.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            AudioEvidence? evidence = await renderer.RenderAsync(track, "run" + i.ToString(CultureInfo.InvariantCulture), [60, 120, 180], CancellationToken.None);
            watch.Stop();
            Assert.NotNull(evidence);
            Assert.Equal(300, evidence.Seconds, 0);
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"5-minute FLAC: evidence rendered in {best.TotalMilliseconds:0} ms (best of 3)"));
        Assert.True(best < TimeSpan.FromSeconds(1), $"{best.TotalMilliseconds:0} ms");
    }

    [FixtureFact]
    public async Task Analysis_of_five_minutes_takes_under_half_a_second()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        string track = FixtureFactory.Get("long5m.flac");

        // Decode alone: FFmpeg to the WAV pipe, counted and nothing else.
        TimeSpan decode = TimeSpan.MaxValue;
        List<float[]> blocks = [];
        WavFormat? format = null;
        for (int i = 0; i < 3; i++)
        {
            Collector collector = new(i == 0 ? blocks : null);
            Stopwatch watch = Stopwatch.StartNew();
            await runner.RunAsync(new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = ["-i", track, "-map", "0:a:0", "-c:a", "pcm_f32le", "-f", "wav", "-"],
                Stdout = StdoutMode.Binary,
                Priority = ProcessPriorityClass.Normal,
                BinaryConsumer = async (stream, token) => format = await AudioDecodeProvider.PumpAsync(stream, [collector], token),
            });
            watch.Stop();
            decode = watch.Elapsed < decode ? watch.Elapsed : decode;
        }

        Assert.NotNull(format);

        // The analyzers of the decode provider over the same samples, from memory.
        TimeSpan analysis = TimeSpan.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            IPcmAnalyzer[] analyzers = Analyzers();
            Stopwatch watch = Stopwatch.StartNew();
            Feed(analyzers, format, blocks);
            watch.Stop();
            analysis = watch.Elapsed < analysis ? watch.Elapsed : analysis;
        }

        // Each analyzer on its own, for docs/perf.md.
        foreach (IPcmAnalyzer single in Analyzers())
        {
            Stopwatch watch = Stopwatch.StartNew();
            Feed([single], format, blocks);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {single.GetType().Name,-18} {watch.Elapsed.TotalMilliseconds,6:0} ms"));
        }

        double share = analysis.TotalMilliseconds / decode.TotalMilliseconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"5-minute FLAC: decode {decode.TotalMilliseconds:0} ms, analysis {analysis.TotalMilliseconds:0} ms ({share:P0} of the decode)"));
        Assert.True(analysis < TimeSpan.FromMilliseconds(500), $"analysis took {analysis.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void Analyzer_memory_stays_flat_over_three_hours()
    {
        const int rate = 44_100;
        const int channels = 2;
        Random random = new(7);
        List<float[]> blocks = [];
        for (int b = 0; b < 16; b++)
        {
            float[] block = new float[BlockFrames * channels];
            float level = 0;
            for (int i = 0; i < block.Length; i++)
            {
                level = (0.95f * level) + (float)(((random.NextDouble() * 2) - 1) * 0.05);
                block[i] = level;
            }

            blocks.Add(block);
        }

        WavFormat format = new(channels, rate, 32);
        IPcmAnalyzer[] analyzers = Analyzers();
        foreach (IPcmAnalyzer analyzer in analyzers)
        {
            analyzer.Begin(format);
        }

        long total = 3L * 3600 * rate / BlockFrames;
        long tenMinutes = 600L * rate / BlockFrames;
        long allocatedAfterTenMinutes = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (long n = 0; n < total; n++)
        {
            float[] block = blocks[(int)(n % blocks.Count)];
            foreach (IPcmAnalyzer analyzer in analyzers)
            {
                analyzer.Process(block);
            }

            if (n == tenMinutes)
            {
                allocatedAfterTenMinutes = GC.GetAllocatedBytesForCurrentThread();
            }
        }

        long growth = GC.GetAllocatedBytesForCurrentThread() - allocatedAfterTenMinutes;
        foreach (IPcmAnalyzer analyzer in analyzers)
        {
            analyzer.End();
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"3 hours of stereo through the analyzers in {watch.Elapsed.TotalSeconds:0.0} s; {growth:N0} bytes allocated after the first 10 minutes"));
        Assert.True(growth < 1024 * 1024, $"{growth:N0} bytes allocated after the first 10 minutes");
    }

    private static IPcmAnalyzer[] Analyzers() =>
    [
        new SampleCounter(), new LevelAnalyzer(), new BitDepthAnalyzer(), new ClippingAnalyzer(), new StereoAnalyzer(),
        new SpectrumAnalyzer(), new DropoutAnalyzer(), new ClickAnalyzer(),
    ];

    private static void Feed(IPcmAnalyzer[] analyzers, WavFormat format, List<float[]> blocks)
    {
        foreach (IPcmAnalyzer analyzer in analyzers)
        {
            analyzer.Begin(format);
        }

        foreach (float[] block in blocks)
        {
            foreach (IPcmAnalyzer analyzer in analyzers)
            {
                analyzer.Process(block);
            }
        }

        foreach (IPcmAnalyzer analyzer in analyzers)
        {
            analyzer.End();
        }
    }

    /// <summary>Keeps copies of the decoded blocks (first run only) so the analyzers can be timed without the decode.</summary>
    private sealed class Collector(List<float[]>? keep) : IPcmAnalyzer
    {
        public void Begin(WavFormat format)
        {
        }

        public void Process(ReadOnlySpan<float> interleaved) => keep?.Add(interleaved.ToArray());

        public void End()
        {
        }
    }
}
