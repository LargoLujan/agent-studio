using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Derivation;
using AgentStudio.Tests.Memory.Store;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Derivation;

/// <summary>
/// A project opened as the app opens it (<see cref="MemoryWorld"/>: the real SQLite log, evidence and memory, the anchors
/// outside the project, a clock and an activity the test sets) with its <see cref="MemoryDerivation"/>, and writers of runs
/// that record what the engine records, in its order and with its payloads. Reopening is closing the app and opening it
/// again: the derivation keeps nothing of its own between the two.
/// </summary>
internal sealed class DerivationWorld : IDisposable
{
    private DerivationWorld(MemoryWorld world)
    {
        World = world;
        Derivation = Create(world);
    }

    public MemoryWorld World { get; }

    public MemoryDerivation Derivation { get; private set; }

    public ProjectContext Project => World.Project;

    public string EvidenceDirectory => Path.Combine(World.DataDirectory, "evidence");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<DerivationWorld> CreateAsync(bool anchored = true) => new(await MemoryWorld.CreateAsync(anchored));

    public async Task ReopenAsync(bool anchored = true)
    {
        await World.ReopenAsync(anchored);
        Derivation = Create(World);
    }

    public Task<MemoryDerivationResult> DeriveAsync() => Derivation.DeriveAsync(Project, Ct);

    public RunWriter Run(string id) => new(this, id);

    public Task<IReadOnlyList<DecisionEntry>> LogAsync() => World.LogAsync();

    /// <summary>Every anchored entry of the memory, whatever its trust.</summary>
    public Task<IReadOnlyList<MemoryEntry>> MemoryAsync() => World.Memory.RecallAsync(Project, new MemoryQuery(MaxEntries: 1000, TrustedOnly: false), Ct);

    /// <summary>What a prompt with <c>ReadSelected</c> could get: the trusted entries only.</summary>
    public Task<IReadOnlyList<MemoryEntry>> TrustedAsync() => World.Memory.RecallAsync(Project, new MemoryQuery(MaxEntries: 1000), Ct);

    public async Task<IReadOnlyList<(DecisionEntry Entry, AuditAlertPayload Alert)>> AlertsAsync(string code) =>
        [.. (await World.EntriesAsync(DecisionKind.AuditAlertRaised))
            .Select(e => (e, DecisionPayloadSerializer.Deserialize<AuditAlertPayload>(e.PayloadJson)))
            .Where(a => a.Item2.Code == code)];

    public Task<DecisionEntry> RecordAsync(Actor actor, DecisionKind kind, string summary, DecisionPayload payload) =>
        World.Instance.Recorder.RecordAsync(World.Project.ProjectId, actor, kind, summary, payload, null, Ct);

    /// <summary>
    /// Accepts the history of the project as a new origin, as the desktop does once a person said yes to it (ADR-0010,
    /// addendum of 2026-10-18): what was in the log before is inherited from then on.
    /// </summary>
    public async Task AcceptHistoryAsNewOriginAsync()
    {
        var state = await World.Instance.Recorder.LoadAsync(Project.ProjectId, Ct);
        var binding = state.Unanchored!.Binding(Project.ProjectId);
        await World.Instance.Recorder.AcceptAsNewOriginAsync(
            Project.ProjectId,
            new ApprovalRequest(UnanchoredHistory.AcceptAction, "Aceptar este historial como nuevo origen.", RiskLevel.Critical, null, [World.Root], Project.ProjectId, Binding: binding),
            new ApprovalDecision(true, "manuel", T0, null, binding, ApprovalVerification.WindowsHello),
            Ct);
    }

    public void Dispose() => World.Dispose();

    /// <summary>The derivation reads the journal of the project through the checked storage, as the app does.</summary>
    private static MemoryDerivation Create(MemoryWorld world) =>
        new(world.Instance.Recorder, world.Instance.Evidence, world.Spy, world.Memory, world.Activity, new ProjectStorageFiles());
}

/// <summary>
/// Writes one run of the project as the engine records it: the planner and <c>AgentRunner</c> (the answer of an agent as
/// evidence it reported, announced with <c>EvidenceRegistered</c> and cited by <c>AgentRunFinished</c>), the start of the
/// run (<c>Orchestrator.RecordStartAsync</c>), the transitions of the tasks, the verification, the integrations, the
/// alerts, the review (<c>JudgeRunRecorder</c> captures the judge's answer and only names it), the end of the run and the
/// delivery through the approval gate.
/// </summary>
internal sealed class RunWriter(DerivationWorld world, string run)
{
    public static readonly Actor System = new(ActorKind.System, "agent-studio");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Id => run;

    /// <summary>The visible branch of the run, where an accepted integration is recorded.</summary>
    public string Branch => "agentstudio/run-" + run;

    /// <summary>The candidate of an integration task, where one that was not accepted is recorded.</summary>
    public string Candidate(string task) => $"refs/agentstudio/{run}/_candidate/{task}";

    public Task<DecisionEntry> RecordAsync(DecisionKind kind, DecisionPayload payload, Actor? actor = null, string? summary = null) =>
        world.RecordAsync(actor ?? System, kind, summary ?? kind.ToString(), payload);

    /// <summary>Evidence captured through the real store and, as <c>RunRecorder</c> does, announced in the log (version 2).</summary>
    public async Task<(EvidenceRecord Record, DecisionEntry? Registration)> CaptureAsync(
        EvidenceKind kind,
        EvidenceOrigin origin,
        string title,
        string producedBy,
        string content,
        string mediaType,
        string? task,
        int? attempt,
        TestRunSummary? tests = null,
        string? commit = null,
        bool register = true)
    {
        EvidenceRecord record;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
        {
            record = await world.World.Instance.Evidence.CaptureAsync(
                world.Project.ProjectId,
                new EvidenceDraft(kind, origin, title, producedBy, tests, null, run, task, attempt, mediaType, commit),
                stream,
                Ct);
        }

        var registration = register
            ? await RecordAsync(DecisionKind.EvidenceRegistered, EvidenceRegisteredPayload.Anchoring(record, run, task is null ? null : Tid(task), attempt), summary: $"Evidencia {record.Id}: {record.Title}")
            : null;
        return (record, registration);
    }

    /// <summary>The orchestrator plans (<c>Planner</c>): its answer, with the summary it wrote, then <c>PlanCreated</c> citing it.</summary>
    public async Task<(string Hash, EvidenceRecord Raw)> PlanAsync(string summary, int tasks = 2)
    {
        var hash = ContentHash.Sha256Hex($"plan:{run}:{summary}");
        var json = JsonSerializer.Serialize(new { summary, tasks = Array.Empty<object>(), risks = new[] { "Un riesgo que escribió el orquestador." } });
        await RecordAsync(
            DecisionKind.AgentRunStarted,
            new AgentRunStartedPayload(null, 1, Aid("orquestador"), ProviderId.ClaudeCode, VendorId.Anthropic, null, @"C:\wt\_base", Eid("prompt-" + run), HashA, []) { RunId = run });
        var (raw, _) = await CaptureAsync(EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, "Plan propuesto por orquestador (intento 1)", "orquestador", json, "application/json", null, 1);
        await RecordAsync(DecisionKind.AgentRunFinished, new AgentRunFinishedPayload(null, 1, AttemptOutcome.Succeeded, 0, "sesion-plan", null, null, raw.Id) { RunId = run });
        await RecordAsync(
            DecisionKind.PlanCreated,
            new PlanCreatedPayload(hash, PlanSource.OrchestratorAgent, Aid("orquestador"), tasks, raw.Id) { RunId = run },
            new Actor(ActorKind.Orchestrator, "orquestador"));
        return (hash, raw);
    }

    /// <summary>
    /// The user approved <paramref name="planHash"/> and the run starts (<c>Orchestrator.RecordStartAsync</c>): the request,
    /// the person's answer, <c>RunStarted</c> and the tasks with the titles the orchestrator wrote.
    /// </summary>
    public async Task<DecisionEntry> StartAsync(string planHash, string decidedBy = "manuel", params (string Id, WorkTaskKind Kind)[] tasks)
    {
        await RecordAsync(
            DecisionKind.ApprovalRequested,
            new ApprovalRequestedPayload(MemoryDerivation.PlanApprovalAction, $"Ejecutar el plan {planHash[..12]} ({tasks.Length} tareas)", RiskLevel.Critical, null, []) { RunId = run },
            summary: "Aprobación del plan");
        var approval = await RecordAsync(
            DecisionKind.ApprovalGranted,
            new ApprovalAnsweredPayload(MemoryDerivation.PlanApprovalAction, true, decidedBy, $"Plan {planHash[..12]} aprobado en el lienzo.", null) { RunId = run },
            new Actor(ActorKind.User, decidedBy),
            "Plan aprobado");
        await RecordAsync(DecisionKind.RunStarted, new RunStartedPayload(planHash, HashA, CommitA, "main", HashB, [], null) { RunId = run });
        foreach (var (id, kind) in tasks)
        {
            await RecordAsync(DecisionKind.TaskCreated, new TaskCreatedPayload(Tid(id), $"Título que escribió el orquestador para {id}", kind, JoinPolicy.AllCompleted, 1) { RunId = run });
        }

        return approval;
    }

    public Task<DecisionEntry> MoveAsync(string task, WorkTaskStatus from, WorkTaskStatus to, string reason, string? block = null, int attempt = 1) =>
        RecordAsync(DecisionKind.TaskStatusChanged, new TaskStatusChangedPayload(Tid(task), from, to, reason, block, [], attempt) { RunId = run }, summary: $"{task}: {from} → {to}");

    /// <summary>An executor answers with its report (<c>AgentRunner</c>): evidence it reported, then <c>AgentRunFinished</c> citing it.</summary>
    public async Task<EvidenceRecord> ReportAsync(string task, int attempt, string status, string summary, params string[] openIssues)
    {
        var json = JsonSerializer.Serialize(new
        {
            status,
            summary,
            filesChanged = Array.Empty<string>(),
            claims = Array.Empty<string>(),
            openIssues,
            outputs = Array.Empty<object>(),
        });
        var (report, _) = await CaptureAsync(EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, $"Informe de {task} (intento {attempt})", "ejecutor", json, "application/json", task, attempt);
        await RecordAsync(DecisionKind.AgentRunFinished, new AgentRunFinishedPayload(Tid(task), attempt, AttemptOutcome.Succeeded, 0, "sesion-" + task, null, null, report.Id) { RunId = run });
        return report;
    }

    /// <summary>A test step of the verification (<c>VerificationRunner</c>, announced by <c>RunRecorder.RegisterAsync</c>).</summary>
    public async Task<DecisionEntry> TestRunAsync(string task, int attempt, TestRunSummary tests, string commit)
    {
        var (_, registration) = await CaptureAsync(
            EvidenceKind.TestRun,
            EvidenceOrigin.CapturedBySystem,
            "Tests (test)",
            "agent-studio:verification",
            $"# step: test (attempt {attempt})\n# tests: passed {tests.Passed}, failed {tests.Failed}, skipped {tests.Skipped}\n",
            "text/plain",
            task,
            attempt,
            tests,
            commit);
        return registration!;
    }

    public Task<DecisionEntry> IntegrationAsync(string task, string target, string? commit, IReadOnlyList<string> sources, params string[] conflicts) =>
        RecordAsync(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(Tid(task), target, sources, commit, conflicts, false) { RunId = run });

    public Task<DecisionEntry> AlertAsync(string code, string message, params string[] tasks) =>
        RecordAsync(DecisionKind.AuditAlertRaised, new AuditAlertPayload(code, [.. tasks.Select(Tid)], message) { RunId = run });

    /// <summary>
    /// One launch of the judge (<c>JudgeRunRecorder</c>): its answer goes to the evidence store, and the log only names it
    /// in <c>AgentRunFinished</c>, with no <c>EvidenceRegistered</c> and no SHA-256.
    /// </summary>
    public async Task<EvidenceRecord> JudgeAnswerAsync(string task, int attempt, string summary)
    {
        await RecordAsync(
            DecisionKind.AgentRunStarted,
            new AgentRunStartedPayload(null, attempt, Aid("juez"), ProviderId.CodexCli, VendorId.OpenAI, null, @"C:\wt\" + task, Eid($"prompt-juez-{run}-{attempt}"), HashB, []) { RunId = run });
        var json = JsonSerializer.Serialize(new { decision = "Approved", summary });
        var (answer, _) = await CaptureAsync(EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, $"Respuesta del juez (intento {attempt})", "juez", json, "application/json", task, attempt, register: false);
        await RecordAsync(DecisionKind.AgentRunFinished, new AgentRunFinishedPayload(null, attempt, AttemptOutcome.Succeeded, 0, "sesion-juez", null, null, answer.Id) { RunId = run });
        return answer;
    }

    public Task<DecisionEntry> VerdictAsync(string verdictId, string task, VerdictDecision decision, bool valid, IReadOnlyList<VerdictViolationCode> violations, string? supersedes = null) =>
        RecordAsync(
            DecisionKind.VerdictIssued,
            new VerdictIssuedPayload(verdictId, Tid(task), decision, valid, violations, HashA, HashB, supersedes, "Juez en 'openai'; vendors que produjeron el trabajo: anthropic (ADR-0014).") { RunId = run },
            new Actor(ActorKind.Judge, "juez"),
            $"Veredicto {decision}");

    public Task<DecisionEntry> FinishAsync(RunPhase phase, string? integration = null, string? verdict = null) =>
        RecordAsync(DecisionKind.RunFinished, new RunFinishedPayload(phase, "Resumen de la ejecución.", integration, verdict) { RunId = run }, summary: $"La ejecución {run} termina: {phase}");

    /// <summary>
    /// A delivery to the user's branch (<c>DeliveryService</c> through the approval gate): the request names the paths that
    /// change; the person's answer; and, on a yes, the delivery as an integration into the user's branch.
    /// </summary>
    public async Task<(DecisionEntry Answer, DecisionEntry? Delivery)> DeliverAsync(bool approved, IReadOnlyList<string> paths, string head, string integration, string decidedBy = "manuel")
    {
        await RecordAsync(
            DecisionKind.ApprovalRequested,
            new ApprovalRequestedPayload(MemoryDerivation.DeliveryApprovalAction, $"Entregar la ejecución {run}: avanzar tu rama main.", RiskLevel.Critical, null, paths) { RunId = run },
            summary: "Aprobación pedida (Critical): " + MemoryDerivation.DeliveryApprovalAction);
        var answer = await RecordAsync(
            approved ? DecisionKind.ApprovalGranted : DecisionKind.ApprovalDenied,
            new ApprovalAnsweredPayload(MemoryDerivation.DeliveryApprovalAction, approved, decidedBy, approved ? null : "Comentario-de-la-persona-4e9", null) { RunId = run },
            new Actor(ActorKind.User, decidedBy));
        var delivery = approved
            ? await RecordAsync(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(null, "refs/heads/main", [integration], head, [], IsDelivery: true) { RunId = run })
            : null;
        return (answer, delivery);
    }
}
