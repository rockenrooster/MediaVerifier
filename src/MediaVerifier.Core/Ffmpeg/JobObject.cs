using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaVerifier.Core.Ffmpeg;

/// <summary>
/// A Windows Job Object created with KILL_ON_JOB_CLOSE: every FFmpeg child assigned to it dies when the
/// job handle closes, including when MediaVerifier itself crashes. One job per engine instance.
/// </summary>
public sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly SafeFileHandle _handle;

    public JobObject()
    {
        IntPtr raw = CreateJobObject(IntPtr.Zero, null);
        if (raw == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        }

        _handle = new SafeFileHandle(raw, ownsHandle: true);
        JobObjectExtendedLimitInformationStruct info = default;
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)size))
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed");
        }
    }

    /// <summary>Adds a started process. Returns false (and the caller kills the process) when Windows refuses.</summary>
    public bool TryAssign(SafeProcessHandle process) => AssignProcessToJobObject(_handle, process);

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimitInformationStruct info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
