using System.Collections;
using System.Text.RegularExpressions;
using AgentStudio.Application.Execution;

namespace AgentStudio.Infrastructure.Git;

internal sealed partial record GitResult(int ExitCode, string StandardOutput, string StandardError, bool OutputLimitExceeded = false)
{
    // git quotes the path in its (possibly translated) message: "Unable to create '<path>.lock': File exists."
    [GeneratedRegex(@"'(?<path>[^'\r\n]+\.lock)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedLockFile();

    /// <summary>
    /// Throws <see cref="GitCommandException"/> unless the command exited with 0, or <see cref="GitLockFileException"/>
    /// when it failed because a lock file named in its error output exists.
    /// </summary>
    internal GitResult EnsureSuccess(string operation)
    {
        if (ExitCode == 0)
        {
            return this;
        }

        if (FindExistingLockFile(StandardError) is { } lockFile)
        {
            throw new GitLockFileException(
                $"git {operation} failed because the lock file '{lockFile}' exists: a git process was interrupted, or one is still working on this repository. "
                + "Agent Studio never deletes git lock files: once no git process is running, delete it by hand and try again.",
                ExitCode,
                StandardError,
                lockFile);
        }

        throw new GitCommandException($"git {operation} failed with exit code {ExitCode}: {Shorten(StandardError)}", ExitCode, StandardError);
    }

    private static string? FindExistingLockFile(string standardError)
    {
        foreach (Match match in QuotedLockFile().Matches(standardError))
        {
            try
            {
                var path = Path.GetFullPath(match.Groups["path"].Value);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a path after all.
            }
        }

        return null;
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "...";
    }
}

/// <summary>
/// The user's repository a content command works on (X-01): the root of the project's working tree and the common
/// directory. A callback program inside either of them is the repository's own (a versioned file, something under
/// <c>.git</c>), so the configuration inspection protects them as it protects the worktree root, whatever working
/// directory the command has: from a worktree, the original repository is not on the way. The same scope names what the
/// walk of S-01 to S-03 reads before the command: the common directory and, when it is known and lies outside it, the git
/// directory of the project's working tree (<paramref name="GitDirectory"/>; the project may itself be a linked worktree).
/// </summary>
internal sealed record GitScope(string RepositoryRoot, string CommonDirectory, string? GitDirectory = null);

/// <summary>
/// Runs git.exe through <see cref="IProcessRunner"/> (which kills the whole process tree on timeout or cancellation)
/// with the settings every Agent Studio command shares: no terminal prompts, no repository-redirecting environment,
/// no hooks, <c>core.longpaths</c>, no lazy fetch and no transport, and no background daemons that would keep files of
/// a worktree open on Windows.
/// </summary>
internal sealed class GitCommandRunner
{
    /// <summary>
    /// File under the worktree root that every command receives as <c>core.hooksPath</c>: an EMPTY FILE, not a folder.
    /// git looks for each hook below that path, and nothing can be below a file, so no hook runs: <c>--no-verify</c> only
    /// skips pre-commit and commit-msg, and a relative <c>core.hooksPath</c> of the user (husky) would even resolve inside
    /// a worktree, which the agent controls.
    /// </summary>
    /// <remarks>
    /// An empty folder is not enough (measured with git-lfs 3.5.1 and git 2.45.2): every git-lfs filter
    /// (<c>filter-process</c>, and the single-file <c>clean</c> and <c>smudge</c>) installs its <c>pre-push</c>,
    /// <c>post-checkout</c>, <c>post-commit</c> and <c>post-merge</c> hooks into <c>core.hooksPath</c> when it starts,
    /// creating the folder when it is missing, and no git-lfs option stops it. git then runs a hook written that way at the
    /// end of the same command: the smudge of a <c>git merge --ff-only</c> wrote <c>post-merge</c> and git ran it. Below a
    /// file git-lfs cannot create its folder: it installs nothing, and its filter goes on (exit code 0, the object stored,
    /// the pointer and the content right).
    /// </remarks>
    internal const string NoHooksFileName = ".no-hooks";

    // Per-process overrides; nothing is written to the user's configuration. Closing every transport is what stops the
    // ssh, askpass, credential and remote-helper programs from running (no internal command needs a transport).
    // `protocol.allow=never` is only the fallback for an unnamed protocol; git checks `protocol.<name>.allow` first, and
    // a repository can set it to `always` (measured with git 2.45.2: `protocol.file.allow=always` reopened the file
    // transport despite `protocol.allow=never`). So every named transport is forced to `never` too; a single-valued key,
    // the command line wins. `credential.helper=` resets the whole helper list, including `credential.<url>.helper`
    // (measured: a url-scoped helper does not run and git falls to the disabled prompt). `core.askPass=`/`core.sshCommand=`
    // are blanked as defence in depth, moot once no transport opens. `gc.auto`/`maintenance.auto` keep background daemons
    // from touching a worktree's files on Windows. The per-transport list only covers the named transports: a repository's
    // protocol.<x>.allow=always for any other name opened git-remote-<x> (measured with git 2.45.2, H-04), so the
    // environment also sets GIT_ALLOW_PROTOCOL, which overrides every protocol.*.allow (see BuildEnvironment). These
    // flags stay as a second line.
    // G-05: the destinations the repository's configuration can add to what git writes, none of them checked for links, are
    // turned off. Measured with git 2.45.2 under the flags above: core.splitIndex=true made every index write (the
    // worktree's, and the user's in a delivery) leave sharedindex.* in the git directory; rerere.enabled=true, or an
    // rr-cache folder planted in the common directory (rerere is then on by default), made every commit write MERGE_RR and
    // create rr-cache; core.untrackedCache=true wrote its extension into the index. With these flags none of them appeared.
    // gc.writeCommitGraph and fetch.writeCommitGraph are a second line: gc.auto=0 and maintenance.auto=false already kept
    // every internal command from writing a commit-graph or a multi-pack-index (measured), and none fetches.
    private static readonly string[] BaseConfiguration =
    [
        "-c", "core.longpaths=true",
        "-c", "core.fsmonitor=false",
        "-c", "gc.auto=0",
        "-c", "gc.writeCommitGraph=false",
        "-c", "maintenance.auto=false",
        "-c", "fetch.writeCommitGraph=false",
        "-c", "rerere.enabled=false",
        "-c", "rerere.autoUpdate=false",
        "-c", "core.splitIndex=false",
        "-c", "core.untrackedCache=false",
        "-c", "protocol.allow=never",
        "-c", "protocol.file.allow=never",
        "-c", "protocol.git.allow=never",
        "-c", "protocol.ssh.allow=never",
        "-c", "protocol.http.allow=never",
        "-c", "protocol.https.allow=never",
        "-c", "protocol.ftp.allow=never",
        "-c", "protocol.ftps.allow=never",
        "-c", "protocol.ext.allow=never",
        "-c", "credential.helper=",
        "-c", "core.askPass=",
        "-c", "core.sshCommand=",
    ];

    // A parent process (a git hook, a shell) must not redirect the commands to another repository, identity or
    // configuration. Every inherited GIT_* variable is stripped (see BuildEnvironment); these well-known ones are also
    // listed so they are always present-and-null, whether or not they were inherited, and EMAIL is stripped too. The
    // user's global configuration stays (core.autocrlf, and `git lfs install`'s filter.lfs.* there, must apply), so HOME
    // and USERPROFILE are kept.
    private static readonly string[] RemovedVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_COMMON_DIR",
        "GIT_NAMESPACE",
        "GIT_CEILING_DIRECTORIES",
        "GIT_PREFIX",
        "GIT_CONFIG",
        "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_SYSTEM",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
        "GIT_AUTHOR_NAME",
        "GIT_AUTHOR_EMAIL",
        "GIT_AUTHOR_DATE",
        "GIT_COMMITTER_NAME",
        "GIT_COMMITTER_EMAIL",
        "GIT_COMMITTER_DATE",
        "EMAIL",
    ];

    // Content sub-commands that make git read the working tree or a tree through the repository's attributes and so can
    // run a configured callback (a filter, merge driver, textconv...). Before one runs, the effective configuration is
    // inspected (N-01). `merge` is here because the approved delivery runs it as an ordinary content command (R3-05): it
    // updates the working tree (running smudge filters) with no user hooks, so the same allowlist guards it. `worktree
    // add` is handled apart (it is `worktree` plus `add`): since A-02 it is created with `--no-checkout`, so it no longer
    // checks out and this parent-context inspection stays only as defence; the checkout is a later `reset --hard`
    // (ContentSubCommands) inspected in the new worktree's OWN config context, where an includeIf/config.worktree the
    // parent cannot see resolves. `worktree list`/`remove`/`prune` are left out, so cleanup still works on a repository
    // that is refused.
    private static readonly HashSet<string> ContentSubCommands = new(StringComparer.Ordinal)
    {
        "status", "add", "commit", "reset", "clean", "checkout", "merge", "merge-tree", "archive",
    };

    // S-02: the internal commands that write refs or objects without reading the content (update-ref writes a ref, its
    // reflog and, deleting a packed ref, packed-refs; commit-tree writes a commit). They run no callback, so they are not
    // inspected, but the walk of the git directory runs before them as before every content command, so they need a
    // GitScope too. `worktree remove` is left out on purpose: it only deletes the administrative folder of its own worktree,
    // which DropRegistrationAsync walks entirely first, a delete never follows a hard link, and the cleanup keeps working on
    // a repository the walk refuses.
    private static readonly HashSet<string> RepositoryWriteSubCommands = new(StringComparer.Ordinal)
    {
        "update-ref", "commit-tree",
    };

    private readonly IProcessRunner _runner;
    private readonly IExecutableLocator _locator;
    private readonly GitWorktreeOptions _options;
    private readonly string[] _configuration;
    private readonly string _worktreeRoot;
    private string? _gitPath;

    internal GitCommandRunner(IProcessRunner runner, IExecutableLocator locator, GitWorktreeOptions options)
    {
        _runner = runner;
        _locator = locator;
        _options = options;
        _worktreeRoot = GitNaming.NormalizePath(options.WorktreeRoot);
        HooksPath = Path.Combine(_worktreeRoot, NoHooksFileName);
        _configuration = [.. BaseConfiguration, "-c", "core.hooksPath=" + HooksPath];
    }

    /// <summary>The absolute path every command receives as <c>core.hooksPath</c>: the empty file <see cref="NoHooksFileName"/>.</summary>
    internal string HooksPath { get; }

    /// <summary>
    /// Runs one git command in <paramref name="workingDirectory"/>. Free text (commit messages) goes through
    /// <paramref name="standardInput"/>, never through the arguments. A timeout throws; any exit code is returned. A
    /// content command (one that can run a callback of the configuration) needs the <paramref name="scope"/> of the
    /// user's repository it works on, whose root and common directory its callbacks may not point inside (X-01); without
    /// it the command is not run. So does a command that writes refs or objects, and before either the git directory of
    /// that scope is walked for planted links (S-01 to S-03).
    /// </summary>
    internal async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken, GitScope? scope = null, int? maxOutputCharacters = null)
    {
        EnsureNoHookCanRun(arguments, scope);
        EnsureNoPlantedLink(arguments, scope);
        await EnsureNoRepositoryCallbackAsync(workingDirectory, arguments, scope, cancellationToken).ConfigureAwait(false);
        return await RunCoreAsync(_configuration, _options.CommandTimeout ?? GitWorktreeOptions.DefaultCommandTimeout, workingDirectory, arguments, standardInput, cancellationToken, maxOutputCharacters)
            .ConfigureAwait(false);
    }

    private async Task<GitResult> RunCoreAsync(
        IReadOnlyList<string> configuration,
        TimeSpan timeout,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken,
        int? maxOutputCharacters = null)
    {
        var all = new List<string>(configuration.Count + arguments.Count);
        all.AddRange(configuration);
        all.AddRange(arguments);

        var environment = BuildEnvironment(Environment.GetEnvironmentVariables());
        var spec = new ProcessSpec(ResolveGit(), all, workingDirectory, environment, timeout, standardInput, MaxOutputCharacters: maxOutputCharacters);
        var result = await _runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new GitCommandException($"git {Describe(arguments)} timed out after {timeout}.", -1, result.StandardError);
        }

        return new GitResult(result.ExitCode, result.StandardOutput, result.StandardError, result.OutputLimitExceeded);
    }

    /// <summary>Runs the command and throws <see cref="GitCommandException"/> unless it exits with 0.</summary>
    internal async Task<GitResult> RunCheckedAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? standardInput = null, GitScope? scope = null)
    {
        var result = await RunAsync(workingDirectory, arguments, standardInput, cancellationToken, scope).ConfigureAwait(false);
        return result.EnsureSuccess(Describe(arguments));
    }

    /// <summary>Creates the empty hooks file. Only called once the worktree root is known not to be inside a repository.</summary>
    internal void CreateHooksPath()
    {
        PrepareHooksPath();
        RequireHooksPathWithoutHooks();
    }

    internal string ResolveGit()
    {
        // A path kept from an earlier lookup is asked again: a project opened since may contain it (ADR-0018, second addendum).
        if (_gitPath is { } kept && !_locator.IsUntrusted(kept))
        {
            return kept;
        }

        _gitPath = null;
        var path = _options.GitExecutable ?? _locator.Find("git");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw _locator.FindInUntrustedRoot("git") is { } inside
                ? new GitUnavailableException($"git.exe was found only inside a project folder ('{inside}'), which is not trusted to provide programs, so it was not used; install git outside the project (Agent Studio needs git 2.38 or later).")
                : new GitUnavailableException("git.exe was not found on the PATH; Agent Studio needs git 2.38 or later.");
        }

        if (_locator.IsUntrusted(path))
        {
            throw new GitUnavailableException($"git.exe at '{path}' lies inside a project folder, which is not trusted to provide programs, so it was not used; install git outside the project.");
        }

        _gitPath = path;
        return path;
    }

    /// <summary>
    /// The value of <c>GIT_ALLOW_PROTOCOL</c>: one name no URL can select, so git allows no transport at all (H-04).
    /// With the variable set, git allows only the transports it lists and ignores every <c>protocol.*.allow</c> of the
    /// configuration (measured with git 2.45.2). An empty value is NOT "none": git splits it into one empty name, and
    /// then allows the empty-named remote helper of a <c>::x</c> URL (measured: <c>git-remote-</c> ran). With <c>-</c>,
    /// <c>::x</c>, <c>sentinel::x</c> and <c>file://</c> were refused (measured). That no URL can select <c>-</c> is read
    /// in git's source, not measured: a transport or helper name taken from a URL starts with a letter or a digit.
    /// </summary>
    internal const string NoTransport = "-";

    /// <summary>
    /// The environment of every command: no prompts, no optional locks (reading the user's repository must not refresh
    /// its index), <c>GIT_PAGER=cat</c>, no lazy fetch and no transport (H-04), and EVERY inherited <c>GIT_*</c> variable
    /// stripped but the five set here, so what Agent Studio inspects and what it runs see the same configuration (R3-07):
    /// a <c>GIT_CONFIG</c>, <c>GIT_CONFIG_GLOBAL</c>/<c>GIT_CONFIG_SYSTEM</c> or a numbered
    /// <c>GIT_CONFIG_KEY_n</c>/<c>VALUE_n</c> left in the environment would make <c>git config</c> (the callback
    /// inspection) read a different file than <c>git add</c> runs against. The user's global and system configuration on
    /// disk stays (HOME and USERPROFILE are kept), so <c>core.autocrlf</c> and <c>git lfs install</c>'s
    /// <c>filter.lfs.*</c> still apply.
    /// </summary>
    /// <remarks>
    /// <c>GIT_NO_LAZY_FETCH=1</c>: in a partial clone git fetches a missing object on demand from inside an ordinary
    /// command (<c>diff --binary</c>, <c>archive</c>, <c>reset --hard</c>), through the transport or remote helper the
    /// repository's configuration chooses; measured with git 2.45.2, that fetch ran a <c>git-remote-&lt;x&gt;</c> the
    /// repository allowed with <c>protocol.&lt;x&gt;.allow=always</c>, past every <c>-c protocol.*.allow=never</c> of
    /// <see cref="BaseConfiguration"/>, and the variable stops it before any transport is chosen. A partial clone is also
    /// refused outright by <see cref="GitCallbacks"/>; this covers the commands that are not inspected. Both values are
    /// set, not inherited: a parent's <c>GIT_NO_LAZY_FETCH=0</c> or its own <c>GIT_ALLOW_PROTOCOL</c> list never applies.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string?> BuildEnvironment(IDictionary inherited)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_PAGER"] = "cat",
            ["GIT_NO_LAZY_FETCH"] = "1",
            ["GIT_ALLOW_PROTOCOL"] = NoTransport,
        };

        // The well-known redirecting variables are always present-and-null, inherited or not.
        foreach (var name in RemovedVariables)
        {
            environment[name] = null;
        }

        // Every other inherited GIT_* variable is stripped too (GIT_LFS_*, further GIT_CONFIG_* pairs, anything new),
        // except the five set above. GIT_LFS_* belong to git-lfs, not git; stripping them follows the rule and may
        // change whether a smudge reaches the network.
        foreach (DictionaryEntry entry in inherited)
        {
            if (entry.Key is string name
                && name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
                && !environment.ContainsKey(name))
            {
                environment[name] = null;
            }
        }

        return environment;
    }

    /// <summary>
    /// S-01 to S-03: before every content command and every command that writes refs or objects, the git directory of the
    /// scope is walked (<see cref="WorktreeFileSystem.RequireGitDirectoriesWithoutLinks"/>) and a planted link anywhere in
    /// it is refused with <see cref="WorktreeOwnershipException"/> before git runs. It is the first check of the gate, so
    /// git-lfs, which a content command can start, never runs either. Without a scope the command is not run (fail closed).
    /// </summary>
    private static void EnsureNoPlantedLink(IReadOnlyList<string> arguments, GitScope? scope)
    {
        var content = IsContentCommand(arguments);
        if (!content && !IsRepositoryWrite(arguments))
        {
            return;
        }

        if (scope is null)
        {
            throw new InvalidOperationException(content
                ? $"git '{Describe(arguments)}' can run a callback of the configuration, and it was asked without the repository it works on; refusing to run it."
                : $"git '{Describe(arguments)}' writes into the git directory, and it was asked without the repository it works on; refusing to run it.");
        }

        WorktreeFileSystem.RequireGitDirectoriesWithoutLinks(scope.CommonDirectory, scope.GitDirectory);
    }

    /// <summary>
    /// Before a content command runs, inspects the effective configuration exactly as that command resolves it (same
    /// <c>--git-dir</c>, <c>--work-tree</c> and working directory) and throws <see cref="UnsafeGitCallbackException"/>
    /// when git could run a callback the repository or a worktree controls (N-01). The query itself is not a content
    /// command, so it never re-enters this check.
    /// </summary>
    private async Task EnsureNoRepositoryCallbackAsync(string workingDirectory, IReadOnlyList<string> arguments, GitScope? scope, CancellationToken cancellationToken)
    {
        if (!IsContentCommand(arguments))
        {
            return;
        }

        // Fail closed: a content command whose repository is not known cannot be inspected against it.
        if (scope is null)
        {
            throw new InvalidOperationException(
                $"git '{Describe(arguments)}' can run a callback of the configuration, and it was asked without the repository it works on; refusing to run it.");
        }

        // G-01: every content command can run the git-lfs filters, the only content callbacks allowed (measured with
        // git-lfs 3.5.1: `add` and also `status`, on a same-size change whose stat is dirty, run the clean and store the
        // object; `reset`, `checkout` and `merge` run the smudge), and git-lfs writes into <common>/lfs. So that storage is
        // checked here, before every one of them, rather than at each call site. Since S-01 to S-03 the walk of the whole
        // git directory (EnsureNoPlantedLink) runs first; this stays as a second line.
        WorktreeFileSystem.RequireLfsStorageWithoutLinks(scope.CommonDirectory);

        var dirFlags = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("--git-dir=", StringComparison.Ordinal) || argument.StartsWith("--work-tree=", StringComparison.Ordinal))
            {
                dirFlags.Add(argument);
            }
            else if (!argument.StartsWith('-'))
            {
                break;
            }
        }

        var refusals = await ClassifyRepositoryCallbacksAsync(workingDirectory, dirFlags, scope, cancellationToken).ConfigureAwait(false);
        if (refusals.Count == 0)
        {
            return;
        }

        var first = refusals[0];
        var more = refusals.Count > 1 ? $" (and {refusals.Count - 1} more)" : string.Empty;
        throw new UnsafeGitCallbackException(
            $"Refusing to run git '{Describe(arguments)}': the configuration key '{first.Key}' ({first.Origin}) would run a program the repository or a worktree controls, because {first.Reason}{more}. "
            + "Agent Studio does not run repository-controlled callbacks with the user's permissions; a git-lfs-style program installed outside the repository is allowed.",
            refusals);
    }

    /// <summary>
    /// Inspects the effective configuration with the given <paramref name="dirFlags"/> (the command's <c>--git-dir</c>
    /// and <c>--work-tree</c>, so <c>includeIf</c> and <c>config.worktree</c> resolve as a content command would) and
    /// classifies it (N-01/R3-01), returning every callback git could run that the repository or a worktree controls.
    /// Non-throwing: the approved delivery asks this before spending an approval, and the content gate above throws on it.
    /// </summary>
    /// <remarks>
    /// The roots a callback program may not point inside (X-01) are the worktree root, the command's working directory,
    /// the root of the user's repository and its common directory (from <paramref name="scope"/>), and the main working
    /// tree git derives from that common directory (its parent when it is a <c>.git</c> folder: the project itself may be
    /// a linked worktree). Measured with git 2.45.2: a worktree-only <c>includeIf</c> naming a program of the original
    /// repository was accepted with only the first two, and the checkout of the worktree ran it. The classifier compares
    /// each root by its final path too, as it does the program. The roots cannot be complete (another linked worktree of
    /// the user, a main working tree with a separate git directory), so a git-lfs filter named by an absolute path must
    /// also be the installed git-lfs, by final path and file id (G-03).
    /// </remarks>
    internal async Task<IReadOnlyList<GitCallbackRefusal>> ClassifyRepositoryCallbacksAsync(string workingDirectory, IReadOnlyList<string> dirFlags, GitScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var query = new List<string>(dirFlags);
        query.AddRange(["config", "-z", "--show-scope", "--show-origin", "--get-regexp", "--end-of-options", GitCallbacks.Pattern]);

        var result = await RunCoreAsync(_configuration, _options.CommandTimeout ?? GitWorktreeOptions.DefaultCommandTimeout, workingDirectory, query, null, cancellationToken)
            .ConfigureAwait(false);

        // Exit code 1 is "no matching key"; anything above is a real failure of the query itself.
        if (result.ExitCode > 1)
        {
            result.EnsureSuccess("config --get-regexp");
        }

        if (result.ExitCode == 1)
        {
            return [];
        }

        var cwd = GitNaming.NormalizePath(workingDirectory);
        var roots = new List<string> { _worktreeRoot, cwd, scope.RepositoryRoot, scope.CommonDirectory };
        if (MainWorkingTree(scope.CommonDirectory) is { } main)
        {
            roots.Add(main);
        }

        return GitCallbacks.Classify(
            GitCallbacks.Parse(result.StandardOutput),
            roots,
            _worktreeRoot,
            cwd,
            name => GitCallbacks.ResolveProgramOnPath(_locator, ResolveGit(), name),
            GitCallbacks.ResolveFinalPath,
            GitCallbacks.ResolveFileIdentity);
    }

    /// <summary>
    /// The main working tree as git derives it from a common directory: its parent when the common directory is a
    /// <c>.git</c> folder (git's own rule for <c>worktree list</c>), or null (a bare or a separate git directory).
    /// </summary>
    private static string? MainWorkingTree(string commonDirectory)
    {
        var common = GitNaming.NormalizePath(commonDirectory);
        return string.Equals(Path.GetFileName(common), ".git", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(common) : null;
    }

    private static bool IsContentCommand(IReadOnlyList<string> arguments)
    {
        var index = SubCommandIndex(arguments);
        if (index >= arguments.Count)
        {
            return false;
        }

        if (ContentSubCommands.Contains(arguments[index]))
        {
            return true;
        }

        // `worktree add` checks out a commit (running smudge filters); `worktree list`/`remove`/`prune` do not.
        return arguments[index] == "worktree" && index + 1 < arguments.Count && arguments[index + 1] == "add";
    }

    private static bool IsRepositoryWrite(IReadOnlyList<string> arguments)
    {
        var index = SubCommandIndex(arguments);
        return index < arguments.Count && RepositoryWriteSubCommands.Contains(arguments[index]);
    }

    private static int SubCommandIndex(IReadOnlyList<string> arguments)
    {
        var index = 0;
        while (index < arguments.Count)
        {
            if (arguments[index] == "-c" && index + 1 < arguments.Count)
            {
                index += 2;
            }
            else if (arguments[index].StartsWith("--git-dir=", StringComparison.Ordinal) || arguments[index].StartsWith("--work-tree=", StringComparison.Ordinal))
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    /// <summary>
    /// Before every command, the hooks path cannot hold a hook. It is under the worktree root, where an agent with a shell
    /// can write: a hook there would run, and so would one git-lfs installs into a folder there. A content command, the only
    /// kind that can start git-lfs (its filters), first puts the empty file in place when it is missing: the first one can
    /// come before any worktree exists (the status of <c>InspectAsync</c>), and with nothing at that path git-lfs creates
    /// the folder and fills it (measured with 3.5.1). Any other command runs no filter, so for it a missing path, or the
    /// empty folder of an earlier version, is as safe as before: git finds no hook in either.
    /// </summary>
    private void EnsureNoHookCanRun(IReadOnlyList<string> arguments, GitScope? scope)
    {
        if (scope is not null && IsContentCommand(arguments) && !File.Exists(HooksPath))
        {
            RequireHooksPathOutside(scope);
            PrepareHooksPath();
        }

        RequireHooksPathWithoutHooks();
    }

    /// <summary>
    /// Agent Studio never creates the hooks file inside the repository a command works on: with the worktree root inside
    /// it (a configuration mistake that <c>EnsureAsync</c> refuses too), the command is refused.
    /// </summary>
    private void RequireHooksPathOutside(GitScope scope)
    {
        foreach (var root in new[] { scope.RepositoryRoot, scope.CommonDirectory, scope.GitDirectory })
        {
            if (root is not null && (GitNaming.SamePath(HooksPath, root) || GitNaming.IsUnder(HooksPath, root)))
            {
                throw new InvalidOperationException(
                    $"The worktree root '{_worktreeRoot}' is inside the repository '{root}': the worktrees and the empty file Agent Studio passes to git as core.hooksPath must live outside it; refusing to create '{HooksPath}' there.");
            }
        }
    }

    /// <summary>
    /// Puts the empty file at the hooks path when nothing is there, or in place of the empty folder an earlier version of
    /// Agent Studio used. A link, or a folder that holds anything, is left as it is for
    /// <see cref="RequireHooksPathWithoutHooks"/> to refuse: it may hold the hooks git-lfs installed into that folder, or
    /// something planted, and the user looks at it before deleting it.
    /// </summary>
    private void PrepareHooksPath()
    {
        var folder = new DirectoryInfo(HooksPath);
        if (folder.Exists)
        {
            if (WorktreeFileSystem.IsLink(folder) || folder.EnumerateFileSystemInfos().Any())
            {
                return;
            }

            // Not recursive: if anything appeared in it meanwhile, this throws and git does not run.
            folder.Delete();
        }
        else if (File.Exists(HooksPath))
        {
            return;
        }

        Directory.CreateDirectory(_worktreeRoot);
        try
        {
            File.Open(HooksPath, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
        }
        catch (IOException) when (File.Exists(HooksPath))
        {
            // Another command created it first; RequireHooksPathWithoutHooks checks what is there.
        }
    }

    /// <summary>
    /// The hooks path must be the empty file (no link, no content), or, for a command that cannot start git-lfs, missing or
    /// the empty folder of an earlier version. A link, a folder with anything in it, or a file with content is refused
    /// before git runs, and nothing is emptied or replaced.
    /// </summary>
    private void RequireHooksPathWithoutHooks()
    {
        var file = new FileInfo(HooksPath);
        if (file.Exists)
        {
            if (!WorktreeFileSystem.IsLink(file) && file.Length == 0)
            {
                return;
            }
        }
        else
        {
            var folder = new DirectoryInfo(HooksPath);
            if (!folder.Exists || (!WorktreeFileSystem.IsLink(folder) && !folder.EnumerateFileSystemInfos().Any()))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"'{HooksPath}' must be an empty file: Agent Studio passes it to git as core.hooksPath, and since nothing can be below a file, no hook runs and git-lfs cannot install its hooks there. "
            + "It is a link, a folder with something in it, or a file with content; refusing to run git until it is deleted (Agent Studio then creates the empty file again).");
    }

    // "worktree add", "commit", ...: the sub-command, without the leading global options ("-c key=value", "--git-dir=...").
    private static string Describe(IReadOnlyList<string> arguments)
    {
        var index = 0;
        while (index < arguments.Count)
        {
            if (arguments[index] == "-c" && index + 1 < arguments.Count)
            {
                index += 2;
            }
            else if (arguments[index].StartsWith("--git-dir=", StringComparison.Ordinal) || arguments[index].StartsWith("--work-tree=", StringComparison.Ordinal))
            {
                index++;
            }
            else
            {
                break;
            }
        }

        if (index >= arguments.Count)
        {
            return string.Empty;
        }

        var command = arguments[index];
        return command == "worktree" && index + 1 < arguments.Count ? command + " " + arguments[index + 1] : command;
    }
}
