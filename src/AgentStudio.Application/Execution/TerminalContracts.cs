namespace AgentStudio.Application.Execution;

/// <summary>
/// An interactive shell to start on a pseudoconsole. <see cref="Environment"/> entries are merged over the current
/// process environment; a <c>null</c> value removes the variable.
/// </summary>
public sealed record TerminalStartInfo(
    string Shell,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    short Columns,
    short Rows,
    IReadOnlyDictionary<string, string?>? Environment = null);

/// <summary>
/// A shell running on a pseudoconsole. Disposing the session kills the whole process tree if it is still running and
/// releases every OS handle.
/// </summary>
public interface ITerminalSession : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>
    /// Everything the shell writes, as UTF-8 text decoded incrementally, VT escape sequences included. Completes after
    /// the shell has ended and its output has been drained. Meant for a single consumer.
    /// </summary>
    IAsyncEnumerable<string> Output { get; }

    /// <summary>Sends text to the shell's input (keystrokes, pasted text). Throws once the session has ended.</summary>
    ValueTask WriteAsync(string input, CancellationToken cancellationToken);

    /// <summary>Resizes the pseudoconsole. Ignored once the session has ended.</summary>
    void Resize(short columns, short rows);

    /// <summary>Completes with the exit code once the shell has ended and its output has been drained (-1 after <see cref="KillAsync"/>).</summary>
    Task<int> Exited { get; }

    /// <summary>Kills the whole process tree. Idempotent.</summary>
    Task KillAsync();
}

/// <summary>Starts shells on a pseudoconsole.</summary>
public interface ITerminalLauncher
{
    /// <exception cref="System.ComponentModel.Win32Exception">The shell could not be started.</exception>
    ITerminalSession Start(TerminalStartInfo info);
}

/// <summary>A shell installed on this machine. <see cref="Id"/> is stable ("pwsh", "powershell", "cmd", "git-bash").</summary>
public sealed record ShellInfo(string Id, string DisplayName, string Path, IReadOnlyList<string> DefaultArguments);

/// <summary>Finds the shells available on this machine, in order of preference.</summary>
public interface IShellLocator
{
    IReadOnlyList<ShellInfo> GetAvailableShells();
}
