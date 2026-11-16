using System.Buffers.Binary;

namespace MediaVerifier.Core.Ffmpeg;

/// <summary>The audio layout from the WAV header FFmpeg writes on a pipe.</summary>
public sealed record WavFormat(int Channels, int SampleRate, int BitsPerSample);

/// <summary>
/// Reads 32-bit float WAV from a forward-only stream (subplan 04, WAV pipe reader): accepts format tag 3
/// or extensible with the float sub-format, skips any other chunk (LIST), stops at <c>data</c>, ignores
/// the RIFF and data sizes (0xFFFFFFFF on a pipe), then yields interleaved samples to end of stream.
/// </summary>
public sealed class WavPipeReader
{
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly Stream _stream;
    private readonly byte[] _carry = new byte[4];
    private byte[] _bytes = [];
    private int _carryCount;

    private WavPipeReader(Stream stream, WavFormat format)
    {
        _stream = stream;
        Format = format;
    }

    public WavFormat Format { get; }

    /// <summary>Reads the header. Throws <see cref="InvalidDataException"/> for anything but float WAV.</summary>
    public static async Task<WavPipeReader> OpenAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[12];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE stream.");
        }

        WavFormat? format = null;
        byte[] chunkHeader = new byte[8];
        while (true)
        {
            await ReadExactAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4));
            if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (format is null)
                {
                    throw new InvalidDataException("The data chunk came before the fmt chunk.");
                }

                return new WavPipeReader(stream, format);
            }

            if (size == 0xFFFFFFFF || size > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("A chunk before data has an impossible size.");
            }

            byte[] body = new byte[size + (size & 1)];
            await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false);
            if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                format = ParseFormat(body.AsSpan(0, (int)size));
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="samples"/> with interleaved float samples and returns how many were read;
    /// 0 at the end of the stream. A trailing partial sample at the end of a truncated stream is dropped.
    /// </summary>
    public async ValueTask<int> ReadAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Little-endian platforms only.");
        }

        int capacity = samples.Length * 4;
        if (_bytes.Length < capacity)
        {
            _bytes = new byte[capacity];
        }

        int filled = 0;
        if (_carryCount > 0)
        {
            _carry.AsSpan(0, _carryCount).CopyTo(_bytes);
            filled = _carryCount;
            _carryCount = 0;
        }

        while (filled < capacity)
        {
            int read = await _stream.ReadAsync(_bytes.AsMemory(filled, capacity - filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
            if (filled >= 4 && read < 4096)
            {
                // The pipe gave a short read: hand over what is here rather than wait for a full buffer.
                break;
            }
        }

        int whole = filled & ~3;
        int rest = filled - whole;
        if (rest > 0)
        {
            _bytes.AsSpan(whole, rest).CopyTo(_carry);
            _carryCount = rest;
        }

        Buffer.BlockCopy(_bytes, 0, samples, 0, whole);
        return whole / 4;
    }

    internal static WavFormat ParseFormat(ReadOnlySpan<byte> fmt)
    {
        if (fmt.Length < 16)
        {
            throw new InvalidDataException("fmt chunk too short.");
        }

        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        bool isFloat = tag == FormatIeeeFloat;
        if (tag == FormatExtensible)
        {
            if (fmt.Length < 40)
            {
                throw new InvalidDataException("Extensible fmt chunk too short.");
            }

            isFloat = new Guid(fmt.Slice(24, 16)) == FloatSubFormat;
        }

        if (!isFloat || bits != 32)
        {
            throw new InvalidDataException($"Unexpected WAV format tag {tag} with {bits} bits; 32-bit float expected.");
        }

        if (channels <= 0 || channels > 64 || rate <= 0)
        {
            throw new InvalidDataException("Implausible channel count or sample rate.");
        }

        return new WavFormat(channels, rate, bits);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("The stream ended inside the WAV header.");
            }

            offset += read;
        }
    }
}
