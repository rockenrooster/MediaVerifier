namespace MediaVerifier.Core.Analysis.Audio;

/// <summary>
/// In-place iterative radix-2 FFT on <see cref="float"/> (subplan 10, Performance): twiddles and the
/// bit-reversal table are computed once per size; one instance per worker, no allocation per call.
/// </summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly int[] _reverse;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The FFT size must be a power of two.");
        }

        _size = size;
        _reverse = new int[size];
        int bits = System.Numerics.BitOperations.Log2((uint)size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reverse[i] = r;
        }

        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double angle = -2 * Math.PI * k / size;
            _cos[k] = (float)Math.Cos(angle);
            _sin[k] = (float)Math.Sin(angle);
        }
    }

    public int Size => _size;

    /// <summary>Transforms <paramref name="re"/> and <paramref name="im"/> in place (forward, unscaled).</summary>
    public void Transform(Span<float> re, Span<float> im)
    {
        if (re.Length != _size || im.Length != _size)
        {
            throw new ArgumentException("Buffers must match the FFT size.");
        }

        for (int i = 0; i < _size; i++)
        {
            int j = _reverse[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int length = 2; length <= _size; length <<= 1)
        {
            int half = length >> 1;
            int step = _size / length;
            for (int start = 0; start < _size; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step];
                    float wi = _sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    float xr = (re[b] * wr) - (im[b] * wi);
                    float xi = (re[b] * wi) + (im[b] * wr);
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }
    }
}
