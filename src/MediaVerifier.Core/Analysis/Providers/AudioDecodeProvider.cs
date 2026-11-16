using System.Buffers.Binary;
using System.Globalization;
using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MediaVerifier.Core.Analysis.Structure;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Analysis.Providers;

/// <summary>What the FLAC MD5 comparison concluded.</summary>
public enum Md5Verdict : byte
{
    /// <summary>Not a FLAC file, or nothing was decoded.</summary>
    NotApplicable = 0,

    /// <summary>The decoded audio matches the MD5 in STREAMINFO.</summary>
    Match = 1,

    /// <summary>All samples decoded and the MD5 differs: the audio changed after it was encoded.</summary>
    Mismatch = 2,

    /// <summary>The encoder stored no MD5 (all zeros).</summary>
    NotStored = 3,

    /// <summary>Fewer samples decoded than STREAMINFO declares; the truncation and decode rules cover it.</summary>
    Incomplete = 4,
}

/// <summary>Audio decode result (subplan 07, Audio — Standard and Deep).</summary>
public sealed record AudioDecodeArtifact(
    RunOutcome Outcome,
    bool IdleTimeout,
    LogDigest Log,
    bool Opened,
    long SampleFrames,
    int SampleRate,
    int Channels,
    Md5Verdict Md5) : IArtifact
{
    [JsonIgnore]
    public double? DecodedSeconds => Opened && SampleRate > 0 ? (double)SampleFrames / SampleRate : null;
}

/// <summary>
/// One streaming consumer of the decoded audio (subplan 10, Analyzer pipeline). Blocks hold whole sample
/// frames, interleaved, as 32-bit float; the span is valid only during the call. Analyzers must not
/// allocate per block.
/// </summary>
public interface IPcmAnalyzer
{
    void Begin(WavFormat format);

    void Process(ReadOnlySpan<float> interleaved);

    void End();
}

/// <summary>Counts decoded sample frames (task C-07).</summary>
public sealed class SampleCounter : IPcmAnalyzer
{
    private int _channels = 1;

    public long Frames { get; private set; }

    public void Begin(WavFormat format) => _channels = Math.Max(1, format.Channels);

    public void Process(ReadOnlySpan<float> interleaved) => Frames += interleaved.Length / _channels;

    public void End()
    {
    }
}

/// <summary>
/// Recomputes the FLAC STREAMINFO MD5 from the float stream (task C-07): each sample times 2^(bits−1) is
/// the original integer exactly for 24 or fewer bits; it is packed little-endian in ceil(bits / 8) bytes,
/// interleaved, as libFLAC does. Verified equal to <c>-c:a pcm_s16le -f md5</c> and <c>pcm_s24le</c>.
/// </summary>
public sealed class FlacMd5Analyzer : IPcmAnalyzer, IDisposable
{
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly int _bits;
    private readonly int _bytesPerSample;
    private readonly float _scale;
    private float[] _scaled = [];
    private int[] _integers = [];
    private byte[] _packed = [];

    public FlacMd5Analyzer(int bitsPerSample)
    {
        if (bitsPerSample is < 4 or > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Only 4 to 24 bits are exact in 32-bit float.");
        }

        _bits = bitsPerSample;
        _bytesPerSample = (bitsPerSample + 7) / 8;
        _scale = MathF.ScaleB(1f, bitsPerSample - 1);
    }

    /// <summary>The MD5 in lower-case hex after <see cref="End"/>.</summary>
    public string? Md5Hex { get; private set; }

    public void Begin(WavFormat format)
    {
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int count = interleaved.Length;
        if (_scaled.Length < count)
        {
            _scaled = new float[count];
            _integers = new int[count];
            _packed = new byte[count * _bytesPerSample];
        }

        Span<float> scaled = _scaled.AsSpan(0, count);
        Span<int> integers = _integers.AsSpan(0, count);
        TensorPrimitives.Multiply(interleaved, _scale, scaled);
        TensorPrimitives.ConvertTruncating<float, int>(scaled, integers);
        int max = (1 << (_bits - 1)) - 1;
        int min = -(1 << (_bits - 1));
        Span<byte> packed = _packed.AsSpan(0, count * _bytesPerSample);
        switch (_bytesPerSample)
        {
            case 1:
                for (int i = 0; i < count; i++)
                {
                    packed[i] = (byte)(sbyte)Math.Clamp(integers[i], min, max);
                }

                break;
            case 2:
                for (int i = 0; i < count; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(packed[(i * 2)..], (short)Math.Clamp(integers[i], min, max));
                }

                break;
            default:
                for (int i = 0, o = 0; i < count; i++, o += 3)
                {
                    int value = Math.Clamp(integers[i], min, max);
                    packed[o] = (byte)value;
                    packed[o + 1] = (byte)(value >> 8);
                    packed[o + 2] = (byte)(value >> 16);
                }

                break;
        }

        _md5.AppendData(packed);
    }

    public void End() => Md5Hex = Convert.ToHexStringLower(_md5.GetHashAndReset());

    public void Dispose() => _md5.Dispose();
}

/// <summary>
/// Decodes the first audio stream to 32-bit float WAV on stdout and streams it through the analyzers (task
/// C-07). One FFmpeg process per file; the probe runs concurrently. 32-bit FLAC gets its MD5 from a second
/// <c>pcm_s32le</c> pass because float cannot hold those samples exactly.
/// </summary>
public sealed class AudioDecodeProvider() : ArtifactProvider<AudioDecodeArtifact>(new ArtifactCodec<AudioDecodeArtifact>(ArtifactKind.AudioDecode, ProviderJsonContext.Default.AudioDecodeArtifact))
{
    /// <summary>Samples per block handed to the analyzers (about 46 ms of stereo audio at 44.1 kHz).</summary>
    private const int BlockSamples = 4096;

    public override int Version => 1;

    public override ScanDepth MinDepth => ScanDepth.Standard;

    public override bool Applies(FileContext file) => file.Kind == MediaKind.Audio;

    protected override async Task<AudioDecodeArtifact> ProduceTypedAsync(FileContext file)
    {
        StructureArtifact? structure = await file.GetAsync<StructureArtifact>().ConfigureAwait(false);
        FlacInfo? flac = structure?.Flac;
        SampleCounter counter = new();
        using FlacMd5Analyzer? md5 = flac is { Md5Hex: not null, BitsPerSample: <= 24 and >= 4 } ? new FlacMd5Analyzer(flac.BitsPerSample) : null;
        List<IPcmAnalyzer> analyzers = [counter];
        if (md5 is not null)
        {
            analyzers.Add(md5);
        }

        WavFormat? format = null;
        List<string> args = [.. FfmpegArgs.Decode(file.Settings.StrictDecoding && file.Depth == ScanDepth.Deep)];
        args.AddRange(["-i", file.FullPath, "-map", "0:a:0", "-map_metadata", "-1", "-fflags", "+bitexact", "-c:a", "pcm_f32le", "-f", "wav", "-"]);
        FfResult result = await file.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = args,
                Stdout = StdoutMode.Binary,
                WorkingDirectory = file.TempDirectory,
                IdleTimeout = TimeSpan.FromSeconds(file.Settings.StallSeconds),
                HardTimeout = FfmpegArgs.HardTimeout(file.Depth, DeclaredForTimeout(flac), file.Stat.Size),
                BinaryConsumer = async (stream, token) => format = await PumpAsync(stream, analyzers, token).ConfigureAwait(false),
            },
            file.Cancellation).ConfigureAwait(false);

        bool opened = format is not null;
        Md5Verdict verdict = Md5Verdict.NotApplicable;
        if (flac is not null && opened && result.Completed)
        {
            bool complete = flac.TotalSamples <= 0 || counter.Frames == flac.TotalSamples;
            if (flac.Md5Hex is null)
            {
                verdict = Md5Verdict.NotStored;
            }
            else if (!complete)
            {
                verdict = Md5Verdict.Incomplete;
            }
            else if (md5 is not null)
            {
                verdict = string.Equals(md5.Md5Hex, flac.Md5Hex, StringComparison.OrdinalIgnoreCase) ? Md5Verdict.Match : Md5Verdict.Mismatch;
            }
            else
            {
                string? wide = await Md5Of32BitAsync(file).ConfigureAwait(false);
                verdict = wide is null ? Md5Verdict.NotApplicable : string.Equals(wide, flac.Md5Hex, StringComparison.OrdinalIgnoreCase) ? Md5Verdict.Match : Md5Verdict.Mismatch;
            }
        }

        return new AudioDecodeArtifact(
            OutcomeMapping.From(result.Outcome),
            result.IdleTimeout,
            LogDigest.From(result.Log),
            opened,
            counter.Frames,
            format?.SampleRate ?? 0,
            format?.Channels ?? 0,
            verdict);
    }

    /// <summary>Reads the WAV stream to its end, feeding whole frames to every analyzer. Null when no audio arrived.</summary>
    internal static async Task<WavFormat?> PumpAsync(Stream stream, IReadOnlyList<IPcmAnalyzer> analyzers, CancellationToken token)
    {
        WavPipeReader reader;
        try
        {
            reader = await WavPipeReader.OpenAsync(stream, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // FFmpeg could not open the input or found no audio: stdout closed before a header.
            return null;
        }

        WavFormat format = reader.Format;
        int channels = Math.Max(1, format.Channels);
        foreach (IPcmAnalyzer analyzer in analyzers)
        {
            analyzer.Begin(format);
        }

        float[] block = new float[BlockSamples * channels];
        float[] buffer = new float[block.Length];
        int pending = 0;
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(block, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                int offset = 0;
                while (offset < read)
                {
                    int take = Math.Min(read - offset, buffer.Length - pending);
                    Array.Copy(block, offset, buffer, pending, take);
                    pending += take;
                    offset += take;
                    int whole = pending - (pending % channels);
                    if (whole == 0)
                    {
                        continue;
                    }

                    ReadOnlySpan<float> frames = buffer.AsSpan(0, whole);
                    foreach (IPcmAnalyzer analyzer in analyzers)
                    {
                        analyzer.Process(frames);
                    }

                    int rest = pending - whole;
                    if (rest > 0)
                    {
                        Array.Copy(buffer, whole, buffer, 0, rest);
                    }

                    pending = rest;
                }
            }
        }
        catch (IOException)
        {
            // The pipe broke (process killed on timeout or cancel): analyse what arrived.
        }
        finally
        {
            foreach (IPcmAnalyzer analyzer in analyzers)
            {
                analyzer.End();
            }
        }

        return format;
    }

    private static double? DeclaredForTimeout(FlacInfo? flac) =>
        flac is { TotalSamples: > 0, SampleRate: > 0 } ? (double)flac.TotalSamples / flac.SampleRate : null;

    /// <summary>The MD5 of 32-bit FLAC audio from <c>-c:a pcm_s32le -f md5</c>; null when the pass fails.</summary>
    private static async Task<string?> Md5Of32BitAsync(FileContext file)
    {
        FfResult result = await file.Runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = ["-i", file.FullPath, "-map", "0:a:0", "-c:a", "pcm_s32le", "-f", "md5", "-"],
                Stdout = StdoutMode.Text,
                WorkingDirectory = file.TempDirectory,
                IdleTimeout = TimeSpan.FromSeconds(file.Settings.StallSeconds),
                HardTimeout = FfmpegArgs.HardTimeout(ScanDepth.Standard, null, file.Stat.Size),
            },
            file.Cancellation).ConfigureAwait(false);
        string? text = result.Completed ? result.StdoutText?.Trim() : null;
        return text is not null && text.StartsWith("MD5=", StringComparison.Ordinal) ? text[4..].ToLower(CultureInfo.InvariantCulture) : null;
    }
}
