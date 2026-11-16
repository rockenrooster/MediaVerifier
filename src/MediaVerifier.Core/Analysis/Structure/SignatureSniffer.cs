using System.Buffers.Binary;
using MediaVerifier.Core.Model;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>
/// Recognises a file's format from its first bytes (subplan 07, Signature sniffing; task C-01). Leading
/// ID3v2 tags are skipped; MP3 without a tag needs a second frame at the computed distance, MPEG-TS five
/// sync bytes. Never throws for any input; only an exception from <c>readAt</c> propagates.
/// </summary>
public static class SignatureSniffer
{
    /// <summary>How many leading bytes callers should pass when the file has them.</summary>
    public const int RecommendedHeadLength = 64;

    private const int Window = 64;
    private const int MaxLeadingTags = 8;
    private const int TsPackets = 5;

    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JxlContainer => [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
    private static ReadOnlySpan<byte> Jp2Signature => [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
    private static ReadOnlySpan<byte> J2kCodestream => [0xFF, 0x4F, 0xFF, 0x51];
    private static ReadOnlySpan<byte> EbmlMagic => [0x1A, 0x45, 0xDF, 0xA3];
    private static ReadOnlySpan<byte> AsfGuid => [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
    private static ReadOnlySpan<byte> MpegPsMagic => [0x00, 0x00, 0x01, 0xBA];
    private static ReadOnlySpan<byte> DtsCore => [0x7F, 0xFE, 0x80, 0x01];
    private static ReadOnlySpan<byte> DtsCoreLe => [0xFE, 0x7F, 0x01, 0x80];
    private static ReadOnlySpan<byte> Dts14Bit => [0x1F, 0xFF, 0xE8, 0x00];
    private static ReadOnlySpan<byte> Dts14BitLe => [0xFF, 0x1F, 0x00, 0xE8];
    private static ReadOnlySpan<byte> TheoraHeader => [0x80, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a'];

    /// <summary>Identifies the format.</summary>
    /// <param name="head">The first bytes of the file; at least <see cref="RecommendedHeadLength"/> when the file has them.</param>
    /// <param name="fileLength">The real file length; bytes in <paramref name="head"/> beyond it are ignored.</param>
    /// <param name="readAt">
    /// Reads up to <c>count</c> bytes at an offset (fewer at the end). Used for what lies beyond
    /// <paramref name="head"/>: content after an ID3v2 tag, MPEG-TS packets, the second MP3 frame.
    /// </param>
    public static SniffResult Sniff(ReadOnlySpan<byte> head, long fileLength, Func<long, int, byte[]>? readAt = null)
    {
        if (fileLength < 0)
        {
            fileLength = 0;
        }

        if (head.Length > fileLength)
        {
            head = head[..(int)fileLength];
        }

        Source source = new(head, fileLength, readAt);
        long start = 0;
        for (int i = 0; i < MaxLeadingTags && start < fileLength; i++)
        {
            if (!Id3v2.TryGetLength(source.Get(start, Id3v2.HeaderLength), out long tagLength))
            {
                break;
            }

            start += tagLength;
        }

        if (start == 0)
        {
            return Recognise(source, 0, 0) ?? new SniffResult(ContainerFormat.Unknown, null, 0, MediaKind.Unknown);
        }

        // After ID3v2: the audio formats that carry such tags; ADTS AAC (no ContainerFormat) is Unknown;
        // anything else, including a tag with nothing after it, is MP3 by the tag alone.
        if (start < fileLength)
        {
            SniffResult? inner = Recognise(source, start, start);
            if (inner is not null && inner.Format is ContainerFormat.Flac or ContainerFormat.Mp3 or ContainerFormat.Ape
                or ContainerFormat.WavPack or ContainerFormat.Tta or ContainerFormat.Musepack or ContainerFormat.Ac3 or ContainerFormat.Dts)
            {
                return inner;
            }

            if (inner is null && IsAdts(source.Get(start, 7)))
            {
                return new SniffResult(ContainerFormat.Unknown, null, start, MediaKind.Unknown);
            }
        }

        return new SniffResult(ContainerFormat.Mp3, null, start, MediaKind.Audio);
    }

    private static SniffResult? Recognise(in Source source, long offset, long id3Length)
    {
        ReadOnlySpan<byte> b = source.Get(offset, Window);
        if (b.Length < 2)
        {
            return null;
        }

        if (b.StartsWith(JpegMagic))
        {
            return Make(ContainerFormat.Jpeg, null, id3Length);
        }

        if (b.StartsWith(PngMagic))
        {
            return Make(ContainerFormat.Png, null, id3Length);
        }

        if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8))
        {
            return Make(ContainerFormat.Gif, null, id3Length);
        }

        if (IsBmp(b))
        {
            return Make(ContainerFormat.Bmp, null, id3Length);
        }

        if (IsTiff(b))
        {
            return Make(ContainerFormat.Tiff, null, id3Length);
        }

        if (b.Length >= 12 && (b.StartsWith("RIFF"u8) || b.StartsWith("RF64"u8) || b.StartsWith("BW64"u8)))
        {
            ReadOnlySpan<byte> form = b.Slice(8, 4);
            bool rf64 = !b.StartsWith("RIFF"u8);
            ContainerFormat format = form.SequenceEqual("WAVE"u8) ? (rf64 ? ContainerFormat.Rf64 : ContainerFormat.Wav)
                : rf64 ? ContainerFormat.Unknown
                : form.SequenceEqual("AVI "u8) ? ContainerFormat.Avi
                : form.SequenceEqual("WEBP"u8) ? ContainerFormat.WebP
                : ContainerFormat.Unknown;
            return format == ContainerFormat.Unknown ? null : Make(format, StructureBytes.Ascii(form), id3Length);
        }

        if (b.Length >= 8 && b.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return Make(ContainerFormat.IsoBmff, b.Length >= 12 ? StructureBytes.Ascii(b.Slice(8, 4)) : null, id3Length);
        }

        if (b.StartsWith(JxlContainer) || (b[0] == 0xFF && b[1] == 0x0A))
        {
            return Make(ContainerFormat.JpegXl, null, id3Length);
        }

        if (b.StartsWith(Jp2Signature) || b.StartsWith(J2kCodestream))
        {
            return Make(ContainerFormat.Jpeg2000, null, id3Length);
        }

        if (b.StartsWith(EbmlMagic))
        {
            return Make(ContainerFormat.Matroska, null, id3Length);
        }

        if (b.StartsWith("fLaC"u8))
        {
            return Make(ContainerFormat.Flac, null, id3Length);
        }

        if (b.StartsWith("OggS"u8))
        {
            return new SniffResult(ContainerFormat.Ogg, null, id3Length, IsTheora(source, offset) ? MediaKind.Video : MediaKind.Audio);
        }

        if (b.Length >= 16 ? b.StartsWith(AsfGuid) : b.Length >= 8 && AsfGuid[..8].SequenceEqual(b[..8]))
        {
            return Make(ContainerFormat.Asf, null, id3Length);
        }

        if (b.Length >= 12 && b.StartsWith("FORM"u8) && (b.Slice(8, 4).SequenceEqual("AIFF"u8) || b.Slice(8, 4).SequenceEqual("AIFC"u8)))
        {
            return Make(ContainerFormat.Aiff, StructureBytes.Ascii(b.Slice(8, 4)), id3Length);
        }

        ContainerFormat simple = SimpleSignature(b);
        if (simple != ContainerFormat.Unknown)
        {
            return Make(simple, null, id3Length);
        }

        if (IsQuickTimeAtom(b, source.Length - offset))
        {
            return Make(ContainerFormat.IsoBmff, null, id3Length);
        }

        if (IsMpegTs(source, offset))
        {
            return Make(ContainerFormat.MpegTs, null, id3Length);
        }

        if (IsMpegAudio(source, offset))
        {
            return Make(ContainerFormat.Mp3, null, id3Length);
        }

        return null;
    }

    private static ContainerFormat SimpleSignature(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b.StartsWith("FLV"u8) && b[3] == 0x01)
        {
            return ContainerFormat.Flv;
        }

        if (b.StartsWith(MpegPsMagic))
        {
            return ContainerFormat.MpegPs;
        }

        if (b.StartsWith("MAC "u8))
        {
            return ContainerFormat.Ape;
        }

        if (b.StartsWith("wvpk"u8))
        {
            return ContainerFormat.WavPack;
        }

        if (b.StartsWith("TTA1"u8))
        {
            return ContainerFormat.Tta;
        }

        if (b.StartsWith("MPCK"u8) || b.StartsWith("MP+"u8))
        {
            return ContainerFormat.Musepack;
        }

        if (b.StartsWith("DSD "u8))
        {
            return ContainerFormat.Dsf;
        }

        if (b.StartsWith("FRM8"u8))
        {
            return ContainerFormat.Dsdiff;
        }

        if (b.StartsWith("#!AMR"u8))
        {
            return ContainerFormat.Amr;
        }

        if (b.StartsWith("caff"u8))
        {
            return ContainerFormat.Caf;
        }

        if (b.StartsWith(".snd"u8))
        {
            return ContainerFormat.Au;
        }

        if (b.StartsWith(DtsCore) || b.StartsWith(DtsCoreLe) || b.StartsWith(Dts14Bit) || b.StartsWith(Dts14BitLe))
        {
            return ContainerFormat.Dts;
        }

        return IsAc3(b) ? ContainerFormat.Ac3 : ContainerFormat.Unknown;
    }

    private static SniffResult Make(ContainerFormat format, string? brand, long id3Length) =>
        new(format, brand, id3Length, FormatKinds.KindHint(format, brand));

    /// <summary><c>BM</c> with a known DIB header size, so that two bytes alone do not decide.</summary>
    private static bool IsBmp(ReadOnlySpan<byte> b)
    {
        if (b.Length < 18 || b[0] != (byte)'B' || b[1] != (byte)'M')
        {
            return false;
        }

        uint dib = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(14, 4));
        return dib is 12 or 16 or 40 or 52 or 56 or 64 or 108 or 124;
    }

    /// <summary>Classic and Big TIFF in both byte orders, and the TIFF-based RAW variants with their own magic.</summary>
    private static bool IsTiff(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4)
        {
            return false;
        }

        return b.StartsWith("II*\0"u8) || b.StartsWith("MM\0*"u8)
            || b.StartsWith("II+\0"u8) || b.StartsWith("MM\0+"u8)
            || b.StartsWith("IIRO"u8) || b.StartsWith("IIRS"u8) || b.StartsWith("MMOR"u8)
            || b.StartsWith("IIU\0"u8)
            || b.StartsWith("FUJIFILMCCD-RAW"u8);
    }

    /// <summary>A QuickTime file without ftyp: a known atom at offset 4 with a plausible size.</summary>
    private static bool IsQuickTimeAtom(ReadOnlySpan<byte> b, long available)
    {
        if (b.Length < 8)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(b);
        ReadOnlySpan<byte> type = b.Slice(4, 4);
        if (type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8))
        {
            return size is 0 or 1 or >= 8;
        }

        return (type.SequenceEqual("wide"u8) || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8) || type.SequenceEqual("pnot"u8))
            && size >= 8 && size <= available;
    }

    /// <summary>AC-3 or E-AC-3 sync word with a valid sample-rate code, frame size and stream id.</summary>
    private static bool IsAc3(ReadOnlySpan<byte> b)
    {
        if (b.Length < 6 || b[0] != 0x0B || b[1] != 0x77)
        {
            return false;
        }

        int bsid = b[5] >> 3;
        if (bsid <= 10)
        {
            return (b[4] >> 6) != 3 && (b[4] & 0x3F) < 38;
        }

        return bsid <= 16 && (b[2] >> 6) != 3 && ((b[4] >> 6) != 3 || ((b[4] >> 4) & 3) != 3);
    }

    /// <summary>An ADTS AAC header: twelve sync bits, layer 0, a valid sampling-frequency index.</summary>
    private static bool IsAdts(ReadOnlySpan<byte> b) =>
        b.Length >= 7 && b[0] == 0xFF && (b[1] & 0xF6) == 0xF0 && ((b[2] >> 2) & 0xF) < 13;

    /// <summary>The first Ogg page begins a Theora stream.</summary>
    private static bool IsTheora(in Source source, long offset)
    {
        ReadOnlySpan<byte> page = source.Get(offset, 27 + 255 + TheoraHeader.Length);
        if (page.Length < 27)
        {
            return false;
        }

        int packet = 27 + page[26];
        return page.Length >= packet + TheoraHeader.Length && page.Slice(packet, TheoraHeader.Length).SequenceEqual(TheoraHeader);
    }

    /// <summary>0x47 every 188 bytes from 0, or every 192 bytes from 4 (M2TS); five packets, or all the file holds (at least two).</summary>
    private static bool IsMpegTs(in Source source, long offset)
    {
        ReadOnlySpan<byte> t = source.Get(offset, 4 + (TsPackets * 192));
        return Packets(t, source.Length - offset, 0, 188) || Packets(t, source.Length - offset, 4, 192);

        static bool Packets(ReadOnlySpan<byte> t, long available, int first, int stride)
        {
            int present = 0;
            for (int i = 0; i < TsPackets; i++)
            {
                int position = first + (i * stride);
                if (position >= available)
                {
                    break;
                }

                if (position >= t.Length || t[position] != 0x47)
                {
                    return false;
                }

                present++;
            }

            return present >= 2;
        }
    }

    /// <summary>A valid MPEG audio frame header confirmed by a compatible one at the computed distance.</summary>
    private static bool IsMpegAudio(in Source source, long offset)
    {
        ReadOnlySpan<byte> first = source.Get(offset, 4);
        if (first.Length < 4 || !MpegAudioFrame.TryParse(BinaryPrimitives.ReadUInt32BigEndian(first), out MpegAudioFrame frame))
        {
            return false;
        }

        long next = offset + frame.FrameLength;
        if (next == source.Length)
        {
            return true;
        }

        ReadOnlySpan<byte> second = source.Get(next, 4);
        if (second.Length < 4)
        {
            return false;
        }

        return (MpegAudioFrame.TryParse(BinaryPrimitives.ReadUInt32BigEndian(second), out MpegAudioFrame following) && frame.IsCompatible(following))
            || second.StartsWith("TAG"u8);
    }

    /// <summary>The head buffer, with <c>readAt</c> for what lies beyond it.</summary>
    private readonly ref struct Source
    {
        private readonly ReadOnlySpan<byte> _head;
        private readonly Func<long, int, byte[]>? _readAt;

        public Source(ReadOnlySpan<byte> head, long length, Func<long, int, byte[]>? readAt)
        {
            _head = head;
            Length = length;
            _readAt = readAt;
        }

        public long Length { get; }

        /// <summary>Up to <paramref name="count"/> bytes at <paramref name="offset"/>; fewer at the end of the file or when they cannot be read.</summary>
        public ReadOnlySpan<byte> Get(long offset, int count)
        {
            if (offset < 0 || count <= 0 || offset >= Length)
            {
                return default;
            }

            if (count > Length - offset)
            {
                count = (int)(Length - offset);
            }

            if (offset + count <= _head.Length)
            {
                return _head.Slice((int)offset, count);
            }

            if (_readAt is null)
            {
                return offset < _head.Length ? _head[(int)offset..] : default;
            }

            byte[]? bytes = _readAt(offset, count);
            if (bytes is null)
            {
                return default;
            }

            return bytes.Length > count ? bytes.AsSpan(0, count) : bytes;
        }
    }
}
