using System.Buffers;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// Positional, bounds-checked reads from the file under test. Small reads go through a one-page
/// read-ahead window so that walking headers costs one I/O per region. A read at or past the end returns
/// fewer bytes (or none); no offset or count makes this throw, only the stream itself can.
/// </summary>
internal sealed class StructureSource : IDisposable
{
    /// <summary>The read-ahead window: one page.</summary>
    public const int WindowSize = 4096;

    /// <summary>The most bytes <see cref="IsPadding"/> inspects; a longer region is judged by its start.</summary>
    public const int PaddingScanLimit = 1024 * 1024;

    private const int ScanBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private byte[]? _window;
    private long _windowStart;
    private int _windowCount;

    public StructureSource(Stream stream)
    {
        _stream = stream;
        Length = Math.Max(0, stream.Length);
        _window = ArrayPool<byte>.Shared.Rent(WindowSize);
    }

    public long Length { get; }

    /// <summary>Copies up to <c>destination.Length</c> bytes at <paramref name="offset"/>; returns how many (fewer only at the end).</summary>
    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Length || destination.IsEmpty)
        {
            return 0;
        }

        long available = Length - offset;
        if (destination.Length > available)
        {
            destination = destination[..(int)available];
        }

        byte[] window = _window ?? throw new ObjectDisposedException(nameof(StructureSource));
        if (offset >= _windowStart && offset - _windowStart + destination.Length <= _windowCount)
        {
            window.AsSpan((int)(offset - _windowStart), destination.Length).CopyTo(destination);
            return destination.Length;
        }

        if (destination.Length > WindowSize)
        {
            return ReadDirect(offset, destination);
        }

        _windowStart = offset;
        _windowCount = 0;
        _windowCount = ReadDirect(offset, window.AsSpan(0, (int)Math.Min(WindowSize, available)));
        int count = Math.Min(destination.Length, _windowCount);
        window.AsSpan(0, count).CopyTo(destination);
        return count;
    }

    /// <summary>True when all of <paramref name="destination"/> could be filled.</summary>
    public bool TryRead(long offset, Span<byte> destination) => Read(offset, destination) == destination.Length;

    /// <summary>Up to <paramref name="count"/> bytes at <paramref name="offset"/> in a new array (shorter at the end).</summary>
    public byte[] ReadBytes(long offset, int count)
    {
        if (offset < 0 || offset >= Length || count <= 0)
        {
            return [];
        }

        int wanted = (int)Math.Min(count, Length - offset);
        byte[] bytes = new byte[wanted];
        int read = Read(offset, bytes);
        return read == wanted ? bytes : bytes[..read];
    }

    /// <summary>Reads straight from the stream, bypassing the window (for sequential scans with their own buffer).</summary>
    public int ReadDirect(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Length || destination.IsEmpty)
        {
            return 0;
        }

        long available = Length - offset;
        if (destination.Length > available)
        {
            destination = destination[..(int)available];
        }

        _stream.Position = offset;
        int total = 0;
        while (total < destination.Length)
        {
            int read = _stream.Read(destination[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// True when the region holds only 0x00 and 0xFF bytes (zero or erased-flash padding). At most
    /// <see cref="PaddingScanLimit"/> bytes are inspected.
    /// </summary>
    public bool IsPadding(long offset, long count)
    {
        if (offset < 0 || count <= 0 || offset >= Length)
        {
            return true;
        }

        long end = Math.Min(Length, offset + Math.Min(count, PaddingScanLimit));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        try
        {
            long position = offset;
            while (position < end)
            {
                int read = ReadDirect(position, buffer.AsSpan(0, (int)Math.Min(ScanBufferSize, end - position)));
                if (read <= 0)
                {
                    break;
                }

                if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0x00, (byte)0xFF) >= 0)
                {
                    return false;
                }

                position += read;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A pooled buffer for sequential scans; return it with <see cref="ReturnScanBuffer"/>.</summary>
    public static byte[] RentScanBuffer() => ArrayPool<byte>.Shared.Rent(ScanBufferSize);

    public static void ReturnScanBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);

    public void Dispose()
    {
        if (_window is not null)
        {
            ArrayPool<byte>.Shared.Return(_window);
            _window = null;
        }
    }
}
