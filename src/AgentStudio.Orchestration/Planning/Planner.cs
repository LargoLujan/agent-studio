using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Engine;

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
        var log = await ports.Decisions.LoadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(project.ProjectId, log.Verification);
        }

        var taken = RunLog.RunIds(log.Entries);
        var run = RunIds.New(ports.Time, id => taken.Contains(id.Value));
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
        var proposal = await PlanWithAsync(request, run, orchestrator, provider, schema, context, memory, history, progress, cancellationToken).ConfigureAwait(false);
        return history?.Withheld is { } notice ? proposal with { HistoryToolNotice = notice } : proposal;
    }

    private async Task<PlanProposal> PlanWithAsync(
        PlanningRequest request,
        RunId run,
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

        var lease = await ports.Worktrees.EnsureAsync(new WorktreeRequest(project, run, "_base", repository.HeadCommit), cancellationToken).ConfigureAwait(false);
        await recorder.RecordAsync(
            DecisionKind.WorktreeCreated,
            $"Worktree _base en {lease.BaseCommit} para planificar",
            new WorktreeCreatedPayload(null, lease.Name, lease.Path, lease.RefName, lease.BaseCommit) { RunId = run.Value },
            cancellationToken).ConfigureAwait(false);
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
            var violations = parsed.Plan is { } plan ? PlanValidator.Validate(plan, context) : [];
            string? firstHash = null;
            if (parsed.Plan is not null)
            {
                firstHash = await CreatedAsync(parsed.Plan, first.Raw, cancellationToken).ConfigureAwait(false);
                if (violations.Count == 0)
                {
                    // A plan that may run is not corrected for its warnings alone (a second run spends quota): they travel
                    // with the proposal (PlanProposal.Warnings), and the run that starts with them says so among its notices.
                    return Warned(new PlanProposal(Run, parsed.Plan, [], false, AgentRunStatus.Succeeded, null, first.Raw?.Id));
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
                return Warned(secondFailure with { Plan = parsed.Plan, Violations = violations, Corrected = true });
            }

            var reparsed = PlanParser.Parse(second.Json, orchestrator.Id);
            if (reparsed.Plan is null)
            {
                return Warned(new PlanProposal(
                    Run,
                    parsed.Plan,
                    violations,
                    true,
                    AgentRunStatus.Succeeded,
                    "El plan corregido no se pudo leer: " + string.Join(" ", reparsed.Problems),
                    second.Raw?.Id));
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
                second.Raw?.Id));
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
                $"Prompt del orquestador (intento {attempt})",
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
