namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// FLAC header reader (subplan 07, task C-03): skips ID3v2, walks the metadata block chain (last-block
/// flag, 24-bit lengths), parses STREAMINFO, records PADDING blocks and confirms that a frame sync follows.
/// </summary>
internal static class FlacReader
{
    private const int StreamInfoLength = 34;
    private const int MaxBlocks = 4096;

    public static FlacInfo? Read(StructureSource source, long start, List<StructureIssue> issues, List<PaddingRegion> padding)
    {
        long length = source.Length;
        Span<byte> buffer = stackalloc byte[StreamInfoLength];
        if (!source.TryRead(start, buffer[..4]) || !buffer[..4].SequenceEqual("fLaC"u8))
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"fLaC marker not found at offset {start}", start, null, "fLaC"));
            return null;
        }

        FlacInfo? info = null;
        long position = start + 4;
        for (int index = 0; ; index++)
        {
            if (index >= MaxBlocks)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"metadata block chain does not end after {MaxBlocks} blocks", position));
                return info;
            }

            int read = source.Read(position, buffer[..4]);
            if (read < 4)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"metadata block header at offset {position} is cut off", position, null, "metadata"));
                return info;
            }

            bool last = (buffer[0] & 0x80) != 0;
            int type = buffer[0] & 0x7F;
            long blockLength = (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
            string name = BlockName(type);
            if (type == 127)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"invalid metadata block type 127 at offset {position}", position, null, name));
                return info;
            }

            if (index == 0 && type != 0)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"the first metadata block is {name}, not STREAMINFO", position, null, name));
                return info;
            }

            if (type == 0 && blockLength != StreamInfoLength)
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"STREAMINFO block is {blockLength} bytes, not {StreamInfoLength}", position, null, name));
                return info;
            }

            long dataStart = position + 4;
            long end = dataStart + blockLength;
            if (end > length)
            {
                long missing = end - length;
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"{name} block ends {missing} bytes past the end of the file", position, missing, name));
                return info;
            }

            if (type == 0 && index == 0 && source.TryRead(dataStart, buffer))
            {
                info = ParseStreamInfo(buffer, dataStart);
            }
            else if (type == 1)
            {
                StructureBytes.AddPadding(padding, dataStart, end);
            }

            position = end;
            if (last)
            {
                break;
            }
        }

        if (position >= length)
        {
            // An empty stream has no frames; TotalSamples 0 means "unknown".
            if (info is { TotalSamples: > 0 })
            {
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"no audio frames follow the metadata blocks", position));
            }

            return info;
        }

        Span<byte> sync = buffer[..2];
        if (source.Read(position, sync) < 2 || sync[0] != 0xFF || (sync[1] & 0xFE) != 0xF8)
        {
            issues.Add(StructureBytes.Issue(StructureIssueKind.ContainerBroken, $"no frame sync after the metadata blocks at offset {position}", position));
        }

        return info;
    }

    private static FlacInfo ParseStreamInfo(ReadOnlySpan<byte> s, long offset)
    {
        int sampleRate = (s[10] << 12) | (s[11] << 4) | (s[12] >> 4);
        int channels = ((s[12] >> 1) & 0x7) + 1;
        int bitsPerSample = (((s[12] & 0x1) << 4) | (s[13] >> 4)) + 1;
        long totalSamples = ((long)(s[13] & 0xF) << 32) | ((long)s[14] << 24) | ((long)s[15] << 16) | ((long)s[16] << 8) | s[17];
        ReadOnlySpan<byte> md5 = s.Slice(18, 16);
        string? md5Hex = md5.IndexOfAnyExcept((byte)0) < 0 ? null : Convert.ToHexStringLower(md5);
        return new FlacInfo(sampleRate, channels, bitsPerSample, totalSamples, md5Hex, offset);
    }

    private static string BlockName(int type) => type switch
    {
        0 => "STREAMINFO",
        1 => "PADDING",
        2 => "APPLICATION",
        3 => "SEEKTABLE",
        4 => "VORBIS_COMMENT",
        5 => "CUESHEET",
        6 => "PICTURE",
        _ => "metadata",
    };
}
