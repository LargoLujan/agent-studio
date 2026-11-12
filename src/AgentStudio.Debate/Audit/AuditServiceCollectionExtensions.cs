using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Challenges;
using AgentStudio.Debate.Judging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Debate.Audit;

public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the audits and the challenges of Phase 5 (docs/15 F5-5 to F5-7, ADR-0030 to ADR-0032): the auditor of the
    /// orchestrator (<see cref="IOrchestratorAuditService"/>), «Auditar proyecto completo» (<see cref="IProjectAuditService"/>,
    /// with its <see cref="ProjectDossierBuilder"/>) and the challenge of a verdict (<see cref="IVerdictChallenges"/>).
    /// </summary>
    /// <remarks>
    /// It needs what other modules register: the stores (<c>IDecisionRecorder</c>, <c>IRunRegistry</c>, <c>IPlanStore</c>,
    /// <c>IRequirementStore</c>, <c>IEvidenceStore</c>, <c>IVerdictStore</c>, <c>IAgentRoster</c>,
    /// <c>IProjectAuditRegistry</c>), the git layer (<c>IWorktreeManager</c>), the verifier (<c>IVerificationRunner</c>), the
    /// judges (<c>IAgentJudgeFactory</c>, from <c>AddAgentStudioJudging</c>), the approval gate, the project's activity
    /// (<c>IProjectActivity</c>), the coordinator of debates (<c>IDebateCoordinator</c>), a <see cref="TimeProvider"/> and,
    /// from the composition root, the trusted profile of a project (<see cref="IAuditVerificationProfiles"/>, which the
    /// desktop adapts from its <c>IVerificationProfileSource</c>). <see cref="JudgingOptions"/> and <see cref="AuditOptions"/>
    /// registered earlier are kept; otherwise they come from the <see cref="OrchestrationPaths"/> of the composition: the
    /// dossiers under its <c>DossierRoot</c>, and the copies of the audited commits in the folder <c>audits</c> beside it.
    /// </remarks>
    public static IServiceCollection AddAgentStudioAudits(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new JudgingOptions(sp.GetRequiredService<OrchestrationPaths>().DossierRoot));
        services.TryAddSingleton(sp => new AuditOptions(SnapshotRootBeside(sp.GetRequiredService<OrchestrationPaths>().DossierRoot)));
        services.TryAddSingleton<IOrchestratorAuditService, OrchestratorAuditService>();
        services.TryAddSingleton<ProjectDossierBuilder>();
        services.TryAddSingleton<IProjectAuditService, ProjectAuditService>();
        services.TryAddSingleton<IVerdictChallenges, VerdictChallengeService>();

        // The close of a milestone (Phase 9, WP3). It needs IMilestoneService and IBoardProjector (WP1) and takes every
        // IMilestoneCloseListener registered (the client documentation hooks in there); it resolves lazily, so a composition
        // without the milestones is unaffected.
        services.TryAddSingleton<IMilestoneCloser, MilestoneCloser>();
        return services;
    }

    private static string SnapshotRootBeside(string dossierRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dossierRoot));
        return Path.Combine(Path.GetDirectoryName(full) ?? full, "audits");
    }
}
