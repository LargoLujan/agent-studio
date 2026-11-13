using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Infrastructure.Hosting.Cli;

/// <summary>How a launch of <c>gh api</c> ended.</summary>
internal enum GhRunStatus
{
    /// <summary>There was a status line: the answer of GitHub, whatever the exit code of <c>gh</c>.</summary>
    Response,

    /// <summary><c>gh.exe</c> is nowhere outside the open projects.</summary>
    NotInstalled,

    /// <summary>No status line and exit code 4: <c>gh</c> says it needs authentication.</summary>
    SignInNeeded,

    /// <summary>No status line (and not 4): the server was not reached, or <c>gh</c> could not start.</summary>
    NoResponse,

    /// <summary>The output is past the limit, or is not the shape of an answer.</summary>
    InvalidAnswer,

    /// <summary><c>gh</c> did not finish in 20 seconds.</summary>
    TimedOut,
}

internal sealed record GhRunResult(GhRunStatus Status, GhHttpResponse? Http = null);

/// <summary>
/// The one launcher of <c>gh</c> for GitHub (docs/21 D-10 to D-12), and it only ever launches <c>gh api</c> (the version goes through the door of Auto Doctor): the executable is
/// <c>gh.exe</c> resolved by <see cref="ExecutableSearch"/> outside every untrusted root, the working folder is the neutral one, the environment is the allow list of
/// <see cref="GhEnvironment"/>, the body goes through standard input, there are 20 seconds and 1 MB per answer, and the standard error is never read into anything: only the exit
/// code 4 and the status line count. It does not queue and it does not know the limit: <see cref="GhQueue"/> does, and every call goes through it.
/// </summary>
internal sealed class GhRunner
{
    /// <summary>How long <c>gh</c> has to answer (D-11).</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The most characters of each output stream: 1 MB.</summary>
    internal const int MaxOutputCharacters = 1024 * 1024;

    /// <summary>The exit code with which <c>gh</c> says a command requires authentication (<c>gh help exit-codes</c>).</summary>
    internal const int AuthenticationExitCode = 4;

    private readonly IProcessRunner _runner;
    private readonly GhFolders _folders;
    private readonly IUntrustedRoots? _roots;
    private readonly ExecutableSearch _search;
    private readonly Func<IReadOnlyDictionary<string, string>> _ambient;
    private readonly TimeProvider _time;

    /// <param name="roots">The projects that were open: neither <c>gh.exe</c> nor a configuration folder is taken from inside one of them.</param>
    /// <param name="pathVariable">The PATH to look for <c>gh.exe</c> on; the one of the process when null.</param>
    /// <param name="ambient">The variables of the process, to remove the ones that are not allowed; the real ones by default.</param>
    internal GhRunner(
        IProcessRunner runner,
        GhFolders folders,
        IUntrustedRoots? roots = null,
        Func<string?>? pathVariable = null,
        Func<IReadOnlyDictionary<string, string>>? ambient = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(folders);
        _runner = runner;
        _folders = folders;
        _roots = roots;
        _search = new ExecutableSearch(roots, pathVariable);
        _ambient = ambient ?? DoctorEnvironment.CurrentProcess;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The full path of <c>gh.exe</c> outside the untrusted roots, or null. Only an <c>.exe</c>: a shim of cmd would re-read the arguments.</summary>
    internal string? FindExecutable() => _search.Find(_search.PathDirectories(), ["gh.exe"], out _);

    internal async Task<GhRunResult> RunAsync(GhCall call, CancellationToken ct)
    {
        var arguments = GhArguments.Build(call);
        var path = FindExecutable();
        if (path is null)
        {
            return new GhRunResult(GhRunStatus.NotInstalled);
        }

        var spec = new ProcessSpec(
            path,
            arguments,
            _folders.WorkingFolder,
            GhEnvironment.For(_folders, _ambient(), _roots),
            Timeout,
            StandardInput: call.Body,
            MaxOutputCharacters: MaxOutputCharacters);
        ProcessResult result;
        try
        {
            _folders.Ensure();
            result = await _runner.RunAsync(spec, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A gh that cannot start is an answer (no response), never a failure of the caller: the message of the exception is not kept.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new GhRunResult(GhRunStatus.NoResponse);
        }

        if (result.TimedOut)
        {
            return new GhRunResult(GhRunStatus.TimedOut);
        }

        if (result.OutputLimitExceeded)
        {
            return new GhRunResult(GhRunStatus.InvalidAnswer);
        }

        if (GhResponseReader.Read(result.StandardOutput, _time.GetUtcNow()) is { } http)
        {
            return new GhRunResult(GhRunStatus.Response, http);
        }

        if (result.ExitCode == AuthenticationExitCode)
        {
            return new GhRunResult(GhRunStatus.SignInNeeded);
        }

        // Something on the standard output that is not an answer is a strange answer; nothing at all is a server that was not reached (D-26).
        return new GhRunResult(string.IsNullOrWhiteSpace(result.StandardOutput) ? GhRunStatus.NoResponse : GhRunStatus.InvalidAnswer);
    }
}
