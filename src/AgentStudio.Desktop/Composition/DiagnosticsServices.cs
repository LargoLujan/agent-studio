using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Doctor;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Providers.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// Auto Doctor (Phase 6, docs/20 and ADR-0061; WP-A): the door for tools and the sources that read this computer (<c>AddAgentStudioDoctorTools</c>),
/// the sources of the providers and of the project, the service that runs them, the runner of fixes and the view model of the panel. The shell
/// takes the panel from the container; the integration of the other packages adds their own sources (<see cref="IDoctorCheckSource"/>) here.
/// </summary>
internal static class DiagnosticsServices
{
    internal static IServiceCollection AddDoctorServices(this IServiceCollection services, AppFolders folders)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(folders);
        services.AddAgentStudioDoctorTools(folders.Local.BaseDirectory);

        // The probes of the providers are not rewritten (D-01): the source adapts them, and the panel hands the same results to the rows of today.
        services.AddSingleton(provider => new ProviderCheckSource(
            provider.GetRequiredService<ProviderDiagnostics>(),
            new ProviderCheckOptions(
                IsUntested: result => result.Version is not null && SupportedVersions.UntestedNote(result.Provider, result.Version) is not null,
                IsParked: ProviderStatusItem.IsParkedResult)));
        services.AddSingleton<IDoctorCheckSource>(provider => provider.GetRequiredService<ProviderCheckSource>());
        services.AddSingleton<IDoctorCheckSource, ProjectCheckSource>();
        services.AddSingleton<IDoctorService>(provider => new DoctorService(provider.GetServices<IDoctorCheckSource>(), provider.GetServices<IDoctorRunScope>()));

        // No fix of Phase 6 asks or records anything beyond what the runner does; the gate and the log are the ones of the engine.
        services.AddSingleton<IDoctorFixRunner>(provider => new DoctorFixRunner(provider.GetService<IDecisionRecorder>(), provider.GetService<IApprovalGate>()));
        services.AddSingleton(provider => new DoctorViewModel(
            provider.GetService<IDoctorService>(),
            provider.GetRequiredService<ProviderDiagnosticsViewModel>(),
            provider.GetService<ProviderCheckSource>(),
            provider.GetService<IDoctorFixRunner>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<IProjectWorkspace>()));
        return services;
    }
}
