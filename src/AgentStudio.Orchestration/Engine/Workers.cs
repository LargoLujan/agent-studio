using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Prompts;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine.Questions;
using AgentStudio.Orchestration.Integration;

namespace AgentStudio.Orchestration.Engine;

/// <summary>What every worker shares: the run, the ports, the log, the guard, and the ways back to the loop.</summary>
internal sealed class WorkerContext(
    RunContext run,
    EnginePorts ports,
    RunRecorder recorder,
    UserRepositoryGuard guard,
    Action<Signal> post,
    Action<TaskId> heartbeat,
    Action<OrchestrationEvent> emit,
    CancellationToken abort)
{
    public RunContext Run { get; } = run;

    public EnginePorts Ports { get; } = ports;

    public RunRecorder Recorder { get; } = recorder;

    public UserRepositoryGuard Guard { get; } = guard;

    public CancellationToken Abort { get; } = abort;

    public DateTimeOffset Now => Ports.Time.GetUtcNow();

    public void Post(Signal signal) => post(signal);

    public void Heartbeat(TaskId task) => heartbeat(task);

    public void Emit(OrchestrationEvent orchestrationEvent) => emit(orchestrationEvent);

    /// <summary>A heartbeat for every line a tool prints (verification, integration).</summary>
    public IProgress<ProcessOutputLine> ProgressFor(TaskId task) => new Callback<ProcessOutputLine>(_ => heartbeat(task));

    public IProgress<AgentEvent> AgentProgressFor(TaskId task) => new Callback<AgentEvent>(e =>
    {
        heartbeat(task);
        emit(new AgentActivityEvent(Now, task, e));
    });

    /// <summary>The worktree of a task, created or adopted on <paramref name="baseCommit"/>, with its <c>WorktreeCreated</c> entry the first time.</summary>
    public async Task<WorktreeLease> EnsureWorktreeAsync(TaskId task, string baseCommit, bool recordCreation)
    {
        var lease = await Ports.Worktrees.EnsureAsync(new WorktreeRequest(Run.Project, Run.Run, task.Value, baseCommit), Abort).ConfigureAwait(false);
        if (recordCreation)
        {
            await Recorder.RecordAsync(
                DecisionKind.WorktreeCreated,
                $"Worktree de {task} en {lease.BaseCommit}",
                new WorktreeCreatedPayload(task, lease.Name, lease.Path, lease.RefName, lease.BaseCommit) { RunId = Recorder.RunValue },
                Abort).ConfigureAwait(false);
        }

        return lease;
    }

    public static WorktreeInfo Info(WorktreeLease lease) => new(lease.Name, lease.Path, lease.RefName, lease.BaseCommit);

    /// <summary>Captures the attempt record and returns its id (it goes first in the evidence of the transition).</summary>
    public async Task<EvidenceId> RecordAttemptAsync(
        TaskId task,
        int attempt,
        string outcome,
        string? attemptBase,
        string? head,
        IReadOnlyList<string> changedPaths,
        ExecutorReport? report,
        EvidenceRecord? reportEvidence,
        TestRunSummary? tests)
    {
        var record = new AttemptRecord(
            1,
            AttemptRecord.RecordKind,
            Recorder.RunValue,
            task.Value,
            attempt,
            outcome,
            attemptBase,
            head,
            changedPaths,
            report?.Outputs ?? [],
            tests,
            reportEvidence?.Id.Value,
            reportEvidence?.ContentSha256);
        var evidence = await Recorder.CaptureAsync(
            EvidenceKind.Document,
            EvidenceOrigin.CapturedBySystem,
            AttemptRecord.Title(task.Value, attempt),
            RunRecorder.ProducedBySystem,
            record.ToJson(),
            AttemptRecord.MediaType,
            task,
            attempt,
            Abort).ConfigureAwait(false);
        return evidence.Id;
    }

    /// <summary>
    /// Runs verification steps (they build and test an agent's code) between two checks of the user's repository, and
    /// records <c>EvidenceRegistered</c> for what the runner captured, which names <paramref name="commit"/> as the commit
    /// it is about. When the repository had already changed nothing runs; a stopped verification is still checked after it.
    /// </summary>
    public async Task<GuardedVerification> VerifyAsync(TaskId task, int attempt, string workingDirectory, string? commit, IReadOnlyList<VerificationStep> steps, CancellationToken stop)
    {
        var before = await Guard.CheckAsync(Abort).ConfigureAwait(false);
        if (before.Count > 0)
        {
            return new GuardedVerification([], Stopped: false, before);
        }

        IReadOnlyList<VerificationResult> results;
        var stopped = false;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, Abort))
        {
            try
            {
                // The repository of the project, not the worktree: an isolated step copies the commit from its objects.
                results = await Ports.Verification.RunAsync(
                    workingDirectory,
                    steps,
                    new EvidenceScope(Run.Project.ProjectId, Run.Run, task, attempt, commit, Run.Project.RootPath),
                    ProgressFor(task),
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!Abort.IsCancellationRequested)
            {
                results = [];
                stopped = true;
            }
        }

        foreach (var result in results)
        {
            await Recorder.RegisterAsync(result.Evidence, task, attempt, Abort).ConfigureAwait(false);
            Emit(new EvidenceCapturedEvent(Now, task, result.Evidence));
        }

        var after = await Guard.CheckAsync(Abort).ConfigureAwait(false);
        return new GuardedVerification(results, stopped, after);
    }

    private sealed class Callback<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

/// <summary>
/// A verification run between two checks of the user's repository: what the runner gave (nothing when it was stopped or
/// when the repository had already changed), and how the repository differs from the baseline (empty: unchanged).
/// </summary>
internal sealed record GuardedVerification(IReadOnlyList<VerificationResult> Results, bool Stopped, IReadOnlyList<string> Differences);

/// <summary>
/// What the loop decided for one attempt of an agent task. <paramref name="CheckAccess"/>: the attempt follows a wait for
/// quota, and the access of its provider is checked before anything else (F8-3). <paramref name="Handoff"/>: the attempt
/// renews the session of its task, never resuming the old one, with a handoff the system builds (F8-6).
/// </summary>
internal sealed record AgentAttemptPlan(
    TaskSpec Spec,
    AgentDefinition Agent,
    IAgentProvider Provider,
    int Attempt,
    string? TaskBase,
    string? AttemptBase,
    bool RecordWorktree,
    string? ResumeSession,
    IReadOnlyList<(TaskId Task, EvidenceId Report, string Sha256)> Upstream,
    FailureNote? PreviousFailure,
    bool CheckAccess = false,
    HandoffFacts? Handoff = null,
    IReadOnlyList<RecordedAnswer>? Answers = null);

/// <summary>
/// One attempt of an <c>AgentWork</c> task (docs/13 section 4.2): worktree, skills, prompt as evidence and
/// <c>InstructionsIssued</c>, the agent run (with the user-repository check around it), then the system's own checks:
/// system commit, changed paths against the write scope on the real diff, the diff as evidence, verification, and the
/// attempt record. The worker never touches the graph: it tells the loop what happened.
/// </summary>
internal static class AgentAttemptWorker
{
    /// <summary>The code of the audit alert recorded when an upstream report is not the one the decision log anchored.</summary>
    internal const string UpstreamTamperedAlert = "upstream.evidence-tampered";

    public static async Task RunAsync(WorkerContext w, AgentAttemptPlan plan, InFlight flight)
    {
        var spec = plan.Spec;
        var task = spec.Id;
        var attempt = plan.Attempt;
        ProvisionedSkills? skills = null;
        var recorded = false;
        WorktreeInfo? info = null;
        try
        {
            // After a wait for quota, the access of the provider first (F8-3): a CLI signed out, or a quota still limited,
            // ends the attempt here, before the memory, the worktree, the skills or the prompt, and before any launch.
            if (plan.CheckAccess && await AccessCheck.BeforeRelaunchAsync(w, task, plan.Provider.Provider).ConfigureAwait(false) is { } stopped)
            {
                w.Post(new WorkerEnded(task, attempt, stopped));
                return;
            }

            // Project memory, with ReadSelected (docs/14 D-23 and D-24), before anything of the attempt exists: when the
            // mandatory requirements do not fit, nothing is launched and the task waits for a person with the reason.
            ContextPack? memory;
            try
            {
                memory = await ProjectMemoryPrompt.PackAsync(
                    w.Ports.ContextPacker,
                    w.Run.Project,
                    plan.Agent,
                    spec.Objective,
                    ExecutorPrompt.Items(spec, w.Run.Requirements, RequirementPriority.Mandatory),
                    ExecutorPrompt.Items(spec, w.Run.Requirements, RequirementPriority.Desirable),
                    w.Abort).ConfigureAwait(false);
            }
            catch (ContextBudgetTooSmallException problem)
            {
                w.Post(new WorkerEnded(task, attempt, new AttemptEnd(
                    EndKind.AwaitingHuman,
                    $"No se lanza {task}: {ProjectMemoryPrompt.TooSmall(problem)}. Decide una persona: reduce los requisitos de la tarea o quita la memoria de {plan.Agent.Id} en la plantilla.")));
                return;
            }
            catch (DecisionLogIntegrityException problem)
            {
                // Memory is anchored in the log (ADR-0022): with a log that does not verify, none can be trusted.
                w.Post(new WorkerEnded(task, attempt, new AttemptEnd(
                    EndKind.AwaitingHuman,
                    $"No se lanza {task}: {ProjectMemoryPrompt.Unverified(problem)}. Decide una persona: revisa el registro o quita la memoria de {plan.Agent.Id} en la plantilla.")));
                return;
            }

            // The brief of the project (docs/17 D-10), whatever the memory policy: a memory entry of the person, read from the
            // verified log like the rest of the memory, so a log that does not verify launches nothing.
            ProjectBrief? brief;
            try
            {
                brief = w.Ports.Brief is { } briefs ? await briefs.ReadAsync(w.Run.Project, w.Abort).ConfigureAwait(false) : null;
            }
            catch (DecisionLogIntegrityException)
            {
                w.Post(new WorkerEnded(task, attempt, new AttemptEnd(
                    EndKind.AwaitingHuman,
                    $"No se lanza {task}: el registro de decisiones no verifica, así que la ficha del proyecto no se puede comprobar. Decide una persona: revisa el registro.") { Text = UserText.Of(OrchestrationUserTexts.WorkerBriefLogUnverified, task.ToString()) }));
                return;
            }

            WorktreeLease? lease = null;
            string workingDirectory;
            if (spec.Workspace != WorkspaceMode.None)
            {
                lease = await w.EnsureWorktreeAsync(task, plan.TaskBase!, plan.RecordWorktree).ConfigureAwait(false);
                info = WorkerContext.Info(lease);
                if (attempt > 1 && plan.AttemptBase is { } attemptBase)
                {
                    // Every attempt starts exactly where the last one left the worktree; no agent of this task is running now.
                    await w.Ports.Worktrees.CheckoutAsync(lease, attemptBase, w.Abort).ConfigureAwait(false);
                }

                workingDirectory = lease.Path;
            }
            else
            {
                workingDirectory = Path.Combine(w.Ports.Paths.WorktreeRoot, "_data", w.Run.Run.Value, task.Value);
                Directory.CreateDirectory(workingDirectory);
            }

            skills = await w.Ports.Skills.ProvisionAsync(w.Run.Project.ProjectId, plan.Agent, $"{w.Run.Run.Value}-{task.Value}-{attempt}", workingDirectory, w.Abort).ConfigureAwait(false);
            var upstream = new List<UpstreamOutput>();
            foreach (var (upstreamTask, report, sha) in plan.Upstream)
            {
                // The report as the log anchored it (R3-08), never as its row says now: one rewritten together with the
                // hash of its row passes the store's own check.
                string text;
                try
                {
                    text = await w.Recorder.ReadTextAsync(report, sha, w.Abort).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException)
                {
                    var refused = await TamperedUpstreamAsync(w, task, upstreamTask, report, sha, ex).ConfigureAwait(false);
                    w.Post(new WorkerEnded(task, attempt, refused with { AgentRunRecorded = recorded, Worktree = info }));
                    return;
                }

                upstream.Add(new UpstreamOutput(upstreamTask, report, sha, text));
            }

            var steps = w.Run.StepsOf(spec);

            // A renewed session gets the handoff the system builds (F8-6), with what the worktree has now: the files the task
            // changed since its base, and the commit the attempt starts from.
            string? handoff = null;
            if (plan.Handoff is { } facts)
            {
                IReadOnlyList<string> changedSoFar = lease is not null && plan.TaskBase is { } taskBase
                    ? await w.Ports.Worktrees.ChangedPathsAsync(lease.RepositoryRoot, taskBase, plan.AttemptBase ?? taskBase, w.Abort).ConfigureAwait(false)
                    : [];
                handoff = Handoff.Block(w.Run, spec, attempt, facts, lease is null ? null : plan.AttemptBase ?? plan.TaskBase, changedSoFar);
            }

            var prompt = ExecutorPrompt.Build(
                w.Run.Run,
                spec,
                attempt,
                w.Run.Requirements,
                steps,
                upstream,
                plan.PreviousFailure,
                memory,
                handoff,
                plan.Handoff is { } renewal ? Handoff.Why(renewal.Reason) : null,
                plan.Agent,
                brief,
                plan.Answers,
                ResponseLanguage.Resolve(w.Ports.Language));
            var promptEvidence = await w.Recorder.CaptureAsync(
                EvidenceKind.Document,
                EvidenceOrigin.CapturedBySystem,
                $"Prompt de {task} (intento {attempt})",
                RunRecorder.ProducedBySystem,
                prompt,
                "text/markdown",
                task,
                attempt,
                w.Abort,
                covers: spec.Covers).ConfigureAwait(false);
            await w.Recorder.RecordAsync(
                DecisionKind.InstructionsIssued,
                $"Instrucciones para {task} (intento {attempt})",
                new InstructionsIssuedPayload(task, attempt, promptEvidence.Id, promptEvidence.ContentSha256, spec.Covers, spec.WriteScope, skills.Skills) { RunId = w.Recorder.RunValue },
                w.Abort).ConfigureAwait(false);

            // The instructions of the agent reach it as data, in its prompt (docs/17 D-42): never also as a system prompt of the
            // CLI, where they would weigh as much as the rules of the protocol.
            var request = new AgentRunRequest(
                plan.Agent with { SystemInstructions = string.Empty },
                prompt,
                workingDirectory,
                Permissions(spec, plan.Agent),
                OutputJsonSchema: ExecutorReport.Schema,
                ResumeSessionId: plan.Handoff is null ? plan.ResumeSession : null, // a renewed session never resumes the old one (D-42, D-43)
                Purpose: $"{w.Run.Run} · {task} · intento {attempt}: {spec.Title}",
                TaskId: task.Value,
                Tags: ["#orquestacion", $"#ejecucion-{w.Run.Run}", $"#intento-{attempt}"],
                SkillsDirectory: skills.Directory);
            var invocation = new AgentInvocation(plan.Agent, plan.Provider, request, task, attempt, promptEvidence, skills.Skills, $"Informe de {task} (intento {attempt})", AgentRunPurpose.Executor, memory?.ToReference());

            ITimer? timeout = null;
            AgentInvocationResult result;
            try
            {
                result = await AgentRunner.RunAsync(
                    invocation,
                    w.Recorder,
                    w.Guard,
                    () => recorded = true,
                    () =>
                    {
                        // The timeout runs from the launch (the queue does not count), and exists before the loop hears of it.
                        flight.Launched = true;
                        timeout = w.Ports.Time.CreateTimer(_ => flight.Cancel(StopCause.TimedOut), null, w.Run.TimeoutOf(spec), Timeout.InfiniteTimeSpan);
                        w.Post(new WorkerLaunched(task, attempt, info));
                    },
                    e =>
                    {
                        w.Heartbeat(task);
                        w.Emit(new AgentActivityEvent(w.Now, task, e));
                    },
                    flight.Stop.Token,
                    w.Abort,
                    w.Ports.Time).ConfigureAwait(false);
            }
            finally
            {
                timeout?.Dispose();
            }

            var end = await ConcludeAsync(w, plan, flight, lease, result).ConfigureAwait(false);
            end = await RenewalAsync(w, plan, result, end).ConfigureAwait(false);
            w.Post(new WorkerEnded(task, attempt, end with { AgentRunRecorded = recorded, Worktree = info }));
        }
        catch (OperationCanceledException) when (w.Abort.IsCancellationRequested)
        {
            // The app is closing: the run was disposed (its processes killed) and nothing more is recorded.
        }
        catch (Exception ex)
        {
            var kind = flight.Launched ? EndKind.Broken : EndKind.LaunchFailed;
            w.Post(new WorkerEnded(task, attempt, new AttemptEnd(kind, "Error del sistema: " + ex.Message) { AgentRunRecorded = recorded, Worktree = info, Text = UserText.Of(OrchestrationUserTexts.WorkerSystemError, ex.Message) }));
        }
        finally
        {
            if (skills is not null)
            {
                w.Ports.Skills.Release(skills);
            }
        }
    }

    /// <summary>
    /// The upstream report a task consumes is not the content the decision log anchored for it: only someone rewriting
    /// the store explains it. Nothing is launched with it: the alert says which report and which hash, and the task waits
    /// for a person (it is not retried on its own, which would only read the same content again).
    /// </summary>
    private static async Task<AttemptEnd> TamperedUpstreamAsync(WorkerContext w, TaskId task, TaskId upstreamTask, EvidenceId report, string anchoredSha256, Exception problem)
    {
        await w.Recorder.RecordAsync(
            DecisionKind.AuditAlertRaised,
            $"El informe de {upstreamTask} que necesita {task} no es el que ancló el registro",
            new AuditAlertPayload(
                UpstreamTamperedAlert,
                [task, upstreamTask],
                $"The report of task '{upstreamTask}' (evidence {report}), which task '{task}' consumes, is not the content the decision log anchored for it (SHA-256 {anchoredSha256}): {problem.Message} Task '{task}' is not launched with it.") { RunId = w.Recorder.RunValue },
            w.Abort).ConfigureAwait(false);
        return new AttemptEnd(
            EndKind.AwaitingHuman,
            $"No se lanza {task}: el informe de {upstreamTask} (evidencia {report}) no es el que ancló el registro de decisiones; alguien lo reescribió en el almacén. Decide una persona.")
        {
            Text = UserText.Of(OrchestrationUserTexts.WorkerUpstreamTampered, task.ToString(), upstreamTask.ToString(), report.ToString()),
        };
    }

    /// <summary>
    /// Whether the next attempt of a failed one renews the session of its task (docs/16 D-42): its context ran out (the class
    /// of its end), or its largest step reached the threshold from which Claude compresses the context (as its end recorded
    /// it; a CLI that does not report the context, like Codex exec, decides only by the class).
    /// </summary>
    /// <remarks>
    /// Phase 13 (docs/17 D-30, decision 4 (a)): the attempt with the answer to a question resumes the session of the attempt
    /// that asked, which ended well; it goes in a new session with a handoff only when that one left no session to resume, or
    /// when its context reached the compression threshold.
    /// </remarks>
    private static async Task<AttemptEnd> RenewalAsync(WorkerContext w, AgentAttemptPlan plan, AgentInvocationResult result, AttemptEnd end)
    {
        if (end.Kind is not (EndKind.Failed or EndKind.AsksQuestion) || result.Outcome is not { } outcome)
        {
            return end;
        }

        if (end.Kind == EndKind.AsksQuestion && string.IsNullOrWhiteSpace(result.SessionId))
        {
            return end with { QuestionRenew = HandoffReason.SessionNotResumable };
        }

        if (end.Kind == EndKind.Failed && outcome.Classified().Class == AttemptEndClass.ContextExhausted)
        {
            return end with { Renew = HandoffReason.ContextExhausted };
        }

        var log = await w.Ports.Decisions.LoadAsync(w.Run.Project.ProjectId, w.Abort).ConfigureAwait(false);
        var finished = RunLog.Of(log.Entries, w.Recorder.RunValue)
            .Select(e => e.Payload)
            .OfType<AgentRunFinishedPayload>()
            .LastOrDefault(p => p.Task == plan.Spec.Id && p.Attempt == plan.Attempt);
        if (!Handoff.NearlyFull(finished?.ContextPeak))
        {
            return end;
        }

        // A question renews only once the loop records it (within the caps), as a resume rebuilds it from QuestionAsked.
        return end.Kind == EndKind.AsksQuestion ? end with { QuestionRenew = HandoffReason.ContextNearlyFull } : end with { Renew = HandoffReason.ContextNearlyFull };
    }

    /// <summary>Executors write only in their worktree and inside their scope; nobody gets network in Phase 3.</summary>
    internal static PermissionProfile Permissions(TaskSpec spec, AgentDefinition agent) =>
        AgentPermissions.For(spec.Workspace == WorkspaceMode.Write, agent);

    private static async Task<AttemptEnd> ConcludeAsync(WorkerContext w, AgentAttemptPlan plan, InFlight flight, WorktreeLease? lease, AgentInvocationResult result)
    {
        var spec = plan.Spec;
        var task = spec.Id;
        var attempt = plan.Attempt;
        if (!result.Launched && result.RepositoryDifferences.Count > 0)
        {
            return new AttemptEnd(EndKind.RepositoryChanged, "El repositorio del usuario había cambiado antes de lanzar el agente: " + string.Join(" ", result.RepositoryDifferences))
            {
                Differences = result.RepositoryDifferences,
                Head = plan.AttemptBase,
            };
        }

        if (!result.Launched)
        {
            return result.LaunchError is OperationCanceledException
                ? new AttemptEnd(EndKind.Cancelled, "Se canceló antes de arrancar.") { Text = UserText.Of(OrchestrationUserTexts.WorkerCancelledBeforeStart) }
                : new AttemptEnd(EndKind.LaunchFailed, "No se pudo lanzar el agente: " + result.LaunchError?.Message) { Text = UserText.Of(OrchestrationUserTexts.WorkerLaunchFailed, result.LaunchError?.Message ?? string.Empty) };
        }

        var outcome = result.Outcome!;
        var evidence = new List<EvidenceId>();
        if (result.Report is { } reportEvidence)
        {
            evidence.Add(reportEvidence.Id);
        }

        if (result.RepositoryDifferences.Count > 0)
        {
            return new AttemptEnd(EndKind.RepositoryChanged, "El repositorio del usuario cambió mientras trabajaba el agente: " + string.Join(" ", result.RepositoryDifferences))
            {
                Differences = result.RepositoryDifferences,
                Evidence = evidence,
                SessionId = result.SessionId,
                Head = plan.AttemptBase,
            };
        }

        // The agent is gone (its run was disposed): the system commits what it left, so the worktree is clean between attempts.
        var head = plan.AttemptBase;
        IReadOnlyList<string> changed = [];
        if (lease is not null)
        {
            var commit = await w.Ports.Worktrees.CommitAllAsync(lease, CommitMessage(w.Run, spec, attempt, plan.Agent), w.Abort).ConfigureAwait(false);
            head = commit.Commit;
            changed = await w.Ports.Worktrees.ChangedPathsAsync(lease.RepositoryRoot, plan.TaskBase!, head, w.Abort).ConfigureAwait(false);
            if (commit.Committed)
            {
                using var diff = new MemoryStream();
                await w.Ports.Worktrees.WriteDiffAsync(lease.RepositoryRoot, plan.AttemptBase!, head, diff, w.Abort).ConfigureAwait(false);
                var diffEvidence = await w.Recorder.CaptureBytesAsync(
                    EvidenceKind.CodeDiff,
                    EvidenceOrigin.CapturedBySystem,
                    $"Diff de {task} (intento {attempt})",
                    RunRecorder.ProducedBySystem,
                    diff.ToArray(),
                    "text/x-diff",
                    task,
                    attempt,
                    w.Abort).ConfigureAwait(false);
                evidence.Add(diffEvidence.Id);
            }
        }

        async Task<AttemptEnd> EndAsync(EndKind kind, string reason, ExecutorReport? report = null, TestRunSummary? tests = null, IReadOnlyList<EvidenceId>? more = null, DateTimeOffset? retryAt = null, string? finalHead = null, UserText? text = null)
        {
            var finalCommit = finalHead ?? head;
            var record = await w.RecordAttemptAsync(task, attempt, kind.ToString(), plan.AttemptBase, finalCommit, changed, report, result.Report, tests).ConfigureAwait(false);
            IReadOnlyList<EvidenceId> all = [record, .. evidence, .. more ?? []];
            return new AttemptEnd(kind, reason)
            {
                Text = text,
                Evidence = all,
                SessionId = result.SessionId,
                Head = finalCommit,
                RetryAt = retryAt,
                Tests = tests,
                AgentSummary = report?.Summary,
                AgentOpenIssues = report?.OpenIssues ?? [],
                Result = kind == EndKind.Completed
                    ? new TaskResult(lease is null ? null : finalCommit, changed, report?.OutputMap() ?? new Dictionary<string, string>(), tests, result.Report?.Id, result.Report?.ContentSha256)
                    : null,
            };
        }

        switch (flight.Cause)
        {
            case StopCause.Stalled or StopCause.TimedOut:
                // Abandoned: back to the base of the attempt (docs/13 section 4.4). The partial diff stays as evidence.
                if (lease is not null && plan.AttemptBase is { } attemptBase)
                {
                    await w.Ports.Worktrees.CheckoutAsync(lease, attemptBase, w.Abort).ConfigureAwait(false);
                }

                return await EndAsync(
                    EndKind.Failed,
                    flight.Cause == StopCause.Stalled
                        ? string.Create(CultureInfo.InvariantCulture, $"Abandonada: sin actividad durante más de {w.Run.StallThreshold.TotalMinutes:0.#} minutos.")
                        : string.Create(CultureInfo.InvariantCulture, $"Tiempo agotado: más de {w.Run.TimeoutOf(spec).TotalMinutes:0.#} minutos."),
                    finalHead: plan.AttemptBase,
                    text: flight.Cause == StopCause.Stalled
                        ? UserText.Of(OrchestrationUserTexts.WorkerAbandoned, LoggedTexts.Minutes(w.Run.StallThreshold))
                        : UserText.Of(OrchestrationUserTexts.WorkerTimedOut, LoggedTexts.Minutes(w.Run.TimeoutOf(spec)))).ConfigureAwait(false);
            case StopCause.User:
                return await EndAsync(EndKind.Cancelled, "Cancelada por el usuario.", text: UserText.Of(OrchestrationUserTexts.WorkerCancelledByUser)).ConfigureAwait(false);
        }

        // The class of the end decides, not the status (docs/16 D-41): a window of Claude rejected without a 429 ends Failed
        // with the class WindowQuota, and it is a limit all the same.
        var classified = outcome.Classified();
        if (classified.Class == AttemptEndClass.SignedOut)
        {
            // No retry: only signing in fixes it, and that is a person's.
            return await EndAsync(
                EndKind.AwaitingHuman,
                $"La sesión de {plan.Provider.Provider} caducó o no está iniciada: inicia sesión con «{AccessCheck.LoginCommand(plan.Provider.Provider)}» y reintenta la tarea.",
                text: UserText.Of(OrchestrationUserTexts.WorkerSignedOut, plan.Provider.Provider.ToString(), AccessCheck.LoginCommand(plan.Provider.Provider))).ConfigureAwait(false);
        }

        if (outcome.Status == AgentRunStatus.RateLimited || classified.Class is AttemptEndClass.WindowQuota or AttemptEndClass.RateOrOverload)
        {
            // The reset the provider announced, as its adapter classified the end or as its events said. A window whose reset
            // the CLI does not say (Codex exec) asks the quota of the provider, which only Codex reads; with none, the loop
            // waits as the policy of the header says (QuotaWaitPolicy).
            var resetsAt = classified.ResetsAt ?? result.RateLimitResetsAt;
            if (resetsAt is null && classified.Class != AttemptEndClass.RateOrOverload)
            {
                resetsAt = await AccessCheck.ReadResetAsync(w, task, plan.Provider.Provider).ConfigureAwait(false);
            }

            var limited = resetsAt is { } reset
                ? $"Límite de uso del proveedor hasta {reset.ToString("O", CultureInfo.InvariantCulture)}."
                : "Límite de uso del proveedor, sin hora de reinicio.";
            var waiting = await EndAsync(
                EndKind.RateLimited,
                limited,
                retryAt: resetsAt,
                text: resetsAt is { } resetAt
                    ? UserText.Of(OrchestrationUserTexts.WorkerRateLimitedUntil, resetAt)
                    : UserText.Of(OrchestrationUserTexts.WorkerRateLimitedNoReset)).ConfigureAwait(false);
            return waiting with { EndClass = classified.Class };
        }

        switch (outcome.Status)
        {
            case AgentRunStatus.Cancelled:
                return await EndAsync(EndKind.Failed, outcome.Error ?? "El proveedor canceló la ejecución.", text: outcome.Error is null ? UserText.Of(OrchestrationUserTexts.WorkerProviderCancelled) : null).ConfigureAwait(false);
            case not AgentRunStatus.Succeeded:
                return await EndAsync(EndKind.Failed, outcome.Error ?? $"El agente terminó con estado {outcome.Status}.", text: outcome.Error is null ? UserText.Of(OrchestrationUserTexts.WorkerAgentEndedStatus, outcome.Status.ToString()) : null).ConfigureAwait(false);
        }

        var parsed = ExecutorReport.Parse(outcome.StructuredOutputJson, out var problem);
        if (parsed is null)
        {
            return await EndAsync(EndKind.Failed, "El informe del agente no es válido: " + problem, text: UserText.Of(OrchestrationUserTexts.WorkerReportInvalid, problem ?? string.Empty)).ConfigureAwait(false);
        }

        if (parsed.Status == ExecutorReport.Blocked && parsed.AgentQuestion() is { } question)
        {
            // A doubt with options (docs/17 D-28): the loop records it within the caps, and the task waits for the answer. The
            // worktree stays where the system's commit left it, the base of the attempt with the answer.
            var asks = await EndAsync(EndKind.AsksQuestion, "El agente hace una pregunta a una persona.", parsed, text: UserText.Of(OrchestrationUserTexts.WorkerAgentAsks)).ConfigureAwait(false);
            return asks with { Question = question, QuestionEstimates = await QuestionEstimator.EstimateAsync(w, plan, question).ConfigureAwait(false) };
        }

        if (parsed.Status == ExecutorReport.Blocked)
        {
            var issues = parsed.OpenIssues.Count == 0 ? string.Empty : " Pendiente: " + string.Join("; ", parsed.OpenIssues);
            return await EndAsync(EndKind.AwaitingHuman, "El agente necesita a una persona: " + parsed.Summary + issues, parsed).ConfigureAwait(false);
        }

        if (parsed.Status == ExecutorReport.Failed)
        {
            return await EndAsync(EndKind.Failed, "El agente declaró que no pudo terminar: " + parsed.Summary, parsed).ConfigureAwait(false);
        }

        var outside = lease is null ? [] : changed.Where(p => !WriteScope.Contains(spec.WriteScope, p)).ToList();
        if (outside.Count > 0)
        {
            return await EndAsync(EndKind.Failed, "Cambios fuera del alcance de escritura: " + string.Join(", ", outside), parsed, text: UserText.Of(OrchestrationUserTexts.WorkerOutsideScope, string.Join(", ", outside))).ConfigureAwait(false);
        }

        var steps = w.Run.StepsOf(spec);
        TestRunSummary? tests = null;
        var verificationEvidence = new List<EvidenceId>();
        if (lease is not null && steps.Count > 0)
        {
            // The build and the tests run the agent's code: the user's repository is checked around them too.
            var verification = await w.VerifyAsync(task, attempt, lease.Path, head, steps, flight.Stop.Token).ConfigureAwait(false);
            verificationEvidence.AddRange(verification.Results.Select(r => r.Evidence.Id));
            tests = verification.Results.Select(r => r.Evidence.TestRun).LastOrDefault(t => t is not null);
            if (verification.Differences.Count > 0)
            {
                var repositoryChanged = await EndAsync(EndKind.RepositoryChanged, "El repositorio del usuario cambió durante la verificación: " + string.Join(" ", verification.Differences), parsed, tests, verificationEvidence).ConfigureAwait(false);
                return repositoryChanged with { Differences = verification.Differences };
            }

            if (verification.Stopped)
            {
                return flight.Cause == StopCause.User
                    ? await EndAsync(EndKind.Cancelled, "Cancelada por el usuario durante la verificación.", parsed, text: UserText.Of(OrchestrationUserTexts.WorkerVerificationCancelled)).ConfigureAwait(false)
                    : await EndAsync(EndKind.Failed, "La verificación no terminó a tiempo.", parsed, text: UserText.Of(OrchestrationUserTexts.WorkerVerificationTimeout)).ConfigureAwait(false);
            }

            var failed = verification.Results.Where(r => !r.Succeeded || r.TimedOut).Select(r => r.StepId).ToList();
            if (failed.Count > 0)
            {
                return await EndAsync(EndKind.Failed, "La verificación falló en: " + string.Join(", ", failed), parsed, tests, verificationEvidence, text: UserText.Of(OrchestrationUserTexts.WorkerVerificationFailed, string.Join(", ", failed))).ConfigureAwait(false);
            }
        }

        return await EndAsync(EndKind.Completed, "Completada: " + parsed.Summary, parsed, tests, verificationEvidence).ConfigureAwait(false);
    }

    /// <summary>
    /// The message of the commit the system makes of an attempt: its subject and the trailers of <see cref="CommitTrailers"/> (docs/16 D-30): run, task,
    /// attempt and the agent with its model. They are an aid to infer the trace and never its source. A value a trailer cannot carry (a control
    /// character) leaves the model out, and then the trailers: the commit is made anyway, since the log holds the ids.
    /// </summary>
    internal static string CommitMessage(RunContext run, TaskSpec spec, int attempt, AgentDefinition agent)
    {
        var subject = $"{spec.Id}: {spec.Title} (intento {attempt})";
        foreach (var model in new[] { agent.Model.Model, null })
        {
            try
            {
                return CommitTrailers.Append(subject, run.Run, spec.Id, attempt, agent.Id, model);
            }
            catch (ArgumentException)
            {
                // Try again without the model; the last attempt falls through to the bare subject.
            }
        }

        return subject;
    }
}

/// <summary>The workers of the system's own tasks: a tool of the profile, an integration, a review and an approval.</summary>
internal static class SystemWorkers
{
    public static async Task ToolAsync(WorkerContext w, TaskSpec spec, int attempt, string baseCommit, bool recordWorktree, InFlight flight)
    {
        var task = spec.Id;
        WorktreeInfo? info = null;
        try
        {
            using var timeout = w.Ports.Time.CreateTimer(_ => flight.Cancel(StopCause.TimedOut), null, w.Run.TimeoutOf(spec), Timeout.InfiniteTimeSpan);
            var lease = await w.EnsureWorktreeAsync(task, baseCommit, recordWorktree).ConfigureAwait(false);
            info = WorkerContext.Info(lease);
            if (attempt > 1)
            {
                await w.Ports.Worktrees.CheckoutAsync(lease, baseCommit, w.Abort).ConfigureAwait(false);
            }

            // The tool, preceded by the steps it requires (a test with --no-build needs its build on the same copy).
            var steps = w.Run.ToolStepsOf(spec);
            var step = steps[^1];

            // The tool runs on the agents' code: the user's repository is checked around it.
            var verification = await w.VerifyAsync(task, attempt, lease.Path, baseCommit, steps, flight.Stop.Token).ConfigureAwait(false);
            var differences = verification.Differences;
            if (differences.Count == 0 && verification.Stopped)
            {
                var stopped = flight.Cause == StopCause.User
                    ? new AttemptEnd(EndKind.Cancelled, "Cancelada por el usuario.") { Text = UserText.Of(OrchestrationUserTexts.WorkerCancelledByUser) }
                    : new AttemptEnd(EndKind.Failed, flight.Cause == StopCause.Stalled ? "Abandonada: la herramienta no dio señales de vida." : "Tiempo agotado.")
                    {
                        Text = UserText.Of(flight.Cause == StopCause.Stalled ? OrchestrationUserTexts.WorkerToolSilent : OrchestrationUserTexts.WorkerToolTimeout),
                    };
                w.Post(new WorkerEnded(task, attempt, stopped with { Worktree = info }));
                return;
            }

            var results = verification.Results;
            var passed = results.Count == steps.Count && results.All(r => r.Succeeded && !r.TimedOut);
            var tests = results.Select(r => r.Evidence.TestRun).LastOrDefault(t => t is not null);
            var outcome = differences.Count > 0 ? nameof(EndKind.RepositoryChanged) : passed ? nameof(EndKind.Completed) : nameof(EndKind.Failed);
            var record = await w.RecordAttemptAsync(task, attempt, outcome, baseCommit, baseCommit, [], null, null, tests).ConfigureAwait(false);
            IReadOnlyList<EvidenceId> evidence = [record, .. results.Select(r => r.Evidence.Id)];
            AttemptEnd end;
            if (differences.Count > 0)
            {
                // The repository is checked before and after the whole verification: it changed before any step ran (then
                // nothing ran), while the steps ran, or while they ran until the verification stopped (a timeout or a cancel
                // hands back no results, although the steps did run).
                var during = verification.Stopped
                    ? "durante " + Steps(steps.Select(s => s.Id)) + ", que se detuvieron antes de terminar"
                    : results.Count == 0
                        ? "antes de " + Steps(steps.Select(s => s.Id))
                        : "durante " + Steps(results.Select(r => r.StepId));
                end = new AttemptEnd(EndKind.RepositoryChanged, $"El repositorio del usuario cambió {during}: " + string.Join(" ", differences))
                {
                    Differences = differences,
                };
            }
            else if (passed)
            {
                end = new AttemptEnd(EndKind.Completed, $"El paso {step.Id} terminó bien.")
                {
                    Text = UserText.Of(OrchestrationUserTexts.WorkerStepSucceeded, step.Id),
                    Result = new TaskResult(baseCommit, [], new Dictionary<string, string>(), tests, null, null),
                };
            }
            else
            {
                // The step that failed, which may be one the tool requires (its build, say): the run stops there.
                var failed = results.FirstOrDefault(r => !r.Succeeded || r.TimedOut)?.StepId ?? steps[Math.Min(results.Count, steps.Count - 1)].Id;
                end = new AttemptEnd(EndKind.Failed, failed == step.Id
                    ? $"El paso {step.Id} falló."
                    : $"El paso {failed}, que {step.Id} requiere, falló: {step.Id} no llegó a ejecutarse.")
                {
                    Text = failed == step.Id
                        ? UserText.Of(OrchestrationUserTexts.WorkerStepFailed, step.Id)
                        : UserText.Of(OrchestrationUserTexts.WorkerStepSkipped, failed, step.Id),
                };
            }

            w.Post(new WorkerEnded(task, attempt, end with { Evidence = evidence, Head = baseCommit, Worktree = info }));
        }
        catch (OperationCanceledException) when (w.Abort.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            w.Post(new WorkerEnded(task, attempt, new AttemptEnd(EndKind.Broken, "Error del sistema: " + ex.Message) { Worktree = info, Text = UserText.Of(OrchestrationUserTexts.WorkerSystemError, ex.Message) }));
        }
    }

    /// <param name="sources">The commits to merge: the ones the system verified for each task, in merge order.</param>
    /// <param name="verified">The refs of those tasks, which must still point at their verified commits.</param>
    public static async Task IntegrationAsync(WorkerContext w, TaskSpec spec, int attempt, IReadOnlyList<IntegrationSource> sources, IReadOnlyList<VerifiedRef> verified, InFlight flight)
    {
        var task = spec.Id;
        try
        {
            using var timeout = w.Ports.Time.CreateTimer(_ => flight.Cancel(StopCause.TimedOut), null, w.Run.TimeoutOf(spec), Timeout.InfiniteTimeSpan);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(flight.Stop.Token, w.Abort);

            // The integration builds and tests the merged code of the agents: the user's repository is checked around it.
            var before = await w.Guard.CheckAsync(w.Abort).ConfigureAwait(false);
            if (before.Count > 0)
            {
                w.Post(new WorkerEnded(task, attempt, RepositoryChanged("antes de la integración", before)));
                return;
            }

            // A task ref that moved after the system verified the task means something outside the system wrote refs.
            var moved = new List<(VerifiedRef Ref, string? Now)>();
            foreach (var reference in verified)
            {
                var now = await w.Ports.Worktrees.ResolveCommitAsync(w.Run.Project.RootPath, reference.RefName, w.Abort).ConfigureAwait(false);
                if (!string.Equals(now, reference.Commit, StringComparison.OrdinalIgnoreCase))
                {
                    moved.Add((reference, now));
                }
            }

            if (moved.Count > 0)
            {
                w.Post(new WorkerEnded(task, attempt, new AttemptEnd(
                    EndKind.SourceMoved,
                    "No se integra nada: la ref de " + string.Join(", ", moved.Select(m => m.Ref.Task)) + " ya no apunta al commit que se verificó.")
                {
                    Text = UserText.Of(OrchestrationUserTexts.WorkerSourcesMoved, string.Join(", ", moved.Select(m => m.Ref.Task))),
                    Differences = [.. moved.Select(m => $"The ref {m.Ref.RefName} of task '{m.Ref.Task}' points at {m.Now ?? "nothing"}, not at {m.Ref.Commit}, the commit the system verified; nothing was merged.")],
                    MovedSources = [.. moved.Select(m => m.Ref.Task)],
                }));
                return;
            }

            IntegrationResult result;
            try
            {
                result = await w.Ports.Integrator.IntegrateAsync(
                    new IntegrationRequest(w.Run.Project, w.Run.Run, task, attempt, w.Run.BaseCommit, sources, w.Run.StepsOf(spec)),
                    w.ProgressFor(task),
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!w.Abort.IsCancellationRequested)
            {
                var stopped = await w.Guard.CheckAsync(w.Abort).ConfigureAwait(false);
                w.Post(new WorkerEnded(task, attempt, stopped.Count > 0
                    ? RepositoryChanged("durante la integración", stopped)
                    : flight.Cause == StopCause.User
                        ? new AttemptEnd(EndKind.Cancelled, "Cancelada por el usuario.") { Text = UserText.Of(OrchestrationUserTexts.WorkerCancelledByUser) }
                        : new AttemptEnd(EndKind.Failed, "La integración no terminó a tiempo.") { Text = UserText.Of(OrchestrationUserTexts.WorkerIntegrationTimeout) }));
                return;
            }

            // The log anchors the evidence exactly as the integrator captured it and handed it back, never a row read back
            // from the store. A row that is gone, or no longer matches its record, moments after the capture and in this
            // process, was rewritten behind the store's triggers: an integrity breach (ADR-0010). An id handed back without
            // its record is the integrator's own slip: it is not anchored, and fails the attempt.
            var (anchored, unanchored, tampered) = await RegisterIntegrationEvidenceAsync(w, task, attempt, result).ConfigureAwait(false);

            // The run's branch only moves to a verified integration (ADR-0012), and the log names it as the target only of an
            // integration whose evidence it anchored as captured, at the commit the integrator fast-forwarded it to. Any
            // other is recorded on the candidate ref of the task, which holds that commit: a failed one, and also one whose
            // evidence was rewritten, even though the integrator had already moved the branch to it (ADR-0010), so that it
            // is never the commit a judge reviews.
            var accepted = result.Succeeded && unanchored.Count == 0 && tampered.Count == 0;
            await w.Recorder.RecordAsync(
                DecisionKind.IntegrationPerformed,
                accepted
                    ? $"Integración de {task} en {result.ResultCommit}"
                    : result.Succeeded
                        ? $"Integración de {task} en {result.ResultCommit}, sin aceptar: el registro no puede dar por buena su evidencia"
                        : $"Integración de {task} sin resultado",
                new IntegrationPerformedPayload(
                    task,
                    accepted ? IntegrationNames.BranchName(w.Run.Run) : IntegrationNames.CandidateRef(w.Run.Run, task),
                    [.. sources.Select(s => s.Ref)],
                    result.ResultCommit,
                    result.ConflictPaths,
                    false) { RunId = w.Recorder.RunValue },
                w.Abort).ConfigureAwait(false);

            var after = await w.Guard.CheckAsync(w.Abort).ConfigureAwait(false);
            var tests = result.Verification.Select(v => v.Evidence.TestRun).LastOrDefault(t => t is not null);
            var outcome = after.Count > 0
                ? nameof(EndKind.RepositoryChanged)
                : tampered.Count > 0 ? nameof(EndKind.IntegrationEvidenceTampered) : accepted ? nameof(EndKind.Completed) : nameof(EndKind.Failed);
            var attemptRecord = await w.RecordAttemptAsync(task, attempt, outcome, w.Run.BaseCommit, result.ResultCommit, [], null, null, tests).ConfigureAwait(false);
            IReadOnlyList<EvidenceId> evidence = [attemptRecord, .. result.Evidence.Where(anchored.Contains), .. result.Verification.Select(v => v.Evidence.Id).Where(id => !result.Evidence.Contains(id))];
            AttemptEnd end;
            if (after.Count > 0)
            {
                end = RepositoryChanged("durante la integración", after);
            }
            else if (tampered.Count > 0)
            {
                IReadOnlyList<string> differences = result.Succeeded
                    ? [.. tampered, $"The integrator had already fast-forwarded the run's branch to {result.ResultCommit}; the log records this integration on {IntegrationNames.CandidateRef(w.Run.Run, task)} and never takes it as the run's."]
                    : tampered;
                end = new AttemptEnd(EndKind.IntegrationEvidenceTampered, "Una evidencia de la integración cambió en el almacén entre su captura y su lectura: alguien la reescribió, así que la integración no se da por buena ni se reintenta. " + string.Join(" ", tampered))
                {
                    Differences = differences,
                };
            }
            else if (unanchored.Count > 0)
            {
                end = new AttemptEnd(EndKind.Failed, "La evidencia de la integración no es la que capturó el integrador y no se da por buena: " + string.Join(" ", unanchored));
            }
            else if (result.Succeeded && result.ResultCommit is { } commit)
            {
                end = new AttemptEnd(EndKind.Completed, $"Integrada en {commit}.")
                {
                    Text = UserText.Of(OrchestrationUserTexts.WorkerIntegrated, commit),
                    Result = new TaskResult(commit, [], new Dictionary<string, string>(), tests, null, null),
                    IntegrationCommit = commit,
                    Head = commit,
                };
            }
            else if (result.ConflictPaths.Count > 0)
            {
                end = new AttemptEnd(EndKind.Failed, "Conflicto de integración en: " + string.Join(", ", result.ConflictPaths) + ". Se resuelve a mano o con una ejecución de seguimiento.")
                {
                    Text = UserText.Of(OrchestrationUserTexts.WorkerIntegrationConflict, string.Join(", ", result.ConflictPaths)),
                };
            }
            else
            {
                end = new AttemptEnd(EndKind.Failed, "La verificación de la integración falló.") { Text = UserText.Of(OrchestrationUserTexts.WorkerIntegrationVerificationFailed) };
            }

            w.Post(new WorkerEnded(task, attempt, end with { Evidence = evidence }));
        }
        catch (OperationCanceledException) when (w.Abort.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            w.Post(new WorkerEnded(task, attempt, new AttemptEnd(EndKind.Broken, "Error del sistema: " + ex.Message) { Text = UserText.Of(OrchestrationUserTexts.WorkerSystemError, ex.Message) }));
        }
    }

    /// <summary>«el paso build», «los pasos build y test», «los pasos restore, build y test».</summary>
    private static string Steps(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        return list.Count == 1
            ? $"el paso {list[0]}"
            : $"los pasos {string.Join(", ", list.Take(list.Count - 1))} y {list[^1]}";
    }

    /// <summary>A step that found the user's repository changed (<paramref name="when"/>, in Spanish: "durante la integración").</summary>
    private static AttemptEnd RepositoryChanged(string when, IReadOnlyList<string> differences) =>
        new(EndKind.RepositoryChanged, $"El repositorio del usuario cambió {when}: " + string.Join(" ", differences))
        {
            Differences = differences,
        };

    /// <summary>
    /// Records <c>EvidenceRegistered</c> for the evidence of an integration exactly as the integrator handed it back (the
    /// records of its verification and of its own reports), never as the store holds it now. Returns the ids it anchored,
    /// one line for each id the result names without its record (<c>Unanchored</c>: the integrator's slip; it is not
    /// anchored), and one line for each record whose row in the store is missing or no longer matches it
    /// (<c>Tampered</c>: the row was rewritten between the capture and now, which only tampering explains; the record is
    /// anchored as captured, and the dossier reports the row).
    /// </summary>
    private static async Task<(HashSet<EvidenceId> Anchored, List<string> Unanchored, List<string> Tampered)> RegisterIntegrationEvidenceAsync(WorkerContext w, TaskId task, int attempt, IntegrationResult result)
    {
        var captured = result.Verification.Select(v => v.Evidence).Concat(result.EvidenceRecords ?? []).DistinctBy(e => e.Id).ToList();
        var anchored = captured.Select(e => e.Id).ToHashSet();
        var unanchored = result.Evidence
            .Where(id => !anchored.Contains(id))
            .Select(id => $"The integrator named evidence {id} without its record, so it is not registered from the store.")
            .ToList();

        var tampered = new List<string>();
        var rows = (await w.Recorder.ListEvidenceAsync(w.Abort).ConfigureAwait(false)).ToDictionary(e => e.Id);
        foreach (var record in captured)
        {
            if (!rows.TryGetValue(record.Id, out var row))
            {
                tampered.Add($"Evidence {record.Id} ({record.Kind}) that the integrator captured is not in the store for this run.");
            }
            else if (EvidenceRegisteredPayload.Anchoring(record, w.Recorder.RunValue, task, attempt).Disagreements(row) is { Count: > 0 } changed)
            {
                tampered.Add($"Evidence {record.Id} ({record.Kind}) no longer matches what the integrator captured: {string.Join("; ", changed)}.");
            }

            await w.Recorder.RegisterAsync(record, task, attempt, w.Abort).ConfigureAwait(false);
            w.Emit(new EvidenceCapturedEvent(w.Now, task, record));
        }

        return (anchored, unanchored, tampered);
    }

    /// <param name="checkAccess">The review follows a wait for its judge's quota: the access of the judge's provider is checked first (F8-3).</param>
    public static async Task ReviewAsync(WorkerContext w, TaskSpec spec, int attempt, string baseCommit, bool recordWorktree, InFlight flight, bool checkAccess = false)
    {
        var task = spec.Id;
        WorktreeInfo? info = null;
        try
        {
            if (checkAccess && w.Run.Agents.TryGetValue(spec.Agent!.Value, out var judge)
                && await AccessCheck.BeforeRelaunchAsync(w, task, judge.Model.Provider).ConfigureAwait(false) is { } refused)
            {
                w.Post(new WorkerEnded(task, attempt, refused));
                return;
            }

            // The timeout of the task holds for the review too: a judge that never answers is stopped.
            using var timeout = w.Ports.Time.CreateTimer(_ => flight.Cancel(StopCause.TimedOut), null, w.Run.TimeoutOf(spec), Timeout.InfiniteTimeSpan);
            var lease = await w.EnsureWorktreeAsync(task, baseCommit, recordWorktree).ConfigureAwait(false);
            info = WorkerContext.Info(lease);
            var before = await w.Guard.CheckAsync(w.Abort).ConfigureAwait(false);
            if (before.Count > 0)
            {
                w.Post(new WorkerEnded(task, attempt, RepositoryChanged("antes de la revisión", before) with { Worktree = info }));
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(flight.Stop.Token, w.Abort);
            ReviewOutcome outcome;
            try
            {
                outcome = await w.Ports.Review.ReviewRunAsync(
                    new ReviewRequest(w.Run.Project, w.Run.Run, task, spec.Agent!.Value, lease.Path),
                    w.AgentProgressFor(task),
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!w.Abort.IsCancellationRequested)
            {
                var stopped = await w.Guard.CheckAsync(w.Abort).ConfigureAwait(false);
                w.Post(new WorkerEnded(task, attempt, (stopped.Count > 0
                    ? RepositoryChanged("durante la revisión", stopped)
                    : flight.Cause == StopCause.User
                        ? new AttemptEnd(EndKind.Cancelled, "Cancelada por el usuario.") { Text = UserText.Of(OrchestrationUserTexts.WorkerCancelledByUser) }
                        : new AttemptEnd(EndKind.Failed, "La revisión no terminó a tiempo.") { Text = UserText.Of(OrchestrationUserTexts.WorkerReviewTimeout) }) with { Worktree = info }));
                return;
            }

            var differences = await w.Guard.CheckAsync(w.Abort).ConfigureAwait(false);
            if (differences.Count > 0)
            {
                w.Post(new WorkerEnded(task, attempt, RepositoryChanged("durante la revisión", differences) with
                {
                    VerdictId = outcome.VerdictId,
                    Worktree = info,
                }));
                return;
            }

            if (outcome.Disposition == ReviewDisposition.RateLimited)
            {
                // No verdict: the judge's provider hit its usage limit. The review waits for it, like an agent would, by the
                // policy of the header when the provider did not say when the limit resets.
                var limited = outcome.RetryAfter is { } resetsAt
                    ? $"El juez alcanzó el límite de uso de su proveedor hasta {resetsAt.ToString("O", CultureInfo.InvariantCulture)}."
                    : "El juez alcanzó el límite de uso de su proveedor, sin hora de reinicio.";
                w.Post(new WorkerEnded(task, attempt, new AttemptEnd(EndKind.RateLimited, limited)
                {
                    Text = outcome.RetryAfter is { } limitEnds
                        ? UserText.Of(OrchestrationUserTexts.WorkerJudgeLimitUntil, limitEnds)
                        : UserText.Of(OrchestrationUserTexts.WorkerJudgeLimitNoReset),
                    RetryAt = outcome.RetryAfter,
                    Worktree = info,
                }));
                return;
            }

            var end = outcome.Disposition switch
            {
                ReviewDisposition.Approved => new AttemptEnd(EndKind.Completed, $"El juez aprobó (veredicto {outcome.VerdictId}).") { Text = UserText.Of(OrchestrationUserTexts.WorkerJudgeApproved, outcome.VerdictId.ToString()) },
                ReviewDisposition.ChangesRequested => new AttemptEnd(EndKind.Failed, $"El juez pidió cambios (veredicto {outcome.VerdictId}); se propone una ejecución de seguimiento.") { Text = UserText.Of(OrchestrationUserTexts.WorkerJudgeChangesRequested, outcome.VerdictId.ToString()) },
                ReviewDisposition.Rejected => new AttemptEnd(EndKind.Failed, $"El juez rechazó el trabajo (veredicto {outcome.VerdictId}).") { Text = UserText.Of(OrchestrationUserTexts.WorkerJudgeRejected, outcome.VerdictId.ToString()) },
                _ => new AttemptEnd(EndKind.ApprovalNeeded, $"El veredicto {outcome.VerdictId} necesita a una persona.") { Text = UserText.Of(OrchestrationUserTexts.WorkerJudgeNeedsPerson, outcome.VerdictId.ToString()) },
            };
            w.Post(new WorkerEnded(task, attempt, end with { VerdictId = outcome.VerdictId, Worktree = info }));
        }
        catch (OperationCanceledException) when (w.Abort.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            w.Post(new WorkerEnded(task, attempt, new AttemptEnd(EndKind.Broken, "Error del sistema: " + ex.Message) { Worktree = info, Text = UserText.Of(OrchestrationUserTexts.WorkerSystemError, ex.Message) }));
        }
    }

    /// <summary>Asks the approval gate; not answering, failing or cancelling is a denial (fail closed).</summary>
    public static async Task ApprovalAsync(WorkerContext w, TaskSpec spec, int attempt, string action, string description, InFlight flight)
    {
        var task = spec.Id;
        var request = new ApprovalRequest(action, description, RiskLevel.Critical, null, [], w.Run.Project.ProjectId, w.Run.Run.Value, task.Value);
        w.Emit(new ApprovalPendingEvent(w.Now, task, request));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(flight.Stop.Token, w.Abort);
        AttemptEnd end;
        try
        {
            var decision = await w.Ports.Approvals.RequestAsync(request, linked.Token).ConfigureAwait(false);
            end = decision.Approved
                ? new AttemptEnd(EndKind.Approved, $"Aprobado por {decision.DecidedBy}.") { Text = UserText.Of(OrchestrationUserTexts.WorkerApproved, decision.DecidedBy) }
                : new AttemptEnd(EndKind.Denied, $"Denegado por {decision.DecidedBy}{(string.IsNullOrWhiteSpace(decision.Comment) ? "." : ": " + decision.Comment)}")
                {
                    Text = string.IsNullOrWhiteSpace(decision.Comment)
                        ? UserText.Of(OrchestrationUserTexts.WorkerDenied, decision.DecidedBy)
                        : UserText.Of(OrchestrationUserTexts.WorkerDeniedWithComment, decision.DecidedBy, decision.Comment),
                };
        }
        catch (OperationCanceledException) when (w.Abort.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (flight.Cause == StopCause.User)
        {
            end = new AttemptEnd(EndKind.Cancelled, "Cancelada por el usuario.") { Text = UserText.Of(OrchestrationUserTexts.WorkerCancelledByUser) };
        }
        catch (Exception ex)
        {
            end = new AttemptEnd(EndKind.Denied, "No se pudo obtener la aprobación (equivale a denegarla): " + ex.Message) { Text = UserText.Of(OrchestrationUserTexts.WorkerApprovalFailed, ex.Message) };
        }

        w.Post(new WorkerEnded(task, attempt, end));
    }
}
