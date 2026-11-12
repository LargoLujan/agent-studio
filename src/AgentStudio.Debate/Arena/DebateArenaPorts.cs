using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Prompts;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Judging;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// The trusted verification profile of a project, for «Ejecutar pruebas» (docs/15 D-27). The profile is read by the
/// composition root (it lives in the engine, which this module cannot see, ADR-0002); without one, the room says the tests
/// cannot be run (<see cref="DebateSnapshot.CanRunTests"/> false), never pretends to.
/// </summary>
public interface IDebateTestProfiles
{
    /// <summary>The trusted profile of the project, or null when it has none.</summary>
    Task<VerificationProfile?> FindAsync(ProjectContext project, CancellationToken cancellationToken);
}

/// <summary>What the coordinator and its debates work with, through the ports of Application (ADR-0002).</summary>
public sealed record DebateArenaPorts(
    IDecisionRecorder Decisions,
    IEvidenceStore Evidence,
    IVerdictStore Verdicts,
    IRequirementStore Requirements,
    IRunRegistry Runs,
    IDebateRegistry Debates,
    IAgentRoster Roster,
    IProjectAgentProviders Providers,
    IWorktreeManager Worktrees,
    IApprovalGate Approvals,
    IProjectActivity Activity,
    IDebateJudgment Judgment,
    ISecretRedactor Redactor,
    DebateArenaOptions Options,
    TimeProvider Time)
{
    /// <summary>The dossier package of a run, the material of a debate about a run (D-08); null leaves it out.</summary>
    public IJudgeDossierPackageBuilder? Dossiers { get; init; }

    /// <summary>The headers of the audits, for the challenge of the verdict of an audit; null when there are none.</summary>
    public IProjectAuditRegistry? Audits { get; init; }

    /// <summary>The isolated verifier of «Ejecutar pruebas»; null when it is not composed.</summary>
    public IVerificationRunner? Verification { get; init; }

    /// <summary>The trusted profiles of «Ejecutar pruebas»; null when they are not composed.</summary>
    public IDebateTestProfiles? TestProfiles { get; init; }

    /// <summary>V44 (docs/17 D-48): the language the person chose, which reaches the prompt of every participant as a block of data; null is the default one.</summary>
    public IResponseLanguage? Language { get; init; }
}
