using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Infrastructure.Terminal;

/// <summary>
/// A shell attached to a Windows pseudoconsole (ConPTY). Output is read on a dedicated thread and decoded with a
/// stateful UTF-8 decoder so multi-byte characters split across reads survive.
/// </summary>
/// <remarks>
/// Shutdown order matters: the pseudoconsole is closed only after the shell has exited, on a worker thread, while the
/// reader keeps draining the output pipe. Closing it from the thread that should be draining deadlocks on Windows
/// builds where <c>ClosePseudoConsole</c> waits for the output to be consumed; closing it before the pipes (and never
/// closing the pipes first) also makes the final frame of output arrive.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ConPtyTerminalSession : ITerminalSession
{
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(10);

    private readonly SafePseudoConsoleHandle _console;
    private readonly SafeProcessHandle _process;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _readerTask;
    private readonly Task _monitorTask;

    private volatile bool _ended;
    private int _killRequested;
    private int _disposed;

    private ConPtyTerminalSession(
        SafePseudoConsoleHandle console,
        SafeProcessHandle process,
        int processId,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead)
    {
        _console = console;
        _process = process;
        ProcessId = processId;
        // Unbuffered writes: every keystroke must reach the shell immediately.
        _input = new FileStream(inputWrite, FileAccess.Write, bufferSize: 0, isAsync: false);
        _output = new FileStream(outputRead, FileAccess.Read, bufferSize: 0, isAsync: false);
        _readerTask = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _monitorTask = Task.Factory.StartNew(MonitorAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public int ProcessId { get; }

    public IAsyncEnumerable<string> Output => _channel.Reader.ReadAllAsync();

    public Task<int> Exited => _exited.Task;

    public static ConPtyTerminalSession Start(TerminalStartInfo info, JobObject job)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.Shell);
        ArgumentNullException.ThrowIfNull(info.Arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(info.Columns, (short)0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(info.Rows, (short)0);

        var commandLine = WindowsCommandLine.Build(info.Shell, info.Arguments);
        var baseline = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            baseline[(string)entry.Key] = entry.Value as string;
        }

        var environmentBlock = WindowsCommandLine.BuildEnvironmentBlock(baseline, info.Environment);

        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafePseudoConsoleHandle? console = null;
        SafeProcessHandle? process = null;
        nint attributeList = 0;
        var attributeListInitialized = false;
        nint commandLinePointer = 0;
        nint environmentPointer = 0;
        var succeeded = false;
        try
        {
            if (!NativeMethods.CreatePipe(out inputRead, out inputWrite, 0, 0)
                || !NativeMethods.CreatePipe(out outputRead, out outputWrite, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var size = new NativeMethods.Coord { X = info.Columns, Y = info.Rows };
            var hr = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out console);
            if (hr != 0)
            {
                throw new Win32Exception(hr, $"CreatePseudoConsole failed (HRESULT 0x{hr:X8}).");
            }

            // The pseudoconsole duplicated the ends it needs; keeping ours open would stop the pipes from breaking.
            inputRead.Dispose();
            outputWrite.Dispose();

            nint attributeListSize = 0;
            // The first call is expected to fail with ERROR_INSUFFICIENT_BUFFER and report the size.
            _ = NativeMethods.InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            attributeListInitialized = true;
            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList, 0, NativeMethods.ProcThreadAttributePseudoConsole, console.DangerousGetHandle(), nint.Size, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startupInfo = new NativeMethods.StartupInfoEx { AttributeList = attributeList };
            startupInfo.StartupInfo.Cb = Unsafe.SizeOf<NativeMethods.StartupInfoEx>();
            // When this process has redirected standard handles (a test host, a service), the shell would otherwise
            // inherit them and print to our stdout instead of the pseudoconsole. Explicitly invalid std handles make
            // it use the pseudoconsole.
            startupInfo.StartupInfo.Flags = NativeMethods.UseStdHandles;
            startupInfo.StartupInfo.StdInput = NativeMethods.InvalidHandleValue;
            startupInfo.StartupInfo.StdOutput = NativeMethods.InvalidHandleValue;
            startupInfo.StartupInfo.StdError = NativeMethods.InvalidHandleValue;

            commandLinePointer = Marshal.StringToHGlobalUni(commandLine);
            environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
            var workingDirectory = string.IsNullOrWhiteSpace(info.WorkingDirectory) ? null : info.WorkingDirectory;

            if (!NativeMethods.CreateProcess(
                    null,
                    commandLinePointer,
                    0,
                    0,
                    false,
                    NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateSuspended,
                    environmentPointer,
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot start '{info.Shell}'.");
            }

            process = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            // The shell starts suspended so it is in the job before it can spawn anything: if Agent Studio dies, the
            // OS kills the shell and every descendant. A failed assignment is logged by the job; KillAsync still
            // kills the tree explicitly.
            job.TryAssign(process);
            var resumed = NativeMethods.ResumeThread(processInformation.Thread);
            var resumeError = Marshal.GetLastPInvokeError();
            _ = NativeMethods.CloseHandle(processInformation.Thread);
            if (resumed == uint.MaxValue)
            {
                using var stuck = Process.GetProcessById(processInformation.ProcessId);
                stuck.Kill(entireProcessTree: true);
                throw new Win32Exception(resumeError, $"Cannot resume '{info.Shell}'.");
            }

            var session = new ConPtyTerminalSession(console, process, processInformation.ProcessId, inputWrite, outputRead);
            succeeded = true;
            return session;
        }
        finally
        {
            if (attributeListInitialized)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != 0)
            {
                Marshal.FreeHGlobal(attributeList);
            }

            if (commandLinePointer != 0)
            {
                Marshal.FreeHGlobal(commandLinePointer);
            }

            if (environmentPointer != 0)
            {
                Marshal.FreeHGlobal(environmentPointer);
            }

            inputRead?.Dispose();
            outputWrite?.Dispose();
            if (!succeeded)
            {
                console?.Dispose();
                process?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
            }
        }
    }

    public async ValueTask WriteAsync(string input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_ended)
        {
            throw new InvalidOperationException("The terminal session has ended.");
        }

        var bytes = Encoding.UTF8.GetBytes(input);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (_ended)
        {
            throw new InvalidOperationException("The terminal session has ended.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Resize(short columns, short rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(columns, (short)0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rows, (short)0);
        if (_ended)
        {
            return;
        }

        try
        {
            var hr = NativeMethods.ResizePseudoConsole(_console, new NativeMethods.Coord { X = columns, Y = rows });
            if (hr != 0 && !_ended)
            {
                throw new Win32Exception(hr, $"ResizePseudoConsole failed (HRESULT 0x{hr:X8}).");
            }
        }
        catch (ObjectDisposedException)
        {
            // The pseudoconsole was closed because the shell ended meanwhile.
        }
    }

    public async Task KillAsync()
    {
        if (_ended)
        {
            return;
        }

        Interlocked.Exchange(ref _killRequested, 1);
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or AggregateException)
        {
            // Already gone (or part of the tree is): the exit monitor reports the outcome.
        }

        try
        {
            await _exited.Task.WaitAsync(KillWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Reported through Exited when it eventually completes.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await KillAsync().ConfigureAwait(false);
        try
        {
            await _monitorTask.WaitAsync(DisposeWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Something keeps the shell alive: leave the handles to their finalizers rather than hang the caller.
            return;
        }

        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
        _console.Dispose();
        _writeLock.Dispose();
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int read;
            while ((read = _output.Read(bytes, 0, bytes.Length)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0)
                {
                    _channel.Writer.TryWrite(new string(chars, 0, count));
                }
            }
        }
        catch (IOException)
        {
            // Broken pipe: the pseudoconsole was closed.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private async Task MonitorAsync()
    {
        var exitCode = -1;
        try
        {
            _ = NativeMethods.WaitForSingleObject(_process, NativeMethods.WaitInfinite);
            if (NativeMethods.GetExitCodeProcess(_process, out var code))
            {
                exitCode = code;
            }

            _ended = true;
            // Close the pseudoconsole off this thread chain and keep draining meanwhile (see the class remarks).
            await Task.Run(_console.Dispose).ConfigureAwait(false);
            await _readerTask.ConfigureAwait(false);
        }
        finally
        {
            _ended = true;
            _exited.TrySetResult(Volatile.Read(ref _killRequested) == 1 ? -1 : exitCode);
        }
    }
}
