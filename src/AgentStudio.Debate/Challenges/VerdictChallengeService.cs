using System.Text;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Debate.Challenges;

/// <summary>
/// «Impugnar» (docs/15 D-38 to D-40, ADR-0032): a stored verdict of a run, a debate or an audit is revised by an adversarial
/// debate whose proponent defends the verdict and whose critic defends the challenge, which starts from the user's reasons.
/// The revision inherits who may not judge the object (it changes the judge, never who may judge) and is judged like the
/// object: a jury's verdict by a jury.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="PrepareAsync"/> writes nothing. It adjusts the revision the card asks for: adversarial, about the verdict,
/// on the requirements of the challenged verdict, the reasons as the question (so round 1 starts from them), and the judge
/// or the panel chosen with <see cref="DebateJudgingRules.Possible"/> over the judges of the template, never on a vendor the
/// object excludes, the agent that issued the verdict last (another model first). The coordinator of debates prepares
/// it, and a proposal that does not inherit the excluded vendors gets a problem, so it never opens.</item>
/// <item><see cref="ChallengeAsync"/> opens the revision through the coordinator (its approval <c>debate.open</c>, the project's
/// activity and <c>DebateOpened</c>, which names the verdict). Then the reasons are captured as a <c>HumanNote</c> of the
/// person who approved and announced in the revision debate (<c>EvidenceRegistered</c> version 2 under its id), so its judge
/// may cite them; and <c>VerdictChallenged</c>, in the unit of the verdict, anchors them and names the revision. The id of the
/// revision exists only once it opened, so <c>VerdictChallenged</c> comes after <c>DebateOpened</c>; a debate opened against a
/// verdict counts as its challenge anyway (<see cref="ChallengeLog"/>).</item>
/// <item>The verdicts of the revision are kept by the judgment of debates, in the unit of the challenged verdict and
/// superseding it (D-40); this service writes none.</item>
/// <item><see cref="AcceptRevisionAsync"/> asks the person to accept a revision that turned towards an approval
/// (<see cref="ChallengeLog.RevisionAcceptanceAction"/>), in the revision debate and bound to its verdict.</item>
/// </list>
/// The texts the user reads (problems, notes, approvals) are in Spanish.
/// </remarks>
public sealed class VerdictChallengeService(
    IDecisionRecorder decisions,
    IVerdictStore verdicts,
    IRequirementStore requirements,
    IEvidenceStore evidence,
    IAgentRoster roster,
    IDebateCoordinator coordinator,
    IApprovalGate approvals) : IVerdictChallenges
{
    private static readonly Actor SystemActor = new(ActorKind.System, Judging.IndependentReview.SystemId);

    public async Task<DebateProposal> PrepareAsync(VerdictChallengeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Revision);
        var project = request.Project;
        var problems = new CodedMessages();
        var notes = new CodedMessages();

        // A critical moment (ADR-0010, addendum of 2026-11-04): who may judge the revision comes from the whole log.
        var log = await decisions.LoadAsync(project.ProjectId, LogLoad.Full, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            problems.Add($"El registro de decisiones no está íntegro: no se impugna sobre un registro que no admite entradas ({log.Verification.Problem}).", UserText.Of(DebateUserTexts.ChallengeProblemLogBroken));
        }

        if (string.IsNullOrWhiteSpace(request.Reasons))
        {
            problems.Add("Escribe tus razones: la ronda 1 de la revisión parte de ellas.", UserText.Of(DebateUserTexts.ChallengeProblemReasonsMissing));
        }

        var challenged = await FindAsync(project.ProjectId, request.Verdict, ct).ConfigureAwait(false);
        var frozen = request.Revision.Requirements;
        ChallengedObject? target = null;
        if (challenged?.RunId is not { } unit)
        {
            problems.Add($"El veredicto {request.Verdict} no está guardado, o no es de ninguna ejecución, debate ni auditoría.", UserText.Of(DebateUserTexts.ChallengeProblemVerdictNotFound, request.Verdict));
        }
        else
        {
            if (!Announced(log.Entries, challenged))
            {
                problems.Add($"El registro no anuncia el veredicto {challenged.Id} en {unit} (VerdictIssued): no se impugna lo que el registro no recoge.", UserText.Of(DebateUserTexts.ChallengeProblemNotAnnounced, challenged.Id, unit));
            }

            target = ChallengeLog.ObjectOf(log.Entries, unit, challenged.Id);
            if (target.Kind == ChallengedUnitKind.Unknown)
            {
                problems.Add($"El registro no tiene el inicio de {unit}, el objeto del veredicto: no se sabe quién no puede juzgar la revisión.", UserText.Of(DebateUserTexts.ChallengeProblemUnitNotStarted, unit));
            }

            if (ChallengeLog.StatusOf(log.Entries, challenged) == ChallengeStatus.Pending)
            {
                problems.Add($"El veredicto {challenged.Id} ya tiene una impugnación sin cerrar (debate {ChallengeLog.LatestRevision(log.Entries, challenged.Id)}): espera a que termine.", UserText.Of(DebateUserTexts.ChallengeProblemAlreadyChallenged, challenged.Id, ChallengeLog.LatestRevision(log.Entries, challenged.Id) ?? "?"));
            }

            var own = await requirements.FindAsync(project.ProjectId, challenged.Verdict.RequirementSetHash, ct).ConfigureAwait(false);
            if (own is null)
            {
                problems.Add($"Los requisitos congelados {challenged.Verdict.RequirementSetHash} del veredicto impugnado no están guardados: la revisión se juzga con ellos.", UserText.Of(DebateUserTexts.ChallengeProblemRequirementsMissing, challenged.Verdict.RequirementSetHash));
            }
            else
            {
                frozen = own;
            }
        }

        var excluded = target?.ExcludedVendors ?? new HashSet<VendorId>();
        var (judging, judges) = await ChooseJudgesAsync(request, challenged, target, excluded, notes, ct).ConfigureAwait(false);
        var revision = request.Revision with
        {
            Mode = DebateMode.Adversarial,
            Question = request.Reasons ?? string.Empty,
            Requirements = frozen,
            Subject = new DebateSubject(DebateSubjectKind.Verdict, request.Verdict),
            Judges = judges,
            Judging = judging,
        };

        var proposal = await coordinator.PrepareAsync(revision, ct).ConfigureAwait(false);
        foreach (var (message, text) in Inheritance(proposal, excluded, target))
        {
            problems.Add(message, text);
        }

        if (challenged is not null && proposal.Judging.Judges.Any(j => j.Agent == challenged.Verdict.Judge.Agent))
        {
            notes.Add($"Juzga el mismo agente que emitió el veredicto impugnado ({challenged.Verdict.Judge.Agent}), en una sesión nueva: la plantilla no tiene otro juez elegible.", UserText.Of(DebateUserTexts.ChallengeNoteSameJudge, challenged.Verdict.Judge.Agent.ToString()));
        }

        // The codes go one for one with the sentences; when the proposal came without them, none is given (the sentences are shown).
        var coded = proposal.ProblemTexts is { } givenProblems && givenProblems.Count == proposal.Problems.Count;
        var merged = new CodedMessages();
        for (var i = 0; i < proposal.Problems.Count; i++)
        {
            merged.Add(proposal.Problems[i], coded ? proposal.ProblemTexts![i] : UserText.Of(DebateUserTexts.ProblemSessionRefused, proposal.Problems[i]));
        }

        for (var i = 0; i < problems.Count; i++)
        {
            if (!proposal.Problems.Contains(problems.Messages[i]))
            {
                merged.Add(problems.Messages[i], problems.Texts[i]);
            }
        }

        var codedNotes = proposal.Estimate.NoteTexts is { } ownNotes && ownNotes.Count == proposal.Estimate.Notes.Count;
        var mergedNotes = new CodedMessages();
        for (var i = 0; i < proposal.Estimate.Notes.Count; i++)
        {
            mergedNotes.Add(proposal.Estimate.Notes[i], codedNotes ? proposal.Estimate.NoteTexts![i] : UserText.Of(DebateUserTexts.ProblemSessionRefused, proposal.Estimate.Notes[i]));
        }

        for (var i = 0; i < notes.Count; i++)
        {
            mergedNotes.Add(notes.Messages[i], notes.Texts[i]);
        }

        return proposal with
        {
            Problems = merged.Messages,
            ProblemTexts = coded ? merged.Texts : null,
            Estimate = proposal.Estimate with { Notes = mergedNotes.Messages, NoteTexts = codedNotes ? mergedNotes.Texts : null },
        };
    }

    public async Task<IDebateRun> ChallengeAsync(DebateProposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(proposal.Request);
        var request = proposal.Request;
        if (request.Mode != DebateMode.Adversarial || request.Subject is not { Kind: DebateSubjectKind.Verdict, Reference: { Length: > 0 } verdict })
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.ChallengeNotRevision, "This proposal is not the revision of a verdict: prepare the challenge with the challenge service.");
        }

        if (proposal.Problems.Count > 0)
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.ChallengeCannotOpen, "The challenge cannot be opened: " + string.Join(" ", proposal.Problems));
        }

        // What was shown is prepared again: the verdict, its object and who may judge it must be as they were.
        var project = request.Project;
        var again = await PrepareAsync(new VerdictChallengeRequest(project, verdict, request.Question, request), ct).ConfigureAwait(false);
        if (again.Problems.Count > 0 || !string.Equals(again.HeaderHash, proposal.HeaderHash, StringComparison.Ordinal))
        {
            throw DebateUserTexts.Refusal(
                DebateUserTexts.ChallengeProposalOutdated,
                "What the challenge would open now is not what was shown (the verdict, its object or who may judge it changed): prepare it again. " + string.Join(" ", again.Problems));
        }

        var challenged = await FindAsync(project.ProjectId, verdict, ct).ConfigureAwait(false)
            ?? throw DebateUserTexts.Refusal(DebateUserTexts.ChallengeVerdictNotStored, $"The verdict {verdict} is not stored.", verdict);
        var unit = challenged.RunId!;
        var revision = await coordinator.OpenAsync(proposal, ct).ConfigureAwait(false);
        try
        {
            await RecordChallengeAsync(project, challenged, unit, revision.Id, request.Question).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A revision whose challenge is not anchored is not left running: it is aborted, and the reason said.
            await revision.AbortAsync($"No se pudo anclar la impugnación de {verdict} en {unit}: {ex.Message}").ConfigureAwait(false);
            throw;
        }

        return revision;
    }

    public async Task<ChallengeStatus> StatusAsync(ProjectContext project, string verdict, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(verdict);
        var log = await VerifiedAsync(project.ProjectId, LogLoad.Incremental, ct).ConfigureAwait(false);
        var challenged = await FindAsync(project.ProjectId, verdict, ct).ConfigureAwait(false);
        if (challenged is null)
        {
            // A verdict the store does not hold is challenged only if the log says so, and then nothing can be compared.
            return ChallengeLog.LatestRevision(log, verdict) is null ? ChallengeStatus.None : ChallengeStatus.Pending;
        }

        return ChallengeLog.StatusOf(log, challenged);
    }

    public async Task AcceptRevisionAsync(ProjectContext project, string revisionVerdict, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionVerdict);

        // Accepting a revision resolves the challenge: a critical moment, on the whole log.
        var log = await VerifiedAsync(project.ProjectId, LogLoad.Full, ct).ConfigureAwait(false);
        if (ChallengeLog.RevisionOf(log, revisionVerdict) is not var (debate, challengedId))
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.ChallengeNothingToAccept, $"The verdict {revisionVerdict} did not close the revision of a challenged verdict: there is nothing to accept.", revisionVerdict);
        }

        var challenged = await FindAsync(project.ProjectId, challengedId, ct).ConfigureAwait(false)
            ?? throw DebateUserTexts.Refusal(DebateUserTexts.ChallengeVerdictNotStored, $"The challenged verdict {challengedId} is not stored.", challengedId);
        var status = ChallengeLog.StatusOf(log, challenged);
        if (status != ChallengeStatus.AwaitingAcceptance || !string.Equals(ChallengeLog.LatestRevision(log, challengedId), debate, StringComparison.Ordinal))
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.ChallengeNotAwaitingAcceptance, $"The revision {debate} of {challengedId} does not await acceptance ({status}): only the latest revision that turned towards an approval is accepted.", debate, challengedId);
        }

        var issued = log.LastOrDefault(e => e.Kind == DecisionKind.VerdictIssued
            && AuditedRun.Parse(e).Payload is VerdictIssuedPayload v && string.Equals(v.VerdictId, revisionVerdict, StringComparison.Ordinal));
        var binding = ContentHash.Sha256Hex(ContentHash.Canonical("verdict-revision-accept/1", project.ProjectId, challengedId, debate, revisionVerdict, issued?.Hash));
        var decision = await approvals.RequestAsync(
            new ApprovalRequest(
                ChallengeLog.RevisionAcceptanceAction,
                $"Aceptar la revisión {debate}: su veredicto {revisionVerdict} da la vuelta hacia la aprobación del veredicto impugnado {challengedId}. Hasta que la aceptes no cuenta.",
                RiskLevel.Critical,
                null,
                [],
                project.ProjectId,
                debate,
                Binding: binding),
            ct).ConfigureAwait(false);
        if (decision.Approved && !string.Equals(decision.Answers, binding, StringComparison.Ordinal))
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.ChallengeAcceptanceNotAnswered, "The approval received does not answer the acceptance of this revision.");
        }
    }

    /// <summary>
    /// The reasons as a note of the person who approved the opening, announced in the revision debate (a row with the id of
    /// the debate that the debate does not announce would block its approval), then <c>VerdictChallenged</c> in the unit of
    /// the verdict, which anchors them by id and SHA-256.
    /// </summary>
    private async Task RecordChallengeAsync(ProjectContext project, StoredVerdict challenged, string unit, RunId revision, string reasons)
    {
        var projectId = project.ProjectId;
        var log = await decisions.LoadAsync(projectId, LogLoad.Full, CancellationToken.None).ConfigureAwait(false);
        var person = ChallengeLog.OpenedBy(log.Entries, revision.Value);
        var challengedBy = person ?? Judging.IndependentReview.SystemId;
        EvidenceRecord note;
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes(reasons), writable: false))
        {
            note = await evidence.CaptureAsync(
                projectId,
                new EvidenceDraft(EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman, $"Razones de la impugnación del veredicto {challenged.Id}", challengedBy, RunId: revision.Value, MediaType: "text/plain"),
                content,
                CancellationToken.None).ConfigureAwait(false);
        }

        await decisions.RecordAsync(projectId, SystemActor, DecisionKind.EvidenceRegistered, $"Evidencia {note.Id}: {note.Title}",
            EvidenceRegisteredPayload.Anchoring(note, revision.Value, null, null), cancellationToken: CancellationToken.None).ConfigureAwait(false);

        // The person who approved the opening challenges; without one in the log (a gate that does not record) the system signs it.
        await decisions.RecordAsync(
            projectId,
            person is null ? SystemActor : new Actor(ActorKind.User, person),
            DecisionKind.VerdictChallenged,
            $"Veredicto {challenged.Id} impugnado: lo revisa el debate {revision}",
            new VerdictChallengedPayload(challenged.Id, challengedBy, note.Id, note.ContentSha256, revision.Value) { RunId = unit },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The disposition of the revision and its judges (D-39): a panel when the challenged verdict came from one, otherwise an
    /// independent judge and, when none fits, the judge of the family of the object (a non-producer judge for work that was
    /// reviewed, a panel for a debate that chose). The judges come from the template (or those the card offers), never on a
    /// vendor the object excludes, the agent that issued the challenged verdict last and another model first.
    /// </summary>
    private async Task<(DebateJudgingKind Judging, IReadOnlyList<AgentId> Judges)> ChooseJudgesAsync(
        VerdictChallengeRequest request,
        StoredVerdict? challenged,
        ChallengedObject? target,
        IReadOnlySet<VendorId> excluded,
        CodedMessages notes,
        CancellationToken ct)
    {
        var template = await roster.ListAsync(request.Project.ProjectId, ct).ConfigureAwait(false);
        var offered = request.Revision.Judges.Count > 0 ? request.Revision.Judges.ToHashSet() : null;
        var original = challenged?.Verdict.Judge;
        var candidates = template
            .Where(a => a.Role is AgentRole.Judge or AgentRole.Auditor && a.Validate().Count == 0)
            .Where(a => offered is null || offered.Contains(a.Id))
            .Where(a => !excluded.Contains(a.Model.Vendor))
            .Select((a, order) => (Agent: a, Order: order))
            .OrderBy(c => original is not null && c.Agent.Id == original.Agent ? 2 : target?.Judges.Contains(c.Agent.Id) == true ? 1 : 0)
            .ThenBy(c => original is not null && string.Equals(c.Agent.Model.Model, original.Model.Model, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(c => c.Order)
            .Select(c => new JudgeIdentity(c.Agent.Id, c.Agent.Model))
            .ToList();
        var participants = request.Revision.Participants
            .Select(p => template.FirstOrDefault(a => a.Id == p.Agent) is { } agent ? new DebateParticipant(agent.Id, p.Role, agent.Model) : null)
            .OfType<DebateParticipant>()
            .ToList();

        DebateJudgingKind[] kinds = target switch
        {
            { FromJury: true } => [DebateJudgingKind.CrossVendorPanel],
            { Choice: true } => [DebateJudgingKind.IndependentJudge, DebateJudgingKind.CrossVendorPanel],
            _ => [DebateJudgingKind.IndependentJudge, DebateJudgingKind.NonProducerJudge],
        };
        var possible = DebateJudgingRules.Possible(DebateMode.Adversarial, DebateSubjectKind.Verdict, participants, excluded, candidates);
        foreach (var kind in kinds)
        {
            if (possible.FirstOrDefault(a => a.Kind == kind) is { } arrangement)
            {
                return (kind, [.. arrangement.Judges.Select(j => j.Agent)]);
            }
        }

        notes.Add("Con estos participantes y jueces nadie puede juzgar la revisión sin juzgar el trabajo de su propio vendor.", UserText.Of(DebateUserTexts.ChallengeNoteNobodyCanJudge));
        return (kinds[0], [.. candidates.Select(c => c.Agent)]);
    }

    /// <summary>What a proposal must keep of the object (D-39): every vendor it excludes, no judge on one of them and, for a jury's verdict, a jury.</summary>
    private static IEnumerable<(string Message, UserText Text)> Inheritance(DebateProposal proposal, IReadOnlySet<VendorId> excluded, ChallengedObject? target)
    {
        var producers = proposal.Judging.ProducerVendors ?? new HashSet<VendorId>();
        var missing = excluded.Where(v => !producers.Contains(v)).Select(v => v.Value).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            yield return (
                $"La revisión no hereda los vendors que el objeto del veredicto excluye ({string.Join(", ", missing)}): con ella podría juzgar un vendor que produjo el trabajo.",
                UserText.Of(DebateUserTexts.ChallengeProblemNotInherited, string.Join(", ", missing)));
        }

        var dependent = proposal.Judging.Judges.Where(j => excluded.Contains(j.Model.Vendor)).Select(j => $"{j.Agent} ({j.Model.Vendor})").ToList();
        if (dependent.Count > 0)
        {
            yield return (
                $"Un juez de la revisión corre en un vendor que el objeto excluye: {string.Join(", ", dependent)}.",
                UserText.Of(DebateUserTexts.ChallengeProblemJudgeOnExcluded, string.Join(", ", dependent)));
        }

        if (target is { FromJury: true } && proposal.Judging.Kind != DebateJudgingKind.CrossVendorPanel)
        {
            yield return ("El veredicto impugnado es de un jurado cruzado: su revisión se juzga con jurado cruzado.", UserText.Of(DebateUserTexts.ChallengeProblemJuryNeeded));
        }
    }

    private static bool Announced(IReadOnlyList<DecisionEntry> log, StoredVerdict verdict) =>
        log.Any(e => e.Kind == DecisionKind.VerdictIssued
            && AuditedRun.Parse(e).Payload is VerdictIssuedPayload issued
            && string.Equals(issued.VerdictId, verdict.Id, StringComparison.Ordinal)
            && string.Equals(issued.RunId, verdict.RunId, StringComparison.Ordinal));

    private async Task<StoredVerdict?> FindAsync(string projectId, string verdict, CancellationToken ct) =>
        (await verdicts.ListAsync(projectId, null, ct).ConfigureAwait(false)).LastOrDefault(v => string.Equals(v.Id, verdict, StringComparison.Ordinal));

    /// <summary>The verified log: whole (<see cref="LogLoad.Full"/>) to accept a revision, incremental to say where a challenge stands.</summary>
    private async Task<IReadOnlyList<DecisionEntry>> VerifiedAsync(string projectId, LogLoad load, CancellationToken ct)
    {
        var log = await decisions.LoadAsync(projectId, load, ct).ConfigureAwait(false);
        return log.Verification.IsValid ? log.Entries : throw new DecisionLogIntegrityException(projectId, log.Verification);
    }
}
