using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// The auditor of the orchestrator (docs/15 D-33 to D-35, ADR-0031): it reads one run from the stores, audits it with
/// <see cref="OrchestratorAudit"/> and appends what is new. Free (no agent, no quota) and idempotent.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The log is verified first (the chain and the anchor outside the project); when it does not verify nothing is
/// written and the report is that verification.</item>
/// <item>The plan and the requirements are the ones <c>RunStarted</c> anchored by hash, the header the stored one; the audit
/// trusts each only when it matches its anchor. The prompts and the attempt records it needs are opened from the store and
/// hashed by the audit against the log; one the store cannot open is left out, and the audit says so.</item>
/// <item>Each new finding is an <c>AuditAlertRaised</c> of the run, signed by <see cref="OrchestratorAudit.Auditor"/>; a
/// request to replan that is due is a <c>ReplanRequested</c> (<see cref="ReplanRequestedPayload"/>) signed by it too. A
/// finding already recorded with the same code and message is not repeated, and the passes over one project in this process
/// go one at a time, so two passes at once never record the same finding twice.</item>
/// <item><see cref="OrchestratorAuditReport.ReplanRequested"/> says whether the run has a request of the auditor once the
/// pass is over: recorded now or by an earlier pass. <see cref="OrchestratorAuditReport.Recorded"/> are the entries of this
/// pass only.</item>
/// <item>Nothing is corrected, removed or rewritten: the log is append-only.</item>
/// </list>
/// </remarks>
public sealed class OrchestratorAuditService(
    IDecisionRecorder decisions,
    IRunRegistry runs,
    IPlanStore plans,
    IRequirementStore requirements,
    IEvidenceStore evidence) : IOrchestratorAuditService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projects = new(StringComparer.Ordinal);

    /// <exception cref="InvalidOperationException">The log verifies but has no <c>RunStarted</c> for the run.</exception>
    public async Task<OrchestratorAuditReport> AuditRunAsync(ProjectContext project, RunId run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var projectId = project.ProjectId;
        var gate = _projects.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var log = await decisions.LoadAsync(projectId, ct).ConfigureAwait(false);
            if (!log.Verification.IsValid)
            {
                return new OrchestratorAuditReport(run, log.Verification, [], [], ReplanRequested: false);
            }

            var entries = RunLog.EntriesOf(log.Entries, run.Value);
            var started = RunLog.RunStarted(entries)
                ?? throw new InvalidOperationException($"The decision log of project '{projectId}' has no RunStarted for run '{run}': there is no run to audit.");
            var header = await runs.FindAsync(projectId, run, ct).ConfigureAwait(false);
            var plan = await plans.FindAsync(projectId, started.PlanHash, ct).ConfigureAwait(false);
            var frozen = await requirements.FindAsync(projectId, started.RequirementSetHash, ct).ConfigureAwait(false);
            var contents = await ReadAsync(projectId, OrchestratorAudit.EvidenceToRead(log.Entries, run), ct).ConfigureAwait(false);
            var outcome = OrchestratorAudit.Audit(new OrchestratorAuditInput(run, log, header, plan, frozen, contents));

            var recorded = new List<long>();
            foreach (var finding in outcome.NewFindings)
            {
                var alert = await decisions.RecordAsync(
                    projectId,
                    OrchestratorAudit.Auditor,
                    DecisionKind.AuditAlertRaised,
                    $"Hallazgo del auditor del orquestador: {finding.Code}",
                    OrchestratorAudit.AlertOf(finding, run),
                    cancellationToken: ct).ConfigureAwait(false);
                recorded.Add(alert.Sequence);
            }

            if (outcome.Replan is { } replan)
            {
                var request = await decisions.RecordAsync(
                    projectId,
                    OrchestratorAudit.Auditor,
                    DecisionKind.ReplanRequested,
                    $"El auditor del orquestador pide replanificar la ejecución {run}",
                    replan,
                    cancellationToken: ct).ConfigureAwait(false);
                recorded.Add(request.Sequence);
            }

            var asked = outcome.Replan is not null
                || entries.Any(e => e.Kind == DecisionKind.ReplanRequested && e.Actor == OrchestratorAudit.Auditor);
            return new OrchestratorAuditReport(run, outcome.Log, outcome.Findings, recorded, asked);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The content of each evidence as the store returns it; one it cannot open is left out (the audit reports it).</summary>
    private async Task<Dictionary<EvidenceId, ReadOnlyMemory<byte>>> ReadAsync(string projectId, IReadOnlyList<AnchoredEvidence> wanted, CancellationToken ct)
    {
        var contents = new Dictionary<EvidenceId, ReadOnlyMemory<byte>>();
        foreach (var item in wanted)
        {
            try
            {
                var stream = await evidence.OpenContentAsync(projectId, item.Evidence, ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                    contents[item.Evidence] = buffer.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException or KeyNotFoundException or InvalidDataException or UnauthorizedAccessException)
            {
                // Left out: the audit says it cannot be read against its anchored hash.
            }
        }

        return contents;
    }
}
