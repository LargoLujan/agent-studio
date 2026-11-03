using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Infrastructure.Terminal;

/// <summary>Starts shells on a Windows pseudoconsole (ConPTY).</summary>
public sealed class ConPtyTerminalLauncher : ITerminalLauncher
{
    private readonly JobObject _job;

    /// <param name="job">Job Object that owns the shells; defaults to the one tied to the Agent Studio process.</param>
    public ConPtyTerminalLauncher(JobObject? job = null) => _job = job ?? JobObject.Shared;

    public ITerminalSession Start(TerminalStartInfo info)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The terminal needs the Windows pseudoconsole (ConPTY).");
        }

        return ConPtyTerminalSession.Start(info, _job);
    }
}
