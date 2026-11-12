using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Git;

public static class GitDependencyInjection
{
    /// <summary>
    /// Registers the git layer: one <see cref="GitWorktreeManager"/> for the whole app, as <see cref="IWorktreeManager"/>
    /// and as itself. It must be a single instance: mutating operations on a repository are serialised inside it, and it
    /// is the only path to git of the integration, the delivery and the cleanup. The worktree root is
    /// <see cref="GitWorktreeOptions.DefaultWorktreeRoot"/> unless <paramref name="options"/> gives another. The process
    /// runner and the executable locator registered earlier (<c>AddAgentStudioInfrastructure</c>) are kept.
    /// </summary>
    public static IServiceCollection AddAgentStudioGit(this IServiceCollection services, GitWorktreeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IExecutableLocator>(provider => new ExecutableLocator(roots: provider.GetService<IUntrustedRoots>()));
        if (options is null)
        {
            services.TryAddSingleton(new GitWorktreeOptions(GitWorktreeOptions.DefaultWorktreeRoot));
        }
        else
        {
            services.AddSingleton(options);
        }

        services.TryAddSingleton(provider => new GitWorktreeManager(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IExecutableLocator>(),
            provider.GetRequiredService<GitWorktreeOptions>()));
        services.TryAddSingleton<IWorktreeManager>(provider => provider.GetRequiredService<GitWorktreeManager>());
        services.TryAddSingleton<IRepositoryHistoryReader>(provider => new RepositoryHistoryReader(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IExecutableLocator>(),
            provider.GetRequiredService<GitWorktreeOptions>()));
        services.TryAddSingleton<IRepositoryRemoteReader>(provider => new RepositoryRemoteReader(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IExecutableLocator>(),
            provider.GetRequiredService<GitWorktreeOptions>()));
        return services;
    }
}
