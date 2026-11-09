using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Fakes;

/// <summary>Starting runs in the harness and waiting for them with real-time guards (the engine's clock is fake).</summary>
internal static class RunTestSupport
{
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The request the canvas sends once the user approved <paramref name="plan"/> with <paramref name="profile"/> (the
    /// fixtures' one by default): validated against the roster the store holds now, which is what it carries as
    /// <see cref="RunRequest.ValidatedRoster"/>, and with the warning the review showed next to «Aprobar plan» as
    /// <see cref="RunRequest.ApprovedWarning"/>: the one that roster and that profile give with what the harness's
    /// confinement probe answers now, empty when there is none. A test that runs with another profile passes it here: a
    /// <c>with { Verification = ... }</c> afterwards would keep the warning of the fixtures' profile.
    /// </summary>
    public static RunRequest Request(this EngineHarness harness, PlanDefinition plan, RunOptions? options = null, RunId? run = null, RunId? follows = null, VerificationProfile? profile = null)
    {
        var roster = harness.Roster.Agents;
        var verification = profile ?? OrchestrationFixtures.Profile();
        var warning = VerificationStepText.PlanWarning(plan, roster, verification, harness.ClaudeWritesConfined()) ?? string.Empty;

        // A plan with writers is on the list of the fixed barrier (docs/17 D-37): the canvas approves it only with Windows Hello,
        // so the approval it sends says so. A plan without writers is approved by the click alone, as before.
        var method = warning.Length > 0 ? ApprovalVerification.WindowsHello : ApprovalVerification.Dialog;
        return new(
            harness.Project,
            run,
            "Añade Calc.Multiply(a,b) con sus pruebas",
            OrchestrationFixtures.Requirements(),
            plan,
            new ApprovalDecision(true, "usuario", OrchestrationFixtures.Start, "adelante", Verification: method),
            verification,
            options ?? new RunOptions(),
            follows,
            ValidatedRoster: roster,
            ApprovedWarning: warning);
    }

    public static Task<IOrchestrationRun> StartAsync(this EngineHarness harness, PlanDefinition plan, RunOptions? options = null) =>
        harness.CreateOrchestrator().StartAsync(harness.Request(plan, options), TestContext.Current.CancellationToken);

    /// <summary>Waits for the run to end; on timeout the message carries the snapshot and the whole log.</summary>
    public static async Task<RunResult> CompleteAsync(this IOrchestrationRun run, EngineHarness harness, TimeSpan? guard = null)
    {
        try
        {
            return await run.Completion.WaitAsync(guard ?? Guard);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The run did not end in time.\n" + Describe(run.Snapshot()) + "\n" + harness.Describe());
        }
    }

    /// <summary>Polls (real time) until the condition holds; fails with the snapshot and the log otherwise.</summary>
    public static async Task WaitUntilAsync(this EngineHarness harness, IOrchestrationRun run, Func<RunSnapshot, bool> condition, string what, TimeSpan? guard = null)
    {
        var deadline = DateTime.UtcNow + (guard ?? Guard);
        while (!condition(run.Snapshot()))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}.\n" + Describe(run.Snapshot()) + "\n" + harness.Describe());
            }

            await Task.Delay(10);
        }
    }

    public static Task WaitForStatusAsync(this EngineHarness harness, IOrchestrationRun run, string task, WorkTaskStatus status) =>
        harness.WaitUntilAsync(run, s => s.Tasks.Single(t => t.Id.Value == task).Status == status, $"{task} {status}");

    public static Task WaitForPhaseAsync(this EngineHarness harness, IOrchestrationRun run, RunPhase phase) =>
        harness.WaitUntilAsync(run, s => s.Phase == phase, $"phase {phase}");

    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? guard = null)
    {
        var deadline = DateTime.UtcNow + (guard ?? Guard);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(10);
        }
    }

    public static TaskView View(this RunSnapshot snapshot, string task) => snapshot.Tasks.Single(t => t.Id.Value == task);

    /// <summary>
    /// A run that an earlier version started and that never got to dispatch anything: its header, plan, requirements
    /// and request are stored and <c>RunStarted</c> and the tasks are in the log. The plan is never validated here, as
    /// it was not when such a run is resumed: it is how a plan the validator of today rejects can reach the engine.
    /// </summary>
    /// <param name="anchorHeader">
    /// True records <c>RunStarted</c> with the hash of the stored header (version 2); false, as versions before the header
    /// was anchored did (version 1, without it).
    /// </param>
    /// <param name="approvedWriters">
    /// When given (with the header anchored), <c>RunStarted</c> is version 3, as the engine records it now, anchoring these
    /// as the writers at medium integrity the plan was approved with; null records a start from before they were anchored.
    /// </param>
    /// <param name="planApproval">
    /// How the approval of its plan confirmed the person, recorded before <c>RunStarted</c> as the engine records it: Windows
    /// Hello by default, as the canvas approves a plan with writers since the fixed barrier (docs/17 D-37); the dialog alone
    /// for a run approved before it.
    /// </param>
    public static async Task StoreStartedRunAsync(
        this EngineHarness harness,
        RunId run,
        PlanDefinition plan,
        bool anchorHeader = true,
        IReadOnlyList<MediumIntegrityWriterEntry>? approvedWriters = null,
        ApprovalVerification planApproval = ApprovalVerification.WindowsHello)
    {
        var ct = TestContext.Current.CancellationToken;
        var project = harness.Project.ProjectId;
        var requirements = OrchestrationFixtures.Requirements();
        var profile = OrchestrationFixtures.Profile();
        var roster = OrchestrationFixtures.Roster();
        await harness.RequirementStore.SaveAsync(project, requirements, ct);
        var planHash = await harness.Plans.SaveAsync(project, plan, ct);
        var request = await harness.Evidence.CaptureAsync(
            project,
            new AgentStudio.Application.Persistence.EvidenceDraft(
                AgentStudio.Domain.Evidence.EvidenceKind.Document,
                AgentStudio.Domain.Evidence.EvidenceOrigin.ProvidedByHuman,
                "Petición original",
                "usuario",
                RunId: run.Value,
                MediaType: "text/plain"),
            new System.IO.MemoryStream(Encoding.UTF8.GetBytes("Añade Calc.Multiply(a,b) con sus pruebas")),
            ct);
        var header = new AgentStudio.Application.Persistence.RunRecord(
            run, project, OrchestrationFixtures.Start, planHash, requirements.ContentHash, request.Id, harness.Worktrees.InitialCommit, "refs/heads/main", profile, roster, new RunOptions(), null);
        await harness.Runs.CreateAsync(header, ct);
        var system = new Actor(ActorKind.System, "agent-studio");
        await harness.Decisions.RecordAsync(
            project,
            system,
            DecisionKind.ApprovalRequested,
            "Aprobación del plan",
            new ApprovalRequestedPayload(AgentStudio.Orchestration.Engine.Orchestrator.PlanApprovalAction, $"Ejecutar el plan {planHash[..12]}", RiskLevel.Critical, null, []) { RunId = run.Value },
            null,
            ct);
        await harness.Decisions.RecordAsync(
            project,
            new Actor(ActorKind.User, "usuario"),
            DecisionKind.ApprovalGranted,
            "Plan aprobado",
            ApprovalAnswers.Payload(AgentStudio.Orchestration.Engine.Orchestrator.PlanApprovalAction, true, "usuario", "adelante", null, planApproval, run.Value),
            null,
            ct);
        var started = new RunStartedPayload(planHash, requirements.ContentHash, harness.Worktrees.InitialCommit, "refs/heads/main", profile.ContentHash, [.. roster.Select(a => new RosterEntry(a.Id, a.Role, a.Model.Provider, a.Model.Vendor))], null) { RunId = run.Value };
        await harness.Decisions.RecordAsync(
            project,
            system,
            DecisionKind.RunStarted,
            $"Arranca la ejecución {run}",
            (anchorHeader, approvedWriters) switch
            {
                (false, _) => started,
                (true, null) => started with { HeaderHash = RunHeaderHash.Of(header), V = RunStartedPayload.HeaderVersion },
                (true, { } writers) => started with { HeaderHash = RunHeaderHash.Of(header), ApprovedWriters = writers, V = RunStartedPayload.WritersVersion },
            },
            null,
            ct);
        foreach (var task in plan.Tasks)
        {
            await harness.Decisions.RecordAsync(
                project,
                system,
                DecisionKind.TaskCreated,
                $"Tarea {task.Id}: {task.Title}",
                new TaskCreatedPayload(task.Id, task.Title, task.Kind, task.Join, task.MaxRetries) { RunId = run.Value },
                null,
                ct);
        }
    }

    /// <summary>The transitions of the log, as "T1 Pending>Running", in log order.</summary>
    public static IReadOnlyList<string> Transitions(this EngineHarness harness, string? task = null) =>
        [.. harness.Payloads<TaskStatusChangedPayload>()
            .Where(p => task is null || p.Payload.Task.Value == task)
            .Select(p => $"{p.Payload.Task} {p.Payload.From}>{p.Payload.To}")];

    public static int IndexOf(this IReadOnlyList<string> items, string item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] == item)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"'{item}' is not in: {string.Join(" | ", items)}");
    }

    public static IReadOnlyList<(DecisionEntry Entry, AuditAlertPayload Payload)> Alerts(this EngineHarness harness, string code) =>
        [.. harness.Payloads<AuditAlertPayload>().Where(a => a.Payload.Code == code)];

    public static string Describe(RunSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.Append("Phase ").Append(snapshot.Phase).Append(", agent runs ").Append(snapshot.AgentRunsUsed).AppendLine();
        foreach (var task in snapshot.Tasks)
        {
            text.Append("  ").Append(task.Id).Append(' ').Append(task.Status).Append(" attempt ").Append(task.Attempt)
                .Append(" retries ").Append(task.RetryCount).Append(" block ").Append(task.BlockReason ?? "-").AppendLine();
        }

        return text.ToString();
    }

    public static TaskId Tid(string id) => new(id);

    /// <summary>The run's events as the replayer reads them from the log (transitions and condition outcomes, with their recorded time).</summary>
    public static IReadOnlyList<RunEvent> RunEvents(this EngineHarness harness, RunId run) =>
        [.. harness.Entries
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)))
            .Where(x => x.Payload is not null && x.Payload.RunId == run.Value)
            .Select(x => x.Payload switch
            {
                TaskStatusChangedPayload p => (RunEvent?)new TaskTransitioned(x.Entry.RecordedAt, p.Task, p.From, p.To, p.Reason, p.Evidence, p.BlockReason),
                ConditionEvaluatedPayload p => new ConditionResolved(x.Entry.RecordedAt, p.Task, p.Branch),
                QuestionAskedPayload p => new QuestionPosed(x.Entry.RecordedAt, p.Task, p.Question),
                QuestionAnsweredPayload p => new QuestionResolved(x.Entry.RecordedAt, p.Task, p.Question),
                _ => null,
            })
            .OfType<RunEvent>()];

    /// <summary>Replays the run from the log the way resuming does.</summary>
    public static async Task<RunReplay> ReplayAsync(this EngineHarness harness, RunId run)
    {
        var record = await harness.Runs.FindAsync(harness.Project.ProjectId, run, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"Run {run} is not registered.");
        var plan = await harness.Plans.FindAsync(harness.Project.ProjectId, record.PlanHash, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"Plan {record.PlanHash} is not stored.");
        return RunStateReplayer.Replay(plan, record.CreatedAt, harness.RunEvents(run), record.PlanHash);
    }

    /// <summary>The state of every task (status, retries, completion evidence, branch) as one comparable text, without timestamps.</summary>
    public static string StateOf(TaskGraph graph) => string.Join(
        "\n",
        graph.Tasks.Select(t => $"{t.Id}|{t.Status}|{t.RetryCount}|{string.Join(",", t.CompletionEvidence)}|{graph.TakenBranch(t.Id) ?? "-"}|{string.Join(";", t.History.Select(h => $"{h.From}>{h.To}:{h.Reason}"))}"));

    /// <summary>The same view of the live snapshot (branches come from the condition outcomes in the log).</summary>
    public static string StateOf(RunSnapshot snapshot, TaskGraph replayed) => string.Join(
        "\n",
        snapshot.Tasks.Select(t => $"{t.Id}|{t.Status}|{t.RetryCount}|{string.Join(",", t.Evidence)}|{replayed.TakenBranch(t.Id) ?? "-"}"));

    public static string StatusOf(TaskGraph graph) => string.Join(
        "\n",
        graph.Tasks.Select(t => $"{t.Id}|{t.Status}|{t.RetryCount}|{string.Join(",", t.CompletionEvidence)}|{graph.TakenBranch(t.Id) ?? "-"}"));
}
