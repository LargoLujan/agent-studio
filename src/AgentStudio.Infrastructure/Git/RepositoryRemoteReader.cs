using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Domain.Team;

namespace AgentStudio.Infrastructure.Git;

/// <summary>
/// Lists the remotes of the user's repository (<c>ListRemotesAsync</c>, docs/17 F13-3, P51) through the same hardened
/// <see cref="GitCommandRunner"/> as the rest of the git layer: neutralised configuration, <c>core.hooksPath</c> on an empty file, no
/// transport, no inherited <c>GIT_*</c> variable, and the walk of the git directories for planted links before the read.
/// </summary>
/// <remarks>
/// <para>The question is <c>git config --local --no-includes -z --get-regexp ^remote\..*\.url$</c>: only the configuration file of the
/// repository (the common one, for a worktree), never the user's or the system's, and no <c>include</c> or <c>includeIf</c> is
/// followed. The answer is a list of keys and values, not a file: git runs no callback to produce it, so the review of the
/// callbacks that the content commands need is not asked for.</para>
/// <para>Bounded at its source: the process runner kills git when a stream passes <see cref="MaxOutputCharacters"/> (the read is then
/// refused with <see cref="RemoteListing.OutputTooBig"/>, never cut), at most <see cref="MaxRemotes"/> remotes are kept, and an address
/// longer than <see cref="MaxUrlCharacters"/> or that git itself would not accept is left out. Each URL is cleaned with
/// <see cref="RepositoryRemote.Cleaned"/> the moment it is read.</para>
/// <para>Fails closed: a directory that is not a repository, a planted link, git missing or failing, a timeout, or too much output
/// returns no remotes and a code, never a path and never an exception; only the cancellation propagates.</para>
/// </remarks>
public sealed class RepositoryRemoteReader : IRepositoryRemoteReader
{
    /// <summary>The most characters git may write for the list of remotes: past it git is killed and the read is refused.</summary>
    public const int MaxOutputCharacters = 256 * 1024;

    /// <summary>The room for the answer of <c>rev-parse</c>: a longer one is not a repository's.</summary>
    private const int MaxProbeCharacters = 64 * 1024;

    /// <summary>The most remotes kept.</summary>
    public const int MaxRemotes = 100;

    /// <summary>The longest address kept; a longer one is left out.</summary>
    public const int MaxUrlCharacters = 2048;

    private readonly GitCommandRunner _git;
    private readonly string _worktreeRoot;

    public RepositoryRemoteReader(IProcessRunner runner, IExecutableLocator locator, GitWorktreeOptions options)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(options);
        _git = new GitCommandRunner(runner, locator, options);
        _worktreeRoot = GitNaming.NormalizePath(options.WorktreeRoot);
    }

    public async Task<RemoteListing> ListRemotesAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        try
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            {
                return RemoteListing.Refused("not-a-repository");
            }

            var directory = GitNaming.NormalizePath(projectRoot);
            if (IsInsideWorktreeRoot(directory))
            {
                return RemoteListing.Refused("inside-worktree-root");
            }

            var paths = await _git.RunAsync(directory, ["rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"], null, cancellationToken, maxOutputCharacters: MaxProbeCharacters).ConfigureAwait(false);
            var lines = paths.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paths.OutputLimitExceeded || paths.ExitCode != 0 || lines.Length != 3)
            {
                return RemoteListing.Refused("not-a-repository");
            }

            var root = GitNaming.NormalizePath(lines[0]);
            var gitDirectory = GitNaming.NormalizePath(lines[1]);
            var commonDirectory = GitNaming.NormalizePath(lines[2]);
            if (IsInsideWorktreeRoot(root) || IsInsideWorktreeRoot(gitDirectory) || IsInsideWorktreeRoot(commonDirectory))
            {
                return RemoteListing.Refused("inside-worktree-root");
            }

            WorktreeFileSystem.RequireDotGitWithoutLink(lines[0]);
            WorktreeFileSystem.RequireGitDirectoriesWithoutLinks(commonDirectory, gitDirectory);

            var result = await _git.RunAsync(
                root,
                ["--git-dir=" + gitDirectory, "config", "--local", "--no-includes", "-z", "--get-regexp", "--end-of-options", @"^remote\..*\.url$"],
                null,
                cancellationToken,
                maxOutputCharacters: MaxOutputCharacters).ConfigureAwait(false);
            if (result.OutputLimitExceeded)
            {
                return RemoteListing.Refused(RemoteListing.OutputTooBig);
            }

            // Exit code 1 is "no matching key": a repository without remotes.
            return result.ExitCode switch
            {
                0 => new RemoteListing(Parse(result.StandardOutput), null),
                1 => new RemoteListing([], null),
                _ => RemoteListing.Refused("git-failed"),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested
            && exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return RemoteListing.Refused(CodeOf(exception));
        }
    }

    /// <summary>
    /// The entries of <c>config -z --get-regexp</c>: <c>key NL value NUL</c>. The key is <c>remote.&lt;name&gt;.url</c>, the name being
    /// everything between the first and the last dot (a name may hold dots). The first address of a name wins; one that is blank, too
    /// long or refused by <see cref="RepositoryRemote"/> is left out.
    /// </summary>
    private static List<RepositoryRemote> Parse(string output)
    {
        const string prefix = "remote.";
        const string suffix = ".url";
        var remotes = new List<RepositoryRemote>();
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = entry.IndexOf('\n', StringComparison.Ordinal);
            if (newline <= 0)
            {
                continue;
            }

            var key = entry[..newline];
            var url = entry[(newline + 1)..].Trim();
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || key.Length <= prefix.Length + suffix.Length)
            {
                continue;
            }

            var name = key[prefix.Length..^suffix.Length];
            if (url.Length is 0 or > MaxUrlCharacters || remotes.Exists(r => string.Equals(r.Name, name, StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                remotes.Add(RepositoryRemote.Cleaned(name, url));
            }
            catch (ArgumentException)
            {
                // A name or an address git itself would refuse: the node has no use for it.
            }

            if (remotes.Count == MaxRemotes)
            {
                break;
            }
        }

        return remotes;
    }

    private bool IsInsideWorktreeRoot(string path) => GitNaming.SamePath(path, _worktreeRoot) || GitNaming.IsUnder(path, _worktreeRoot);

    private static string CodeOf(Exception exception) => exception switch
    {
        WorktreeOwnershipException => "unsafe-repository",
        GitUnavailableException or System.ComponentModel.Win32Exception => "git-unavailable",
        GitCommandException => "git-failed",
        _ => "unreadable",
    };
}
