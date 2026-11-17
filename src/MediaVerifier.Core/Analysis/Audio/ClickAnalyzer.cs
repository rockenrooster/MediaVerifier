using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;

namespace MediaVerifier.Core.Analysis.Audio;

/// <summary>Isolated sample-level jumps (subplan 10, COR-CLICKS).</summary>
public sealed record ClickStats(int Clicks, double[] Positions)
{
    public static ClickStats None { get; } = new(0, []);
}

/// <summary>
/// Finds isolated sample-level jumps (subplan 10, COR-CLICKS; task A-12). A click is a step into one sample
/// and a step out of it the other way, both at least <see cref="Ratio"/> times the local median step and at
/// least <see cref="MinStep"/> of full scale, with ordinary steps (under a quarter of that limit) on both
/// sides. The local median is the highest of the step medians of the 1,024-sample block holding the sample
/// and the blocks either side, so that the edges of loud passages next to silence are judged against the
/// loud side; each block's median comes from every sixteenth step. Samples at full scale
/// (clipping) and steps next to two exact zeros (dropout edges, the start of the file) are skipped. A click
/// in several channels at the same sample counts once.
/// </summary>
public sealed class ClickAnalyzer : IPcmAnalyzer
{
    public const int Block = 1024;
    public const float Ratio = 20;
    public const float MinStep = 0.05f;
    public const int MaxPositions = 20;
    private const int Stride = 16;
    private readonly float[] _scratch = new float[Block / Stride];
    private readonly float[] _steps = new float[Block];
    private readonly List<double> _positions = [];
    private float[][] _window = [];
    private float[] _medians = [];
    private float[] _limits = [];
    private bool[] _candidate = [];
    private int _channels = 1;
    private int _rate = 1;
    private int _fill;
    private int _blocks;
    private int _clicks;

    public ClickStats Result { get; private set; } = ClickStats.None;

    public void Begin(WavFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _channels = Math.Max(1, format.Channels);
        _rate = Math.Max(1, format.SampleRate);
        _window = new float[_channels][];
        for (int c = 0; c < _channels; c++)
        {
            // Three blocks per channel: previous, current (being judged) and next.
            _window[c] = new float[3 * Block];
        }

        _medians = new float[_channels * 3];
        _limits = new float[_channels];
        _candidate = new bool[_channels];
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        int f = 0;
        while (f < frames)
        {
            int take = Math.Min(frames - f, Block - _fill);
            ReadOnlySpan<float> source = interleaved.Slice(f * _channels, take * _channels);
            for (int c = 0; c < _channels; c++)
            {
                Span<float> target = _window[c].AsSpan((2 * Block) + _fill, take);
                if (_channels == 1)
                {
                    source.CopyTo(target);
                    continue;
                }

                for (int i = 0, s = c; i < take; i++, s += _channels)
                {
                    target[i] = source[s];
                }
            }

            _fill += take;
            f += take;
            if (_fill == Block)
            {
                Advance(Block);
            }
        }
    }

    public void End()
    {
        if (_fill > 0)
        {
            // Hold the last sample to the end of the block so that the padding adds no steps.
            for (int c = 0; c < _channels; c++)
            {
                Array.Fill(_window[c], _window[c][(2 * Block) + _fill - 1], (2 * Block) + _fill, Block - _fill);
            }

            Advance(_fill);
        }

        if (_blocks > 0)
        {
            for (int c = 0; c < _channels; c++)
            {
                Array.Fill(_window[c], _window[c][(2 * Block) - 1], 2 * Block, Block);
                _medians[(c * 3) + 2] = _medians[(c * 3) + 1];
            }

            Judge();
        }

        Result = new ClickStats(_clicks, [.. _positions]);
    }

    /// <summary>The block in the "next" slot is complete: judge the current block, then move everything one block back.</summary>
    private void Advance(int valid)
    {
        for (int c = 0; c < _channels; c++)
        {
            _medians[(c * 3) + 2] = BlockMedian(_window[c], valid);
        }

        if (_blocks > 0)
        {
            Judge();
        }

        for (int c = 0; c < _channels; c++)
        {
            Array.Copy(_window[c], Block, _window[c], 0, 2 * Block);
            _medians[c * 3] = _medians[(c * 3) + 1];
            _medians[(c * 3) + 1] = _medians[(c * 3) + 2];
        }

        _blocks++;
        _fill = 0;
    }

    private float BlockMedian(float[] window, int valid)
    {
        int count = 0;
        for (int i = 0; i < valid; i += Stride)
        {
            int n = (2 * Block) + i;
            _scratch[count++] = MathF.Abs(window[n] - window[n - 1]);
        }

        return count == 0 ? 0 : Select(_scratch.AsSpan(0, count), count / 2);
    }

    /// <summary>Checks every sample of the current block (frames <c>Block</c> to <c>2 × Block</c> of the window).</summary>
    private void Judge()
    {
        // Most blocks have no step near the limit: one vectorised pass per channel rules them out.
        bool any = false;
        for (int c = 0; c < _channels; c++)
        {
            float local = MathF.Max(_medians[c * 3], MathF.Max(_medians[(c * 3) + 1], _medians[(c * 3) + 2]));
            _limits[c] = MathF.Max(MinStep, Ratio * local);
            Span<float> steps = _steps.AsSpan();
            System.Numerics.Tensors.TensorPrimitives.Subtract(_window[c].AsSpan(Block, Block), _window[c].AsSpan(Block - 1, Block), steps);
            _candidate[c] = MathF.Abs(System.Numerics.Tensors.TensorPrimitives.MaxMagnitude(steps)) >= _limits[c];
            any |= _candidate[c];
        }

        if (!any)
        {
            return;
        }

        long start = (long)(_blocks - 1) * Block;
        for (int n = Block; n < 2 * Block; n++)
        {
            bool click = false;
            for (int c = 0; c < _channels && !click; c++)
            {
                if (!_candidate[c])
                {
                    continue;
                }

                float[] w = _window[c];
                float a = w[n] - w[n - 1];
                float limit = _limits[c];
                if (MathF.Abs(a) < limit)
                {
                    continue;
                }

                float b = w[n + 1] - w[n];
                click = MathF.Abs(b) >= limit
                    && (a > 0) != (b > 0)
                    && MathF.Abs(w[n - 1] - w[n - 2]) < limit / 4
                    && MathF.Abs(w[n + 2] - w[n + 1]) < limit / 4
                    && MathF.Abs(w[n]) < 0.999f
                    && !(w[n - 1] == 0 && w[n - 2] == 0)
                    && !(w[n + 1] == 0 && w[n + 2] == 0);
            }

            if (click)
            {
                _clicks++;
                if (_positions.Count < MaxPositions)
                {
                    _positions.Add(Math.Round((double)(start + n - Block) / _rate, 3));
                }
            }
        }
    }

    /// <summary>The k-th smallest value (quickselect; reorders the span).</summary>
    internal static float Select(Span<float> values, int k)
    {
        int left = 0;
        int right = values.Length - 1;
        while (left < right)
        {
            float pivot = values[(left + right) >>> 1];
            int i = left;
            int j = right;
            while (i <= j)
            {
                while (values[i] < pivot)
                {
                    i++;
                }

                while (values[j] > pivot)
                {
                    j--;
                }

                if (i <= j)
                {
                    (values[i], values[j]) = (values[j], values[i]);
                    i++;
                    j--;
                }
            }

            if (k <= j)
            {
                right = j;
            }
            else if (k >= i)
            {
                left = i;
            }
            else
            {
                return values[k];
            }
        }

        return values[k];
    }
}
