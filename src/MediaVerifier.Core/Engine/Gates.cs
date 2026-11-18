using System.Runtime.InteropServices;
using MediaVerifier.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace MediaVerifier.Core.Engine;

/// <summary>The physical volume a path lives on and what kind of storage it is.</summary>
public sealed record VolumeInfo(string Key, StorageClass Class);

public interface IVolumeResolver
{
    VolumeInfo Resolve(string path);
}

/// <summary>
/// Storage-class detection (task E-05): UNC paths and network drives are Network, removable drives
/// Removable, otherwise <c>IOCTL_STORAGE_QUERY_PROPERTY</c> with <c>StorageDeviceSeekPenaltyProperty</c>
/// says HDD (seek penalty) or SSD. Volumes are keyed by their volume GUID path, so two drive letters or
/// folders on one disk share a gate. Failures give Unknown.
/// </summary>
public sealed class WindowsVolumeResolver : IVolumeResolver
{
    private readonly Dictionary<string, VolumeInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public VolumeInfo Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            string[] parts = full.TrimStart('\\').Split('\\');
            string share = parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : full;
            return new VolumeInfo(share.ToLowerInvariant(), StorageClass.Network);
        }

        string mount = VolumeMountPoint(full) ?? Path.GetPathRoot(full) ?? full;
        lock (_gate)
        {
            if (_cache.TryGetValue(mount, out VolumeInfo? cached))
            {
                return cached;
            }
        }

        VolumeInfo info = Detect(mount);
        lock (_gate)
        {
            _cache[mount] = info;
        }

        return info;
    }

    private static VolumeInfo Detect(string mountPoint)
    {
        string key = VolumeGuid(mountPoint) ?? mountPoint.ToUpperInvariant();
        try
        {
            DriveInfo drive = new(mountPoint);
            switch (drive.DriveType)
            {
                case DriveType.Network:
                    return new VolumeInfo(key, StorageClass.Network);
                case DriveType.Removable:
                case DriveType.CDRom:
                    return new VolumeInfo(key, StorageClass.Removable);
            }
        }
        catch (ArgumentException)
        {
        }

        bool? seekPenalty = SeekPenalty(key);
        StorageClass storage = seekPenalty switch
        {
            true => StorageClass.Hdd,
            false => StorageClass.Ssd,
            _ => StorageClass.Unknown,
        };
        return new VolumeInfo(key, storage);
    }

    private static string? VolumeMountPoint(string path)
    {
        char[] buffer = new char[1024];
        return GetVolumePathName(path, buffer, buffer.Length) ? new string(buffer).TrimEnd('\0') : null;
    }

    private static string? VolumeGuid(string mountPoint)
    {
        string withSlash = mountPoint.EndsWith('\\') ? mountPoint : mountPoint + "\\";
        char[] buffer = new char[64];
        return GetVolumeNameForVolumeMountPoint(withSlash, buffer, buffer.Length) ? new string(buffer).TrimEnd('\0') : null;
    }

    /// <summary>True for a seek penalty (rotational), false for none, null when the query fails.</summary>
    private static bool? SeekPenalty(string volumeKey)
    {
        // \\?\Volume{...}\ → \\.\Volume{...} (no trailing slash) opens the volume device.
        string device = volumeKey.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\.\" + volumeKey[4..].TrimEnd('\\') : @"\\.\" + volumeKey.TrimEnd('\\');
        using SafeFileHandle handle = CreateFile(device, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        StoragePropertyQuery query = new() { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, ref query, Marshal.SizeOf<StoragePropertyQuery>(), out DeviceSeekPenaltyDescriptor result, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(), out _, IntPtr.Zero))
        {
            return null;
        }

        return result.IncursSeekPenalty != 0;
    }

    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref StoragePropertyQuery input, int inputSize, out DeviceSeekPenaltyDescriptor output, int outputSize, out int returned, IntPtr overlapped);
}

/// <summary>
/// One gate per physical volume: how many readers may touch it at once. Each is a <see cref="CpuGate"/> taken with
/// weight 1, so <see cref="Refresh"/> can apply a changed Readers setting to a scan that is running.
/// </summary>
public sealed class IoGates : IDisposable
{
    private readonly Dictionary<string, (CpuGate Gate, StorageClass Class)> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly Func<StorageClass, int> _readersFor;

    public IoGates(Func<StorageClass, int> readersFor)
    {
        _readersFor = readersFor;
    }

    /// <summary>Default readers per storage class (subplan 05).</summary>
    public static int DefaultReaders(StorageClass storage) => storage switch
    {
        StorageClass.Hdd => 1,
        StorageClass.Ssd => 4,
        StorageClass.Network => 2,
        StorageClass.Removable => 1,
        _ => 2,
    };

    public CpuGate For(VolumeInfo volume)
    {
        lock (_gate)
        {
            if (!_gates.TryGetValue(volume.Key, out (CpuGate Gate, StorageClass Class) entry))
            {
                entry = (new CpuGate(Math.Max(1, _readersFor(volume.Class))), volume.Class);
                _gates[volume.Key] = entry;
            }

            return entry.Gate;
        }
    }

    public int LimitFor(VolumeInfo volume) => For(volume).Capacity;

    /// <summary>Reads the readers per storage class again and applies them to every volume's gate.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            foreach ((CpuGate gate, StorageClass storage) in _gates.Values)
            {
                gate.SetCapacity(Math.Max(1, _readersFor(storage)));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _gates.Clear();
        }
    }
}

/// <summary>
/// The global CPU gate (task E-06): a weighted semaphore with capacity = logical cores × performance level
/// (Low 25 %, Balanced 50 %, High 100 %; minimum 1). Waiters are served in order so heavy jobs cannot
/// starve. A weight above the capacity is clamped to it. The capacity can change while leases are held (the Speed
/// setting changed during a scan): a lower one takes effect as leases come back.
/// </summary>
public sealed class CpuGate
{
    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _available;
    private int _capacity;

    public CpuGate(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _available = _capacity;
    }

    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _capacity;
            }
        }
    }

    public int InUse
    {
        get
        {
            lock (_gate)
            {
                return _capacity - _available;
            }
        }
    }

    /// <summary>
    /// Changes the capacity. Leases already granted keep their weight; waiting requests heavier than the new capacity
    /// are clamped to it, as new ones are.
    /// </summary>
    public void SetCapacity(int capacity)
    {
        capacity = Math.Max(1, capacity);
        lock (_gate)
        {
            if (capacity == _capacity)
            {
                return;
            }

            _available += capacity - _capacity;
            _capacity = capacity;
            foreach (Waiter waiter in _waiters)
            {
                waiter.Weight = Math.Min(waiter.Weight, capacity);
            }

            Grant();
        }
    }

    public static int CapacityFor(PerformanceLevel level, int logicalCores) => Math.Max(1, (int)Math.Round(logicalCores * level switch
    {
        PerformanceLevel.Low => 0.25,
        PerformanceLevel.High => 1.0,
        _ => 0.5,
    }));

    public async Task<Lease> AcquireAsync(int weight, CancellationToken cancellationToken)
    {
        Waiter? waiter = null;
        lock (_gate)
        {
            weight = Math.Clamp(weight, 1, _capacity);
            if (_waiters.Count == 0 && _available >= weight)
            {
                _available -= weight;
                return new Lease(this, weight);
            }

            waiter = new Waiter(weight);
            waiter.Node = _waiters.AddLast(waiter);
        }

        using (cancellationToken.Register(() => Cancel(waiter)))
        {
            await waiter.Completion.Task.ConfigureAwait(false);
        }

        // The weight granted, which a lower capacity may have clamped while the request waited.
        return new Lease(this, waiter.Weight);
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node?.List is null)
            {
                return;
            }

            _waiters.Remove(waiter.Node);
            Grant();
        }

        waiter.Completion.TrySetCanceled();
    }

    private void Release(int weight)
    {
        lock (_gate)
        {
            _available += weight;
            Grant();
        }
    }

    // Called under the lock: hand capacity to waiters in order.
    private void Grant()
    {
        while (_waiters.First is LinkedListNode<Waiter> first && _available >= first.Value.Weight)
        {
            _available -= first.Value.Weight;
            _waiters.RemoveFirst();
            first.Value.Completion.TrySetResult();
        }
    }

    public readonly struct Lease : IDisposable
    {
        private readonly CpuGate? _owner;

        public Lease(CpuGate owner, int weight)
        {
            _owner = owner;
            Weight = weight;
        }

        public int Weight { get; }

        public void Dispose() => _owner?.Release(Weight);
    }

    private sealed class Waiter(int weight)
    {
        /// <summary>Changed only under the gate's lock, before the waiter is granted.</summary>
        public int Weight { get; set; } = weight;

        public LinkedListNode<Waiter>? Node { get; set; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public enum PerformanceLevel : byte
{
    Low = 0,
    Balanced = 1,
    High = 2,
}
