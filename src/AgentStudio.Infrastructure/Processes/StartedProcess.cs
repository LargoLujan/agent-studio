using System.ComponentModel;
using System.Diagnostics;

namespace AgentStudio.Infrastructure.Processes;

/// <summary>
/// A process <see cref="ProcessLaunch"/> started, whatever its isolation: the runners read its streams and wait for it
/// the same way for a <see cref="Process"/> and for an isolated one.
/// </summary>
internal interface IStartedProcess : IDisposable
{
    int Id { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    /// <summary>Only when the spec has standard input.</summary>
    StreamWriter StandardInput { get; }

    /// <summary>True once the process ended (or it can no longer be asked).</summary>
    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the whole process tree. Never throws.</summary>
    void KillTree();
}

/// <summary>A process started without isolation: <see cref="Process"/>, exactly as before.</summary>
internal sealed class ManagedProcess(Process process) : IStartedProcess
{
    public int Id => process.Id;

    public StreamReader StandardOutput => process.StandardOutput;

    public StreamReader StandardError => process.StandardError;

    public StreamWriter StandardInput => process.StandardInput;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

    public void KillTree()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Access denied or already exiting: nothing more can be done.
        }
    }

    public void Dispose() => process.Dispose();
}
