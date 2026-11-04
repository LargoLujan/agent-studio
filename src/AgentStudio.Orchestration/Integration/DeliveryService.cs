using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Integration;

/// <summary>
/// Delivers a run's branch, <c>agentstudio/run-&lt;run&gt;</c>, to the branch the user has checked out (docs/13 sections 5
/// and 9, ADR-0012). It is the only place where Agent Studio changes the user's branch, and it only does so after
/// <see cref="IApprovalGate"/> approved it as a critical action.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only a run the verified decision log shows ended Succeeded, with a valid Approved verdict (in the log and in the
/// verdict store) of the integration it ended with, whose judge was shown that integration and worked on it, is delivered
/// (<see cref="CheckAsync"/>); and only that commit, while the run's branch still points at it. Anything else is refused
/// before git is asked anything.</item>
/// <item>Phase 5 (docs/15 D-35 and D-40, decision 6(a); ADR-0012, addendum of 2026-10-19): a challenge of a verdict of the run
/// without a closed revision blocks the delivery; a revision that withdraws an approval blocks it; one that turns towards
/// an approval counts only once the user accepts it (<see cref="RevisionAcceptanceAction"/>), and one that confirms the
/// approval lets it through. A request to replan the run blocks it until the user dismisses it
/// (<see cref="ReplanDismissalAction"/>) or starts its follow-up run. All of it is read from the verified log.</item>
/// <item>Nothing is asked when there is nothing to deliver: HEAD detached, no run branch, or the user's branch already
/// contains it (that counts as delivered). A fast-forward that is no longer possible is refused before asking.</item>
/// <item>The approval names the branch, both commits and the paths that change, and warns about uncommitted changes. It
/// states explicitly that the delivery does NOT run the user's hooks and does NOT sign (it runs like any internal
/// command, R3-05), and gives the exact <c>git merge</c> command the user can run by hand afterwards if they want their
/// hooks or signature.</item>
/// <item>A delivery the git layer would refuse is refused before asking, so it spends no approval
/// (<see cref="IWorktreeManager.FindDeliveryRefusalsAsync"/>): the delivery merge goes through the same allowlist
/// classifier, so an in-repository callback in the user's configuration is refused (ADR-0012, N-01/R3-05). The git layer
/// checks it again just before the merge.</item>
/// <item>The request is bound to exactly this delivery (<see cref="DeliveryApproval"/>: repository, branch, mode, commit
/// and expected head, with a nonce), and the gate's yes must answer it. After the approval the repository is inspected
/// again: if HEAD moved or switched branch meanwhile, nothing is done. The delivery itself goes through
/// <see cref="IWorktreeManager.DeliverToUserBranchAsync"/> with the request and the answer as its approval, and the exact
/// commit that was approved, not the branch name. The git layer checks the binding (and spends it), checks HEAD again,
/// and does nothing when the user has work in a delivered path.</item>
/// <item><see cref="DeliveryMode.FastForwardOnly"/> is <c>git merge --ff-only</c>; <see cref="DeliveryMode.MergeCommit"/>
/// makes a merge commit only when it has no conflicts. Both run with the user's identity but without the user's hooks or
/// signing.</item>
/// <item>A delivery that moved the user's branch is recorded as <c>IntegrationPerformed</c> with <c>IsDelivery</c>.</item>
/// </list>
/// The messages are shown to the user, so they are in Spanish.
/// </remarks>
public sealed class DeliveryService : IDeliveryService
{
    public const string ApprovalAction = "deliver-integration";

    /// <summary>
    /// The action of the user's acceptance of a revision that turned towards an approval (docs/15 D-40): an
    /// <c>ApprovalGranted</c> of a person, asked after the revision closed, in the run of the challenged verdict or in the
    /// revision debate.
    /// </summary>
    public const string RevisionAcceptanceAction = "verdict.revision.accept";

    /// <summary>The action of the user's dismissal of a request to replan a run (docs/15 D-35), in the run itself.</summary>
    public const string ReplanDismissalAction = "replan.dismiss";

    /// <summary>
    /// The action of the approval to open a debate (docs/15 D-06), in the debate's own unit: a person's no to opening the
    /// revision of a challenge withdraws that challenge before it started.
    /// </summary>
    public const string DebateOpenAction = "debate.open";

    /// <summary>Who signs the record of a delivery: the system that made it (the gate recorded who approved it).</summary>
    public const string SystemId = "agent-studio";

    private static readonly Actor SystemActor = new(ActorKind.System, SystemId);

    private readonly IWorktreeManager _worktrees;
    private readonly IApprovalGate _approvals;
    private readonly IDecisionRecorder _decisions;
    private readonly IVerdictStore _verdicts;

    public DeliveryService(IWorktreeManager worktrees, IApprovalGate approvals, IDecisionRecorder decisions, IVerdictStore verdicts)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(verdicts);

        _worktrees = worktrees;
        _approvals = approvals;
        _decisions = decisions;
        _verdicts = verdicts;
    }

    /// <summary>
    /// Whether the run may be delivered, read from the verified decision log and the verdict store, without git. The
    /// commit it gives is the integration a judge reviewed and approved, the only one a delivery takes.
    /// </summary>
    /// <remarks>
    /// The verdict is tied to that commit by the log itself: it is the integration the run ended with
    /// (<c>RunFinished.IntegrationCommit</c>), recorded before the verdict, with no other integration after it; it is
    /// exactly the integration the judge was shown, the last one in the log at the head recorded with the verdict
    /// (<c>VerdictIssued.DecisionLogHeadHash</c>, the head the dossier was built on, <see cref="ShownCommit"/>); and it is
    /// the commit the worktree of the verdict's review started on (<c>WorktreeCreated</c> of that task). The verdict the
    /// run ended with is the one of the review that finished last, so a plan whose reviews do not all start from the last
    /// integration may not be delivered (<c>PlanValidator.Warnings</c> says so before it runs). A run that succeeded
    /// because a person approved an escalated review is not ready: no judge approved it.
    /// <para>
    /// Phase 5 (docs/15 D-35 and D-40). When the verdict the run ended with was challenged, what decides is its revision, read
    /// by <see cref="Revisions"/>: a revision that confirms the approval lets the delivery through, one that withdraws it
    /// blocks it, and one that turns towards an approval (the run's verdict was no judge's approval) counts only once the
    /// user accepts it. A challenge whose revision has not closed, has not opened, was aborted, needs a person or was
    /// decided by the user blocks it: a decision of a person is not the approval of an independent judge. A request to
    /// replan the run that nobody attended blocks it too. The run must still have ended Succeeded: a revision changes which
    /// verdict counts, never the end of the run.
    /// </para>
    /// </remarks>
    public async Task<DeliveryReadiness> CheckAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var runBranch = IntegrationNames.BranchName(run);
        // A critical moment: the whole log, verified from its first entry (ADR-0010, addendum of 2026-11-04).
        var log = await _decisions.LoadAsync(project.ProjectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            return NotReady($"El registro de decisiones no está íntegro (entrada {log.Verification.FirstInvalidSequence}): no se entrega nada.");
        }

        var all = TypedEntries(log.Entries);
        var entries = all.Where(e => string.Equals(e.Payload.RunId, run.Value, StringComparison.Ordinal)).ToList();
        var finished = entries.Select(e => e.Payload).OfType<RunFinishedPayload>().LastOrDefault();
        if (finished is null)
        {
            return NotReady($"La ejecución {run.Value} no ha terminado: no hay nada que entregar.");
        }

        if (finished.Phase != RunPhase.Succeeded)
        {
            return NotReady($"La ejecución {run.Value} terminó como {finished.Phase}: solo se entrega una ejecución completada.");
        }

        if (finished.VerdictId is not { } verdictId || finished.IntegrationCommit is not { } integrationCommit)
        {
            return NotReady($"La ejecución {run.Value} no tiene una integración aprobada por un juez independiente: no se entrega.");
        }

        var issued = entries.Where(e => e.Payload is VerdictIssuedPayload).ToList();
        var verdict = issued.LastOrDefault(e => ((VerdictIssuedPayload)e.Payload).VerdictId == verdictId);
        if (verdict.Payload is not VerdictIssuedPayload announced)
        {
            return NotReady($"El veredicto {verdictId} con el que terminó la ejecución no está en el registro de decisiones: no se entrega.");
        }

        var stored = await _verdicts.ListAsync(project.ProjectId, run.Value, cancellationToken).ConfigureAwait(false);
        var kept = stored.FirstOrDefault(v => v.Id == verdictId);
        var approved = announced.Valid && announced.Decision == VerdictDecision.Approved;
        var revisions = new Revisions(all, run.Value, stored, announced.RequirementSetHash, integrationCommit);
        if (!revisions.Challenged(verdictId))
        {
            // Phase 3, as it was: a valid approval of a judge, confirmed by the store and superseded by none.
            if (!approved)
            {
                return NotReady($"El veredicto del juez es {announced.Decision}{(announced.Valid ? string.Empty : " y no es válido")}: "
                    + "solo se entrega lo que un juez independiente aprobó con un veredicto válido.");
            }

            if (kept is not { Valid: true, Verdict.Decision: VerdictDecision.Approved } || kept.RunId != run.Value)
            {
                return NotReady($"El almacén de veredictos no confirma que el veredicto {verdictId} sea una aprobación válida: no se entrega.");
            }

            if (stored.Any(v => v.Supersedes == verdictId) || issued.Any(e => ((VerdictIssuedPayload)e.Payload).Supersedes == verdictId))
            {
                return NotReady($"El veredicto {verdictId} fue sustituido por otro: no se entrega.");
            }
        }
        else
        {
            // Challenged (D-40): the store must say what the log announced of it, and its revision decides.
            if (kept is null || kept.RunId != run.Value || kept.Valid != announced.Valid || kept.Verdict.Decision != announced.Decision)
            {
                return NotReady($"El almacén de veredictos no confirma el veredicto {verdictId} tal como lo anunció el registro de decisiones: no se entrega.");
            }
        }

        var standing = revisions.Judge(verdictId, approved);
        if (standing.Blocked is { } blocked)
        {
            return NotReady(blocked);
        }

        var revised = standing.Note;

        // Whatever verdict of the run it revises, a revision still going on means the run is disputed.
        if (revisions.Pending() is { } pending)
        {
            return NotReady(pending);
        }

        var integrations = entries.Where(e => e.Payload is IntegrationPerformedPayload { IsDelivery: false }).ToList();
        if (integrations.Any(e => e.Entry.Sequence > verdict.Entry.Sequence))
        {
            return NotReady($"Hubo una integración en {runBranch} después del veredicto: el juez no revisó lo que se entregaría.");
        }

        var reviewed = integrations.LastOrDefault(e => e.Entry.Sequence < verdict.Entry.Sequence).Payload as IntegrationPerformedPayload;
        if (reviewed is not { ResultCommit: { } reviewedCommit, ConflictPaths.Count: 0 }
            || !string.Equals(reviewedCommit, integrationCommit, StringComparison.OrdinalIgnoreCase))
        {
            return NotReady($"La integración que revisó el juez no es la que dio por buena la ejecución ({Short(integrationCommit)}): no se entrega.");
        }

        // The order of the log is not enough: a review that did not wait for a later integration can end after it, and the
        // last integration before its verdict would then be one its judge never saw. What the judge was shown is the
        // dossier's candidate at the head of the log the dossier was built on, which the system records with the verdict
        // (VerdictKeeper copies it from the dossier, never from the judge's answer): only that exact commit is delivered.
        // The run ends with the verdict of the review that finished last (OrchestrationRun), which need not be the one that
        // was shown the last integration.
        var review = announced.Task is { } task ? $"de la revisión {task}" : "de una revisión sin tarea";
        var shown = ShownCommit(log.Entries, announced.DecisionLogHeadHash, run);
        if (shown is null)
        {
            return NotReady($"El veredicto {verdictId} no dice qué integración se le mostró al juez (el registro no tiene la cabeza que cita, o no había ninguna integración): no se entrega.");
        }

        if (!string.Equals(shown, integrationCommit, StringComparison.OrdinalIgnoreCase))
        {
            return NotReady($"La ejecución terminó con el veredicto {verdictId}, {review}, la última que acabó, y a esa revisión se le mostró la integración {Short(shown)}; lo que se entregaría es {Short(integrationCommit)}: no se entrega.");
        }

        // What the judge had in front of it is also its worktree («Workspace with the integrated code», JudgePrompt), which
        // the engine starts on the review's code frontier, not on the dossier's candidate: it must be that very commit too.
        var worktrees = entries
            .Where(e => e.Entry.Sequence < verdict.Entry.Sequence)
            .Select(e => e.Payload)
            .OfType<WorktreeCreatedPayload>()
            .Where(w => announced.Task is { } reviewTask && w.Task == reviewTask)
            .ToList();
        if (worktrees.Count == 0)
        {
            return NotReady($"El registro no dice sobre qué código trabajó el juez del veredicto {verdictId} (no tiene el espacio de trabajo {review}): no se entrega.");
        }

        if (worktrees.FirstOrDefault(w => !string.Equals(w.BaseCommit, integrationCommit, StringComparison.OrdinalIgnoreCase)) is { } elsewhere)
        {
            return NotReady($"El juez del veredicto {verdictId} trabajó sobre {Short(elsewhere.BaseCommit)} (el espacio de trabajo {review}), no sobre la integración {Short(integrationCommit)} que se entregaría: no tuvo delante ese código, así que no se entrega.");
        }

        // D-35: a request to replan that nobody attended (dismissed, or followed by a follow-up run) blocks the delivery.
        if (UnattendedReplan(all, run.Value) is { } replan)
        {
            return NotReady(replan);
        }

        var commit = reviewedCommit.ToLowerInvariant();
        return new DeliveryReadiness(
            true,
            commit,
            revised is null
                ? $"El juez aprobó la integración {Short(commit)} de la ejecución {run.Value}."
                : $"Se puede entregar la integración {Short(commit)} de la ejecución {run.Value}: {revised}.");
    }

    public async Task<DeliveryResult> DeliverAsync(ProjectContext project, RunId run, DeliveryMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown delivery mode.");
        }

        // Only what the log shows finished well and a judge approved, and only that exact commit.
        var readiness = await CheckAsync(project, run, cancellationToken).ConfigureAwait(false);
        if (readiness is not { Ready: true, Commit: { } integration })
        {
            return new DeliveryResult(false, null, string.Empty, readiness.Reason);
        }

        var runBranch = IntegrationNames.BranchName(run);
        var before = await _worktrees.InspectAsync(project.RootPath, cancellationToken).ConfigureAwait(false);
        if (before.HeadBranch is null)
        {
            return new DeliveryResult(false, null, "HEAD", $"HEAD no está en ninguna rama: no hay a qué rama entregar {runBranch}.");
        }

        var target = before.HeadBranch;
        var tip = await _worktrees.ResolveCommitAsync(before.Root, IntegrationNames.BranchRef(run), cancellationToken).ConfigureAwait(false);
        if (tip is null)
        {
            return new DeliveryResult(false, null, target, $"No existe la rama {runBranch}: no hay nada que entregar.");
        }

        // The branch is only a pointer an agent with a shell can move: it must still be at what the judge approved.
        if (!string.Equals(tip, integration, StringComparison.OrdinalIgnoreCase))
        {
            return new DeliveryResult(
                false,
                null,
                target,
                $"La rama {runBranch} está en {Short(tip)}, pero el juez aprobó {Short(integration)}: se movió después de la revisión y no se entrega nada.");
        }

        if (await ContainsAsync(before.Root, before.HeadCommit, integration, cancellationToken).ConfigureAwait(false))
        {
            return new DeliveryResult(true, before.HeadCommit, target, $"{target} ya contiene {runBranch}: no hay nada que entregar.");
        }

        if (mode == DeliveryMode.FastForwardOnly
            && !await _worktrees.IsAncestorAsync(before.Root, before.HeadCommit, integration, cancellationToken).ConfigureAwait(false))
        {
            return new DeliveryResult(
                false,
                null,
                target,
                $"{target} ha avanzado desde que empezó la ejecución, así que no se puede entregar con --ff-only. "
                + "Entrega con un commit de fusión o lanza una ejecución de seguimiento.");
        }

        // Before asking: what changes (the approval names the count and the paths).
        var paths = await _worktrees.ChangedPathsAsync(before.Root, before.HeadCommit, integration, cancellationToken).ConfigureAwait(false);

        // What the git layer would refuse is refused before asking, so no approval is spent on a delivery that cannot be
        // made: the delivery runs like any internal command, so an in-repository callback in the user's configuration is
        // refused (N-01/R3-05). The git layer checks it again just before the merge, since the repository can change
        // while the approval is pending.
        var refusals = await _worktrees.FindDeliveryRefusalsAsync(before.Root, before.HeadCommit, integration, cancellationToken).ConfigureAwait(false);
        if (refusals.Count > 0)
        {
            return new DeliveryResult(false, null, target, Refused(target, refusals));
        }

        // The approval is bound to exactly this delivery: the git layer refuses it for anything else, and only once.
        var binding = DeliveryApproval.Bind(before.Root, target, mode, integration, before.HeadCommit);
        var request = new ApprovalRequest(
            ApprovalAction,
            DescribeRequest(run, mode, target, before, integration, paths.Count),
            RiskLevel.Critical,
            RequestedBy: null,
            AffectedPaths: paths,
            ProjectId: project.ProjectId,
            RunId: run.Value,
            Binding: binding);
        var decision = await _approvals.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            var comment = string.IsNullOrWhiteSpace(decision.Comment) ? string.Empty : $" ({decision.Comment})";
            return new DeliveryResult(false, null, target, $"Entrega denegada por {decision.DecidedBy}{comment}: {target} no se ha tocado.");
        }

        if (!string.Equals(decision.Answers, binding, StringComparison.Ordinal))
        {
            return new DeliveryResult(false, null, target, $"La aprobación recibida no responde a la petición de esta entrega: {target} no se ha tocado. Vuelve a pedir la entrega.");
        }

        var now = await _worktrees.InspectAsync(project.RootPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(now.Root, before.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(now.HeadBranch, before.HeadBranch, StringComparison.Ordinal)
            || !string.Equals(now.HeadCommit, before.HeadCommit, StringComparison.OrdinalIgnoreCase))
        {
            return Changed(target, now.HeadBranch, now.HeadCommit);
        }

        var outcome = await _worktrees.DeliverToUserBranchAsync(
            new ApprovedDelivery(before.Root, target, before.HeadCommit, integration, mode, MergeMessage(run, integration), request, decision),
            cancellationToken).ConfigureAwait(false);
        if (outcome.Status == UserBranchDeliveryStatus.Delivered)
        {
            var delivered = $"Entregado: {target} está en {Short(outcome.HeadCommit)} e incluye {runBranch}.";
            var unrecorded = await RecordDeliveryAsync(project, run, target, integration, outcome.HeadCommit).ConfigureAwait(false);
            return new DeliveryResult(
                true,
                outcome.HeadCommit,
                target,
                unrecorded is null ? delivered : $"{delivered} Atención: la entrega no se pudo anotar en el registro de decisiones ({unrecorded}).");
        }

        return outcome.Status switch
        {
            UserBranchDeliveryStatus.RepositoryChanged => Changed(target, null, outcome.HeadCommit),
            UserBranchDeliveryStatus.DirtyPaths => new DeliveryResult(
                false,
                null,
                target,
                $"Tienes cambios sin confirmar, ficheros sin seguimiento o ignorados en rutas que cambia la entrega ({string.Join(", ", outcome.Paths)}): "
                + $"{target} no se ha tocado. Confírmalos o apártalos y vuelve a pedir la entrega."),
            UserBranchDeliveryStatus.Conflicts => new DeliveryResult(
                false,
                null,
                target,
                $"Fusionar {runBranch} en {target} tendría conflictos en {string.Join(", ", outcome.Paths)}: no se ha tocado nada. "
                + $"Lanza una ejecución de seguimiento desde {target}."),
            UserBranchDeliveryStatus.IncompleteMerge => new DeliveryResult(
                false,
                null,
                target,
                $"git fusionó {runBranch} en tu árbol de trabajo pero no pudo crear el commit (¿lo rechazó un hook tuyo?): {target} no se ha movido y la "
                + $"fusión sigue en curso. Termínala con git commit o deshazla con git merge --abort. git dijo: {outcome.Output}"),
            _ => new DeliveryResult(
                false,
                null,
                target,
                $"git no completó la entrega y {target} no se ha movido; revisa tu repositorio (git status). git dijo: {outcome.Output}"),
        };
    }

    /// <summary>
    /// Records the delivery as an integration into the user's branch (<c>IntegrationPerformed</c> with
    /// <c>IsDelivery</c>), signed by the system: the gate already recorded who approved it. The delivery already happened,
    /// so it is recorded even if the caller is cancelling, and a failure is returned to be told, never thrown.
    /// </summary>
    private async Task<string?> RecordDeliveryAsync(ProjectContext project, RunId run, string target, string integration, string head)
    {
        try
        {
            await _decisions.RecordAsync(
                project.ProjectId,
                SystemActor,
                DecisionKind.IntegrationPerformed,
                $"Entrega de la ejecución {run.Value} en {target}: {Short(head)}",
                new IntegrationPerformedPayload(null, TargetRef(target), [integration], head, [], IsDelivery: true) { RunId = run.Value },
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return null;
        }
#pragma warning disable CA1031 // The user's branch already moved: a failure to record it is told to the user, not thrown away with the result.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The commit the judge of <paramref name="run"/> was shown when its dossier was built on the log up to the entry whose
    /// hash is <paramref name="head"/>: the dossier's candidate, the run's last clean integration into its branch (a
    /// result, no conflict, not a delivery). It mirrors <c>RunLog.CandidateCommit</c>, which the dossier uses and this
    /// assembly cannot reference (ADR-0002); <c>DeliveryServiceTests</c> checks that both agree on every head of a log.
    /// Null when the log has no entry with that hash, or the run had no such integration yet.
    /// </summary>
    public static string? ShownCommit(IReadOnlyList<DecisionEntry> log, string head, RunId run)
    {
        ArgumentNullException.ThrowIfNull(log);
        var at = log.FirstOrDefault(e => string.Equals(e.Hash, head, StringComparison.Ordinal));
        if (at is null)
        {
            return null;
        }

        var runBranch = IntegrationNames.BranchName(run);
        return TypedEntries(log.Where(e => e.Kind == DecisionKind.IntegrationPerformed && e.Sequence <= at.Sequence))
            .Select(e => e.Payload)
            .OfType<IntegrationPerformedPayload>()
            .LastOrDefault(p => p is { IsDelivery: false, ResultCommit: not null, ConflictPaths.Count: 0 }
                && string.Equals(p.RunId, run.Value, StringComparison.Ordinal)
                && string.Equals(p.TargetRef, runBranch, StringComparison.Ordinal))
            ?.ResultCommit;
    }

    /// <summary>
    /// The entries of the log with their payloads, in log order (each payload names its run: a run, a debate, an audit or
    /// none). An unreadable payload names no run, so it is left out.
    /// </summary>
    private static List<(DecisionEntry Entry, DecisionPayload Payload)> TypedEntries(IEnumerable<DecisionEntry> entries)
    {
        var result = new List<(DecisionEntry, DecisionPayload)>();
        foreach (var entry in entries.OrderBy(e => e.Sequence))
        {
            DecisionPayload? payload;
            try
            {
                payload = DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                payload = null;
            }

            if (payload is not null)
            {
                result.Add((entry, payload));
            }
        }

        return result;
    }

    /// <summary>
    /// The first request to replan <paramref name="run"/> that nobody attended (docs/15 D-35), told for the person, or null.
    /// A request is attended by a person's yes to dismissing it (<see cref="ReplanDismissalAction"/>, in the run) asked after
    /// it, or by a follow-up run of this one (<c>RunStarted.Follows</c>) that started after it. A dismissal or a follow-up
    /// that came before a request does not answer it, so a new request blocks again.
    /// </summary>
    private static string? UnattendedReplan(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> all, string run)
    {
        var dismissals = GrantedByPerson(all, ReplanDismissalAction, unit => string.Equals(unit, run, StringComparison.Ordinal));
        var followUps = all
            .Where(e => e.Payload is RunStartedPayload { Follows: { } follows } && string.Equals(follows, run, StringComparison.Ordinal))
            .Select(e => e.Entry.Sequence)
            .ToList();
        foreach (var (entry, payload) in all)
        {
            if (payload is not ReplanRequestedPayload request || !string.Equals(payload.RunId, run, StringComparison.Ordinal))
            {
                continue;
            }

            if (dismissals.Any(d => d.Requested > entry.Sequence) || followUps.Any(f => f > entry.Sequence))
            {
                continue;
            }

            var findings = request.Findings.Count == 0 ? "ninguno" : string.Join(", ", request.Findings);
            return $"Hay una petición de replanificar la ejecución {run} sin atender ({ReplanAuthor(entry.Actor)}; hallazgos: {findings}): "
                + "no se entrega hasta que la descartes o lances la ejecución de seguimiento.";
        }

        return null;
    }

    private static string ReplanAuthor(Actor actor) => actor.Kind switch
    {
        ActorKind.Auditor => "la pidió el auditor del orquestador",
        ActorKind.Judge => $"la pidió el juez {actor.Id} de la revisión del orquestador",
        _ => $"la pidió {actor.Id}",
    };

    /// <summary>
    /// A person's yes to an approval of <c>Action</c> in a unit of the log, with the sequence of the request it answers: the
    /// last request of the same action in the same unit before it, which the answer spends (the gate records the request
    /// first, then the answer).
    /// </summary>
    private sealed record PersonsYes(string Unit, long Requested, long Answered);

    private static List<PersonsYes> GrantedByPerson(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> all, string action, Func<string, bool> inUnit)
    {
        var requests = new Dictionary<string, long>(StringComparer.Ordinal);
        var granted = new List<PersonsYes>();
        foreach (var (entry, payload) in all)
        {
            if (!inUnit(payload.RunId))
            {
                continue;
            }

            switch (payload)
            {
                case ApprovalRequestedPayload asked when entry.Kind == DecisionKind.ApprovalRequested && string.Equals(asked.Action, action, StringComparison.Ordinal):
                    requests[payload.RunId] = entry.Sequence;
                    break;
                case ApprovalAnsweredPayload answer when string.Equals(answer.Action, action, StringComparison.Ordinal):
                    if (requests.Remove(payload.RunId, out var requested)
                        && entry.Kind == DecisionKind.ApprovalGranted
                        && answer.Approved
                        && entry.Actor.Kind == ActorKind.User)
                    {
                        granted.Add(new PersonsYes(payload.RunId, requested, entry.Sequence));
                    }

                    break;
            }
        }

        return granted;
    }

    /// <summary>
    /// The challenges of the verdicts of one run and their revisions, as the verified log and the verdict store say (docs/15
    /// D-38 to D-40): <c>VerdictChallenged</c> in the run names the verdict and its revision debate; the debate opens with
    /// <c>DebateOpened</c> naming the verdict it challenges and ends with <c>DebateClosed</c> naming its verdicts, which are
    /// kept in the run of the original (stored and announced with <c>VerdictIssued</c>) with <c>Supersedes</c> leading back
    /// to the challenged verdict, through the one correction of each judge.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A judgment (the verdict the run ended with, or the verdicts of a revision) stands unless challenged; the latest
    /// challenge of it is the latest word on it. A challenge whose revision opened and has not closed blocks; the latest one
    /// blocks too when its revision never opened, was aborted, needs a person or was decided by the user. An earlier one in
    /// those states gave way to the latest; one that was judged still stands beside it. A challenge whose revision a person
    /// declined to open (their no to <see cref="DebateOpenAction"/> in the debate) never started and counts for nothing; a no
    /// of the system (the question expired, say) is no decision, so that one still blocks.</item>
    /// <item>Every revision judged in that way is a branch, and every branch must end in a valid approval that counts: one
    /// that confirms an approval that counted, or one the user accepted (<see cref="RevisionAcceptanceAction"/>). Challenging
    /// the original again never undoes a revision that withdrew its approval.</item>
    /// <item>A revision counts only if it checks out: opened against that verdict, closed once with the rule of a judge or a
    /// panel, each of its verdicts announced after the challenge, valid, about the same requirements, superseding the
    /// challenged verdict (through its correction) and confirmed by the store; a panel's verdicts say the same. A verdict
    /// that supersedes one of a judgment without a challenge before it, or one only the store holds, blocks as in Phase 3.</item>
    /// <item>An acceptance is a person's yes asked after the revision closed and its verdicts were announced: in the revision
    /// debate it accepts that revision; in the run, the revision that closed last before it was asked.</item>
    /// </list>
    /// </remarks>
    private sealed class Revisions
    {
        private const int MaxDepth = 16;
        private const int MaxCorrections = 8;

        private readonly string _run;
        private readonly string _requirements;
        private readonly string _commit;
        private readonly IReadOnlyList<StoredVerdict> _stored;
        private readonly List<(DecisionEntry Entry, VerdictIssuedPayload Payload)> _issued;
        private readonly List<(DecisionEntry Entry, VerdictChallengedPayload Payload)> _challenges;
        private readonly Dictionary<string, (DecisionEntry Entry, DebateOpenedPayload Payload)> _opened = new(StringComparer.Ordinal);

        /// <summary>
        /// Revision debates that opened against a verdict of this run, have not closed, and that no <c>VerdictChallenged</c>
        /// of the run names: the app stopped between the two entries (the challenge is written once the debate has its id).
        /// </summary>
        private readonly List<string> _unannouncedOpen = [];
        private readonly Dictionary<string, List<(DecisionEntry Entry, DebateClosedPayload Payload)>> _closed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _declined = new(StringComparer.Ordinal);
        private readonly List<PersonsYes> _acceptances;

        /// <param name="requirements">The hash of the requirements the verdict the run ended with judged: a revision judges the same.</param>
        /// <param name="commit">The integration the run ended with: the one a revision judges («del mismo commit», D-40).</param>
        public Revisions(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> all, string run, IReadOnlyList<StoredVerdict> stored, string requirements, string commit)
        {
            _run = run;
            _requirements = requirements;
            _commit = commit;
            _stored = stored;
            _issued = [.. all
                .Where(e => string.Equals(e.Payload.RunId, run, StringComparison.Ordinal) && e.Payload is VerdictIssuedPayload)
                .Select(e => (e.Entry, (VerdictIssuedPayload)e.Payload))];
            _challenges = [.. all
                .Where(e => string.Equals(e.Payload.RunId, run, StringComparison.Ordinal) && e.Payload is VerdictChallengedPayload)
                .Select(e => (e.Entry, (VerdictChallengedPayload)e.Payload))];
            var debates = _challenges.Select(c => c.Payload.RevisionDebate).ToHashSet(StringComparer.Ordinal);
            foreach (var (entry, payload) in all)
            {
                if (!debates.Contains(payload.RunId))
                {
                    continue;
                }

                switch (payload)
                {
                    case DebateOpenedPayload opened:
                        _opened.TryAdd(payload.RunId, (entry, opened));
                        break;
                    case DebateClosedPayload closed:
                        if (!_closed.TryGetValue(payload.RunId, out var closures))
                        {
                            _closed.Add(payload.RunId, closures = []);
                        }

                        closures.Add((entry, closed));
                        break;
                    case ApprovalAnsweredPayload { Approved: false } answer
                        when entry.Kind == DecisionKind.ApprovalDenied
                            && entry.Actor.Kind == ActorKind.User
                            && string.Equals(answer.Action, DebateOpenAction, StringComparison.Ordinal):
                        _declined.Add(payload.RunId);
                        break;
                }
            }

            _acceptances = GrantedByPerson(all, RevisionAcceptanceAction, unit => string.Equals(unit, run, StringComparison.Ordinal) || debates.Contains(unit));

            var verdicts = _issued.Select(v => v.Payload.VerdictId).ToHashSet(StringComparer.Ordinal);
            var unannounced = all
                .Where(e => e.Payload is DebateOpenedPayload { Challenges: { } challenged } && verdicts.Contains(challenged) && !debates.Contains(e.Payload.RunId))
                .Select(e => e.Payload.RunId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var closedUnannounced = all
                .Where(e => e.Payload is DebateClosedPayload && unannounced.Contains(e.Payload.RunId, StringComparer.Ordinal))
                .Select(e => e.Payload.RunId)
                .ToHashSet(StringComparer.Ordinal);
            _unannouncedOpen.AddRange(unannounced.Where(debate => !closedUnannounced.Contains(debate)));
        }

        private enum StateKind
        {
            /// <summary>The person said no to opening its revision: the challenge never started, and counts for nothing.</summary>
            Withdrawn,
            NotOpened,
            Pending,
            Aborted,
            NeedsPerson,
            DecidedByUser,
            Inconsistent,
            Judged,
        }

        /// <summary>Whether the log records a challenge of <paramref name="verdict"/> in this run.</summary>
        public bool Challenged(string verdict) => _challenges.Any(c => string.Equals(c.Payload.Verdict, verdict, StringComparison.Ordinal));

        /// <summary>A revision of a challenge of this run that opened and has not closed, told for the person, or null.</summary>
        public string? Pending()
        {
            foreach (var (_, challenge) in _challenges)
            {
                if (_opened.ContainsKey(challenge.RevisionDebate) && !_closed.ContainsKey(challenge.RevisionDebate))
                {
                    return PendingReason(challenge.Verdict, challenge.RevisionDebate);
                }
            }

            // Fail closed when the challenge itself was never written (the app stopped right after opening its revision).
            return _unannouncedOpen.Count > 0
                ? $"Hay una revisión abierta (debate {_unannouncedOpen[0]}) de un veredicto de esta ejecución que no ha terminado, aunque el registro no anotó la impugnación (la app se cerró a mitad): no se entrega hasta que la revisión termine."
                : null;
        }

        /// <summary>
        /// What the challenges of the run leave of its delivery: those of <paramref name="root"/>, the verdict the run ended
        /// with, and its revisions decide (when it was challenged); a challenge of any other verdict of the run (the first
        /// attempt its judge corrected, say) is about the same work, so its revision may not leave a withdrawn approval
        /// either, although it does not need to be accepted to let the delivery through.
        /// </summary>
        /// <param name="approved">Whether <paramref name="root"/> is a valid approval of a judge.</param>
        public Standing Judge(string root, bool approved)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { root };
            var standing = Challenged(root) ? Evaluate(new Node([root], approved, approved, null, false, false), 0, visited) : new Standing(null, null);
            if (standing.Blocked is not null)
            {
                return standing;
            }

            foreach (var other in _challenges.Select(c => c.Payload.Verdict).Distinct(StringComparer.Ordinal).ToList())
            {
                if (!visited.Add(other))
                {
                    continue;
                }

                // Its own supersession is the judge's correction that the run ended with, not something to explain.
                var aside = Evaluate(new Node([other], true, true, null, false, false), 0, visited, checkSupersession: false);
                if (aside.Blocked is not null)
                {
                    return aside;
                }
            }

            return standing;
        }

        private Standing Evaluate(Node node, int depth, HashSet<string> visited, bool checkSupersession = true)
        {
            if (depth > MaxDepth)
            {
                return Standing.Block($"La cadena de revisiones de los veredictos de la ejecución {_run} es más larga de lo que se comprueba ({MaxDepth}): no se entrega.");
            }

            var all = _challenges
                .Where(c => node.Verdicts.Contains(c.Payload.Verdict, StringComparer.Ordinal))
                .Select(c => (Challenge: c, State: StateOf(c)))
                .ToList();

            // A challenge whose revision the person declined to open never started: it counts for nothing, but as the latest
            // challenge it is still the latest word, so the ones before it that never got a judgment give way to it.
            var live = all.Where(c => c.State.Kind != StateKind.Withdrawn).ToList();
            if (live.Count == 0)
            {
                return checkSupersession && Supersession(node, []) is { } superseded ? Standing.Block(superseded) : Leaf(node);
            }

            foreach (var (_, state) in live)
            {
                if (state.Kind is StateKind.Pending or StateKind.Inconsistent)
                {
                    return Standing.Block(state.Reason!);
                }
            }

            if (all[^1].State is { Kind: not (StateKind.Judged or StateKind.Withdrawn) } latest)
            {
                return Standing.Block(latest.Reason!);
            }

            if (checkSupersession && Supersession(node, [.. live.Select(c => c.Challenge)]) is { } unexplained)
            {
                return Standing.Block(unexplained);
            }

            var judged = live.Where(c => c.State.Kind == StateKind.Judged).Select(c => c.State.Revision!).ToList();
            if (judged.Count == 0)
            {
                return Leaf(node);
            }

            var counted = node.Approved && node.Trusted;
            var standing = Standing.Block("Ninguna revisión decide.");
            foreach (var revision in judged)
            {
                if (revision.Verdicts.Any(visited.Contains))
                {
                    return Standing.Block(Inconsistent(revision.Challenged, revision.Debate, "sus veredictos ya estaban en la cadena de revisiones").Reason!);
                }

                visited.UnionWith(revision.Verdicts);
                var approves = revision.Decision == VerdictDecision.Approved;
                var child = new Node(revision.Verdicts, approves, approves && (counted || revision.Accepted), revision, node.Approved, counted);
                standing = Evaluate(child, depth + 1, visited);
                if (standing.Blocked is not null)
                {
                    return standing;
                }
            }

            return standing;
        }

        /// <summary>A judgment nobody challenged: it decides if it is an approval that counts.</summary>
        private static Standing Leaf(Node node)
        {
            if (node.Via is not { } via)
            {
                return node.Approved && node.Trusted ? new Standing(null, null) : Standing.Block("El veredicto con el que terminó la ejecución no es una aprobación válida de un juez: no se entrega.");
            }

            if (!node.Approved)
            {
                return Standing.Block(
                    $"La revisión (debate {via.Debate}) de la impugnación del veredicto {via.Challenged} dejó la integración en «{DecisionText(via.Decision)}»: "
                    + (node.ParentApproved ? "retira la aprobación y no se entrega." : "no la aprueba y no se entrega."));
            }

            if (!node.Trusted)
            {
                return Standing.Block(
                    $"La revisión (debate {via.Debate}) de la impugnación del veredicto {via.Challenged} "
                    + (node.ParentApproved ? "confirma una aprobación que aún no habías aceptado" : "da la vuelta hacia una aprobación")
                    + $" (veredicto {string.Join(", ", via.Verdicts)}): solo cuenta si la aceptas expresamente, así que no se entrega hasta que la aceptes.");
            }

            return new Standing(
                null,
                node.ParentCounted
                    ? $"la revisión (debate {via.Debate}) de la impugnación del veredicto {via.Challenged} confirmó la aprobación"
                    : $"aceptaste la revisión (debate {via.Debate}) de la impugnación del veredicto {via.Challenged}, que la aprueba");
        }

        /// <summary>
        /// A verdict that supersedes one of <paramref name="node"/> without a challenge of it before (Phase 3: a superseded
        /// verdict is not delivered), or one only the store holds; null when there is none.
        /// </summary>
        private string? Supersession(Node node, IReadOnlyList<(DecisionEntry Entry, VerdictChallengedPayload Payload)> challenges)
        {
            foreach (var member in node.Verdicts)
            {
                foreach (var (entry, _) in _issued.Where(v => string.Equals(v.Payload.Supersedes, member, StringComparison.Ordinal)))
                {
                    if (!challenges.Any(c => string.Equals(c.Payload.Verdict, member, StringComparison.Ordinal) && c.Entry.Sequence < entry.Sequence))
                    {
                        return $"El veredicto {member} fue sustituido por otro: no se entrega.";
                    }
                }

                foreach (var kept in _stored.Where(v => string.Equals(v.Supersedes, member, StringComparison.Ordinal)))
                {
                    if (!_issued.Any(v => string.Equals(v.Payload.VerdictId, kept.Id, StringComparison.Ordinal)))
                    {
                        return $"El almacén de veredictos tiene un veredicto ({kept.Id}) que sustituye a {member} y que el registro de decisiones no anuncia: no se entrega.";
                    }
                }
            }

            return null;
        }

        private State StateOf((DecisionEntry Entry, VerdictChallengedPayload Payload) challenge)
        {
            var (entry, payload) = challenge;
            var verdict = payload.Verdict;
            var debate = payload.RevisionDebate;
            if (!_opened.TryGetValue(debate, out var opened))
            {
                if (_declined.Contains(debate))
                {
                    return new State(StateKind.Withdrawn, null);
                }

                return new State(
                    StateKind.NotOpened,
                    $"El veredicto {verdict} está impugnado y su revisión (debate {debate}) no llegó a abrirse: no se entrega. Vuelve a impugnarlo para abrir la revisión.");
            }

            if (!string.Equals(opened.Payload.Challenges, verdict, StringComparison.Ordinal))
            {
                return Inconsistent(verdict, debate, "su apertura no impugna ese veredicto");
            }

            if (!string.Equals(opened.Payload.RequirementSetHash, _requirements, StringComparison.Ordinal))
            {
                return Inconsistent(verdict, debate, "su apertura juzga otros requisitos");
            }

            if (opened.Payload.SnapshotCommit is { } snapshot && !string.Equals(snapshot, _commit, StringComparison.OrdinalIgnoreCase))
            {
                return Inconsistent(verdict, debate, $"su copia es del commit {Short(snapshot)}, no de la integración {Short(_commit)} de la ejecución");
            }

            if (!_closed.TryGetValue(debate, out var closures))
            {
                return new State(StateKind.Pending, PendingReason(verdict, debate));
            }

            var closed = closures[^1].Payload;
            var closedAt = closures[^1].Entry.Sequence;
            if (string.Equals(closed.Rule, JudgingPanel.User, StringComparison.Ordinal))
            {
                return new State(
                    StateKind.DecidedByUser,
                    $"La revisión (debate {debate}) de la impugnación del veredicto {verdict} la decidiste tú («Decidir yo»): una decisión tuya no es la aprobación "
                    + "de un juez independiente, así que no se entrega. Vuelve a impugnar el veredicto para otra revisión o lanza una ejecución de seguimiento.");
            }

            if (closures.Count > 1)
            {
                return Inconsistent(verdict, debate, "el debate se cerró más de una vez");
            }

            if (closed.Outcome == DebateOutcome.Aborted || string.Equals(closed.Rule, DebateClosedPayload.AbortedRule, StringComparison.Ordinal))
            {
                return new State(
                    StateKind.Aborted,
                    $"La revisión (debate {debate}) de la impugnación del veredicto {verdict} se abortó sin juicio: la impugnación sigue abierta y no se entrega. Vuelve a impugnar el veredicto.");
            }

            if (closed.Outcome != DebateOutcome.Resolved || closed.Rule is not (JudgingPanel.Single or JudgingPanel.Agree))
            {
                return new State(
                    StateKind.NeedsPerson,
                    $"La revisión (debate {debate}) de la impugnación del veredicto {verdict} necesita a una persona: ni un veredicto sin resolver ni una decisión "
                    + "tuya son la aprobación de un juez independiente, así que no se entrega. Vuelve a impugnar el veredicto para otra revisión o lanza una ejecución de seguimiento.");
            }

            if (closed.Verdicts.Count == 0)
            {
                return Inconsistent(verdict, debate, "su cierre no nombra ningún veredicto");
            }

            var announced = new List<(DecisionEntry Entry, VerdictIssuedPayload Payload)>();
            foreach (var id in closed.Verdicts)
            {
                var issued = _issued.LastOrDefault(v => string.Equals(v.Payload.VerdictId, id, StringComparison.Ordinal));
                if (issued.Payload is null)
                {
                    return Inconsistent(verdict, debate, $"el veredicto {id} no está en el registro de la ejecución {_run}");
                }

                if (issued.Entry.Sequence <= entry.Sequence)
                {
                    return Inconsistent(verdict, debate, $"el veredicto {id} es anterior a la impugnación");
                }

                if (!issued.Payload.Valid)
                {
                    return Inconsistent(verdict, debate, $"el veredicto {id} no es válido");
                }

                if (!string.Equals(issued.Payload.RequirementSetHash, _requirements, StringComparison.Ordinal))
                {
                    return Inconsistent(verdict, debate, $"el veredicto {id} juzga otros requisitos");
                }

                if (!Revises(issued.Payload, verdict, entry.Sequence))
                {
                    return Inconsistent(verdict, debate, $"el veredicto {id} no sustituye al impugnado");
                }

                var kept = _stored.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));
                if (kept is not { Valid: true }
                    || !string.Equals(kept.RunId, _run, StringComparison.Ordinal)
                    || kept.Verdict.Decision != issued.Payload.Decision
                    || !string.Equals(kept.Supersedes, issued.Payload.Supersedes, StringComparison.Ordinal))
                {
                    return Inconsistent(verdict, debate, $"el almacén de veredictos no confirma el veredicto {id}");
                }

                announced.Add(issued);
            }

            var decisions = announced.Select(a => a.Payload.Decision).Distinct().ToList();
            if (decisions is not [VerdictDecision.Approved or VerdictDecision.ChangesRequested or VerdictDecision.Rejected])
            {
                return Inconsistent(verdict, debate, "sus veredictos no dicen lo mismo, o no deciden");
            }

            var after = Math.Max(closedAt, announced.Max(a => a.Entry.Sequence));
            return new State(StateKind.Judged, null, new Revision(verdict, debate, decisions[0], [.. closed.Verdicts], Accepted(debate, after)));
        }

        /// <summary>Whether <paramref name="verdict"/> supersedes <paramref name="challenged"/>, directly or through the attempts of its judge announced after the challenge.</summary>
        private bool Revises(VerdictIssuedPayload verdict, string challenged, long challengedAt)
        {
            var current = verdict.Supersedes;
            for (var step = 0; step < MaxCorrections && current is not null; step++)
            {
                if (string.Equals(current, challenged, StringComparison.Ordinal))
                {
                    return true;
                }

                var attempt = _issued.LastOrDefault(v => string.Equals(v.Payload.VerdictId, current, StringComparison.Ordinal));
                if (attempt.Payload is null || attempt.Entry.Sequence <= challengedAt)
                {
                    return false;
                }

                current = attempt.Payload.Supersedes;
            }

            return false;
        }

        /// <summary>
        /// Whether a person accepted the revision of <paramref name="debate"/>: a yes asked after <paramref name="after"/> (its
        /// closure and its verdicts), in the debate itself, or in the run when this is the revision that closed last before
        /// it was asked.
        /// </summary>
        private bool Accepted(string debate, long after) =>
            _acceptances.Any(a => a.Requested > after
                && (string.Equals(a.Unit, debate, StringComparison.Ordinal)
                    || (string.Equals(a.Unit, _run, StringComparison.Ordinal) && string.Equals(ClosedLastBefore(a.Requested), debate, StringComparison.Ordinal))));

        private string? ClosedLastBefore(long sequence) =>
            _closed
                .SelectMany(c => c.Value.Select(x => (Debate: c.Key, x.Entry.Sequence)))
                .Where(c => c.Sequence < sequence)
                .OrderByDescending(c => c.Sequence)
                .Select(c => c.Debate)
                .FirstOrDefault();

        private static string PendingReason(string verdict, string debate) =>
            $"El veredicto {verdict} está impugnado y su revisión (debate {debate}) no ha terminado: no se entrega hasta que se cierre.";

        private static State Inconsistent(string verdict, string debate, string detail) =>
            new(
                StateKind.Inconsistent,
                $"La revisión (debate {debate}) de la impugnación del veredicto {verdict} no cuadra con el registro de decisiones o con el almacén de veredictos ({detail}): no se entrega.");

        private static string DecisionText(VerdictDecision decision) => decision switch
        {
            VerdictDecision.Approved => "aprobado",
            VerdictDecision.ChangesRequested => "pide cambios",
            VerdictDecision.Rejected => "rechazado",
            VerdictDecision.EscalateToHuman => "lo deja en manos de una persona",
            VerdictDecision.Unresolved => "sin resolver",
            _ => decision.ToString(),
        };

        /// <summary>A revision a judge or a panel decided: the challenged verdict, its debate, its decision, its verdicts and whether a person accepted it.</summary>
        private sealed record Revision(string Challenged, string Debate, VerdictDecision Decision, IReadOnlyList<string> Verdicts, bool Accepted);

        /// <summary>Where a challenge stands; <see cref="Reason"/> says why it blocks, for the person.</summary>
        private sealed record State(StateKind Kind, string? Reason, Revision? Revision = null);

        /// <summary>
        /// A judgment of the chain: its verdicts, whether they approve, whether that approval counts (the original's, a
        /// confirmation of one that counted, or one a person accepted), the revision that gave it (null for the verdict the
        /// run ended with), and whether the judgment it revises approved and whether that approval counted.
        /// </summary>
        private sealed record Node(IReadOnlyList<string> Verdicts, bool Approved, bool Trusted, Revision? Via, bool ParentApproved, bool ParentCounted);
    }

    /// <summary>What the challenges leave of a delivery: why it is blocked, or a note on the revision it stands on (null for none).</summary>
    private sealed record Standing(string? Blocked, string? Note)
    {
        public static Standing Block(string reason) => new(reason, null);
    }

    private static DeliveryReadiness NotReady(string reason) => new(false, null, reason);

    private static string TargetRef(string branch) => branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;

    private async Task<bool> ContainsAsync(string repoRoot, string head, string commit, CancellationToken cancellationToken) =>
        string.Equals(head, commit, StringComparison.OrdinalIgnoreCase)
        || await _worktrees.IsAncestorAsync(repoRoot, commit, head, cancellationToken).ConfigureAwait(false);

    private static string Refused(string target, IReadOnlyList<DeliveryRefusal> refusals) =>
        "Tu repositorio tiene configurado un programa que controla el propio repositorio o un worktree, y la entrega se "
        + "ejecuta como cualquier comando interno, así que lo rechaza: "
        + string.Join(", ", refusals.Select(r => $"«{r.Key}»{(string.IsNullOrEmpty(r.Reason) ? string.Empty : $" ({r.Reason})")}"))
        + $". No se ha pedido ninguna aprobación y {target} no se ha tocado. Revisa el cambio y fusiónalo a mano.";

    private static DeliveryResult Changed(string target, string? branch, string commit) =>
        new(
            false,
            null,
            target,
            $"Tu repositorio cambió mientras se esperaba la aprobación (HEAD está en {branch ?? "otra rama o en ninguna"}, {Short(commit)}): "
            + "no se ha entregado nada. Vuelve a pedir la entrega.");

    private static string DescribeRequest(
        RunId run,
        DeliveryMode mode,
        string target,
        RepositoryState state,
        string integration,
        int changedPaths)
    {
        var runBranch = IntegrationNames.BranchName(run);
        var manual = mode == DeliveryMode.FastForwardOnly
            ? $"git merge --ff-only {integration}"
            : $"git merge --no-ff {integration}";
        var description = mode == DeliveryMode.FastForwardOnly
            ? $"Entregar la ejecución {run.Value}: avanzar tu rama {target} de {Short(state.HeadCommit)} a {Short(integration)} ({runBranch}) con --ff-only."
            : $"Entregar la ejecución {run.Value}: fusionar {runBranch} ({Short(integration)}) en tu rama {target} ({Short(state.HeadCommit)}) con un commit de fusión.";
        description += " " + ChangedFiles(changedPaths);

        // R3-05: the delivery runs like any internal command. Say so, and give the exact command the user can run by
        // hand after reviewing, if they want their hooks or their signature.
        description += " La entrega NO ejecuta tus hooks ni firma el commit (corre como cualquier comando interno de Agent Studio)."
            + $" Si tras revisar quieres tus hooks o tu firma, fusiona a mano en {target}: {manual}.";

        if (state.HasUncommittedChanges)
        {
            description += " Tu árbol de trabajo tiene cambios sin confirmar: si alguno está en una ruta que cambia la entrega, no se entregará nada.";
        }

        return description;
    }

    /// <summary>How many files the delivery changes, in words: «Cambia 1 fichero.», «Cambian 3 ficheros.» or «No cambia ningún fichero.».</summary>
    private static string ChangedFiles(int count) => count switch
    {
        <= 0 => "No cambia ningún fichero.",
        1 => "Cambia 1 fichero.",
        _ => $"Cambian {count} ficheros.",
    };

    private static string MergeMessage(RunId run, string integration) =>
        $"Agent Studio: entrega de la ejecución {run.Value}\n\nIntegra {IntegrationNames.BranchName(run)} ({integration}).\n";

    private static string Short(string commit) => commit.Length > 10 ? commit[..10] : commit;
}
