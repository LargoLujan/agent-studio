using AgentStudio.Domain.Team;

namespace AgentStudio.Application.Git;

/// <summary>
/// The remotes of a repository, as the hardened git layer read them (docs/17 F13-3, P51).
/// </summary>
/// <param name="Remotes">The remotes in the order of the configuration, each with a URL clean of credentials; empty when none was read.</param>
/// <param name="RefusalCode">Why nothing was read: <c>output-too-big</c> (the list passed its bound), <c>not-a-repository</c>, <c>unsafe-repository</c>, <c>git-unavailable</c>, <c>git-failed</c> or <c>unreadable</c>; null when the read went through (a repository without remotes included). Never a path.</param>
public sealed record RemoteListing(IReadOnlyList<RepositoryRemote> Remotes, string? RefusalCode)
{
    /// <summary>The code of <see cref="RefusalCode"/> for an answer past the bound.</summary>
    public const string OutputTooBig = "output-too-big";

    public static RemoteListing Refused(string code) => new([], code);
}

/// <summary>The remotes of the repository of a project, read without network and without running anything the repository controls.</summary>
public interface IRepositoryRemoteReader
{
    /// <summary>
    /// Lists the remotes (name and URL) of the repository at <paramref name="projectRoot"/>. It fails closed: a repository that is
    /// not accepted, git failing, or an answer past the bound returns no remotes and says why in <see cref="RemoteListing.RefusalCode"/>.
    /// Only the cancellation throws.
    /// </summary>
    Task<RemoteListing> ListRemotesAsync(string projectRoot, CancellationToken cancellationToken);
}
