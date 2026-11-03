using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LowIlVerification.Native;

/// <summary>
/// Launches child processes under a Low integrity-level primary token, without admin rights. The token is a duplicate
/// of the caller's own token whose integrity level is lowered to S-1-16-4096; lowering (never raising) one's own token
/// is always allowed, so no privilege is required. Children get a chosen working directory, an explicit environment
/// block, and their stdout+stderr joined into one inheritable file so the Medium parent can read the output afterwards
/// (Low IL is no-write-up, not no-read-down).
/// </summary>
internal sealed class LowIntegrityLauncher : IDisposable
{
    private const string LowIntegritySid = "S-1-16-4096";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private readonly SafeTokenHandle _loweredToken;

    private LowIntegrityLauncher(SafeTokenHandle loweredToken) => _loweredToken = loweredToken;

    /// <summary>Builds the lowered primary token once; reuse it for every child.</summary>
    public static LowIntegrityLauncher Create()
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenDuplicate | NativeMethods.TokenAdjustDefault | NativeMethods.TokenQuery | NativeMethods.TokenAssignPrimary,
                out var own))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed.");
        }

        using (own)
        {
            if (!NativeMethods.DuplicateTokenEx(own, 0, 0, NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out var lowered))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateTokenEx failed.");
            }

            try
            {
                SetIntegrityLevel(lowered, LowIntegritySid);
            }
            catch
            {
                lowered.Dispose();
                throw;
            }

            return new LowIntegrityLauncher(lowered);
        }
    }

    /// <summary>The integrity SID of the current process token, as a string (e.g. S-1-16-8192 for Medium).</summary>
    public static string CurrentIntegritySid()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken failed.");
        }

        using (token)
        {
            return IntegritySidOf(token);
        }
    }

    /// <summary>Starts a Low-IL child. When <paramref name="job"/> is given, the child is created suspended, assigned and resumed.</summary>
    public LowProcess Start(
        string applicationPath,
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environmentOverrides,
        string outputLogPath,
        SafeJobHandle? job = null)
    {
        var inheritable = new NativeMethods.SecurityAttributes
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
            SecurityDescriptor = 0,
            InheritHandle = 1,
        };

        var stdIn = NativeMethods.CreateFile("NUL", GenericRead, FileShareReadWrite, ref inheritable, OpenExisting, FileAttributeNormal, 0);
        var stdOut = NativeMethods.CreateFile(outputLogPath, GenericWrite, FileShareReadWrite, ref inheritable, CreateAlways, FileAttributeNormal, 0);
        if (stdIn.IsInvalid || stdOut.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            stdIn.Dispose();
            stdOut.Dispose();
            throw new Win32Exception(error, "Could not open the child's standard handles.");
        }

        var environmentBlock = BuildEnvironmentBlock(environmentOverrides);
        var commandLineBuffer = Marshal.StringToHGlobalUni(commandLine);
        var startup = new NativeMethods.StartupInfo
        {
            Cb = (uint)Marshal.SizeOf<NativeMethods.StartupInfo>(),
            Flags = NativeMethods.StartfUseStdHandles,
            StdInput = stdIn.DangerousGetHandle(),
            StdOutput = stdOut.DangerousGetHandle(),
            StdError = stdOut.DangerousGetHandle(),
        };

        var flags = NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateNoWindow;
        if (job is not null)
        {
            flags |= NativeMethods.CreateSuspended;
        }

        try
        {
            if (!NativeMethods.CreateProcessAsUser(
                    _loweredToken,
                    applicationPath,
                    commandLineBuffer,
                    0,
                    0,
                    inheritHandles: true,
                    flags,
                    environmentBlock,
                    workingDirectory,
                    ref startup,
                    out var info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcessAsUser failed.");
            }

            if (job is not null)
            {
                if (!NativeMethods.AssignProcessToJobObject(job, info.Process))
                {
                    var error = Marshal.GetLastPInvokeError();
                    NativeMethods.CloseHandle(info.Thread);
                    NativeMethods.CloseHandle(info.Process);
                    throw new Win32Exception(error, "AssignProcessToJobObject failed.");
                }

                NativeMethods.ResumeThread(info.Thread);
            }

            NativeMethods.CloseHandle(info.Thread);
            return new LowProcess(info.Process, info.ProcessId, outputLogPath);
        }
        finally
        {
            Marshal.FreeHGlobal(commandLineBuffer);
            Marshal.FreeHGlobal(environmentBlock);
            stdIn.Dispose();
            stdOut.Dispose();
        }
    }

    /// <summary>
    /// Starts a Low-IL child inside its own kill-on-close Job Object and waits for it. Closing the job at the end kills
    /// whatever the child left behind (an MSBuild node, a compiler server, a detached grandchild), and also enforces the
    /// timeout for the whole tree. Returns the exit code, elapsed time and captured output.
    /// </summary>
    public LowRunResult Run(
        string applicationPath,
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environmentOverrides,
        string outputLogPath,
        TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        var job = new KillOnCloseJob();
        LowProcess? child = null;
        var finished = false;
        uint exit = 0;
        try
        {
            child = Start(applicationPath, commandLine, workingDirectory, environmentOverrides, outputLogPath, job.Handle);
            finished = child.Wait(timeout);
            if (finished)
            {
                exit = child.ExitCode();
            }
        }
        finally
        {
            job.Dispose(); // Kills every process still in the job (the whole tree on timeout).
            child?.Dispose();
        }

        var elapsed = DateTimeOffset.UtcNow - start;
        return finished
            ? new LowRunResult((int)exit, TimedOut: false, elapsed, SafeReadLog(outputLogPath))
            : new LowRunResult(-1, TimedOut: true, elapsed, SafeReadLog(outputLogPath));
    }

    public void Dispose() => _loweredToken.Dispose();

    internal static string SafeReadLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void SetIntegrityLevel(SafeTokenHandle token, string integritySid)
    {
        if (!NativeMethods.ConvertStringSidToSid(integritySid, out var sid))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertStringSidToSid failed.");
        }

        // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label { PSID Sid; DWORD Attributes; } }. On x64 the pointer forces
        // 8-byte alignment, so sizeof is 16 (pointer + DWORD + 4 padding); passing less fails with ERROR_BAD_LENGTH (24).
        var tmlSize = IntPtr.Size * 2;
        var buffer = Marshal.AllocHGlobal(tmlSize);
        try
        {
            for (var i = 0; i < tmlSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteIntPtr(buffer, 0, sid);
            Marshal.WriteInt32(buffer, IntPtr.Size, (int)NativeMethods.SeGroupIntegrity);
            if (!NativeMethods.SetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, (uint)tmlSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetTokenInformation(TokenIntegrityLevel) failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            NativeMethods.LocalFree(sid);
        }
    }

    private static string IntegritySidOf(SafeTokenHandle token)
    {
        NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, 0, 0, out var needed);
        if (needed == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetTokenInformation returned no size.");
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buffer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetTokenInformation failed.");
            }

            var sid = Marshal.ReadIntPtr(buffer, 0); // TOKEN_MANDATORY_LABEL.Label.Sid
            if (!NativeMethods.ConvertSidToStringSid(sid, out var stringSid))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertSidToStringSid failed.");
            }

            try
            {
                return Marshal.PtrToStringUni(stringSid) ?? string.Empty;
            }
            finally
            {
                NativeMethods.LocalFree(stringSid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static nint BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (!string.IsNullOrEmpty(key) && !key.Contains('=', StringComparison.Ordinal))
            {
                merged[key] = entry.Value?.ToString() ?? string.Empty;
            }
        }

        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                merged.Remove(key);
            }
            else
            {
                merged[key] = value;
            }
        }

        var builder = new StringBuilder();
        foreach (var key in merged.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(key).Append('=').Append(merged[key]).Append('\0');
        }

        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }
}

/// <summary>A started Low-IL child. Owns its process handle.</summary>
internal sealed class LowProcess(nint process, uint processId, string outputLogPath) : IDisposable
{
    public uint ProcessId => processId;

    public string OutputLogPath => outputLogPath;

    /// <summary>Waits up to <paramref name="timeout"/>; true when the process ended.</summary>
    public bool Wait(TimeSpan timeout)
    {
        var millis = timeout == Timeout.InfiniteTimeSpan ? NativeMethods.Infinite : (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
        return NativeMethods.WaitForSingleObject(process, millis) == NativeMethods.WaitObject0;
    }

    public bool IsRunning()
    {
        return NativeMethods.GetExitCodeProcess(process, out var code) && code == NativeMethods.StillActive;
    }

    public uint ExitCode()
    {
        NativeMethods.GetExitCodeProcess(process, out var code);
        return code;
    }

    public void Dispose() => NativeMethods.CloseHandle(process);
}

internal readonly record struct LowRunResult(int ExitCode, bool TimedOut, TimeSpan Elapsed, string Output);
