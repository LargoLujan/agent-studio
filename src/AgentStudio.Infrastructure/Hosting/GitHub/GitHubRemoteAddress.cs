using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>
/// The repository that a remote names, and whether the remote is by ssh (for Auto Doctor). The recognising is <see cref="RemoteRepository.TryParseGitHub"/>, which knows the addresses as the git layer keeps them,
/// without the user (<c>github.com:o/r</c>, <c>ssh://github.com/o/r</c>); nothing is normalised here: another host, a port, an owner or a name outside the rules are refused by the type.
/// </summary>
internal static class GitHubRemoteAddress
{
    internal static RemoteRepository? TryParse(string? url, out bool bySsh)
    {
        var repository = RemoteRepository.TryParseGitHub(url, out _);
        bySsh = repository is not null && url is not null && !url.StartsWith("https://", StringComparison.Ordinal);
        return repository;
    }
}