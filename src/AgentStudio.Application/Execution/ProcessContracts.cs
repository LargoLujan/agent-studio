namespace AgentStudio.Application.Execution;

/// <summary>
/// A process to run. Arguments are passed as a list and never concatenated into a command line by callers.
/// Free text (prompts, file contents) must go through <see cref="StandardInput"/>, never through arguments: on
/// Windows, npm-installed CLIs are <c>.cmd</c> shims and cmd.exe re-parses their arguments.
/// </summary>
/// <param name="Isolation">
/// How the process is kept from writing the user's files (ADR-0017). Only verification asks for it; the default runs the
/// process as before.
/// </param>
/// <param name="KeepStandardInputOpen">
/// Only for a streaming session (<c>IStreamingProcessRunner</c>): <see cref="StandardInput"/> is written but not closed,
/// for a server that ends at the end of its input before it answers (the Codex app-server, docs/14 D-15). Disposing the
/// session ends the tree. The batch runner refuses it: it would wait for the timeout.
/// </param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    string? StandardInput = null,
    ProcessIsolation Isolation = ProcessIsolation.None,
    bool KeepStandardInputOpen = false);

/// <summary>How a process is kept from writing the user's files (ADR-0017).</summary>
public enum ProcessIsolation
{
    /// <summary>The token of Agent Studio itself: the user at Medium integrity, who can write wherever the user can.</summary>
    None,

    /// <summary>
    /// A copy of that token lowered to Low integrity (S-1-16-4096), in a kill-on-close Job Object of its own and
    /// inheriting only its standard input, output and error. Windows then refuses its writes to every file, folder and
    /// registry key that is not labelled Low. It still reads whatever the user can read and still has the network. When
    /// it cannot be applied the start fails with <see cref="ProcessIsolationException"/>: it never falls back to
    /// <see cref="None"/>.
    /// </summary>
    LowIntegrity,
}

/// <summary>An isolation that was asked for could not be applied, so nothing was started with fewer restrictions.</summary>
public sealed class ProcessIsolationException : InvalidOperationException
{
    public ProcessIsolationException(string message)
        : base(message)
    {
    }

    public ProcessIsolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration, bool TimedOut);

/// <summary>Runs a real OS process to completion. Implementations kill the whole process tree on timeout or cancellation.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the process. On timeout the tree is killed and the result has <see cref="ProcessResult.TimedOut"/> set;
    /// on cancellation the tree is killed and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

/// <summary>Finds an executable on the PATH, honouring PATHEXT on Windows (claude.exe, codex.cmd...).</summary>
public interface IExecutableLocator
{
    /// <summary>Full path of the first match, or null when the command is not installed.</summary>
    string? Find(string command);
}
