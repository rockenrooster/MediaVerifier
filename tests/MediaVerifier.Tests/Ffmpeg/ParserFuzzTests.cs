using System.Diagnostics;
using System.Text;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Ffmpeg;

/// <summary>
/// Task T-07 (subplan 15, Robustness tests): the WAV, progress, probe-JSON and log parsers and the filter-log parsers
/// get 10,000 random inputs and 10,000 mutated real ones each. They must return (or, for the WAV reader, reject with
/// <see cref="InvalidDataException"/>), never throw anything else and never take more than 200 ms a call.
/// The structural walkers have their own fuzz tests (StructureFuzzTests). CI runs the Fuzz category with a 60 s budget.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class ParserFuzzTests
{
    private const int Cases = 10_000;
    private static readonly TimeSpan CallBudget = TimeSpan.FromMilliseconds(200);

    private static readonly string[] ProgressKeys = ["frame", "fps", "out_time_us", "out_time_ms", "out_time", "dup_frames", "drop_frames", "speed", "progress", "bitrate", "total_size", "stream_0_0_q"];

    [Fact]
    public void Progress_lines_never_throw()
    {
        Random random = new(15_07_01);
        string real = string.Join('\n', Enumerable.Range(0, 3).Select(i => $"frame={48 * i}\nfps=0.0\nout_time_us={2_000_000 * i}\nout_time=00:00:0{2 * i}.000000\ndup_frames=0\ndrop_frames=0\nspeed={i}.5x\nprogress=continue")) + "\nprogress=end";
        for (int i = 0; i < Cases; i++)
        {
            string text = i % 2 == 0 ? RandomProgress(random) : Mutate(real, random);
            TimeBoxed(() =>
            {
                ProgressParser parser = new();
                foreach (string line in text.Split('\n'))
                {
                    parser.AddLine(line);
                }

                parser.Complete();
            }, i);
        }
    }

    [Fact]
    public void Probe_json_never_throws()
    {
        Random random = new(15_07_02);
        string[] real = [.. Directory.GetFiles(RepositoryPaths.Combine("tests", "MediaVerifier.Tests", "Data", "probe"), "*.json").Select(File.ReadAllText)];
        Assert.NotEmpty(real);
        for (int i = 0; i < Cases; i++)
        {
            string json = i % 2 == 0 ? RandomText(random, 0, 600, "{}[]\":,0123456789.-eE truefalsnul\\abcdxyz_") : Mutate(real[i % real.Length], random);
            TimeBoxed(() => _ = ProbeParser.Parse(json, LogSummary.Empty), i);
        }
    }

    [Fact]
    public void Log_lines_never_throw()
    {
        Random random = new(15_07_03);
        string[] real = [.. Directory.GetFiles(RepositoryPaths.Combine("tests", "MediaVerifier.Tests", "Data", "logs"), "*.txt").SelectMany(File.ReadAllLines).Where(l => l.Length > 0)];
        Assert.NotEmpty(real);
        for (int i = 0; i < Cases; i++)
        {
            string line = i % 2 == 0 ? RandomText(random, 0, 300, "[]@0x123456789abcdef: ()/=-_.%'\"\\\tabcdefghijklmnopqrstuvwxyzERROR") : Mutate(real[i % real.Length], random);
            TimeBoxed(() =>
            {
                _ = LogClassifier.Classify(line);
                _ = LogSummary.FromLines([line, line]);
            }, i);
        }
    }

    [Fact]
    public void Filter_logs_never_throw()
    {
        Random random = new(15_07_04);
        string ssim = "n:1 Y:0.991 U:0.995 V:0.995 All:0.993 (21.5)\nn:2 Y:inf U:nan V:1.0 All:0.95 (13.0)";
        string frame = "frame:0 pts:0 pts_time:0\nlavfi.siti.si=48.2\nlavfi.block=2.1\nlavfi.blur=3.4\nframe:1 pts:1 pts_time:0.04\nlavfi.siti.si=inf";
        string idet = "[Parsed_idet_0 @ 0x1] Multi frame detection: TFF:  178 BFF:    0 Progressive:   12 Undetermined:   10";
        for (int i = 0; i < Cases; i++)
        {
            string text = (i % 4) switch
            {
                0 => RandomText(random, 0, 300, "n:YUVAl0123456789.(), \nframe:pts_timelavfi.siti.sblockur=infa-"),
                1 => Mutate(ssim, random),
                2 => Mutate(frame, random),
                _ => Mutate(idet, random),
            };
            TimeBoxed(() =>
            {
                _ = FrameSampling.ParseSsim(text);
                _ = FrameSampling.ParseFrameLog(text);
                foreach (string line in text.Split('\n'))
                {
                    _ = InterlaceProvider.Parse(line);
                }
            }, i);
        }
    }

    [Fact]
    public async Task Wav_headers_are_read_or_rejected_cleanly()
    {
        Random random = new(15_07_05);
        byte[] real = Wav();
        float[] buffer = new float[4096];
        for (int i = 0; i < Cases; i++)
        {
            byte[] bytes = i % 2 == 0 ? RandomBytes(random, 0, 300) : MutateBytes(real, random);
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                WavPipeReader reader = await WavPipeReader.OpenAsync(new MemoryStream(bytes));
                Assert.InRange(reader.Format.Channels, 1, int.MaxValue);
                int reads = 0;
                while (await reader.ReadAsync(buffer) > 0)
                {
                    Assert.True(++reads < 10_000, $"case {i}: the reader does not reach the end");
                }
            }
            catch (InvalidDataException)
            {
                // The documented rejection of a stream that is not 32-bit float WAV.
            }

            Assert.True(watch.Elapsed < CallBudget || i < 5, $"case {i} took {watch.ElapsedMilliseconds} ms");
        }
    }

    private static void TimeBoxed(Action call, int index)
    {
        Stopwatch watch = Stopwatch.StartNew();
        call();
        Assert.True(watch.Elapsed < CallBudget || index < 5, $"case {index} took {watch.ElapsedMilliseconds} ms");
    }

    private static string RandomProgress(Random random)
    {
        StringBuilder text = new();
        for (int line = random.Next(0, 30); line > 0; line--)
        {
            string key = random.Next(10) == 0 ? RandomText(random, 0, 12, "abc_=") : ProgressKeys[random.Next(ProgressKeys.Length)];
            string value = random.Next(4) switch
            {
                0 => random.NextInt64(long.MinValue, long.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture),
                1 => RandomText(random, 0, 20, "0123456789.-x:NaInfE"),
                2 => "N/A",
                _ => random.NextDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "x",
            };
            text.Append(key).Append(random.Next(20) == 0 ? string.Empty : "=").Append(value).Append('\n');
        }

        return text.ToString();
    }

    private static string RandomText(Random random, int min, int max, string alphabet)
    {
        char[] text = new char[random.Next(min, max)];
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = random.Next(50) == 0 ? (char)random.Next(0, 0xD800) : alphabet[random.Next(alphabet.Length)];
        }

        return new string(text);
    }

    /// <summary>Flips, deletes, duplicates or truncates a few characters.</summary>
    private static string Mutate(string text, Random random)
    {
        StringBuilder result = new(text);
        for (int edits = random.Next(1, 6); edits > 0 && result.Length > 0; edits--)
        {
            int at = random.Next(result.Length);
            switch (random.Next(4))
            {
                case 0:
                    result[at] = (char)random.Next(0x20, 0x7F);
                    break;
                case 1:
                    result.Remove(at, Math.Min(result.Length - at, random.Next(1, 16)));
                    break;
                case 2:
                    result.Insert(at, result.ToString(at, Math.Min(result.Length - at, random.Next(1, 16))));
                    break;
                default:
                    result.Length = at;
                    break;
            }
        }

        return result.ToString();
    }

    private static byte[] RandomBytes(Random random, int min, int max)
    {
        byte[] bytes = new byte[random.Next(min, max)];
        random.NextBytes(bytes);
        return bytes;
    }

    private static byte[] MutateBytes(byte[] source, Random random)
    {
        byte[] bytes = (byte[])source.Clone();
        for (int edits = random.Next(1, 5); edits > 0; edits--)
        {
            bytes[random.Next(Math.Min(bytes.Length, 80))] = (byte)random.Next(256);
        }

        return random.Next(5) == 0 ? bytes[..random.Next(bytes.Length)] : bytes;
    }

    /// <summary>A 32-bit float WAV with a LIST chunk, as FFmpeg writes to its pipe.</summary>
    private static byte[] Wav()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(0xFFFFFFFF);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)3);
        writer.Write((ushort)2);
        writer.Write(44100);
        writer.Write(44100 * 8);
        writer.Write((ushort)8);
        writer.Write((ushort)32);
        byte[] info = "INFOISFT\u000e\u0000\u0000\u0000Lavf62.3.100\u0000\u0000"u8.ToArray();
        writer.Write("LIST"u8);
        writer.Write(info.Length);
        writer.Write(info);
        writer.Write("data"u8);
        writer.Write(0xFFFFFFFF);
        for (int i = 0; i < 64; i++)
        {
            writer.Write(MathF.Sin(i / 5f));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
