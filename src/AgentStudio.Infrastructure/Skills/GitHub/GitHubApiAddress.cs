using AgentStudio.Domain.Skills;

namespace AgentStudio.Infrastructure.Skills.GitHub;

/// <summary>
/// The only place that builds an address for the API of GitHub (docs/20 D-14). It takes a <see cref="GitHubSkillOrigin"/> (already checked, character
/// by character) and never a loose string, escapes every piece again, puts the branch, the commit and the folder as values of the query, and checks the
/// final address before returning it: <c>https</c>, host <c>api.github.com</c>, the default port, no credentials in it, no fragment, and a path that is
/// exactly one of the two this app asks for. So a <c>..</c>, a <c>?</c>, a <c>#</c> or a <c>%2e%2e</c> in a datum never leads to another point of the API
/// carrying the token: the origin refuses it first, and this refuses it a second time.
/// </summary>
public static class GitHubApiAddress
{
    /// <summary>The only host the app talks to.</summary>
    public const string Host = "api.github.com";

    /// <summary>Everything asked is read under this prefix; a redirection to anything else is refused.</summary>
    public const string ReposPrefix = "/repos/";

    /// <summary>The path GitHub redirects a renamed repository to.</summary>
    public const string RepositoriesPrefix = "/repositories/";

    /// <summary><c>GET /repos/{owner}/{repo}</c>: whether it is archived (or gone).</summary>
    public static Uri Repository(GitHubSkillOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var path = RepositoryPath(origin);
        return Checked("https://" + Host + path, path, origin, query: false);
    }

    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/commits?sha={sha}&amp;path={folder}&amp;per_page=1</c>: the last commit that touched the folder, going back from
    /// <paramref name="sha"/> (a commit or a branch). An empty folder (the root) leaves <c>path</c> out.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sha"/> is neither the commit nor the branch of the origin.</exception>
    public static Uri Commits(GitHubSkillOrigin origin, string sha)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!string.Equals(sha, origin.Commit, StringComparison.Ordinal) && !string.Equals(sha, origin.Branch, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only the commit or the branch of the origin can be asked for.", nameof(sha));
        }

        var path = RepositoryPath(origin) + "/commits";
        var query = "?sha=" + Uri.EscapeDataString(sha);
        if (origin.Folder.Length > 0)
        {
            query += "&path=" + Uri.EscapeDataString(origin.Folder);
        }

        query += "&per_page=1";
        return Checked("https://" + Host + path + query, path, origin, query: true);
    }

    /// <summary>
    /// Whether <paramref name="uri"/> may be asked for at all: <c>https</c>, the host of the API, the default port, no credentials, no fragment and a
    /// path under <c>/repos/</c> (or <c>/repositories/</c>, where GitHub sends a renamed repository). The guard of the handler and the manual
    /// redirections both ask this.
    /// </summary>
    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && PathAsWritten(uri) is { } written
        && !written.Contains("..", StringComparison.Ordinal)
        && !written.Contains('%', StringComparison.Ordinal)
        && !written.Contains('\\', StringComparison.Ordinal)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0
        && (uri.AbsolutePath.StartsWith(ReposPrefix, StringComparison.Ordinal) || uri.AbsolutePath.StartsWith(RepositoriesPrefix, StringComparison.Ordinal))
        && !uri.AbsolutePath.Contains("..", StringComparison.Ordinal)
        && !uri.AbsolutePath.Contains('%', StringComparison.Ordinal);

    /// <summary>
    /// The address as it was written, up to its query: <see cref="Uri"/> collapses <c>..</c> and <c>%2e%2e</c> in <see cref="Uri.AbsolutePath"/>, so a path that
    /// climbs would look like a plain one there. What was written must not have them.
    /// </summary>
    private static string? PathAsWritten(Uri uri)
    {
        var text = uri.OriginalString;
        var query = text.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? text : text[..query];
    }

    private static string RepositoryPath(GitHubSkillOrigin origin) =>
        ReposPrefix + Uri.EscapeDataString(origin.Owner) + "/" + Uri.EscapeDataString(origin.Repository);

    private static Uri Checked(string text, string expectedPath, GitHubSkillOrigin origin, bool query)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !IsAllowed(uri)
            || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal)
            || !uri.AbsolutePath.StartsWith(ReposPrefix + origin.Owner + "/" + origin.Repository, StringComparison.Ordinal)
            || (!query && uri.Query.Length != 0))
        {
            // The origin passed its own checks, so this is a defect of the builder: nothing is sent.
            throw new InvalidOperationException("The address built for GitHub did not pass the final check.");
        }

        return uri;
    }
}
