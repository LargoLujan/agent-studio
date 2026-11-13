using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Infrastructure.Hosting.Cli;
using AgentStudio.Infrastructure.Hosting.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>Registration of GitHub through <c>gh</c> (docs/21 D-09 to D-14; ADR-0065). The composition of the app calls it; no other part of the project does.</summary>
public static class GitHubHostingServiceCollectionExtensions
{
    /// <summary>
    /// The launcher of <c>gh</c>, its single queue, the provider, the registry of providers and the checks of Auto Doctor about <c>gh</c> and the remote. It needs <c>IProcessRunner</c> (<c>AddAgentStudioInfrastructure</c>),
    /// <c>IDoctorToolRunner</c> (<c>AddAgentStudioDoctorTools</c>), <c>IRepositoryRemoteReader</c> and <c>IUntrustedRoots</c> (required: without the roots <c>gh.exe</c> would be looked for in the whole PATH, projects included); <c>IPublicationService</c>, <c>IRemoteSync</c> and <c>IRemoteSyncPolicy</c> are used when
    /// they are registered. <paramref name="localBase"/> is <c>%LOCALAPPDATA%\AgentStudio</c>: the folders of <c>gh</c> live under it, in <c>hosting</c>.
    /// </summary>
    public static IServiceCollection AddAgentStudioGitHubHosting(this IServiceCollection services, string localBase)
    {
        ArgumentNullException.ThrowIfNull(services);
        var folders = GhFolders.Under(localBase);
        services.TryAddSingleton(folders);
        services.TryAddSingleton(provider => new GhRunner(provider.GetRequiredService<IProcessRunner>(), folders, provider.GetRequiredService<IUntrustedRoots>()));
        services.TryAddSingleton(provider => new GhQueue(provider.GetRequiredService<GhRunner>(), provider.GetService<TimeProvider>()));
        services.TryAddSingleton(provider => new GitHubHostingProvider(provider.GetRequiredService<GhQueue>(), provider.GetRequiredService<IDoctorToolRunner>()));
        services.TryAddSingleton<IHostingProvider>(provider => provider.GetRequiredService<GitHubHostingProvider>());
        services.TryAddSingleton<IHostingProviders>(provider => new HostingProviders(
            provider.GetRequiredService<GitHubHostingProvider>(),
            provider.GetRequiredService<AgentStudio.Application.Git.IRepositoryRemoteReader>()));
        services.AddSingleton<IDoctorCheckSource>(provider => new GhToolCheckSource(provider.GetRequiredService<IDoctorToolRunner>()));
        services.AddSingleton<IDoctorCheckSource>(provider => new HostingCheckSource(
            provider.GetRequiredService<AgentStudio.Application.Git.IRepositoryRemoteReader>(),
            provider.GetService<IPublicationService>(),
            provider.GetService<IRemoteSync>(),
            provider.GetService<IRemoteSyncPolicy>()));
        return services;
    }
}
