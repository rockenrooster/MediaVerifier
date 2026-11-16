using System.Globalization;

namespace MediaVerifier.Core.Analysis.Structure;

/// <summary>Small byte and text helpers shared by the walkers.</summary>
internal static class StructureBytes
{
    /// <summary>Four bytes of printable ASCII (a FourCC such as a box or chunk type).</summary>
    public static bool IsPrintableFourCc(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (bytes[i] < 0x20 || bytes[i] > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Bytes as ASCII text; anything outside printable ASCII becomes '?'.</summary>
    public static string Ascii(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = bytes.Length <= 64 ? stackalloc char[bytes.Length] : new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            chars[i] = b >= 0x20 && b <= 0x7E ? (char)b : '?';
        }

        return new string(chars);
    }

    /// <summary><paramref name="a"/> + <paramref name="b"/> for non-negative values, saturating at <see cref="long.MaxValue"/>.</summary>
    public static long SaturatingAdd(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;

    /// <summary>The most padding regions one artifact records; a pathological file stops adding after it.</summary>
    public const int MaxPaddingRegions = 4096;

    /// <summary>Records <c>[offset, end)</c> as padding when it is not empty and the list is not full.</summary>
    public static void AddPadding(List<PaddingRegion> padding, long offset, long end)
    {
        if (end > offset && padding.Count < MaxPaddingRegions)
        {
            padding.Add(new PaddingRegion(offset, end - offset));
        }
    }

    /// <summary>An unsigned value as a long, saturating at <see cref="long.MaxValue"/>.</summary>
    public static long Saturate(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>
    /// A new structural issue whose detail is formatted with the invariant culture. There is deliberately no
    /// <c>string</c> overload: it would win overload resolution for <c>$"..."</c> and format with the
    /// current culture.
    /// </summary>
    public static StructureIssue Issue(StructureIssueKind kind, FormattableString detail, long? offset = null, long? missingBytes = null, string? element = null) =>
        new(kind, detail.ToString(CultureInfo.InvariantCulture), offset, missingBytes, element);
}
