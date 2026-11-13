using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using static System.FormattableString;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// Runs the debates of the arena (docs/15 D-01 to D-22): prepares what «Abrir debate» shows without writing anything, opens a
/// debate after the user's approval, resumes an interrupted one, and lists and reads them by replaying the log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Prepare</b> (D-06): the participants and judges of the template, the subject (HEAD of the user's repository, a
/// finished run or a stored verdict), the vendors that produced it, the arrangement the rules allow
/// (<see cref="DebateJudgingRules"/>), a dry run of the domain, the launches allowed, the estimate and the hash of what is
/// shown (<see cref="DebateHeaderHash.Proposal(DebateRecord, string)"/>), which the approval binds.</item>
/// <item><b>Open</b>: the project's activity is entered before anything is written (D-05), the proposal is prepared again and
/// must give the same hash (nothing changed since it was shown), the approval (<see cref="OpenAction"/>) must answer that
/// hash, and then, in the order of a run: the question as evidence, the immutable header, the copy of the commit and
/// <c>DebateOpened</c>, which anchors the hash of the header and of the proposal. A debate whose opening is denied has no
/// header and no <c>DebateOpened</c>, so it is never listed.</item>
/// <item><b>Resume</b> (D-04): the log verifies, the stored header gives the hash <c>DebateOpened</c> anchored, the replay is
/// clean, the user approves (<see cref="ResumeAction"/>), every open launch is closed as interrupted, the copy is extracted
/// again if it is gone, and the round in course starts again for whoever had not published in it.</item>
/// </list>
/// </remarks>
public sealed class DebateCoordinator : IDebateCoordinator
{
    /// <summary>The action of the approval of a debate (D-06).</summary>
    public const string OpenAction = "debate.open";

    /// <summary>The action of the approval of resuming a debate (D-04).</summary>
    public const string ResumeAction = "debate.resume";

    /// <summary>The id the proposal is hashed with: what only exists once the debate opens is never part of what is approved.</summary>
    private static readonly RunId ProposalId = new("deb-00000000-000000");

    /// <summary>What a turn of each CLI is expected to spend at most, from what was measured (D-21).</summary>
    private static readonly IReadOnlyDictionary<ProviderId, long> TurnTokens = new Dictionary<ProviderId, long>
    {
        [ProviderId.ClaudeCode] = 160_000,
        [ProviderId.CodexCli] = 80_000,
    };

    private readonly DebateArenaPorts _ports;
    private readonly ConcurrentDictionary<string, DebateRun> _live = new(StringComparer.Ordinal);

    public DebateCoordinator(DebateArenaPorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        _ports = ports;
    }

    public async Task<DebateProposal> PrepareAsync(DebateRequest request, CancellationToken ct) =>
        (await PlanAsync(request, ct).ConfigureAwait(false)).Proposal;

    /// <exception cref="InvalidOperationException">The proposal has problems, the project is busy, what was approved changed, or the user did not approve it.</exception>
    /// <exception cref="DecisionLogIntegrityException">The decision log does not verify: nothing is written.</exception>
    public async Task<IDebateRun> OpenAsync(DebateProposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Problems.Count > 0)
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.CannotOpen, "The debate cannot be opened: " + string.Join(" ", proposal.Problems));
        }

        var project = proposal.Request.Project;
        var lease = await EnterAsync(project, ct).ConfigureAwait(false);
        try
        {
            var plan = await PlanAsync(proposal.Request, ct).ConfigureAwait(false);
            if (plan.Proposal.Problems.Count > 0 || !string.Equals(plan.Proposal.HeaderHash, proposal.HeaderHash, StringComparison.Ordinal))
            {
                throw DebateUserTexts.Refusal(
                    DebateUserTexts.ProposalOutdated,
                    "What you were shown to approve is no longer what there is (the template, the commit or the subject changed): prepare the debate again. "
                    + string.Join(" ", plan.Proposal.Problems));
            }

            var log = await LoadVerifiedAsync(project, ct).ConfigureAwait(false);
            var taken = RunIdsOf(log.Entries);
            foreach (var existing in await _ports.Debates.ListAsync(project.ProjectId, ct).ConfigureAwait(false))
            {
                taken.Add(existing.Id.Value);
            }

            var debate = DebateIds.New(_ports.Time, taken.Contains);
            var recorder = new DebateTurnRecorder(_ports.Decisions, _ports.Evidence, project.ProjectId, debate);
            var guard = new DebateRepositoryGuard(_ports.Worktrees, project.RootPath, recorder);
            await guard.EstablishBaselineAsync(ct).ConfigureAwait(false);

            var approval = await _ports.Approvals.RequestAsync(
                new ApprovalRequest(OpenAction, plan.Proposal.ApprovalText, RiskLevel.Critical, null, [], project.ProjectId, debate.Value, null, plan.Proposal.HeaderHash),
                ct).ConfigureAwait(false);
            if (approval is not { Approved: true } || !string.Equals(approval.Answers, plan.Proposal.HeaderHash, StringComparison.Ordinal))
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.OpeningNotApproved, "The opening of the debate was not approved: it is not opened.");
            }

            var request = plan.Proposal.Request;
            var question = await recorder.CaptureRegisteredAsync(
                EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, "Pregunta del debate", approval.DecidedBy, Encoding.UTF8.GetBytes(request.Question), "text/plain", null, null, null, ct).ConfigureAwait(false);
            if (await _ports.Requirements.FindAsync(project.ProjectId, request.Requirements.ContentHash, ct).ConfigureAwait(false) is null)
            {
                await _ports.Requirements.SaveAsync(project.ProjectId, request.Requirements, ct).ConfigureAwait(false);
            }

            var folders = new DebateFolders(_ports.Options, project.RootPath, debate);
            if (plan.Record.SnapshotCommit is { } commit)
            {
                await folders.ExtractSnapshotAsync(_ports.Worktrees, project.RootPath, commit, ct).ConfigureAwait(false);
            }

            var createdAt = _ports.Time.GetUtcNow();
            await _ports.Debates.CreateAsync(plan.Record with { Id = debate, ProjectId = project.ProjectId, CreatedAt = createdAt, Question = question.Id }, ct).ConfigureAwait(false);

            // Once the header is stored the opening is not left half done: DebateOpened anchors the header as the registry
            // gives it back, and the proposal the user approved is checked on that same header with the hash of the question
            // captured, as the dossier of the judgment recomputes both.
            var record = await _ports.Debates.FindAsync(project.ProjectId, debate, CancellationToken.None).ConfigureAwait(false)
                ?? throw DebateUserTexts.Refusal(DebateUserTexts.OpeningInconsistent, $"The debate registry does not return the header of {debate} it has just saved: it is not opened.");
            var approved = DebateHeaderHash.Proposal(record, question.ContentSha256);
            if (!string.Equals(approved, proposal.HeaderHash, StringComparison.Ordinal))
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.OpeningInconsistent, $"The header the debate registry saved is not the proposal that was approved (its hash is {approved}, not {proposal.HeaderHash}): the debate is not opened.");
            }

            var opened = Opened(record, plan, approved, question);
            await recorder.RecordAsync(DecisionKind.DebateOpened, $"Abre el debate {debate} ({request.Mode})", opened, CancellationToken.None).ConfigureAwait(false);
            if (plan.Challenged is { } challenged)
            {
                await recorder.CaptureRegisteredAsync(
                    EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, $"Veredicto impugnado {challenged.Id}", challenged.Verdict.Judge.Agent.Value,
                    Encoding.UTF8.GetBytes(VerdictJson.ToJson(challenged.Verdict)), "application/json", null, null, null, CancellationToken.None).ConfigureAwait(false);
            }

            var session = DebateSession.Open(new DebateId(debate.Value), request.Mode, request.Subject.Kind, request.Question, request.Requirements, plan.Participants, plan.Arrangement!, request.Limits, record.CreatedAt);
            var context = await ContextAsync(project, record, opened, request.Question, request.Requirements, plan.Arrangement!, folders, ct).ConfigureAwait(false);
            return await StartAsync(context, session, recorder, guard, lease, ct).ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <exception cref="InvalidOperationException">The project is busy, the debate is running, unknown, closed, its header changed, its replay has problems, or the user did not approve resuming it.</exception>
    public async Task<IDebateRun> ResumeAsync(ProjectContext project, RunId debate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_live.ContainsKey(Key(project.ProjectId, debate)))
        {
            throw DebateUserTexts.Refusal(DebateUserTexts.AlreadyRunning, $"Debate {debate} is already running in this process.", debate.Value);
        }

        var lease = await EnterAsync(project, ct).ConfigureAwait(false);
        try
        {
            var log = await LoadVerifiedAsync(project, ct).ConfigureAwait(false);
            var record = await _ports.Debates.FindAsync(project.ProjectId, debate, ct).ConfigureAwait(false)
                ?? throw DebateUserTexts.Refusal(DebateUserTexts.NotFound, $"Debate {debate} does not exist in this project.", debate.Value);
            var debateLog = DebateLog.Of(log.Entries, debate);
            var opened = debateLog.Opened ?? throw DebateUserTexts.Refusal(DebateUserTexts.CannotResume, $"The log has no DebateOpened for {debate}.", debate.Value);
            if (!string.Equals(DebateHeaderHash.Of(record), opened.HeaderHash, StringComparison.Ordinal))
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.CannotResume, $"The stored header of debate {debate} is not the one that was opened (its hash is not the one DebateOpened anchored): it is not resumed.", debate.Value);
            }

            if (debateLog.Closed is not null)
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.AlreadyFinished, $"Debate {debate} has already finished.", debate.Value);
            }

            var recorder = new DebateTurnRecorder(_ports.Decisions, _ports.Evidence, project.ProjectId, debate);
            string question;
            try
            {
                question = await recorder.ReadTextAsync(record.Question, opened.QuestionSha256, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException)
            {
                throw new UserTextException(UserText.Of(DebateUserTexts.CannotResume, debate.Value), $"The question of debate {debate} cannot be read against the hash DebateOpened anchored: it is not resumed. {ex.Message}", ex);
            }
            var requirements = await _ports.Requirements.FindAsync(project.ProjectId, record.RequirementSetHash, ct).ConfigureAwait(false)
                ?? throw DebateUserTexts.Refusal(DebateUserTexts.CannotResume, $"The frozen requirements {record.RequirementSetHash} of the debate are not stored.", debate.Value);
            var arrangement = Arrangement(record);
            var replay = await ReplayAsync(project, record, debateLog, question, requirements, arrangement, ct).ConfigureAwait(false);
            if (!replay.IsResumable)
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.CannotResumeDetail, $"The log of debate {debate} does not describe a debate that can be resumed: {string.Join(" ", replay.Problems)}", debate.Value, string.Join(" ", replay.Problems));
            }

            var guard = new DebateRepositoryGuard(_ports.Worktrees, project.RootPath, recorder);
            await guard.EstablishBaselineAsync(ct).ConfigureAwait(false);
            var binding = ContentHash.Sha256Hex(ContentHash.Canonical(["debate-resume/1", debate.Value, opened.HeaderHash, log.HeadHash]));
            var approval = await _ports.Approvals.RequestAsync(
                new ApprovalRequest(ResumeAction, $"Reanudar el debate {debate} ({record.Mode}) en la ronda {replay.Session!.CurrentRound}: gasta cuota.", RiskLevel.Critical, null, [], project.ProjectId, debate.Value, null, binding),
                ct).ConfigureAwait(false);
            if (approval is not { Approved: true } || !string.Equals(approval.Answers, binding, StringComparison.Ordinal))
            {
                throw DebateUserTexts.Refusal(DebateUserTexts.ResumeNotApproved, "Resuming the debate was not approved: it is not resumed.");
            }

            // D-04: every launch the log shows open ended with the process; it is closed as interrupted (not a retry).
            foreach (var attempt in debateLog.Attempts.Where(a => a.IsOpen))
            {
                await recorder.RecordAsync(
                    DecisionKind.AgentRunFinished,
                    Invariant($"Turno {attempt.Task?.Value ?? "-"} interrumpido (intento {attempt.Attempt})"),
                    new AgentRunFinishedPayload(attempt.Task, attempt.Attempt, AttemptOutcome.Interrupted, null, null, "Agent Studio se cerró con el turno en curso.", null, null)
                    {
                        RunId = debate.Value,
                        V = AgentRunFinishedPayload.UsageVersion,
                    },
                    ct).ConfigureAwait(false);
            }

            var folders = new DebateFolders(_ports.Options, project.RootPath, debate);
            if (record.SnapshotCommit is { } commit && !folders.HasSnapshot())
            {
                await folders.ExtractSnapshotAsync(_ports.Worktrees, project.RootPath, commit, ct).ConfigureAwait(false);
            }

            var context = await ContextAsync(project, record, opened, question, requirements, arrangement, folders, ct).ConfigureAwait(false);
            return await StartAsync(context, replay.Session, recorder, guard, lease, ct).ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<DebateSummary>> ListAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var log = await _ports.Decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        var summaries = new List<DebateSummary>();
        foreach (var record in (await _ports.Debates.ListAsync(project.ProjectId, ct).ConfigureAwait(false)).OrderBy(r => r.CreatedAt))
        {
            summaries.Add((await ReadOneAsync(project, record, log, ct).ConfigureAwait(false)).Summary);
        }

        return summaries;
    }

    public async Task<DebateTranscript> ReadAsync(ProjectContext project, RunId debate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var log = await _ports.Decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        var record = await _ports.Debates.FindAsync(project.ProjectId, debate, ct).ConfigureAwait(false)
            ?? throw DebateUserTexts.Refusal(DebateUserTexts.NotFound, $"Debate {debate} does not exist in this project.", debate.Value);
        return await ReadOneAsync(project, record, log, ct).ConfigureAwait(false);
    }

    // ---- preparing ----

    /// <summary>Everything an opening needs, prepared without writing anything.</summary>
    private sealed record Plan(
        DebateProposal Proposal,
        DebateRecord Record,
        IReadOnlyList<DebateParticipant> Participants,
        JudgingArrangement? Arrangement,
        StoredVerdict? Challenged,
        string QuestionSha256);

    /// <summary>What the debate is about, resolved: the subject of the header, the commit of the copy and the vendors that produced it.</summary>
    private sealed record ResolvedSubject(DebateSubject Header, string? SnapshotCommit, IReadOnlySet<VendorId> Producers, StoredVerdict? Challenged);

    private async Task<Plan> PlanAsync(DebateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Requirements);
        ArgumentNullException.ThrowIfNull(request.Subject);
        ArgumentNullException.ThrowIfNull(request.Participants);
        ArgumentNullException.ThrowIfNull(request.Judges);
        ArgumentNullException.ThrowIfNull(request.Limits);

        var project = request.Project;
        var problems = new CodedMessages();
        var notes = new CodedMessages();
        var roster = await _ports.Roster.ListAsync(project.ProjectId, ct).ConfigureAwait(false);
        var providers = _ports.Providers.For(project.RootPath);

        var definitions = new List<DebateParticipantDefinition>();
        foreach (var (asked, index) in request.Participants.Select((p, i) => (p, i)))
        {
            var agent = roster.FirstOrDefault(a => a.Id == asked.Agent);
            if (agent is null)
            {
                problems.Add($"El participante '{asked.Agent}' no está en la plantilla del proyecto.", UserText.Of(DebateUserTexts.ProblemParticipantMissing, asked.Agent.ToString()));
                continue;
            }

            if (agent.Role is AgentRole.Judge or AgentRole.Auditor or AgentRole.Orchestrator)
            {
                problems.Add($"'{agent.Id}' es un {agent.Role}: un juez, un auditor o el orquestador no participan en un debate.", UserText.Of(DebateUserTexts.ProblemParticipantRole, agent.Id.ToString()));
            }

            if (!providers.Any(p => p.Provider == agent.Model.Provider))
            {
                problems.Add($"No hay proveedor para {agent.Model.Provider}, el CLI de '{agent.Id}'.", UserText.Of(DebateUserTexts.ProblemNoProvider, ProviderNames.Of(agent.Model.Provider), agent.Id.ToString()));
            }

            definitions.Add(new DebateParticipantDefinition(agent, asked.Role, DebateIds.Participant(index)));
        }

        var judges = new List<AgentDefinition>();
        foreach (var id in request.Judges.Distinct())
        {
            var judge = roster.FirstOrDefault(a => a.Id == id);
            if (judge is null)
            {
                problems.Add($"El juez '{id}' no está en la plantilla del proyecto.", UserText.Of(DebateUserTexts.ProblemJudgeMissing, id.ToString()));
            }
            else if (judge.Role is not (AgentRole.Judge or AgentRole.Auditor))
            {
                problems.Add($"'{judge.Id}' es un {judge.Role}, no un juez.", UserText.Of(DebateUserTexts.ProblemJudgeRole, judge.Id.ToString()));
            }
            else
            {
                judges.Add(judge);
            }
        }

        var subject = await ResolveSubjectAsync(request, problems, notes, ct).ConfigureAwait(false);
        if (request.Mode == DebateMode.OrchestratorReview && request.Requirements.ContentHash != DebateModes.OrchestratorReview(request.Requirements.FrozenAt).ContentHash)
        {
            problems.Add("La revisión del orquestador se juzga con sus requisitos fijos, orq-1 a orq-7 (DebateModes.OrchestratorReview).", UserText.Of(DebateUserTexts.ProblemOrchestratorRequirements));
        }

        var participants = definitions.Select(d => new DebateParticipant(d.Agent.Id, d.Role, d.Agent.Model)).ToList();
        var candidates = judges.Select(j => new JudgeIdentity(j.Id, j.Model)).ToList();
        var arrangement = DebateJudgingRules.Possible(request.Mode, request.Subject.Kind, participants, subject.Producers, candidates)
            .FirstOrDefault(a => a.Kind == request.Judging);
        if (arrangement is null)
        {
            problems.Add($"Con estos participantes y jueces no cabe ninguna disposición «{request.Judging}»: ningún juez la puede ocupar sin juzgar el trabajo de su propio vendor.", UserText.Of(DebateUserTexts.ProblemNoArrangement));
        }
        else
        {
            try
            {
                _ = DebateSession.Open(new DebateId(ProposalId.Value), request.Mode, request.Subject.Kind, request.Question, request.Requirements, participants, arrangement, request.Limits, _ports.Time.GetUtcNow());
            }
            catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
            {
                // The rule of the domain says it in English: it goes as the technical detail of a text the person can read.
                problems.Add(ex is DomainRuleViolationException { Rule.Length: > 0 } violation ? $"{violation.Rule}: {ex.Message}" : ex.Message, UserText.Of(DebateUserTexts.ProblemSessionRefused, ex.Message));
            }
        }

        var rounds = request.Mode == DebateMode.Tribunal ? 1 : request.Limits.MaxRounds;
        var maxAgentRuns = request.MaxAgentRuns ?? (definitions.Count * rounds) + 2;
        if (maxAgentRuns < definitions.Count)
        {
            problems.Add(Invariant($"Con {maxAgentRuns} ejecuciones no cabe ni la ronda 1 de {definitions.Count} participantes."), UserText.Of(DebateUserTexts.ProblemNoRoundFits, maxAgentRuns, definitions.Count));
        }

        var judgesOfArrangement = arrangement is null
            ? judges
            : [.. arrangement.Judges.Select(j => judges.First(d => d.Id == j.Agent))];
        var record = new DebateRecord(
            ProposalId,
            project.ProjectId,
            DateTimeOffset.UnixEpoch,
            request.Mode,
            request.Requirements.ContentHash,
            new EvidenceId("pregunta"),
            subject.Header,
            subject.SnapshotCommit,
            definitions,
            judgesOfArrangement,
            request.Judging,
            [.. subject.Producers.OrderBy(v => v.Value, StringComparer.Ordinal)],
            [.. (arrangement?.WaivedParticipantVendors ?? new HashSet<VendorId>()).OrderBy(v => v.Value, StringComparer.Ordinal)],
            request.Limits,
            maxAgentRuns,
            subject.Challenged?.Id);
        var questionSha256 = ContentHash.Sha256Hex(Encoding.UTF8.GetBytes(request.Question ?? string.Empty));
        var estimate = Estimate(request, definitions, rounds, maxAgentRuns, notes);
        var hash = DebateHeaderHash.Proposal(record, questionSha256);
        var proposal = new DebateProposal(
            request,
            arrangement ?? new JudgingArrangement(request.Judging, [], new HashSet<VendorId>(subject.Producers), new HashSet<VendorId>()),
            problems.Messages,
            estimate,
            hash,
            ApprovalText(request, definitions, judgesOfArrangement, arrangement, subject, maxAgentRuns, estimate),
            problems.Texts);
        return new Plan(proposal, record, participants, arrangement, subject.Challenged, questionSha256);
    }

    private async Task<ResolvedSubject> ResolveSubjectAsync(DebateRequest request, CodedMessages problems, CodedMessages notes, CancellationToken ct)
    {
        var project = request.Project;
        var none = new HashSet<VendorId>();
        switch (request.Subject.Kind)
        {
            case DebateSubjectKind.None:
                return new ResolvedSubject(new DebateSubject(DebateSubjectKind.None, null), null, none, null);
            case DebateSubjectKind.Commit:
            {
                var repository = await _ports.Worktrees.InspectAsync(project.RootPath, ct).ConfigureAwait(false);
                if (repository.HasUncommittedChanges)
                {
                    notes.Add("Tienes cambios sin confirmar: no entran en el debate, que parte del último commit.", UserText.Of(DebateUserTexts.NoteUncommitted));
                }

                return new ResolvedSubject(new DebateSubject(DebateSubjectKind.Commit, repository.HeadCommit), repository.HeadCommit, none, null);
            }

            case DebateSubjectKind.Run:
            {
                if (string.IsNullOrWhiteSpace(request.Subject.Reference))
                {
                    problems.Add("Un debate sobre una ejecución tiene que nombrarla.", UserText.Of(DebateUserTexts.ProblemRunUnnamed));
                    return new ResolvedSubject(request.Subject, null, none, null);
                }

                var log = await _ports.Decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
                var entries = RunLog.EntriesOf(log.Entries, request.Subject.Reference);
                var started = RunLog.RunStarted(entries);
                if (started is null)
                {
                    problems.Add($"La ejecución {request.Subject.Reference} no está en el registro.", UserText.Of(DebateUserTexts.ProblemRunNotInLog, request.Subject.Reference));
                    return new ResolvedSubject(request.Subject, null, none, null);
                }

                var candidate = RunLog.CandidateCommit(entries);
                if (request.Mode == DebateMode.ImplementationAudit)
                {
                    if (candidate is null)
                    {
                        problems.Add($"La ejecución {request.Subject.Reference} no tiene commit candidato: una auditoría de implementación no se abre sin él.", UserText.Of(DebateUserTexts.ProblemNoCandidateCommit, request.Subject.Reference));
                    }

                    if (!string.Equals(request.Requirements.ContentHash, started.RequirementSetHash, StringComparison.Ordinal))
                    {
                        problems.Add("Una auditoría de implementación se juzga con los requisitos congelados de su ejecución.", UserText.Of(DebateUserTexts.ProblemAuditRequirements));
                    }
                }

                return new ResolvedSubject(new DebateSubject(DebateSubjectKind.Run, request.Subject.Reference), candidate, new HashSet<VendorId>(RunLog.ProducerVendors(entries)), null);
            }

            case DebateSubjectKind.Verdict:
                return await ChallengedAsync(request, problems, ct).ConfigureAwait(false);
            default:
                problems.Add($"Objeto de debate desconocido: {request.Subject.Kind}.", UserText.Of(DebateUserTexts.ProblemUnknownSubject));
                return new ResolvedSubject(request.Subject, null, none, null);
        }
    }

    /// <summary>
    /// The subject of a challenge (D-38 and D-39): the stored verdict, the commit its object was about, and the vendors no judge
    /// of the revision may run on, inherited from that object (its producers, the participants of a debate it closed that were
    /// not waived, the producers and declared authors of an audit). The vendor of the judge that issued it is not added.
    /// </summary>
    private async Task<ResolvedSubject> ChallengedAsync(DebateRequest request, CodedMessages problems, CancellationToken ct)
    {
        var project = request.Project;
        var none = new HashSet<VendorId>();
        if (string.IsNullOrWhiteSpace(request.Subject.Reference))
        {
            problems.Add("Una impugnación tiene que nombrar el veredicto que impugna.", UserText.Of(DebateUserTexts.ProblemVerdictUnnamed));
            return new ResolvedSubject(request.Subject, null, none, null);
        }

        var stored = (await _ports.Verdicts.ListAsync(project.ProjectId, null, ct).ConfigureAwait(false)).FirstOrDefault(v => v.Id == request.Subject.Reference);
        if (stored?.RunId is not { } of)
        {
            problems.Add($"El veredicto {request.Subject.Reference} no está guardado, o no es de ninguna ejecución, debate ni auditoría.", UserText.Of(DebateUserTexts.ProblemVerdictNotStored, request.Subject.Reference));
            return new ResolvedSubject(request.Subject, null, none, null);
        }

        var subject = new DebateSubject(DebateSubjectKind.Verdict, stored.Id);
        if (DebateIds.IsDebate(of))
        {
            var debate = await _ports.Debates.FindAsync(project.ProjectId, new RunId(of), ct).ConfigureAwait(false);
            if (debate is null)
            {
                problems.Add($"El debate {of} del veredicto impugnado no existe.", UserText.Of(DebateUserTexts.ProblemDebateMissing, of));
                return new ResolvedSubject(subject, null, none, stored);
            }

            var excluded = new HashSet<VendorId>(debate.ProducerVendors);
            excluded.UnionWith(debate.Participants.Select(p => p.Agent.Model.Vendor).Where(v => !debate.WaivedParticipantVendors.Contains(v)));
            return new ResolvedSubject(subject, debate.SnapshotCommit, excluded, stored);
        }

        if (of.StartsWith(JournalingAgentProvider.AuditPrefix, StringComparison.Ordinal))
        {
            var audit = _ports.Audits is null ? null : await _ports.Audits.FindAsync(project.ProjectId, new RunId(of), ct).ConfigureAwait(false);
            if (audit is null)
            {
                problems.Add($"La auditoría {of} del veredicto impugnado no existe.", UserText.Of(DebateUserTexts.ProblemAuditMissing, of));
                return new ResolvedSubject(subject, null, none, stored);
            }

            return new ResolvedSubject(subject, audit.HeadCommit, new HashSet<VendorId>([.. audit.ProducerVendors, .. audit.DeclaredAuthors]), stored);
        }

        var log = await _ports.Decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        var entries = RunLog.EntriesOf(log.Entries, of);
        return new ResolvedSubject(subject, RunLog.CandidateCommit(entries), new HashSet<VendorId>(RunLog.ProducerVendors(entries)), stored);
    }

    /// <summary>What the debate may spend at most (D-21), «estimado por Agent Studio».</summary>
    private static DebateEstimate Estimate(DebateRequest request, IReadOnlyList<DebateParticipantDefinition> participants, int rounds, int maxAgentRuns, CodedMessages notes)
    {
        var perRound = participants.Sum(p => TurnTokens.GetValueOrDefault(p.Agent.Model.Provider, 160_000));
        var total = checked(perRound * rounds);
        notes.Add(
            Invariant($"Como mucho {maxAgentRuns} ejecuciones: {participants.Count} participantes por {rounds} {(rounds == 1 ? "ronda" : "rondas")} y los reintentos."),
            rounds == 1
                ? UserText.Of(DebateUserTexts.NoteMaxRunsOneRound, maxAgentRuns, participants.Count)
                : UserText.Of(DebateUserTexts.NoteMaxRuns, maxAgentRuns, participants.Count, rounds));
        notes.Add("Cada turno de Claude con haiku ronda los 40 000 a 160 000 tokens y uno de Codex los 20 000 a 80 000, según lo que lea.", UserText.Of(DebateUserTexts.NoteTokensPerTurn));
        if (request.Limits.MaxTotalTokens is { } limit && limit < total)
        {
            notes.Add(Invariant($"El límite de {limit} tokens corta el debate antes: se va a juicio al alcanzarlo."), UserText.Of(DebateUserTexts.NoteTokenLimit, limit));
            total = limit;
        }

        notes.Add("Estimado por Agent Studio: con suscripción no es lo que pagas.", UserText.Of(DebateUserTexts.NoteEstimated));
        return new DebateEstimate(maxAgentRuns, MetricValue<long>.EstimatedBySystem(total, "estimate: measured tokens per turn × rounds (docs/15 D-21)"), notes.Messages, notes.Texts);
    }

    private static string ApprovalText(
        DebateRequest request,
        IReadOnlyList<DebateParticipantDefinition> participants,
        IReadOnlyList<AgentDefinition> judges,
        JudgingArrangement? arrangement,
        ResolvedSubject subject,
        int maxAgentRuns,
        DebateEstimate estimate)
    {
        var text = new StringBuilder();
        text.AppendLine(Invariant($"Abrir un debate {request.Mode} sobre {subject.Header.Kind}{(subject.SnapshotCommit is { } commit ? " (" + commit[..Math.Min(12, commit.Length)] + ")" : string.Empty)}."));
        text.AppendLine("Participantes (solo lectura, sesiones sin persistencia):");
        foreach (var participant in participants)
        {
            text.AppendLine($"- {participant.Label} · {participant.Role}: {participant.Agent.Id} ({participant.Agent.Model.Provider}, {participant.Agent.Model.Model ?? "modelo por defecto"})");
        }

        text.AppendLine($"Juicio: {request.Judging}, con {string.Join(" y ", judges.Select(j => $"{j.Id} ({j.Model.Provider}, {j.Model.Model ?? "modelo por defecto"})"))}.");
        if (arrangement is { WaivedParticipantVendors.Count: > 0 } waived)
        {
            text.AppendLine($"Dispensa registrada de la regla de los participantes para: {string.Join(", ", waived.WaivedParticipantVendors.Select(v => v.Value).Order(StringComparer.Ordinal))}.");
        }

        text.AppendLine(Invariant($"Límites: {request.Limits.MaxRounds} rondas, {request.Limits.MaxDuration.TotalMinutes} minutos, {request.Limits.MaxTotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "sin límite de"} tokens; {maxAgentRuns} ejecuciones como mucho."));
        text.AppendLine(Invariant($"Estimación: hasta {estimate.Tokens.Value} tokens, estimado por Agent Studio."));
        return text.ToString().TrimEnd();
    }

    private static DebateOpenedPayload Opened(DebateRecord record, Plan plan, string approvedProposalHash, EvidenceRecord question) =>
        new(
            record.Mode,
            DebateHeaderHash.Of(record),
            approvedProposalHash,
            record.RequirementSetHash,
            question.Id,
            question.ContentSha256,
            record.Subject.Kind,
            record.Subject.Reference,
            record.SnapshotCommit,
            [.. record.Participants.Select(p => new DebateParticipantEntry(p.Agent.Id, p.Role, p.Agent.Model.Provider, p.Agent.Model.Vendor, p.Agent.Model.Model, p.Label))],
            [.. record.Judges.Select(j => new DebateJudgeEntry(j.Id, j.Model.Provider, j.Model.Vendor, j.Model.Model))],
            record.Judging,
            record.ProducerVendors,
            record.WaivedParticipantVendors,
            record.Limits.MaxRounds,
            (long)record.Limits.MaxDuration.TotalSeconds,
            record.Limits.MaxTotalTokens,
            record.MaxAgentRuns,
            record.Challenges)
        {
            RunId = record.Id.Value,
        };

    // ---- running ----

    private async Task<IDebateRun> StartAsync(DebateContext context, DebateSession session, DebateTurnRecorder recorder, DebateRepositoryGuard guard, IDisposable lease, CancellationToken ct)
    {
        var canRunTests = context.Record.SnapshotCommit is not null
            && _ports.Verification is not null
            && _ports.TestProfiles is { } profiles
            && await profiles.FindAsync(context.Project, ct).ConfigureAwait(false) is not null;
        var run = new DebateRun(_ports, context, session, recorder, guard, lease, Forget);
        _live[Key(context.Project.ProjectId, context.Debate)] = run;
        run.Start(canRunTests);
        return run;
    }

    private void Forget(DebateRun run) => _live.TryRemove(Key(run.Context.Project.ProjectId, run.Id), out _);

    private async Task<DebateContext> ContextAsync(
        ProjectContext project,
        DebateRecord record,
        DebateOpenedPayload opened,
        string question,
        FrozenRequirementSet requirements,
        JudgingArrangement arrangement,
        DebateFolders folders,
        CancellationToken ct)
    {
        var providers = _ports.Providers.For(project.RootPath);
        var seats = record.Participants
            .Select(p => new DebateSeat(p, providers.FirstOrDefault(x => x.Provider == p.Agent.Model.Provider)
                ?? throw DebateUserTexts.Refusal(DebateUserTexts.NoProvider, $"There is no provider for {p.Agent.Model.Provider}, the CLI of '{p.Agent.Id}'.", ProviderNames.Of(p.Agent.Model.Provider), p.Agent.Id.ToString())))
            .ToList();

        string? subjectFolder = null;
        IReadOnlyList<EvidenceRecord> subjectEvidence = [];
        if (record.Subject is { Kind: DebateSubjectKind.Run, Reference: { } run } && _ports.Dossiers is { } dossiers)
        {
            var package = await dossiers.BuildPackageAsync(new DossierRequest(project.ProjectId, AuditScope.CurrentPlan, run), ct).ConfigureAwait(false);
            subjectFolder = package.Files.Folder;
            subjectEvidence = package.Dossier.Evidence;
        }

        return new DebateContext(project, record, opened, question, requirements, arrangement, seats, folders, subjectFolder, subjectEvidence);
    }

    /// <summary>The arrangement the header froze: its kind, its judges, the producers and the waivers.</summary>
    private static JudgingArrangement Arrangement(DebateRecord record) =>
        new(
            record.Judging,
            [.. record.Judges.Select(j => new JudgeIdentity(j.Id, j.Model))],
            new HashSet<VendorId>(record.ProducerVendors),
            new HashSet<VendorId>(record.WaivedParticipantVendors));

    private async Task<DebateReplay> ReplayAsync(ProjectContext project, DebateRecord record, DebateLog log, string question, FrozenRequirementSet requirements, JudgingArrangement arrangement, CancellationToken ct)
    {
        var stored = await _ports.Verdicts.ListAsync(project.ProjectId, record.Id.Value, ct).ConfigureAwait(false);
        var header = new DebateHeader(
            new DebateId(record.Id.Value),
            record.Mode,
            record.Subject.Kind,
            question,
            requirements,
            [.. record.Participants.Select(p => new DebateParticipant(p.Agent.Id, p.Role, p.Agent.Model))],
            arrangement,
            record.Limits,
            record.CreatedAt);
        var events = log.Events(record.CreatedAt, ids => [.. ids.Select(id => stored.FirstOrDefault(v => v.Id == id)).OfType<StoredVerdict>().Select(v => (v.Verdict, v.Valid))]);
        var replay = DebateStateReplayer.Replay(header, events);
        return log.Problems.Count == 0 ? replay : replay with { Problems = [.. log.Problems, .. replay.Problems] };
    }

    private async Task<DebateTranscript> ReadOneAsync(ProjectContext project, DebateRecord record, DecisionLogState log, CancellationToken ct)
    {
        var problems = new CodedMessages();
        if (!log.Verification.IsValid)
        {
            problems.Add($"El registro de decisiones no verifica (entrada {log.Verification.FirstInvalidSequence}): {log.Verification.Problem}", UserText.Of(DebateUserTexts.ProblemLogUnverified, log.Verification.FirstInvalidSequence?.ToString(CultureInfo.InvariantCulture) ?? "?"));
        }

        var debateLog = DebateLog.Of(log.Entries, record.Id);
        var opened = debateLog.Opened;
        var recorder = new DebateTurnRecorder(_ports.Decisions, _ports.Evidence, project.ProjectId, record.Id);
        string? question = null;
        if (opened is null)
        {
            problems.Add($"El registro no tiene DebateOpened para {record.Id}.", UserText.Of(DebateUserTexts.ProblemNotOpened, record.Id.Value));
        }
        else
        {
            if (!string.Equals(DebateHeaderHash.Of(record), opened.HeaderHash, StringComparison.Ordinal))
            {
                problems.Add("La cabecera guardada no es la que ancló DebateOpened.", UserText.Of(DebateUserTexts.ProblemHeaderMismatch));
            }

            try
            {
                question = await recorder.ReadTextAsync(record.Question, opened.QuestionSha256, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                problems.Add("La pregunta no se puede leer contra su hash: " + ex.Message, UserText.Of(DebateUserTexts.ProblemQuestionUnreadable));
            }
        }

        DebateSession? session = null;
        var requirements = await _ports.Requirements.FindAsync(project.ProjectId, record.RequirementSetHash, ct).ConfigureAwait(false);
        if (requirements is not null && question is not null)
        {
            var replay = await ReplayAsync(project, record, debateLog, question, requirements, Arrangement(record), ct).ConfigureAwait(false);
            session = replay.Session;
            foreach (var replayed in replay.Problems.Where(p => !debateLog.Problems.Contains(p)))
            {
                problems.Add(replayed, UserText.Of(DebateUserTexts.ProblemReplay, replayed));
            }
        }
        else if (requirements is null)
        {
            problems.Add($"Los requisitos congelados {record.RequirementSetHash} no están guardados.", UserText.Of(DebateUserTexts.ProblemRequirementsMissing, record.RequirementSetHash));
        }

        var stored = await _ports.Verdicts.ListAsync(project.ProjectId, record.Id.Value, ct).ConfigureAwait(false);
        var verdicts = stored.Select(v => new JudgedVerdict(v.Id, v.Verdict, v.Issues, v.Supersedes is not null)).ToList();
        var contributions = new List<DebateContributionView>();
        if (opened is not null)
        {
            foreach (var (_, payload) in debateLog.Contributions)
            {
                string? text = null;
                try
                {
                    text = await recorder.ReadTextAsync(payload.Evidence, payload.Sha256, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                {
                    problems.Add($"La contribución {ContributionLabels.Of(payload.Index)} no se puede leer contra su hash: {ex.Message}", UserText.Of(DebateUserTexts.ProblemContributionUnreadable, ContributionLabels.Of(payload.Index)));
                }

                contributions.Add(DebateViews.Contribution(opened, payload, text));
            }
        }

        var closed = debateLog.Closed?.Payload;
        var panel = session?.Panel ?? (closed is null || closed.Rule == DebateClosedPayload.AbortedRule ? null : new PanelOutcome(closed.Outcome, null, closed.ChosenProposal, closed.Rule));
        var summary = new DebateSummary(
            record.Id,
            record.CreatedAt,
            record.Mode,
            record.Subject.Kind,
            question,
            session?.Phase ?? (closed is null ? DebatePhase.Proposals : DebatePhase.Closed),
            session?.Outcome ?? closed?.Outcome ?? DebateOutcome.Open,
            panel?.Decision,
            panel?.ChosenProposal ?? closed?.ChosenProposal,
            closed is null && !_live.ContainsKey(Key(project.ProjectId, record.Id)),
            debateLog.Of<VerdictChallengedPayload>().Any(),
            record.Challenges,
            [.. debateLog.Problems, .. problems.Messages],
            [.. debateLog.ProblemTexts, .. problems.Texts]);
        return new DebateTranscript(
            summary,
            record.RequirementSetHash,
            record.Judging,
            record.WaivedParticipantVendors,
            opened is null ? [] : DebateViews.Participants(opened),
            contributions,
            verdicts,
            panel,
            session?.LimitReached ?? DebateLimitReached.None,
            session?.JudgmentReason,
            summary.Problems,
            summary.ProblemTexts);
    }

    // ---- helpers ----

    /// <summary>
    /// Holds the project for the debate before anything is written. While what holds it is a remote operation of Agent Studio itself (docs/21 D-25) it waits, up to
    /// <see cref="ProjectActivityEntry.RemoteWaitCap"/>, and past it throws the <see cref="UserTextException"/> of <see cref="ProjectActivityEntry.BusyRemoteCode"/>; with a
    /// run, a planning, an audit or another debate it refuses at once, as ever.
    /// </summary>
    private async Task<IDisposable> EnterAsync(ProjectContext project, CancellationToken ct) =>
        await ProjectActivityEntry.EnterAsync(_ports.Activity, project.ProjectId, ProjectActivityKind.Debate, _ports.Time, ct).ConfigureAwait(false)
            ?? throw DebateUserTexts.Refusal(
                DebateUserTexts.ProjectBusy,
                $"The project is busy ({_ports.Activity.Current(project.ProjectId)?.ToString() ?? "another activity"}): a debate does not share the project with any other activity with agents.");

    private async Task<DecisionLogState> LoadVerifiedAsync(ProjectContext project, CancellationToken ct)
    {
        var log = await _ports.Decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(project.ProjectId, log.Verification);
        }

        return log;
    }

    /// <summary>Every run id the log names (runs, debates, audits, auxiliary runs): a new debate id is none of them.</summary>
    private static HashSet<string> RunIdsOf(IReadOnlyList<DecisionEntry> entries)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            try
            {
                if (DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson) is { } payload)
                {
                    taken.Add(payload.RunId);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // A payload that cannot be read names no run.
            }
        }

        return taken;
    }

    private static string Key(string projectId, RunId debate) => projectId + "/" + debate.Value;
}
