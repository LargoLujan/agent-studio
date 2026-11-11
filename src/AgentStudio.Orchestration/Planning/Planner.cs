using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Engine.Questions;

namespace AgentStudio.Orchestration.Planning;

/// <summary>
/// The planning use case (ADR-0013, docs/13 section 6 steps 2 to 4). The orchestrator agent reads the repository in a
/// <c>_base</c> worktree on HEAD (the user's uncommitted changes are not part of it) and answers with JSON against
/// <see cref="PlanSchema"/>; the answer is parsed strictly and judged by <see cref="PlanValidator"/>. A plan that does not
/// parse or does not validate gets one correction, by resuming the agent's session with the problems as data; if it is
/// still wrong the proposal goes to the user with its violations. Nothing runs until the user approves the plan.
/// <para>
/// Phase 12 (docs/17 D-06, F12-9): with the history tool of the ports, a planner with memory on Claude Code also gets the
/// local MCP server over a snapshot of what is trusted (<c>--history</c>), in both of its runs, and its prompt says so in
/// one sentence; the snapshot is deleted when the planning ends. Without it (another CLI, nothing trusted yet...) it plans
/// as before, and the proposal says why (<see cref="PlanProposal.HistoryToolNotice"/>).
/// </para>
/// </summary>
internal sealed class Planner(EnginePorts ports)
{
    /// <summary>The tools the orchestrator may use: reading only.</summary>
    public static readonly IReadOnlyList<string> Tools = ["Read", "Grep", "Glob"];

    public async Task<PlanProposal> PlanAsync(PlanningRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        var orchestrator = request.Roster.FirstOrDefault(a => a.Id == request.Orchestrator)
            ?? throw new ArgumentException($"The orchestrator '{request.Orchestrator}' is not in the roster.", nameof(request));
        if (orchestrator.Role != AgentRole.Orchestrator)
        {
            throw new ArgumentException($"'{orchestrator.Id}' is a {orchestrator.Role}, not an orchestrator.", nameof(request));
        }

        var project = request.Project;
        // A critical moment: the plan rests on the whole log, verified from its first entry (ADR-0010, addendum of 2026-11-04).
        var log = await ports.Decisions.LoadAsync(project.ProjectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(project.ProjectId, log.Verification);
        }

        // The questions and the person's own text go on with the secrets out (docs/17 D-28): what is recorded, compared with the
        // answers recorded when a failed planning is tried again, and sent to the vendor is the redacted text, the same each time.
        request = PlannerQuestions.Redacted(request, ports.Redactor);

        // Phase 13 (docs/17 D-28): a planning with the person's answers goes on in the run that asked; any other gets a new one.
        var answers = request.Answers ?? [];
        RunId run;
        var continuation = Continuation.None;
        if (answers.Count > 0)
        {
            (run, _) = PlannerQuestions.RunOf(answers, log.Entries);
            continuation = new Continuation(PlannerQuestions.BaseOf(log.Entries, run), log.Entries);
        }
        else
        {
            var taken = RunLog.RunIds(log.Entries);
            run = RunIds.New(ports.Time, id => taken.Contains(id.Value));
        }

        var steps = request.Verification.Steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var context = new PlanContext(request.Requirements, request.Roster, steps, steps, request.Limits, Orchestrator.TestRunStepsOf(request.Verification));
        var schema = PlanSchema.Build(context);
        if (!PlanSchema.Fits(schema))
        {
            return Failed(run, string.Create(CultureInfo.InvariantCulture, $"El esquema del plan ocupa {StrictJsonSchema.Utf8Bytes(schema)} bytes y Claude Code solo acepta {PlanSchema.MaxBytes} en la línea de órdenes: reduce los requisitos, los agentes o los pasos del perfil."));
        }

        var provider = ports.ProviderFor(project.RootPath, orchestrator.Model.Provider);
        if (provider is null)
        {
            return Failed(run, $"No hay ningún proveedor «{orchestrator.Model.Provider}» para el orquestador «{orchestrator.Id}».");
        }

        // Project memory, with ReadSelected (docs/14 D-23 and D-24): before anything is written, because it fails closed.
        ContextPack? memory;
        try
        {
            memory = await ProjectMemoryPrompt.PackAsync(
                ports.ContextPacker,
                project,
                orchestrator,
                request.OriginalRequest,
                PlannerPrompt.Items(request.Requirements, RequirementPriority.Mandatory),
                PlannerPrompt.Items(request.Requirements, RequirementPriority.Desirable),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ContextBudgetTooSmallException problem)
        {
            return Failed(run, $"No se lanza el orquestador: {ProjectMemoryPrompt.TooSmall(problem)}. Reduce los requisitos obligatorios o quita la memoria del orquestador en la plantilla.");
        }

        // The history tool (docs/17 D-06), after the memory and before anything is written: a log that does not verify stops
        // the planning here as it stops the memory; any other problem only leaves the planner without the tool.
        using var history = ports.History is { } tool
            ? await tool.OfferAsync(project, orchestrator, AgentRunner.ReadOnly(Tools), cancellationToken).ConfigureAwait(false)
            : null;
        var proposal = await PlanWithAsync(request, run, continuation, orchestrator, provider, schema, context, memory, history, progress, cancellationToken).ConfigureAwait(false);
        return history?.Withheld is { } notice ? proposal with { HistoryToolNotice = notice } : proposal;
    }

    /// <summary>
    /// What a planning with the person's answers goes on from (docs/17 D-28): the commit of the <c>_base</c> the questions were
    /// asked from, the one the run builds on (<c>Orchestrator.BaseCommitAsync</c> takes the first <c>_base</c> of the run), and the
    /// log as it was before this planning, for it to tell what it recorded already (a planning with these answers that failed,
    /// tried again).
    /// </summary>
    private sealed record Continuation(string? AskedBase, IReadOnlyList<DecisionEntry> Log)
    {
        public static Continuation None { get; } = new(null, []);
    }

    private async Task<PlanProposal> PlanWithAsync(
        PlanningRequest request,
        RunId run,
        Continuation continuation,
        AgentDefinition orchestrator,
        IAgentProvider provider,
        string schema,
        PlanContext context,
        ContextPack? memory,
        ProjectHistoryOffer? history,
        IProgress<AgentEvent>? progress,
        CancellationToken cancellationToken)
    {
        var project = request.Project;
        var recorder = new RunRecorder(ports.Decisions, ports.Evidence, project.ProjectId, run);
        var repository = await ports.Worktrees.InspectAsync(project.RootPath, cancellationToken).ConfigureAwait(false);

        // Both runs of the orchestrator are checked against the user's repository as it was before the first one. The
        // orchestrator only reads (Tools, without writes or a shell): it can never write there at medium integrity.
        // The baseline is captured BEFORE the _base worktree is created (A-02): that creation checks out the base
        // commit, and a program it could run must be seen as a difference, not become the baseline.
        var guard = new UserRepositoryGuard(ports.Worktrees, project.RootPath, canWriteAtMediumIntegrity: false, recorder);
        await guard.EstablishBaselineAsync(cancellationToken).ConfigureAwait(false);

        // A planning that follows the questions of its run plans on the commit it asked from: the first _base of the run, which
        // the run then builds on. Its copy is adopted, or made again on that commit if it was removed, never on a newer HEAD.
        var answers = request.Answers ?? [];
        var asked = answers.Count == 0
            ? null
            : (await ports.Worktrees.ListOwnedAsync(project, cancellationToken).ConfigureAwait(false)).FirstOrDefault(l => l.Run == run && l.Name == "_base");
        var lease = await ports.Worktrees.EnsureAsync(new WorktreeRequest(project, run, "_base", continuation.AskedBase ?? asked?.BaseCommit ?? repository.HeadCommit), cancellationToken).ConfigureAwait(false);
        if (asked is null)
        {
            await recorder.RecordAsync(
                DecisionKind.WorktreeCreated,
                $"Worktree _base en {lease.BaseCommit} para planificar",
                new WorktreeCreatedPayload(null, lease.Name, lease.Path, lease.RefName, lease.BaseCommit) { RunId = run.Value },
                cancellationToken).ConfigureAwait(false);
        }

        await RecordAnswersAsync(recorder, answers, continuation.Log, cancellationToken).ConfigureAwait(false);
        var session = new Session(ports, request, orchestrator, provider, recorder, guard, lease, schema, progress, memory, history?.Server);
        var skills = await ports.Skills.ProvisionAsync(project.ProjectId, orchestrator, $"{run.Value}-plan", lease.Path, cancellationToken).ConfigureAwait(false);
        try
        {
            return await session.RunAsync(context, skills, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ports.Skills.Release(skills);
        }
    }

    private static PlanProposal Failed(RunId run, string error) => new(run, null, [], false, AgentRunStatus.Failed, error, null);

    /// <summary>
    /// Records each answer of the person (docs/17 D-29), before the orchestrator runs with them: the option chosen, or the
    /// person's own text as evidence they provided, cited by its SHA-256. Answering is not approving: it grants nothing.
    /// Then, once all of them are recorded, the decision for the judge of each, as for the question of an executor (P54, docs/17
    /// D-33): the document the system captures with ids, hashes and instants, no task, never the text of the agent. Each step is
    /// done once: what an earlier planning with these same answers recorded (<paramref name="log"/>, which
    /// <see cref="PlannerQuestions.RunOf"/> checked) is not recorded again, and the decisions that planning did not reach are
    /// captured now.
    /// </summary>
    private static async Task RecordAnswersAsync(RunRecorder recorder, IReadOnlyList<AnsweredQuestion> answers, IReadOnlyList<DecisionEntry> log, CancellationToken cancellationToken)
    {
        if (answers.Count == 0)
        {
            return;
        }

        var asked = new Dictionary<string, (DecisionEntry Entry, QuestionAskedPayload Payload)>(StringComparer.Ordinal);
        var recorded = new Dictionary<string, (DecisionEntry Entry, QuestionAnsweredPayload Payload)>(StringComparer.Ordinal);
        var captured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, payload) in RunLog.Of(log, recorder.RunValue))
        {
            switch (payload)
            {
                case QuestionAskedPayload { Task: null } question:
                    asked.TryAdd(question.Question, (entry, question));
                    break;
                case QuestionAnsweredPayload { Task: null } answer:
                    recorded.TryAdd(answer.Question, (entry, answer)); // the first answer the log recorded is the one that counts
                    break;
                case EvidenceRegisteredPayload { Title: { } title }:
                    captured.Add(title);
                    break;
            }
        }

        // First every answer, then every decision: a decision that fails to be captured (a transient failure) leaves all the answers
        // recorded, so the planning can be tried again with the same ones (PlannerQuestions.RunOf takes only the whole round, once).
        var decisions = new List<RecordedAnswer>();
        foreach (var answered in answers)
        {
            var answer = answered.Answer;
            EvidenceId? textEvidence;
            string? textSha256;
            DateTimeOffset answeredAt;
            if (recorded.TryGetValue(answered.Question.Id, out var before))
            {
                (textEvidence, textSha256, answeredAt) = (before.Payload.AnswerEvidence, before.Payload.AnswerSha256, before.Entry.RecordedAt);
            }
            else
            {
                EvidenceRecord? text = null;
                if (answer.OtherText is { } own)
                {
                    text = await recorder.CaptureAsync(
                        EvidenceKind.Document,
                        EvidenceOrigin.ProvidedByHuman,
                        $"Respuesta a la pregunta {answered.Question.Id} del orquestador",
                        answer.DecidedBy,
                        own,
                        "text/plain",
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }

                var entry = await recorder.RecordAsync(
                    new Actor(answer.Method == Domain.Questions.QuestionAnswerMethod.ChosenByPerson ? ActorKind.User : ActorKind.System, answer.DecidedBy),
                    DecisionKind.QuestionAnswered,
                    $"Respuesta a la pregunta {answered.Question.Id} del orquestador",
                    QuestionAnsweredPayload.Answering(recorder.RunValue, null, answered.Question.Id, answer.OptionId, text?.Id, text?.ContentSha256, answer.DecidedBy, answer.Method),
                    cancellationToken).ConfigureAwait(false);
                (textEvidence, textSha256, answeredAt) = (text?.Id, text?.ContentSha256, entry.RecordedAt);
            }

            // The question as the log anchored it (its evidence and hash, the instant it was recorded), not as the caller gives it back.
            var (askedEntry, asking) = asked[answered.Question.Id];
            decisions.Add(new RecordedAnswer(
                new AskedQuestion(asking.Question, null, asking.Agent, asking.Attempt, answered.Question.Question, asking.QuestionEvidence, asking.QuestionSha256, asking.Estimates, askedEntry.RecordedAt),
                answer.OptionId,
                answer.OtherText,
                textEvidence,
                textSha256,
                answer.DecidedBy,
                answer.Method,
                answeredAt));
        }

        foreach (var decided in decisions)
        {
            if (!captured.Contains(QuestionDocuments.DecisionTitle(decided.Asked)))
            {
                await OrchestrationRun.CaptureDecisionAsync(recorder, decided, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string Describe(PlanViolation violation) =>
        violation.Task is { } task ? $"[{violation.Code}] Task '{task}': {violation.Message}" : $"[{violation.Code}] {violation.Message}";

    private static string Describe(PlanWarning warning) =>
        warning.Task is { } task ? $"[{warning.Code}] Task '{task}': {warning.Message}" : $"[{warning.Code}] {warning.Message}";

    /// <summary>
    /// The (at most) two runs of one planning request. The memory goes with the first prompt; a correction that resumes the
    /// session already has it, and one that cannot resume repeats the whole prompt, memory included. The history server, when
    /// there is one, goes with both runs: each is a new process of the CLI.
    /// </summary>
    private sealed class Session(
        EnginePorts ports,
        PlanningRequest request,
        AgentDefinition orchestrator,
        IAgentProvider provider,
        RunRecorder recorder,
        UserRepositoryGuard guard,
        WorktreeLease lease,
        string schema,
        IProgress<AgentEvent>? progress,
        ContextPack? memory,
        McpServerSpec? history)
    {
        private RunId Run => recorder.Run;

        public async Task<PlanProposal> RunAsync(PlanContext context, ProvisionedSkills skills, CancellationToken cancellationToken)
        {
            var first = await AskAsync(1, PlannerPrompt.Build(request, Run, memory, history is not null), null, memory, skills, cancellationToken).ConfigureAwait(false);
            if (first.Failure is { } failure)
            {
                return failure;
            }

            var parsed = PlanParser.Parse(first.Json, orchestrator.Id);
            if (parsed.Questions.Count > 0)
            {
                // Phase 13 (docs/17 D-28): the orchestrator asks instead of planning, once. With the answers given, it may not
                // ask again: that is a problem of the answer, corrected like any other.
                if ((request.Answers ?? []).Count == 0)
                {
                    return await AskedAsync(parsed.Questions, first.Raw, cancellationToken).ConfigureAwait(false);
                }

                parsed = parsed with { Problems = [AlreadyAnswered] };
            }

            var violations = parsed.Plan is { } plan ? PlanValidator.Validate(plan, context) : [];
            string? firstHash = null;
            if (parsed.Plan is not null)
            {
                firstHash = await CreatedAsync(parsed.Plan, first.Raw, cancellationToken).ConfigureAwait(false);
                if (violations.Count == 0)
                {
                    // A plan that may run is not corrected for its warnings alone (a second run spends quota): they travel
                    // with the proposal (PlanProposal.Warnings), and the run that starts with them says so among its notices.
                    return Warned(new PlanProposal(Run, parsed.Plan, [], false, AgentRunStatus.Succeeded, null, first.Raw?.Id) { OrderNotes = NotesOf(parsed) });
                }
            }

            // Only what makes the plan invalid: the warnings are read of a plan that may run, never of one with violations
            // (an answer of the orchestrator is not trusted, and an invalid one may be of any size).
            IReadOnlyList<string> problems = parsed.Plan is null ? parsed.Problems : [.. violations.Select(Describe)];
            var resumed = first.SessionId is not null;
            var correction = resumed
                ? PlannerPrompt.Correction(Run, problems)
                : PlannerPrompt.Build(request, Run, memory, history is not null) + Environment.NewLine + PlannerPrompt.Correction(Run, problems);
            var second = await AskAsync(2, correction, first.SessionId, resumed ? null : memory, skills, cancellationToken).ConfigureAwait(false);
            if (second.Failure is { } secondFailure)
            {
                return Warned(secondFailure with { Plan = parsed.Plan, Violations = violations, Corrected = true, OrderNotes = NotesOf(parsed) });
            }

            var reparsed = PlanParser.Parse(second.Json, orchestrator.Id);
            if (reparsed.Questions.Count > 0)
            {
                reparsed = reparsed with { Problems = [AlreadyAnswered] };
            }

            if (reparsed.Plan is null)
            {
                return Warned(new PlanProposal(
                    Run,
                    parsed.Plan,
                    violations,
                    true,
                    AgentRunStatus.Succeeded,
                    "El plan corregido no se pudo leer: " + string.Join(" ", reparsed.Problems),
                    second.Raw?.Id) { OrderNotes = NotesOf(parsed) });
            }

            var finalViolations = PlanValidator.Validate(reparsed.Plan, context);
            if (firstHash is null)
            {
                await CreatedAsync(reparsed.Plan, second.Raw, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var hash = await ports.Plans.SaveAsync(recorder.ProjectId, reparsed.Plan, cancellationToken).ConfigureAwait(false);
                await recorder.RecordAsync(
                    new Actor(ActorKind.Orchestrator, orchestrator.Id.Value),
                    DecisionKind.PlanRevised,
                    $"Plan corregido {Short(hash)} (sustituye a {Short(firstHash)})",
                    new PlanRevisedPayload(hash, firstHash, string.Join(" ", problems)) { RunId = Run.Value },
                    cancellationToken).ConfigureAwait(false);
            }

            return Warned(new PlanProposal(
                Run,
                reparsed.Plan,
                finalViolations,
                true,
                AgentRunStatus.Succeeded,
                finalViolations.Count == 0 ? null : "El plan corregido sigue sin ser válido: revísalo antes de aprobarlo.",
                second.Raw?.Id) { OrderNotes = NotesOf(reparsed) });
        }

        /// <summary>Why an answer with questions is not valid when the orchestrator may no longer ask (in English: it goes back to the agent).</summary>
        private const string AlreadyAnswered = "The orchestrator asks only once, before it plans: return the plan, with 'questions' empty.";

        /// <summary>The notes the system kept of a plan, or null without a plan or without notes.</summary>
        private static Domain.Orchestration.Analysis.OrderNotes? NotesOf(PlanParseResult parsed) =>
            parsed.Plan is null || parsed.OrderNotes.Tasks.Count == 0 ? null : parsed.OrderNotes;

        /// <summary>
        /// The questions of the orchestrator instead of a plan (docs/17 D-28 and D-29): each, as validated, is evidence reported
        /// by the agent and a <c>QuestionAsked</c> with no task in this run; the proposal carries them, with no plan, for the
        /// person to answer. The system estimates nothing for them: going on is one more planning, not a task.
        /// </summary>
        private async Task<PlanProposal> AskedAsync(IReadOnlyList<Domain.Questions.AgentQuestion> questions, EvidenceRecord? raw, CancellationToken cancellationToken)
        {
            var actor = new Actor(ActorKind.Orchestrator, orchestrator.Id.Value);
            var pending = new List<PendingQuestion>();
            for (var i = 0; i < questions.Count; i++)
            {
                var id = PlannerQuestions.IdOf(i);
                var question = QuestionDocuments.Redacted(questions[i], ports.Redactor);
                var evidence = await recorder.CaptureAsync(
                    EvidenceKind.AgentStatement,
                    EvidenceOrigin.ReportedByAgent,
                    $"Pregunta {id} del orquestador",
                    orchestrator.Id.Value,
                    PlannerQuestions.EvidenceOf(question),
                    "application/json",
                    null,
                    1,
                    cancellationToken).ConfigureAwait(false);
                await recorder.RecordAsync(
                    actor,
                    DecisionKind.QuestionAsked,
                    $"El orquestador pregunta antes de planificar ({id})",
                    new QuestionAskedPayload(null, 1, orchestrator.Id, id, evidence.Id, evidence.ContentSha256, [.. question.Options.Select(o => o.Id)], question.RecommendedOption?.Id, []) { RunId = Run.Value },
                    cancellationToken).ConfigureAwait(false);
                pending.Add(new PendingQuestion(id, null, orchestrator.Id, 1, question, evidence.Id, [], ports.Time.GetUtcNow()));
            }

            return new PlanProposal(Run, null, [], false, AgentRunStatus.Succeeded, null, raw?.Id) { Questions = pending };
        }

        private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

        /// <summary>
        /// The proposal with the validator's warnings about its plan (<see cref="PlanValidator.Warnings"/>), only when the plan
        /// has no violations: a plan that may not run has nothing to warn about yet, and its size is not bounded.
        /// </summary>
        private static PlanProposal Warned(PlanProposal proposal) =>
            proposal is { Plan: { } plan, Violations.Count: 0 } ? proposal with { Warnings = PlanValidator.Warnings(plan) } : proposal;

        private async Task<string> CreatedAsync(PlanDefinition plan, EvidenceRecord? raw, CancellationToken cancellationToken)
        {
            var hash = await ports.Plans.SaveAsync(recorder.ProjectId, plan, cancellationToken).ConfigureAwait(false);
            await recorder.RecordAsync(
                new Actor(ActorKind.Orchestrator, orchestrator.Id.Value),
                DecisionKind.PlanCreated,
                string.Create(CultureInfo.InvariantCulture, $"Plan {Short(hash)} con {plan.Tasks.Count} tareas"),
                new PlanCreatedPayload(hash, plan.Source, plan.Author, plan.Tasks.Count, raw?.Id) { RunId = Run.Value },
                cancellationToken).ConfigureAwait(false);
            return hash;
        }

        /// <param name="given">The memory pack the prompt carries, recorded in <c>AgentRunStarted</c>; null when it carries none.</param>
        private async Task<Answer> AskAsync(int attempt, string prompt, string? resumeSession, ContextPack? given, ProvisionedSkills skills, CancellationToken cancellationToken)
        {
            var promptEvidence = await recorder.CaptureAsync(
                EvidenceKind.Document,
                EvidenceOrigin.CapturedBySystem,
                PlannerEvidenceTitles.Prompt(attempt),
                RunRecorder.ProducedBySystem,
                prompt,
                "text/markdown",
                null,
                attempt,
                cancellationToken).ConfigureAwait(false);
            var agentRequest = new AgentRunRequest(
                orchestrator,
                prompt,
                lease.Path,
                AgentRunner.ReadOnly(Tools),
                OutputJsonSchema: schema,
                ResumeSessionId: resumeSession,
                Purpose: $"Planificar la ejecución {Run} (intento {attempt})",
                TaskId: null,
                Tags: ["#orquestacion", $"#ejecucion-{Run}", $"#intento-{attempt}"],
                SkillsDirectory: skills.Directory);
            if (history is not null)
            {
                agentRequest = ProjectHistoryLaunch.With(agentRequest, history);
            }

            var invocation = new AgentInvocation(
                orchestrator,
                provider,
                agentRequest,
                null,
                attempt,
                promptEvidence,
                skills.Skills,
                $"Plan propuesto por {orchestrator.Id} (intento {attempt})",
                AgentRunPurpose.Planner,
                given?.ToReference());

            var result = await AgentRunner.RunAsync(
                invocation,
                recorder,
                guard,
                null,
                null,
                e => progress?.Report(e),
                cancellationToken,
                CancellationToken.None,
                ports.Time).ConfigureAwait(false);

            if (result.RepositoryDifferences.Count > 0)
            {
                await AgentRunner.RecordRepositoryAlertAsync(recorder, null, result.RepositoryDifferences, CancellationToken.None).ConfigureAwait(false);
                return new Answer(null, null, null, new PlanProposal(
                    Run,
                    null,
                    [],
                    attempt > 1,
                    AgentRunStatus.Failed,
                    "El repositorio del usuario cambió mientras planificaba el orquestador; el plan se descarta. " + string.Join(" ", result.RepositoryDifferences),
                    result.Report?.Id));
            }

            if (!result.Launched)
            {
                var cancelled = result.LaunchError is OperationCanceledException;
                return new Answer(null, null, null, new PlanProposal(
                    Run,
                    null,
                    [],
                    attempt > 1,
                    cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed,
                    cancelled ? "Se canceló la planificación." : "No se pudo lanzar el orquestador: " + result.LaunchError?.Message,
                    null));
            }

            var outcome = result.Outcome!;
            if (outcome.Status != AgentRunStatus.Succeeded)
            {
                return new Answer(null, null, null, new PlanProposal(
                    Run,
                    null,
                    [],
                    attempt > 1,
                    outcome.Status,
                    outcome.Error ?? $"El orquestador terminó con estado {outcome.Status}.",
                    result.Report?.Id));
            }

            return new Answer(outcome.StructuredOutputJson, result.Report, result.SessionId, null);
        }
    }

    private sealed record Answer(string? Json, EvidenceRecord? Raw, string? SessionId, PlanProposal? Failure);
}
