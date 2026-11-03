using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// Everything the planner and the engine reach the world through: only Application ports (ADR-0002).
/// <see cref="ContextPacker"/> packs the project memory of an agent with <c>ReadSelected</c> (docs/14 D-24); without one,
/// no agent gets memory.
/// </summary>
internal sealed record EnginePorts(
    IDecisionRecorder Decisions,
    IEvidenceStore Evidence,
    IPlanStore Plans,
    IRunRegistry Runs,
    IRequirementStore Requirements,
    IAgentRoster Roster,
    IProjectAgentProviders Providers,
    IWorktreeManager Worktrees,
    IVerificationRunner Verification,
    IIntegrator Integrator,
    IIndependentReview Review,
    IApprovalGate Approvals,
    ISkillProvisioner Skills,
    OrchestrationPaths Paths,
    TimeProvider Time,
    IWriteConfinementProbe? WriteConfinement = null,
    IContextPacker? ContextPacker = null)
{
    /// <summary>
    /// The session probes of the CLIs (<c>claude auth status</c>, <c>codex login status</c>), which the check before going on
    /// after a wait asks (docs/16 D-38, F8-3); with none for a provider, its session is not checked.
    /// </summary>
    public IReadOnlyList<IProviderProbe> Probes { get; init; } = [];

    /// <summary>
    /// The quota readers of the providers. The check before going on reads only Codex's, the app-server's, which launches
    /// nothing without the user's opt-in (ADR-0020); none is a turn of the model.
    /// </summary>
    public IReadOnlyList<IProviderQuotaReader> QuotaReaders { get; init; } = [];

    /// <summary>
    /// The power events of the app (docs/16 D-40, WP3): a run listens while it lasts, and a resume tells its clock how long the
    /// machine slept, which it leaves out of the silence of its attempts. Null: only the clock finds the sleep.
    /// </summary>
    public IPowerEvents? Power { get; init; }

    /// <summary>The composed provider (journal and concurrency limit included) of a CLI for a project, or null.</summary>
    public IAgentProvider? ProviderFor(string projectRoot, Domain.Providers.ProviderId provider) =>
        Providers.For(projectRoot).FirstOrDefault(p => p.Provider == provider);

    /// <summary>
    /// Whether the installed Claude Code keeps the writes of an executor without a shell in its worktree (ADR-0018,
    /// «Medición»), asked once when a run starts or resumes. Without a probe nobody can say, so it does not (fail closed).
    /// </summary>
    public async Task<bool> ClaudeWritesConfinedAsync(CancellationToken cancellationToken) =>
        WriteConfinement is { } probe
        && await probe.IsWriteConfinedAsync(Domain.Providers.ProviderId.ClaudeCode, cancellationToken).ConfigureAwait(false);
}
