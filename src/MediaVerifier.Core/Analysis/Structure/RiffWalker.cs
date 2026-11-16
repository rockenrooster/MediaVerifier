using System.Buffers.Binary;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// RIFF checks for WAV, RF64, AVI and WebP (subplan 07, task C-02): the RIFF size + 8 and the
/// <c>data</c>/<c>movi</c> extent against the file length, sizes never finalised, OpenDML AVIX chunks and
/// what follows the last RIFF chunk.
/// </summary>
internal static class RiffWalker
{
    private const int MaxChunks = 100_000;
    private const int MaxRiffChunks = 10_000;

    /// <summary>Returns the declared end (the end of the last RIFF chunk), or null when the size was never written.</summary>
    public static long? Walk(StructureSource source, SniffResult sniff, List<StructureIssue> issues, List<PaddingRegion> padding)
    {
        long length = source.Length;
        Span<byte> head = stackalloc byte[12];
        if (!source.TryRead(0, head))
        {
            return null;
        }

        bool rf64 = !head.StartsWith("RIFF"u8);
        uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4, 4));

        // RF64 / BW64: the real sizes live in the ds64 chunk that comes first.
        long? ds64Riff = null;
        long? ds64Data = null;
        Span<byte> ds64 = stackalloc byte[24];
        if (rf64 && source.TryRead(12, ds64) && ds64.StartsWith("ds64"u8) && BinaryPrimitives.ReadUInt32LittleEndian(ds64.Slice(4, 4)) >= 16)
        {
            ds64Riff = StructureBytes.Saturate(BinaryPrimitives.ReadUInt64LittleEndian(ds64.Slice(8, 8)));
            ds64Data = StructureBytes.Saturate(BinaryPrimitives.ReadUInt64LittleEndian(ds64.Slice(16, 8)));
        }

        long riffSize = rf64 && size32 == 0xFFFFFFFF && ds64Riff is long fromDs64 ? fromDs64 : size32;
        bool riffFinal = riffSize != 0 && (size32 != 0xFFFFFFFF || (rf64 && ds64Riff > 0));
        long riffEnd = StructureBytes.SaturatingAdd(8, riffSize);

        // Walk the chunks inside the RIFF chunk for the payload extent and JUNK padding.
        string? payloadName = sniff.Format switch
        {
            ContainerFormat.Avi => "movi",
            ContainerFormat.Wav or ContainerFormat.Rf64 => "data",
            _ => null,
        };
        bool payloadFound = false;
        bool payloadFinal = true;
        long payloadOffset = 0;
        long payloadEnd = 0;
        long walkEnd = riffFinal ? Math.Min(riffEnd, length) : length;
        long position = 12;
        Span<byte> chunk = stackalloc byte[12];
        for (int i = 0; i < MaxChunks && position + 8 <= walkEnd; i++)
        {
            if (!source.TryRead(position, chunk[..8]) || !StructureBytes.IsPrintableFourCc(chunk))
            {
                break;
            }

            ReadOnlySpan<byte> id = chunk[..4];
            uint chunkSize32 = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
            long chunkSize = chunkSize32;
            long dataStart = position + 8;
            bool isData = payloadName == "data" && id.SequenceEqual("data"u8);
            bool isMovi = payloadName == "movi" && id.SequenceEqual("LIST"u8)
                && source.TryRead(dataStart, chunk.Slice(8, 4)) && chunk.Slice(8, 4).SequenceEqual("movi"u8);
            if (isData && chunkSize32 == 0xFFFFFFFF && ds64Data is long dataSize && dataSize > 0)
            {
                chunkSize = dataSize;
            }

            long end = StructureBytes.SaturatingAdd(dataStart, chunkSize);
            if ((isData || isMovi) && !payloadFound)
            {
                payloadFound = true;
                payloadOffset = position;
                payloadEnd = end;
                payloadFinal = chunkSize32 != 0xFFFFFFFF || (isData && ds64Data is > 0);
            }

            if (id.SequenceEqual("JUNK"u8) || id.SequenceEqual("junk"u8) || id.SequenceEqual("PAD "u8) || id.SequenceEqual("FLLR"u8))
            {
                StructureBytes.AddPadding(padding, dataStart, Math.Min(end, length));
            }

            if (end >= walkEnd)
            {
                break;
            }

            position = end + (chunkSize & 1);
        }

        if (!riffFinal)
        {
            string value = size32 == 0 || (rf64 && ds64Riff == 0) ? "0" : "0xFFFFFFFF";
            issues.Add(StructureBytes.Issue(StructureIssueKind.IndexNotFinalised, $"RIFF size is {value}; the writer never finalised the file", 0, null, rf64 ? "ds64" : "RIFF"));
        }
        else if (payloadFound && !payloadFinal)
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.IndexNotFinalised, $"{payloadName} chunk size is 0xFFFFFFFF; the writer never finalised the file", payloadOffset, null, payloadName));
        }

        // One byte short is the final pad byte that many writers count in the RIFF size but never write.
        if (riffFinal && riffEnd > length + 1)
        {
            long missing = riffEnd - length;
            issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"RIFF chunk ends {missing} bytes past the end of the file", 0, missing, "RIFF"));
            return riffEnd;
        }

        if (payloadFound && payloadFinal && payloadEnd > length)
        {
            long missing = payloadEnd - length;
            issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"{payloadName} chunk ends {missing} bytes past the end of the file", payloadOffset, missing, payloadName));
        }

        if (!riffFinal)
        {
            return null;
        }

        long declaredEnd = riffEnd;
        if (sniff.Format == ContainerFormat.Avi)
        {
            declaredEnd = WalkAvix(source, StructureBytes.SaturatingAdd(riffEnd, riffSize & 1), riffEnd, issues);
        }

        if (declaredEnd < length)
        {
            CheckTrailing(source, declaredEnd, issues);
        }

        return declaredEnd;
    }

    /// <summary>OpenDML files above 1 GiB continue in further <c>RIFF AVIX</c> chunks.</summary>
    private static long WalkAvix(StructureSource source, long position, long declaredEnd, List<StructureIssue> issues)
    {
        long length = source.Length;
        Span<byte> head = stackalloc byte[12];
        for (int i = 0; i < MaxRiffChunks && position < length; i++)
        {
            if (!source.TryRead(position, head) || !head.StartsWith("RIFF"u8) || !head.Slice(8, 4).SequenceEqual("AVIX"u8))
            {
                break;
            }

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4, 4));
            long end = StructureBytes.SaturatingAdd(position + 8, size);
            declaredEnd = end;
            if (end > length)
            {
                long missing = end - length;
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"AVIX chunk ends {missing} bytes past the end of the file", position, missing, "AVIX"));
                break;
            }

            position = end + (size & 1);
        }

        return declaredEnd;
    }

    /// <summary>
    /// Bytes after the last RIFF chunk: a pad byte, zero padding, ID3v1/ID3v2/APE tags are known; anything
    /// else is trailing data.
    /// </summary>
    private static void CheckTrailing(StructureSource source, long position, List<StructureIssue> issues)
    {
        long length = source.Length;
        long count = length - position;
        if (count <= 1)
        {
            return;
        }

        Span<byte> start = stackalloc byte[8];
        int read = source.Read(position, start);
        ReadOnlySpan<byte> first = start[..read];
        Span<byte> footer = stackalloc byte[8];
        bool apeAtEnd = (source.TryRead(length - 32, footer) && footer.SequenceEqual("APETAGEX"u8))
            || (source.TryRead(length - 160, footer) && footer.SequenceEqual("APETAGEX"u8));
        bool known = first.StartsWith("ID3"u8) || first.StartsWith("APETAGEX"u8) || (count == 128 && first.StartsWith("TAG"u8)) || apeAtEnd;
        if (!known && !source.IsPadding(position, count))
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.TrailingData, $"{count} bytes after the end of the RIFF chunk", position));
        }
    }
}
