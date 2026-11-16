using System.Buffers.Binary;
using System.Globalization;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// MP3 header check (subplan 07, task C-13): finds the first frame after the ID3v2 tags, reads a
/// Xing/Info or VBRI header, subtracts trailing ID3v1, APE, Lyrics3 and appended ID3v2 tags, and compares
/// the audio bytes with what the header declares.
/// </summary>
internal static class Mp3Reader
{
    /// <summary>How far past the expected start the first frame is searched for (junk or padding after a tag).</summary>
    public const int SyncSearchWindow = 64 * 1024;

    /// <summary>The allowed difference from the declared size, as a fraction (subplan 07: 1 %).</summary>
    private const double Tolerance = 0.01;

    private const int MaxTrailingTags = 8;

    /// <summary>
    /// Reads the stream layout from <paramref name="start"/> (the end of the ID3v2 tags); null when no
    /// frame confirmed by <paramref name="framesRequired"/> consecutive compatible frames is found.
    /// </summary>
    public static Mp3Info? Read(StructureSource source, long start, int framesRequired, List<StructureIssue> issues, out long? declaredEnd)
    {
        declaredEnd = null;
        long audioEnd = FindAudioEnd(source, start);
        if (!TryFindFirstFrame(source, start, audioEnd, framesRequired, out long frameOffset, out MpegAudioFrame frame))
        {
            return null;
        }

        string? tag = null;
        long? frames = null;
        long? bytes = null;
        if (frame.Layer == 3)
        {
            int sideInfo = frame.Version == 1 ? (frame.Channels == 1 ? 17 : 32) : (frame.Channels == 1 ? 9 : 17);
            Span<byte> xing = stackalloc byte[16];
            Span<byte> vbri = stackalloc byte[18];
            if (source.TryRead(frameOffset + 4 + sideInfo, xing) && (xing.StartsWith("Xing"u8) || xing.StartsWith("Info"u8)))
            {
                tag = xing.StartsWith("Xing"u8) ? "Xing" : "Info";
                uint flags = BinaryPrimitives.ReadUInt32BigEndian(xing.Slice(4, 4));
                int field = 8;
                if ((flags & 1) != 0)
                {
                    frames = NonZero(BinaryPrimitives.ReadUInt32BigEndian(xing.Slice(field, 4)));
                    field += 4;
                }

                if ((flags & 2) != 0)
                {
                    bytes = NonZero(BinaryPrimitives.ReadUInt32BigEndian(xing.Slice(field, 4)));
                }
            }
            else if (source.TryRead(frameOffset + 36, vbri) && vbri.StartsWith("VBRI"u8))
            {
                tag = "VBRI";
                bytes = NonZero(BinaryPrimitives.ReadUInt32BigEndian(vbri.Slice(10, 4)));
                frames = NonZero(BinaryPrimitives.ReadUInt32BigEndian(vbri.Slice(14, 4)));
            }
        }

        long audioBytes = Math.Max(0, audioEnd - frameOffset);
        if (tag is not null)
        {
            Compare(frame, frameOffset, audioBytes, tag, frames, bytes, issues);
        }

        if (bytes is long declared)
        {
            declaredEnd = frameOffset + declared;
        }

        return new Mp3Info(frameOffset, audioBytes, frames, bytes, tag, frame.Bitrate, frame.SampleRate, frame.Channels);
    }

    /// <summary>
    /// Audio more than 1 % shorter than declared is truncation, more than 1 % longer a wrong index. The
    /// tolerance is at least one frame, so that very short files and writers that count the header frame
    /// differently are not flagged. A CBR "Info" header without a byte count is checked through its frame
    /// count; with both, the two must agree.
    /// </summary>
    private static void Compare(MpegAudioFrame frame, long frameOffset, long audioBytes, string tag, long? frames, long? bytes, List<StructureIssue> issues)
    {
        bool cbr = tag == "Info";
        long? expected = bytes;
        if (expected is null && cbr && frames is long count)
        {
            expected = frame.FrameLength + (long)Math.Round(count * frame.AverageFrameLength);
        }

        if (expected is not long declared)
        {
            return;
        }

        long tolerance = Math.Max((long)Math.Ceiling(declared * Tolerance), frame.FrameLength);
        long difference = audioBytes - declared;
        string what = bytes is null ? "frame count implies" : "declares";
        if (difference < -tolerance)
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"audio data is {-difference} bytes shorter than the {tag} header {what} ({declared} bytes)", frameOffset, -difference, tag));
            return;
        }

        if (difference > tolerance)
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.IndexMissingOrWrong, $"audio data is {difference} bytes longer than the {tag} header {what} ({declared} bytes)", frameOffset, null, tag));
            return;
        }

        if (cbr && bytes is long declaredBytes && frames is long declaredFrames)
        {
            long implied = (long)Math.Round((declaredBytes - frame.FrameLength) / frame.AverageFrameLength);
            long fromFrames = frame.FrameLength + (long)Math.Round(declaredFrames * frame.AverageFrameLength);
            if (Math.Abs(fromFrames - declaredBytes) > tolerance)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.IndexMissingOrWrong, $"Info header declares {declaredFrames} frames but its byte count implies about {implied}", frameOffset, null, tag));
            }
        }
    }

    /// <summary>
    /// The first frame at <paramref name="start"/>, or the first one after it within
    /// <see cref="SyncSearchWindow"/>, that <paramref name="framesRequired"/> consecutive compatible frames confirm.
    /// </summary>
    private static bool TryFindFirstFrame(StructureSource source, long start, long end, int framesRequired, out long offset, out MpegAudioFrame frame)
    {
        offset = start;
        if (IsConfirmed(source, start, end, framesRequired, out frame))
        {
            return true;
        }

        byte[] buffer = StructureSource.RentScanBuffer();
        try
        {
            int count = source.ReadDirect(start, buffer.AsSpan(0, (int)Math.Min(buffer.Length, Math.Max(0, end - start))));
            ReadOnlySpan<byte> span = buffer.AsSpan(0, Math.Min(count, SyncSearchWindow));
            int i = 1;
            while (i < span.Length)
            {
                int found = span[i..].IndexOf((byte)0xFF);
                if (found < 0)
                {
                    break;
                }

                i += found;
                if (IsConfirmed(source, start + i, end, framesRequired, out frame))
                {
                    offset = start + i;
                    return true;
                }

                i++;
            }

            return false;
        }
        finally
        {
            StructureSource.ReturnScanBuffer(buffer);
        }
    }

    private static bool IsConfirmed(StructureSource source, long offset, long end, int framesRequired, out MpegAudioFrame first)
    {
        first = default;
        Span<byte> header = stackalloc byte[4];
        long position = offset;
        for (int k = 0; k < framesRequired; k++)
        {
            if (k > 0 && position == end)
            {
                return true;
            }

            if (position + 4 > end || !source.TryRead(position, header)
                || !MpegAudioFrame.TryParse(BinaryPrimitives.ReadUInt32BigEndian(header), out MpegAudioFrame frame))
            {
                return false;
            }

            if (k == 0)
            {
                first = frame;
            }
            else if (!first.IsCompatible(frame))
            {
                return false;
            }

            position += frame.FrameLength;
        }

        return true;
    }

    /// <summary>The end of the audio: the file end less trailing ID3v1 (and "TAG+"), APE, Lyrics3 v2 and appended ID3v2 tags.</summary>
    internal static long FindAudioEnd(StructureSource source, long minimum)
    {
        long end = source.Length;
        Span<byte> b = stackalloc byte[32];
        for (int i = 0; i < MaxTrailingTags; i++)
        {
            bool changed = false;
            if (end - 128 >= minimum && source.TryRead(end - 128, b[..3]) && b[..3].SequenceEqual("TAG"u8))
            {
                end -= 128;
                changed = true;
                if (end - 227 >= minimum && source.TryRead(end - 227, b[..4]) && b[..4].SequenceEqual("TAG+"u8))
                {
                    end -= 227;
                }
            }

            if (end - 32 >= minimum && source.TryRead(end - 32, b) && b.StartsWith("APETAGEX"u8))
            {
                long size = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12, 4));
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(20, 4));
                long total = size + ((flags & 0x80000000) != 0 ? 32 : 0);
                if (size >= 32 && end - total >= minimum)
                {
                    end -= total;
                    changed = true;
                }
            }

            if (end - 15 >= minimum && source.TryRead(end - 15, b[..15]) && b.Slice(6, 9).SequenceEqual("LYRICS200"u8)
                && long.TryParse(b[..6], NumberStyles.None, CultureInfo.InvariantCulture, out long lyrics)
                && end - 15 - lyrics >= minimum && source.TryRead(end - 15 - lyrics, b[..11]) && b[..11].SequenceEqual("LYRICSBEGIN"u8))
            {
                end -= 15 + lyrics;
                changed = true;
            }

            if (end - 10 >= minimum && source.TryRead(end - 10, b[..10]) && b[..3].SequenceEqual("3DI"u8)
                && ((b[6] | b[7] | b[8] | b[9]) & 0x80) == 0)
            {
                long total = 20 + (((long)b[6] << 21) | ((long)b[7] << 14) | ((long)b[8] << 7) | b[9]);
                if (end - total >= minimum && source.TryRead(end - total, b[..3]) && b[..3].SequenceEqual("ID3"u8))
                {
                    end -= total;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return end;
    }

    private static long? NonZero(uint value) => value == 0 ? null : value;
}
