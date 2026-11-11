using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Debate.Audit;

/// <summary>
/// Closes a milestone by auditing it (docs/16 D-49, F9-10, ADR-0038), in this order: refuses what cannot close before any quota is
/// spent (not found, with problems, closed, no run linked); prepares the audit narrowed to the milestone (its frozen set, its runs
/// with their follow-ups and its debates) and runs it with the judge and the verification of «Auditar proyecto completo»; checks that
/// the audit really ran (an audit unit that finished is in the log); builds the deterministic report (<see cref="MilestoneReport"/>),
/// keeps it as evidence in the unit of the milestone and records <c>MilestoneClosed</c> with its id and SHA-256; and tells the listeners
/// (<see cref="IMilestoneCloseListener"/>, the point where the client documentation hooks in).
/// </summary>
/// <remarks>
/// An audit that ran closes the milestone whatever its disposition: the report states the deviations and reopening exists for them
/// (D-50). The audit releases the project when it ends and the close is written afterwards, without holding the project again.
/// </remarks>
public sealed class MilestoneCloser(
    IMilestoneService milestones,
    IProjectAuditService audits,
    IAgentRoster roster,
    IDecisionRecorder decisions,
    IRequirementStore requirements,
    IEvidenceStore evidence,
    IBoardProjector board,
    IEnumerable<IMilestoneCloseListener>? listeners = null) : IMilestoneCloser
{
    private static readonly Actor SystemActor = new(ActorKind.System, IndependentReview.SystemId);

    public async Task<MilestoneView> CloseAsync(CloseMilestoneRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = request.Project;
        var projectId = project.ProjectId;

        var view = await RequireClosableAsync(request, cancellationToken).ConfigureAwait(false);
        var log = await decisions.LoadAsync(projectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new MilestoneCloseRefusedException("log.not-intact", ["El registro de decisiones no está íntegro: no se cierra un hito sobre un registro que no verifica."]);
        }

        var scope = ScopeOf(view, log.Entries);
        var proposal = await PrepareAsync(request, view, scope, cancellationToken).ConfigureAwait(false);
        var report = await audits.AuditAsync(proposal, progress, cancellationToken).ConfigureAwait(false);

        // Nothing is closed on an audit that did not run (approval denied, log broken, nobody to judge): it would name an audit that is not in the log.
        log = await decisions.LoadAsync(projectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new MilestoneCloseRefusedException("log.not-intact", ["El registro de decisiones dejó de verificar durante la auditoría: no se cierra el hito."], report);
        }

        if (!RunLog.EntriesOf(log.Entries, report.Audit.Value).Any(e => e.Kind == DecisionKind.ProjectAuditFinished))
        {
            throw new MilestoneCloseRefusedException("milestone.audit-not-run", [report.Verdict.Summary], report);
        }

        // The milestone as it is now: the audit took time, and the close is the revision after the reports it has.
        view = await RequireClosableAsync(request, cancellationToken).ConfigureAwait(false);
        var frozen = await requirements.FindAsync(projectId, view.CurrentRequirementSetHash, cancellationToken).ConfigureAwait(false)
            ?? throw new MilestoneCloseRefusedException("milestone.requirements", [$"El proyecto no tiene el conjunto de requisitos congelado {view.CurrentRequirementSetHash}."], report);
        var projected = await board.ProjectAsync(project, request.Milestone, cancellationToken).ConfigureAwait(false);
        var content = MilestoneReportBuilder.Build(new MilestoneReportInput(view, frozen, log.Entries, scope, report, projected));

        var record = await CaptureAsync(projectId, request.Milestone, content, cancellationToken).ConfigureAwait(false);
        await decisions.RecordAsync(
            projectId,
            SystemActor,
            DecisionKind.EvidenceRegistered,
            $"Evidencia {record.Id}: {record.Title}",
            EvidenceRegisteredPayload.Anchoring(record, request.Milestone.Value, null, null),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await decisions.RecordAsync(
            projectId,
            SystemActor,
            DecisionKind.MilestoneClosed,
            $"Se cierra el hito {request.Milestone.Value} (informe {content.Revision})",
            MilestoneClosedPayload.Closing(request.Milestone, content.Revision, report.Audit.Value, record.Id, record.ContentSha256, report.HeadCommit, request.ClosedBy),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var closed = await milestones.GetAsync(project, request.Milestone, cancellationToken).ConfigureAwait(false);
        if (closed is null || closed.Status != MilestoneStatus.Closed || closed.Problems.Count > 0 || closed.Reports.Count == 0)
        {
            throw new InvalidOperationException(
                $"The close of milestone '{request.Milestone}' was recorded but it does not replay as closed: {string.Join(" ", closed?.Problems ?? ["it was not found"])}");
        }

        await NotifyAsync(new MilestoneClosedNotice(project, closed, closed.Reports[^1], report), cancellationToken).ConfigureAwait(false);
        return closed;
    }

    private async Task<MilestoneView> RequireClosableAsync(CloseMilestoneRequest request, CancellationToken ct)
    {
        var view = await milestones.GetAsync(request.Project, request.Milestone, ct).ConfigureAwait(false)
            ?? throw new MilestoneCloseRefusedException("milestone.not-found", [$"El hito {request.Milestone.Value} no existe en este proyecto."]);
        if (view.Problems.Count > 0)
        {
            throw new MilestoneCloseRefusedException("milestone.problems", ["El registro del hito no describe un hito válido: una persona debe revisarlo antes de cerrarlo.", .. view.Problems]);
        }

        if (view.Status != MilestoneStatus.Open)
        {
            throw new MilestoneCloseRefusedException("milestone.not-open", ["El hito ya está cerrado: solo se puede reabrir."]);
        }

        if (!view.Targets.Any(t => t.Kind == MilestoneTargetKind.Run))
        {
            var debates = view.Targets.Any(t => t.Kind == MilestoneTargetKind.Debate);
            throw new MilestoneCloseRefusedException(
                "milestone.nothing-to-close",
                [debates
                    ? "El hito solo tiene debates vinculados: cerrar es auditar el trabajo de sus ejecuciones, y no tiene ninguna."
                    : "El hito no tiene ninguna ejecución vinculada: no hay trabajo que auditar."]);
        }

        return view;
    }

    /// <summary>The runs linked and, transitively, the runs that follow them (<c>RunStarted.Follows</c>), and the debates linked.</summary>
    private static AuditMilestoneScope ScopeOf(MilestoneView view, IReadOnlyList<DecisionEntry> log)
    {
        var follows = log.Where(e => e.Kind == DecisionKind.RunStarted)
            .Select(AuditedRun.Parse)
            .Select(i => i.Payload as RunStartedPayload)
            .OfType<RunStartedPayload>()
            .Where(p => !string.IsNullOrEmpty(p.Follows))
            .Select(p => (Run: p.RunId, Follows: p.Follows!))
            .ToList();
        var runs = new HashSet<string>(view.Targets.Where(t => t.Kind == MilestoneTargetKind.Run).Select(t => t.Id), StringComparer.Ordinal);
        bool grew;
        do
        {
            grew = false;
            foreach (var (run, previous) in follows)
            {
                if (runs.Contains(previous) && runs.Add(run))
                {
                    grew = true;
                }
            }
        }
        while (grew);

        var debates = view.Targets.Where(t => t.Kind == MilestoneTargetKind.Debate).Select(t => t.Id).Order(StringComparer.Ordinal).ToList();
        return new AuditMilestoneScope(view.Header.Id.Value, view.Header.Name, view.Header.Objective, [.. runs.Order(StringComparer.Ordinal)], debates);
    }

    private async Task<ProjectAuditProposal> PrepareAsync(CloseMilestoneRequest request, MilestoneView view, AuditMilestoneScope scope, CancellationToken ct)
    {
        var declared = request.DeclaredAuthors ?? [];
        IReadOnlyList<AgentId> candidates;
        if (request.Judge is { } chosen)
        {
            candidates = [chosen];
        }
        else
        {
            var template = await roster.ListAsync(request.Project.ProjectId, ct).ConfigureAwait(false);
            candidates = [.. template.Where(a => a.Role is AgentRole.Judge or AgentRole.Auditor).Select(a => a.Id)];
            if (candidates.Count == 0)
            {
                throw new MilestoneCloseRefusedException("milestone.no-judge", ["La plantilla de agentes del proyecto no tiene ningún juez ni auditor: sin uno no se audita."]);
            }
        }

        var problems = new List<string>();
        foreach (var judge in candidates)
        {
            var proposal = await audits.PrepareAsync(new ProjectAuditRequest(request.Project, view.CurrentRequirementSetHash, judge, declared, scope), ct).ConfigureAwait(false);
            if (proposal.CanJudge)
            {
                return proposal;
            }

            problems.AddRange(proposal.Problems.Where(p => !problems.Contains(p)));
        }

        throw new MilestoneCloseRefusedException("milestone.no-judge", problems.Count > 0 ? problems : ["Nadie puede juzgar esta auditoría."]);
    }

    private async Task<EvidenceRecord> CaptureAsync(string projectId, MilestoneId milestone, MilestoneReport content, CancellationToken ct)
    {
        using var stream = new MemoryStream(content.ToBytes(), writable: false);
        return await evidence.CaptureAsync(
            projectId,
            new EvidenceDraft(
                EvidenceKind.Document,
                EvidenceOrigin.CapturedBySystem,
                $"{MilestoneReport.EvidenceTitle} {milestone.Value} (revisión {content.Revision})",
                IndependentReview.SystemId,
                RunId: milestone.Value,
                MediaType: "application/json"),
            stream,
            ct).ConfigureAwait(false);
    }

    private async Task NotifyAsync(MilestoneClosedNotice notice, CancellationToken ct)
    {
        foreach (var listener in listeners ?? [])
        {
            try
            {
                await listener.OnClosedAsync(notice, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Milestone {notice.Milestone.Header.Id}: a listener of its close failed: {ex.Message}");
            }
        }
    }
}
