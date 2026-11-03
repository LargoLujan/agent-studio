using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Debate.Audit.Checks;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// The deterministic audit of the orchestrator (docs/15 D-33 and D-34, F5-6): seven checks of what the run left in the
/// verified decision log, plus its replay and a premature finish. It is pure (no store, no clock, no agent, no quota) and
/// writes nothing: its service records what <see cref="Audit"/> says is new.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>With a log that does not verify there is nothing else: the outcome is the verification, with no finding and
/// nothing to record (D-37, a broken chain).</item>
/// <item>The material of the run is used only as <c>RunStarted</c> anchored it: the plan by its content hash, the header by
/// <c>RunHeaderHash</c>, the requirements by theirs, the content of each piece of evidence by the SHA-256 the log anchored.
/// What does not match is a finding, and the checks that need it are left out.</item>
/// <item>The order of the findings is the order of the checks of D-33: assignment, dependencies, instructions, reviews,
/// permissions, approvals, integration, then the replay and a premature finish. One cause is one finding: a task dispatched
/// out of order is <see cref="DependencyOrderCode"/>, not also the replay the replayer refuses for it.</item>
/// <item>Nothing is asked of a run beyond what it was required to do: an integration is judged against the steps its plan
/// names and the kinds the frozen profile gives them.</item>
/// <item>A finding is recorded once: one the auditor (<see cref="Auditor"/>) already raised for the run with the same code and
/// message is not new. Its messages are made only of what the log says (ids, sequences, commits), so the same finding has
/// the same message on every pass. They are in English, like the alerts the engine raises.</item>
/// <item>A request to replan is due when a high or critical finding is new, or when there are such findings and the auditor
/// never asked for the run.</item>
/// </list>
/// </remarks>
public static class OrchestratorAudit
{
    /// <summary>The id the auditor signs its alerts and requests with (actor <see cref="ActorKind.Auditor"/>).</summary>
    public const string AuditorId = "auditor-orquestador";

    public const string AssignmentCode = "orchestrator.assignment";
    public const string DependencyOrderCode = "orchestrator.dependency-order";
    public const string InstructionsCode = "orchestrator.instructions";
    public const string ReviewOmittedCode = "orchestrator.review-omitted";
    public const string PermissionsCode = "orchestrator.permissions";
    public const string ApprovalMissingCode = "orchestrator.approval-missing";
    public const string IntegrationUnverifiedCode = "orchestrator.integration-unverified";
    public const string ReplayCode = "orchestrator.replay";
    public const string PrematureFinishCode = "orchestrator.premature-finish";

    /// <summary>The action of the approval of a plan (<c>Orchestrator.PlanApprovalAction</c>; the Debate module cannot see it).</summary>
    public const string PlanApprovalAction = "plan.approve";

    /// <summary>The action of the approval of a delivery (<c>DeliveryService.ApprovalAction</c>).</summary>
    public const string DeliveryApprovalAction = "deliver-integration";

    /// <summary>The action of the approval to discard the work of a run (<c>WorkspaceCleanup.ApprovalAction</c>).</summary>
    public const string DiscardApprovalAction = "discard-run-work";

    /// <summary>The action of the approval a review that needs a person asks for (the engine's review escalation).</summary>
    public const string ReviewEscalationAction = "orchestration.review-escalation";

    /// <summary>The format of the attempt record the engine captures at the end of each attempt (<c>AttemptRecord.RecordKind</c>).</summary>
    public const string AttemptRecordKind = "agent-studio/attempt-record";

    /// <summary>How the title of an attempt record starts (<c>AttemptRecord.TitlePrefix</c>), which version 2 of <c>EvidenceRegistered</c> anchors.</summary>
    public const string AttemptRecordTitlePrefix = "Registro del intento";

    /// <summary>Who signs the alerts and the requests of the auditor.</summary>
    public static readonly Actor Auditor = new(ActorKind.Auditor, AuditorId);

    /// <summary>The visible branch of a run, where an accepted integration is recorded (<c>IntegrationNames.BranchName</c>).</summary>
    public static string RunBranch(RunId run) => "agentstudio/run-" + run.Value;

    /// <summary>
    /// The evidence whose content the audit reads, in log order, with the SHA-256 the log anchored for it: the prompt of each
    /// executor's instructions (as <c>InstructionsIssued</c> anchored it) and each attempt record (as <c>EvidenceRegistered</c>
    /// anchored it). Its service opens them and hands them to <see cref="Audit"/>, which checks every hash itself.
    /// </summary>
    public static IReadOnlyList<AnchoredEvidence> EvidenceToRead(IReadOnlyList<DecisionEntry> log, RunId run)
    {
        ArgumentNullException.ThrowIfNull(log);
        var wanted = new List<AnchoredEvidence>();
        var seen = new HashSet<EvidenceId>();
        foreach (var item in RunLog.EntriesOf(log, run.Value).Select(AuditedRun.Parse))
        {
            var next = item.Payload switch
            {
                InstructionsIssuedPayload instructions => new AnchoredEvidence(instructions.PromptEvidenceId, instructions.PromptSha256),
                EvidenceRegisteredPayload evidence when AuditedRun.MayBeAttemptRecord(evidence) => new AnchoredEvidence(evidence.Evidence, evidence.Sha256),
                _ => null,
            };
            if (next is not null && seen.Add(next.Evidence))
            {
                wanted.Add(next);
            }
        }

        return wanted;
    }

    /// <summary>Audits one run.</summary>
    /// <exception cref="InvalidOperationException">The log verifies but has no <c>RunStarted</c> for the run: there is no run to audit.</exception>
    public static OrchestratorAuditOutcome Audit(OrchestratorAuditInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Log);
        ArgumentNullException.ThrowIfNull(input.Evidence);
        var verification = input.Log.Verification;
        if (!verification.IsValid)
        {
            return new OrchestratorAuditOutcome(input.Run, verification, [], [], null);
        }

        var run = AuditedRun.Read(input);
        var findings = new AuditFindings();
        AssignmentCheck.Run(run, findings);
        var outOfOrder = DependencyOrderCheck.Run(run, findings);
        InstructionsCheck.Run(run, findings);
        ReviewCheck.Run(run, findings);
        PermissionsCheck.Run(run, findings);
        ApprovalCheck.Run(run, findings);
        IntegrationCheck.Run(run, findings);
        ReplayCheck.Run(run, findings, outOfOrder);
        PrematureFinishCheck.Run(run, findings);

        var all = findings.All;
        var recorded = run.Items
            .Where(i => i.Kind == DecisionKind.AuditAlertRaised && i.Entry.Actor == Auditor)
            .Select(i => i.Payload)
            .OfType<AuditAlertPayload>()
            .Select(a => (a.Code, a.Message))
            .ToHashSet();
        var fresh = all.Where(f => !recorded.Contains((f.Code, f.Message))).ToList();
        var serious = all.Where(f => f.Severity >= Severity.High).ToList();
        var asked = run.Items.Any(i => i.Kind == DecisionKind.ReplanRequested && i.Entry.Actor == Auditor);
        var due = serious.Count > 0 && (fresh.Any(f => f.Severity >= Severity.High) || !asked);
        return new OrchestratorAuditOutcome(input.Run, verification, all, fresh, due ? Replan(input.Run, serious) : null);
    }

    /// <summary>The alert its service records for a finding (actor <see cref="Auditor"/>, in the run of the finding).</summary>
    public static AuditAlertPayload AlertOf(OrchestratorFinding finding, RunId run)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new AuditAlertPayload(finding.Code, finding.Tasks, finding.Message) { RunId = run.Value };
    }

    /// <summary>The request to replan: the codes of the high and critical findings, their tasks and a message; no verdict behind it.</summary>
    private static ReplanRequestedPayload Replan(RunId run, IReadOnlyList<OrchestratorFinding> serious)
    {
        var codes = serious.Select(f => f.Code).Distinct(StringComparer.Ordinal).ToList();
        var tasks = serious.SelectMany(f => f.Tasks).Distinct().ToList();
        var listed = string.Join("; ", serious.Select(f => f.Tasks.Count == 0 ? f.Code : $"{f.Code} ({string.Join(", ", f.Tasks)})"));
        return new ReplanRequestedPayload(codes, tasks, FormattableString.Invariant(
            $"The orchestrator audit of run {run} found {serious.Count} high or critical finding(s): {listed}. Replanning is a follow-up run that a person plans and approves."), null)
        {
            RunId = run.Value,
        };
    }
}
