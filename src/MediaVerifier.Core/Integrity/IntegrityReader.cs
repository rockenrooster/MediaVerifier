using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using MediaVerifier.Core.Engine;

namespace MediaVerifier.Core.Integrity;

public enum IntegrityOutcome : byte
{
    /// <summary>Read to the end; fingerprint and zero map are valid.</summary>
    Ok = 0,

    /// <summary>The storage could not deliver the data (GEN-UNREADABLE).</summary>
    Unreadable = 1,

    /// <summary>Sharing violation: skipped as Locked, retried at the end of the run.</summary>
    Locked = 2,

    /// <summary>Skipped as AccessDenied.</summary>
    AccessDenied = 3,

    /// <summary>Device or share went away: abort this root for the run, flag nothing.</summary>
    DeviceGone = 4,

    /// <summary>Size or modified time changed during the read: skipped as InProgress.</summary>
    ChangedDuringRead = 5,

    /// <summary>The file vanished before it could be opened.</summary>
    NotFound = 6,
}

/// <summary>4 KiB-aligned all-zero blocks of a file (subplan 06, Zero-filled content).</summary>
public sealed record ZeroMap(long ZeroBlocks, long TotalBlocks, long LongestRunBlocks, long LongestRunOffset, IReadOnlyList<long> RunOffsets)
{
    /// <summary>Runs of at least this many consecutive zero blocks (64 KiB) are recorded.</summary>
    public const int RecordedRunBlocks = 16;

    public static ZeroMap None { get; } = new(0, 0, 0, 0, []);

    public double ZeroFraction => TotalBlocks == 0 ? 0 : (double)ZeroBlocks / TotalBlocks;
}

/// <summary>The result of reading a file once: fingerprint, zero map, read error.</summary>
public sealed record IntegrityResult(
    IntegrityOutcome Outcome,
    byte[]? Hash,
    long Size,
    long LastWriteUtcTicks,
    long BytesRead,
    ZeroMap Zeros,
    int? Win32Error,
    long? ErrorOffset,
    TimeSpan Elapsed)
{
    /// <summary>The NTFS file id seen when the read started (rename detection), when available.</summary>
    public long? FileId { get; init; }

    public bool Succeeded => Outcome == IntegrityOutcome.Ok && Hash is not null;
}

/// <summary>
/// One sequential pass over a file (task I-01): XXH3-128 fingerprint, map of all-zero 4 KiB blocks
/// (vectorised scan), read-error capture with one retry, and a re-stat guard against files that change
/// while being read. Uses the OS cache on purpose: analysis reads the same bytes next.
/// </summary>
public static class IntegrityReader
{
    public const int BufferSize = 1 << 20;
    public const int BlockSize = 4096;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    public static async Task<IntegrityResult> ReadAsync(string path, IMediaFileSystem fileSystem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        Stopwatch watch = Stopwatch.StartNew();
        FileStat? before = fileSystem.Stat(path);
        if (before is null)
        {
            return Failed(IntegrityOutcome.NotFound, 0, 0, null, null, watch.Elapsed);
        }

        FileStat stat = before.Value;
        IntegrityResult result = await ReadOnceAsync(path, stat, fileSystem, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == IntegrityOutcome.Unreadable)
        {
            // Never retry in a tight loop: one retry after 500 ms, then record it.
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            result = await ReadOnceAsync(path, stat, fileSystem, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome == IntegrityOutcome.Ok)
        {
            FileStat? after = fileSystem.Stat(path);
            if (after is null || after.Value.Size != stat.Size || after.Value.LastWriteUtcTicks != stat.LastWriteUtcTicks || result.BytesRead != stat.Size)
            {
                return result with { Outcome = IntegrityOutcome.ChangedDuringRead, Hash = null };
            }
        }

        return result with { Elapsed = watch.Elapsed, FileId = stat.FileId };
    }

    /// <summary>Maps a Win32 error code from a failed read (subplan 06, table).</summary>
    public static IntegrityOutcome MapError(int win32) => win32 switch
    {
        23 or 27 or 30 or 1117 or 1127 or 1392 or 1393 => IntegrityOutcome.Unreadable,
        32 or 33 => IntegrityOutcome.Locked,
        5 => IntegrityOutcome.AccessDenied,
        21 or 53 or 59 or 64 or 1167 => IntegrityOutcome.DeviceGone,
        2 or 3 => IntegrityOutcome.NotFound,
        _ => IntegrityOutcome.Unreadable,
    };

    private static async Task<IntegrityResult> ReadOnceAsync(string path, FileStat stat, IMediaFileSystem fileSystem, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        XxHash128 hash = new();
        ZeroScanner zeros = new();
        long offset = 0;
        try
        {
            Stream stream;
            try
            {
                stream = fileSystem.OpenRead(path, bufferSize: 1, sequential: true);
            }
            catch (UnauthorizedAccessException)
            {
                return Failed(IntegrityOutcome.AccessDenied, stat.Size, stat.LastWriteUtcTicks, 5, null, TimeSpan.Zero);
            }
            catch (FileNotFoundException)
            {
                return Failed(IntegrityOutcome.NotFound, stat.Size, stat.LastWriteUtcTicks, 2, null, TimeSpan.Zero);
            }
            catch (DirectoryNotFoundException)
            {
                return Failed(IntegrityOutcome.DeviceGone, stat.Size, stat.LastWriteUtcTicks, 3, null, TimeSpan.Zero);
            }
            catch (IOException ex)
            {
                int code = ex.HResult & 0xFFFF;
                return Failed(MapError(code), stat.Size, stat.LastWriteUtcTicks, code, 0, TimeSpan.Zero);
            }

            await using (stream.ConfigureAwait(false))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int filled = 0;
                    try
                    {
                        // Fill whole buffers so zero blocks stay aligned to file offsets.
                        while (filled < BufferSize)
                        {
                            int read = await stream.ReadAsync(buffer.AsMemory(filled, BufferSize - filled), cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            filled += read;
                        }
                    }
                    catch (IOException ex)
                    {
                        int code = ex.HResult & 0xFFFF;
                        return Failed(MapError(code), stat.Size, stat.LastWriteUtcTicks, code, offset + filled, TimeSpan.Zero);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return Failed(IntegrityOutcome.AccessDenied, stat.Size, stat.LastWriteUtcTicks, 5, offset + filled, TimeSpan.Zero);
                    }

                    if (filled == 0)
                    {
                        break;
                    }

                    ReadOnlySpan<byte> chunk = buffer.AsSpan(0, filled);
                    hash.Append(chunk);
                    zeros.Scan(chunk, offset);
                    offset += filled;
                    if (filled < BufferSize)
                    {
                        break;
                    }
                }
            }

            byte[] fingerprint = new byte[16];
            hash.GetCurrentHash(fingerprint);
            return new IntegrityResult(IntegrityOutcome.Ok, fingerprint, stat.Size, stat.LastWriteUtcTicks, offset, zeros.ToMap(), null, null, TimeSpan.Zero);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IntegrityResult Failed(IntegrityOutcome outcome, long size, long mtime, int? code, long? offset, TimeSpan elapsed) =>
        new(outcome, null, size, mtime, 0, ZeroMap.None, code, offset, elapsed);

    /// <summary>Counts all-zero 4 KiB blocks and tracks runs across chunk boundaries.</summary>
    private sealed class ZeroScanner
    {
        private const int MaxRecordedRuns = 8;
        private readonly List<long> _runOffsets = [];
        private long _zeroBlocks;
        private long _totalBlocks;
        private long _runStart = -1;
        private long _runLength;
        private long _longest;
        private long _longestOffset;

        public void Scan(ReadOnlySpan<byte> chunk, long chunkOffset)
        {
            for (int start = 0; start < chunk.Length; start += BlockSize)
            {
                ReadOnlySpan<byte> block = chunk.Slice(start, Math.Min(BlockSize, chunk.Length - start));
                _totalBlocks++;
                if (block.IndexOfAnyExcept((byte)0) < 0)
                {
                    _zeroBlocks++;
                    if (_runStart < 0)
                    {
                        _runStart = chunkOffset + start;
                        _runLength = 0;
                    }

                    _runLength++;
                }
                else
                {
                    CloseRun();
                }
            }
        }

        public ZeroMap ToMap()
        {
            CloseRun();
            return new ZeroMap(_zeroBlocks, _totalBlocks, _longest, _longestOffset, [.. _runOffsets]);
        }

        private void CloseRun()
        {
            if (_runStart < 0)
            {
                return;
            }

            if (_runLength > _longest)
            {
                _longest = _runLength;
                _longestOffset = _runStart;
            }

            if (_runLength >= ZeroMap.RecordedRunBlocks && _runOffsets.Count < MaxRecordedRuns)
            {
                _runOffsets.Add(_runStart);
            }

            _runStart = -1;
            _runLength = 0;
        }
    }
}
