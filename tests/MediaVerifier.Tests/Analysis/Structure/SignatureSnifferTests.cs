using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Model;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;
using static MediaVerifier.Tests.Analysis.Structure.TestMedia;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Task C-01: one minimal byte array per signature in the subplan 07 table.</summary>
public sealed class SignatureSnifferTests
{
    public static TheoryData<string, byte[], ContainerFormat, MediaKind> Signatures => new()
    {
        { "JPEG", Pad(0xFF, 0xD8, 0xFF, 0xE0), ContainerFormat.Jpeg, MediaKind.Photo },
        { "PNG", Pad(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), ContainerFormat.Png, MediaKind.Photo },
        { "GIF87a", Pad(Ascii("GIF87a")), ContainerFormat.Gif, MediaKind.Photo },
        { "GIF89a", Pad(Ascii("GIF89a")), ContainerFormat.Gif, MediaKind.Photo },
        { "BMP", Pad(new ByteBuilder().Ascii("BM").U32LE(1000).U32LE(0).U32LE(54).U32LE(40).ToArray()), ContainerFormat.Bmp, MediaKind.Photo },
        { "TIFF little-endian", Pad(0x49, 0x49, 0x2A, 0x00, 0x08, 0, 0, 0), ContainerFormat.Tiff, MediaKind.Photo },
        { "TIFF big-endian", Pad(0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8), ContainerFormat.Tiff, MediaKind.Photo },
        { "ORF", Pad(Ascii("IIRO")), ContainerFormat.Tiff, MediaKind.Photo },
        { "RW2", Pad(0x49, 0x49, 0x55, 0x00), ContainerFormat.Tiff, MediaKind.Photo },
        { "RAF", Pad(Ascii("FUJIFILMCCD-RAW 0201")), ContainerFormat.Tiff, MediaKind.Photo },
        { "WebP", Riff("WEBP", Chunk("VP8 ", new byte[40])), ContainerFormat.WebP, MediaKind.Photo },
        { "WAV", Wav(100), ContainerFormat.Wav, MediaKind.Audio },
        { "AVI", Avi(100), ContainerFormat.Avi, MediaKind.Video },
        { "RF64", Rf64(100), ContainerFormat.Rf64, MediaKind.Audio },
        { "MP4", FastStartMp4(100), ContainerFormat.IsoBmff, MediaKind.Video },
        { "M4A", Concat(Ftyp("M4A "), Moov()), ContainerFormat.IsoBmff, MediaKind.Audio },
        { "HEIC", Concat(Ftyp("heic"), Box("meta", new byte[20])), ContainerFormat.IsoBmff, MediaKind.Photo },
        { "AVIF", Concat(Ftyp("avif"), Box("meta", new byte[20])), ContainerFormat.IsoBmff, MediaKind.Photo },
        { "CR3", Concat(Ftyp("crx "), Moov()), ContainerFormat.IsoBmff, MediaKind.Photo },
        { "QuickTime moov first", Concat(Moov(), Mdat(100)), ContainerFormat.IsoBmff, MediaKind.Video },
        { "QuickTime wide first", Concat(Box("wide"), Mdat(100), Moov()), ContainerFormat.IsoBmff, MediaKind.Video },
        { "QuickTime mdat first", Concat(Mdat(100), Moov()), ContainerFormat.IsoBmff, MediaKind.Video },
        { "JPEG XL codestream", Pad(0xFF, 0x0A, 0xFA, 0x7F), ContainerFormat.JpegXl, MediaKind.Photo },
        { "JPEG XL container", Pad(0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A), ContainerFormat.JpegXl, MediaKind.Photo },
        { "JPEG 2000", Pad(0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A), ContainerFormat.Jpeg2000, MediaKind.Photo },
        { "Matroska", Mkv(), ContainerFormat.Matroska, MediaKind.Video },
        { "FLAC", Flac(), ContainerFormat.Flac, MediaKind.Audio },
        { "Ogg Vorbis", OggPage([0x01, .. Ascii("vorbis")]), ContainerFormat.Ogg, MediaKind.Audio },
        { "Ogg Theora", OggPage([0x80, .. Ascii("theora")]), ContainerFormat.Ogg, MediaKind.Video },
        { "MP3 with ID3v2", Mp3(20), ContainerFormat.Mp3, MediaKind.Audio },
        { "MP3 frame sync", Mp3(20, id3: false), ContainerFormat.Mp3, MediaKind.Audio },
        { "ASF", Pad(0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C), ContainerFormat.Asf, MediaKind.Video },
        { "AIFF", Pad(new ByteBuilder().Ascii("FORM").U32BE(100).Ascii("AIFF").ToArray()), ContainerFormat.Aiff, MediaKind.Audio },
        { "AIFC", Pad(new ByteBuilder().Ascii("FORM").U32BE(100).Ascii("AIFC").ToArray()), ContainerFormat.Aiff, MediaKind.Audio },
        { "FLV", Pad(Ascii("FLV"), [0x01, 0x05]), ContainerFormat.Flv, MediaKind.Video },
        { "MPEG-PS", Pad(0x00, 0x00, 0x01, 0xBA, 0x44), ContainerFormat.MpegPs, MediaKind.Video },
        { "MPEG-TS", TransportStream(188, 0, 6), ContainerFormat.MpegTs, MediaKind.Video },
        { "M2TS", TransportStream(192, 4, 6), ContainerFormat.MpegTs, MediaKind.Video },
        { "Monkey's Audio", Pad(Ascii("MAC ")), ContainerFormat.Ape, MediaKind.Audio },
        { "WavPack", Pad(Ascii("wvpk")), ContainerFormat.WavPack, MediaKind.Audio },
        { "TTA", Pad(Ascii("TTA1")), ContainerFormat.Tta, MediaKind.Audio },
        { "Musepack SV8", Pad(Ascii("MPCK")), ContainerFormat.Musepack, MediaKind.Audio },
        { "Musepack SV7", Pad(Ascii("MP+"), [0x17]), ContainerFormat.Musepack, MediaKind.Audio },
        { "DSF", Pad(Ascii("DSD "), [0x1C, 0, 0, 0, 0, 0, 0, 0]), ContainerFormat.Dsf, MediaKind.Audio },
        { "DSDIFF", Pad(Ascii("FRM8")), ContainerFormat.Dsdiff, MediaKind.Audio },
        { "AC-3", Pad(0x0B, 0x77, 0x12, 0x34, 0x14, 0x40), ContainerFormat.Ac3, MediaKind.Audio },
        { "DTS", Pad(0x7F, 0xFE, 0x80, 0x01), ContainerFormat.Dts, MediaKind.Audio },
        { "AMR", Pad(Ascii("#!AMR\n")), ContainerFormat.Amr, MediaKind.Audio },
        { "CAF", Pad(Ascii("caff"), [0x00, 0x01]), ContainerFormat.Caf, MediaKind.Audio },
        { "AU", Pad(Ascii(".snd")), ContainerFormat.Au, MediaKind.Audio },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void RecognisesEachSignature(string name, byte[] bytes, ContainerFormat format, MediaKind kind)
    {
        SniffResult result = Sniff(bytes);
        Assert.True(format == result.Format, $"{name}: expected {format}, got {result.Format}");
        Assert.Equal(kind, result.KindHint);
    }

    [Theory]
    [InlineData("isom", "isom")]
    [InlineData("M4A ", "M4A ")]
    [InlineData("qt  ", "qt  ")]
    [InlineData("heic", "heic")]
    public void ReportsTheIsoBmffMajorBrand(string brand, string expected) =>
        Assert.Equal(expected, Sniff(Concat(Ftyp(brand), Moov())).Brand);

    [Fact]
    public void ReportsRiffAndAiffFormTypes()
    {
        Assert.Equal("WAVE", Sniff(Wav(10)).Brand);
        Assert.Equal("AVI ", Sniff(Avi(10)).Brand);
        Assert.Equal("WEBP", Sniff(WebP(10)).Brand);
        Assert.Equal("AIFC", Sniff(Pad(new ByteBuilder().Ascii("FORM").U32BE(100).Ascii("AIFC").ToArray())).Brand);
        Assert.Null(Sniff(Flac()).Brand);
        Assert.Null(Sniff(Concat(Moov(), Mdat(10))).Brand);
    }

    [Fact]
    public void FlacAfterAnId3TagKeepsTheTagLength()
    {
        byte[] tag = Id3v2(paddingBytes: 500);
        SniffResult result = Sniff(Concat(tag, Flac()));
        Assert.Equal(ContainerFormat.Flac, result.Format);
        Assert.Equal(tag.Length, result.Id3v2Length);
    }

    [Fact]
    public void Id3v2FooterAndConsecutiveTagsAreCounted()
    {
        byte[] first = Id3v2(paddingBytes: 10, footer: true);
        byte[] second = Id3v2(paddingBytes: 20);
        Assert.Equal(first.Length, Sniff(Concat(first, Mp3(10, id3: false))).Id3v2Length);
        Assert.Equal(first.Length + second.Length, Sniff(Concat(first, second, Mp3(10, id3: false))).Id3v2Length);
    }

    [Fact]
    public void Id3TagAloneIsMp3()
    {
        byte[] tag = Id3v2();
        SniffResult result = Sniff(tag);
        Assert.Equal(ContainerFormat.Mp3, result.Format);
        Assert.Equal(tag.Length, result.Id3v2Length);

        // A tag cut off by truncation still names the format and reports its declared length.
        SniffResult cut = Sniff(tag[..40]);
        Assert.Equal(ContainerFormat.Mp3, cut.Format);
        Assert.Equal(tag.Length, cut.Id3v2Length);
    }

    [Fact]
    public void AdtsAfterAnId3TagIsNotMp3()
    {
        byte[] adts = Pad(0xFF, 0xF1, 0x50, 0x80, 0x2E, 0x7F, 0xFC);
        SniffResult result = Sniff(Concat(Id3v2(), adts));
        Assert.Equal(ContainerFormat.Unknown, result.Format);
        Assert.Equal(MediaKind.Unknown, result.KindHint);
        Assert.True(result.Id3v2Length > 0);
        Assert.Equal(ContainerFormat.Unknown, Sniff(adts).Format);
    }

    [Fact]
    public void MpegAudioNeedsASecondFrame()
    {
        byte[] frame = Mp3Frame();
        Assert.Equal(ContainerFormat.Mp3, Sniff(Concat(frame, frame)).Format);

        // A lone header followed by something that is not a frame header is not enough.
        Assert.Equal(ContainerFormat.Unknown, Sniff(Concat(frame, Ascii("not a frame header at all"))).Format);

        // MPEG-2 Layer III and MPEG-1 Layer II headers are MPEG audio too.
        // MPEG-2 Layer III, 48 kbit/s, 24 kHz: 72 x 48000 / 24000 = 144 bytes.
        byte[] mpeg2 = new ByteBuilder().Bytes(0xFF, 0xF3, 0x64, 0xC4).Fill(144 - 4, 0x11).ToArray();
        Assert.Equal(ContainerFormat.Mp3, Sniff(Concat(mpeg2, mpeg2, mpeg2)).Format);

        // MPEG-1 Layer II, 160 kbit/s, 48 kHz: 144 x 160000 / 48000 = 480 bytes.
        byte[] layer2 = new ByteBuilder().Bytes(0xFF, 0xFD, 0x94, 0x04).Fill(480 - 4, 0x22).ToArray();
        Assert.Equal(ContainerFormat.Mp3, Sniff(Concat(layer2, layer2)).Format);
    }

    [Fact]
    public void TransportStreamNeedsFivePackets()
    {
        byte[] stream = TransportStream(188, 0, 6);
        stream[188 * 3] = 0x00;
        Assert.Equal(ContainerFormat.Unknown, Sniff(stream).Format);
    }

    [Fact]
    public void UnrecognisedInputIsUnknown()
    {
        Assert.Equal(ContainerFormat.Unknown, Sniff([]).Format);
        Assert.Equal(ContainerFormat.Unknown, Sniff([0xFF]).Format);
        Assert.Equal(ContainerFormat.Unknown, Sniff(Ascii("This is a plain text file, not audio at all.\r\n")).Format);
        Assert.Equal(ContainerFormat.Unknown, Sniff(Pad(Ascii("RIFF"), [0x10, 0, 0, 0], Ascii("CDXA"))).Format);
        Assert.Equal(ContainerFormat.Unknown, Sniff(Pad(Ascii("BM"), [0, 0, 0, 0])).Format);
        Assert.Equal(MediaKind.Unknown, Sniff(new byte[100]).KindHint);
    }

    [Fact]
    public void HeadBeyondTheFileLengthIsIgnored()
    {
        byte[] jpeg = Pad(0xFF, 0xD8, 0xFF, 0xE0);
        Assert.Equal(ContainerFormat.Unknown, SignatureSniffer.Sniff(jpeg, 2).Format);
        Assert.Equal(ContainerFormat.Unknown, SignatureSniffer.Sniff(jpeg, -5).Format);
        Assert.Equal(ContainerFormat.Jpeg, SignatureSniffer.Sniff(jpeg, jpeg.Length).Format);
    }

    [Fact]
    public void WorksWithoutReadAt()
    {
        byte[] mp4 = FastStartMp4(100);
        Assert.Equal(ContainerFormat.IsoBmff, SignatureSniffer.Sniff(mp4.AsSpan(0, 64), mp4.Length).Format);

        // The second MP3 frame lies beyond a 64-byte head: without readAt it cannot be confirmed.
        byte[] mp3 = Mp3(10, id3: false);
        Assert.Equal(ContainerFormat.Unknown, SignatureSniffer.Sniff(mp3.AsSpan(0, 64), mp3.Length).Format);
        Assert.Equal(ContainerFormat.Mp3, SignatureSniffer.Sniff(mp3, mp3.Length).Format);
    }

    private static byte[] Pad(params byte[] start) => Pad(start, []);

    private static byte[] Pad(byte[] start, params byte[][] more)
    {
        byte[] head = Concat([start, .. more]);
        return Concat(head, new byte[Math.Max(0, 64 - head.Length)]);
    }

    private static byte[] OggPage(byte[] packet)
    {
        return new ByteBuilder().Ascii("OggS").U8(0).U8(2).Fill(8).U32LE(1).U32LE(0).U32LE(0).U8(1).U8(packet.Length + 20)
            .Bytes(packet).Fill(20).ToArray();
    }

    private static byte[] TransportStream(int stride, int syncOffset, int packets)
    {
        byte[] bytes = new byte[stride * packets];
        for (int i = 0; i < packets; i++)
        {
            bytes[(i * stride) + syncOffset] = 0x47;
            bytes[(i * stride) + syncOffset + 1] = 0x40;
        }

        return bytes;
    }
}
