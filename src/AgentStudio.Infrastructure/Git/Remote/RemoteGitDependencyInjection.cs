using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Git.Remote;

public static class RemoteGitDependencyInjection
{
    /// <summary>
    /// Registers the remote executor of git (docs/21 D-06, ADR-0064) with the current user's folders. The executor always gets <c>RemoteTransport.GitHubHttps</c>;
    /// no option, setting, environment variable or file changes that transport. Existing registrations of its ports and git options are kept.
    /// </summary>
    public static IServiceCollection AddAgentStudioRemoteGit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return AddRemoteGit(services, RemoteGitFolders.Default);
    }

    /// <summary>
    /// Registers the same HTTPS executor with folders under the host's local base. This changes only its temporary folder and empty hooks file, never its transport or permissions.
    /// </summary>
    public static IServiceCollection AddAgentStudioRemoteGit(this IServiceCollection services, string localBase)
    {
        ArgumentNullException.ThrowIfNull(services);

        return AddRemoteGit(services, RemoteGitFolders.Under(localBase));
    }

    private static IServiceCollection AddRemoteGit(IServiceCollection services, RemoteGitFolders folders)
    {
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IExecutableLocator>(provider => new ExecutableLocator(roots: provider.GetService<IUntrustedRoots>()));
        services.TryAddSingleton<IRemoteGitRunner>(provider => new RemoteGitRunner(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IExecutableLocator>(),
            folders,
            provider.GetService<GitWorktreeOptions>()?.WorktreeRoot ?? GitWorktreeOptions.DefaultWorktreeRoot,
            RemoteTransport.GitHubHttps));
        return services;
    }
}
