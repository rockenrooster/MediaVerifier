using System.Numerics;
using System.Runtime.InteropServices;

namespace MediaVerifier.Core.Analysis.Audio;

/// <summary>
/// In-place iterative radix-2 FFT on <see cref="float"/> (subplan 10, Performance): twiddles per stage and the
/// bit-reversal table are computed once per size; one instance per worker, no allocation per call. Stages
/// whose half-length is at least one <see cref="Vector{T}"/> run their butterflies with SIMD; the scalar path
/// gives the same results bit for bit (same operations in the same order, no fused multiply-add).
/// </summary>
public sealed class Fft
{
    private readonly int _size;
    private readonly int[] _reverse;
    private readonly float[][] _cos;
    private readonly float[][] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The FFT size must be a power of two.");
        }

        _size = size;
        _reverse = new int[size];
        int bits = BitOperations.Log2((uint)size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reverse[i] = r;
        }

        // Stage s combines blocks of 2^(s+1): its twiddles are e^(-2πik / 2^(s+1)) for k below 2^s.
        _cos = new float[bits][];
        _sin = new float[bits][];
        for (int stage = 0; stage < bits; stage++)
        {
            int half = 1 << stage;
            _cos[stage] = new float[half];
            _sin[stage] = new float[half];
            for (int k = 0; k < half; k++)
            {
                int index = k * (size / (2 * half));
                double angle = -2 * Math.PI * index / size;
                _cos[stage][k] = (float)Math.Cos(angle);
                _sin[stage][k] = (float)Math.Sin(angle);
            }
        }
    }

    public int Size => _size;

    /// <summary>Transforms <paramref name="re"/> and <paramref name="im"/> in place (forward, unscaled).</summary>
    public void Transform(Span<float> re, Span<float> im) => Transform(re, im, Vector.IsHardwareAccelerated);

    /// <summary>The transform with SIMD on or off (tests compare both).</summary>
    internal void Transform(Span<float> re, Span<float> im, bool simd)
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

        int width = Vector<float>.Count;
        ref float r0 = ref MemoryMarshal.GetReference(re);
        ref float i0 = ref MemoryMarshal.GetReference(im);
        for (int stage = 0, half = 1; half < _size; stage++, half <<= 1)
        {
            float[] cos = _cos[stage];
            float[] sin = _sin[stage];
            int length = half << 1;
            if (simd && half >= width)
            {
                // Indices stay inside the buffers by construction: start + k + half + width <= size.
                for (int start = 0; start < _size; start += length)
                {
                    for (int k = 0; k < half; k += width)
                    {
                        nuint a = (nuint)(start + k);
                        nuint b = a + (nuint)half;
                        Vector<float> wr = new(cos, k);
                        Vector<float> wi = new(sin, k);
                        Vector<float> br = Vector.LoadUnsafe(ref r0, b);
                        Vector<float> bi = Vector.LoadUnsafe(ref i0, b);
                        Vector<float> xr = (br * wr) - (bi * wi);
                        Vector<float> xi = (br * wi) + (bi * wr);
                        Vector<float> ar = Vector.LoadUnsafe(ref r0, a);
                        Vector<float> ai = Vector.LoadUnsafe(ref i0, a);
                        (ar - xr).StoreUnsafe(ref r0, b);
                        (ai - xi).StoreUnsafe(ref i0, b);
                        (ar + xr).StoreUnsafe(ref r0, a);
                        (ai + xi).StoreUnsafe(ref i0, a);
                    }
                }

                continue;
            }

            for (int start = 0; start < _size; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    int a = start + k;
                    int b = a + half;
                    float wr = cos[k];
                    float wi = sin[k];
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
