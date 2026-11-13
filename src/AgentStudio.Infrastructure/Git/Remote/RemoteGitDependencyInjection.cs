using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Git.Remote;

public static class RemoteGitDependencyInjection
{
    /// <summary>
    /// Registers the remote executor of git (docs/21 D-06, ADR-0064) as <see cref="IRemoteGitRunner"/>. It has no parameters on purpose: the executor always gets <c>RemoteTransport.GitHubHttps</c>, and no option, setting,
    /// variable of the environment or file changes that. The process runner, the executable locator and the options of the git layer registered earlier are kept (the root of the worktrees is the one of those options, or the default).
    /// </summary>
    public static IServiceCollection AddAgentStudioRemoteGit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IExecutableLocator>(provider => new ExecutableLocator(roots: provider.GetService<IUntrustedRoots>()));
        services.TryAddSingleton<IRemoteGitRunner>(provider => new RemoteGitRunner(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IExecutableLocator>(),
            RemoteGitFolders.Default,
            provider.GetService<GitWorktreeOptions>()?.WorktreeRoot ?? GitWorktreeOptions.DefaultWorktreeRoot,
            RemoteTransport.GitHubHttps));
        return services;
    }
}
