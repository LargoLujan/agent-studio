using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LowIlVerification.Native;

/// <summary>
/// A Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, mirroring
/// <c>src/AgentStudio.Infrastructure/Processes/JobObject.cs</c>. Closing the handle makes the OS kill every process
/// still in the job. The spike uses it to show a Low-IL child (and its tree) is contained exactly like a Medium one.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private readonly SafeJobHandle _handle;

    public KillOnCloseJob()
    {
        _handle = NativeMethods.CreateJobObject(0, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var info = default(NativeMethods.JobObjectExtendedLimitInformation);
        info.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        if (!NativeMethods.SetInformationJobObject(
                _handle,
                NativeMethods.JobObjectExtendedLimitInformationClass,
                ref info,
                (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }
    }

    public SafeJobHandle Handle => _handle;

    /// <summary>Closes the job handle; the OS then kills every process still in the job.</summary>
    public void Dispose() => _handle.Dispose();
}
