using System.Buffers.Binary;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// JPEG marker walker (subplan 07, task C-04): segments with lengths until SOS, then a scan of the
/// entropy-coded data for the next marker that is not a stuffed <c>FF 00</c>, a fill byte or RST0–7,
/// across all scans, until EOI. Records SOF dimensions, quantisation tables, scan count and what follows
/// EOI.
/// </summary>
internal static class JpegWalker
{
    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;
    private const byte Dqt = 0xDB;

    public static JpegInfo Read(StructureSource source, List<StructureIssue> issues, out long? declaredEnd)
    {
        declaredEnd = null;
        long length = source.Length;
        int width = 0;
        int height = 0;
        bool sofSeen = false;
        bool progressive = false;
        int scans = 0;
        int[]?[] tables = new int[]?[4];
        bool hasEoi = false;
        long eoiOffset = -1;
        bool truncationReported = false;

        Span<byte> two = stackalloc byte[2];
        Span<byte> sof = stackalloc byte[6];
        long position = 2;
        while (position < length)
        {
            // A marker is FF, any number of FF fill bytes, then the code. Anything else here is stray data:
            // resynchronise on the next marker the way decoders do.
            if (source.Read(position, two[..1]) < 1)
            {
                break;
            }

            if (two[0] != 0xFF)
            {
                long next = FindMarker(source, position);
                if (next < 0)
                {
                    break;
                }

                position = next;
                continue;
            }

            long codeOffset = SkipFill(source, position + 1);
            if (codeOffset < 0 || source.Read(codeOffset, two[..1]) < 1)
            {
                break;
            }

            byte code = two[0];
            long markerOffset = codeOffset - 1;
            if (code == Eoi)
            {
                hasEoi = true;
                eoiOffset = markerOffset;
                break;
            }

            if (code == Soi || code == 0x01 || code == 0x00 || (code >= 0xD0 && code <= 0xD7))
            {
                position = markerOffset + 2;
                continue;
            }

            string name = MarkerName(code);
            if (!source.TryRead(markerOffset + 2, two))
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"{name} segment header is cut off", markerOffset, null, name));
                truncationReported = true;
                break;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(two);
            if (segmentLength < 2)
            {
                position = markerOffset + 2;
                continue;
            }

            long dataStart = markerOffset + 4;
            long segmentEnd = markerOffset + 2 + segmentLength;
            if (segmentEnd > length)
            {
                long missing = segmentEnd - length;
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"{name} segment ends {missing} bytes past the end of the file", markerOffset, missing, name));
                truncationReported = true;
                break;
            }

            if (IsSof(code) && !sofSeen && segmentLength >= 8 && source.TryRead(dataStart, sof))
            {
                sofSeen = true;
                height = BinaryPrimitives.ReadUInt16BigEndian(sof.Slice(1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(sof.Slice(3, 2));
                progressive = code is 0xC2 or 0xC6 or 0xCA or 0xCE;
            }
            else if (code == Dqt)
            {
                ParseDqt(source.ReadBytes(dataStart, segmentLength - 2), tables);
            }

            if (code == Sos)
            {
                scans++;
                long next = FindMarker(source, segmentEnd);
                if (next < 0)
                {
                    break;
                }

                position = next;
                continue;
            }

            position = segmentEnd;
        }

        long bytesAfterEoi = 0;
        if (hasEoi)
        {
            declaredEnd = eoiOffset + 2;
            bytesAfterEoi = length - declaredEnd.Value;
            if (bytesAfterEoi > 0 && !IsKnownTrailer(source, declaredEnd.Value, bytesAfterEoi))
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.TrailingData, $"{bytesAfterEoi} bytes after the EOI marker are not a known trailer", declaredEnd.Value));
            }
        }
        else if (!truncationReported)
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"EOI marker not found; the image data ends without it", length, null, "EOI"));
        }

        return new JpegInfo(width, height, progressive, scans, ToList(tables), hasEoi, eoiOffset, bytesAfterEoi);
    }

    /// <summary>
    /// The offset of the next marker at or after <paramref name="start"/> that is not a stuffed <c>FF 00</c>,
    /// a fill byte or RST0–7; -1 when the file ends first. Vectorised search for 0xFF.
    /// </summary>
    internal static long FindMarker(StructureSource source, long start)
    {
        long length = source.Length;
        byte[] buffer = StructureSource.RentScanBuffer();
        try
        {
            long position = start;
            while (position < length)
            {
                int count = source.ReadDirect(position, buffer);
                if (count <= 0)
                {
                    return -1;
                }

                ReadOnlySpan<byte> span = buffer.AsSpan(0, count);
                int i = 0;
                while (i < count)
                {
                    int found = span[i..].IndexOf((byte)0xFF);
                    if (found < 0)
                    {
                        i = count;
                        break;
                    }

                    i += found;
                    if (i + 1 >= count)
                    {
                        break;
                    }

                    byte next = span[i + 1];
                    if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
                    {
                        i += 2;
                    }
                    else if (next == 0xFF)
                    {
                        i++;
                    }
                    else
                    {
                        return position + i;
                    }
                }

                if (i < count)
                {
                    // 0xFF is the last byte read: re-read from it so that its code byte comes with it.
                    if (position + count >= length)
                    {
                        return -1;
                    }

                    position += i;
                }
                else
                {
                    position += count;
                }
            }

            return -1;
        }
        finally
        {
            StructureSource.ReturnScanBuffer(buffer);
        }
    }

    /// <summary>The first byte at or after <paramref name="position"/> that is not a 0xFF fill byte; -1 at the end of the file.</summary>
    private static long SkipFill(StructureSource source, long position)
    {
        Span<byte> chunk = stackalloc byte[256];
        while (true)
        {
            int read = source.Read(position, chunk);
            if (read <= 0)
            {
                return -1;
            }

            int other = chunk[..read].IndexOfAnyExcept((byte)0xFF);
            if (other >= 0)
            {
                return position + other;
            }

            position += read;
        }
    }

    /// <summary>
    /// What may follow EOI: another JPEG (multi-picture, gain maps), an MP4 with <c>ftyp</c> at +4 (motion
    /// photos), Samsung's motion-photo marker or SEF trailer (the file ends with "SEFT"), or 0x00/0xFF padding.
    /// </summary>
    private static bool IsKnownTrailer(StructureSource source, long offset, long count)
    {
        Span<byte> head = stackalloc byte[16];
        ReadOnlySpan<byte> start = head[..source.Read(offset, head)];
        if (start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]) || (start.Length >= 8 && start.Slice(4, 4).SequenceEqual("ftyp"u8))
            || start.StartsWith("MotionPhoto_Data"u8))
        {
            return true;
        }

        Span<byte> tail = stackalloc byte[4];
        if (count >= 4 && source.TryRead(source.Length - 4, tail) && tail.SequenceEqual("SEFT"u8))
        {
            return true;
        }

        return source.IsPadding(offset, count);
    }

    private static void ParseDqt(ReadOnlySpan<byte> data, int[]?[] tables)
    {
        int i = 0;
        while (i < data.Length)
        {
            int precision = data[i] >> 4;
            int id = data[i] & 0x0F;
            i++;
            int size = precision == 0 ? 64 : 128;
            if (id > 3 || precision > 1 || i + size > data.Length)
            {
                return;
            }

            if (tables[id] is null)
            {
                int[] values = new int[64];
                for (int k = 0; k < 64; k++)
                {
                    values[k] = precision == 0 ? data[i + k] : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + (2 * k), 2));
                }

                tables[id] = values;
            }

            i += size;
        }
    }

    private static IReadOnlyList<int[]> ToList(int[]?[] tables)
    {
        int count = 0;
        for (int id = 0; id < tables.Length; id++)
        {
            if (tables[id] is not null)
            {
                count = id + 1;
            }
        }

        int[][] list = new int[count][];
        for (int id = 0; id < count; id++)
        {
            list[id] = tables[id] ?? [];
        }

        return list;
    }

    /// <summary>SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC).</summary>
    private static bool IsSof(byte code) => code >= 0xC0 && code <= 0xCF && code is not (0xC4 or 0xC8 or 0xCC);

    private static string MarkerName(byte code) => code switch
    {
        0xC4 => "DHT",
        0xC8 => "JPG",
        0xCC => "DAC",
        >= 0xC0 and <= 0xCF => "SOF" + (code - 0xC0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Sos => "SOS",
        Dqt => "DQT",
        0xDC => "DNL",
        0xDD => "DRI",
        0xDE => "DHP",
        0xDF => "EXP",
        >= 0xE0 and <= 0xEF => "APP" + (code - 0xE0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        0xFE => "COM",
        _ => "FF" + code.ToString("X2", System.Globalization.CultureInfo.InvariantCulture),
    };
}
