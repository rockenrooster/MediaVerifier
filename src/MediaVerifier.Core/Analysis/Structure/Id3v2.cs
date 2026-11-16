using System.Buffers.Binary;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>ID3v2 tag headers: their length, and where their padding is.</summary>
internal static class Id3v2
{
    public const int HeaderLength = 10;

    private const int MaxFrames = 10_000;

    /// <summary>
    /// The total length of an ID3v2 tag starting at <paramref name="header"/>: 10 + synchsafe size, + 10 when
    /// a v2.4 footer is flagged. Validated like FFmpeg's <c>ff_id3v2_match</c>: version and revision are not
    /// 0xFF and every size byte is below 0x80.
    /// </summary>
    public static bool TryGetLength(ReadOnlySpan<byte> header, out long length)
    {
        length = 0;
        if (header.Length < HeaderLength || header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3'
            || header[3] == 0xFF || header[4] == 0xFF || ((header[6] | header[7] | header[8] | header[9]) & 0x80) != 0)
        {
            return false;
        }

        length = HeaderLength + Synchsafe(header.Slice(6, 4));
        if (header[3] >= 4 && (header[5] & 0x10) != 0)
        {
            length += HeaderLength;
        }

        return true;
    }

    /// <summary>Records the padding of each consecutive ID3v2 tag in front of <paramref name="end"/>.</summary>
    public static void CollectPadding(StructureSource source, long end, List<PaddingRegion> padding)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        long position = 0;
        for (int i = 0; i < 64 && position < end; i++)
        {
            if (!source.TryRead(position, header) || !TryGetLength(header, out long length))
            {
                return;
            }

            if (FindPadding(source, position, header) is PaddingRegion region)
            {
                StructureBytes.AddPadding(padding, region.Offset, region.Offset + region.Length);
            }

            position += length;
        }
    }

    /// <summary>
    /// The padding after the last frame of the tag at <paramref name="tagStart"/>: the first frame header
    /// that starts with a zero byte begins it, and it runs to the end of the tag (before any footer). Null
    /// when the tag has none or cannot be walked (v2.2/v2.3 whole-tag unsynchronisation, a bad frame id).
    /// </summary>
    private static PaddingRegion? FindPadding(StructureSource source, long tagStart, ReadOnlySpan<byte> header)
    {
        int version = header[3];
        byte flags = header[5];
        if (version < 2 || version > 4 || (version < 4 && (flags & 0x80) != 0))
        {
            return null;
        }

        long tagEnd = Math.Min(source.Length, tagStart + HeaderLength + Synchsafe(header.Slice(6, 4)));
        long position = tagStart + HeaderLength;
        Span<byte> frame = stackalloc byte[10];
        if ((flags & 0x40) != 0 && version >= 3)
        {
            if (!source.TryRead(position, frame[..4]))
            {
                return null;
            }

            // v2.3: the size excludes its own four bytes; v2.4: synchsafe and includes them.
            position += version == 3 ? 4 + (long)BinaryPrimitives.ReadUInt32BigEndian(frame) : Synchsafe(frame[..4]);
        }

        int headerSize = version == 2 ? 6 : 10;
        int idLength = version == 2 ? 3 : 4;
        for (int i = 0; i < MaxFrames && position + headerSize <= tagEnd; i++)
        {
            if (!source.TryRead(position, frame[..headerSize]))
            {
                return null;
            }

            if (frame[0] == 0)
            {
                return new PaddingRegion(position, tagEnd - position);
            }

            for (int c = 0; c < idLength; c++)
            {
                byte b = frame[c];
                if (!((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'0' && b <= (byte)'9')))
                {
                    return null;
                }
            }

            long size = version switch
            {
                2 => (frame[3] << 16) | (frame[4] << 8) | frame[5],

                // Some v2.4 writers store plain sizes; a byte with the top bit set cannot be synchsafe.
                4 when ((frame[4] | frame[5] | frame[6] | frame[7]) & 0x80) == 0 => Synchsafe(frame.Slice(4, 4)),
                _ => BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(4, 4)),
            };

            position += headerSize + size;
        }

        return null;
    }

    private static long Synchsafe(ReadOnlySpan<byte> b) =>
        ((long)(b[0] & 0x7F) << 21) | ((long)(b[1] & 0x7F) << 14) | ((long)(b[2] & 0x7F) << 7) | (long)(b[3] & 0x7F);
}
