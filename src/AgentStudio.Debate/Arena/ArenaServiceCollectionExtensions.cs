using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Judging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Debate.Arena;

public static class ArenaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the arena of debates (docs/15 WP1): <see cref="IDebateCoordinator"/> (one <see cref="DebateCoordinator"/>, which
    /// knows the debates going on in this process) over the ports of Application.
    /// </summary>
    /// <remarks>
    /// It needs what other packages register: the stores (<c>IDecisionRecorder</c>, <c>IEvidenceStore</c>, <c>IVerdictStore</c>,
    /// <c>IRequirementStore</c>, <c>IRunRegistry</c>, <c>IDebateRegistry</c>, <c>IAgentRoster</c>), <c>IProjectAgentProviders</c>,
    /// <c>IWorktreeManager</c>, <c>IApprovalGate</c>, the one <c>IProjectActivity</c> of the process, the judgment of the debates
    /// (<c>IDebateJudgment</c>), <c>ISecretRedactor</c> and a <see cref="TimeProvider"/>. The dossier package of a run, the
    /// audits, the verifier and the trusted profiles are used when they are registered. Without <see cref="DebateArenaOptions"/>
    /// the debates live next to the worktrees of <see cref="OrchestrationPaths"/>, in <c>…\AgentStudio\debates</c>. Options
    /// and ports registered earlier are kept.
    /// </remarks>
    public static IServiceCollection AddAgentStudioDebateArena(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var worktrees = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sp.GetRequiredService<OrchestrationPaths>().WorktreeRoot));
            var root = Path.GetDirectoryName(worktrees) ?? worktrees;
            return new DebateArenaOptions(Path.Combine(root, "debates"));
        });
        services.TryAddSingleton(sp => new DebateArenaPorts(
            sp.GetRequiredService<IDecisionRecorder>(),
            sp.GetRequiredService<IEvidenceStore>(),
            sp.GetRequiredService<IVerdictStore>(),
            sp.GetRequiredService<IRequirementStore>(),
            sp.GetRequiredService<IRunRegistry>(),
            sp.GetRequiredService<IDebateRegistry>(),
            sp.GetRequiredService<IAgentRoster>(),
            sp.GetRequiredService<IProjectAgentProviders>(),
            sp.GetRequiredService<IWorktreeManager>(),
            sp.GetRequiredService<IApprovalGate>(),
            sp.GetRequiredService<IProjectActivity>(),
            sp.GetRequiredService<IDebateJudgment>(),
            sp.GetRequiredService<ISecretRedactor>(),
            sp.GetRequiredService<DebateArenaOptions>(),
            sp.GetRequiredService<TimeProvider>())
        {
            Dossiers = sp.GetService<IJudgeDossierPackageBuilder>(),
            Audits = sp.GetService<IProjectAuditRegistry>(),
            Verification = sp.GetService<IVerificationRunner>(),
            TestProfiles = sp.GetService<IDebateTestProfiles>(),
        });
        services.TryAddSingleton<DebateCoordinator>();
        services.TryAddSingleton<IDebateCoordinator>(sp => sp.GetRequiredService<DebateCoordinator>());
        return services;
    }
}
