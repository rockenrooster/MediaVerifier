using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// The native structure check (subplan 07, Structure checks; tasks C-01 to C-05 and C-13). Sniffs the
/// format, then walks headers and seeks; it never reads payload except where it must (the JPEG entropy
/// scan for EOI, the first MP3 frames and VBR header). Every length is checked against the file length:
/// a field pointing past the end is an issue, never an exception. Only an <see cref="IOException"/> from
/// the stream itself propagates.
/// </summary>
public static class StructureReader
{
    /// <summary>Bytes read for sniffing: one page, so that most signatures need no further read.</summary>
    private const int HeadLength = StructureSource.WindowSize;

    /// <summary>Checks one file.</summary>
    /// <param name="stream">A readable, seekable stream over the file; its position is changed.</param>
    /// <param name="extension">
    /// The file's extension (without the dot, any case). It only matters when the signature is unknown: an
    /// MPEG audio extension lets the reader look further for the first frame (junk before it).
    /// </param>
    public static StructureArtifact Read(Stream stream, string extension)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        using StructureSource source = new(stream);
        long length = source.Length;
        byte[] head = source.ReadBytes(0, HeadLength);
        SniffResult sniff = SignatureSniffer.Sniff(head, length, source.ReadBytes);
        List<StructureIssue> issues = [];
        List<PaddingRegion> padding = [];
        long? declaredEnd = null;
        FlacInfo? flac = null;
        JpegInfo? jpeg = null;
        Mp3Info? mp3 = null;
        MatroskaInfo? matroska = null;

        long id3 = sniff.Id3v2Length;
        if (id3 > 0)
        {
            Id3v2.CollectPadding(source, Math.Min(id3, length), padding);
            if (id3 > length)
            {
                long missing = id3 - length;
                issues.Add(StructureBytes.Issue(StructureIssueKind.Truncated, $"ID3v2 tag ends {missing} bytes past the end of the file", 0, missing, "ID3v2"));
                return new StructureArtifact(sniff, length, id3, issues, padding);
            }
        }

        switch (sniff.Format)
        {
            case ContainerFormat.IsoBmff:
                declaredEnd = IsoBmffWalker.Walk(source, sniff, issues, padding);
                break;
            case ContainerFormat.Wav or ContainerFormat.Rf64 or ContainerFormat.Avi or ContainerFormat.WebP:
                declaredEnd = RiffWalker.Walk(source, sniff, issues, padding);
                break;
            case ContainerFormat.Flac:
                flac = FlacReader.Read(source, id3, issues, padding);
                break;
            case ContainerFormat.Jpeg:
                jpeg = JpegWalker.Read(source, issues, padding, out declaredEnd);
                break;
            case ContainerFormat.Png:
                declaredEnd = PngWalker.Walk(source, issues);
                break;
            case ContainerFormat.Matroska:
                matroska = MatroskaReader.Read(source, issues, padding, out declaredEnd);
                break;
            case ContainerFormat.Mp3:
                mp3 = Mp3Reader.Read(source, id3, framesRequired: 2, issues, out declaredEnd);
                break;
            case ContainerFormat.Unknown when IsMpegAudioExtension(extension):
                // No signature at the start, but the name says MPEG audio: look past leading junk, with a
                // stricter three-frame confirmation since nothing else vouches for the format.
                mp3 = Mp3Reader.Read(source, id3, framesRequired: 3, issues, out declaredEnd);
                if (mp3 is not null)
                {
                    sniff = sniff with { Format = ContainerFormat.Mp3, KindHint = MediaKind.Audio };
                }

                break;
        }

        return new StructureArtifact(sniff, length, declaredEnd, issues, padding, flac, jpeg, mp3, matroska);
    }

    private static bool IsMpegAudioExtension(string? extension) =>
        extension?.Trim().TrimStart('.').ToLowerInvariant() is "mp3" or "mp2" or "mpa" or "mp1";
}
