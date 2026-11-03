using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Orchestration.Engine;
using Engine = AgentStudio.Orchestration.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

internal static class UnattendedWorkServices
{
    /// <summary>
    /// Registers the unattended work of Phase 8 on the shell (docs/16 §4.5, «Integración»): Windows Hello and the lock
    /// (<see cref="LockServices.AddWindowsHelloAndLock"/>, ADR-0037), the background (the power events the engine listens to,
    /// «mantener despierto», the registry of the work, the managed exit, the tray and its panel:
    /// <see cref="BackgroundServiceCollectionExtensions.AddAgentStudioBackground"/>, ADR-0036), what the lock screen says goes
    /// on (<see cref="BackgroundWorkInProgress"/>, over that registry), «Trabajo desatendido» in «Ajustes», and the consented
    /// resume (<see cref="ConsentedResumes"/>) over the one orchestrator of the app. A registration made earlier wins, so
    /// tests put doubles of whatever would touch Windows first.
    /// </summary>
    internal static IServiceCollection AddUnattendedWorkServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWorkInProgress>(provider => new BackgroundWorkInProgress(provider.GetRequiredService<BackgroundWorkRegistry>()));
        services.TryAddSingleton(provider => new UnattendedSettingsViewModel(
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetService<LockSettingsViewModel>()));
        services.TryAddSingleton(provider => new ConsentedResumes(Scheduler(provider), provider.GetService<IProjectWorkspace>()));
        services.TryAddSingleton<IConsentedResumes>(provider => provider.GetRequiredService<ConsentedResumes>());
        return services
            .AddWindowsHelloAndLock()
            .AddAgentStudioBackground();
    }

    /// <summary>
    /// The engine's scheduler of consented resumes, over the same singleton orchestrator the screen resumes with by hand; null
    /// when the engine (or its log or its run registry) was not composed, or the orchestrator is not the engine's.
    /// </summary>
    private static ResumeScheduler? Scheduler(IServiceProvider provider) =>
        provider.GetService<IOrchestrator>() is Engine.Orchestrator engine
        && provider.GetService<IDecisionRecorder>() is { } decisions
        && provider.GetService<IRunRegistry>() is { } runs
            ? new ResumeScheduler(engine, decisions, runs)
            : null;
}
