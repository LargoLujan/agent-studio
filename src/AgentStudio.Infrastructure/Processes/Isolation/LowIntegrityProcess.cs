using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentStudio.Application.Execution;
using Microsoft.Win32.SafeHandles;
using static AgentStudio.Infrastructure.Processes.Isolation.IsolationNativeMethods;

namespace AgentStudio.Infrastructure.Processes.Isolation;

/// <summary>
/// A process started with <see cref="ProcessIsolation.LowIntegrity"/> (ADR-0017), following the spike
/// (docs/investigacion/2026-10-16-spike-verificacion-aislada.md):
/// <list type="number">
/// <item>its token is a copy of the token of Agent Studio lowered to Low (<see cref="LowIntegrityToken"/>);</item>
/// <item>its standard input, output and error are anonymous pipes, and <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> makes those
/// three the only handles it inherits: a handle inherited from Agent Studio would bypass the integrity check, which
/// Windows only makes when a handle is opened;</item>
/// <item>it is created suspended, put in a kill-on-close Job Object of its own and only then resumed, so nothing it starts
/// runs outside the job; killing it terminates the job, and disposing it closes the job, which kills whatever is left.</item>
/// </list>
/// The executable is resolved to a full path first, in the order <c>CreateProcess</c> uses for a process without isolation
/// (the system folder, the Windows folder, then the PATH, adding <c>.exe</c> to a name without extension) except that
/// neither the folder of Agent Studio nor its current folder are searched, only fully qualified PATH entries are, and nothing
/// that lies inside an untrusted root is taken (<see cref="ExecutableSearch"/>, the rule the locator shares).
/// Any step that fails throws and starts nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class LowIntegrityProcess : IStartedProcess
{
    private readonly SafeProcessHandle _process;
    private readonly SafeJobHandle _job;
    private readonly AnonymousPipeServerStream? _stdinPipe;
    private readonly AnonymousPipeServerStream _stdoutPipe;
    private readonly AnonymousPipeServerStream _stderrPipe;
    private readonly StreamWriter? _stdin;
    private readonly WaitHandle _exitHandle;
    private readonly RegisteredWaitHandle _exitRegistration;
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private LowIntegrityProcess(
        SafeProcessHandle process,
        int id,
        SafeJobHandle job,
        AnonymousPipeServerStream? stdin,
        AnonymousPipeServerStream stdout,
        AnonymousPipeServerStream stderr)
    {
        _process = process;
        Id = id;
        _job = job;
        _stdinPipe = stdin;
        _stdoutPipe = stdout;
        _stderrPipe = stderr;

        // The same readers and writer System.Diagnostics.Process builds over its pipes.
        StandardOutput = new StreamReader(stdout, ProcessLaunch.Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
        StandardError = new StreamReader(stderr, ProcessLaunch.Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
        _stdin = stdin is null ? null : new StreamWriter(stdin, ProcessLaunch.Utf8NoBom, bufferSize: 4096) { AutoFlush = true };

        _exitHandle = new ExitWaitHandle(process);
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitHandle, (_, _) => _exit.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int Id { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    public StreamWriter StandardInput => _stdin ?? throw new InvalidOperationException("The process was started without standard input.");

    public bool HasExited
    {
        get
        {
            try
            {
                return WaitForSingleObject(_process, 0) == WaitObject0;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }

    public int ExitCode =>
        GetExitCodeProcess(_process, out var code)
            ? unchecked((int)code)
            : throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exit code of the isolated process could not be read.");

    /// <summary>Starts <paramref name="spec"/> at Low integrity. Its arguments were already validated.</summary>
    /// <exception cref="ProcessIsolationException">The isolation could not be applied; nothing runs.</exception>
    /// <exception cref="Win32Exception">The executable was not found or could not be started.</exception>
    internal static LowIntegrityProcess Start(ProcessSpec spec, ExecutableSearch search)
    {
        var executable = ResolveExecutable(spec, search);
        var commandLine = WindowsCommandLine.Build(executable, spec.Arguments);
        var environment = WindowsCommandLine.EnvironmentBlock(search.EnvironmentFor(spec));

        using var token = LowIntegrityToken.Create();
        var job = CreateJob(spec.MemoryLimitBytes);
        AnonymousPipeServerStream? stdin = null, stdout = null, stderr = null;
        try
        {
            stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

            var info = StartSuspended(token, executable, commandLine, environment, spec.WorkingDirectory, stdin.ClientSafePipeHandle, stdout.ClientSafePipeHandle, stderr.ClientSafePipeHandle);
            var process = new SafeProcessHandle(info.Process, ownsHandle: true);
            try
            {
                if (!AssignProcessToJobObject(job, process))
                {
                    var error = new Win32Exception(Marshal.GetLastPInvokeError());
                    TerminateProcess(process, 1);
                    throw new ProcessIsolationException(
                        $"The isolated process could not be put in its Job Object ({error.Message}); it was terminated before it ran.", error);
                }

                if (ResumeThread(info.Thread) == uint.MaxValue)
                {
                    var error = new Win32Exception(Marshal.GetLastPInvokeError());
                    TerminateJobObject(job, 1);
                    throw new ProcessIsolationException($"The isolated process could not be resumed ({error.Message}); it was terminated.", error);
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }
            finally
            {
                CloseHandle(info.Thread);
            }

            // The child holds its ends now; without ours the pipes reach their end when the tree is gone.
            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            if (spec.StandardInput is null)
            {
                // Nothing to write: the child reads the end of its input at once.
                stdin.Dispose();
                stdin = null;
            }

            try
            {
                return new LowIntegrityProcess(process, unchecked((int)info.ProcessId), job, stdin, stdout, stderr);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
        catch
        {
            stdin?.Dispose();
            stdout?.Dispose();
            stderr?.Dispose();
            job.Dispose();
            throw;
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void KillTree()
    {
        try
        {
            TerminateJobObject(_job, 1);
        }
        catch (ObjectDisposedException)
        {
            // Disposed: the job is closed and its processes are dead.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _exitRegistration.Unregister(null);
        _exitHandle.Dispose();

        // Closing the only handle of the job kills whatever is still in it (an MSBuild node, a detached grandchild).
        _job.Dispose();
        _process.Dispose();
        _stdin?.Dispose();
        _stdinPipe?.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _stdoutPipe.Dispose();
        _stderrPipe.Dispose();
    }

    private static string ResolveExecutable(ProcessSpec spec, ExecutableSearch search)
    {
        var name = spec.FileName;
        if (Path.IsPathFullyQualified(name))
        {
            return name;
        }

        if (name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Path.GetFullPath(name, spec.WorkingDirectory ?? Environment.CurrentDirectory);
        }

        // A bare name: the system folder, the Windows folder and the PATH, outside the untrusted roots (the shared rule).
        return search.ResolveBareName(name);
    }

    private static SafeJobHandle CreateJob(long? memoryLimitBytes)
    {
        var job = IsolationNativeMethods.CreateJobObject(0, 0);
        if (job.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            job.Dispose();
            throw new ProcessIsolationException($"The Job Object of the isolated process could not be created ({error.Message}); nothing was started.", error);
        }

        // Kill on close contains the tree; no crash dialog of Windows Error Reporting ever waits on the desktop.
        var info = default(JobObjectExtendedLimitInformation);
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException;
        if (memoryLimitBytes is > 0)
        {
            // Only a process that asks (Auto Graph reads files that are not trusted): the memory one process may commit, so a hostile file cannot eat the RAM of the person.
            info.BasicLimitInformation.LimitFlags |= JobObjectLimitProcessMemory;
            info.ProcessMemoryLimit = (nuint)memoryLimitBytes.Value;
        }

        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            job.Dispose();
            throw new ProcessIsolationException($"The Job Object of the isolated process could not be configured ({error.Message}); nothing was started.", error);
        }

        return job;
    }

    private static ProcessInformation StartSuspended(
        SafeTokenHandle token,
        string executable,
        string commandLine,
        string environment,
        string? workingDirectory,
        SafePipeHandle stdin,
        SafePipeHandle stdout,
        SafePipeHandle stderr)
    {
        SafeHandle[] inherited = [stdin, stdout, stderr];
        var added = 0;
        nint attributes = 0, handles = 0, commandLineBuffer = 0, environmentBuffer = 0;
        var initialized = false;
        try
        {
            foreach (var handle in inherited)
            {
                var ok = false;
                handle.DangerousAddRef(ref ok);
                added++;
            }

            nint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw HandleListFailure();
            }

            initialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size * inherited.Length);
            for (var i = 0; i < inherited.Length; i++)
            {
                Marshal.WriteIntPtr(handles, i * IntPtr.Size, inherited[i].DangerousGetHandle());
            }

            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handles, IntPtr.Size * inherited.Length, 0, 0))
            {
                throw HandleListFailure();
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StdInput = stdin.DangerousGetHandle(),
                    StdOutput = stdout.DangerousGetHandle(),
                    StdError = stderr.DangerousGetHandle(),
                },
                AttributeList = attributes,
            };

            commandLineBuffer = Marshal.StringToHGlobalUni(commandLine);
            environmentBuffer = Marshal.StringToHGlobalUni(environment);
            const uint Flags = CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow | ExtendedStartupInfoPresent;
            if (!CreateProcessAsUser(token, null, commandLineBuffer, 0, 0, inheritHandles: true, Flags, environmentBuffer, workingDirectory, ref startup, out var info))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, $"The isolated process '{executable}' could not be started: {new Win32Exception(error).Message}");
            }

            return info;
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(commandLineBuffer);
            Marshal.FreeHGlobal(environmentBuffer);
            for (var i = 0; i < added; i++)
            {
                inherited[i].DangerousRelease();
            }
        }
    }

    private static ProcessIsolationException HandleListFailure()
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new ProcessIsolationException($"The handles the isolated process inherits could not be limited to its standard ones ({error.Message}); nothing was started.", error);
    }

    /// <summary>A wait handle over a duplicate of the process handle, as <see cref="System.Diagnostics.Process"/> uses.</summary>
    private sealed class ExitWaitHandle : WaitHandle
    {
        public ExitWaitHandle(SafeProcessHandle process)
        {
            var self = IsolationNativeMethods.GetCurrentProcess();
            if (!DuplicateHandle(self, process, self, out var duplicate, 0, inheritHandle: false, DuplicateSameAccess))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The handle of the isolated process could not be duplicated to wait for it.");
            }

            SafeWaitHandle = duplicate;
        }
    }
}
