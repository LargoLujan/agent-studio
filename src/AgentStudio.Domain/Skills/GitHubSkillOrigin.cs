using System.Diagnostics.CodeAnalysis;

namespace AgentStudio.Domain.Skills;

/// <summary>Which part of the origin of a pinned skill is not valid (docs/20 D-14); the doctor says «origen no válido» and asks nothing.</summary>
public enum GitHubOriginProblem
{
    Owner,
    Repository,
    Branch,
    Commit,
    Folder,
}

/// <summary>
/// Where a pinned skill lives on GitHub, checked (docs/20 D-14 and D-15, F6-13). The five parts came from an index or from a URL the person
/// typed: they are not trusted, and an address is only ever built from a value that passed here. The rules are the ones of the design:
/// <list type="bullet">
/// <item><c>owner</c>: 1 to 39 characters, letters, digits and <c>-</c>, starting with a letter or digit;</item>
/// <item><c>repo</c>: 1 to 100 characters, letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, and never <c>.</c> or <c>..</c>;</item>
/// <item><c>commit</c>: 40 lowercase hexadecimal characters;</item>
/// <item><c>branch</c>: 1 to 200 characters, letters, digits, <c>.</c>, <c>_</c>, <c>-</c> and <c>/</c>, no <c>..</c>, not starting or ending with <c>/</c>;</item>
/// <item><c>folder</c>: empty (the root of the repository) or segments joined by <c>/</c> that follow the rule of <c>repo</c>, up to 400 characters.</item>
/// </list>
/// Nothing is escaped or normalized here: what is not valid is refused, so a <c>..</c>, a <c>?</c>, a <c>#</c>, a <c>%2e%2e</c>, a new line or a
/// backslash never reaches the builder of addresses. The checks go character by character, not by regular expression, so a trailing new line
/// cannot slip through the end of a pattern.
/// </summary>
public sealed record GitHubSkillOrigin
{
    public const int MaxOwnerLength = 39;

    public const int MaxRepositoryLength = 100;

    public const int MaxBranchLength = 200;

    public const int MaxFolderLength = 400;

    public const int CommitLength = 40;

    private GitHubSkillOrigin(string owner, string repository, string branch, string commit, string folder)
    {
        Owner = owner;
        Repository = repository;
        Branch = branch;
        Commit = commit;
        Folder = folder;
    }

    public string Owner { get; }

    public string Repository { get; }

    public string Branch { get; }

    /// <summary>The commit the skill is pinned to: 40 lowercase hexadecimal characters.</summary>
    public string Commit { get; }

    /// <summary>The folder of the skill inside the repository, relative and with <c>/</c>; empty for the root.</summary>
    public string Folder { get; }

    /// <exception cref="ArgumentException">A part is not valid; the message names which, never the value.</exception>
    public static GitHubSkillOrigin Create(string? owner, string? repository, string? branch, string? commit, string? folder) =>
        TryCreate(owner, repository, branch, commit, folder, out var origin, out var problem)
            ? origin
            : throw new ArgumentException($"The {problem} of the origin of a skill is not valid.");

    public static bool TryCreate(
        string? owner,
        string? repository,
        string? branch,
        string? commit,
        string? folder,
        [NotNullWhen(true)] out GitHubSkillOrigin? origin,
        out GitHubOriginProblem problem)
    {
        origin = null;
        problem = default;
        if (!IsOwner(owner))
        {
            problem = GitHubOriginProblem.Owner;
            return false;
        }

        if (!IsRepository(repository))
        {
            problem = GitHubOriginProblem.Repository;
            return false;
        }

        if (!IsBranch(branch))
        {
            problem = GitHubOriginProblem.Branch;
            return false;
        }

        if (!IsCommit(commit))
        {
            problem = GitHubOriginProblem.Commit;
            return false;
        }

        if (!IsFolder(folder))
        {
            problem = GitHubOriginProblem.Folder;
            return false;
        }

        origin = new GitHubSkillOrigin(owner, repository, branch, commit, folder);
        return true;
    }

    public static bool IsOwner([NotNullWhen(true)] string? value) =>
        value is { Length: >= 1 and <= MaxOwnerLength }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public static bool IsRepository([NotNullWhen(true)] string? value) =>
        value is { Length: >= 1 and <= MaxRepositoryLength } && value is not ("." or "..") && value.All(IsNameCharacter);

    public static bool IsCommit([NotNullWhen(true)] string? value) =>
        value is { Length: CommitLength } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static bool IsBranch([NotNullWhen(true)] string? value) =>
        value is { Length: >= 1 and <= MaxBranchLength }
        && value.All(c => IsNameCharacter(c) || c == '/')
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.StartsWith('/')
        && !value.EndsWith('/')
        && !value.Contains("//", StringComparison.Ordinal);

    public static bool IsFolder([NotNullWhen(true)] string? value) =>
        value is not null
        && value.Length <= MaxFolderLength
        && (value.Length == 0 || value.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") && segment.All(IsNameCharacter)));

    private static bool IsNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';
}
