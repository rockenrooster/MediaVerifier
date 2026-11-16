using System.Buffers.Binary;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// PNG chunk walker (subplan 07, task C-05): follows chunk lengths from the signature to IEND without
/// reading chunk data. CRCs are left to the decoder at Standard depth.
/// </summary>
internal static class PngWalker
{
    private const int ChunkOverhead = 12;

    /// <summary>Returns the declared end: the end of IEND, or of the chunk that runs past the end of the file.</summary>
    public static long? Walk(StructureSource source, List<StructureIssue> issues)
    {
        long length = source.Length;
        long position = 8;
        Span<byte> header = stackalloc byte[8];
        while (position < length)
        {
            int read = source.Read(position, header);
            if (read < 8)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"chunk header at offset {position} is cut off; IEND chunk missing", position, null, null));
                return null;
            }

            ReadOnlySpan<byte> typeBytes = header.Slice(4, 4);
            if (!IsChunkType(typeBytes))
            {
                // A damaged length sent the walk into chunk data; the decoder reports what is wrong there.
                return null;
            }

            uint dataLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = StructureBytes.Ascii(typeBytes);
            long end = position + ChunkOverhead + dataLength;
            bool isEnd = type == "IEND";
            if (end > length)
            {
                long missing = end - length;
                long totalMissing = missing + (isEnd ? 0 : ChunkOverhead);
                issues.Add(isEnd
                    ? StructureBytes.Issue(StructureIssueKind.Truncated, $"IEND chunk ends {missing} bytes past the end of the file", position, totalMissing, type)
                    : StructureBytes.Issue(StructureIssueKind.Truncated, $"{type} chunk ends {missing} bytes past the end of the file; IEND chunk missing", position, totalMissing, type));
                return end;
            }

            position = end;
            if (isEnd)
            {
                if (end < length && !source.IsPadding(end, length - end))
                {
                    issues.Add(StructureBytes.Issue(StructureIssueKind.TrailingData, $"{length - end} bytes after the IEND chunk", end));
                }

                return end;
            }
        }

        issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"IEND chunk not found", length, ChunkOverhead, "IEND"));
        return null;
    }

    private static bool IsChunkType(ReadOnlySpan<byte> type)
    {
        foreach (byte b in type)
        {
            if (!((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z')))
            {
                return false;
            }
        }

        return true;
    }
}
