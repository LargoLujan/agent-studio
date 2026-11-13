using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Security;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Tests.Debate.Audit.Milestones;

/// <summary>
/// The builder of the report of the close of a milestone as Phase 9 left it, to compare byte for byte with the one of today on a log with no remote (docs/21 D-29, F10-8). It is the
/// class <c>MilestoneReportBuilder</c> of <c>src/AgentStudio.Debate/Audit/MilestoneReport.cs</c> at commit 1eb2c56a (<c>git show 1eb2c56a:src/AgentStudio.Debate/Audit/MilestoneReport.cs</c>,
/// lines 162 to 371) copied as it was, with only these changes: the name, the namespace, and <c>RunItem</c>/<c>AuditedRun.Parse</c> (internal to the assembly of Debate) written again here
/// with the same body. It builds the current types of the report, whose new members are optional and omitted when empty, so what it gives is what Phase 9 gave.
/// </summary>
internal static class Phase9MilestoneReportBaseline
{
    private sealed record RunItem(DecisionEntry Entry, DecisionPayload? Payload, bool Unreadable)
    {
        public long Sequence => Entry.Sequence;

        public DecisionKind Kind => Entry.Kind;
    }

    private static RunItem Parse(DecisionEntry entry)
    {
        try
        {
            return new RunItem(entry, DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson), Unreadable: false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new RunItem(entry, null, Unreadable: true);
        }
    }

    private static readonly string[] NotIncludedSections =
    [
        "Consumo con su desglose y estimación frente a lo real (D-34 y D-20): los da la observabilidad de la Fase 9, no este informe.",
        "Despliegue (D-50): Agent Studio no despliega nada y ninguna plataforma lo ha informado; sin ese dato el estado no aparece.",
    ];

    private static string Clean(string text) => LocalPaths.Remove(text, "[ruta local]").Text;

    public static MilestoneReport Build(MilestoneReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var milestone = input.Milestone;
        var items = input.Log.OrderBy(e => e.Sequence).Select(Parse).ToList();
        var head = input.Log.Count == 0 ? DecisionLog.GenesisHash : input.Log[^1].Hash;
        var linked = milestone.Targets.Where(t => t.Kind == MilestoneTargetKind.Run).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var challenged = items.Select(i => i.Payload).OfType<VerdictChallengedPayload>().Select(c => c.Verdict).ToHashSet(StringComparer.Ordinal);

        var runs = input.Scope.Runs.Order(StringComparer.Ordinal).Select(run => RunOf(run, linked.Contains(run), items, challenged)).ToList();
        var debates = input.Scope.Debates.Order(StringComparer.Ordinal).Select(id => DebateOf(id, items)).ToList();
        var audit = AuditOf(input.Audit, items);
        var requirements = RequirementsOf(input);
        var units = input.Scope.Runs.Concat(input.Scope.Debates).Append(input.Audit.Audit.Value).ToHashSet(StringComparer.Ordinal);
        var pending = PendingProblems.From(input.Log).Where(p => units.Contains(p.Source)).Select(p => new MilestoneReportPending(p.Code, p.Source, Clean(p.Message))).ToList();

        var sets = milestone.RequirementSetHistory.Select((hash, index) => new MilestoneReportRequirementSet(index + 1, hash, index == milestone.RequirementSetHistory.Count - 1)).ToList();
        var earlier = milestone.Reports.OrderBy(r => r.Revision).Select(r => new MilestoneReportEarlier(r.Revision, r.Audit, r.Report.Value, r.ReportSha256, r.IntegratedCommit, r.At)).ToList();
        var anchor = new MilestoneReportAnchor(input.Log.Count, head, input.Audit.HeadCommit, input.Audit.UncommittedChanges, input.Board?.LogEntries ?? 0, input.Board?.LogHeadHash ?? string.Empty);

        return new MilestoneReport(
            MilestoneReport.CurrentFormat,
            milestone.Reports.Count + 1,
            new MilestoneReportHeader(milestone.Header.Id.Value, milestone.Header.Name, milestone.Header.Objective, milestone.Header.CreatedAt),
            anchor,
            sets,
            requirements,
            runs,
            debates,
            audit,
            Deviations(input, requirements, runs, audit),
            pending,
            earlier,
            NotIncludedSections);
    }

    private static List<MilestoneReportRequirement> RequirementsOf(MilestoneReportInput input)
    {
        var columns = (input.Board?.Requirements ?? [])
            .Where(c => c.Kind == BoardCardKind.Requirement)
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Column, StringComparer.Ordinal);
        var assessments = input.Audit.Verdict.Assessments
            .GroupBy(a => a.Requirement.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Status, StringComparer.Ordinal);
        var result = new List<MilestoneReportRequirement>();
        foreach (var requirement in input.Requirements.Requirements.OrderBy(r => r.Id.Value, StringComparer.Ordinal))
        {
            BoardColumn? column = columns.TryGetValue(requirement.Id.Value, out var found) ? found : null;
            AssessmentStatus? status = assessments.TryGetValue(requirement.Id.Value, out var assessed) ? assessed : null;
            result.Add(new MilestoneReportRequirement(
                requirement.Id.Value,
                requirement.Priority.ToString(),
                requirement.Statement,
                column?.ToString(),
                status?.ToString() ?? "NotAssessed",
                Implemented: input.Audit.IsTrustworthy && status == AssessmentStatus.Satisfied,
                Reviewed: column is BoardColumn.Done or BoardColumn.Integrated,
                Integrated: column is BoardColumn.Integrated));
        }

        return result;
    }

    private static MilestoneReportRun RunOf(string run, bool linkedDirectly, List<RunItem> items, HashSet<string> challenged)
    {
        var started = items.Where(i => i.Payload is RunStartedPayload).FirstOrDefault(i => i.Payload!.RunId == run);
        var startedPayload = started?.Payload as RunStartedPayload;
        var finished = items.Select(i => i.Payload).OfType<RunFinishedPayload>().LastOrDefault(p => p.RunId == run);
        var deliveries = items.Select(i => i.Payload).OfType<IntegrationPerformedPayload>()
            .Where(p => p.RunId == run && p.IsDelivery && !string.IsNullOrEmpty(p.ResultCommit))
            .Select(p => p.ResultCommit!)
            .ToList();
        var verdicts = items.Select(i => i.Payload).OfType<VerdictIssuedPayload>().Where(v => v.RunId == run)
            .Select(v => new MilestoneReportVerdict(v.VerdictId, v.Task?.Value, v.Decision.ToString(), v.Valid, challenged.Contains(v.VerdictId)))
            .ToList();
        var alerts = items.Select(i => i.Payload).OfType<AuditAlertPayload>().Where(a => a.RunId == run).Select(a => new MilestoneReportAlert(a.Code, Clean(a.Message))).ToList();
        var agents = (startedPayload?.Roster ?? [])
            .OrderBy(r => r.Agent.Value, StringComparer.Ordinal)
            .Select(r => new MilestoneReportAgent(r.Agent.Value, r.Role.ToString(), r.Provider.Value, r.Vendor.Value))
            .ToList();
        return new MilestoneReportRun(
            run,
            linkedDirectly,
            started?.Entry.RecordedAt,
            startedPayload?.Follows,
            startedPayload?.BaseCommit,
            finished?.Phase.ToString(),
            finished?.IntegrationCommit,
            deliveries,
            agents,
            verdicts,
            alerts);
    }

    private static MilestoneReportDebate DebateOf(string id, List<RunItem> items)
    {
        var opened = items.Select(i => i.Payload).OfType<DebateOpenedPayload>().FirstOrDefault(d => d.RunId == id);
        var closed = items.Select(i => i.Payload).OfType<DebateClosedPayload>().LastOrDefault(c => c.RunId == id);
        return new MilestoneReportDebate(id, opened?.Mode.ToString(), closed?.Outcome.ToString() ?? "Open", closed?.Rule);
    }

    private static MilestoneReportAudit AuditOf(AuditReport audit, List<RunItem> items)
    {
        var judge = audit.Verdict.Judge;
        var alerts = items.Select(i => i.Payload).OfType<AuditAlertPayload>().Where(a => a.RunId == audit.Audit.Value).Select(a => new MilestoneReportAlert(a.Code, Clean(a.Message))).ToList();
        var verification = audit.Verification.OrderBy(e => e.Id.Value, StringComparer.Ordinal).Select(e => new MilestoneReportEvidence(
            e.Id.Value,
            e.Kind.ToString(),
            Clean(e.Title),
            e.SubjectCommit,
            e.TestRun?.Passed,
            e.TestRun?.Failed,
            e.TestRun?.Skipped,
            e.TestRun?.ExitCode)).ToList();
        return new MilestoneReportAudit(
            audit.Audit.Value,
            audit.VerdictId,
            audit.Verdict.Decision.ToString(),
            audit.Disposition.ToString(),
            judge.Agent.Value,
            judge.Model.Vendor.Value,
            judge.Model.Model,
            audit.IsTrustworthy,
            audit.Corrected,
            audit.DecisionLogIntact,
            audit.ValidationIssues.Count,
            verification,
            alerts);
    }

    private static List<MilestoneReportDeviation> Deviations(MilestoneReportInput input, List<MilestoneReportRequirement> requirements, List<MilestoneReportRun> runs, MilestoneReportAudit audit)
    {
        var deviations = new List<MilestoneReportDeviation>();
        if (!audit.Trustworthy)
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.AuditNotTrustworthy, audit.Id, "El veredicto de la auditoría no es de fiar (no lo dio un juez, no pasó la validación o el registro no estaba íntegro): ningún requisito cuenta como implementado."));
        }

        if (!string.Equals(audit.Disposition, nameof(ReviewDisposition.Approved), StringComparison.Ordinal))
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.AuditNotApproved, audit.Id, $"La auditoría no terminó aprobada (disposición {audit.Disposition}): una persona decide qué hacer con lo que señala."));
        }

        if (input.Audit.UncommittedChanges)
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.UncommittedChanges, audit.Id, "Había cambios sin confirmar en el repositorio: no entran en lo auditado."));
        }

        foreach (var requirement in requirements)
        {
            var code = requirement.Assessment switch
            {
                nameof(AssessmentStatus.NotSatisfied) => MilestoneReport.RequirementNotSatisfied,
                nameof(AssessmentStatus.NotVerifiable) => MilestoneReport.RequirementNotVerifiable,
                nameof(AssessmentStatus.Satisfied) => null,
                _ => MilestoneReport.RequirementNotAssessed,
            };
            if (code is not null)
            {
                deviations.Add(new MilestoneReportDeviation(code, requirement.Id, $"El requisito {requirement.Id} no consta como satisfecho en la auditoría ({requirement.Assessment})."));
            }

            if (string.Equals(requirement.Column, nameof(BoardColumn.Backlog), StringComparison.Ordinal))
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RequirementUncovered, requirement.Id, $"Ninguna tarea de las ejecuciones del hito cubre el requisito {requirement.Id}."));
            }
            else if (!requirement.Reviewed)
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RequirementNotReviewed, requirement.Id, $"Ningún veredicto aprobado y válido da por satisfecho el requisito {requirement.Id} (columna {requirement.Column ?? "desconocida"})."));
            }
            else if (!requirement.Integrated)
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RequirementNotIntegrated, requirement.Id, $"El requisito {requirement.Id} está revisado pero su ejecución no se entregó a tu rama."));
            }
        }

        foreach (var run in runs)
        {
            foreach (var alert in run.Alerts)
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.RunAlert, run.Id, $"Alerta {alert.Code} en la ejecución {run.Id}: {alert.Message}"));
            }

            foreach (var verdict in run.Verdicts.Where(v => v.Challenged))
            {
                deviations.Add(new MilestoneReportDeviation(MilestoneReport.VerdictChallenged, verdict.Id, $"El veredicto {verdict.Id} de la ejecución {run.Id} está impugnado."));
            }
        }

        foreach (var alert in audit.Alerts)
        {
            deviations.Add(new MilestoneReportDeviation(MilestoneReport.AuditAlert, audit.Id, $"Alerta {alert.Code} en la auditoría: {alert.Message}"));
        }

        return deviations;
    }
}
