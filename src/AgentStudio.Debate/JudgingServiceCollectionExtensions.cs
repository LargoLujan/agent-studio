using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Judging;
using AgentStudio.Debate.Judging.Debates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Debate;

public static class JudgingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the independent review of orchestration runs (docs/13, WP5): <see cref="IIndependentReview"/>, the dossier
    /// builder behind <see cref="IJudgeDossierBuilder"/> and <see cref="IJudgeDossierPackageBuilder"/> (one instance), and
    /// the factory of judges that run on the project's providers. Phase 5 (docs/15 WP2) adds the judgment of debates:
    /// <see cref="IDebateJudgment"/> and the builder of its blind dossier, <see cref="DebateDossierBuilder"/>.
    /// </summary>
    /// <remarks>
    /// It needs what other modules register: the stores (<c>IDecisionRecorder</c>, <c>IRunRegistry</c>, <c>IPlanStore</c>,
    /// <c>IRequirementStore</c>, <c>IEvidenceStore</c>, <c>IVerdictStore</c> and, for debates, <c>IDebateRegistry</c>),
    /// <c>IProjectAgentProviders</c>, a <see cref="TimeProvider"/>, and either <see cref="JudgingOptions"/> or the
    /// <see cref="OrchestrationPaths"/> of the composition root, whose <c>DossierRoot</c> is used. Options registered earlier
    /// are kept. With an <c>IWorktreeManager</c> the judges of a debate work in a copy of its commit; without one, in its
    /// package.
    /// </remarks>
    public static IServiceCollection AddAgentStudioJudging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new JudgingOptions(sp.GetRequiredService<OrchestrationPaths>().DossierRoot));
        services.TryAddSingleton<JudgeDossierBuilder>();
        services.TryAddSingleton<IJudgeDossierBuilder>(sp => sp.GetRequiredService<JudgeDossierBuilder>());
        services.TryAddSingleton<IJudgeDossierPackageBuilder>(sp => sp.GetRequiredService<JudgeDossierBuilder>());
        services.TryAddSingleton<IAgentJudgeFactory, AgentJudgeFactory>();
        services.TryAddSingleton<IIndependentReview, IndependentReview>();
        services.TryAddSingleton<DebateDossierBuilder>();
        services.TryAddSingleton<IDebateJudgment, DebateJudgment>();
        return services;
    }
}
