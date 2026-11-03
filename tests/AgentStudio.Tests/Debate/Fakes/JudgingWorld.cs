using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>What can vary between the runs the judging tests prepare.</summary>
internal sealed record JudgingWorldOptions
{
    public string OriginalRequest { get; init; } = "Añade Calc.Multiply(a,b) a la librería, con sus pruebas.";

    public ModelBinding JudgeModel { get; init; } = new(ProviderId.CodexCli, VendorId.OpenAI);

    public ModelBinding ExecutorModel { get; init; } = new(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-sonnet-4-5");

    public TestRunSummary TestRun { get; init; } = Green;

    /// <summary>When set, a second executor on this binding is in the roster but never assigned nor run.</summary>
    public ModelBinding? IdleExecutorModel { get; init; }

    public string DiffContent { get; init; } = "+ public static int Multiply(int a, int b) => a * b;";

    public string TestRunContent { get; init; } = "<TestRun>…</TestRun>";

    /// <summary>When false, the original request is captured but the log never registers it.</summary>
    public bool RegisterOriginalRequest { get; init; } = true;

    /// <summary>
    /// True records <c>RunStarted</c> as the engine does (version 2, with the hash of the stored header); false, as
    /// versions before the header was anchored did (version 1, without it).
    /// </summary>
    public bool AnchorHeader { get; init; } = true;
}

/// <summary>
/// A finished orchestration run in the in-memory stores, as the engine would leave it when it reaches the review task:
/// Claude orchestrates and executes T1, T2 is the review by the judge (Codex by default). A second run of the same
/// project shares the stores, so filtering by run is exercised too.
/// </summary>
internal sealed class JudgingWorld : IDisposable
{
    public const string Project = "6f1c2b0e-0000-4000-8000-000000000001";
    public static readonly RunId Run = new("20261015-a1b2c3");
    public static readonly RunId OtherRun = new("20261015-ffffff");
    public static readonly TaskId Work = new("T1");
    public static readonly TaskId Review = new("T2");
    public static readonly AgentId Orchestrator = new("orquestador");
    public static readonly AgentId Executor = new("ejecutor");
    public static readonly AgentId IdleExecutor = new("ejecutor-2");
    public static readonly AgentId Judge = new("juez");
    public static readonly Actor System = new(ActorKind.System, "agent-studio");

    private JudgingWorld(JudgingWorldOptions options)
    {
        Options = options;
        Clock = new FakeTimeProvider(T0);
        LogStore = new InMemoryDecisionLogStore();
        Recorder = new DecisionRecorder(LogStore, Clock);
        Evidence = new FakeEvidenceStore(Clock);
        Root = Path.Combine(Path.GetTempPath(), "agentstudio-test-" + Guid.NewGuid().ToString("N"));
        DossierRoot = Path.Combine(Root, "dossiers");
        Workspace = Path.Combine(Root, "wt", "_integration");
        Directory.CreateDirectory(Workspace);
        Frozen = FrozenRequirementSet.Freeze(
        [
            Req("RQ-01", RequirementPriority.Mandatory, "Añadir Calc.Multiply(a,b)", Crit("c1", VerificationMethod.AutomatedTest, "Un test automático prueba Multiply")),
            Req("RQ-02", RequirementPriority.Desirable, "Documentar Multiply", Crit("c2", VerificationMethod.JudgeAssessment, "El código explica Multiply")),
        ],
            T0);
        Roster = BuildRoster(options);
        Plan = PlanDefinition.Create(
            "Añadir Multiply con pruebas",
            [
                new TaskSpec(Work, "Añadir Multiply", WorkTaskKind.AgentWork, Executor, "Implementar Calc.Multiply", "Sigue TDD.", [], JoinPolicy.AllCompleted,
                    [new RequirementId("RQ-01")], ["src/Calc/", "tests/Calc.Tests/"], ["build", "test"], null, null, 1, null, WorkspaceMode.Write),
                new TaskSpec(Review, "Revisión independiente", WorkTaskKind.Review, Judge, "Revisar la ejecución", "Revisa todo.", [new DependencySpec(Work)],
                    JoinPolicy.AllCompleted, [], [], [], null, null, 0, null, WorkspaceMode.ReadOnly),
            ],
            ["Multiply puede desbordar"],
            PlanSource.OrchestratorAgent,
            Orchestrator);
    }

    public JudgingWorldOptions Options { get; }

    public FakeTimeProvider Clock { get; }

    public InMemoryDecisionLogStore LogStore { get; }

    public DecisionRecorder Recorder { get; }

    public FakeEvidenceStore Evidence { get; }

    public FakeRunRegistry Runs { get; } = new();

    public FakePlanStore Plans { get; } = new();

    public FakeRequirementStore Requirements { get; } = new();

    public FakeVerdictStore Verdicts { get; } = new();

    public string Root { get; }

    public string DossierRoot { get; }

    public string Workspace { get; }

    public FrozenRequirementSet Frozen { get; }

    public PlanDefinition Plan { get; }

    public IReadOnlyList<AgentDefinition> Roster { get; }

    public ProjectContext ProjectContext => new(Project, Path.Combine(Root, "repo"));

    public EvidenceRecord RequestEvidence { get; private set; } = null!;

    public EvidenceRecord ReportEvidence { get; private set; } = null!;

    public EvidenceRecord DiffEvidence { get; private set; } = null!;

    public EvidenceRecord TestEvidence { get; private set; } = null!;

    public EvidenceRecord OtherRunEvidence { get; private set; } = null!;

    public AgentDefinition JudgeAgent => Roster.Single(a => a.Id == Judge);

    public static async Task<JudgingWorld> CreateAsync(JudgingWorldOptions? options = null)
    {
        var world = new JudgingWorld(options ?? new JudgingWorldOptions());
        await world.PopulateAsync();
        return world;
    }

    public static AgentDefinition Agent(AgentId id, AgentRole role, ModelBinding model, params PathGrant[] grants) =>
        new(id, id.Value, role, model, string.Empty, [], role is AgentRole.Judge or AgentRole.Orchestrator ? ["Read", "Grep", "Glob"] : [],
            grants, new ExecutionBudget(12, TimeSpan.FromMinutes(10)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));

    public Task<DecisionEntry> RecordAsync(Actor actor, DecisionKind kind, string summary, DecisionPayload payload)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        return Recorder.RecordAsync(Project, actor, kind, summary, payload, cancellationToken: TestContext.Current.CancellationToken);
    }

    public async Task<EvidenceRecord> CaptureAsync(EvidenceKind kind, EvidenceOrigin origin, string title, string content, RunId run, TestRunSummary? testRun = null, string? task = null, string? commit = null)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await Evidence.CaptureAsync(
            Project,
            new EvidenceDraft(kind, origin, title, origin == EvidenceOrigin.ProvidedByHuman ? "usuario" : "agent-studio", testRun, RunId: run.Value, TaskId: task, SubjectCommit: commit),
            stream,
            TestContext.Current.CancellationToken);
    }

    /// <summary>A system test run of the run, bound to the commit it verified and registered, as the engine leaves it after a verification.</summary>
    public async Task<EvidenceRecord> RecordTestRunAsync(TestRunSummary summary, string commit, TaskId? task = null)
    {
        var taskId = task ?? Work;
        var run = await CaptureAsync(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, $"dotnet test ({taskId})", $"<TestRun commit=\"{commit}\"/>", Run, summary, taskId.Value, commit);
        await RegisterAsync(run, Run, taskId, 1);
        return run;
    }

    /// <summary>
    /// An integration as the engine records it: the tests of the merged commit (bound to it and registered), then
    /// <c>IntegrationPerformed</c> with the result. Returns the test run.
    /// </summary>
    /// <param name="verified">
    /// False records an integration whose candidate failed its verification: the engine then names the candidate ref,
    /// not the run's branch, as the target (ADR-0012).
    /// </param>
    public async Task<EvidenceRecord> RecordIntegrationAsync(string resultCommit, TestRunSummary tests, IReadOnlyList<string>? conflictPaths = null, bool delivery = false, bool verified = true)
    {
        var integration = new TaskId("I1");
        var run = await RecordTestRunAsync(tests, resultCommit, integration);
        var target = verified ? $"agentstudio/run-{Run}" : $"refs/agentstudio/{Run}/_candidate/{integration}";
        await RecordAsync(System, DecisionKind.IntegrationPerformed, $"Integración en {resultCommit}",
            new IntegrationPerformedPayload(integration, target, [$"refs/agentstudio/{Run}/T1"], resultCommit, conflictPaths ?? [], delivery) { RunId = Run.Value });
        return run;
    }

    /// <summary>Announces evidence in the log as the engine does (<c>RunRecorder.RegisterAsync</c>): version 2, with its metadata.</summary>
    public Task<DecisionEntry> RegisterAsync(EvidenceRecord evidence, RunId run, TaskId? task = null, int? attempt = null) =>
        RecordAsync(System, DecisionKind.EvidenceRegistered, "Evidencia registrada", EvidenceRegisteredPayload.Anchoring(evidence, run.Value, task, attempt));

    /// <summary>Announces evidence as an earlier version did (version 1): its content, but none of its metadata.</summary>
    public Task<DecisionEntry> RegisterUnanchoredAsync(EvidenceRecord evidence, RunId run, TaskId? task = null, int? attempt = null) =>
        RecordAsync(System, DecisionKind.EvidenceRegistered, "Evidencia registrada",
            new EvidenceRegisteredPayload(evidence.Id, evidence.Kind, evidence.Origin, evidence.ContentSha256, task, attempt) { RunId = run.Value });

    public Task<DecisionLogState> LoadLogAsync() => Recorder.LoadAsync(Project, TestContext.Current.CancellationToken);

    // A junction a test planted is removed as a link, never followed.
    public void Dispose() => TempFolders.Delete(Root);

    private static List<AgentDefinition> BuildRoster(JudgingWorldOptions options)
    {
        var roster = new List<AgentDefinition>
        {
            Agent(Orchestrator, AgentRole.Orchestrator, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-sonnet-4-5")),
            Agent(Executor, AgentRole.Executor, options.ExecutorModel, new PathGrant("src/", FileAccessLevel.ReadWrite)),
            Agent(Judge, AgentRole.Judge, options.JudgeModel),
        };
        if (options.IdleExecutorModel is { } idle)
        {
            roster.Add(Agent(IdleExecutor, AgentRole.Executor, idle, new PathGrant("docs/", FileAccessLevel.ReadWrite)));
        }

        return roster;
    }

    private async Task PopulateAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await Plans.SaveAsync(Project, Plan, ct);
        await Requirements.SaveAsync(Project, Frozen, ct);

        // Every capture of the engine is announced with EvidenceRegistered (RunRecorder), the original request included.
        RequestEvidence = await CaptureAsync(EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, "Petición original", Options.OriginalRequest, Run);
        if (Options.RegisterOriginalRequest)
        {
            await RegisterAsync(RequestEvidence, Run);
        }

        var run = Run.Value;
        await RecordAsync(new Actor(ActorKind.Orchestrator, Orchestrator.Value), DecisionKind.PlanCreated, "Plan creado",
            new PlanCreatedPayload(Plan.ContentHash, PlanSource.OrchestratorAgent, Orchestrator, Plan.Tasks.Count, null) { RunId = run });
        var profile = new VerificationProfile("default", []);
        var header = new RunRecord(Run, Project, Clock.GetUtcNow(), Plan.ContentHash, Frozen.ContentHash, RequestEvidence.Id, "abc123", "main", profile, Roster, new RunOptions(), null);
        await Runs.CreateAsync(header, ct);
        var started = new RunStartedPayload(Plan.ContentHash, Frozen.ContentHash, "abc123", "main", profile.ContentHash,
            [.. Roster.Select(a => new RosterEntry(a.Id, a.Role, a.Model.Provider, a.Model.Vendor))], null) { RunId = run };
        await RecordAsync(System, DecisionKind.RunStarted, "Ejecución iniciada", Options.AnchorHeader
            ? started with { HeaderHash = RunHeaderHash.Of(header), V = RunStartedPayload.HeaderVersion }
            : started);
        foreach (var spec in Plan.Tasks)
        {
            await RecordAsync(System, DecisionKind.TaskCreated, $"Tarea {spec.Id} creada",
                new TaskCreatedPayload(spec.Id, spec.Title, spec.Kind, spec.Join, spec.MaxRetries) { RunId = run });
            await RecordAsync(new Actor(ActorKind.Orchestrator, Orchestrator.Value), DecisionKind.TaskAssigned, $"Tarea {spec.Id} asignada",
                new TaskAssignedPayload(spec.Id, spec.Agent!.Value) { RunId = run });
        }

        var prompt = await CaptureAsync(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, "Prompt de T1", "Implementa Multiply.", Run, task: Work.Value);
        await RegisterAsync(prompt, Run, Work, 1);
        await RecordAsync(System, DecisionKind.AgentRunStarted, "Ejecutor lanzado",
            new AgentRunStartedPayload(Work, 1, Executor, Options.ExecutorModel.Provider, Options.ExecutorModel.Vendor, Options.ExecutorModel.Model, Workspace, prompt.Id, prompt.ContentSha256, []) { RunId = run });
        await RecordAsync(System, DecisionKind.TaskStatusChanged, "T1 en marcha",
            new TaskStatusChangedPayload(Work, WorkTaskStatus.Pending, WorkTaskStatus.Running, "lanzado", null, [], 1) { RunId = run });
        ReportEvidence = await CaptureAsync(EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, "Informe del ejecutor",
            """{"status":"completed","summary":"Multiply añadido; todos los tests pasan"}""", Run, task: Work.Value);
        await RegisterAsync(ReportEvidence, Run, Work, 1);
        await RecordAsync(System, DecisionKind.AgentRunFinished, "Ejecutor terminado",
            new AgentRunFinishedPayload(Work, 1, AttemptOutcome.Succeeded, 0, "sess-exec", null, null, ReportEvidence.Id) { RunId = run });
        DiffEvidence = await CaptureAsync(EvidenceKind.CodeDiff, EvidenceOrigin.CapturedBySystem, "Diff de T1",
            Options.DiffContent, Run, task: Work.Value);
        TestEvidence = await CaptureAsync(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "dotnet test",
            Options.TestRunContent, Run, Options.TestRun, Work.Value);
        foreach (var evidence in new[] { DiffEvidence, TestEvidence })
        {
            await RegisterAsync(evidence, Run, Work, 1);
        }

        await RecordAsync(System, DecisionKind.TaskStatusChanged, "T1 completada",
            new TaskStatusChangedPayload(Work, WorkTaskStatus.Running, WorkTaskStatus.Completed, "verificada", null, [DiffEvidence.Id, TestEvidence.Id], 1) { RunId = run });
        await RecordAsync(System, DecisionKind.TaskStatusChanged, "T2 en marcha",
            new TaskStatusChangedPayload(Review, WorkTaskStatus.Pending, WorkTaskStatus.Running, "revisión", null, [], 1) { RunId = run });

        // Another run of the same project: none of it belongs in the dossier of the first one.
        await RecordAsync(System, DecisionKind.RunStarted, "Otra ejecución",
            new RunStartedPayload(Plan.ContentHash, Frozen.ContentHash, "abc123", null, profile.ContentHash,
                [new RosterEntry(new AgentId("gemini"), AgentRole.Executor, ProviderId.GeminiCli, VendorId.Google)], null) { RunId = OtherRun.Value });
        await RecordAsync(System, DecisionKind.AgentRunStarted, "Otro ejecutor",
            new AgentRunStartedPayload(Work, 1, new AgentId("gemini"), ProviderId.GeminiCli, VendorId.Google, null, Workspace, prompt.Id, prompt.ContentSha256, []) { RunId = OtherRun.Value });
        OtherRunEvidence = await CaptureAsync(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "dotnet test (otra)", "<TestRun/>", OtherRun, Red);
    }
}
