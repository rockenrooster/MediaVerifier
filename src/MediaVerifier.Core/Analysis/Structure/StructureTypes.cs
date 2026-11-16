using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// A container or elementary-stream format recognised from its signature (subplan 07, Signature
/// sniffing). <see cref="Tiff"/> also covers TIFF-based RAW files (DNG, CR2, NEF, ARW, PEF, SRW), the RAW
/// variants with their own magic (ORF, RW2) and Fujifilm RAF; <see cref="Mp3"/> covers MPEG audio
/// layers I to III.
/// </summary>
public enum ContainerFormat
{
    Unknown,
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    WebP,
    Wav,
    Rf64,
    Avi,
    IsoBmff,
    JpegXl,
    Jpeg2000,
    Matroska,
    Flac,
    Ogg,
    Mp3,
    Asf,
    Aiff,
    Flv,
    MpegPs,
    MpegTs,
    Ape,
    WavPack,
    Tta,
    Musepack,
    Dsf,
    Dsdiff,
    Ac3,
    Dts,
    Amr,
    Caf,
    Au,
}

/// <summary>What <see cref="SignatureSniffer"/> recognised.</summary>
/// <param name="Format">The format; <see cref="ContainerFormat.Unknown"/> when no signature matched.</param>
/// <param name="Brand">
/// The ISO-BMFF major brand ("isom", "M4A ", "heic", "avif", "qt  "), the RIFF form type ("WAVE", "AVI ",
/// "WEBP") or the AIFF form type ("AIFF", "AIFC"): four characters with spaces kept, bytes outside
/// printable ASCII shown as '?'. Null for every other format and for QuickTime files without an ftyp box.
/// </param>
/// <param name="Id3v2Length">
/// Total bytes of the ID3v2 tags in front of the content (each one: the 10-byte header, the synchsafe
/// size and the optional 10-byte footer), or 0. Larger than the file when a tag is cut off.
/// </param>
/// <param name="KindHint">
/// A first guess at the media kind from the signature alone (the probe refines it); Unknown when no
/// signature matched.
/// </param>
public sealed record SniffResult(ContainerFormat Format, string? Brand, long Id3v2Length, MediaKind KindHint);

/// <summary>A structural problem found by <see cref="StructureReader"/>. Each member names the rule it feeds.</summary>
public enum StructureIssueKind
{
    /// <summary>
    /// <c>COR-CONTAINER</c>: ISO-BMFF without <c>moov</c> (or HEIF without <c>meta</c>), <c>moof</c> boxes
    /// without <c>moov</c> (a bare segment), a bad FLAC block chain or no frame sync after it, a bad EBML
    /// header or no Segment.
    /// </summary>
    ContainerBroken,

    /// <summary>
    /// <c>COR-TRUNCATED</c>: a declared size or extent past the end of the file, no JPEG EOI, no PNG IEND,
    /// a Matroska Segment past the end, MP3 audio more than 1 % shorter than its VBR header declares.
    /// </summary>
    Truncated,

    /// <summary><c>COR-TRAILING</c>: bytes after the logical end that are neither a known trailer nor padding.</summary>
    TrailingData,

    /// <summary><c>COR-INDEX</c> (Low): a RIFF or data size of 0 or 0xFFFFFFFF; the writer never finalised it.</summary>
    IndexNotFinalised,

    /// <summary>
    /// <c>COR-INDEX</c>: Matroska with a video track but no Cues; MP3 audio more than 1 % longer than its VBR
    /// header declares, or a CBR Info header whose frame count disagrees with its byte count.
    /// </summary>
    IndexMissingOrWrong,
}

/// <summary>One structural finding with the facts for its evidence.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Detail">A short English statement of fact, numbers formatted with the invariant culture.</param>
/// <param name="Offset">File offset of the box, chunk, element, block or marker concerned.</param>
/// <param name="MissingBytes">
/// How far the declared structure reaches past the end of the file. For a PNG chunk it includes the IEND
/// chunk that must follow, and for a JPEG segment only that segment: a lower bound in both cases.
/// </param>
/// <param name="Element">The box, chunk, block, segment or element name ("moov", "mdat", "data", "IEND", "Segment").</param>
public sealed record StructureIssue(StructureIssueKind Kind, string Detail, long? Offset = null, long? MissingBytes = null, string? Element = null);

/// <summary>
/// Bytes the container declares as padding, clipped to the file: the payload of MP4 <c>free</c>/<c>skip</c>
/// boxes, Matroska <c>Void</c> elements, FLAC <c>PADDING</c> blocks and RIFF <c>JUNK</c> chunks, and the
/// padding at the end of an ID3v2 tag. The zero-fill check (subplan 06) subtracts them.
/// </summary>
public sealed record PaddingRegion(long Offset, long Length);

/// <summary>The STREAMINFO block of a FLAC file.</summary>
/// <param name="SampleRate">In Hz.</param>
/// <param name="Channels">1 to 8.</param>
/// <param name="BitsPerSample">4 to 32.</param>
/// <param name="TotalSamples">Samples per channel; 0 means unknown.</param>
/// <param name="Md5Hex">The MD5 of the decoded audio in lower-case hex; null when stored as all zeros (not computed).</param>
/// <param name="StreamInfoOffset">
/// File offset of the STREAMINFO block data. The MD5 is at +18, which is file offset 26 for a FLAC file
/// without an ID3v2 tag.
/// </param>
public sealed record FlacInfo(int SampleRate, int Channels, int BitsPerSample, long TotalSamples, string? Md5Hex, long StreamInfoOffset);

/// <summary>What the JPEG marker walk found.</summary>
/// <param name="Width">From the first SOF segment; 0 when there is none.</param>
/// <param name="Height">From the first SOF segment; 0 when there is none (or when a DNL segment defines it).</param>
/// <param name="Progressive">The first SOF is progressive (SOF2, SOF6, SOF10, SOF14).</param>
/// <param name="ScanCount">Number of SOS segments.</param>
/// <param name="QuantTables">
/// The quantisation tables indexed by destination id (Tq 0 to 3; table 0 is normally luminance). Each
/// table holds its 64 values in the order stored in the file, which is zig-zag order; 8-bit and 16-bit
/// tables are both widened to int. An id that was never defined holds an empty array, and when an id is
/// defined twice the first definition is kept. Empty when the file has no DQT segment.
/// </param>
/// <param name="HasEoi">The entropy data ends with an EOI marker.</param>
/// <param name="EoiOffset">File offset of the EOI marker (its FF byte); -1 without one.</param>
/// <param name="BytesAfterEoi">Bytes after the EOI marker; 0 without one.</param>
public sealed record JpegInfo(int Width, int Height, bool Progressive, int ScanCount, IReadOnlyList<int[]> QuantTables, bool HasEoi, long EoiOffset, long BytesAfterEoi);

/// <summary>The MPEG audio stream layout of an MP3 (or MP2) file.</summary>
/// <param name="AudioStart">File offset of the first frame (the VBR header frame when there is one).</param>
/// <param name="AudioBytes">
/// Bytes from <paramref name="AudioStart"/> to the end of the audio: trailing ID3v1, APE, Lyrics3 and
/// appended ID3v2 tags are not counted.
/// </param>
/// <param name="DeclaredFrames">The Xing/Info or VBRI frame count (it does not count the header frame); null without one.</param>
/// <param name="DeclaredBytes">The Xing/Info or VBRI byte count (it counts the header frame); null without one.</param>
/// <param name="VbrHeader">"Xing", "Info" (written for CBR) or "VBRI"; null without one.</param>
/// <param name="Bitrate">Nominal bitrate of the first frame in bit/s; for VBR files that is the header frame, not the average.</param>
/// <param name="SampleRate">In Hz.</param>
/// <param name="Channels">1 or 2.</param>
public sealed record Mp3Info(long AudioStart, long AudioBytes, long? DeclaredFrames, long? DeclaredBytes, string? VbrHeader, int Bitrate, int SampleRate, int Channels);

/// <summary>What the Matroska header and top-level walk found.</summary>
/// <param name="DocType">From the EBML header ("matroska", "webm"); null when absent.</param>
/// <param name="SegmentSize">The Segment's declared data size; null when unknown (a live or unfinalised file).</param>
/// <param name="HasCues">A Cues element exists, found among the Segment's children or through a SeekHead entry.</param>
/// <param name="HasVideoTrack">A TrackEntry has TrackType 1.</param>
public sealed record MatroskaInfo(string? DocType, long? SegmentSize, bool HasCues, bool HasVideoTrack);

/// <summary>The native structure check of one file (subplan 07, <c>StructureArtifact</c>).</summary>
/// <param name="Sniff">The recognised format.</param>
/// <param name="FileLength">Length of the file when it was read.</param>
/// <param name="DeclaredEnd">
/// Where the container says the file ends: end of the last ISO-BMFF box, RIFF size + 8 (or the last AVIX
/// chunk), end of the PNG IEND chunk, EOI + 2, Segment end, or the MP3 VBR header frame + its declared
/// bytes. Beyond <paramref name="FileLength"/> when truncated; null when the format declares no end or it
/// is unknown (FLAC, unknown Matroska Segment size, unfinalised RIFF, JPEG without EOI).
/// </param>
/// <param name="Issues">Structural problems, in the order found; empty for a healthy file.</param>
/// <param name="Padding">Declared padding regions.</param>
/// <param name="Flac">STREAMINFO for FLAC files.</param>
/// <param name="Jpeg">Marker-walk results for JPEG files.</param>
/// <param name="Mp3">Stream layout for MPEG audio files.</param>
/// <param name="Matroska">Header and index facts for Matroska and WebM files.</param>
public sealed record StructureArtifact(
    SniffResult Sniff,
    long FileLength,
    long? DeclaredEnd,
    IReadOnlyList<StructureIssue> Issues,
    IReadOnlyList<PaddingRegion> Padding,
    FlacInfo? Flac = null,
    JpegInfo? Jpeg = null,
    Mp3Info? Mp3 = null,
    MatroskaInfo? Matroska = null);

/// <summary>How a file's extension compares with its content (<c>GEN-EXT-MISMATCH</c>, <c>GEN-UNRECOGNIZED</c>).</summary>
public enum ExtensionVerdict
{
    /// <summary>The extension fits the content, or there is nothing to compare.</summary>
    Match,

    /// <summary>An image named as another image type (PNG called .jpg).</summary>
    MismatchLow,

    /// <summary>A different audio or video container, or a different media kind.</summary>
    MismatchMedium,

    /// <summary>No known signature and FFmpeg cannot open it.</summary>
    Unrecognized,
}
