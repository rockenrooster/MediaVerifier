using System.Buffers.Binary;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// Walks the top-level boxes of an ISO-BMFF or QuickTime file (subplan 07, task C-02): 32-bit sizes,
/// 1 = a 64-bit size follows, 0 = to the end of the file. <c>mdat</c> before <c>moov</c> is normal.
/// </summary>
internal static class IsoBmffWalker
{
    /// <summary>Top-level boxes walked at most (fragmented recordings have thousands; this is far beyond them).</summary>
    private const int MaxBoxes = 1_000_000;

    /// <summary>Returns the declared end: the end of the last box (beyond the file when one is cut off).</summary>
    public static long? Walk(StructureSource source, SniffResult sniff, List<StructureIssue> issues, List<PaddingRegion> padding)
    {
        long length = source.Length;
        long position = 0;
        long? declaredEnd = null;
        bool hasMoov = false;
        bool hasMoof = false;
        bool hasMeta = false;
        int boxes = 0;
        Span<byte> header = stackalloc byte[16];

        while (position < length)
        {
            int read = source.Read(position, header);
            if (read < 8 || !StructureBytes.IsPrintableFourCc(header.Slice(4, 4)))
            {
                AddTrailing(source, position, issues);
                break;
            }

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
            ReadOnlySpan<byte> typeBytes = header.Slice(4, 4);
            bool known = IsKnownTopLevel(typeBytes);
            string type = StructureBytes.Ascii(typeBytes);
            int headerSize = 8;
            long boxSize;
            if (size32 == 1)
            {
                if (read < 16)
                {
                    if (known)
                    {
                        issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"{type} box header is cut off", position, null, type));
                    }
                    else
                    {
                        AddTrailing(source, position, issues);
                    }

                    break;
                }

                ulong large = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                if (large < 16)
                {
                    AddTrailing(source, position, issues);
                    break;
                }

                headerSize = 16;
                boxSize = StructureBytes.Saturate(large);
            }
            else if (size32 == 0)
            {
                if (!known)
                {
                    AddTrailing(source, position, issues);
                    break;
                }

                boxSize = length - position;
            }
            else if (size32 < 8)
            {
                AddTrailing(source, position, issues);
                break;
            }
            else
            {
                boxSize = size32;
            }

            long end = StructureBytes.SaturatingAdd(position, boxSize);

            // Junk after the last box can look like a header; only a known box type may run past the end.
            if (end > length && !known)
            {
                AddTrailing(source, position, issues);
                break;
            }

            switch (type)
            {
                case "moov":
                    hasMoov = true;
                    break;
                case "moof":
                    hasMoof = true;
                    break;
                case "meta":
                    hasMeta = true;
                    break;
                case "free":
                case "skip":
                    StructureBytes.AddPadding(padding, position + headerSize, Math.Min(end, length));
                    break;
            }

            declaredEnd = end;
            if (end > length)
            {
                long missing = end - length;
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"{type} box ends {missing} bytes past the end of the file", position, missing, type));
                break;
            }

            position = end;
            if (++boxes >= MaxBoxes)
            {
                // Far beyond any real file: give up without a verdict on the index.
                return declaredEnd;
            }
        }

        if (!hasMoov && !hasMeta)
        {
            if (FormatKinds.KindHint(ContainerFormat.IsoBmff, sniff.Brand) == MediaKind.Photo)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"meta box not found", null, null, "meta"));
            }
            else if (hasMoof)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"bare media segment: moof boxes without a moov box", null, null, "moov"));
            }
            else
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"moov box not found", null, null, "moov"));
            }
        }

        return declaredEnd;
    }

    /// <summary>Bytes from <paramref name="position"/> to the end that are not a box; zero or 0xFF padding is not reported.</summary>
    private static void AddTrailing(StructureSource source, long position, List<StructureIssue> issues)
    {
        long count = source.Length - position;
        if (count > 0 && !source.IsPadding(position, count))
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.TrailingData, $"{count} bytes after the last box are not a box", position));
        }
    }

    private static bool IsKnownTopLevel(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("ftyp"u8) || type.SequenceEqual("styp"u8) || type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8)
        || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8) || type.SequenceEqual("wide"u8) || type.SequenceEqual("moof"u8)
        || type.SequenceEqual("mfra"u8) || type.SequenceEqual("meta"u8) || type.SequenceEqual("uuid"u8) || type.SequenceEqual("sidx"u8)
        || type.SequenceEqual("ssix"u8) || type.SequenceEqual("prft"u8) || type.SequenceEqual("emsg"u8) || type.SequenceEqual("pdin"u8)
        || type.SequenceEqual("pnot"u8) || type.SequenceEqual("PICT"u8) || type.SequenceEqual("udta"u8);
}
