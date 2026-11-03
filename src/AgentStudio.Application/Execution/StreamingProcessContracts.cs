namespace AgentStudio.Application.Execution;

// Phase 2 contract: long-running processes whose output is consumed line by line (agent CLIs emitting JSONL).

/// <summary>A line written by the process, tagged with the stream it came from.</summary>
public sealed record ProcessOutputLine(ProcessStream Stream, string Text, DateTimeOffset At);

public enum ProcessStream
{
    StandardOutput,
    StandardError,
}

/// <summary>
/// A running process. Output lines arrive as they are written. Disposing the session kills the whole process tree
/// if it is still running; implementations also tie child processes to Agent Studio so they die with it.
/// </summary>
public interface IProcessSession : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>All output lines (stdout and stderr) in arrival order. Completes when both streams close.</summary>
    IAsyncEnumerable<ProcessOutputLine> Output { get; }

    /// <summary>Completes with the exit code when the process ends (after the tree is killed, with -1).</summary>
    Task<int> Exited { get; }

    /// <summary>Kills the whole process tree. Idempotent.</summary>
    Task KillAsync();
}

/// <summary>
/// Starts processes for streaming. Same argument rules as <see cref="IProcessRunner"/>: arguments as a list, free
/// text only through <see cref="ProcessSpec.StandardInput"/> (written and closed right after start), and cmd.exe
/// metacharacters rejected in arguments of .cmd/.bat files. <see cref="ProcessSpec.Timeout"/>, when set, kills the
/// tree once it elapses.
/// </summary>
public interface IStreamingProcessRunner
{
    Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken);
}
