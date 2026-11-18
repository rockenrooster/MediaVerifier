using System.Buffers.Binary;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Ffmpeg;

public sealed class ParsingTests
{
    public static TheoryData<string> CorpusFiles()
    {
        TheoryData<string> data = [];
        foreach (string file in Directory.EnumerateFiles(RepositoryPaths.Combine("tests", "MediaVerifier.Tests", "Data", "logs"), "*.txt").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void EveryCorpusLineMapsToItsExpectedKind(string scenario)
    {
        string folder = RepositoryPaths.Combine("tests", "MediaVerifier.Tests", "Data", "logs");
        string[] lines = File.ReadAllLines(Path.Combine(folder, scenario + ".txt"));
        string[] kinds = File.ReadAllLines(Path.Combine(folder, scenario + ".kinds"));
        Assert.Equal(lines.Length, kinds.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            LogLine line = LogClassifier.Classify(lines[i]);
            Assert.True(Enum.Parse<LogSignalKind>(kinds[i]) == line.Kind, $"{scenario}:{i + 1} '{lines[i]}' gave {line.Kind}, expected {kinds[i]}");
        }
    }

    [Fact]
    public void TheCorpusExercisesEverySignalKind()
    {
        string folder = RepositoryPaths.Combine("tests", "MediaVerifier.Tests", "Data", "logs");
        HashSet<string> seen = [.. Directory.EnumerateFiles(folder, "*.kinds").SelectMany(File.ReadAllLines)];
        foreach (LogSignalKind kind in Enum.GetValues<LogSignalKind>())
        {
            Assert.Contains(kind.ToString(), seen);
        }
    }

    [Fact]
    public void LinesSplitIntoContextsLevelAndMessage()
    {
        LogLine line = LogClassifier.Classify("[vist#0:3/h264 @ 0000018f84263e00] [dec:h264 @ 0000018f845909c0] [warning] corrupt decoded frame");
        Assert.Equal(["vist#0:3/h264", "dec:h264"], line.Contexts);
        Assert.Equal(FfLogLevel.Warning, line.Level);
        Assert.Equal("corrupt decoded frame", line.Message);
        Assert.Equal(3, line.StreamIndex);
        Assert.Equal("Invalid NAL unit size (N > N).", LogClassifier.Normalize("Invalid NAL unit size (12345 > 6789)."));
    }

    [Fact]
    public void CollectorKeepsBoundedSamples()
    {
        LogCollector collector = new();
        for (int i = 0; i < 100_000; i++)
        {
            collector.Add($"[h264 @ 0000018f845909c0] [error] error while decoding MB {i % 120} {i % 68}, bytestream -{i % 9}");
        }

        LogSummary summary = collector.ToSummary();
        Assert.Equal(100_000, summary.TotalLines);
        Assert.Equal(200, summary.Head.Count);
        Assert.Equal(50, summary.Tail.Count);
        SignalSummary errors = summary.Kinds[LogSignalKind.DecodeError];
        Assert.Equal(100_000, errors.Count);
        (string message, int count) = Assert.Single(errors.TopMessages);
        Assert.Equal("error while decoding MB N N, bytestream -N", message);
        Assert.Equal(100_000, count);
        Assert.Equal(100_000, summary.WeightedDecodeErrors);
    }

    [Fact]
    public void ProgressStreamParsesAndToleratesACutBlock()
    {
        const string stream =
            "frame=96\nfps=0.00\nstream_0_0_q=-0.0\nbitrate=N/A\ntotal_size=N/A\nout_time_us=4000000\nout_time_ms=4000000\nout_time=00:00:04.000000\ndup_frames=0\ndrop_frames=0\nspeed=48.2x\nprogress=continue\n" +
            "frame=144\nout_time_us=6000000\ndup_frames=1\ndrop_frames=2\nspeed=50x\nprogress=end\n";
        ProgressParser parser = ProgressParser.ParseAll(stream);
        Assert.Equal(2, parser.Blocks);
        Assert.True(parser.Final!.IsEnd);
        Assert.Equal(144, parser.Final.Frame);
        Assert.Equal(6.0, parser.Final.OutTimeSeconds);
        Assert.Equal(2, parser.Final.DropFrames);
        Assert.Equal(50, parser.Final.Speed);

        ProgressParser cut = ProgressParser.ParseAll(stream[..(stream.IndexOf("frame=144", StringComparison.Ordinal) + 20)]);
        Assert.Equal(1, cut.Blocks);
        Assert.Equal(96, cut.Final!.Frame);
        Assert.False(cut.Final.IsEnd);
    }

    [Fact]
    public async Task WavReaderAcceptsPlainFloat()
    {
        byte[] wav = Wav(tag: 3, channels: 2, rate: 44100, bits: 32, samples: [0.5f, -0.25f, 1.5f, 0f], list: false);
        WavPipeReader reader = await WavPipeReader.OpenAsync(new MemoryStream(wav));
        Assert.Equal(new WavFormat(2, 44100, 32), reader.Format);
        float[] buffer = new float[16];
        int read = await reader.ReadAsync(buffer);
        Assert.Equal(4, read);
        Assert.Equal([0.5f, -0.25f, 1.5f, 0f], buffer[..4]);
        Assert.Equal(0, await reader.ReadAsync(buffer));
    }

    [Fact]
    public async Task WavReaderSkipsAListChunkAndAcceptsExtensibleSixChannels()
    {
        byte[] wav = Wav(tag: 0xFFFE, channels: 6, rate: 48000, bits: 32, samples: [1, 2, 3, 4, 5, 6], list: true);
        WavPipeReader reader = await WavPipeReader.OpenAsync(new MemoryStream(wav));
        Assert.Equal(6, reader.Format.Channels);
        float[] buffer = new float[6];
        Assert.Equal(6, await reader.ReadAsync(buffer));
        Assert.Equal(6f, buffer[5]);
    }

    [Fact]
    public async Task WavReaderDropsAPartialSampleAtATruncatedEnd()
    {
        byte[] wav = Wav(tag: 3, channels: 1, rate: 8000, bits: 32, samples: [0.1f, 0.2f], list: false);
        MemoryStream cut = new(wav[..^2]);
        WavPipeReader reader = await WavPipeReader.OpenAsync(cut);
        float[] buffer = new float[8];
        Assert.Equal(1, await reader.ReadAsync(buffer));
        Assert.Equal(0, await reader.ReadAsync(buffer));
        await Assert.ThrowsAsync<InvalidDataException>(() => WavPipeReader.OpenAsync(new MemoryStream(wav[..20])));
    }

    [Fact]
    public async Task WavReaderRejectsIntegerPcm()
    {
        byte[] wav = Wav(tag: 1, channels: 2, rate: 44100, bits: 16, samples: [], list: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => WavPipeReader.OpenAsync(new MemoryStream(wav)));
    }

    [Fact]
    public void StandardArgumentsDifferOnlyByNostdin()
    {
        Assert.Equal(["-hide_banner", "-nostdin", "-loglevel", "repeat+level+warning", "-protocol_whitelist", "file,pipe"], FfmpegArgs.Standard(FfTool.Ffmpeg));
        Assert.Equal(["-hide_banner", "-loglevel", "repeat+level+warning", "-protocol_whitelist", "file,pipe"], FfmpegArgs.Standard(FfTool.Ffprobe));
        Assert.Equal(["-max_error_rate", "1", "-err_detect", "crccheck+bitstream"], FfmpegArgs.Decode());
        Assert.Equal("crccheck+bitstream+careful+compliant", FfmpegArgs.Decode(strict: true)[3]);
    }

    [Fact]
    public void PhotosGetAnExplicitDemuxer()
    {
        Assert.Equal(["-f", "jpeg_pipe", "-i", @"C:\p\a%03d.jpg"], FfmpegArgs.Input(@"C:\p\a%03d.jpg", MediaKind.Photo, "jpeg"));
        Assert.Equal(["-f", "image2", "-pattern_type", "none", "-i", @"C:\p\x%d.raw"], FfmpegArgs.Input(@"C:\p\x%d.raw", MediaKind.Photo, null));
        Assert.Equal(["-i", @"C:\p\photo.cr2"], FfmpegArgs.Input(@"C:\p\photo.cr2", MediaKind.Photo, null));
        Assert.Equal(["-i", @"C:\v\a%03d.mkv"], FfmpegArgs.Input(@"C:\v\a%03d.mkv", MediaKind.Video, null));
    }

    [Fact]
    public void HardTimeoutsFollowSubplan04()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), FfmpegArgs.HardTimeout(ScanDepth.Standard, 10, 0));
        Assert.Equal(TimeSpan.FromSeconds(400), FfmpegArgs.HardTimeout(ScanDepth.Standard, 100, 0));
        Assert.Equal(TimeSpan.FromSeconds(600), FfmpegArgs.HardTimeout(ScanDepth.Deep, 10, 0));
        Assert.Equal(TimeSpan.FromSeconds(2000), FfmpegArgs.HardTimeout(ScanDepth.Deep, 100, 0));
        Assert.Equal(TimeSpan.FromSeconds(130), FfmpegArgs.HardTimeout(ScanDepth.Standard, null, 10L * 1024 * 1024));
    }

    [Fact]
    public void ProtocolListIsParsed()
    {
        const string text = "Supported file protocols:\nInput:\n  file\n  pipe\nOutput:\n  file\n  pipe\n";
        (IReadOnlyList<string> input, IReadOnlyList<string> output) = BundleIntegrity.ParseProtocols(text);
        Assert.Equal(["file", "pipe"], input);
        Assert.Equal(["file", "pipe"], output);
    }

    [Fact]
    public void RationalParsesFrameRates()
    {
        Assert.Equal(new Rational(24000, 1001), Rational.Parse("24000/1001"));
        Assert.Equal(24.0, Rational.Parse("24/1")!.Value.Value);
        Assert.Null(Rational.Parse("0/0"));
        Assert.Null(Rational.Parse("N/A"));
    }

    private static byte[] Wav(ushort tag, int channels, int rate, int bits, float[] samples, bool list)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(0xFFFFFFFF);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        bool extensible = tag == 0xFFFE;
        writer.Write(extensible ? 40 : 16);
        writer.Write(tag);
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bits / 8);
        writer.Write((ushort)(channels * bits / 8));
        writer.Write((ushort)bits);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)bits);
            writer.Write(0x3F);
            writer.Write(new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray());
        }

        if (list)
        {
            byte[] info = "INFOISFT\u000e\u0000\u0000\u0000Lavf62.3.100\u0000\u0000"u8.ToArray();
            writer.Write("LIST"u8);
            writer.Write(info.Length);
            writer.Write(info);
        }

        writer.Write("data"u8);
        writer.Write(0xFFFFFFFF);
        byte[] bytes = new byte[4];
        foreach (float sample in samples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, sample);
            writer.Write(bytes);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
