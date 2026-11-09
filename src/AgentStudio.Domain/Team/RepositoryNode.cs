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

    /// <summary>Whether <paramref name="url"/> carries user information, a query or a fragment: whether <see cref="WithoutCredentials"/> would change it.</summary>
    public static bool HasCredentials(string? url) => url is not null && !string.Equals(WithoutCredentials(url), url, StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="url"/> without anything that can carry a secret: the user information (<c>scheme://user:pass@host</c>, and the
    /// short form <c>user@host:path</c> of ssh too), the whole query and the fragment (<c>?token=…</c>, <c>#…</c>). The authority
    /// is taken up to the first <c>/</c> and its user information up to the last <c>@</c> of it, so a password with a <c>?</c> or a
    /// <c>#</c> in it goes entirely. A path on this computer is left as it is.
    /// </summary>
    public static string WithoutCredentials(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            var rest = url[(scheme + 3)..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var authority = slash < 0 ? rest : rest[..slash];
            var path = slash < 0 ? string.Empty : rest[slash..];
            authority = authority[(authority.LastIndexOf('@') + 1)..];
            if (slash < 0)
            {
                authority = CutQuery(authority);
            }

            return url[..(scheme + 3)] + authority + CutQuery(path);
        }

        // The short form of ssh: user@host:path.
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        var firstSlash = url.IndexOf('/', StringComparison.Ordinal);
        var hostEnd = colon >= 0 && (firstSlash < 0 || colon < firstSlash) ? colon : -1;
        if (hostEnd > 0 && url[..hostEnd].Contains('@', StringComparison.Ordinal))
        {
            var host = url[..hostEnd];
            return host[(host.LastIndexOf('@') + 1)..] + CutQuery(url[hostEnd..]);
        }

        return url;
    }

    private static string CutQuery(string text)
    {
        var cut = text.IndexOfAny(['?', '#']);
        return cut < 0 ? text : text[..cut];
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
