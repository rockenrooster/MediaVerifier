using System.Numerics;
using System.Numerics.Tensors;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;

namespace MediaVerifier.Core.Analysis.Audio;

/// <summary>
/// Per channel: peak, RMS, mean (DC), samples above full scale, the sign stability of the mean in 1 s
/// windows, and the leading and trailing digital silence (subplan 10, LevelAnalyzer).
/// </summary>
public sealed class LevelAnalyzer : IPcmAnalyzer
{
    /// <summary>Digital silence: exactly zero, or below 2^-20 for float sources.</summary>
    public const float SilenceLevel = 1f / (1 << 20);

    private int _channels = 1;
    private int _rate = 1;
    private double[] _peak = [];
    private double[] _sumSquares = [];
    private double[] _sum = [];
    private long[] _overs = [];
    private double[] _windowSum = [];
    private long[] _positiveWindows = [];
    private long[] _negativeWindows = [];
    private long _windows;
    private int _windowFill;
    private long _frames;
    private long _firstSound = -1;
    private long _lastSound = -1;

    public ChannelLevels[] Levels { get; private set; } = [];

    public double LeadingSilenceSeconds { get; private set; }

    public double TrailingSilenceSeconds { get; private set; }

    public void Begin(WavFormat format)
    {
        _channels = Math.Max(1, format.Channels);
        _rate = Math.Max(1, format.SampleRate);
        _peak = new double[_channels];
        _sumSquares = new double[_channels];
        _sum = new double[_channels];
        _overs = new long[_channels];
        _windowSum = new double[_channels];
        _positiveWindows = new long[_channels];
        _negativeWindows = new long[_channels];
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        int f = 0;
        while (f < frames)
        {
            // Runs end at the 1 s window boundaries.
            int take = Math.Min(frames - f, _rate - _windowFill);
            ReadOnlySpan<float> run = interleaved.Slice(f * _channels, take * _channels);
            if (Lanes.Fit(_channels))
            {
                Vectorised(run);
            }
            else
            {
                Scalar(run);
            }

            Sound(run, take);
            _frames += take;
            _windowFill += take;
            f += take;
            if (_windowFill == _rate)
            {
                CloseWindow();
            }
        }
    }

    /// <summary>Sums per lane in float over the run (at most one second), then adds each lane to its channel in double.</summary>
    private void Vectorised(ReadOnlySpan<float> run)
    {
        int width = Vector<float>.Count;
        Vector<float> sum = Vector<float>.Zero;
        Vector<float> squares = Vector<float>.Zero;
        Vector<float> peak = Vector<float>.Zero;
        Vector<int> overs = Vector<int>.Zero;
        int i = 0;
        for (; i <= run.Length - width; i += width)
        {
            Vector<float> v = new(run[i..]);
            Vector<float> a = Vector.Abs(v);
            sum += v;
            squares += v * v;
            peak = Vector.Max(peak, a);
            overs -= Vector.GreaterThan(a, Vector<float>.One);
        }

        for (int lane = 0; lane < width; lane++)
        {
            int c = lane % _channels;
            _sum[c] += sum[lane];
            _windowSum[c] += sum[lane];
            _sumSquares[c] += squares[lane];
            _peak[c] = Math.Max(_peak[c], peak[lane]);
            _overs[c] += overs[lane];
        }

        // The tail starts on a whole frame because the width is a multiple of the channel count.
        Scalar(run[i..]);
    }

    private void Scalar(ReadOnlySpan<float> run)
    {
        for (int i = 0; i < run.Length; i++)
        {
            int c = i % _channels;
            float v = run[i];
            float a = Math.Abs(v);
            if (a > _peak[c])
            {
                _peak[c] = a;
            }

            if (a > 1f)
            {
                _overs[c]++;
            }

            _sumSquares[c] += v * (double)v;
            _sum[c] += v;
            _windowSum[c] += v;
        }
    }

    /// <summary>Tracks the first and last frame holding sound (any channel at or above <see cref="SilenceLevel"/>).</summary>
    private void Sound(ReadOnlySpan<float> run, int frames)
    {
        if (Math.Abs(TensorPrimitives.MaxMagnitude(run)) < SilenceLevel)
        {
            return;
        }

        int first = -1;
        int last = -1;
        for (int i = 0; i < run.Length && first < 0; i++)
        {
            first = Math.Abs(run[i]) >= SilenceLevel ? i / _channels : -1;
        }

        for (int i = run.Length - 1; i >= 0 && last < 0; i--)
        {
            last = Math.Abs(run[i]) >= SilenceLevel ? i / _channels : -1;
        }

        if (_firstSound < 0)
        {
            _firstSound = _frames + first;
        }

        _lastSound = _frames + Math.Min(last, frames - 1);
    }

    private void CloseWindow()
    {
        for (int c = 0; c < _channels; c++)
        {
            double mean = _windowSum[c] / _rate;
            _positiveWindows[c] += mean > 0 ? 1 : 0;
            _negativeWindows[c] += mean < 0 ? 1 : 0;
            _windowSum[c] = 0;
        }

        _windows++;
        _windowFill = 0;
    }

    public void End()
    {
        long frames = Math.Max(1, _frames);
        Levels = new ChannelLevels[_channels];
        for (int c = 0; c < _channels; c++)
        {
            double mean = _sum[c] / frames;
            long same = mean > 0 ? _positiveWindows[c] : mean < 0 ? _negativeWindows[c] : 0;
            Levels[c] = new ChannelLevels(_peak[c], Math.Sqrt(_sumSquares[c] / frames), mean, _overs[c], _windows == 0 ? 0 : (double)same / _windows);
        }

        LeadingSilenceSeconds = _firstSound < 0 ? (double)_frames / _rate : (double)_firstSound / _rate;
        TrailingSilenceSeconds = _lastSound < 0 ? 0 : (double)(_frames - 1 - _lastSound) / _rate;
    }
}

/// <summary>
/// Whether every sample is an exact multiple of 2^-23 and, if so, the OR of the scaled integers, giving
/// the effective bit depth (subplan 10, AUD-PADDED-BITS).
/// </summary>
public sealed class BitDepthAnalyzer : IPcmAnalyzer
{
    private float[] _scaled = [];
    private int[] _integers = [];
    private int _or;
    private bool _integral = true;
    private bool _any;

    public bool Integral => _integral && _any;

    /// <summary>24 minus the trailing zeros of the OR; 0 for digital silence; -1 when not integral.</summary>
    public int EffectiveBits => !Integral ? -1 : _or == 0 ? 0 : 24 - System.Numerics.BitOperations.TrailingZeroCount(_or);

    public void Begin(WavFormat format)
    {
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        if (!_integral || interleaved.Length == 0)
        {
            return;
        }

        _any = true;
        if (_scaled.Length < interleaved.Length)
        {
            _scaled = new float[interleaved.Length];
            _integers = new int[interleaved.Length];
        }

        Span<float> scaled = _scaled.AsSpan(0, interleaved.Length);
        Span<int> integers = _integers.AsSpan(0, interleaved.Length);
        TensorPrimitives.Multiply(interleaved, 8388608f, scaled);
        TensorPrimitives.ConvertTruncating<float, int>(scaled, integers);
        int or = _or;
        for (int i = 0; i < scaled.Length; i++)
        {
            if (integers[i] != scaled[i])
            {
                _integral = false;
                return;
            }

            or |= integers[i];
        }

        _or = or;
    }

    public void End()
    {
    }
}

/// <summary>
/// Clipping (subplan 10): for integer sources, runs of three or more same-sign samples at |x| ≥ 0.1,
/// each within one 16-bit step of the previous, entered or left by a step of at least 8 steps, kept in
/// a 0.1 dB level histogram per channel and summed within 0.2 dB of the channel's peak. For lossy and
/// float sources, the share of samples at or above 98 % of the peak.
/// </summary>
public sealed class ClippingAnalyzer : IPcmAnalyzer
{
    private const double Lsb = 1.0 / 32768;
    private const int Bins = 261;
    private const int BinOffset = 200;
    private const int MaxPositions = 20;
    private const int AmplitudeBins = 4096;
    private int _channels = 1;
    private int _rate = 1;
    private long _frames;
    private ChannelState[] _states = [];
    private readonly long[] _amplitude = new long[AmplitudeBins];
    private const int Chunk = 64;
    private const double Margin = 1.001;
    private long _samples;
    private double _peak;

    public ClippingStats Result { get; private set; } = new(0, 0, 0, -240, 0, []);

    public void Begin(WavFormat format)
    {
        _channels = Math.Max(1, format.Channels);
        _rate = Math.Max(1, format.SampleRate);
        _states = [.. Enumerable.Range(0, _channels).Select(_ => new ChannelState())];
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        int f = 0;
        while (f < frames)
        {
            int take = Math.Min(frames - f, Chunk);
            ReadOnlySpan<float> chunk = interleaved.Slice(f * _channels, take * _channels);
            Histogram(chunk);
            if (MayHoldRun(chunk))
            {
                for (int i = 0; i < take; i++)
                {
                    long position = _frames + f + i;
                    for (int c = 0; c < _channels; c++)
                    {
                        _states[c].Next(chunk[(i * _channels) + c], position);
                    }
                }
            }
            else
            {
                for (int c = 0; c < _channels; c++)
                {
                    _states[c].Skip(chunk, c, _channels, _frames + f);
                }
            }

            f += take;
        }

        _frames += frames;
        _samples += frames * (long)_channels;
    }

    /// <summary>
    /// The amplitude histogram only needs samples that can reach 98 % of the final peak: those below the
    /// bin of 98 % of the peak so far are skipped (the final peak is never lower), so the count is exact.
    /// </summary>
    private void Histogram(ReadOnlySpan<float> chunk)
    {
        float top = Math.Abs(TensorPrimitives.MaxMagnitude(chunk));
        if (top > _peak)
        {
            _peak = top;
        }

        int floorBin = Math.Min(AmplitudeBins - 1, (int)(0.98 * _peak * (AmplitudeBins / 2)));
        if (Math.Min(AmplitudeBins - 1, (int)(top * (double)(AmplitudeBins / 2))) < floorBin)
        {
            return;
        }

        foreach (float x in chunk)
        {
            int bin = Math.Min(AmplitudeBins - 1, (int)(Math.Abs((double)x) * (AmplitudeBins / 2)));
            if (bin >= floorBin)
            {
                _amplitude[bin]++;
            }
        }
    }

    /// <summary>
    /// False when no sample in the chunk is within one 16-bit step of the previous sample of its channel at
    /// 0.1 or more (no run can grow); then every channel only needs its last sample remembered.
    /// </summary>
    private bool MayHoldRun(ReadOnlySpan<float> chunk)
    {
        for (int c = 0; c < _channels; c++)
        {
            if (_states[c].Continues(chunk[c]))
            {
                return true;
            }
        }

        int i = _channels;
        if (Vector.IsHardwareAccelerated)
        {
            int width = Vector<float>.Count;
            // Slightly wider limits than Next uses, so float rounding can only send a chunk to the exact path.
            Vector<float> lsb = new((float)(Lsb * Margin));
            Vector<float> loud = new((float)(0.1 / Margin));
            for (; i <= chunk.Length - width; i += width)
            {
                Vector<float> x = new(chunk[i..]);
                Vector<float> previous = new(chunk[(i - _channels)..]);
                Vector<int> flat = Vector.LessThanOrEqual(Vector.Abs(x - previous), lsb) & Vector.GreaterThanOrEqual(Vector.Abs(x), loud);
                if (flat != Vector<int>.Zero)
                {
                    return true;
                }
            }
        }

        for (; i < chunk.Length; i++)
        {
            if (Math.Abs((double)chunk[i] - chunk[i - _channels]) <= Lsb * Margin && Math.Abs((double)chunk[i]) >= 0.1 / Margin)
            {
                return true;
            }
        }

        return false;
    }

    public void End()
    {
        ChannelState? worst = null;
        long worstSamples = -1;
        long runs = 0;
        int longest = 0;
        foreach (ChannelState state in _states)
        {
            state.Finish();
            state.Sum(out long r, out long s, out int l);
            if (s > worstSamples)
            {
                worstSamples = s;
                worst = state;
            }

            runs = Math.Max(runs, r);
            longest = Math.Max(longest, l);
        }

        double threshold = 0.98 * _peak;
        long crowded = 0;
        for (int i = Math.Min(AmplitudeBins - 1, (int)(threshold * (AmplitudeBins / 2))); i < AmplitudeBins; i++)
        {
            crowded += _amplitude[i];
        }

        double peakDb = 20 * Math.Log10(Math.Max(1e-12, _peak));
        if (worst is null)
        {
            Result = new ClippingStats(0, 0, 0, peakDb, 0, []);
            return;
        }

        worst.Sum(out long worstRuns, out long clipped, out int worstLongest);
        Result = new ClippingStats(
            worstRuns,
            clipped,
            worstLongest,
            peakDb,
            _samples == 0 ? 0 : (double)crowded / _samples,
            [.. worst.Positions.Select(p => Math.Round((double)p / _rate, 3))]);
    }

    private static int Bin(double level) => Math.Clamp((int)Math.Round(20 * Math.Log10(Math.Max(1e-9, level)) * 10) + BinOffset, 0, Bins - 1);

    private sealed class ChannelState
    {
        private readonly long[] _runs = new long[Bins];
        private readonly long[] _clipped = new long[Bins];
        private readonly int[] _longest = new int[Bins];
        private int _run;
        private int _sign;
        private double _previous;
        private double _jumpIn;
        private double _level;
        private long _start;
        private double _peak;
        private double _topLevel;

        public List<long> Positions { get; } = new(MaxPositions);

        public void Next(float value, long position)
        {
            double x = value;
            double a = Math.Abs(x);
            if (a > _peak)
            {
                _peak = a;
            }

            int sign = x > 0 ? 1 : -1;
            if (_run > 0 && a >= 0.1 && sign == _sign && Math.Abs(x - _previous) <= Lsb)
            {
                _run++;
            }
            else
            {
                if (_run >= 3)
                {
                    Close(Math.Abs(x - _previous));
                }

                if (a >= 0.1)
                {
                    _run = 1;
                    _sign = sign;
                    _jumpIn = Math.Abs(x - _previous);
                    _level = a;
                    _start = position;
                }
                else
                {
                    _run = 0;
                }
            }

            _previous = x;
        }

        /// <summary>Whether the sample could extend the current run (the test of <see cref="Next"/>, without the run-length condition).</summary>
        public bool Continues(float value) => Math.Abs((double)value - _previous) <= Lsb * Margin && Math.Abs(value) >= 0.1 / Margin;

        /// <summary>
        /// The state <see cref="Next"/> would reach over a chunk where no sample continues a run: a pending
        /// run closes at the first sample, and only the last sample can start one.
        /// </summary>
        public void Skip(ReadOnlySpan<float> chunk, int channel, int channels, long position)
        {
            int frames = chunk.Length / channels;
            float first = chunk[channel];
            if (_run >= 3)
            {
                Close(Math.Abs(first - _previous));
            }

            for (int i = channel; i < chunk.Length; i += channels)
            {
                double a = Math.Abs((double)chunk[i]);
                if (a > _peak)
                {
                    _peak = a;
                }
            }

            double last = chunk[((frames - 1) * channels) + channel];
            double before = frames > 1 ? chunk[((frames - 2) * channels) + channel] : _previous;
            if (Math.Abs(last) >= 0.1)
            {
                _run = 1;
                _sign = last > 0 ? 1 : -1;
                _jumpIn = Math.Abs(last - before);
                _level = Math.Abs(last);
                _start = position + frames - 1;
            }
            else
            {
                _run = 0;
            }

            _previous = last;
        }

        public void Finish()
        {
            if (_run >= 3)
            {
                Close(0);
            }

            _run = 0;
        }

        /// <summary>Totals within 0.2 dB of this channel's peak.</summary>
        public void Sum(out long runs, out long clipped, out int longest)
        {
            int peakBin = Bin(_peak);
            runs = 0;
            clipped = 0;
            longest = 0;
            for (int b = Math.Max(0, peakBin - 2); b <= Math.Min(Bins - 1, peakBin + 2); b++)
            {
                runs += _runs[b];
                clipped += _clipped[b];
                longest = Math.Max(longest, _longest[b]);
            }
        }

        private void Close(double jumpOut)
        {
            if (Math.Max(_jumpIn, jumpOut) < 8 * Lsb)
            {
                return;
            }

            int bin = Bin(_level);
            _runs[bin]++;
            _clipped[bin] += _run;
            _longest[bin] = Math.Max(_longest[bin], _run);

            // Keep up to 20 positions of runs near the highest plateau seen so far.
            double levelDb = 20 * Math.Log10(_level);
            double topDb = 20 * Math.Log10(Math.Max(1e-9, _topLevel));
            if (levelDb > topDb + 0.2)
            {
                _topLevel = _level;
                Positions.Clear();
            }

            if (levelDb >= 20 * Math.Log10(Math.Max(1e-9, _topLevel)) - 0.2 && Positions.Count < MaxPositions)
            {
                Positions.Add(_start);
            }
        }
    }
}

/// <summary>Left/right correlation and whether both channels are identical (subplan 10, StereoAnalyzer).</summary>
public sealed class StereoAnalyzer : IPcmAnalyzer
{
    private int _channels = 1;
    private double _l;
    private double _r;
    private double _ll;
    private double _rr;
    private double _lr;
    private long _n;
    private bool _identical = true;

    public double Correlation { get; private set; }

    public bool Identical => _channels >= 2 && _identical && _n > 0;

    public void Begin(WavFormat format) => _channels = Math.Max(1, format.Channels);

    public void Process(ReadOnlySpan<float> interleaved)
    {
        if (_channels < 2)
        {
            return;
        }

        int frames = interleaved.Length / _channels;
        for (int f = 0; f < frames; f++)
        {
            double l = interleaved[f * _channels];
            double r = interleaved[(f * _channels) + 1];
            _identical &= l == r;
            _l += l;
            _r += r;
            _ll += l * l;
            _rr += r * r;
            _lr += l * r;
        }

        _n += frames;
    }

    public void End()
    {
        if (_n == 0 || _channels < 2)
        {
            Correlation = 1;
            return;
        }

        double cov = (_lr / _n) - ((_l / _n) * (_r / _n));
        double vl = (_ll / _n) - ((_l / _n) * (_l / _n));
        double vr = (_rr / _n) - ((_r / _n) * (_r / _n));
        Correlation = vl <= 1e-20 || vr <= 1e-20 ? 1 : Math.Clamp(cov / Math.Sqrt(vl * vr), -1, 1);
    }
}

/// <summary>
/// The long-term average spectrum of the mid signal and its terminal-drop features (subplan 10,
/// Spectral cutoff analysis, steps 1–7). Every non-silent frame is used; the FFT costs a few percent of
/// the decode. The left channel's spectrum is kept from every fourth frame for out-of-phase material.
/// </summary>
public sealed class SpectrumAnalyzer : IPcmAnalyzer
{
    private const int LeftEvery = 4;
    private int _channels = 1;
    private int _rate = 1;
    private int _size = 4096;
    private Fft? _fft;
    private float[] _window = [];
    private float[] _mid = [];
    private float[] _left = [];
    private float[] _re = [];
    private float[] _im = [];
    private double[] _acc = [];
    private double[] _accLeft = [];
    private int _fill;
    private int _framesUsed;
    private int _leftFrames;
    private double _midEnergy;
    private double _leftEnergy;

    public SpectrumStats Result { get; private set; } = SpectrumStats.Insufficient(0, 0, 0, 0);

    public static int FftSizeFor(int sampleRate) => sampleRate <= 50_000 ? 4096 : sampleRate <= 100_000 ? 8192 : 16384;

    public void Begin(WavFormat format)
    {
        _channels = Math.Max(1, format.Channels);
        _rate = Math.Max(1, format.SampleRate);
        _size = FftSizeFor(_rate);
        _fft = new Fft(_size);
        _window = new float[_size];
        for (int i = 0; i < _size; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / _size)));
        }

        _mid = new float[_size];
        _left = new float[_size];
        _re = new float[_size];
        _im = new float[_size];
        _acc = new double[(_size / 2) + 1];
        _accLeft = new double[(_size / 2) + 1];
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        int f = 0;
        while (f < frames)
        {
            // Fill the frame in one tight run, then transform it.
            int take = Math.Min(frames - f, _size - _fill);
            Span<float> mid = _mid.AsSpan(_fill, take);
            Span<float> left = _left.AsSpan(_fill, take);
            ReadOnlySpan<float> source = interleaved.Slice(f * _channels, take * _channels);
            if (_channels == 1)
            {
                source.CopyTo(mid);
                source.CopyTo(left);
            }
            else if (_channels == 2)
            {
                for (int i = 0; i < take; i++)
                {
                    float l = source[2 * i];
                    left[i] = l;
                    mid[i] = (l + source[(2 * i) + 1]) / 2;
                }
            }
            else
            {
                for (int i = 0; i < take; i++)
                {
                    float l = source[i * _channels];
                    left[i] = l;
                    mid[i] = (l + source[(i * _channels) + 1]) / 2;
                }
            }

            _fill += take;
            f += take;
            if (_fill == _size)
            {
                Frame();
                _fill = 0;
            }
        }
    }

    public void End()
    {
        double nyquist = _rate / 2.0;
        double seconds = (double)_framesUsed * _size / _rate;
        bool useLeft = _channels >= 2 && _framesUsed > 0 && _leftFrames > 0
            && 10 * Math.Log10(Math.Max(1e-30, _midEnergy / _framesUsed)) < (10 * Math.Log10(Math.Max(1e-30, _leftEnergy / _leftFrames))) - 20;
        int frames = useLeft ? _leftFrames : _framesUsed;
        if (frames < 100 || seconds < 15 || _rate <= 32_000)
        {
            Result = SpectrumStats.Insufficient(nyquist, _size, frames, seconds);
            return;
        }

        Result = Measure(useLeft ? _accLeft : _acc, frames, _size, _rate, seconds);
    }

    /// <summary>Steps 5–7 on an accumulated power spectrum; public for tests on synthetic spectra.</summary>
    public static SpectrumStats Measure(double[] accumulated, int frames, int size, int rate, double seconds)
    {
        int bins = accumulated.Length;
        double binHz = (double)rate / size;
        double nyquist = rate / 2.0;

        // Step 5: smooth in the power domain over ±100 Hz, then to dB (normalised by (N/2)²).
        double[] prefix = new double[bins + 1];
        for (int k = 0; k < bins; k++)
        {
            prefix[k + 1] = prefix[k] + accumulated[k];
        }

        int half = Math.Max(1, (int)(100 / binHz));
        double norm = Math.Max(1, frames) * ((double)size * size / 4);
        double[] db = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            int a = Math.Max(1, k - half);
            int b = Math.Min(bins - 1, k + half);
            double mean = (prefix[b + 1] - prefix[a]) / Math.Max(1, b - a + 1);
            db[k] = 10 * Math.Log10((mean / norm) + 1e-30);
        }

        // Step 6: terminal-drop search with prefix sums and a suffix maximum.
        double[] dbPrefix = new double[bins + 1];
        for (int k = 0; k < bins; k++)
        {
            dbPrefix[k + 1] = dbPrefix[k] + db[k];
        }

        int top = Math.Clamp((int)((nyquist - 50) / binHz), 1, bins - 1);
        double[] suffixMax = new double[bins + 1];
        suffixMax[top + 1] = double.MinValue;
        for (int k = top; k >= 0; k--)
        {
            suffixMax[k] = Math.Max(db[k], suffixMax[k + 1]);
        }

        double Mean(double f0, double f1)
        {
            int a = Math.Clamp((int)(f0 / binHz), 1, bins - 1);
            int b = Math.Clamp((int)(f1 / binHz), a, bins - 1);
            return (dbPrefix[b + 1] - dbPrefix[a]) / (b - a + 1);
        }

        double bestF = nyquist;
        double bestDrop = 0;
        double bestBelow = 0;
        double bestAbove = 0;
        for (double f = 4000; f <= nyquist - 500; f += 50)
        {
            double below = Mean(f - 1000, f - 200);
            double above = Mean(f + 200, Math.Min(f + 1000, nyquist - 50));
            int from = Math.Clamp((int)((f + 200) / binHz), 1, top);
            double maxAbove = suffixMax[from];
            if (maxAbove > below - 15)
            {
                continue;
            }

            double drop = below - above;
            if (drop > bestDrop)
            {
                bestDrop = drop;
                bestF = f;
                bestBelow = below;
                bestAbove = above;
            }
        }

        // Step 7: walk down while the local slope stays 4 dB or more per 500 Hz.
        if (bestDrop > 0)
        {
            double f = bestF;
            while (f - 250 > 3000)
            {
                double slope = db[Math.Clamp((int)((f - 250) / binHz), 0, bins - 1)] - db[Math.Clamp((int)((f + 250) / binHz), 0, bins - 1)];
                if (slope < 4)
                {
                    break;
                }

                f -= 50;
            }

            bestF = Math.Min(nyquist, f + 250);
        }

        byte[] bands = new byte[96];
        for (int band = 0; band < 96; band++)
        {
            int a = Math.Max(1, band * (bins - 1) / 96);
            int b = Math.Max(a, ((band + 1) * (bins - 1) / 96) - 1);
            double mean = (dbPrefix[b + 1] - dbPrefix[a]) / (b - a + 1);
            bands[band] = (byte)Math.Clamp(Math.Round((mean + 160) * 255 / 160), 0, 255);
        }

        return new SpectrumStats(true, bestF, bestDrop, bestBelow, bestAbove, nyquist, size, frames, seconds, bands);
    }

    private void Frame()
    {
        float energy = TensorPrimitives.SumOfSquares<float>(_mid) / _size;
        if (energy < 1e-7f)
        {
            return;
        }

        Accumulate(_mid, _acc);
        _midEnergy += energy;
        _framesUsed++;
        if (_channels >= 2 && _framesUsed % LeftEvery == 0)
        {
            _leftEnergy += TensorPrimitives.SumOfSquares<float>(_left) / _size;
            Accumulate(_left, _accLeft);
            _leftFrames++;
        }
    }

    private void Accumulate(float[] signal, double[] accumulator)
    {
        TensorPrimitives.Multiply(signal, _window, _re);
        Array.Clear(_im);
        _fft!.Transform(_re, _im);
        for (int k = 0; k < accumulator.Length; k++)
        {
            accumulator[k] += (_re[k] * (double)_re[k]) + (_im[k] * (double)_im[k]);
        }
    }
}

/// <summary>
/// Digital-zero gaps of 1–50 ms with sound (above −50 dBFS) in the 50 ms around them and an abrupt
/// edge on both sides (subplan 10, COR-DROPOUT).
/// </summary>
public sealed class DropoutAnalyzer : IPcmAnalyzer
{
    private const double Edge = 0.01;
    private const int MaxRecorded = 20;
    private static readonly int[] Packets = [588, 1024, 1152, 4096];
    private static readonly double MinRms = Math.Pow(10, -50 / 20.0);
    private readonly List<double> _positions = new(MaxRecorded);
    private readonly List<int> _lengths = new(MaxRecorded);
    private readonly Pending[] _pending = new Pending[8];
    private int _channels = 1;
    private int _rate = 1;
    private int _window = 1;
    private const int Chunk = 256;
    private float[] _history = [];
    private int _historyIndex;
    private int _historyFrames;
    private int _active;
    private long _frame;
    private int _zeroRun;
    private long _runStart;
    private double _rmsBefore;
    private double _edgeBefore;
    private double _previousMax;
    private int _gaps;
    private int _aligned;

    public DropoutStats Result { get; private set; } = new(0, 0, [], []);

    public static bool Aligned(int length)
    {
        foreach (int packet in Packets)
        {
            int r = length % packet;
            if (length >= packet - 1 && (r <= 1 || packet - r <= 1))
            {
                return true;
            }
        }

        return false;
    }

    public void Begin(WavFormat format)
    {
        _channels = Math.Max(1, format.Channels);
        _rate = Math.Max(1, format.SampleRate);
        _window = Math.Max(1, _rate / 20);
        _history = new float[_window * _channels];
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        int f = 0;
        while (f < frames)
        {
            int take = Math.Min(frames - f, Chunk);
            ReadOnlySpan<float> chunk = interleaved.Slice(f * _channels, take * _channels);

            // Nearly every chunk has no sample near silence and nothing pending: only remember it.
            if (_zeroRun == 0 && _active == 0 && Math.Abs(TensorPrimitives.MinMagnitude(chunk)) >= LevelAnalyzer.SilenceLevel)
            {
                Remember(chunk);
                _previousMax = Math.Abs(TensorPrimitives.MaxMagnitude(chunk[^_channels..]));
                _frame += take;
            }
            else
            {
                for (int i = 0; i < take; i++)
                {
                    Frame(chunk.Slice(i * _channels, _channels));
                }
            }

            f += take;
        }
    }

    private void Frame(ReadOnlySpan<float> frame)
    {
        double max = 0;
        double energy = 0;
        foreach (float sample in frame)
        {
            double v = sample;
            max = Math.Max(max, Math.Abs(v));
            energy += v * v;
        }

        energy /= _channels;
        if (max < LevelAnalyzer.SilenceLevel)
        {
            if (_zeroRun++ == 0)
            {
                _runStart = _frame;
                _rmsBefore = _historyFrames == _window ? Math.Sqrt(TensorPrimitives.SumOfSquares<float>(_history) / _history.Length) : 0;
                _edgeBefore = _previousMax;
            }
        }
        else
        {
            if (_zeroRun > 0)
            {
                int length = _zeroRun;
                bool sized = length >= _rate / 1000 && length <= _rate / 20;
                if (sized && _rmsBefore >= MinRms && _edgeBefore >= Edge && max >= Edge)
                {
                    for (int i = 0; i < _pending.Length; i++)
                    {
                        if (!_pending[i].Active)
                        {
                            _pending[i] = new Pending { Active = true, Start = _runStart, Length = length };
                            _active++;
                            break;
                        }
                    }
                }

                _zeroRun = 0;
            }

            for (int i = 0; i < _pending.Length && _active > 0; i++)
            {
                if (!_pending[i].Active)
                {
                    continue;
                }

                _pending[i].Sum += energy;
                if (++_pending[i].Count == _window)
                {
                    if (Math.Sqrt(_pending[i].Sum / _window) >= MinRms)
                    {
                        Confirm(_pending[i]);
                    }

                    _pending[i].Active = false;
                    _active--;
                }
            }
        }

        Remember(frame);
        _previousMax = max;
        _frame++;
    }

    /// <summary>Keeps the last 50 ms of samples for the level before a gap.</summary>
    private void Remember(ReadOnlySpan<float> samples)
    {
        int length = _history.Length;
        if (samples.Length >= length)
        {
            samples[^length..].CopyTo(_history);
            _historyIndex = 0;
        }
        else
        {
            int first = Math.Min(samples.Length, length - _historyIndex);
            samples[..first].CopyTo(_history.AsSpan(_historyIndex));
            samples[first..].CopyTo(_history);
            _historyIndex = (_historyIndex + samples.Length) % length;
        }

        _historyFrames = Math.Min(_window, _historyFrames + (samples.Length / _channels));
    }

    public void End() => Result = new DropoutStats(_gaps, _aligned, [.. _positions], [.. _lengths]);

    private void Confirm(Pending gap)
    {
        _gaps++;
        if (Aligned(gap.Length))
        {
            _aligned++;
        }

        if (_positions.Count < MaxRecorded)
        {
            _positions.Add(Math.Round((double)gap.Start / _rate, 3));
            _lengths.Add(gap.Length);
        }
    }

    private struct Pending
    {
        public bool Active;
        public long Start;
        public int Length;
        public double Sum;
        public int Count;
    }
}

/// <summary>SIMD over interleaved samples: when the vector width is a multiple of the channel count, lane <c>j</c> always holds channel <c>j % channels</c>.</summary>
internal static class Lanes
{
    public static bool Fit(int channels) => Vector.IsHardwareAccelerated && channels > 0 && Vector<float>.Count % channels == 0;
}
