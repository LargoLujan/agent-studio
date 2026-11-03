using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator.Monitor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// The composition of the Phase 5 screens (docs/15 section 9, WP6): «Debates», «Auditoría» and the review of a run in the
/// monitor. They register no port of their own: each one is taken when the composition root registered it (the debate
/// module, the audits, the stores, the approval gate), and without it the screen says what is missing.
/// </summary>
public static class DebateUiServices
{
    /// <summary>
    /// Registers <see cref="DebateScreenBackend"/> over whatever ports are registered (the fixed requirements of a mode
    /// come from a <see cref="DebateModeRequirements"/>, when the debate module gives one), <see cref="DebateScreens"/>
    /// (one «Debates» and one «Auditoría» per project, and the moves between them, through the shell's
    /// <see cref="IReviewNavigation"/>, resolved when it is needed) and the ports of the review of a run
    /// (<see cref="RunReviewServices"/>, without the moves that leave the monitor: the shell adds them).
    /// </summary>
    public static IServiceCollection AddDebateUiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => new DebateScreenBackend(
            provider.GetService<IDebateCoordinator>(),
            provider.GetService<IVerdictChallenges>(),
            provider.GetService<IProjectAuditService>(),
            provider.GetService<IProjectWorkspace>(),
            provider.GetService<IAgentRoster>(),
            provider.GetService<IRequirementStore>(),
            provider.GetService<IRunRegistry>(),
            provider.GetService<IVerdictStore>(),
            provider.GetService<IEvidenceStore>(),
            provider.GetService<DebateModeRequirements>()));
        services.TryAddSingleton(provider => new DebateScreens(
            provider.GetRequiredService<DebateScreenBackend>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            () => provider.GetService<IReviewNavigation>()));
        services.TryAddSingleton(provider => new RunReviewServices(
            provider.GetService<IOrchestratorAuditService>(),
            provider.GetService<IVerdictStore>(),
            provider.GetService<IVerdictChallenges>(),
            provider.GetService<IApprovalGate>(),
            provider.GetService<IRequirementStore>()));
        return services;
    }
}
