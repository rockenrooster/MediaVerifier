using System.Diagnostics;
using System.IO.Compression;
using System.Numerics.Tensors;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;

namespace MediaVerifier.Core.Analysis.Audio;

/// <summary>
/// The pictures behind an audio finding (subplan 10, Evidence views; task A-09): a spectrogram (STFT with
/// N = 2048, at most 1,024 columns holding the maximum of their frames, linear frequency to Nyquist,
/// −120 to 0 dB as bytes), a waveform envelope, and 20 ms of samples around each marked position for the
/// zoomed view.
/// </summary>
public sealed class AudioEvidence
{
    public const int FftSize = 2048;
    public const int MaxColumns = 1024;
    public const int Rows = 256;
    public const double FloorDb = -120;
    public const double WindowSeconds = 0.020;
    private const uint Magic = 0x5645_564D; // "MVEV"
    private const int FormatVersion = 1;

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required double Seconds { get; init; }

    /// <summary>Spectrogram columns of <see cref="Rows"/> bytes each, lowest band first: 0 is −120 dB or less, 255 is 0 dB.</summary>
    public required byte[] Spectrogram { get; init; }

    /// <summary>Per-column minimum over all channels, full scale.</summary>
    public required float[] WaveMin { get; init; }

    /// <summary>Per-column maximum over all channels, full scale.</summary>
    public required float[] WaveMax { get; init; }

    /// <summary>The marked positions in seconds (clipped runs, dropouts), in the order requested.</summary>
    public required double[] Marks { get; init; }

    /// <summary>For each mark, <see cref="WindowChannels"/> channels interleaved from 10 ms before it to 10 ms after.</summary>
    public required float[][] MarkWindows { get; init; }

    public required int WindowChannels { get; init; }

    public int SpectrogramColumns => Spectrogram.Length / Rows;

    /// <summary>The byte for a level in dB relative to a full-scale sine.</summary>
    public static byte Level(double db) => double.IsNaN(db) ? (byte)0 : (byte)Math.Clamp(Math.Round((db - FloorDb) * 255 / -FloorDb), 0, 255);

    /// <summary>The level in dB that a byte stands for.</summary>
    public static double Db(byte level) => FloorDb + (level * -FloorDb / 255);

    /// <summary>Writes the evidence compressed (format version 1, little-endian).</summary>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using ZLibStream zip = new(stream, CompressionLevel.Fastest, leaveOpen: true);
        using BinaryWriter writer = new(zip);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(SampleRate);
        writer.Write(Channels);
        writer.Write(Seconds);
        writer.Write(Spectrogram.Length);
        writer.Write(Spectrogram);
        writer.Write(WaveMin.Length);
        WriteFloats(writer, WaveMin);
        WriteFloats(writer, WaveMax);
        writer.Write(WindowChannels);
        writer.Write(Marks.Length);
        for (int i = 0; i < Marks.Length; i++)
        {
            writer.Write(Marks[i]);
            writer.Write(MarkWindows[i].Length);
            WriteFloats(writer, MarkWindows[i]);
        }
    }

    /// <summary>Reads evidence written by <see cref="Write"/>; null when the data is not in this format.</summary>
    public static AudioEvidence? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using ZLibStream zip = new(stream, CompressionMode.Decompress, leaveOpen: true);
            using BinaryReader reader = new(zip);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            int rate = reader.ReadInt32();
            int channels = reader.ReadInt32();
            double seconds = reader.ReadDouble();
            byte[] spectrogram = reader.ReadBytes(Bounded(reader.ReadInt32(), MaxColumns * Rows));
            int waveColumns = Bounded(reader.ReadInt32(), MaxColumns);
            float[] min = ReadFloats(reader, waveColumns);
            float[] max = ReadFloats(reader, waveColumns);
            int windowChannels = reader.ReadInt32();
            int marks = Bounded(reader.ReadInt32(), MarkWindowCollector.MaxMarks);
            double[] positions = new double[marks];
            float[][] windows = new float[marks][];
            for (int i = 0; i < marks; i++)
            {
                positions[i] = reader.ReadDouble();
                windows[i] = ReadFloats(reader, Bounded(reader.ReadInt32(), 2 * 192_000));
            }

            return spectrogram.Length % Rows != 0 ? null : new AudioEvidence
            {
                SampleRate = rate,
                Channels = channels,
                Seconds = seconds,
                Spectrogram = spectrogram,
                WaveMin = min,
                WaveMax = max,
                Marks = positions,
                MarkWindows = windows,
                WindowChannels = windowChannels,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    private static int Bounded(int value, int max) => value < 0 || value > max ? throw new InvalidDataException("Evidence size out of range.") : value;

    // BinaryWriter and BinaryReader always use little-endian, and ReadSingle waits for all four bytes
    // (a decompressing stream may return fewer than asked for).
    private static void WriteFloats(BinaryWriter writer, float[] values)
    {
        foreach (float value in values)
        {
            writer.Write(value);
        }
    }

    private static float[] ReadFloats(BinaryReader reader, int count)
    {
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadSingle();
        }

        return values;
    }
}

/// <summary>
/// The spectrogram of <see cref="AudioEvidence"/>: Hann frames of 2,048 samples without overlap. Each
/// column keeps the maximum of its frames per band; whenever the column count would pass 1,024, pairs of
/// columns merge and each new column takes twice as many frames, so any length fits without knowing it in
/// advance. Two channels go through one complex FFT (left as the real part, right as the imaginary part);
/// the level is the mean of their powers, 0 dB for a full-scale sine in both.
/// </summary>
public sealed class SpectrogramBuilder : IPcmAnalyzer
{
    private const int N = AudioEvidence.FftSize;
    private const int BinsPerRow = N / 2 / AudioEvidence.Rows;
    private readonly Fft _fft = new(N);
    private readonly float[] _window = new float[N];
    private readonly float[] _a = new float[N];
    private readonly float[] _b = new float[N];
    private readonly float[] _re = new float[N];
    private readonly float[] _im = new float[N];
    private readonly float[] _power = new float[N];
    private readonly byte[] _columns = new byte[AudioEvidence.MaxColumns * AudioEvidence.Rows];
    private int _channels = 1;
    private int _fill;
    private int _column;
    private int _framesInColumn;
    private int _framesPerColumn = 1;

    public SpectrogramBuilder()
    {
        for (int i = 0; i < N; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / N)));
        }
    }

    /// <summary>The finished columns after <see cref="End"/>.</summary>
    public byte[] Result { get; private set; } = [];

    public void Begin(WavFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _channels = Math.Max(1, format.Channels);
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        bool stereo = _channels >= 2;
        for (int f = 0; f < frames; f++)
        {
            int offset = f * _channels;
            _a[_fill] = interleaved[offset];
            _b[_fill] = stereo ? interleaved[offset + 1] : 0;
            if (++_fill == N)
            {
                Transform(stereo);
                _fill = 0;
            }
        }
    }

    public void End()
    {
        int columns = _column + (_framesInColumn > 0 ? 1 : 0);
        Result = _columns.AsSpan(0, columns * AudioEvidence.Rows).ToArray();
    }

    private void Transform(bool stereo)
    {
        TensorPrimitives.Multiply(_a, _window, _re);
        if (stereo)
        {
            TensorPrimitives.Multiply(_b, _window, _im);
        }
        else
        {
            Array.Clear(_im);
        }

        _fft.Transform(_re, _im);

        // Power per bin, normalised so that a full-scale sine gives 1 (|X| = N / 4 with a Hann window).
        const float scale = 16f / ((float)N * N);
        for (int k = 0; k <= N / 2; k++)
        {
            float p = (_re[k] * _re[k]) + (_im[k] * _im[k]);
            if (stereo)
            {
                int m = (N - k) & (N - 1);
                p = (p + (_re[m] * _re[m]) + (_im[m] * _im[m])) / 4;
            }

            _power[k] = p * scale;
        }

        if (_column == AudioEvidence.MaxColumns)
        {
            Merge();
        }

        Span<byte> column = _columns.AsSpan(_column * AudioEvidence.Rows, AudioEvidence.Rows);
        for (int r = 0; r < AudioEvidence.Rows; r++)
        {
            int count = r == AudioEvidence.Rows - 1 ? BinsPerRow + 1 : BinsPerRow;
            float peak = TensorPrimitives.Max(_power.AsSpan(r * BinsPerRow, count));
            byte level = peak > 0 ? AudioEvidence.Level(10 * Math.Log10(peak)) : (byte)0;
            if (level > column[r])
            {
                column[r] = level;
            }
        }

        if (++_framesInColumn == _framesPerColumn)
        {
            _framesInColumn = 0;
            _column++;
        }
    }

    /// <summary>Halves the column count by keeping the maximum of each pair.</summary>
    private void Merge()
    {
        const int rows = AudioEvidence.Rows;
        for (int c = 0; c < AudioEvidence.MaxColumns / 2; c++)
        {
            Span<byte> target = _columns.AsSpan(c * rows, rows);
            ReadOnlySpan<byte> first = _columns.AsSpan(2 * c * rows, rows);
            ReadOnlySpan<byte> second = _columns.AsSpan(((2 * c) + 1) * rows, rows);
            TensorPrimitives.Max(first, second, target);
        }

        Array.Clear(_columns, AudioEvidence.MaxColumns / 2 * rows, AudioEvidence.MaxColumns / 2 * rows);
        _column = AudioEvidence.MaxColumns / 2;
        _framesPerColumn *= 2;
    }
}

/// <summary>
/// The waveform envelope of <see cref="AudioEvidence"/>: minimum and maximum over all channels per column,
/// starting at 64 sample frames per column and merging pairs whenever the count would pass 1,024.
/// </summary>
public sealed class WaveformBuilder : IPcmAnalyzer
{
    private const int Limit = AudioEvidence.MaxColumns;
    private readonly float[] _min = new float[Limit];
    private readonly float[] _max = new float[Limit];
    private int _channels = 1;
    private int _column;
    private int _framesInColumn;
    private int _framesPerColumn = 64;

    public float[] Min { get; private set; } = [];

    public float[] MaxValues { get; private set; } = [];

    public void Begin(WavFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _channels = Math.Max(1, format.Channels);
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        int f = 0;
        while (f < frames)
        {
            if (_column == Limit)
            {
                Merge();
            }

            int take = Math.Min(frames - f, _framesPerColumn - _framesInColumn);
            ReadOnlySpan<float> chunk = interleaved.Slice(f * _channels, take * _channels);
            float low = TensorPrimitives.Min(chunk);
            float high = TensorPrimitives.Max(chunk);
            if (_framesInColumn == 0)
            {
                _min[_column] = low;
                _max[_column] = high;
            }
            else
            {
                _min[_column] = Math.Min(_min[_column], low);
                _max[_column] = Math.Max(_max[_column], high);
            }

            _framesInColumn += take;
            f += take;
            if (_framesInColumn == _framesPerColumn)
            {
                _framesInColumn = 0;
                _column++;
            }
        }
    }

    public void End()
    {
        int columns = _column + (_framesInColumn > 0 ? 1 : 0);
        Min = _min.AsSpan(0, columns).ToArray();
        MaxValues = _max.AsSpan(0, columns).ToArray();
    }

    private void Merge()
    {
        for (int c = 0; c < Limit / 2; c++)
        {
            _min[c] = Math.Min(_min[2 * c], _min[(2 * c) + 1]);
            _max[c] = Math.Max(_max[2 * c], _max[(2 * c) + 1]);
        }

        _column = Limit / 2;
        _framesPerColumn *= 2;
    }
}

/// <summary>Copies 20 ms of the first two channels around each marked position (the waveform's zoomed view).</summary>
public sealed class MarkWindowCollector(IReadOnlyList<double> marks) : IPcmAnalyzer
{
    public const int MaxMarks = 40;
    private readonly double[] _marks = [.. marks.Take(MaxMarks)];
    private long[] _starts = [];
    private int _length;
    private int _channels = 1;
    private long _position;

    public int WindowChannels { get; private set; } = 1;

    public float[][] Windows { get; private set; } = [];

    public double[] Marks => _marks;

    public void Begin(WavFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        _channels = Math.Max(1, format.Channels);
        WindowChannels = Math.Min(2, _channels);
        _length = Math.Max(1, (int)Math.Round(AudioEvidence.WindowSeconds * format.SampleRate));
        _starts = [.. _marks.Select(m => Math.Max(0, (long)Math.Round(m * format.SampleRate) - (_length / 2)))];
        Windows = [.. _marks.Select(_ => new float[_length * WindowChannels])];
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        for (int i = 0; i < _starts.Length; i++)
        {
            long from = Math.Max(_starts[i], _position);
            long to = Math.Min(_starts[i] + _length, _position + frames);
            for (long frame = from; frame < to; frame++)
            {
                int source = (int)(frame - _position) * _channels;
                int target = (int)(frame - _starts[i]) * WindowChannels;
                for (int c = 0; c < WindowChannels; c++)
                {
                    Windows[i][target + c] = interleaved[source + c];
                }
            }
        }

        _position += frames;
    }

    public void End()
    {
    }
}

/// <summary>
/// Produces <see cref="AudioEvidence"/> on demand (subplan 10, Evidence views): one decode by the bundled
/// FFmpeg, kept in a disk cache keyed by file id and fingerprint. Never call it on the UI thread.
/// </summary>
public sealed class EvidenceRenderer(FfRunner runner, string cacheDirectory)
{
    /// <summary>Cached renders kept; the least recently written go first.</summary>
    public const int MaxCachedFiles = 200;

    public string CacheDirectory => cacheDirectory;

    /// <summary>Time taken by the last render that decoded (for the performance check).</summary>
    public TimeSpan LastRenderTime { get; private set; }

    /// <summary>The cache key for a file: its id and its fingerprint (or size and modification time without one).</summary>
    public static string CacheKey(long fileId, byte[]? fingerprint, long size, long mtimeUtc) =>
        fingerprint is { Length: > 0 }
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{fileId}-{Convert.ToHexStringLower(fingerprint)}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{fileId}-{size}-{mtimeUtc}");

    /// <summary>The evidence for a file, from the cache or a fresh decode; null when the file cannot be read or holds no audio.</summary>
    public async Task<AudioEvidence?> RenderAsync(string path, string cacheKey, IReadOnlyList<double> marks, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(marks);
        string file = Path.Combine(cacheDirectory, cacheKey + ".mvev");
        double[] wanted = [.. marks.Take(MarkWindowCollector.MaxMarks)];
        AudioEvidence? cached = TryRead(file);
        if (cached is not null && cached.Marks.AsSpan().SequenceEqual(wanted))
        {
            return cached;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        Stopwatch watch = Stopwatch.StartNew();
        SampleCounter counter = new();
        SpectrogramBuilder spectrogram = new();
        WaveformBuilder waveform = new();
        MarkWindowCollector windows = new(wanted);
        WavFormat? format = null;
        IPcmAnalyzer[] analyzers = [counter, spectrogram, waveform, windows];
        await runner.RunAsync(
            new FfRequest
            {
                Tool = FfTool.Ffmpeg,
                Arguments = ["-i", path, "-map", "0:a:0", "-map_metadata", "-1", "-c:a", "pcm_f32le", "-f", "wav", "-"],
                Stdout = StdoutMode.Binary,
                Priority = ProcessPriorityClass.Normal,
                IdleTimeout = TimeSpan.FromSeconds(30),
                HardTimeout = TimeSpan.FromMinutes(10),
                BinaryConsumer = async (stream, t) => format = await AudioDecodeProvider.PumpAsync(stream, analyzers, t).ConfigureAwait(false),
            },
            token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (format is null || counter.Frames == 0)
        {
            return null;
        }

        AudioEvidence evidence = new()
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            Seconds = (double)counter.Frames / Math.Max(1, format.SampleRate),
            Spectrogram = spectrogram.Result,
            WaveMin = waveform.Min,
            WaveMax = waveform.MaxValues,
            Marks = windows.Marks,
            MarkWindows = windows.Windows,
            WindowChannels = windows.WindowChannels,
        };
        LastRenderTime = watch.Elapsed;
        Save(file, evidence);
        return evidence;
    }

    private static AudioEvidence? TryRead(string file)
    {
        try
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return AudioEvidence.Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(string file, AudioEvidence evidence)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            string temp = file + ".tmp";
            using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                evidence.Write(stream);
            }

            File.Move(temp, file, overwrite: true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation; the evidence is still shown.
        }
    }

    private void Prune()
    {
        FileInfo[] files = new DirectoryInfo(cacheDirectory).GetFiles("*.mvev");
        if (files.Length <= MaxCachedFiles)
        {
            return;
        }

        foreach (FileInfo old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxCachedFiles))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use by another reader; the next render prunes it.
            }
        }
    }
}
