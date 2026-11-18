using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;

namespace MediaVerifier.Core.Engine;

/// <summary>One media file as the enumerator saw it, before any I/O on its content.</summary>
public sealed record EnumeratedFile(string RelDir, string Name, string Extension, MediaKind Kind, long Size, long MtimeUtcTicks, FileAttributes Attributes, SkipReason Skip);

/// <summary>A directory and its media files. Unlisted directories are excluded from missing-file detection.</summary>
public sealed record EnumeratedDirectory(string RelDir, IReadOnlyList<EnumeratedFile> Files, bool Listed, string? Error);

/// <summary>Enumeration settings: exclusions, globs and skip limits (subplan 05).</summary>
public sealed record EnumerationRules
{
    /// <summary>Directory names skipped everywhere (case-insensitive, wildcards allowed).</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedDirectories =
    [
        "$RECYCLE.BIN", "System Volume Information", ".mediaverifier-quarantine", "@eaDir", ".@__thumb", "#recycle",
        ".Trash*", "lost+found", ".git",
    ];

    /// <summary>Extensions that mark a sibling as still downloading.</summary>
    public static readonly IReadOnlyList<string> PartialMarkers = [".part", ".crdownload", ".!qB", ".!ut", ".tmp"];

    public IReadOnlyList<string> ExcludedDirectories { get; init; } = DefaultExcludedDirectories;

    /// <summary>Globs on the path relative to the root; null includes everything.</summary>
    public IReadOnlyList<string>? IncludeGlobs { get; init; }

    public IReadOnlyList<string>? ExcludeGlobs { get; init; }

    public bool SkipReparsePoints { get; init; } = true;

    public bool ScanCloudPlaceholders { get; init; }

    /// <summary>Files larger than this are skipped as TooLarge; null means no limit.</summary>
    public long? MaxFileSize { get; init; }

    /// <summary>Files modified more recently than this are skipped as InProgress.</summary>
    public TimeSpan InProgressAge { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Walks a root depth-first, one directory at a time (task E-02): media files only, exclusions and
/// globs applied, reparse points and hidden+system files skipped, every listing failure recorded.
/// Skip rules that need no I/O are decided here (task E-03).
/// </summary>
public sealed class TreeEnumerator
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    private readonly IMediaFileSystem _fileSystem;
    private readonly MediaClassifier _classifier;
    private readonly EnumerationRules _rules;
    private readonly Regex? _include;
    private readonly Regex? _exclude;
    private readonly IClock _clock;

    public TreeEnumerator(IMediaFileSystem fileSystem, MediaClassifier classifier, EnumerationRules rules, IClock clock)
    {
        _fileSystem = fileSystem;
        _classifier = classifier;
        _rules = rules;
        _clock = clock;
        _include = Glob.Compile(rules.IncludeGlobs);
        _exclude = Glob.Compile(rules.ExcludeGlobs);
    }

    /// <summary>
    /// Enumerates <paramref name="rootPath"/> from <paramref name="startRelDir"/> (the whole root when empty).
    /// The root itself failing to list is reported as an unlisted directory with an error.
    /// </summary>
    public IEnumerable<EnumeratedDirectory> Enumerate(string rootPath, string startRelDir = "", CancellationToken cancellationToken = default)
    {
        Stack<string> pending = new();
        pending.Push(FileIndex.NormalizeRelDir(startRelDir));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relDir = pending.Pop();
            string fullDir = relDir.Length == 0 ? rootPath : Path.Combine(rootPath, relDir);
            List<FsEntry>? entries = null;
            string? error = null;
            try
            {
                entries = [.. _fileSystem.EnumerateDirectory(fullDir)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error = ex.Message;
            }

            if (entries is null)
            {
                yield return new EnumeratedDirectory(relDir, [], Listed: false, error);
                continue;
            }

            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            HashSet<string>? names = null;
            List<EnumeratedFile> files = [];
            List<string> subdirectories = [];
            long now = _clock.UtcNow.UtcTicks;
            foreach (FsEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if ((_rules.SkipReparsePoints && entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) || IsExcludedDirectory(entry.Name))
                    {
                        continue;
                    }

                    subdirectories.Add(relDir.Length == 0 ? entry.Name : relDir + "\\" + entry.Name);
                    continue;
                }

                if (_rules.SkipReparsePoints && entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry.Attributes.HasFlag(FileAttributes.Hidden) && entry.Attributes.HasFlag(FileAttributes.System))
                {
                    continue;
                }

                MediaKind kind = _classifier.Classify(entry.Name);
                if (kind == MediaKind.Unknown)
                {
                    continue;
                }

                string relPath = relDir.Length == 0 ? entry.Name : relDir + "\\" + entry.Name;
                if ((_include is not null && !_include.IsMatch(relPath)) || (_exclude is not null && _exclude.IsMatch(relPath)))
                {
                    continue;
                }

                names ??= new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                SkipReason skip = DecideSkip(entry, names, now);
                files.Add(new EnumeratedFile(relDir, entry.Name, MediaClassifier.Extension(entry.Name), kind, entry.Size, entry.LastWriteUtcTicks, entry.Attributes, skip));
            }

            yield return new EnumeratedDirectory(relDir, files, Listed: true, null);
            for (int i = subdirectories.Count - 1; i >= 0; i--)
            {
                pending.Push(subdirectories[i]);
            }
        }
    }

    /// <summary>
    /// Whether these rules leave a file out, as a listing from <paramref name="startRelDir"/> would: a skipped folder
    /// name below the start, an extension that is not media here (the file types, or the folder's Contains choices),
    /// or the folder's include and exclude patterns. Missing-file detection uses it: a file the rules leave out is out
    /// of scope, not missing.
    /// </summary>
    public bool Excludes(string relDir, string name, string startRelDir = "")
    {
        relDir = FileIndex.NormalizeRelDir(relDir);
        string start = FileIndex.NormalizeRelDir(startRelDir);
        string below = start.Length == 0 ? relDir
            : relDir.Length > start.Length && relDir.StartsWith(start + "\\", StringComparison.Ordinal) ? relDir[(start.Length + 1)..]
            : "";
        foreach (string folder in below.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsExcludedDirectory(folder))
            {
                return true;
            }
        }

        if (_classifier.Classify(name) == MediaKind.Unknown)
        {
            return true;
        }

        string relPath = relDir.Length == 0 ? name : relDir + "\\" + name;
        return (_include is not null && !_include.IsMatch(relPath)) || (_exclude is not null && _exclude.IsMatch(relPath));
    }

    /// <summary>Skip reasons decidable from the listing alone; Locked and AccessDenied come from the read.</summary>
    public SkipReason DecideSkip(FsEntry entry, IReadOnlySet<string> siblingNames, long nowTicks)
    {
        if (!_rules.ScanCloudPlaceholders && (entry.Attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0)
        {
            return SkipReason.CloudPlaceholder;
        }

        if (_rules.MaxFileSize is long limit && entry.Size > limit)
        {
            return SkipReason.TooLarge;
        }

        long age = nowTicks - entry.LastWriteUtcTicks;
        if (age >= 0 && age < _rules.InProgressAge.Ticks)
        {
            // A time in the future (a camera with a wrong clock) is not a file being written.
            return SkipReason.InProgress;
        }

        foreach (string marker in PartialMarkers)
        {
            if (siblingNames.Contains(entry.Name + marker))
            {
                return SkipReason.InProgress;
            }
        }

        return SkipReason.None;
    }

    private static IReadOnlyList<string> PartialMarkers => EnumerationRules.PartialMarkers;

    private bool IsExcludedDirectory(string name)
    {
        foreach (string pattern in _rules.ExcludedDirectories)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Glob patterns on relative paths: <c>**</c> crosses folders, <c>*</c> and <c>?</c> stay within one. Case-insensitive.</summary>
public static class Glob
{
    public static Regex? Compile(IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return null;
        }

        StringBuilder regex = new("^(?:");
        bool first = true;
        foreach (string raw in patterns)
        {
            string pattern = raw.Trim().Replace('/', '\\').TrimStart('\\');
            if (pattern.Length == 0)
            {
                continue;
            }

            regex.Append(first ? "" : "|");
            first = false;
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    // "**\" matches zero or more folders; a trailing "**" matches anything.
                    bool slash = i + 2 < pattern.Length && pattern[i + 2] == '\\';
                    regex.Append(slash ? @"(?:.*\\)?" : ".*");
                    i += slash ? 2 : 1;
                }
                else if (c == '*')
                {
                    regex.Append(@"[^\\]*");
                }
                else if (c == '?')
                {
                    regex.Append(@"[^\\]");
                }
                else
                {
                    regex.Append(Regex.Escape(c.ToString()));
                }
            }
        }

        regex.Append(")$");
        return first ? null : new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
