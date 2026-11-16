using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaVerifier.Core.Engine;

/// <summary>What the enumerator captures about one directory entry, without allocating a FileInfo.</summary>
public readonly record struct FsEntry(string Name, bool IsDirectory, long Size, long LastWriteUtcTicks, FileAttributes Attributes);

/// <summary>Size, times, attributes and the NTFS file id of a file at one moment.</summary>
public readonly record struct FileStat(long Size, long LastWriteUtcTicks, FileAttributes Attributes, long? FileId);

/// <summary>
/// The file-system operations the engine, integrity and remediation code use. The real implementation
/// talks to Windows; tests substitute fakes (synthetic million-file trees, faulting reads, failures at
/// each step of a repair).
/// </summary>
public interface IMediaFileSystem
{
    /// <summary>Entries of one directory. Throws for an unlistable directory (access denied, I/O error, gone).</summary>
    IEnumerable<FsEntry> EnumerateDirectory(string path);

    bool DirectoryExists(string path);

    bool FileExists(string path);

    FileStat? Stat(string path);

    /// <summary>Opens for reading, sharing read, write and delete, with sequential-scan hinting.</summary>
    Stream OpenRead(string path, int bufferSize = 4096, bool sequential = true);
}

/// <summary>The real file system.</summary>
public sealed class WindowsFileSystem : IMediaFileSystem
{
    public static WindowsFileSystem Instance { get; } = new();

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Win32,
    };

    public IEnumerable<FsEntry> EnumerateDirectory(string path) =>
        new FileSystemEnumerable<FsEntry>(
            path,
            (ref FileSystemEntry entry) => new FsEntry(
                entry.FileName.ToString(),
                entry.IsDirectory,
                entry.Length,
                entry.LastWriteTimeUtc.UtcTicks,
                entry.Attributes),
            Options);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public FileStat? Stat(string path)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            {
                FileInfo fallback = new(path);
                return new FileStat(fallback.Length, fallback.LastWriteTimeUtc.Ticks, fallback.Attributes, null);
            }

            long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            long write = DateTime.FromFileTimeUtc(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow).Ticks;
            long id = ((long)info.FileIndexHigh << 32) | info.FileIndexLow;
            return new FileStat(size, write, (FileAttributes)info.FileAttributes, id == 0 ? null : id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                // Locked or access denied: the directory entry still tells size and time.
                FileInfo info = new(path);
                return new FileStat(info.Length, info.LastWriteTimeUtc.Ticks, info.Attributes, null);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public Stream OpenRead(string path, int bufferSize = 4096, bool sequential = true) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, sequential ? FileOptions.SequentialScan : FileOptions.RandomAccess);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
