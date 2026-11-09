namespace AgentStudio.Domain.Team;

/// <summary>
/// A remote of the repository (docs/17 D-10), with its URL clean: never a user nor a password. <c>https://usuario:token@host/…</c>
/// is shown and stored as <c>https://host/…</c>. A remote is built with <see cref="Cleaned"/> from what git says; the
/// constructor refuses a URL that still carries credentials, so no node, screen or file can hold one.
/// </summary>
/// <remarks>
/// Only the user information of a URL with a scheme (<c>scheme://user:password@host</c>) is a credential. The short form of
/// ssh (<c>git@github.com:org/repo.git</c>) names the account of the server, not a secret, and stays as it is.
/// </remarks>
public sealed record RepositoryRemote
{
    /// <exception cref="ArgumentException">A blank name or URL, or a URL with credentials.</exception>
    public RepositoryRemote(string name, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (HasCredentials(url))
        {
            throw new ArgumentException("A remote is never kept with the user or the password of its URL: clean it first (RepositoryRemote.Cleaned).", nameof(url));
        }

        Name = name;
        Url = url;
    }

    /// <summary>The name of the remote, such as <c>origin</c>.</summary>
    public string Name { get; }

    /// <summary>The URL without credentials.</summary>
    public string Url { get; }

    /// <summary>The remote as git names it, with the user and the password of its URL removed.</summary>
    /// <exception cref="ArgumentException">A blank name or URL.</exception>
    public static RepositoryRemote Cleaned(string name, string url) => new(name, WithoutCredentials(url));

    /// <summary>Whether <paramref name="url"/> carries user information after its scheme (<c>scheme://…@host</c>).</summary>
    public static bool HasCredentials(string? url) => url is not null && UserInfoRange(url) is not null;

    /// <summary><paramref name="url"/> without the user information after its scheme; any other URL as it is.</summary>
    public static string WithoutCredentials(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return UserInfoRange(url) is { } range ? string.Concat(url.AsSpan(0, range.Start), url.AsSpan(range.End + 1)) : url;
    }

    /// <summary>Where the user information of a URL with a scheme is, up to and including its <c>@</c>; null when it has none.</summary>
    private static (int Start, int End)? UserInfoRange(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0)
        {
            return null;
        }

        var start = scheme + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], start);
        var authority = authorityEnd < 0 ? url[start..] : url[start..authorityEnd];
        var at = authority.LastIndexOf('@');
        return at < 0 ? null : (start, start + at);
    }
}

/// <summary>
/// The repository of the project as the Team layer shows it (docs/17 D-10): the folder, the current branch and its short HEAD,
/// the remotes with their clean URL, whether there are changes not committed, the stack detected and the main folders. Read
/// through the hardened git layer without network (no fetch, nothing asked of the remote). Each main folder is a connector of
/// the node: the cable «Trabaja en» goes from an agent to one of them.
/// </summary>
/// <param name="Folder">The folder of the repository on this computer, as shown.</param>
/// <param name="Branch">The branch checked out; null on a detached HEAD.</param>
/// <param name="HeadShort">The short hash of HEAD; null in a repository without commits.</param>
/// <param name="Remotes">The remotes, clean.</param>
/// <param name="HasUncommittedChanges">With changes not committed: working in the repository during a run makes the task fail (ADR-0013, point 7).</param>
/// <param name="Stack">The stack detected (<c>ProjectStackDetector</c>), or null.</param>
/// <param name="MainFolders">The main folders, relative to the repository, with <c>/</c> and no <c>..</c>: by default the top-level ones with most files, the person chooses.</param>
public sealed record RepositoryNode(
    string Folder,
    string? Branch,
    string? HeadShort,
    IReadOnlyList<RepositoryRemote> Remotes,
    bool HasUncommittedChanges,
    string? Stack,
    IReadOnlyList<string> MainFolders)
{
    /// <summary>The folders that are never a main folder by default: git's own and what builds leave.</summary>
    public static IReadOnlyList<string> ExcludedByDefault { get; } = [".git", "bin", "obj", "node_modules"];

    /// <summary>Whether <paramref name="folder"/> is a main folder as the node keeps one: relative, with <c>/</c>, without <c>..</c> nor a drive.</summary>
    public static bool IsMainFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var trimmed = folder.EndsWith('/') ? folder[..^1] : folder;
        return trimmed.Length > 0
            && !trimmed.Contains('\\', StringComparison.Ordinal)
            && !trimmed.StartsWith('/')
            && !trimmed.Contains(':', StringComparison.Ordinal)
            && trimmed.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }
}
