using System.Buffers.Binary;
using System.Text;
using MediaVerifier.Core.Analysis.Structure;

namespace MediaVerifier.Tests.Analysis.Structure;

/// <summary>Builds byte sequences for structure tests. Test media is always built in code, never committed.</summary>
internal sealed class ByteBuilder
{
    private readonly List<byte> _bytes = [];

    public int Length => _bytes.Count;

    public ByteBuilder U8(int value)
    {
        _bytes.Add((byte)value);
        return this;
    }

    public ByteBuilder U16BE(int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
        return Append(b);
    }

    public ByteBuilder U16LE(int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
        return Append(b);
    }

    public ByteBuilder U24BE(int value) => U8(value >> 16).U8(value >> 8).U8(value);

    public ByteBuilder U32BE(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return Append(b);
    }

    public ByteBuilder U32LE(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return Append(b);
    }

    public ByteBuilder U64BE(ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        return Append(b);
    }

    public ByteBuilder U64LE(ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return Append(b);
    }

    public ByteBuilder Ascii(string text)
    {
        _bytes.AddRange(Encoding.ASCII.GetBytes(text));
        return this;
    }

    public ByteBuilder Bytes(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    public ByteBuilder Fill(int count, byte value = 0)
    {
        for (int i = 0; i < count; i++)
        {
            _bytes.Add(value);
        }

        return this;
    }

    /// <summary>Seeded noise without 0xFF, so it can stand in for entropy-coded data or chunk payloads.</summary>
    public ByteBuilder Noise(int count, int seed)
    {
        Random random = new(seed);
        for (int i = 0; i < count; i++)
        {
            _bytes.Add((byte)random.Next(0, 0xFF));
        }

        return this;
    }

    public byte[] ToArray() => [.. _bytes];

    private ByteBuilder Append(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            _bytes.Add(b);
        }

        return this;
    }
}

/// <summary>Shared helpers for running the structure reader over built bytes.</summary>
internal static class StructureTest
{
    public static StructureArtifact Read(byte[] bytes, string extension)
    {
        using MemoryStream stream = new(bytes, writable: false);
        return StructureReader.Read(stream, extension);
    }

    /// <summary>Sniffs with only the first 64 bytes as head and the rest through <c>readAt</c>.</summary>
    public static SniffResult Sniff(byte[] bytes) =>
        SignatureSniffer.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, SignatureSniffer.RecommendedHeadLength)), bytes.Length, ReadAt(bytes));

    public static Func<long, int, byte[]> ReadAt(byte[] bytes) => (offset, count) =>
        offset < 0 || offset >= bytes.Length ? [] : bytes.AsSpan((int)offset, (int)Math.Min(count, bytes.Length - offset)).ToArray();

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    public static byte[] Truncate(byte[] bytes, double fraction) => bytes[..(int)(bytes.Length * fraction)];

    public static StructureIssue Single(StructureArtifact artifact, StructureIssueKind kind) =>
        Assert.Single(artifact.Issues, i => i.Kind == kind);
}
