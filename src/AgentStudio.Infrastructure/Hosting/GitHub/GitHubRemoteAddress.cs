using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>
/// Recognises the repository that a remote names from the URL as the git layer of the app keeps it: <b>without the user</b> (<c>RepositoryRemote.Cleaned</c> takes <c>git@</c> out of the short form of ssh and of
/// <c>ssh://git@…</c>, because a user can carry a secret). <see cref="RemoteRepository.TryParseGitHub"/> asks for the short form with its <c>git@</c>; so the clean forms are put back to the one it knows before
/// asking, and nothing else is changed: another host, a port, an owner or a name outside the rules are refused by the type, never fixed here. Whether the remote is by ssh is told apart as well, for Auto Doctor.
/// </summary>
internal static class GitHubRemoteAddress
{
    /// <summary>The repository of a remote URL (clean or not), or null when it is not one of GitHub.</summary>
    internal static RemoteRepository? TryParse(string? url, out bool bySsh)
    {
        bySsh = false;
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var normalised = url;
        if (url.StartsWith("github.com:", StringComparison.Ordinal))
        {
            normalised = "git@" + url;
        }
        else if (url.StartsWith("ssh://github.com/", StringComparison.Ordinal))
        {
            normalised = "git@github.com:" + url["ssh://github.com/".Length..];
        }
        else if (url.StartsWith("ssh://git@github.com/", StringComparison.Ordinal))
        {
            normalised = "git@github.com:" + url["ssh://git@github.com/".Length..];
        }

        var repository = RemoteRepository.TryParseGitHub(normalised, out _);
        bySsh = repository is not null && normalised.StartsWith("git@", StringComparison.Ordinal);
        return repository;
    }
}
