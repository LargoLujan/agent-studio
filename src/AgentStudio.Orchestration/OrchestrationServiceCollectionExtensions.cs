using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Planning;
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
    /// <para>Phase 13 (docs/17 D-20 and D-44): «Revisar el orden» (<see cref="IOrderReview"/>) and the chat of preparation
    /// (<see cref="IPreparationChat"/>), single for the app, and the one register of the OKs to spend quota
    /// (<see cref="IQuotaConsentLedger"/>) they share with the recommendation of skills and the analysis of consumption with an
    /// agent, which take it when it is registered.</para>
    /// </remarks>
    public static IServiceCollection AddAgentStudioOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OrchestrationActivity>();
        services.TryAddSingleton<IProjectActivity>(provider => provider.GetRequiredService<OrchestrationActivity>());
        services.TryAddSingleton<IRunActivity>(provider => provider.GetRequiredService<IProjectActivity>());
        services.TryAddSingleton<IOrchestrator, Orchestrator>();
        services.TryAddSingleton<IQuotaConsentLedger, QuotaConsentLedger>();
        services.TryAddSingleton<IOrderReview, OrderReviewService>();
        services.TryAddSingleton<IPreparationChat, PreparationChatService>();
        return services;
    }
}
