using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>
/// The provider of a repository (docs/21 D-09): by the kind its identifier carries, and only GitHub has an adapter in Phase 10. <see cref="IdentifyAsync"/> reads the remotes of the repository through
/// the hardened reader of the git layer (no network, nothing the repository controls runs) and recognises the address by the rules of <see cref="RemoteRepository"/>: an unknown host, a port, a user or a
/// token in the address, or an owner or a name outside the rules give no repository, never a corrected one.
/// </summary>
public sealed class HostingProviders(GitHubHostingProvider gitHub, IRepositoryRemoteReader remotes) : IHostingProviders
{
    public IHostingProvider? For(RemoteRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return repository.Provider == gitHub.Kind ? gitHub : null;
    }

    public async Task<RemoteRepository?> IdentifyAsync(ProjectContext project, string remoteName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
        var listing = await remotes.ListRemotesAsync(project.RootPath, ct).ConfigureAwait(false);
        var remote = listing.Remotes.FirstOrDefault(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));
        return remote is null ? null : GitHubRemoteAddress.TryParse(remote.Url, out _);
    }
}
