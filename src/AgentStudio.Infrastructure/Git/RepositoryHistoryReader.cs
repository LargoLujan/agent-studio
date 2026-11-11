using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;

namespace AgentStudio.Infrastructure.Git;

/// <summary>
/// Reads the history of the user's repository for «Repositorio y cambios» (docs/16 WP4, D-44), through the same hardened
/// <see cref="GitCommandRunner"/> as the rest of the git layer: no transport, no hooks, no inherited <c>GIT_*</c> variables, the
/// walk of the git directory for planted links and the inspection of the configuration for callbacks (ADR-0012). It reads only.
/// </summary>
/// <remarks>
/// <para>Bounded: at most <see cref="MaxCommitsLimit"/> commits whatever the query asks, <see cref="MaxOutputCharacters"/> of git's
/// output parsed, <see cref="MaxBranches"/> branches, and every text that comes from git (subject, author, ref) cut and cleaned of
/// control characters, because it is data of whoever wrote the commit.</para>
/// <para>Fails closed: when the repository is not accepted (not a repository, inside the worktree root, a planted link, a
/// configuration that could run a program the repository controls) <see cref="SummaryAsync"/> says so with the codes of the reasons
/// and the other two return nothing. Git failing to run, or timing out, reads the same way; only the cancellation propagates.</para>
/// <para>The log runs with the options that keep it from running anything: no textconv, no external diff, no signature
/// verification (<c>log.showSignature</c> would start gpg), no replace objects and an explicit encoding. The remote-tracking
/// branches take part in the graph (as refs of its commits) but not in the list of branches: the contract has no kind for them
/// until Phase 10.</para>
/// </remarks>
public sealed class RepositoryHistoryReader : IRepositoryHistoryReader
{
    /// <summary>The most commits one read returns, whatever the query asks.</summary>
    public const int MaxCommitsLimit = 1000;

    /// <summary>The most characters of git's output that are parsed in one read.</summary>
    public const int MaxOutputCharacters = 4 * 1024 * 1024;

    /// <summary>The most branches listed.</summary>
    public const int MaxBranches = 500;

    public const int MaxSubjectCharacters = 200;

    public const int MaxAuthorCharacters = 100;

    public const int MaxRefCharacters = 120;

    public const int MaxRefsPerCommit = 20;

    private const char FieldSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';
    private const string RunBranchPrefix = "agentstudio/";

    private static readonly Regex SafeBranchName = new(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,199}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly GitCommandRunner _git;
    private readonly string _worktreeRoot;

    public RepositoryHistoryReader(IProcessRunner runner, IExecutableLocator locator, GitWorktreeOptions options)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(options);
        _git = new GitCommandRunner(runner, locator, options);
        _worktreeRoot = GitNaming.NormalizePath(options.WorktreeRoot);
    }

    /// <summary>What the reader refuses with: the repository it could not open, and why (a code, never a path).</summary>
    private sealed record Opened(GitRepositoryRef? Repository, string? RefusalCode);

    private sealed record GitRepositoryRef(string Root, string GitDirectory, string CommonDirectory);

    public async Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var opened = await OpenAsync(project, cancellationToken).ConfigureAwait(false);
        if (opened.Repository is not { } repository)
        {
            return new RepositorySummary(project.RootPath, false, null, null, false, [opened.RefusalCode ?? "refused"]);
        }

        try
        {
            var head = await _git.RunAsync(repository.Root, [.. GitDir(repository), "symbolic-ref", "--quiet", "HEAD"], null, cancellationToken).ConfigureAwait(false);
            var branch = head.ExitCode == 0 ? StripHeads(head.StandardOutput.Trim()) : null;

            var commit = await _git.RunAsync(repository.Root, [.. GitDir(repository), "rev-parse", "--verify", "--quiet", "HEAD^{commit}"], null, cancellationToken).ConfigureAwait(false);
            var commitId = commit.ExitCode == 0 && IsObjectId(commit.StandardOutput.Trim()) ? commit.StandardOutput.Trim() : null;

            var status = await _git.RunAsync(
                repository.Root,
                ["--git-dir=" + repository.GitDirectory, "--work-tree=" + repository.Root, "status", "--porcelain=v1", "--untracked-files=normal", "--ignore-submodules=all"],
                null,
                cancellationToken,
                ScopeOf(repository)).ConfigureAwait(false);
            if (status.ExitCode != 0)
            {
                return Refused(project, "git-failed");
            }

            return new RepositorySummary(repository.Root, true, branch is null ? null : Clean(branch, MaxRefCharacters), commitId, status.StandardOutput.Length > 0, []);
        }
        catch (Exception exception) when (IsRefusal(exception, cancellationToken))
        {
            return Refused(project, CodeOf(exception));
        }
    }

    public async Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var opened = await OpenAsync(project, cancellationToken).ConfigureAwait(false);
        if (opened.Repository is not { } repository)
        {
            return [];
        }

        try
        {
            var result = await _git.RunAsync(
                repository.Root,
                [.. GitDir(repository), "for-each-ref", "--count=" + MaxBranches.ToString(CultureInfo.InvariantCulture), "--format=%(refname)%1f%(objectname)%1f%(HEAD)", "refs/heads/"],
                null,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return [];
            }

            var branches = new List<BranchSummary>();
            foreach (var line in Truncate(result.StandardOutput).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimEnd('\r').Split(FieldSeparator);
                if (fields.Length != 3 || !IsObjectId(fields[1]) || !fields[0].StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = fields[0]["refs/heads/".Length..];
                var kind = name.StartsWith(RunBranchPrefix, StringComparison.Ordinal) ? BranchKind.Run : BranchKind.Local;
                branches.Add(new BranchSummary(Clean(name, MaxRefCharacters), fields[1], fields[2] == "*", kind));
                if (branches.Count == MaxBranches)
                {
                    break;
                }
            }

            return branches;
        }
        catch (Exception exception) when (IsRefusal(exception, cancellationToken))
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        var opened = await OpenAsync(project, cancellationToken).ConfigureAwait(false);
        if (opened.Repository is not { } repository)
        {
            return [];
        }

        var max = Math.Clamp(query.MaxCommits, 1, MaxCommitsLimit);
        var arguments = new List<string>
        {
            "--no-replace-objects",
            "--git-dir=" + repository.GitDirectory,
            "-c", "log.showSignature=false",
            "-c", "log.mailmap=false",
            "log",
            "--max-count=" + max.ToString(CultureInfo.InvariantCulture),
            "--date-order",
            "--no-color",
            "--no-textconv",
            "--no-ext-diff",
            "--no-show-signature",
            "--no-notes",
            "--encoding=UTF-8",
            "--format=%H%x1f%P%x1f%ct%x1f%an%x1f%s%x1f%D%x1e",
        };
        if (query.Since is { } since)
        {
            arguments.Add("--since=" + since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        if (query.Until is { } until)
        {
            arguments.Add("--until=" + until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        if (query.Branch is { } branch)
        {
            if (!SafeBranchName.IsMatch(branch) || branch.Contains("..", StringComparison.Ordinal) || branch.EndsWith('/') || branch.EndsWith(".lock", StringComparison.Ordinal))
            {
                return [];
            }

            // The branch is resolved as a local branch only, so the name can never be read as an option or a revision expression.
            arguments.Add("refs/heads/" + branch);
        }
        else
        {
            arguments.Add("--branches");
            arguments.Add("--remotes");
            arguments.Add("HEAD");
        }

        arguments.Add("--");

        try
        {
            var result = await _git.RunAsync(repository.Root, arguments, null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return [];
            }

            return Parse(Truncate(result.StandardOutput), max);
        }
        catch (Exception exception) when (IsRefusal(exception, cancellationToken))
        {
            return [];
        }
    }

    private static List<CommitNode> Parse(string output, int max)
    {
        var commits = new List<CommitNode>();
        foreach (var record in output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\r', '\n').Split(FieldSeparator);
            if (fields.Length != 6 || !IsObjectId(fields[0]) || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                continue;
            }

            var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Any(parent => !IsObjectId(parent)))
            {
                continue;
            }

            DateTimeOffset at;
            try
            {
                at = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            var refs = fields[5]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MaxRefsPerCommit)
                .Select(reference => Clean(reference, MaxRefCharacters))
                .ToArray();
            commits.Add(new CommitNode(fields[0], parents, at, Clean(fields[4], MaxSubjectCharacters), Clean(fields[3], MaxAuthorCharacters), refs));
            if (commits.Count == max)
            {
                break;
            }
        }

        return commits;
    }

    /// <summary>
    /// Finds the repository of the project and applies the checks every command of the git layer is under: the working tree
    /// root and its directories are not inside the worktree root, no planted link in the git directory (S-01 to S-03) and no
    /// callback the repository controls in the effective configuration (N-01). The refusal is a code, never a path.
    /// </summary>
    private async Task<Opened> OpenAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(project.RootPath) || !Directory.Exists(project.RootPath))
            {
                return new Opened(null, "not-a-repository");
            }

            var directory = GitNaming.NormalizePath(project.RootPath);
            if (IsInsideWorktreeRoot(directory))
            {
                return new Opened(null, "inside-worktree-root");
            }

            var paths = await _git.RunAsync(directory, ["rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"], null, cancellationToken).ConfigureAwait(false);
            var lines = paths.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paths.ExitCode != 0 || lines.Length != 3)
            {
                return new Opened(null, "not-a-repository");
            }

            WorktreeFileSystem.RequireDotGitWithoutLink(lines[0]);
            var repository = new GitRepositoryRef(GitNaming.NormalizePath(lines[0]), GitNaming.NormalizePath(lines[1]), GitNaming.NormalizePath(lines[2]));
            if (IsInsideWorktreeRoot(repository.Root) || IsInsideWorktreeRoot(repository.GitDirectory) || IsInsideWorktreeRoot(repository.CommonDirectory))
            {
                return new Opened(null, "inside-worktree-root");
            }

            WorktreeFileSystem.RequireGitDirectoriesWithoutLinks(repository.CommonDirectory, repository.GitDirectory);
            var refusals = await _git.ClassifyRepositoryCallbacksAsync(
                repository.Root, ["--git-dir=" + repository.GitDirectory, "--work-tree=" + repository.Root], ScopeOf(repository), cancellationToken).ConfigureAwait(false);
            return refusals.Count == 0 ? new Opened(repository, null) : new Opened(null, "unsafe-configuration");
        }
        catch (Exception exception) when (IsRefusal(exception, cancellationToken))
        {
            return new Opened(null, CodeOf(exception));
        }
    }

    private static RepositorySummary Refused(ProjectContext project, string code) => new(project.RootPath, false, null, null, false, [code]);

    private static bool IsRefusal(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException
        && !cancellationToken.IsCancellationRequested
        && (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception);

    private static string CodeOf(Exception exception) => exception switch
    {
        UnsafeGitCallbackException => "unsafe-configuration",
        WorktreeOwnershipException => "unsafe-repository",
        UnsupportedRepositoryException => "unsupported-repository",
        GitUnavailableException or System.ComponentModel.Win32Exception => "git-unavailable",
        GitCommandException => "git-failed",
        _ => "unreadable",
    };

    private bool IsInsideWorktreeRoot(string path) => GitNaming.SamePath(path, _worktreeRoot) || GitNaming.IsUnder(path, _worktreeRoot);

    private static string[] GitDir(GitRepositoryRef repository) => ["--git-dir=" + repository.GitDirectory];

    private static GitScope ScopeOf(GitRepositoryRef repository) => new(repository.Root, repository.CommonDirectory, repository.GitDirectory);

    private static string StripHeads(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    private static string Truncate(string output) => output.Length <= MaxOutputCharacters ? output : output[..MaxOutputCharacters];

    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);

    /// <summary>The text of git cut to <paramref name="limit"/> characters, without control characters (a writer cannot add a line or an escape sequence).</summary>
    private static string Clean(string text, int limit)
    {
        var builder = new StringBuilder(Math.Min(text.Length, limit));
        foreach (var character in text)
        {
            if (builder.Length == limit)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
