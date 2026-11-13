using System.IO;
using AgentStudio.Application.Hosting;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Desktop.Services;
using AgentStudio.Infrastructure.Git.Remote;
using AgentStudio.Infrastructure.Hosting.GitHub;
using AgentStudio.Infrastructure.Hosting.State;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Orchestration.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// Git remote (Phase 10, docs/21 §10.9, ADR-0064 to ADR-0068): the remote executor of git (the one that knows only <c>GitHubHttps</c>), GitHub through <c>gh</c> with
/// the checks of Auto Doctor, the projections of the pull requests, the gate with the service of publication and the mailbox with its poll, the local state of the
/// remote, the permission «Sincronizar con GitHub» and the screens that show it. All of them need what <see cref="PlatformServices"/> registered first (the process
/// runner, the roots that are not trusted, the door of Auto Doctor, the log, the approvals, the delivery, the readers of the history), so it is called after
/// <c>AddDoctorServices</c>; whatever a test registered earlier is kept.
/// </summary>
internal static class HostingServices
{
    /// <summary>
    /// How long the exit waits for the stretch of the mailbox that is going on: a <c>push</c> is not cut in half (docs/21 D-25), and the longest one the executor allows
    /// ends well before this.
    /// </summary>
    internal static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(170);

    internal static IServiceCollection AddHostingServices(this IServiceCollection services, AppFolders folders)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(folders);

        // The local state lives next to the folders of gh and of the executor: %LOCALAPPDATA%\AgentStudio\hosting of this app's folders (a test has its own).
        services.TryAddSingleton(new RemoteSyncStoreOptions(Path.Combine(folders.Local.BaseDirectory, "hosting")));

        // One value of the settings answers the switch of a project and the policy of the mailbox: without the permission there is no network in the background (D-02).
        services.TryAddSingleton(provider => new AppSettingsRemoteSyncPermission(provider.GetRequiredService<IAppSettingsStore>()));
        services.TryAddSingleton<IRemoteSyncPermission>(provider => provider.GetRequiredService<AppSettingsRemoteSyncPermission>());
        services.TryAddSingleton<IRemoteSyncPolicy>(provider => provider.GetRequiredService<AppSettingsRemoteSyncPermission>());

        services
            .AddAgentStudioRemoteGit()
            .AddAgentStudioGitHubHosting(folders.Local.BaseDirectory)
            .AddAgentStudioHostingProjections()
            .AddAgentStudioRemoteSyncStore()
            .AddAgentStudioPublication();

        // The shell: opening a project opens its mailbox, and leaving ends the stretch in course.
        services.TryAddSingleton(provider => new RemoteSyncAttachment(
            provider.GetService<AgentStudio.Application.Projects.IProjectWorkspace>(),
            provider.GetService<RemoteSyncService>(),
            provider.GetService<AppSettingsRemoteSyncPermission>(),
            provider.GetService<Microsoft.Extensions.Logging.ILogger<RemoteSyncAttachment>>()));

        // The section of the pull requests (docs/21 §7.1): the real ports, and the browser of Windows for «Abrir en el navegador».
        services.TryAddSingleton<IAddressOpener, ShellAddressOpener>();
        services.TryAddSingleton(provider => new PullRequestsViewModel(
            provider.GetService<IHostingProviders>(),
            provider.GetService<IPullRequestProjector>(),
            provider.GetService<IRemoteSync>(),
            provider.GetService<IPublicationService>(),
            provider.GetService<IHostingPolicyGate>(),
            provider.GetService<IRemoteSyncPolicy>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<IAddressOpener>(),
            provider.GetService<IRemoteSyncPermission>()));
        return services;
    }
}
