using System.Diagnostics;
using MediaVerifier.Core.Analysis.Structure;
using static MediaVerifier.Tests.Analysis.Structure.StructureTest;
using static MediaVerifier.Tests.Analysis.Structure.TestMedia;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>
/// Task C-01 acceptance: the sniffer and the reader never throw on random input, and every call finishes
/// quickly (each one is time-boxed at 200 ms).
/// </summary>
public sealed class StructureFuzzTests
{
    private const int Cases = 10_000;
    private static readonly TimeSpan CallBudget = TimeSpan.FromMilliseconds(200);

    private static readonly (string Extension, byte[] Bytes)[] Samples =
    [
        ("mp4", FastStartMp4(3000)),
        ("mov", MoovAtEndMp4(3000)),
        ("mp4", Concat(Ftyp("isom"), Moov(), LargeBox("mdat", new byte[500]))),
        ("wav", Wav(2000)),
        ("wav", Rf64(2000)),
        ("avi", Avi(2000)),
        ("webp", WebP(2000)),
        ("flac", Flac()),
        ("flac", Concat(Id3v2(), Flac())),
        ("jpg", BaselineJpeg(restartMarkers: true)),
        ("jpg", ProgressiveJpeg()),
        ("jpg", Concat(BaselineJpeg(), FastStartMp4(500))),
        ("png", Png(2000)),
        ("mkv", Mkv()),
        ("mkv", Mkv(unknownSize: true)),
        ("webm", Mkv(seekHead: false, docType: "webm")),
        ("mp3", Mp3(30, "Xing")),
        ("mp3", Concat(Mp3(30, "VBRI"), ApeTag(200), Id3v1())),
        ("mp3", Mp3(30, "Info", id3: false)),
    ];

    [Fact]
    public void RandomBuffersNeverThrow()
    {
        Warm();
        Random random = new(20261116);
        string[] extensions = ["mp3", "jpg", "mp4", "mkv", "flac", "bin", ""];
        for (int i = 0; i < Cases; i++)
        {
            byte[] bytes = new byte[random.Next(0, 4097)];
            random.NextBytes(bytes);
            Run(bytes, extensions[i % extensions.Length], i);
        }
    }

    [Fact]
    public void MutatedValidHeadersNeverThrow()
    {
        Warm();
        Random random = new(7);
        for (int i = 0; i < Cases; i++)
        {
            (string extension, byte[] sample) = Samples[i % Samples.Length];
            Run(Mutate(sample, random), extension, i);
        }
    }

    [Fact]
    public void RandomDataBehindAValidSignatureNeverThrows()
    {
        // Each walker gets garbage right after the magic it keys on.
        byte[][] prefixes =
        [
            [0xFF, 0xD8, 0xFF], [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0x1A, 0x45, 0xDF, 0xA3], Ascii("fLaC"),
            Ascii("ID3"), Concat(Id3v2(), Ascii("fLaC")), [0, 0, 0, 0x20, .. Ascii("ftypisom")], Ascii("RIFF"), Ascii("RF64"),
            [.. Ascii("RIFF"), 0, 0, 0, 0, .. Ascii("AVI ")], [.. Ascii("RIFF"), 0, 0, 0, 0, .. Ascii("WAVE")], [0xFF, 0xFB, 0x90, 0x00],
        ];
        string[] extensions = ["jpg", "png", "mkv", "flac", "mp3", "flac", "mp4", "wav", "wav", "avi", "wav", "mp3"];
        Warm();
        Random random = new(42);
        for (int i = 0; i < Cases; i++)
        {
            int which = i % prefixes.Length;
            byte[] tail = new byte[random.Next(0, 4097)];
            random.NextBytes(tail);
            Run(Concat(prefixes[which], tail), extensions[which], i);
        }
    }

    [Fact]
    public void SamplesAreHealthy()
    {
        foreach ((string extension, byte[] bytes) in Samples)
        {
            StructureArtifact artifact = Read(bytes, extension);
            Assert.True(artifact.Issues.Count == 0, $"{extension}: {string.Join("; ", artifact.Issues.Select(i => i.Detail))}");
            Assert.NotEqual(ContainerFormat.Unknown, artifact.Sniff.Format);
        }
    }

    /// <summary>A few kinds of damage at once, concentrated in the first 256 bytes where the headers are.</summary>
    private static byte[] Mutate(byte[] sample, Random random)
    {
        byte[] bytes = (byte[])sample.Clone();
        int mutations = random.Next(1, 9);
        for (int m = 0; m < mutations && bytes.Length > 0; m++)
        {
            int position = random.Next(4) == 0 ? random.Next(bytes.Length) : random.Next(Math.Min(bytes.Length, 256));
            switch (random.Next(5))
            {
                case 0:
                    bytes[position] = (byte)random.Next(256);
                    break;
                case 1:
                    bytes[position] = random.Next(2) == 0 ? (byte)0x00 : (byte)0xFF;
                    break;
                case 2:
                    bytes[position] ^= (byte)(1 << random.Next(8));
                    break;
                case 3:
                    // A length-like field set to an extreme.
                    for (int k = 0; k < 4 && position + k < bytes.Length; k++)
                    {
                        bytes[position + k] = random.Next(2) == 0 ? (byte)0xFF : (byte)0x00;
                    }

                    break;
                default:
                    bytes = bytes[..random.Next(bytes.Length + 1)];
                    break;
            }
        }

        return bytes;
    }

    private static void Run(byte[] bytes, string extension, int index)
    {
        Timed(index, "sniffing the whole buffer", () => SignatureSniffer.Sniff(bytes, bytes.Length));
        Timed(index, "sniffing 64 bytes with readAt", () => SignatureSniffer.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, SignatureSniffer.RecommendedHeadLength)), bytes.Length, ReadAt(bytes)));
        StructureArtifact artifact = Timed(index, "reading", () => Read(bytes, extension));
        Assert.Equal(bytes.Length, artifact.FileLength);
    }

    private static T Timed<T>(int index, string what, Func<T> call)
    {
        Stopwatch watch = Stopwatch.StartNew();
        T result = call();
        watch.Stop();
        Assert.True(watch.Elapsed < CallBudget, $"case {index}: {what} took {watch.Elapsed.TotalMilliseconds} ms");
        return result;
    }

    private static void Warm()
    {
        foreach ((string extension, byte[] bytes) in Samples)
        {
            Read(bytes, extension);
        }
    }
}
