using System.Text;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Minimal, structurally valid files of each format, built in code.</summary>
internal static class TestMedia
{
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    // ---- ISO-BMFF ----

    public static byte[] Box(string type, params byte[][] payload)
    {
        byte[] body = Concat(payload);
        return new ByteBuilder().U32BE((uint)(8 + body.Length)).Ascii(type).Bytes(body).ToArray();
    }

    public static byte[] LargeBox(string type, byte[] payload) =>
        new ByteBuilder().U32BE(1).Ascii(type).U64BE((ulong)(16 + payload.Length)).Bytes(payload).ToArray();

    public static byte[] Ftyp(string brand) =>
        Box("ftyp", new ByteBuilder().Ascii(brand).U32BE(0x200).Ascii(brand).Ascii("mp41").ToArray());

    public static byte[] Moov() => Box("moov", Box("mvhd", new byte[100]), Box("trak", new byte[200]));

    public static byte[] Mdat(int size, int seed = 1) => Box("mdat", new ByteBuilder().Noise(size, seed).ToArray());

    /// <summary>ftyp, moov, free (64 bytes), mdat: index at the front.</summary>
    public static byte[] FastStartMp4(int mdatSize = 50_000) => Concat(Ftyp("isom"), Moov(), Box("free", new byte[64]), Mdat(mdatSize));

    /// <summary>ftyp, mdat, moov: the camera layout with the index at the end.</summary>
    public static byte[] MoovAtEndMp4(int mdatSize = 50_000) => Concat(Ftyp("isom"), Mdat(mdatSize), Moov());

    // ---- RIFF ----

    public static byte[] Chunk(string id, byte[] data) =>
        new ByteBuilder().Ascii(id).U32LE((uint)data.Length).Bytes(data).Fill(data.Length & 1).ToArray();

    public static byte[] List(string type, params byte[][] chunks) => Chunk("LIST", Concat([Ascii(type), .. chunks]));

    public static byte[] Riff(string form, params byte[][] chunks)
    {
        byte[] body = Concat(chunks);
        return new ByteBuilder().Ascii("RIFF").U32LE((uint)(4 + body.Length)).Ascii(form).Bytes(body).ToArray();
    }

    public static byte[] PcmFormat() =>
        new ByteBuilder().U16LE(1).U16LE(2).U32LE(44100).U32LE(176400).U16LE(4).U16LE(16).ToArray();

    public static byte[] Wav(int dataBytes = 8000) =>
        Riff("WAVE", Chunk("fmt ", PcmFormat()), Chunk("data", new ByteBuilder().Noise(dataBytes, 3).ToArray()));

    public static byte[] Avi(int moviBytes = 6000) => Riff(
        "AVI ",
        List("hdrl", Chunk("avih", new byte[56])),
        Chunk("JUNK", new byte[100]),
        List("movi", Chunk("00dc", new ByteBuilder().Noise(moviBytes, 4).ToArray())),
        Chunk("idx1", new byte[16]));

    public static byte[] WebP(int imageBytes = 3000) => Riff("WEBP", Chunk("VP8 ", new ByteBuilder().Noise(imageBytes, 5).ToArray()));

    /// <summary>RF64: RIFF size and data size 0xFFFFFFFF, the real ones in ds64.</summary>
    public static byte[] Rf64(int dataBytes = 8000, ulong? riffSize = null)
    {
        byte[] fmt = Chunk("fmt ", PcmFormat());
        byte[] data = new ByteBuilder().Ascii("data").U32LE(0xFFFFFFFF).Noise(dataBytes, 6).ToArray();
        int total = 12 + 8 + 28 + fmt.Length + data.Length;
        byte[] ds64 = new ByteBuilder().Ascii("ds64").U32LE(28).U64LE(riffSize ?? (ulong)(total - 8)).U64LE((ulong)dataBytes).U64LE((ulong)dataBytes / 4).U32LE(0).ToArray();
        return Concat(new ByteBuilder().Ascii("RF64").U32LE(0xFFFFFFFF).Ascii("WAVE").ToArray(), ds64, fmt, data);
    }

    // ---- FLAC ----

    public static byte[] StreamInfo(int sampleRate, int channels, int bits, long totalSamples, byte[] md5)
    {
        ulong packed = ((ulong)sampleRate << 44) | ((ulong)(channels - 1) << 41) | ((ulong)(bits - 1) << 36) | (ulong)totalSamples;
        return new ByteBuilder().U16BE(4096).U16BE(4096).U24BE(14).U24BE(9000).U64BE(packed).Bytes(md5).ToArray();
    }

    public static byte[] FlacBlock(int type, bool last, byte[] data) =>
        new ByteBuilder().U8((last ? 0x80 : 0) | type).U24BE(data.Length).Bytes(data).ToArray();

    public static readonly byte[] SampleMd5 = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10];

    /// <summary>fLaC, STREAMINFO (44.1 kHz, 2 ch, 16 bit, 882000 samples), VORBIS_COMMENT, PADDING (1024), frames.</summary>
    public static byte[] Flac(byte[]? md5 = null, int paddingBytes = 1024) => Concat(
        Ascii("fLaC"),
        FlacBlock(0, false, StreamInfo(44100, 2, 16, 882_000, md5 ?? SampleMd5)),
        FlacBlock(4, false, new ByteBuilder().U32LE(4).Ascii("test").U32LE(0).ToArray()),
        FlacBlock(1, true, new byte[paddingBytes]),
        new ByteBuilder().Bytes(0xFF, 0xF8, 0x69, 0x08).Noise(5000, 7).ToArray());

    // ---- ID3 and MP3 ----

    /// <summary>An ID3v2.4 tag with one TIT2 frame and <paramref name="paddingBytes"/> of padding.</summary>
    public static byte[] Id3v2(int paddingBytes = 100, bool footer = false)
    {
        byte[] frame = new ByteBuilder().Ascii("TIT2").U32BE(6).U16BE(0).U8(3).Ascii("Title").ToArray();
        int size = frame.Length + paddingBytes;
        ByteBuilder tag = new ByteBuilder().Ascii("ID3").U8(4).U8(0).U8(footer ? 0x10 : 0).Bytes(Synchsafe(size)).Bytes(frame).Fill(paddingBytes);
        if (footer)
        {
            tag.Ascii("3DI").U8(4).U8(0).U8(0x10).Bytes(Synchsafe(size));
        }

        return tag.ToArray();
    }

    public static byte[] Synchsafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    public static byte[] Id3v1() => new ByteBuilder().Ascii("TAG").Fill(125, (byte)'x').ToArray();

    /// <summary>An APEv2 tag with a header and one item padded to <paramref name="itemBytes"/>.</summary>
    public static byte[] ApeTag(int itemBytes)
    {
        byte[] items = new ByteBuilder().U32LE((uint)(itemBytes - 14)).U32LE(0).Ascii("Title").U8(0).Fill(itemBytes - 14, (byte)'a').ToArray();
        uint size = (uint)(items.Length + 32);
        ByteBuilder header = new ByteBuilder().Ascii("APETAGEX").U32LE(2000).U32LE(size).U32LE(1).U32LE(0xA0000000).Fill(8);
        ByteBuilder footer = new ByteBuilder().Ascii("APETAGEX").U32LE(2000).U32LE(size).U32LE(1).U32LE(0x80000000).Fill(8);
        return Concat(header.ToArray(), items, footer.ToArray());
    }

    /// <summary>MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, stereo: 417 bytes, 418 with padding.</summary>
    public static byte[] Mp3Frame(bool padding = false) =>
        new ByteBuilder().Bytes(0xFF, 0xFB, (byte)(padding ? 0x92 : 0x90), 0x00).Fill(padding ? 414 : 413, 0x55).ToArray();

    public const int FrameLength = 417;

    /// <summary>A Xing/Info header frame (flags for whichever counts are given), or a VBRI frame.</summary>
    public static byte[] VbrFrame(string tag, uint? frames, uint? bytes)
    {
        ByteBuilder b = new ByteBuilder().Bytes(0xFF, 0xFB, 0x90, 0x00).Fill(32);
        if (tag == "VBRI")
        {
            b.Ascii("VBRI").U16BE(1).U16BE(576).U16BE(75).U32BE(bytes ?? 0).U32BE(frames ?? 0);
        }
        else
        {
            uint flags = (frames is null ? 0u : 1u) | (bytes is null ? 0u : 2u);
            b.Ascii(tag).U32BE(flags);
            if (frames is uint f)
            {
                b.U32BE(f);
            }

            if (bytes is uint n)
            {
                b.U32BE(n);
            }
        }

        return b.Fill(FrameLength - b.Length).ToArray();
    }

    /// <summary>ID3v2 + VBR header frame + <paramref name="frameCount"/> audio frames; the header declares the true counts unless overridden.</summary>
    public static byte[] Mp3(int frameCount = 200, string tag = "Info", uint? declaredFrames = null, uint? declaredBytes = null, bool withBytes = true, bool id3 = true)
    {
        uint frames = declaredFrames ?? (uint)frameCount;
        uint bytes = declaredBytes ?? (uint)((frameCount + 1) * FrameLength);
        byte[][] audio = [.. Enumerable.Range(0, frameCount).Select(_ => Mp3Frame())];
        return Concat([id3 ? Id3v2() : [], VbrFrame(tag, frames, withBytes ? bytes : null), .. audio]);
    }

    // ---- JPEG ----

    public static byte[] Segment(byte marker, byte[] payload) =>
        new ByteBuilder().U8(0xFF).U8(marker).U16BE(payload.Length + 2).Bytes(payload).ToArray();

    public static byte[] App0() => Segment(0xE0, new ByteBuilder().Ascii("JFIF").U8(0).Bytes(1, 2, 0).U16BE(1).U16BE(1).Bytes(0, 0).ToArray());

    public static int[] Table(int start) => [.. Enumerable.Range(start, 64)];

    /// <summary>One DQT segment holding several tables: (precision 0 = 8-bit, 1 = 16-bit; id; values).</summary>
    public static byte[] Dqt(params (int Precision, int Id, int[] Values)[] tables)
    {
        ByteBuilder b = new();
        foreach ((int precision, int id, int[] values) in tables)
        {
            b.U8((precision << 4) | id);
            foreach (int v in values)
            {
                if (precision == 0)
                {
                    b.U8(v);
                }
                else
                {
                    b.U16BE(v);
                }
            }
        }

        return Segment(0xDB, b.ToArray());
    }

    public static byte[] Sof(byte marker, int width, int height) =>
        Segment(marker, new ByteBuilder().U8(8).U16BE(height).U16BE(width).U8(3).Bytes(1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1).ToArray());

    public static byte[] Dht() => Segment(0xC4, new ByteBuilder().U8(0).U8(1).Fill(15).U8(0).ToArray());

    public static byte[] Sos() => Segment(0xDA, [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);

    /// <summary>Entropy-coded stand-in: noise with stuffed FF 00 pairs and, optionally, RST markers.</summary>
    public static byte[] Entropy(int length, int seed, bool restartMarkers = false)
    {
        ByteBuilder b = new();
        Random random = new(seed);
        int restart = 0;
        while (b.Length < length)
        {
            int roll = random.Next(100);
            if (roll < 3)
            {
                b.Bytes(0xFF, 0x00);
            }
            else if (restartMarkers && roll < 5)
            {
                b.Bytes(0xFF, (byte)(0xD0 + (restart++ % 8)));
            }
            else
            {
                b.U8(random.Next(0, 0xFF));
            }
        }

        return b.ToArray();
    }

    public static readonly byte[] Soi = [0xFF, 0xD8];

    public static readonly byte[] EoiMarker = [0xFF, 0xD9];

    public static byte[] BaselineJpeg(int width = 640, int height = 480, bool restartMarkers = false) => Concat(
        Soi, App0(), Dqt((0, 0, Table(1)), (0, 1, Table(2))), Sof(0xC0, width, height), Dht(),
        restartMarkers ? Segment(0xDD, [0, 8]) : [], Sos(), Entropy(4000, 11, restartMarkers), EoiMarker);

    public static byte[] ProgressiveJpeg(int scans = 4) => Concat([
        Soi, App0(), Dqt((0, 0, Table(1))), Sof(0xC2, 800, 600),
        .. Enumerable.Range(0, scans).SelectMany(i => new[] { Dht(), Sos(), Entropy(1500, 20 + i) }),
        EoiMarker,
    ]);

    // ---- PNG ----

    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] PngChunk(string type, byte[] data) =>
        new ByteBuilder().U32BE((uint)data.Length).Ascii(type).Bytes(data).U32BE(0).ToArray();

    public static byte[] Png(int idatBytes = 1000) => Concat(
        PngSignature,
        PngChunk("IHDR", new ByteBuilder().U32BE(64).U32BE(48).U8(8).U8(2).U8(0).U8(0).U8(0).ToArray()),
        PngChunk("IDAT", new ByteBuilder().Noise(idatBytes, 9).ToArray()),
        PngChunk("IEND", []));

    // ---- Matroska ----

    public const uint EbmlId = 0x1A45DFA3;
    public const uint SegmentId = 0x18538067;
    public const uint SeekHeadId = 0x114D9B74;
    public const uint InfoId = 0x1549A966;
    public const uint TracksId = 0x1654AE6B;
    public const uint ClusterId = 0x1F43B675;
    public const uint CuesId = 0x1C53BB6B;
    public const uint VoidId = 0xEC;

    public static byte[] EbmlIdBytes(uint id) => id switch
    {
        > 0xFFFFFF => [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id],
        > 0xFFFF => [(byte)(id >> 16), (byte)(id >> 8), (byte)id],
        > 0xFF => [(byte)(id >> 8), (byte)id],
        _ => [(byte)id],
    };

    public static byte[] EbmlSize(long size)
    {
        for (int n = 1; n <= 8; n++)
        {
            if (size < (1L << (7 * n)) - 1)
            {
                byte[] bytes = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    bytes[n - 1 - i] = (byte)(size >> (8 * i));
                }

                bytes[0] |= (byte)(0x80 >> (n - 1));
                return bytes;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(size));
    }

    public static readonly byte[] UnknownSize = [0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    public static byte[] Element(uint id, params byte[][] payload)
    {
        byte[] body = Concat(payload);
        return Concat(EbmlIdBytes(id), EbmlSize(body.Length), body);
    }

    public static byte[] UnknownSizeElement(uint id, params byte[][] payload) => Concat([EbmlIdBytes(id), UnknownSize, .. payload]);

    public static byte[] UInt(uint id, ulong value, int width = 0)
    {
        int n = width > 0 ? width : Math.Max(1, (64 - System.Numerics.BitOperations.LeadingZeroCount(value) + 7) / 8);
        byte[] bytes = new byte[n];
        for (int i = 0; i < n; i++)
        {
            bytes[n - 1 - i] = (byte)(value >> (8 * i));
        }

        return Element(id, bytes);
    }

    public static byte[] EbmlHeader(string docType) => Element(
        EbmlId, UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8), Element(0x4282, Ascii(docType)), UInt(0x4287, 4), UInt(0x4285, 2));

    public static byte[] Info() => Element(InfoId, UInt(0x2AD7B1, 1_000_000), Element(0x4D80, Ascii("test")));

    /// <summary>Tracks with one TrackEntry per type (1 video, 2 audio).</summary>
    public static byte[] Tracks(params int[] types) =>
        Element(TracksId, Concat([.. types.Select((t, i) => Element(0xAE, UInt(0xD7, (ulong)(i + 1)), UInt(0x73C5, (ulong)(i + 100)), UInt(0x83, (ulong)t), Element(0x86, Ascii("V_TEST"))))]));

    public static byte[] Cluster(int bytes, int seed) =>
        Element(ClusterId, UInt(0xE7, (ulong)seed), Element(0xA3, new ByteBuilder().Noise(bytes, seed).ToArray()));

    public static byte[] Cues() =>
        Element(CuesId, Element(0xBB, UInt(0xB3, 0), Element(0xB7, UInt(0xF7, 1), UInt(0xF1, 0))));

    public static byte[] Void(int bytes) => Element(VoidId, new byte[bytes]);

    /// <summary>A SeekHead with fixed-width (8-byte) positions, so its size does not depend on them.</summary>
    public static byte[] SeekHead(params (uint Id, long Position)[] entries) => Element(
        SeekHeadId,
        Concat([.. entries.Select(e => Element(0x4DBB, Element(0x53AB, EbmlIdBytes(e.Id)), UInt(0x53AC, (ulong)e.Position, 8)))]));

    /// <summary>
    /// A Matroska file: EBML header, Segment with SeekHead (when <paramref name="seekHead"/>), Void, Info,
    /// Tracks, three Clusters and, when <paramref name="cues"/>, Cues at the end.
    /// </summary>
    public static byte[] Mkv(bool cues = true, bool seekHead = true, bool seekToCues = true, int[]? trackTypes = null, bool unknownSize = false, string docType = "matroska")
    {
        byte[] voidElement = Void(40);
        byte[] info = Info();
        byte[] tracks = Tracks(trackTypes ?? [1, 2]);
        byte[][] clusters = [Cluster(3000, 1), Cluster(3000, 2), Cluster(3000, 3)];
        byte[] cuesElement = cues ? Cues() : [];

        (uint, long)[] Entries(long headLength)
        {
            long infoAt = headLength + voidElement.Length;
            long tracksAt = infoAt + info.Length;
            long cuesAt = tracksAt + tracks.Length + clusters.Sum(c => c.Length);
            List<(uint, long)> list = [(InfoId, infoAt), (TracksId, tracksAt)];
            if (seekToCues)
            {
                list.Add((CuesId, cuesAt));
            }

            return [.. list];
        }

        byte[] head = [];
        if (seekHead)
        {
            int headLength = SeekHead(Entries(0)).Length;
            head = SeekHead(Entries(headLength));
        }

        byte[] children = Concat([head, voidElement, info, tracks, .. clusters, cuesElement]);
        byte[] segment = unknownSize ? UnknownSizeElement(SegmentId, children) : Element(SegmentId, children);
        return Concat(EbmlHeader(docType), segment);
    }
}
