using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// What <see cref="OrchestratorAudit"/> examines of one run, as <see cref="OrchestratorAuditService"/> reads it: the project's
/// log with its verification (the chain and the anchor outside the project), the stored header, plan and requirements (the
/// audit checks each against what <c>RunStarted</c> anchored, so a null or a rewritten one is a finding, never trusted), and
/// the content of the evidence <see cref="OrchestratorAudit.EvidenceToRead"/> names, as the store returned it (the audit
/// hashes it against the log; a missing id is content that could not be opened).
/// </summary>
public sealed record OrchestratorAuditInput(
    RunId Run,
    DecisionLogState Log,
    RunRecord? Header,
    PlanDefinition? Plan,
    FrozenRequirementSet? Requirements,
    IReadOnlyDictionary<EvidenceId, ReadOnlyMemory<byte>> Evidence);

/// <summary>
/// One pass of the audit over a run, before anything is written: the findings, those the auditor had not recorded for the
/// run yet (same code and message) and the request to replan that is due, or null. Its service records an
/// <c>AuditAlertRaised</c> for each new finding and the <c>ReplanRequested</c>, and reports them as an
/// <see cref="OrchestratorAuditReport"/>. With a log that does not verify there is nothing else: no finding, nothing to record.
/// </summary>
/// <param name="Findings">Every finding of this pass, in the order of the checks of D-33.</param>
/// <param name="NewFindings">The findings the auditor had not recorded for this run yet.</param>
/// <param name="Replan">The request to replan to record (its <c>Findings</c> are codes), or null: none is due.</param>
public sealed record OrchestratorAuditOutcome(
    RunId Run,
    DecisionLogVerification Log,
    IReadOnlyList<OrchestratorFinding> Findings,
    IReadOnlyList<OrchestratorFinding> NewFindings,
    ReplanRequestedPayload? Replan)
{
    /// <summary>Whether the pass leaves anything to record.</summary>
    public bool HasSomethingToRecord => NewFindings.Count > 0 || Replan is not null;
}

/// <summary>A piece of evidence whose content the audit needs, with the SHA-256 the log anchored for it.</summary>
public sealed record AnchoredEvidence(EvidenceId Evidence, string Sha256);
