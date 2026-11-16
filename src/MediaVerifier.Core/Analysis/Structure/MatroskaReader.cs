using System.Globalization;
using System.Numerics;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// Matroska and WebM checks (subplan 07, tasks C-05 and C-13): validates the EBML header, reads the
/// Segment size, then walks the Segment's top-level children by size (never reading Cluster payloads) for
/// Tracks and Cues, using SeekHead entries so that a finalised file is settled before its first Cluster.
/// </summary>
internal static class MatroskaReader
{
    private const uint EbmlId = 0x1A45DFA3;
    private const uint DocTypeId = 0x4282;
    private const uint SegmentId = 0x18538067;
    private const uint SeekHeadId = 0x114D9B74;
    private const uint SeekId = 0x4DBB;
    private const uint SeekIdId = 0x53AB;
    private const uint SeekPositionId = 0x53AC;
    private const uint TracksId = 0x1654AE6B;
    private const uint TrackEntryId = 0xAE;
    private const uint TrackTypeId = 0x83;
    private const uint CuesId = 0x1C53BB6B;
    private const uint ClusterId = 0x1F43B675;
    private const uint VoidId = 0xEC;

    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxSeekHeadBytes = 256 * 1024;
    private const int MaxTracksBytes = 1024 * 1024;
    private const int MaxTopLevelElements = 100_000;
    private const int MaxSeekEntries = 4096;
    private const int MaxSeekHeads = 8;

    public static MatroskaInfo? Read(StructureSource source, List<StructureIssue> issues, List<PaddingRegion> padding, out long? declaredEnd)
    {
        declaredEnd = null;
        long length = source.Length;
        if (!TryReadElement(source, 0, out Element ebml) || ebml.Id != EbmlId || ebml.UnknownSize)
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"EBML header is invalid", 0, null, "EBML"));
            return null;
        }

        if (ebml.End > length)
        {
            long missing = ebml.End - length;
            issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"EBML header ends {missing} bytes past the end of the file", 0, missing, "EBML"));
            return null;
        }

        bool complete = ebml.Size <= MaxHeaderBytes;
        if (!TryParseHeader(source.ReadBytes(ebml.DataStart, (int)Math.Min(ebml.Size, MaxHeaderBytes)), complete, out string? docType))
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"EBML header is invalid", 0, null, "EBML"));
            return null;
        }

        // The Segment follows the header (Void or other level-0 elements may come first).
        long position = ebml.End;
        Element segment = default;
        bool found = false;
        for (int i = 0; i < 64 && position < length; i++)
        {
            if (!TryReadElement(source, position, out Element element))
            {
                break;
            }

            if (element.Id == SegmentId)
            {
                segment = element;
                found = true;
                break;
            }

            if (element.UnknownSize)
            {
                break;
            }

            position = element.End;
        }

        if (!found)
        {
            // A clean end right after the header is a cut file; anything else is not a Matroska Segment.
            if (position == length)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"the file ends before the Segment element", length, null, "Segment"));
            }
            else
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"Segment element not found", position, null, "Segment"));
            }

            return new MatroskaInfo(docType, null, false, false);
        }

        bool truncated = false;
        if (!segment.UnknownSize)
        {
            declaredEnd = segment.End;
            if (segment.End > length)
            {
                long missing = segment.End - length;
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"Segment ends {missing} bytes past the end of the file", segment.Offset, missing, "Segment"));
                truncated = true;
            }
        }

        Walker walker = new(source, segment.DataStart);
        long walkEnd = Math.Min(segment.End, length);
        bool walkComplete = false;
        position = segment.DataStart;
        for (int count = 0; ; count++)
        {
            if (position >= walkEnd)
            {
                walkComplete = true;
                break;
            }

            if (count >= MaxTopLevelElements || !TryReadElement(source, position, out Element element))
            {
                break;
            }

            if (element.UnknownSize)
            {
                // A live or unfinalised file: nothing after an unknown-size Cluster can be reached by size.
                walkComplete = element.Id == ClusterId;
                break;
            }

            if (element.End > length)
            {
                if (!truncated)
                {
                    long missing = element.End - length;
                    string name = ElementName(element.Id);
                    issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"{name} element ends {missing} bytes past the end of the file", element.Offset, missing, name));
                    truncated = true;
                }

                walker.Visit(element, padding);
                break;
            }

            walker.Visit(element, padding);
            if (element.Id == ClusterId && !walker.SeeksResolved)
            {
                // Before skipping Clusters, let the SeekHead settle Tracks and Cues where it can.
                walker.ResolveSeeks();
                if (walker.HasCues && walker.TracksSeen)
                {
                    walkComplete = true;
                    break;
                }
            }

            position = element.End;
        }

        walker.ResolveSeeks();
        if (walker.HasVideo && !walker.HasCues && walkComplete && !truncated)
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.IndexMissingOrWrong, $"Cues element not found; the file has a video track", segment.Offset, null, "Cues"));
        }

        if (!segment.UnknownSize && segment.End < length)
        {
            CheckTrailing(source, segment.End, issues);
        }

        return new MatroskaInfo(docType, segment.UnknownSize ? null : segment.Size, walker.HasCues, walker.HasVideo);
    }

    /// <summary>After a known-size Segment: another EBML document (chained segments) or padding is fine.</summary>
    private static void CheckTrailing(StructureSource source, long position, List<StructureIssue> issues)
    {
        long count = source.Length - position;
        Span<byte> next = stackalloc byte[4];
        if (source.TryRead(position, next) && next.SequenceEqual((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
        {
            return;
        }

        if (!source.IsPadding(position, count))
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.TrailingData, $"{count} bytes after the end of the Segment", position));
        }
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> data, bool complete, out string? docType)
    {
        docType = null;
        int i = 0;
        while (i < data.Length)
        {
            if (!TryReadHeader(data[i..], out uint id, out long size, out bool unknown, out int headerLength) || unknown)
            {
                return false;
            }

            long end = i + headerLength + size;
            if (end > data.Length)
            {
                return !complete;
            }

            if (id == DocTypeId)
            {
                docType = StructureBytes.Ascii(data.Slice(i + headerLength, (int)size)).TrimEnd('?');
            }

            i = (int)end;
        }

        return true;
    }

    private static bool TryReadElement(StructureSource source, long position, out Element element)
    {
        element = default;
        Span<byte> bytes = stackalloc byte[12];
        int read = source.Read(position, bytes);
        if (!TryReadHeader(bytes[..read], out uint id, out long size, out bool unknown, out int headerLength))
        {
            return false;
        }

        element = new Element(id, position, position + headerLength, size, unknown);
        return true;
    }

    /// <summary>An element ID (1–4 bytes, marker bits kept) and size (1–8 bytes; all value bits set = unknown).</summary>
    internal static bool TryReadHeader(ReadOnlySpan<byte> b, out uint id, out long size, out bool unknownSize, out int headerLength)
    {
        id = 0;
        size = 0;
        unknownSize = false;
        headerLength = 0;
        if (b.IsEmpty || b[0] == 0)
        {
            return false;
        }

        int idLength = BitOperations.LeadingZeroCount((uint)b[0]) - 23;
        if (idLength > 4 || b.Length <= idLength || b[idLength] == 0)
        {
            return false;
        }

        for (int i = 0; i < idLength; i++)
        {
            id = (id << 8) | b[i];
        }

        ReadOnlySpan<byte> s = b[idLength..];
        int sizeLength = BitOperations.LeadingZeroCount((uint)s[0]) - 23;
        if (s.Length < sizeLength)
        {
            return false;
        }

        int mask = 0xFF >> sizeLength;
        ulong value = (ulong)(s[0] & mask);
        bool allOnes = (s[0] & mask) == mask;
        for (int i = 1; i < sizeLength; i++)
        {
            value = (value << 8) | s[i];
            allOnes &= s[i] == 0xFF;
        }

        unknownSize = allOnes;
        size = StructureBytes.Saturate(value);
        headerLength = idLength + sizeLength;
        return true;
    }

    private static bool TryReadUInt(ReadOnlySpan<byte> data, out ulong value)
    {
        value = 0;
        if (data.Length > 8)
        {
            return false;
        }

        foreach (byte b in data)
        {
            value = (value << 8) | b;
        }

        return true;
    }

    private static string ElementName(uint id) => id switch
    {
        SeekHeadId => "SeekHead",
        0x1549A966 => "Info",
        TracksId => "Tracks",
        CuesId => "Cues",
        ClusterId => "Cluster",
        0x1254C367 => "Tags",
        0x1043A770 => "Chapters",
        0x1941A469 => "Attachments",
        VoidId => "Void",
        _ => "0x" + id.ToString("X", CultureInfo.InvariantCulture),
    };

    private readonly record struct Element(uint Id, long Offset, long DataStart, long Size, bool UnknownSize)
    {
        public long End => UnknownSize ? long.MaxValue : StructureBytes.SaturatingAdd(DataStart, Size);
    }

    /// <summary>What the walk has learnt about Tracks and Cues, directly or through SeekHead entries.</summary>
    private sealed class Walker(StructureSource source, long segmentData)
    {
        private readonly List<(uint Id, long Position)> _seeks = [];
        private readonly HashSet<long> _seekHeadsParsed = [];
        private int _resolvedCount;

        public bool HasCues { get; private set; }

        public bool HasVideo { get; private set; }

        public bool TracksSeen { get; private set; }

        public bool SeeksResolved { get; private set; }

        public void Visit(in Element element, List<PaddingRegion> padding)
        {
            switch (element.Id)
            {
                case SeekHeadId:
                    ParseSeekHead(element);
                    break;
                case TracksId:
                    TracksSeen = true;
                    HasVideo |= ContainsVideoTrack(element);
                    break;
                case CuesId:
                    HasCues = true;
                    break;
                case VoidId:
                    StructureBytes.AddPadding(padding, element.DataStart, Math.Min(element.End, source.Length));
                    break;
            }
        }

        /// <summary>Follows SeekHead entries for Cues, Tracks and further SeekHeads that the walk has not met yet.</summary>
        public void ResolveSeeks()
        {
            SeeksResolved = true;
            for (; _resolvedCount < _seeks.Count && _resolvedCount < MaxSeekEntries; _resolvedCount++)
            {
                (uint id, long relative) = _seeks[_resolvedCount];
                if ((id == CuesId && HasCues) || (id == TracksId && TracksSeen) || (id != CuesId && id != TracksId && id != SeekHeadId))
                {
                    continue;
                }

                long position = StructureBytes.SaturatingAdd(segmentData, relative);
                if (!TryReadElement(source, position, out Element target) || target.Id != id)
                {
                    continue;
                }

                if (id == CuesId)
                {
                    HasCues = true;
                }
                else if (id == TracksId && !target.UnknownSize)
                {
                    TracksSeen = true;
                    HasVideo |= ContainsVideoTrack(target);
                }
                else if (id == SeekHeadId && !target.UnknownSize)
                {
                    ParseSeekHead(target);
                }
            }
        }

        private void ParseSeekHead(in Element element)
        {
            if (element.UnknownSize || _seekHeadsParsed.Count >= MaxSeekHeads || !_seekHeadsParsed.Add(element.Offset))
            {
                return;
            }

            ReadOnlySpan<byte> data = source.ReadBytes(element.DataStart, (int)Math.Min(element.Size, MaxSeekHeadBytes));
            int i = 0;
            while (i < data.Length && _seeks.Count < MaxSeekEntries)
            {
                if (!TryChild(data, ref i, out uint childId, out ReadOnlySpan<byte> seek))
                {
                    break;
                }

                if (childId != SeekId)
                {
                    continue;
                }

                uint target = 0;
                ulong targetPosition = 0;
                bool hasId = false;
                bool hasPosition = false;
                int j = 0;
                while (j < seek.Length)
                {
                    if (!TryChild(seek, ref j, out uint field, out ReadOnlySpan<byte> value))
                    {
                        break;
                    }

                    if (field == SeekIdId && value.Length is > 0 and <= 4)
                    {
                        foreach (byte b in value)
                        {
                            target = (target << 8) | b;
                        }

                        hasId = true;
                    }
                    else if (field == SeekPositionId && TryReadUInt(value, out targetPosition))
                    {
                        hasPosition = true;
                    }
                }

                if (hasId && hasPosition)
                {
                    _seeks.Add((target, StructureBytes.Saturate(targetPosition)));
                }
            }
        }

        private bool ContainsVideoTrack(in Element tracks)
        {
            ReadOnlySpan<byte> data = source.ReadBytes(tracks.DataStart, (int)Math.Min(tracks.Size, MaxTracksBytes));
            int i = 0;
            while (i < data.Length)
            {
                if (!TryChild(data, ref i, out uint childId, out ReadOnlySpan<byte> entry))
                {
                    return false;
                }

                if (childId != TrackEntryId)
                {
                    continue;
                }

                int j = 0;
                while (j < entry.Length)
                {
                    if (!TryChild(entry, ref j, out uint field, out ReadOnlySpan<byte> value))
                    {
                        break;
                    }

                    if (field == TrackTypeId && TryReadUInt(value, out ulong trackType) && trackType == 1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>The next complete child element inside <paramref name="data"/>; false at the end or on damage.</summary>
        private static bool TryChild(ReadOnlySpan<byte> data, ref int position, out uint id, out ReadOnlySpan<byte> payload)
        {
            payload = default;
            if (!TryReadHeader(data[position..], out id, out long size, out bool unknown, out int headerLength) || unknown)
            {
                return false;
            }

            long end = position + headerLength + size;
            if (end > data.Length)
            {
                return false;
            }

            payload = data.Slice(position + headerLength, (int)size);
            position = (int)end;
            return true;
        }
    }
}
