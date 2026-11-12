using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Orchestration;

public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the orchestrator (planner and engine) behind <see cref="IOrchestrator"/>. It only depends on Application
    /// ports, which other modules register: the decision recorder and the stores (persistence), the composed agent
    /// providers (providers), the worktree manager (git), the verification runner and the integrator, the independent
    /// review (debate), the approval gate (security) and the skill provisioner, plus <see cref="OrchestrationPaths"/>,
    /// which the composition root decides. The context packer of the memory is used when one is registered.
    /// </summary>
    /// <remarks>
    /// It also registers the one activity with agents of the process (docs/15 D-05): <see cref="OrchestrationActivity"/>, as
    /// the <see cref="IProjectActivity"/> the orchestrator, the coordinator of debates and the audits enter, and as the
    /// <see cref="IRunActivity"/> the memory waits on (docs/14 D-19 and D-22). It is never the orchestrator itself, which
    /// needs the context packer, which needs the memory, which needs the activity. An <see cref="IProjectActivity"/>
    /// registered earlier is kept, and then it is also the <see cref="IRunActivity"/>, so every part of the app shares it.
    /// </remarks>
    public static IServiceCollection AddAgentStudioOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OrchestrationActivity>();
        services.TryAddSingleton<IProjectActivity>(provider => provider.GetRequiredService<OrchestrationActivity>());
        services.TryAddSingleton<IRunActivity>(provider => provider.GetRequiredService<IProjectActivity>());
        services.TryAddSingleton<IOrchestrator, Orchestrator>();

        // Phase 9 (docs/16 D-17 to D-19 and D-29 to D-30): milestones, backlog, trace links, suggestions and the board. The suggester also needs
        // an IRepositoryHistoryReader (the hardened git layer), which the composition root registers.
        // Built by factories, so a container that only resolves the orchestrator does not need the stores and the git reader these use.
        services.TryAddSingleton(provider => new WorkWriteGate(provider.GetRequiredService<IRunActivity>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IMilestoneService>(provider => new MilestoneService(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IMilestoneRegistry>(),
            provider.GetRequiredService<IRequirementStore>(),
            provider.GetRequiredService<IEvidenceStore>(),
            provider.GetRequiredService<WorkWriteGate>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IBacklogService>(provider => new BacklogService(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IBacklogStore>(),
            provider.GetRequiredService<WorkWriteGate>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ITraceLinkService>(provider => new TraceLinkService(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<WorkWriteGate>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(provider => new TraceSuggester(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IRepositoryHistoryReader>()));
        services.TryAddSingleton<ITraceSuggester>(provider => provider.GetRequiredService<TraceSuggester>());
        services.TryAddSingleton<ITraceChainReader>(provider => new TraceChainReader(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IRequirementStore>(),
            provider.GetRequiredService<IPlanStore>(),
            provider.GetRequiredService<IVerdictStore>()));
        services.TryAddSingleton<IParticipationProjector>(provider => new ParticipationProjector(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IVerdictStore>(),
            provider.GetRequiredService<IPlanStore>()));
        services.TryAddSingleton<IRepositoryAnnotator>(provider => new RepositoryAnnotator(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<TraceSuggester>()));
        services.TryAddSingleton<IRepositoryActivityReader>(provider => new RepositoryActivityReader(provider.GetRequiredService<IDecisionRecorder>()));
        services.TryAddSingleton<IBoardProjector>(provider => new BoardProjector(
            provider.GetRequiredService<IDecisionRecorder>(),
            provider.GetRequiredService<IMilestoneService>(),
            provider.GetRequiredService<IBacklogService>(),
            provider.GetRequiredService<IRequirementStore>(),
            provider.GetRequiredService<IRunRegistry>(),
            provider.GetRequiredService<IPlanStore>(),
            provider.GetRequiredService<IVerdictStore>()));
        return services;
    }
}
