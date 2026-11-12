using System.Globalization;
using System.Text;
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
/// <para>Bounded: at most <see cref="MaxCommitsLimit"/> commits whatever the query asks, <see cref="MaxBranches"/> branches, and every
/// text that comes from git (subject, author, ref) cut by git itself (<c>%&lt;(N,trunc)</c>) and again here, and cleaned of control
/// characters, because it is data of whoever wrote the commit. The output is bounded at its source: the process runner kills git
/// when a stream passes <see cref="MaxOutputCharacters"/> (the read is then refused), and the question «are there changes?» is
/// asked with a few characters of room, not for the list. A commit is never lost to its text: the fields are framed by a random
/// marker of each read that no writer can know, so a subject or author with control characters cannot break the record.</para>
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

    /// <summary>The most characters git may write to a stream in the log or the branch list: past it git is killed and the read is refused.</summary>
    public const int MaxOutputCharacters = 4 * 1024 * 1024;

    /// <summary>The room for the commands whose answer is a line (HEAD, the paths of the repository): a longer one is not a repository's.</summary>
    private const int MaxProbeCharacters = 64 * 1024;

    /// <summary>The room for «are there changes?»: any output means there are, so the list is never read in full (a tree with millions of untracked files).</summary>
    private const int ChangesProbeCharacters = 512;

    /// <summary>The most branches listed.</summary>
    public const int MaxBranches = 500;

    public const int MaxSubjectCharacters = 200;

    public const int MaxAuthorCharacters = 100;

    public const int MaxRefCharacters = 120;

    public const int MaxRefsPerCommit = 20;

    private const char FieldSeparator = '\u001f';
    private const string RunBranchPrefix = "agentstudio/";

    private const int MaxBranchNameCharacters = 200;

    // Widths that git cuts the free texts to (%<(N,trunc) pads to N columns and marks the cut with «..»); wider than the limits kept
    // here so the cut by Clean stays the one that decides, except for the refs and parents, whose last entry may arrive cut.
    private const int GitSubjectWidth = 240;
    private const int GitAuthorWidth = 120;
    private const int GitRefsWidth = 1200;
    private const int GitParentsWidth = 1300;
    private const int GitTrailersWidth = 480;
    private static readonly string TrailerKeys = string.Concat(
        new[] { AgentStudio.Domain.Tracing.CommitTrailers.RunKey, AgentStudio.Domain.Tracing.CommitTrailers.TaskKey, AgentStudio.Domain.Tracing.CommitTrailers.AttemptKey, AgentStudio.Domain.Tracing.CommitTrailers.AgentKey }
            .Select(key => $"key={key},"));

    private const int MaxTrailerLineCharacters = 240;
    private const int MaxTrailerLines = 8;
    private const int FieldsPerRecord = 8;

    /// <summary>The characters git refuses in a ref name (git check-ref-format), besides the control characters and the space.</summary>
    private static readonly char[] ForbiddenRefCharacters = ['~', '^', ':', '?', '*', '[', '\\'];

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
            var head = await _git.RunAsync(repository.Root, [.. GitDir(repository), "symbolic-ref", "--quiet", "HEAD"], null, cancellationToken, maxOutputCharacters: MaxProbeCharacters).ConfigureAwait(false);
            var branch = head.ExitCode == 0 ? StripHeads(head.StandardOutput.Trim()) : null;

            var commit = await _git.RunAsync(repository.Root, [.. GitDir(repository), "rev-parse", "--verify", "--quiet", "HEAD^{commit}"], null, cancellationToken, maxOutputCharacters: MaxProbeCharacters).ConfigureAwait(false);
            var commitId = commit.ExitCode == 0 && IsObjectId(commit.StandardOutput.Trim()) ? commit.StandardOutput.Trim() : null;

            var status = await _git.RunAsync(
                repository.Root,
                ["--git-dir=" + repository.GitDirectory, "--work-tree=" + repository.Root, "status", "--porcelain=v1", "--untracked-files=normal", "--ignore-submodules=all"],
                null,
                cancellationToken,
                ScopeOf(repository),
                ChangesProbeCharacters).ConfigureAwait(false);

            // Output past the room (git is killed for it) is itself the answer: there are changes.
            if (!status.OutputLimitExceeded && status.ExitCode != 0)
            {
                return Refused(project, "git-failed");
            }

            return new RepositorySummary(
                repository.Root, true, branch is null ? null : Clean(branch, MaxRefCharacters), commitId, status.OutputLimitExceeded || status.StandardOutput.Length > 0, []);
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
                cancellationToken,
                maxOutputCharacters: MaxOutputCharacters).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.OutputLimitExceeded)
            {
                return [];
            }

            var branches = new List<BranchSummary>();
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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

        // The marker frames every field of this read. It is random and only known here, so no text of a commit can contain it.
        var marker = "\u001e" + Guid.NewGuid().ToString("N") + "\u001e";
        var gitMarker = "%x1e" + marker[1..^1] + "%x1e";
        var format = string.Join(
            gitMarker,
            string.Empty,
            "%H",
            $"%<({GitParentsWidth.ToString(CultureInfo.InvariantCulture)},trunc)%P",
            "%ct",
            $"%<({GitRefsWidth.ToString(CultureInfo.InvariantCulture)},trunc)%D",
            $"%<({GitAuthorWidth.ToString(CultureInfo.InvariantCulture)},trunc)%an",
            $"%<({GitSubjectWidth.ToString(CultureInfo.InvariantCulture)},trunc)%s",

            // Not the body: the trailers of the last paragraph, which git finds itself and cuts, so a message of megabytes costs nothing here
            // and its trailers (at the end, where a cut of the body would lose them) still arrive. Only the keys the system writes: other trailers
            // (a Signed-off-by, a Co-authored-by) written before them cannot push them out of the width. The lines are kept raw (not unfolded).
            $"%<({GitTrailersWidth.ToString(CultureInfo.InvariantCulture)},trunc)%(trailers:{TrailerKeys}unfold=false)",
            string.Empty);
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
            "--format=" + format,
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
            if (!IsValidBranchName(branch))
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
            var result = await _git.RunAsync(repository.Root, arguments, null, cancellationToken, maxOutputCharacters: MaxOutputCharacters).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.OutputLimitExceeded)
            {
                return [];
            }

            return Parse(result.StandardOutput, marker, max);
        }
        catch (Exception exception) when (IsRefusal(exception, cancellationToken))
        {
            return [];
        }
    }

    /// <summary>
    /// The records of the log: «marker id marker parents marker time marker refs marker author marker subject marker newline», one
    /// after another, so the segments after the first empty one come in groups of <see cref="FieldsPerRecord"/> (the last is the
    /// newline git ends each record with). The marker is random per read: a text of a commit cannot contain it, so it cannot add
    /// or remove a field. A group that does not hold (an id that is not one, an incomplete one at the end) is skipped.
    /// </summary>
    private static List<CommitNode> Parse(string output, string marker, int max)
    {
        var commits = new List<CommitNode>();
        var segments = output.Split(marker);
        for (var start = 1; start + FieldsPerRecord <= segments.Length; start += FieldsPerRecord)
        {
            var fields = segments.AsSpan(start, FieldsPerRecord).ToArray();
            if (fields[7].Trim().Length != 0 || !IsObjectId(fields[0]) || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                continue;
            }

            // The parents are cut by git when a merge has too many: the last one may arrive cut and is left out.
            var tokens = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var parents = tokens.Where(IsObjectId).ToArray();
            if (parents.Length < tokens.Length - 1 || (parents.Length == tokens.Length - 1 && !tokens[^1].EndsWith("..", StringComparison.Ordinal)))
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

            var refs = fields[3]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MaxRefsPerCommit)
                .Select(reference => Clean(reference, MaxRefCharacters))
                .ToArray();
            var subject = Clean(fields[5], MaxSubjectCharacters);
            var trailers = CleanLines(fields[6], MaxTrailerLineCharacters, MaxTrailerLines);

            // The message as far as this read goes: the subject and, after a blank line, the trailers of the last paragraph when it has any.
            commits.Add(new CommitNode(fields[0], parents, at, subject, Clean(fields[4], MaxAuthorCharacters), refs, trailers.Length == 0 ? null : subject + "\n\n" + trailers));
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

            var paths = await _git.RunAsync(directory, ["rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"], null, cancellationToken, maxOutputCharacters: MaxProbeCharacters).ConfigureAwait(false);
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
        GitUnavailableException or System.ComponentModel.Win32Exception => "git-unavailable",
        GitCommandException => "git-failed",
        _ => "unreadable",
    };

    private bool IsInsideWorktreeRoot(string path) => GitNaming.SamePath(path, _worktreeRoot) || GitNaming.IsUnder(path, _worktreeRoot);

    private static string[] GitDir(GitRepositoryRef repository) => ["--git-dir=" + repository.GitDirectory];

    private static GitScope ScopeOf(GitRepositoryRef repository) => new(repository.Root, repository.CommonDirectory, repository.GitDirectory);

    private static string StripHeads(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    /// <summary>
    /// Whether <paramref name="name"/> is a branch name git accepts (<c>git check-ref-format</c>, which allows <c>@</c>, <c>+</c>,
    /// <c>#</c> and any Unicode letter) and cannot be read as an option or a revision expression. Anything else is refused: the
    /// read returns nothing, as for a branch that does not exist.
    /// </summary>
    private static bool IsValidBranchName(string name)
    {
        if (name.Length is 0 or > MaxBranchNameCharacters
            || name[0] is '-' or '/' || name[^1] is '/' or '.'
            || name == "@"
            || name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal)
            || name.IndexOfAny(ForbiddenRefCharacters) >= 0)
        {
            return false;
        }

        if (name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        return name.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
    }

    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);

    /// <summary>The non-empty lines of <paramref name="text"/> (git pads the field with spaces), each cleaned and cut, at most <paramref name="maxLines"/>, joined with a line feed.</summary>
    private static string CleanLines(string text, int limit, int maxLines) =>
        string.Join('\n', text.Split('\n').Select(line => Clean(line, limit)).Where(line => line.Length > 0).Take(maxLines));

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
