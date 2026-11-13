using System.ComponentModel;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>
/// The remote executor of git (docs/21 D-03 to D-08, ADR-0064): the only component of Agent Studio that opens a transport with git. It is separate from the internal layer (<c>GitCommandRunner</c> is not used here, and an
/// architecture test says so); it runs three operations of a closed list and nothing else: the inspection of the configuration (and of the address), <c>ls-remote</c> of a branch and the <c>push</c> of one commit to
/// <c>refs/heads/agentstudio/run-&lt;id&gt;</c>.
/// <list type="bullet">
/// <item><c>git.exe</c> is found with the hardened locator, must be an <c>.exe</c> outside every untrusted root and outside the project, and is asked again on every use.</item>
/// <item>The working folder is the repository (git cannot publish otherwise); the environment is the allow list of <see cref="RemoteGitEnvironment"/>.</item>
/// <item>Credentials are the person's: the helper of their global and system configuration. Nothing is read, passed or logged here.</item>
/// <item>The output of git is not trusted: it is read by structure with a cap, and failures are codes (<see cref="RemoteGitCodes"/>), never its text.</item>
/// </list>
/// </summary>
internal sealed class RemoteGitRunner : IRemoteGitRunner
{
    private readonly IProcessRunner _runner;
    private readonly IExecutableLocator _locator;
    private readonly RemoteGitFolders _folders;
    private readonly string _worktreeRoot;
    private readonly Func<IReadOnlyDictionary<string, string>> _ambient;

    /// <param name="worktreeRoot">The root of the worktrees of Agent Studio: a configuration file that lives there is not trusted.</param>
    /// <param name="transport">The transport: <see cref="RemoteTransport.GitHubHttps"/> in production; the tests give a transport of their own.</param>
    /// <param name="ambient">The environment variables the process has, to remove the ones that are not allowed; the ones of the current process by default.</param>
    internal RemoteGitRunner(
        IProcessRunner runner,
        IExecutableLocator locator,
        RemoteGitFolders folders,
        string worktreeRoot,
        RemoteTransport transport,
        Func<IReadOnlyDictionary<string, string>>? ambient = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        ArgumentNullException.ThrowIfNull(transport);
        _runner = runner;
        _locator = locator;
        _folders = folders;
        _worktreeRoot = worktreeRoot;
        Transport = transport;
        _ambient = ambient ?? DoctorEnvironment.CurrentProcess;
    }

    /// <summary>The transport of this executor: what the composition test of the executor reads.</summary>
    internal RemoteTransport Transport { get; }

    public async Task<RemoteGitInspection> InspectAsync(ProjectContext project, RemoteRepository destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(destination);
        return (await InspectCoreAsync(project, destination, ct).ConfigureAwait(false)).Inspection;
    }

    public async Task<RemoteBranchRead> ReadBranchAsync(ProjectContext project, RemoteRepository destination, RemoteBranch branch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(branch);

        // The inspection comes before every ls-remote (D-05). A repository that uses LFS is not refused here: reading a branch sends nothing.
        var (inspection, prepared) = await InspectCoreAsync(project, destination, ct).ConfigureAwait(false);
        if (prepared is null || inspection.FailureCode is not null || inspection.Refusals.Count > 0)
        {
            return new RemoteBranchRead(false, null, inspection.FailureCode ?? RemoteGitCodes.ConfigurationRefused);
        }

        return await LsRemoteAsync(prepared, branch, ct).ConfigureAwait(false);
    }

    public async Task<RemotePushResult> PushRunBranchAsync(RemotePush push, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(push);
        ArgumentNullException.ThrowIfNull(push.Project);
        ArgumentNullException.ThrowIfNull(push.Destination);

        var refspec = RemoteGitCommandLine.TryRefspec(push.Commit, push.Run);
        if (refspec is null)
        {
            return new RemotePushResult(RemotePushOutcome.Failed, null, FailedInspection(RemoteGitCodes.InvalidCommit));
        }

        var (inspection, prepared) = await InspectCoreAsync(push.Project, push.Destination, ct).ConfigureAwait(false);
        if (prepared is null || inspection.FailureCode is not null)
        {
            return new RemotePushResult(RemotePushOutcome.Failed, null, inspection);
        }

        if (inspection.Refusals.Count > 0)
        {
            return new RemotePushResult(RemotePushOutcome.ConfigurationRefused, null, inspection);
        }

        if (inspection.UsesLfs)
        {
            return new RemotePushResult(RemotePushOutcome.LfsRefused, null, inspection);
        }

        var branch = RemoteBranch.ForRun(push.Run);

        // Idempotent: a remote that already has the branch at that commit is done, and nothing is pushed.
        var before = await LsRemoteAsync(prepared, branch, ct).ConfigureAwait(false);
        if (!before.Reached)
        {
            return new RemotePushResult(OutcomeOf(before.FailureCode), null, inspection);
        }

        if (string.Equals(before.Commit, push.Commit, StringComparison.Ordinal))
        {
            return new RemotePushResult(RemotePushOutcome.AlreadyUpToDate, push.Commit, inspection);
        }

        var result = await RunAsync(prepared, RemoteGitCommandLine.Push(prepared.Address, refspec), RemoteGitCommandLine.PushTimeout, ct).ConfigureAwait(false);
        if (result is null)
        {
            return new RemotePushResult(RemotePushOutcome.Failed, null, inspection);
        }

        if (result.TimedOut)
        {
            // What the remote got is not known: «not confirmed», never «published».
            return new RemotePushResult(RemotePushOutcome.NotConfirmed, null, inspection);
        }

        if (result.OutputLimitExceeded)
        {
            return new RemotePushResult(RemotePushOutcome.Failed, null, inspection);
        }

        var line = RemoteGitOutput.ReadPush(result.StandardOutput, refspec);
        switch (line)
        {
            case RemotePushLine.RejectedNotFastForward:
                return new RemotePushResult(RemotePushOutcome.RejectedNotFastForward, null, inspection);
            case RemotePushLine.Other:
                return new RemotePushResult(RemotePushOutcome.Failed, null, inspection);
            case RemotePushLine.Missing when result.ExitCode != 0:
                return new RemotePushResult(OutcomeOf(RemoteGitOutput.ClassifyError(result.StandardError)), null, inspection);
        }

        // After the push the commit is checked on the remote: anything else is «not confirmed».
        var after = await LsRemoteAsync(prepared, branch, ct).ConfigureAwait(false);
        if (after.Reached && string.Equals(after.Commit, push.Commit, StringComparison.Ordinal))
        {
            return new RemotePushResult(line == RemotePushLine.UpToDate ? RemotePushOutcome.AlreadyUpToDate : RemotePushOutcome.Pushed, push.Commit, inspection);
        }

        return new RemotePushResult(RemotePushOutcome.NotConfirmed, after.Reached ? after.Commit : null, inspection);
    }

    private static RemotePushOutcome OutcomeOf(string? code) => code switch
    {
        RemoteGitCodes.CredentialsNeeded => RemotePushOutcome.CredentialsNeeded,
        RemoteGitCodes.RemoteUnreachable or RemoteGitCodes.TimedOut => RemotePushOutcome.Unreachable,
        RemoteGitCodes.Redirected => RemotePushOutcome.Redirected,
        _ => RemotePushOutcome.Failed,
    };

    private static RemoteGitInspection FailedInspection(string code) => new(false, [], false, code);

    private sealed record Prepared(string Git, string WorkingDirectory, IReadOnlyList<string> Configuration, IReadOnlyDictionary<string, string?> Environment, RemoteConfigRoots Roots, string Address);

    private async Task<(RemoteGitInspection Inspection, Prepared? Prepared)> InspectCoreAsync(ProjectContext project, RemoteRepository destination, CancellationToken ct)
    {
        var failure = TryPrepare(project, destination, out var prepared);
        if (failure is not null || prepared is null)
        {
            return (FailedInspection(failure ?? RemoteGitCodes.InspectionFailed), null);
        }

        var query = await RunAsync(prepared, RemoteGitCommandLine.ConfigQuery(), RemoteGitCommandLine.QueryTimeout, ct).ConfigureAwait(false);
        var queryFailure = FailureOf(query, acceptedExitCodes: 1);
        if (queryFailure is not null || query is null)
        {
            return (FailedInspection(queryFailure ?? RemoteGitCodes.InspectionFailed), prepared);
        }

        // Exit code 1 is «no key matched»; any other is a failure of the query: closed.
        var entries = query.ExitCode == 0 ? GitCallbacks.Parse(query.StandardOutput) : [];
        var analysis = RemoteConfigInspection.Analyze(entries, prepared.Address, prepared.Roots.IsInside);
        if (analysis.Refusals.Count > 0)
        {
            return (new RemoteGitInspection(false, analysis.Refusals, false, null), prepared);
        }

        // The address that is approved is the address that is used: git is asked what it would rewrite it to (it opens no transport).
        var url = await RunAsync(prepared, RemoteGitCommandLine.GetUrl(prepared.Address), RemoteGitCommandLine.QueryTimeout, ct).ConfigureAwait(false);
        var urlFailure = FailureOf(url, acceptedExitCodes: 0);
        if (urlFailure is not null || url is null)
        {
            return (FailedInspection(urlFailure ?? RemoteGitCodes.InspectionFailed), prepared);
        }

        if (!string.Equals(url.StandardOutput.TrimEnd('\r', '\n'), prepared.Address, StringComparison.Ordinal))
        {
            return (new RemoteGitInspection(false, [RemoteConfigInspection.NameRewrite(entries, prepared.Address)], false, null), prepared);
        }

        var usesLfs = analysis.LfsFilterConfigured && RemoteLfsDetector.UsesLfs(prepared.Roots);
        return (new RemoteGitInspection(!usesLfs, [], usesLfs, null), prepared);
    }

    // The code that a query that did not run, timed out, wrote too much or ended with an exit code that is not accepted stops with; null when it can be read.
    private static string? FailureOf(ProcessResult? result, int acceptedExitCodes)
    {
        if (result is null)
        {
            return RemoteGitCodes.InspectionFailed;
        }

        if (result.TimedOut)
        {
            return RemoteGitCodes.TimedOut;
        }

        if (result.OutputLimitExceeded)
        {
            return RemoteGitCodes.InspectionOutputTooLarge;
        }

        return result.ExitCode > acceptedExitCodes || result.ExitCode < 0 ? RemoteGitCodes.InspectionFailed : null;
    }

    private async Task<RemoteBranchRead> LsRemoteAsync(Prepared prepared, RemoteBranch branch, CancellationToken ct)
    {
        var result = await RunAsync(prepared, RemoteGitCommandLine.LsRemote(prepared.Address, branch), RemoteGitCommandLine.QueryTimeout, ct).ConfigureAwait(false);
        if (result is null)
        {
            return new RemoteBranchRead(false, null, RemoteGitCodes.GitFailed);
        }

        if (result.TimedOut)
        {
            return new RemoteBranchRead(false, null, RemoteGitCodes.TimedOut);
        }

        if (result.OutputLimitExceeded)
        {
            return new RemoteBranchRead(false, null, RemoteGitCodes.OutputTooLarge);
        }

        return result.ExitCode == 0
            ? new RemoteBranchRead(true, RemoteGitOutput.ReadBranchCommit(result.StandardOutput, "refs/heads/" + branch.Name), null)
            : new RemoteBranchRead(false, null, RemoteGitOutput.ClassifyError(result.StandardError));
    }

    // One process of git. Null when it could not even be started (git.exe vanished, the folder is gone): the caller says «failed».
    private async Task<ProcessResult?> RunAsync(Prepared prepared, IReadOnlyList<string> command, TimeSpan timeout, CancellationToken ct)
    {
        var arguments = new List<string>(prepared.Configuration.Count + command.Count);
        arguments.AddRange(prepared.Configuration);
        arguments.AddRange(command);
        var spec = new ProcessSpec(
            prepared.Git,
            arguments,
            prepared.WorkingDirectory,
            prepared.Environment,
            timeout,
            StandardInput: null,
            MaxOutputCharacters: RemoteGitCommandLine.MaxOutputCharacters);
        try
        {
            return await _runner.RunAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // Everything a command needs before it runs; the code of what stopped it, or null.
    private string? TryPrepare(ProjectContext project, RemoteRepository destination, out Prepared? prepared)
    {
        prepared = null;
        if (string.IsNullOrWhiteSpace(project.RootPath) || !Path.IsPathFullyQualified(project.RootPath) || !Directory.Exists(project.RootPath))
        {
            return RemoteGitCodes.ProjectUnreadable;
        }

        var git = ResolveGit(project.RootPath);
        if (git is null)
        {
            return RemoteGitCodes.GitUnavailable;
        }

        try
        {
            _folders.Ensure();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return RemoteGitCodes.UnsafeHooksFile;
        }

        var roots = RemoteConfigRoots.TryCreate(project.RootPath, _worktreeRoot);
        if (roots is null)
        {
            return RemoteGitCodes.ProjectUnreadable;
        }

        var address = Transport.AddressOf(destination);
        if (string.IsNullOrEmpty(address) || address.Length > 1024 || address[0] == '-' || address.Any(char.IsControl))
        {
            return RemoteGitCodes.InvalidAddress;
        }

        prepared = new Prepared(
            git,
            project.RootPath,
            RemoteGitCommandLine.Configuration(Transport, _folders.HooksFile),
            RemoteGitEnvironment.Build(Transport, _folders.TempFolder, _ambient()),
            roots,
            address);
        return null;
    }

    // git.exe by full path: an .exe (never a .cmd or .bat shim), outside every untrusted root and outside the project, as it is now.
    private string? ResolveGit(string projectRoot)
    {
        var path = _locator.Find("git");
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)
            || _locator.IsUntrusted(path))
        {
            return null;
        }

        return UntrustedPaths.IsInside(path, UntrustedPaths.Resolve([projectRoot])) ? null : path;
    }
}
