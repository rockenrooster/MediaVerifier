using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Analysis.Audio;
using MediaVerifier.Core.Analysis.Detectors;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Rules;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Tests.Analysis.Audio;

/// <summary>Streaming analyzers and the audio quality rules on synthetic signals (tasks A-01 to A-07).</summary>
public sealed class AudioAnalyzerTests
{
    private const int Rate = 44_100;

    [Fact]
    public void Fft_puts_a_pure_tone_in_its_bin()
    {
        const int n = 1024;
        float[] re = new float[n];
        float[] im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = MathF.Sin(2 * MathF.PI * 64 * i / n);
        }

        new Fft(n).Transform(re, im);
        int best = Enumerable.Range(0, n / 2).MaxBy(k => (re[k] * re[k]) + (im[k] * im[k]));
        Assert.Equal(64, best);
        Assert.Equal(n / 2, Math.Sqrt((re[64] * re[64]) + (im[64] * im[64])), 1);
    }

    [Fact]
    public void A_synthetic_brickwall_at_16_kHz_is_located_within_100_Hz()
    {
        const int size = 4096;
        double[] power = new double[(size / 2) + 1];
        double binHz = (double)Rate / size;
        for (int k = 0; k < power.Length; k++)
        {
            double db = k * binHz < 16_000 ? -60 - (k * binHz / 2000) : -130;
            power[k] = Math.Pow(10, db / 10) * (size * (double)size / 4) * 200;
        }

        SpectrumStats stats = SpectrumAnalyzer.Measure(power, 200, size, Rate, 60);
        Assert.True(stats.Sufficient && stats.DropDb >= 30);
        Assert.InRange(stats.KneeHz, 15_900, 16_100);
        Assert.True(stats.DropDb > 50, $"drop {stats.DropDb}");
    }

    [Fact]
    public void Full_band_noise_has_no_brickwall()
    {
        Random random = new(3);
        float[] noise = [.. Enumerable.Range(0, Rate * 2 * 20).Select(_ => (float)((random.NextDouble() * 2) - 1) * 0.3f)];
        SpectrumAnalyzer analyzer = Run(new SpectrumAnalyzer(), noise, 2);
        Assert.True(analyzer.Result.Sufficient);
        Assert.False(analyzer.Result.Sufficient && analyzer.Result.DropDb >= 30);
    }

    [Fact]
    public void Sixteen_bit_samples_stored_as_24_bit_have_16_effective_bits()
    {
        Random random = new(5);
        float[] sixteen = [.. Enumerable.Range(0, 20_000).Select(_ => random.Next(-32768, 32768) / 32768f)];
        BitDepthAnalyzer padded = Run(new BitDepthAnalyzer(), sixteen, 2);
        Assert.True(padded.Integral);
        Assert.Equal(16, padded.EffectiveBits);

        float[] full = [.. Enumerable.Range(0, 20_000).Select(_ => random.Next(-8388608, 8388608) / 8388608f)];
        Assert.Equal(24, Run(new BitDepthAnalyzer(), full, 2).EffectiveBits);

        float[] lossy = [.. Enumerable.Range(0, 20_000).Select(_ => (float)(random.NextDouble() * 0.5))];
        BitDepthAnalyzer floats = Run(new BitDepthAnalyzer(), lossy, 2);
        Assert.False(floats.Integral);
        Assert.Equal(-1, floats.EffectiveBits);
    }

    [Fact]
    public void A_full_scale_30_Hz_sine_is_not_clipping_but_a_clipped_40_Hz_sine_is()
    {
        float[] clean = Sine(30, 1.0, 10);
        ClippingAnalyzer gentle = Run(new ClippingAnalyzer(), clean, 1);
        Assert.Equal(0, gentle.Result.Runs);

        float[] clipped = Sine(40, 2.0, 10);
        ClippingAnalyzer hard = Run(new ClippingAnalyzer(), clipped, 1);
        Assert.InRange(hard.Result.Runs, 790, 810);
        Assert.True(hard.Result.LongestRun > 64);
        Assert.NotEmpty(hard.Result.Positions);
    }

    [Fact]
    public void A_588_sample_gap_is_an_aligned_dropout_and_a_faded_gap_is_not()
    {
        Random random = new(9);
        float[] signal = [.. Enumerable.Range(0, Rate * 4).Select(_ => (float)((random.NextDouble() * 2) - 1) * 0.3f)];
        Array.Clear(signal, Rate * 2, 588);
        DropoutAnalyzer analyzer = Run(new DropoutAnalyzer(), signal, 1);
        Assert.Equal(1, analyzer.Result.Gaps);
        Assert.Equal(1, analyzer.Result.AlignedGaps);
        Assert.Equal(2.0, analyzer.Result.Positions[0], 2);

        float[] faded = [.. signal];
        int start = Rate;
        for (int i = 0; i < 882; i++)
        {
            faded[start + i] *= 1 - (i / 882f);
        }

        Array.Clear(faded, start + 882, 1323);
        for (int i = 0; i < 882; i++)
        {
            faded[start + 882 + 1323 + i] *= i / 882f;
        }

        Array.Clear(faded, Rate * 2, 588);
        for (int i = Rate * 2; i < (Rate * 2) + 588; i++)
        {
            faded[i] = signal[i - 588];
        }

        Assert.Equal(0, Run(new DropoutAnalyzer(), faded, 1).Result.Gaps);
        Assert.True(DropoutAnalyzer.Aligned(1152));
        Assert.True(DropoutAnalyzer.Aligned(2049));
        Assert.False(DropoutAnalyzer.Aligned(700));
    }

    [Fact]
    public void Levels_report_dc_with_a_stable_sign_and_silence_at_the_edges()
    {
        Random random = new(11);
        float[] signal = new float[Rate * 30];
        for (int i = Rate * 12; i < Rate * 30; i++)
        {
            signal[i] = (float)(((random.NextDouble() * 2) - 1) * 0.2) + 0.06f;
        }

        LevelAnalyzer analyzer = Run(new LevelAnalyzer(), signal, 1);
        Assert.Equal(12, analyzer.LeadingSilenceSeconds, 2);
        Assert.InRange(analyzer.Levels[0].Mean, 0.035, 0.037);
        Assert.InRange(analyzer.Levels[0].DcSameSignShare, 0.59, 0.61);
    }

    [Fact]
    public void Fake_lossless_features_raise_the_rule_with_its_wording()
    {
        PcmStats stats = Stats(knee: 16_600, drop: 67, above: -134.4, seconds: 60, bits: 16);
        List<Finding> findings = Evaluate(stats, Probe("flac", 44_100, 2, 16, null));
        Finding fake = Assert.Single(findings, f => f.RuleId == "AUD-FAKE-LOSSLESS");
        Assert.Equal(Severity.High, fake.Severity);
        Assert.Equal(0.95f, fake.Confidence, 2);
        Assert.Equal("MP3", fake.Evidence["sourceName"]);
        Assert.Contains("16.6 kHz", fake.Summary, StringComparison.Ordinal);
        Assert.Contains("rounding noise", fake.Summary, StringComparison.Ordinal);

        Finding possible = Assert.Single(Evaluate(Stats(knee: 20_000, drop: 68, above: -135, seconds: 60, bits: 16), Probe("flac", 44_100, 2, 16, null)), f => f.RuleId == "AUD-FAKE-LOSSLESS");
        Assert.True(possible.Confidence <= 0.70f);
        Assert.Empty(Evaluate(Stats(knee: 22_050, drop: 0, above: 0, seconds: 60, bits: 16), Probe("flac", 44_100, 2, 16, null)));
    }

    [Fact]
    public void Upsampled_padded_and_inflated_files_are_reported()
    {
        Finding up = Assert.Single(Evaluate(Stats(knee: 23_200, drop: 48, above: -158.9, seconds: 60, bits: 24, rate: 96_000), Probe("flac", 96_000, 2, 24, null)), f => f.RuleId == "AUD-UPSAMPLED");
        Assert.Equal(Severity.Medium, up.Severity);

        PcmStats padded = Stats(knee: 22_050, drop: 0, above: 0, seconds: 60, bits: 16);
        Finding pad = Assert.Single(Evaluate(padded, Probe("flac", 44_100, 2, 24, null)), f => f.RuleId == "AUD-PADDED-BITS");
        Assert.Equal("24-bit file with 16 significant bits", pad.Summary);

        PcmStats lossy = Stats(knee: 16_550, drop: 84, above: -152, seconds: 60, bits: -1);
        Finding inflated = Assert.Single(Evaluate(lossy, Probe("mp3", 44_100, 2, null, 320_000)), f => f.RuleId == "AUD-BITRATE-INFLATED");
        Assert.Equal(Severity.Medium, inflated.Severity);
        Assert.Equal(0.80f, inflated.Confidence, 2);
        Assert.Empty(Evaluate(Stats(knee: 20_000, drop: 86, above: -154, seconds: 60, bits: -1), Probe("mp3", 44_100, 2, null, 320_000)));
    }

    private static PcmStats Stats(double knee, double drop, double above, double seconds, int bits, int rate = Rate)
    {
        ChannelLevels level = new(0.3, 0.1, 0.001, 0, 0.5);
        SpectrumStats spectrum = new(true, knee, drop, above + drop, above, rate / 2.0, SpectrumAnalyzer.FftSizeFor(rate), 500, seconds, new byte[96]);
        return new PcmStats(rate, 2, seconds, [level, level], bits > 0, bits, new ClippingStats(0, 0, 0, -10, 0, []), 0.5, false, spectrum, new DropoutStats(0, 0, [], []), 0, 0);
    }

    private static ProbeArtifact Probe(string codec, int rate, int channels, int? bits, long? bitrate) => new(
        true, false, codec == "flac" ? "flac" : "mp3", 100, 60, bitrate, 1000,
        [new StreamInfo(0, "audio", codec, null, null, null, null, null, null, null, rate, channels, null, bits, 60, bitrate, null, null, null, false, true)],
        LogDigest.Empty, RunOutcome.Completed);

    private static List<Finding> Evaluate(PcmStats stats, ProbeArtifact probe)
    {
        AudioDecodeArtifact decode = new(RunOutcome.Completed, false, LogDigest.Empty, true, (long)(stats.Seconds * stats.SampleRate), stats.SampleRate, stats.Channels, Md5Verdict.NotApplicable, stats);
        Dictionary<Type, IArtifact> artifacts = new() { [typeof(AudioDecodeArtifact)] = decode, [typeof(ProbeArtifact)] = probe };
        DetectorInput input = new(@"D:\a.flac", ".flac", MediaKind.Audio, ScanDepth.Standard, RootRole.Library, null, artifacts, new RuleSettings());
        FindingSink sink = new();
        new AudioQualityDetector().Evaluate(input, sink);
        return [.. sink.Findings];
    }

    private static float[] Sine(double hz, double amplitude, int seconds)
    {
        float[] samples = new float[Rate * seconds];
        for (int i = 0; i < samples.Length; i++)
        {
            double v = amplitude * Math.Sin(2 * Math.PI * hz * i / Rate);
            samples[i] = (float)(Math.Round(Math.Clamp(v, -1, 32767.0 / 32768) * 32768) / 32768);
        }

        return samples;
    }

    private static T Run<T>(T analyzer, float[] interleaved, int channels)
        where T : IPcmAnalyzer
    {
        analyzer.Begin(new WavFormat(channels, Rate, 32));
        for (int offset = 0; offset < interleaved.Length; offset += 4096 * channels)
        {
            int count = Math.Min(4096 * channels, interleaved.Length - offset);
            analyzer.Process(interleaved.AsSpan(offset, count));
        }

        analyzer.End();
        return analyzer;
    }
}
