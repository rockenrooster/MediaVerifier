using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Analysis.Audio;

/// <summary>Click detection on synthetic signals (task A-12).</summary>
public sealed class ClickAnalyzerTests
{
    private const int Rate = 44_100;

    [Fact]
    public void Five_single_sample_errors_in_noise_are_five_clicks()
    {
        Random random = new(17);
        float[] signal = new float[Rate * 10];
        float smooth = 0;
        for (int i = 0; i < signal.Length; i++)
        {
            // Brown-ish noise: small steps, like most music at 44.1 kHz.
            smooth = (0.98f * smooth) + (float)(((random.NextDouble() * 2) - 1) * 0.02);
            signal[i] = smooth;
        }

        int[] at = [Rate, Rate * 3, Rate * 5, Rate * 7, Rate * 9];
        foreach (int n in at)
        {
            signal[n] += 0.4f;
        }

        ClickAnalyzer analyzer = Run(signal);
        Assert.Equal(5, analyzer.Result.Clicks);
        Assert.Equal([1.0, 3.0, 5.0, 7.0, 9.0], analyzer.Result.Positions);
    }

    [Fact]
    public void Noise_bursts_from_silence_and_full_scale_samples_are_not_clicks()
    {
        Random random = new(23);
        float[] bursts = new float[Rate * 4];
        for (int i = 0; i < bursts.Length; i++)
        {
            double t = (double)i / Rate % 0.5;
            bursts[i] = t < 0.1 ? (float)(((random.NextDouble() * 2) - 1) * 0.8 * Math.Exp(-t * 30)) : 0;
        }

        Assert.Equal(0, Run(bursts).Result.Clicks);

        float[] clipped = new float[Rate];
        for (int i = 0; i < clipped.Length; i++)
        {
            clipped[i] = (float)Math.Clamp(3 * Math.Sin(2 * Math.PI * 40 * i / Rate), -1, 1);
        }

        clipped[Rate / 2] = 1f;
        Assert.Equal(0, Run(clipped).Result.Clicks);
    }

    [Fact]
    public void Select_finds_the_kth_smallest()
    {
        Random random = new(5);
        for (int round = 0; round < 50; round++)
        {
            float[] values = [.. Enumerable.Range(0, 1 + random.Next(300)).Select(_ => (float)random.Next(20))];
            float[] sorted = [.. values.Order()];
            int k = random.Next(values.Length);
            Assert.Equal(sorted[k], ClickAnalyzer.Select(values, k));
        }
    }

    private static ClickAnalyzer Run(float[] mono)
    {
        ClickAnalyzer analyzer = new();
        analyzer.Begin(new WavFormat(1, Rate, 32));
        for (int offset = 0; offset < mono.Length; offset += 4096)
        {
            analyzer.Process(mono.AsSpan(offset, Math.Min(4096, mono.Length - offset)));
        }

        analyzer.End();
        return analyzer;
    }
}

/// <summary>The evidence renderer and COR-CLICKS on generated fixtures (tasks A-09 and A-12).</summary>
[Trait("Category", "Integration")]
public sealed class EvidenceAndClickFixtureTests
{
    [FixtureFact]
    public async Task The_spectrogram_of_a_fake_lossless_file_is_dark_above_its_cutoff()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using TempDirectory temp = new();
        EvidenceRenderer renderer = new(runner, temp.Combine("cache"));
        AudioEvidence fake = (await renderer.RenderAsync(FixtureFactory.Get("fake128.flac"), "1-fake", [], CancellationToken.None))!;
        AudioEvidence full = (await renderer.RenderAsync(FixtureFactory.Get("src16.flac"), "2-full", [], CancellationToken.None))!;
        Assert.Equal(44_100, fake.SampleRate);
        Assert.Equal(20, fake.Seconds, 1);
        Assert.InRange(fake.SpectrogramColumns, 400, AudioEvidence.MaxColumns);

        // 16.6 kHz of 22.05 kHz is band 192 of 256: above it only rounding noise, below it the music.
        Assert.True(MeanLevel(fake, 200, 250) < AudioEvidence.Level(-110), $"above the knee: {AudioEvidence.Db((byte)MeanLevel(fake, 200, 250)):0} dB");
        Assert.True(MeanLevel(fake, 100, 180) > AudioEvidence.Level(-90), $"below the knee: {AudioEvidence.Db((byte)MeanLevel(fake, 100, 180)):0} dB");
        Assert.True(MeanLevel(full, 200, 250) > AudioEvidence.Level(-90), $"full band: {AudioEvidence.Db((byte)MeanLevel(full, 200, 250)):0} dB");
    }

    [FixtureFact]
    public async Task The_waveform_shows_clipping_and_keeps_20_ms_around_each_mark_and_is_cached()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using TempDirectory temp = new();
        string copy = FixtureFactory.CopyTo("clip.flac", temp.Path);
        EvidenceRenderer renderer = new(runner, temp.Combine("cache"));
        AudioEvidence evidence = (await renderer.RenderAsync(copy, "3-clip", [5.0, 12.5], CancellationToken.None))!;
        Assert.True(evidence.WaveMax.Max() > 0.99f);
        Assert.True(evidence.WaveMin.Min() < -0.99f);
        Assert.Equal([5.0, 12.5], evidence.Marks);
        Assert.Equal(2, evidence.WindowChannels);
        Assert.All(evidence.MarkWindows, w => Assert.Equal(882 * 2, w.Length));
        Assert.All(evidence.MarkWindows, w => Assert.Contains(w, v => v != 0));

        // The second request comes from the disk cache: it needs neither the file nor a decode.
        File.Delete(copy);
        AudioEvidence cached = (await renderer.RenderAsync(copy, "3-clip", [5.0, 12.5], CancellationToken.None))!;
        Assert.Equal(evidence.Spectrogram, cached.Spectrogram);
        Assert.Equal(evidence.WaveMax, cached.WaveMax);
        Assert.Equal(evidence.MarkWindows[1], cached.MarkWindows[1]);
        Assert.Null(await renderer.RenderAsync(copy, "3-clip", [1.0], CancellationToken.None));
    }

    [FixtureFact]
    public async Task Spliced_single_sample_errors_raise_clicks_and_percussion_does_not()
    {
        using FfRunner runner = FfmpegEnvironment.CreateRunner();
        using EngineHarness harness = new(EngineFactory.CreateProviders(), EngineFactory.CreateDetectors(), runner: runner);
        string[] names = ["clicks.flac", "drums.flac", "src16.flac", "clip.flac", "natural.flac", "m320.mp3", "fake128.flac"];
        foreach (string name in names)
        {
            FixtureFactory.CopyTo(name, harness.Library);
        }

        harness.Rules.SetEnabled("COR-CLICKS", true);
        await harness.AddRootAsync(harness.Library);
        await harness.ScanAsync(ScanDepth.Standard);
        foreach (string name in names)
        {
            FileRecord file = harness.AllFiles().Single(f => f.Name == name);
            FindingRecord[] clicks = [.. harness.Database.Findings.ForFile(file.Id).Where(f => f.RuleId == "COR-CLICKS")];
            if (name == "clicks.flac")
            {
                FindingRecord finding = Assert.Single(clicks);
                Assert.Equal(Severity.Low, finding.Severity);
                Assert.Equal("5 clicks far above the surrounding sound", finding.Summary);
            }
            else
            {
                Assert.True(clicks.Length == 0, $"{name}: {string.Join(", ", clicks.Select(c => c.Summary))}");
            }
        }
    }

    private static double MeanLevel(AudioEvidence evidence, int fromRow, int toRow)
    {
        double sum = 0;
        int count = 0;
        for (int c = 0; c < evidence.SpectrogramColumns; c++)
        {
            for (int r = fromRow; r < toRow; r++)
            {
                sum += evidence.Spectrogram[(c * AudioEvidence.Rows) + r];
                count++;
            }
        }

        return sum / count;
    }
}

/// <summary>The SIMD and scalar FFT paths agree bit for bit (subplan 10, Performance).</summary>
public sealed class FftPathTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void Simd_and_scalar_transforms_are_identical(int size)
    {
        Random random = new(size);
        float[] re1 = [.. Enumerable.Range(0, size).Select(_ => (float)((random.NextDouble() * 2) - 1))];
        float[] im1 = [.. Enumerable.Range(0, size).Select(_ => (float)((random.NextDouble() * 2) - 1))];
        float[] re2 = [.. re1];
        float[] im2 = [.. im1];
        Fft fft = new(size);
        fft.Transform(re1, im1, simd: true);
        fft.Transform(re2, im2, simd: false);
        Assert.Equal(re2, re1);
        Assert.Equal(im2, im1);
    }
}
