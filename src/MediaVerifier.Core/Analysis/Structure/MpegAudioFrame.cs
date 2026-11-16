namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>A parsed MPEG audio frame header (MPEG 1, 2 and 2.5; layers I to III).</summary>
/// <param name="Version">1, 2, or 25 for MPEG 2.5.</param>
/// <param name="Layer">1 to 3.</param>
/// <param name="Bitrate">In bit/s.</param>
/// <param name="SampleRate">In Hz.</param>
/// <param name="Padding">The frame carries one padding slot.</param>
/// <param name="ChannelMode">0 stereo, 1 joint stereo, 2 dual channel, 3 mono.</param>
internal readonly record struct MpegAudioFrame(int Version, int Layer, int Bitrate, int SampleRate, bool Padding, int ChannelMode)
{
    private static readonly short[] Mpeg1Layer1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly short[] Mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly short[] Mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly short[] Mpeg2Layer1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly short[] Mpeg2Layer23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000];

    public int Channels => ChannelMode == 3 ? 1 : 2;

    public int SamplesPerFrame => Layer switch
    {
        1 => 384,
        2 => 1152,
        _ => Version == 1 ? 1152 : 576,
    };

    /// <summary>Frame length in bytes, header included.</summary>
    public int FrameLength => Layer == 1
        ? ((12 * Bitrate / SampleRate) + (Padding ? 1 : 0)) * 4
        : (SamplesPerFrame / 8 * Bitrate / SampleRate) + (Padding ? 1 : 0);

    /// <summary>The average frame length at this bitrate (padding slots spread evenly).</summary>
    public double AverageFrameLength => SamplesPerFrame / 8.0 * Bitrate / SampleRate;

    /// <summary>
    /// Parses a big-endian header word with FFmpeg's validity rules (<c>ff_mpa_check_header</c>); free-format
    /// frames are rejected because their length cannot be computed.
    /// </summary>
    public static bool TryParse(uint header, out MpegAudioFrame frame)
    {
        frame = default;
        if ((header & 0xFFE00000) != 0xFFE00000)
        {
            return false;
        }

        int versionBits = (int)(header >> 19) & 3;
        int layerBits = (int)(header >> 17) & 3;
        int bitrateIndex = (int)(header >> 12) & 0xF;
        int rateIndex = (int)(header >> 10) & 3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3)
        {
            return false;
        }

        int version = versionBits switch { 3 => 1, 2 => 2, _ => 25 };
        int layer = 4 - layerBits;
        short[] table = (version, layer) switch
        {
            (1, 1) => Mpeg1Layer1,
            (1, 2) => Mpeg1Layer2,
            (1, _) => Mpeg1Layer3,
            (_, 1) => Mpeg2Layer1,
            _ => Mpeg2Layer23,
        };

        int sampleRate = Mpeg1Rates[rateIndex] >> (version switch { 1 => 0, 2 => 1, _ => 2 });
        frame = new MpegAudioFrame(version, layer, table[bitrateIndex] * 1000, sampleRate, ((header >> 9) & 1) != 0, (int)(header >> 6) & 3);
        return true;
    }

    /// <summary>Same version, layer and sample rate: a frame that can follow this one in a stream.</summary>
    public bool IsCompatible(MpegAudioFrame other) =>
        other.Version == Version && other.Layer == Layer && other.SampleRate == SampleRate;
}
