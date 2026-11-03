using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Infrastructure.Processes;

/// <summary>
/// A Windows Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process assigned to it (and its
/// descendants) is killed by the OS when the last handle to the job closes, which happens when Agent Studio exits
/// for any reason. No-op on other platforms.
/// </summary>
public sealed partial class JobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private static readonly Lazy<JobObject> SharedInstance = new(() => new JobObject(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly SafeJobHandle? _handle;

    /// <summary>Creates a job. Use <see cref="Shared"/> for the one tied to the Agent Studio process.</summary>
    public JobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _handle = Create();
        }
        catch (Win32Exception ex)
        {
            Trace.TraceWarning("Agent Studio: could not create the Job Object ({0}); child processes will only be killed explicitly.", ex.Message);
        }
    }

    /// <summary>The lazily created job of this Agent Studio process.</summary>
    public static JobObject Shared => SharedInstance.Value;

    /// <summary>
    /// Assigns a started process to the job. Returns false (after logging) when the platform is not Windows or the
    /// assignment fails, e.g. the process already left or belongs to a job that forbids nesting; callers then rely on
    /// killing the tree explicitly.
    /// </summary>
    public bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return TryAssign(process.SafeHandle);
        }
        catch (InvalidOperationException)
        {
            return false; // The process already exited.
        }
    }

    /// <summary>Same as <see cref="TryAssign(Process)"/> for a process known only by its handle (e.g. a ConPTY shell).</summary>
    public bool TryAssign(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows() || _handle is null || _handle.IsClosed)
        {
            return false;
        }

        try
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle, process))
            {
                Trace.TraceWarning("Agent Studio: could not assign process to the Job Object ({0}).", new Win32Exception(Marshal.GetLastPInvokeError()).Message);
                return false;
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // The process already exited.
        }
    }

    /// <summary>Closes the job handle; the OS then kills every process still in the job.</summary>
    public void Dispose() => _handle?.Dispose();

    [SupportedOSPlatform("windows")]
    private static SafeJobHandle Create()
    {
        var handle = NativeMethods.CreateJobObject(0, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var info = default(ExtendedLimitInformation);
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!NativeMethods.SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        [SupportedOSPlatform("windows")]
        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [SupportedOSPlatform("windows")]
    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeJobHandle CreateJobObject(nint attributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(nint handle);
    }
}
