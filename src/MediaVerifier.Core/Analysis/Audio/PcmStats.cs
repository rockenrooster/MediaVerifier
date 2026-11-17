using System.Text.Json.Serialization;

namespace MediaVerifier.Core.Analysis.Audio;

/// <summary>Level features of one channel (subplan 10, LevelAnalyzer).</summary>
public sealed record ChannelLevels(
    double Peak,
    double Rms,
    double Mean,
    long Overs,

    // Share of 1 s windows whose mean has the same sign as the track's mean.
    double DcSameSignShare);

/// <summary>Clipping features (subplan 10, ClippingAnalyzer): runs on the peak plateau, worst channel.</summary>
public sealed record ClippingStats(
    long Runs,
    long ClippedSamples,
    int LongestRun,
    double PeakDb,

    // Share of samples at or above 98 % of the peak (lossy and float sources).
    double CrowdingRatio,
    double[] Positions);

/// <summary>Long-term average spectrum features (subplan 10, Spectral cutoff analysis).</summary>
public sealed record SpectrumStats(
    bool Sufficient,
    double KneeHz,
    double DropDb,
    double BelowDb,
    double AboveDb,
    double NyquistHz,
    int FftSize,
    int FramesUsed,
    double AnalysedSeconds,

    // 96 linear bands to Nyquist, dB as bytes: (dB + 160) × 255 / 160, clamped.
    byte[] Spectrum96)
{
    /// <summary>A terminal drop of 30 dB or more.</summary>
    [JsonIgnore]
    public bool Brickwall => Sufficient && DropDb >= 30;

    [JsonIgnore]
    public double Ratio => NyquistHz <= 0 ? 1 : KneeHz / NyquistHz;

    public static SpectrumStats Insufficient(double nyquist, int fftSize, int frames, double seconds) =>
        new(false, nyquist, 0, 0, 0, nyquist, fftSize, frames, seconds, []);

    /// <summary>
    /// Level of undithered quantisation noise per bin for integer PCM with <paramref name="bits"/>
    /// effective bits: 10·log10((2^-(b-1))² / 12 · 1.5 / N).
    /// </summary>
    public double FloorDb(int bits) => bits <= 0 ? double.NegativeInfinity : 10 * Math.Log10(Math.Pow(Math.Pow(2, -(bits - 1)), 2) / 12 * 1.5 / FftSize);
}

/// <summary>Digital-zero gaps inside the audio (subplan 10, COR-DROPOUT).</summary>
public sealed record DropoutStats(int Gaps, int AlignedGaps, double[] Positions, int[] Lengths);

/// <summary>
/// The features of one decode (subplan 10): small, so rules can be judged again without decoding.
/// Stored inside the audio decode artifact.
/// </summary>
public sealed record PcmStats(
    int SampleRate,
    int Channels,
    double Seconds,
    ChannelLevels[] Levels,

    // Every sample was an exact multiple of 2^-23 (an integer source of 24 bits or fewer).
    bool Integral,

    // 24 minus the trailing zero bits of all samples ORed; 0 for digital silence; -1 when not integral.
    int EffectiveBits,
    ClippingStats Clipping,
    double Correlation,
    bool IdenticalChannels,
    SpectrumStats Spectrum,
    DropoutStats Dropouts,
    double LeadingSilenceSeconds,
    double TrailingSilenceSeconds)
{
    /// <summary>Level above the knee relative to the rounding-noise floor (NaN when unknown).</summary>
    [JsonIgnore]
    public double AboveVsFloorDb => Integral && EffectiveBits > 0 && Spectrum.Sufficient ? Spectrum.AboveDb - Spectrum.FloorDb(EffectiveBits) : double.NaN;

    [JsonIgnore]
    public double PeakDb => 20 * Math.Log10(Math.Max(1e-12, Levels.Length == 0 ? 0 : Levels.Max(l => l.Peak)));

    [JsonIgnore]
    public double RmsDb
    {
        get
        {
            if (Levels.Length == 0)
            {
                return -240;
            }

            double power = Levels.Average(l => l.Rms * l.Rms);
            return 10 * Math.Log10(Math.Max(1e-24, power));
        }
    }
}
