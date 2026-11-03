using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit.Fixtures;

/// <summary>
/// Writes one run through the real <see cref="DecisionRecorder"/> (its log anchored outside the project, so a cut end is
/// seen) as the engine records it, step by step and in its order: <c>Orchestrator.StartAsync</c> (the request, the approval
/// of the plan, <c>RunStarted</c> version 3 and the tasks), <c>AgentAttemptWorker</c> (the prompt as evidence,
/// <c>InstructionsIssued</c>, <c>AgentRunStarted</c> before the task is running, the report, the diff, the verification and
/// the attempt record), the integration (its verification bound to the merged commit before <c>IntegrationPerformed</c>),
/// the review (<c>ReviewRequested</c>, the judge's launch and <c>VerdictIssued</c>), <c>RunFinished</c> and the delivery
/// through the approval gate. A test leaves out, reorders or changes one step: the chain stays valid, the protocol does not
/// (docs/15 D-37). <see cref="AuditAsync"/> reads it all back as the auditor's service will.
/// </summary>
internal sealed class PreparedRun
{
    public static readonly Actor System = new(ActorKind.System, "agent-studio");

    public static readonly Actor Person = new(ActorKind.User, "usuario");

    public static readonly Actor Planner = new(ActorKind.Orchestrator, Orchestrator.Value);

    private readonly Dictionary<TaskId, string> _heads = [];
    private readonly Dictionary<TaskId, EvidenceId> _reports = [];
    private string? _integration;
    private string? _verdict;
    private int _verdicts;

    private PreparedRun(PlanDefinition plan, IReadOnlyList<AgentDefinition> roster, FrozenRequirementSet requirements, VerificationProfile profile)
    {
        Plan = plan;
        Roster = roster;
        Frozen = requirements;
        Profile = profile;
        Clock = new FakeTimeProvider(T0);
        Store = new InMemoryDecisionLogStore();
        LogStore = new CuttableLogStore(Store);
        Anchors = new MemoryDecisionLogAnchors();
        Recorder = new DecisionRecorder(LogStore, Clock, Anchors);
        Evidence = new FakeEvidenceStore(Clock);
    }

    public PlanDefinition Plan { get; }

    public IReadOnlyList<AgentDefinition> Roster { get; }

    public FrozenRequirementSet Frozen { get; }

    public VerificationProfile Profile { get; }

    public FakeTimeProvider Clock { get; }

    public InMemoryDecisionLogStore Store { get; }

    public CuttableLogStore LogStore { get; }

    public MemoryDecisionLogAnchors Anchors { get; }

    public DecisionRecorder Recorder { get; }

    public FakeEvidenceStore Evidence { get; }

    public FakeRunRegistry Runs { get; } = new();

    public FakePlanStore Plans { get; } = new();

    public FakeRequirementStore Requirements { get; } = new();

    public RunRecord Header { get; private set; } = null!;

    /// <summary>The commit the last accepted integration left the run's branch at.</summary>
    public string? IntegrationCommit => _integration;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static PreparedRun Create(PlanDefinition? plan = null, IReadOnlyList<AgentDefinition>? roster = null, VerificationProfile? profile = null) =>
        new(plan ?? F38Plan(), roster ?? AuditWorld.Roster(), AuditWorld.Requirements(), profile ?? AuditWorld.Profile());

    /// <summary>
    /// The clean F3-8 run, every step as the engine takes it: T1 written and verified, I1 integrated and verified, R1
    /// reviewed and approved, and the run finished.
    /// </summary>
    public static async Task<PreparedRun> CleanAsync()
    {
        var run = Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        await run.IntegrateAsync("I1");
        await run.ReviewAsync("R1");
        await run.FinishAsync();
        return run;
    }

    public TaskSpec Spec(string task) => Plan.Tasks.Single(t => t.Id.Value == task);

    public AgentDefinition Definition(AgentId agent) => Roster.Single(a => a.Id == agent);

    public Task<DecisionEntry> RecordAsync(Actor actor, DecisionKind kind, string summary, DecisionPayload payload)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        return Recorder.RecordAsync(AuditWorld.Project, actor, kind, summary, payload, cancellationToken: Ct);
    }

    /// <summary>
    /// <c>Orchestrator.StartAsync</c>: the original request (evidence the person provided), the stored plan, requirements and
    /// header, the approval of the plan, <c>RunStarted</c> version 3 (with the hash of the header) and every task with its
    /// assignment.
    /// </summary>
    /// <param name="approvePlan">False leaves out the approval of the plan.</param>
    /// <param name="anchorHeader">False records <c>RunStarted</c> as version 1 did, without the hash of the header.</param>
    /// <param name="assign">Assignments the log records instead of the plan's, by task.</param>
    public async Task StartAsync(bool approvePlan = true, bool anchorHeader = true, IReadOnlyDictionary<string, AgentId>? assign = null)
    {
        await Plans.SaveAsync(AuditWorld.Project, Plan, Ct);
        await Requirements.SaveAsync(AuditWorld.Project, Frozen, Ct);
        var request = await CaptureAndRegisterAsync(EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, "Petición original", "usuario", "Añade Calc.Multiply(a,b) con sus pruebas", null, null);
        Header = new RunRecord(Run, AuditWorld.Project, Clock.GetUtcNow(), Plan.ContentHash, Frozen.ContentHash, request.Id, BaseCommit, "refs/heads/main", Profile, Roster, new RunOptions(), null);
        await Runs.CreateAsync(Header, Ct);

        var run = Run.Value;
        if (approvePlan)
        {
            await RecordAsync(System, DecisionKind.ApprovalRequested, "Aprobación del plan",
                new ApprovalRequestedPayload(OrchestratorAudit.PlanApprovalAction, $"Ejecutar el plan {Plan.ContentHash[..12]} ({Plan.Tasks.Count} tareas)", RiskLevel.Critical, null, []) { RunId = run });
            await RecordAsync(Person, DecisionKind.ApprovalGranted, "Plan aprobado",
                new ApprovalAnsweredPayload(OrchestratorAudit.PlanApprovalAction, true, "usuario", "adelante", null) { RunId = run });
        }

        var started = new RunStartedPayload(
            Plan.ContentHash,
            Frozen.ContentHash,
            BaseCommit,
            "refs/heads/main",
            Profile.ContentHash,
            [.. Roster.Select(a => new RosterEntry(a.Id, a.Role, a.Model.Provider, a.Model.Vendor))],
            null) { RunId = run };
        await RecordAsync(System, DecisionKind.RunStarted, $"Arranca la ejecución {run}", anchorHeader
            ? started with { HeaderHash = RunHeaderHash.Of(Header), ApprovedWarning = null, ApprovedWriters = [], V = RunStartedPayload.WritersVersion }
            : started);

        foreach (var task in Plan.Tasks)
        {
            await RecordAsync(System, DecisionKind.TaskCreated, $"Tarea {task.Id}: {task.Title}",
                new TaskCreatedPayload(task.Id, task.Title, task.Kind, task.Join, task.MaxRetries) { RunId = run });
            var agent = assign is not null && assign.TryGetValue(task.Id.Value, out var other) ? other : task.Agent;
            if (agent is { } assigned)
            {
                await RecordAsync(Planner, DecisionKind.TaskAssigned, $"{task.Id} asignada a {assigned}", new TaskAssignedPayload(task.Id, assigned) { RunId = run });
            }
        }
    }

    /// <summary>A transition of a task, as the engine's loop records it.</summary>
    public Task<DecisionEntry> TransitionAsync(string task, WorkTaskStatus from, WorkTaskStatus to, int attempt = 1, IReadOnlyList<EvidenceId>? evidence = null, string? block = null) =>
        RecordAsync(System, DecisionKind.TaskStatusChanged, $"{task}: {from} → {to}",
            new TaskStatusChangedPayload(Tid(task), from, to, $"{from} → {to}", block, evidence ?? [], attempt) { RunId = Run.Value });

    /// <summary>
    /// The start of an attempt of an agent task (<c>AgentAttemptWorker</c> and <c>AgentRunner</c>): the worktree the first
    /// time, the prompt as evidence, <c>InstructionsIssued</c> with the requirements and the write scope of the plan,
    /// <c>AgentRunStarted</c> version 2, and then the task becomes running. Returns the prompt.
    /// </summary>
    /// <param name="instructions">False launches the agent without <c>InstructionsIssued</c>.</param>
    /// <param name="prompt">Changes the prompt the engine would build.</param>
    /// <param name="covers">The requirements <c>InstructionsIssued</c> records instead of the plan's.</param>
    /// <param name="scope">The write scope <c>InstructionsIssued</c> records instead of the plan's.</param>
    /// <param name="agent">The agent launched instead of the plan's.</param>
    /// <param name="running">False leaves the task as it was (the loop has not heard of the launch yet).</param>
    /// <param name="memory">The pack of project memory <c>AgentRunStarted</c> says the prompt carried.</param>
    public async Task<EvidenceRecord> LaunchAsync(
        string task,
        int attempt = 1,
        bool instructions = true,
        Func<string, string>? prompt = null,
        IReadOnlyList<RequirementId>? covers = null,
        IReadOnlyList<string>? scope = null,
        AgentId? agent = null,
        bool running = true,
        ContextPackReference? memory = null)
    {
        var spec = Spec(task);
        var run = Run.Value;
        if (attempt == 1 && spec.Workspace != WorkspaceMode.None)
        {
            await RecordAsync(System, DecisionKind.WorktreeCreated, $"Worktree de {task}",
                new WorktreeCreatedPayload(spec.Id, task, $@"C:\wt\{run}\{task}", $"refs/agentstudio/{run}/{task}", BaseCommit) { RunId = run });
        }

        var text = PromptOf(spec, attempt);
        var captured = await CaptureAndRegisterAsync(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, $"Prompt de {task} (intento {attempt})", "agent-studio",
            prompt?.Invoke(text) ?? text, spec.Id, attempt);
        if (instructions)
        {
            await RecordAsync(System, DecisionKind.InstructionsIssued, $"Instrucciones para {task} (intento {attempt})",
                new InstructionsIssuedPayload(spec.Id, attempt, captured.Id, captured.ContentSha256, covers ?? spec.Covers, scope ?? spec.WriteScope, []) { RunId = run });
        }

        var launched = Definition(agent ?? spec.Agent!.Value);
        await RecordAsync(System, DecisionKind.AgentRunStarted, $"Arranca {launched.Id} para {task}, intento {attempt}",
            new AgentRunStartedPayload(spec.Id, attempt, launched.Id, launched.Model.Provider, launched.Model.Vendor, launched.Model.Model, $@"C:\wt\{run}\{task}", captured.Id, captured.ContentSha256, [], AgentRunPurpose.Executor, memory)
            {
                RunId = run,
                V = AgentRunStartedPayload.PurposeVersion,
            });
        if (running)
        {
            await TransitionAsync(task, WorkTaskStatus.Pending, WorkTaskStatus.Running, attempt);
        }

        return captured;
    }

    /// <summary>
    /// The end of an attempt of an agent task (<c>AgentRunner</c> and <c>ConcludeAsync</c>): the report as evidence the agent
    /// reported, <c>AgentRunFinished</c> version 2, the diff, the verification bound to the commit the system made, the
    /// attempt record and the transition, which cites the record first. Returns the attempt record.
    /// </summary>
    /// <param name="to">Where the task goes: Completed, or Failed.</param>
    /// <param name="changedPaths">The paths the attempt record lists; by default one file in each folder of the write scope.</param>
    /// <param name="diff">False captures no diff.</param>
    /// <param name="tests">The counters of the system's test run of the task.</param>
    public async Task<EvidenceRecord> EndAttemptAsync(string task, int attempt = 1, WorkTaskStatus to = WorkTaskStatus.Completed, IReadOnlyList<string>? changedPaths = null, bool diff = true, TestRunSummary? tests = null)
    {
        var spec = Spec(task);
        var run = Run.Value;
        var agent = spec.Agent!.Value;
        var report = await CaptureAndRegisterAsync(EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, $"Informe de {task} (intento {attempt})", agent.Value,
            """{"status":"completed","summary":"Multiply añadido; los tests pasan","filesChanged":[],"claims":[],"openIssues":[],"outputs":[]}""", spec.Id, attempt);
        await RecordAsync(System, DecisionKind.AgentRunFinished, $"Termina {agent} para {task}, intento {attempt}",
            new AgentRunFinishedPayload(spec.Id, attempt, AttemptOutcome.Succeeded, 0, $"sesion-{task}-{attempt}", null, null, report.Id, Clock.GetUtcNow(), "claude-haiku-4-5") { RunId = run, V = AgentRunFinishedPayload.UsageVersion });

        var writes = spec.Workspace == WorkspaceMode.Write;
        var head = writes ? CommitOf($"{task}#{attempt}") : BaseCommit;
        var paths = changedPaths ?? (writes ? [.. spec.WriteScope.Select(folder => folder + "Code.cs")] : []);
        var evidence = new List<EvidenceId> { report.Id };
        if (diff && paths.Count > 0)
        {
            evidence.Add((await CaptureAndRegisterAsync(EvidenceKind.CodeDiff, EvidenceOrigin.CapturedBySystem, $"Diff de {task} (intento {attempt})", "agent-studio",
                string.Join("\n", paths.Select(p => "+++ b/" + p)), spec.Id, attempt)).Id);
        }

        if (writes)
        {
            evidence.AddRange(await VerifyAsync(spec, attempt, head, tests ?? Green));
        }

        var outcome = to == WorkTaskStatus.Completed ? "Completed" : "Failed";
        var record = await AttemptRecordAsync(spec, attempt, outcome, BaseCommit, head, paths, report);
        if (to == WorkTaskStatus.Completed)
        {
            _heads[spec.Id] = head;
            _reports[spec.Id] = report.Id;
        }

        await TransitionAsync(task, WorkTaskStatus.Running, to, attempt, [record.Id, .. evidence]);
        return record;
    }

    /// <summary>One attempt of an agent task, start to end.</summary>
    public async Task WorkAsync(string task, int attempt = 1)
    {
        await LaunchAsync(task, attempt);
        await EndAttemptAsync(task, attempt);
    }

    /// <summary>
    /// An integration (<c>SystemWorkers.IntegrationAsync</c>): the task runs, the integrator verifies the merged commit and
    /// its evidence is registered bound to that commit, <c>IntegrationPerformed</c> records it on the run's branch (or, not
    /// accepted, on the candidate ref), then the attempt record and the transition.
    /// </summary>
    /// <param name="accepted">False records the integration on its candidate ref and fails the task.</param>
    /// <param name="build">False captures no build of the merged commit.</param>
    /// <param name="tests">False captures no test run of the merged commit.</param>
    /// <param name="summary">The counters of the test run of the merged commit.</param>
    /// <param name="complete">False leaves the task running (the test decides what happens next).</param>
    public async Task<string> IntegrateAsync(string task, bool accepted = true, bool build = true, bool tests = true, TestRunSummary? summary = null, bool complete = true)
    {
        var spec = Spec(task);
        var run = Run.Value;
        await TransitionAsync(task, WorkTaskStatus.Pending, WorkTaskStatus.Running, 1);
        var commit = CommitOf("merge-" + task);
        var evidence = new List<EvidenceId>();
        if (build)
        {
            evidence.Add((await CaptureAndRegisterAsync(EvidenceKind.BuildLog, EvidenceOrigin.CapturedBySystem, "Compilar (build)", "agent-studio", "build ok", spec.Id, 1, commit: commit)).Id);
        }

        if (tests)
        {
            evidence.Add((await CaptureAndRegisterAsync(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "Pruebas (test)", "agent-studio", "<TestRun/>", spec.Id, 1, summary ?? Green, commit)).Id);
        }

        var sources = spec.DependsOn.Select(d => $"refs/agentstudio/{run}/{d.Prerequisite}").ToList();
        var target = accepted ? OrchestratorAudit.RunBranch(Run) : $"refs/agentstudio/{run}/_candidate/{task}";
        await RecordAsync(System, DecisionKind.IntegrationPerformed, $"Integración de {task} en {commit}",
            new IntegrationPerformedPayload(spec.Id, target, sources, commit, [], false) { RunId = run });
        if (accepted)
        {
            _integration = commit;
        }

        var record = await AttemptRecordAsync(spec, 1, accepted ? "Completed" : "Failed", BaseCommit, commit, [], null);
        if (complete)
        {
            await TransitionAsync(task, WorkTaskStatus.Running, accepted ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, 1, [record.Id, .. evidence]);
        }

        return commit;
    }

    /// <summary>A system test run of a commit, registered for a task, as a verification leaves it.</summary>
    public Task<EvidenceRecord> RegisterTestRunAsync(string task, string commit, TestRunSummary? summary = null) =>
        CaptureAndRegisterAsync(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "Pruebas (test)", "agent-studio", "<TestRun/>", Tid(task), 1, summary ?? Green, commit);

    /// <summary>
    /// A review (<c>SystemWorkers.ReviewAsync</c> and <c>IndependentReview</c>): the task runs, <c>ReviewRequested</c>, the
    /// judge's launch (its prompt and answer captured but announced by the launch itself, never registered, and with no
    /// task, as <c>JudgeRunRecorder</c> does), <c>VerdictIssued</c> and the transition.
    /// </summary>
    /// <param name="request">False leaves out <c>ReviewRequested</c>.</param>
    /// <param name="verdict">False leaves out <c>VerdictIssued</c>.</param>
    public async Task ReviewAsync(string task, bool request = true, bool verdict = true, VerdictDecision decision = VerdictDecision.Approved)
    {
        var spec = Spec(task);
        var run = Run.Value;
        var judge = Definition(spec.Agent!.Value);
        await TransitionAsync(task, WorkTaskStatus.Pending, WorkTaskStatus.Running, 1);
        await RecordAsync(System, DecisionKind.WorktreeCreated, $"Worktree de {task}",
            new WorktreeCreatedPayload(spec.Id, task, $@"C:\wt\{run}\{task}", $"refs/agentstudio/{run}/{task}", _integration ?? BaseCommit) { RunId = run });
        if (request)
        {
            await RecordAsync(System, DecisionKind.ReviewRequested, $"Revisión pedida a {judge.Id}", new ReviewRequestedPayload(spec.Id, judge.Id) { RunId = run });
        }

        var prompt = await CaptureAsync(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, "Prompt del juez (intento 1)", "agent-studio", "Revisa el dossier.", spec.Id, 1);
        await RecordAsync(System, DecisionKind.AgentRunStarted, $"Juez '{judge.Id}' lanzado (intento 1)",
            new AgentRunStartedPayload(null, 1, judge.Id, judge.Model.Provider, judge.Model.Vendor, judge.Model.Model, $@"C:\wt\{run}\{task}", prompt.Id, prompt.ContentSha256, [], AgentRunPurpose.Judge)
            {
                RunId = run,
                V = AgentRunStartedPayload.PurposeVersion,
            });
        var answer = await CaptureAsync(EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, "Respuesta del juez (intento 1)", judge.Id.Value, """{"decision":"Approved"}""", spec.Id, 1);
        await RecordAsync(System, DecisionKind.AgentRunFinished, $"Juez '{judge.Id}' terminado",
            new AgentRunFinishedPayload(null, 1, AttemptOutcome.Succeeded, 0, "sesion-del-juez", null, null, answer.Id, Clock.GetUtcNow()) { RunId = run, V = AgentRunFinishedPayload.UsageVersion });
        if (verdict)
        {
            _verdict = "veredicto-" + (++_verdicts).ToString(CultureInfo.InvariantCulture);
            var head = (await Recorder.LoadAsync(AuditWorld.Project, Ct)).HeadHash;
            await RecordAsync(new Actor(ActorKind.Judge, judge.Id.Value), DecisionKind.VerdictIssued, $"Veredicto {decision} válido",
                new VerdictIssuedPayload(_verdict, spec.Id, decision, true, [], Frozen.ContentHash, head, null, "Juez en 'openai'.") { RunId = run });
        }

        await TransitionAsync(task, WorkTaskStatus.Running, WorkTaskStatus.Completed, 1);
    }

    /// <summary>A condition (<c>EvaluateConditionAsync</c>): running, <c>ConditionEvaluated</c> with its branch, completed.</summary>
    public async Task EvaluateAsync(string task, bool result)
    {
        var spec = Spec(task);
        var branch = result ? ConditionBranches.Yes : ConditionBranches.No;
        await TransitionAsync(task, WorkTaskStatus.Pending, WorkTaskStatus.Running, 1);
        await RecordAsync(System, DecisionKind.ConditionEvaluated, $"Condición {task} → {branch}",
            new ConditionEvaluatedPayload(spec.Id, spec.Condition!.GetType().Name, result, branch, []) { RunId = Run.Value });
        await TransitionAsync(task, WorkTaskStatus.Running, WorkTaskStatus.Completed, 1);
    }

    /// <summary><c>FinishAsync</c> of the engine's loop.</summary>
    public Task<DecisionEntry> FinishAsync(RunPhase phase = RunPhase.Succeeded) =>
        RecordAsync(System, DecisionKind.RunFinished, $"La ejecución {Run} termina: {phase}",
            new RunFinishedPayload(phase, phase.ToString(), _integration, _verdict) { RunId = Run.Value });

    /// <summary>
    /// <c>DeliveryService.DeliverAsync</c>: the approval gate records the request and the person's answer, then the delivery
    /// is recorded as an integration into the user's branch.
    /// </summary>
    /// <param name="approve">False delivers without asking.</param>
    public async Task DeliverAsync(bool approve = true)
    {
        var run = Run.Value;
        var integration = _integration ?? throw new InvalidOperationException("Nothing was integrated: there is nothing to deliver.");
        if (approve)
        {
            await RecordAsync(System, DecisionKind.ApprovalRequested, $"Aprobación pedida (Critical): {OrchestratorAudit.DeliveryApprovalAction}",
                new ApprovalRequestedPayload(OrchestratorAudit.DeliveryApprovalAction, $"Entregar la ejecución {run}.", RiskLevel.Critical, null, ["src/T1/Code.cs"]) { RunId = run });
            await RecordAsync(Person, DecisionKind.ApprovalGranted, $"Aprobado por usuario: {OrchestratorAudit.DeliveryApprovalAction}",
                new ApprovalAnsweredPayload(OrchestratorAudit.DeliveryApprovalAction, true, "usuario", null, null) { RunId = run });
        }

        await RecordAsync(System, DecisionKind.IntegrationPerformed, $"Entrega de la ejecución {run} en main",
            new IntegrationPerformedPayload(null, "refs/heads/main", [integration], CommitOf("delivered"), [], true) { RunId = run });
    }

    /// <summary>
    /// The audit as its service will run it: the verified log, the stored header, plan and requirements, and the content of
    /// the evidence it asks for. <paramref name="change"/> lets a test hand it other material.
    /// </summary>
    public async Task<OrchestratorAuditOutcome> AuditAsync(Func<OrchestratorAuditInput, OrchestratorAuditInput>? change = null)
    {
        var log = await Recorder.LoadAsync(AuditWorld.Project, Ct);
        var started = RunLog.RunStarted(RunLog.EntriesOf(log.Entries, Run.Value));
        var header = await Runs.FindAsync(AuditWorld.Project, Run, Ct);
        var plan = started is null ? null : await Plans.FindAsync(AuditWorld.Project, started.PlanHash, Ct);
        var requirements = started is null ? null : await Requirements.FindAsync(AuditWorld.Project, started.RequirementSetHash, Ct);
        var contents = new Dictionary<EvidenceId, ReadOnlyMemory<byte>>();
        foreach (var wanted in OrchestratorAudit.EvidenceToRead(log.Entries, Run))
        {
            try
            {
                await using var stream = await Evidence.OpenContentAsync(AuditWorld.Project, wanted.Evidence, Ct);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, Ct);
                contents[wanted.Evidence] = buffer.ToArray();
            }
            catch (FileNotFoundException)
            {
                // Content that cannot be opened is left out: the audit says so.
            }
        }

        var input = new OrchestratorAuditInput(Run, log, header, plan, requirements, contents);
        return OrchestratorAudit.Audit(change?.Invoke(input) ?? input);
    }

    /// <summary>What the auditor's service records of a pass: an alert for each new finding and the request to replan.</summary>
    public async Task RecordOutcomeAsync(OrchestratorAuditOutcome outcome)
    {
        foreach (var finding in outcome.NewFindings)
        {
            await RecordAsync(OrchestratorAudit.Auditor, DecisionKind.AuditAlertRaised, $"Hallazgo del auditor: {finding.Code}", OrchestratorAudit.AlertOf(finding, Run));
        }

        if (outcome.Replan is { } replan)
        {
            await RecordAsync(OrchestratorAudit.Auditor, DecisionKind.ReplanRequested, "El auditor pide replanificar", replan);
        }
    }

    /// <summary>The entries of the run in the log, as the store holds them.</summary>
    public IReadOnlyList<DecisionEntry> Entries => Store.Of(AuditWorld.Project);

    /// <summary>The sequence of the last entry of a kind, for the assertions.</summary>
    public long Last(DecisionKind kind) => Entries.Last(e => e.Kind == kind).Sequence;

    /// <summary>The prompt of an executor as <c>ExecutorPrompt.Build</c> lays it out: the objective, then each covered requirement quoted literally.</summary>
    public string PromptOf(TaskSpec spec, int attempt)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Tarea {spec.Id} de Agent Studio: {spec.Title}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Ejecución {Run}, intento {attempt}.");
        text.AppendLine();
        text.AppendLine("## Objetivo");
        text.AppendLine(spec.Objective);
        text.AppendLine();
        text.AppendLine("## Instrucciones de la tarea");
        text.AppendLine(spec.Instructions);
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"## Requisitos que cubre (conjunto congelado {Frozen.ContentHash})");
        foreach (var requirement in spec.Covers.Select(Frozen.Find).OfType<Requirement>())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"### {requirement.Id} ({(requirement.Priority == RequirementPriority.Mandatory ? "obligatorio" : "deseable")})");
            text.AppendLine(requirement.Statement);
            foreach (var criterion in requirement.Criteria)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- Criterio {criterion.Id} ({criterion.Method}): {criterion.Description}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Alcance de escritura");
        foreach (var entry in spec.WriteScope)
        {
            text.AppendLine("- " + entry);
        }

        return text.ToString();
    }

    private async Task<IReadOnlyList<EvidenceId>> VerifyAsync(TaskSpec spec, int attempt, string commit, TestRunSummary tests)
    {
        var evidence = new List<EvidenceId>();
        foreach (var step in spec.VerificationSteps.Select(id => Profile.Find(id)!))
        {
            evidence.Add((await CaptureAndRegisterAsync(step.Kind, EvidenceOrigin.CapturedBySystem, $"{step.Title} ({step.Id})", "agent-studio", step.Id + " output", spec.Id, attempt,
                step.Kind == EvidenceKind.TestRun ? tests : null, commit)).Id);
        }

        return evidence;
    }

    /// <summary>The attempt record (<c>AttemptRecord.ToJson</c>), captured by the system and registered.</summary>
    private Task<EvidenceRecord> AttemptRecordAsync(TaskSpec spec, int attempt, string outcome, string attemptBase, string head, IReadOnlyList<string> changedPaths, EvidenceRecord? report)
    {
        var json = JsonSerializer.Serialize(new
        {
            v = 1,
            kind = OrchestratorAudit.AttemptRecordKind,
            run = Run.Value,
            task = spec.Id.Value,
            attempt,
            outcome,
            attemptBase,
            head,
            changedPaths,
            outputs = Array.Empty<object>(),
            tests = (object?)null,
            reportEvidence = report?.Id.Value,
            reportSha256 = report?.ContentSha256,
        });
        return CaptureAndRegisterAsync(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, $"{OrchestratorAudit.AttemptRecordTitlePrefix} {spec.Id} #{attempt}", "agent-studio", json, spec.Id, attempt);
    }

    private async Task<EvidenceRecord> CaptureAndRegisterAsync(EvidenceKind kind, EvidenceOrigin origin, string title, string producedBy, string content, TaskId? task, int? attempt, TestRunSummary? tests = null, string? commit = null)
    {
        var record = await CaptureAsync(kind, origin, title, producedBy, content, task, attempt, tests, commit);
        await RecordAsync(System, DecisionKind.EvidenceRegistered, $"Evidencia {record.Id}: {record.Title}", EvidenceRegisteredPayload.Anchoring(record, Run.Value, task, attempt));
        return record;
    }

    private async Task<EvidenceRecord> CaptureAsync(EvidenceKind kind, EvidenceOrigin origin, string title, string producedBy, string content, TaskId? task, int? attempt, TestRunSummary? tests = null, string? commit = null)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await Evidence.CaptureAsync(
            AuditWorld.Project,
            new EvidenceDraft(kind, origin, title, producedBy, tests, RunId: Run.Value, TaskId: task?.Value, Attempt: attempt, MediaType: "text/plain", SubjectCommit: commit),
            stream,
            Ct);
    }
}

/// <summary>
/// The payload of the kinds of entry no component writes yet and that have no typed payload (<c>PermissionDenied</c>,
/// <c>ReviewSkipped</c>): whatever a future writer puts there, it names its run and its task.
/// </summary>
internal sealed record FreeFormStandIn(TaskId? Task, string Reason) : DecisionPayload;

/// <summary>The decision_log table, read back with its last entries missing when a test says so: a log whose end was cut off.</summary>
internal sealed class CuttableLogStore(InMemoryDecisionLogStore inner) : IDecisionLogStore
{
    /// <summary>How many entries at the end of the log reading leaves out.</summary>
    public int CutLast { get; set; }

    public Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken) => inner.AppendAsync(projectId, entry, cancellationToken);

    public async Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken)
    {
        var all = await inner.ReadAllAsync(projectId, cancellationToken);
        return [.. all.Take(Math.Max(0, all.Count - CutLast))];
    }
}
