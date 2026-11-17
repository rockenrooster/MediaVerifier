namespace MediaVerifier.Core.Engine;

/// <summary>
/// Win32 path normalisation drops trailing spaces and dots from every path segment, so a folder named
/// <c>Trailing </c> or <c>Demo.</c> (made on a NAS, by macOS or by a Linux copy) cannot be reached by its plain path
/// (subplan 15, Path matrix; task T-12). Such paths use the <c>\\?\</c> form, which reaches the file system unchanged.
/// Every other path stays as it is, so logs, the database and the UI keep the familiar form.
/// </summary>
public static class ExtendedPath
{
    private const string Prefix = @"\\?\";

    /// <summary>The path to hand to the file system: the <c>\\?\</c> form when a segment ends in a space or a dot.</summary>
    public static string For(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!IsPlainRooted(path) || !HasTrimmedSegment(path))
        {
            return path;
        }

        string full = path.Replace('/', '\\');
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? Prefix + @"UNC\" + full[2..] : Prefix + full;
    }

    /// <summary>A process argument: only an absolute path (drive or UNC) changes; options and filter text never do.</summary>
    public static string ForArgument(string argument) => For(argument);

    /// <summary>True when a segment other than <c>.</c> and <c>..</c> ends in a space or a dot.</summary>
    public static bool HasTrimmedSegment(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i == path.Length || path[i] == '\\' || path[i] == '/')
            {
                ReadOnlySpan<char> segment = path.AsSpan(start, i - start);
                if (segment.Length > 0 && !segment.SequenceEqual(".") && !segment.SequenceEqual("..") && (segment[^1] == ' ' || segment[^1] == '.'))
                {
                    return true;
                }

                start = i + 1;
            }
        }

        return false;
    }

    /// <summary><c>C:\…</c> or <c>\\server\share\…</c>, not already in a device or extended form.</summary>
    private static bool IsPlainRooted(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
        || (path.Length >= 3 && path[0] == '\\' && path[1] == '\\' && path[2] != '?' && path[2] != '.');
}
