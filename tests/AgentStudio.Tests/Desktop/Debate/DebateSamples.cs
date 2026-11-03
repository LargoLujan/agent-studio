using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// Synthetic debates for the screens of Phase 5, built by hand with the records P0 fixed (docs/04 §17.3): an architecture
/// review between a Claude architect and a Codex critic, judged by a cross-vendor panel; its contributions with quotes and
/// one quote that did not resolve; the verdicts of both judges; and an audit of the project. No real paths or names.
/// </summary>
internal static class DebateSamples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(2));

    public const string Root = @"C:\proyectos\calc";

    public static readonly ProjectContext Project = new("p-1", Root);

    public static readonly RunId DebateRunId = new("deb-20261019-a1b2c3");

    public static readonly string Commit = new('7', 40);

    public static readonly ModelBinding ClaudeHaiku = new(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku");

    public static readonly ModelBinding Codex = new(ProviderId.CodexCli, VendorId.OpenAI);

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["arquitecto"] = "Arquitecto",
        ["critico"] = "Crítico",
        ["juez-claude"] = "Juez Claude",
        ["juez-revision"] = "Juez de revisión",
        ["orquestador"] = "Orquestador",
        ["ejecutor"] = "Ejecutor",
        ["auditor"] = "Auditor",
    };

    public static AgentDefinition Agent(string id, AgentRole role, ModelBinding model) => new(
        new AgentId(id),
        Names.GetValueOrDefault(id, id),
        role,
        model,
        "Instrucciones de prueba.",
        [],
        ["Read"],
        [],
        new ExecutionBudget(12, TimeSpan.FromMinutes(5)),
        4,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    /// <summary>The template of D-09: two participants of different vendors, two judges, and the three of Phase 3.</summary>
    public static IReadOnlyList<AgentDefinition> Roster { get; } =
    [
        Agent("arquitecto", AgentRole.Architect, ClaudeHaiku),
        Agent("critico", AgentRole.Critic, Codex),
        Agent("juez-claude", AgentRole.Judge, ClaudeHaiku),
        Agent("juez-revision", AgentRole.Judge, Codex),
        Agent("orquestador", AgentRole.Orchestrator, ClaudeHaiku),
        Agent("ejecutor", AgentRole.Executor, ClaudeHaiku),
        Agent("auditor", AgentRole.Auditor, Codex),
    ];

    public static IReadOnlyList<DebateParticipantView> Participants { get; } =
    [
        new("P1", DebateRole.Proponent, new AgentId("arquitecto"), ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5"),
        new("P2", DebateRole.Critic, new AgentId("critico"), ProviderId.CodexCli, VendorId.OpenAI, null),
    ];

    public static JudgeIdentity ClaudeJudge { get; } = new(new AgentId("juez-claude"), new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5"));

    public static JudgeIdentity CodexJudge { get; } = new(new AgentId("juez-revision"), Codex);

    public static FrozenRequirementSet Requirements { get; } = FrozenRequirementSet.Freeze(
    [
        new Requirement(new RequirementId("D-01"), "Mantenibilidad: el diseño se entiende, se prueba y se cambia sin romper lo demás.", RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", "Mantenibilidad", VerificationMethod.JudgeAssessment)]),
        new Requirement(new RequirementId("D-02"), "Seguridad: ningún agente escribe fuera de su carpeta.", RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", "Seguridad", VerificationMethod.HumanReview)]),
    ],
    Now);

    public const string ProposalText = """
        {"kind":"Proposal","repliesTo":"ninguna","summary":"Un proceso aislado por agente.","argument":"Cada agente corre en su proceso con su carpeta de solo lectura.","claims":[{"claim":"El aislamiento por proceso limita el alcance de un fallo.","support":["CITA-1"]}],"citations":[{"id":"CITA-1","path":"src/Agents/Launcher.cs","startLine":10,"endLine":40,"why":"Lanza cada CLI en su proceso."}],"concedes":["ninguna"],"finished":false}
        """;

    public const string SecondProposalText = """
        {"kind":"Proposal","repliesTo":"ninguna","summary":"Un único proceso con hilos.","argument":"Los agentes comparten el proceso y se coordinan con colas; cuesta menos arrancar.","claims":[{"claim":"Un solo proceso reduce el coste de arranque.","support":["CITA-1"]}],"citations":[{"id":"CITA-1","path":"src/Agents/Pool.cs","startLine":200,"endLine":210,"why":"El grupo de hilos que reutilizaría."}],"concedes":["ninguna"],"finished":false}
        """;

    public const string ObjectionText = """
        {"kind":"Objection","repliesTo":"C1","summary":"Falta la política de escritura concurrente.","argument":"Dos agentes pueden tocar el mismo fichero de la copia.","claims":[{"claim":"No hay bloqueo entre turnos.","support":["C1"]}],"citations":[],"concedes":["ninguna"],"finished":true}
        """;

    public static DebateContributionView Contribution(string label, int index, int round, string author, ContributionKind kind, string? repliesTo, string? text, string[]? cites = null, string[]? unresolved = null, int redactions = 0) =>
        new(label, index, round, author, kind, repliesTo, new EvidenceId("ev-" + label.ToLowerInvariant()), new string((char)('a' + index), 64), text, [.. (cites ?? []).Select(c => new EvidenceId(c))], unresolved ?? [], redactions, kind == ContributionKind.Objection, Now.AddMinutes(index));

    public static IReadOnlyList<DebateContributionView> RoundOne { get; } =
    [
        Contribution("C1", 1, 1, "P1", ContributionKind.Proposal, null, ProposalText, ["ev-cita-1"]),
        Contribution("C2", 2, 1, "P2", ContributionKind.Proposal, null, SecondProposalText, unresolved: ["src/Agents/Pool.cs:200-210"]),
    ];

    public static DebateContributionView Objection { get; } = Contribution("C3", 3, 2, "P2", ContributionKind.Objection, "C1", ObjectionText, redactions: 1);

    public static MetricAggregate<long> Tokens(long sum, MetricOrigin origin = MetricOrigin.ReportedByProvider, int reported = 3) =>
        new(sum, origin, reported, 0, ["claude result.usage", "codex turn.completed.usage"]);

    /// <summary>Round 2 of an architecture review, with two turns sealed and the contributions of round 1 published.</summary>
    public static DebateSnapshot Snapshot(
        DebatePhase phase = DebatePhase.Deliberation,
        int round = 2,
        int turnsInFlight = 2,
        IReadOnlyList<DebateContributionView>? contributions = null,
        DebateAwaiting? awaiting = null,
        MetricAggregate<long>? tokens = null,
        int reportedTurns = 3,
        bool canRunTests = true,
        DebateOutcome outcome = DebateOutcome.Open,
        DebateJudgmentReason? judgment = null,
        DebateLimitReached limit = DebateLimitReached.None) => new(
        DebateRunId,
        DebateMode.ArchitectureReview,
        DebateSubjectKind.Commit,
        phase,
        outcome,
        round,
        Now.AddMinutes(-11),
        TimeSpan.FromMinutes(11),
        DebateLimits.Default,
        tokens ?? Tokens(84_000),
        reportedTurns,
        AgentRuns: 3,
        MaxAgentRuns: 10,
        Participants,
        contributions ?? RoundOne,
        turnsInFlight,
        limit,
        judgment,
        awaiting,
        canRunTests,
        []);

    public static Verdict VerdictOf(JudgeIdentity judge, VerdictDecision decision, string? chosen, ConfidenceLevel confidence = ConfidenceLevel.Medium, string head = "head-1") =>
        new(decision,
            $"El juez {judge.Agent} elige {chosen ?? "ninguna"}.",
            [
                new RequirementAssessment(new RequirementId("D-01"), AssessmentStatus.Satisfied, [new EvidenceId("ev-c1")], "La propuesta separa responsabilidades."),
                new RequirementAssessment(new RequirementId("D-02"), AssessmentStatus.NotVerifiable, [], "Falta la revisión de una persona."),
            ],
            [new EvidenceId("ev-c1")],
            ["La objeción C3 sigue abierta."],
            ["Coste de un proceso por agente."],
            [new Finding(FindingCategory.SecurityIssue, Severity.Medium, "Escritura concurrente sin política.", [new EvidenceId("ev-c3")], [41])],
            new Confidence(confidence, "Las dos propuestas son defendibles."),
            ["Definir la política de escritura concurrente."],
            false,
            judge,
            Requirements.ContentHash,
            head,
            Now)
        {
            Resolution = new DebateResolution(chosen, [new ProposalComparison("P1", "Aislamiento claro.", "Más procesos."), new ProposalComparison("P2", "Menos coste.", "Comparte memoria.")]),
        };

    public static JudgedVerdict Judged(string id, Verdict verdict, params VerdictViolation[] violations) => new(id, verdict, violations, false);

    /// <summary>A verdict of the review of a run or of an audit: no proposals, and texts of a review.</summary>
    public static Verdict ReviewVerdict(JudgeIdentity judge, VerdictDecision decision) =>
        VerdictOf(judge, decision, null) with
        {
            Resolution = null,
            Summary = "D-01 se cumple con pruebas del commit; D-02 necesita la revisión de una persona.",
            Confidence = new Confidence(ConfidenceLevel.Medium, "Las pruebas capturadas por el sistema cubren D-01; D-02 no se puede verificar con pruebas."),
            PendingObjections = [],
        };

    /// <summary>The two verdicts of a cross-vendor panel that disagree on the proposal.</summary>
    public static IReadOnlyList<JudgedVerdict> PanelThatDisagrees { get; } =
    [
        Judged("v-claude", VerdictOf(ClaudeJudge, VerdictDecision.Approved, "P1")),
        Judged("v-codex", VerdictOf(CodexJudge, VerdictDecision.Approved, "P2", ConfidenceLevel.High)),
    ];

    public static PanelOutcome Disagree { get; } = new(DebateOutcome.Unresolved, null, null, JudgingPanel.Disagree);

    public static PanelOutcome Agree { get; } = new(DebateOutcome.Resolved, VerdictDecision.Approved, "P1", JudgingPanel.Agree) { Confidence = ConfidenceLevel.Medium };

    public static JudgingArrangement Panel { get; } = new(
        DebateJudgingKind.CrossVendorPanel,
        [ClaudeJudge, CodexJudge],
        new HashSet<VendorId>(),
        new HashSet<VendorId> { VendorId.Anthropic, VendorId.OpenAI });

    public static DebateEstimate Estimate { get; } = new(10, MetricValue<long>.EstimatedBySystem(240_000, "D-21: turns × measured cost"), ["Cada turno arrastra unos 39.000 tokens fijos del CLI."]);

    /// <summary>What the coordinator answers to <paramref name="request"/>: the cross-vendor panel, no problems.</summary>
    public static DebateProposal Proposal(DebateRequest request, params string[] problems) =>
        new(request, Panel, problems, Estimate, new string('c', 64), "Abrir el debate «Revisión de arquitectura» con 2 participantes y un jurado cruzado.");

    public static DebateRequest Request(DebateMode mode = DebateMode.ArchitectureReview) => new(
        Project,
        mode,
        "¿Procesos aislados por agente o un único proceso con hilos?",
        Requirements,
        new DebateSubject(DebateSubjectKind.Commit, null),
        [new DebateParticipantRequest(new AgentId("arquitecto"), DebateRole.Proponent), new DebateParticipantRequest(new AgentId("critico"), DebateRole.Critic)],
        [new AgentId("juez-claude"), new AgentId("juez-revision")],
        DebateJudgingKind.CrossVendorPanel,
        DebateLimits.Default);

    public static DebateSummary Summary(string id, DebateMode mode, DebatePhase phase, DebateOutcome outcome, bool interrupted = false, VerdictDecision? decision = null, string? chosen = null, int minutesAgo = 60, string? challenges = null, bool challenged = false, IReadOnlyList<string>? problems = null, string? question = null) =>
        new(new RunId(id), Now.AddMinutes(-minutesAgo), mode, DebateSubjectKind.None, question ?? $"Pregunta de {id}", phase, outcome, decision, chosen, interrupted, challenged, challenges, problems ?? []);

    public static DebateTranscript Transcript(PanelOutcome? outcome = null, IReadOnlyList<JudgedVerdict>? verdicts = null) => new(
        Summary(DebateRunId.Value, DebateMode.ArchitectureReview, DebatePhase.Closed, outcome?.Outcome ?? DebateOutcome.Unresolved) with { Subject = DebateSubjectKind.Commit, Question = "¿Procesos aislados por agente o un único proceso con hilos?" },
        Requirements.ContentHash,
        DebateJudgingKind.CrossVendorPanel,
        [VendorId.Anthropic, VendorId.OpenAI],
        Participants,
        [.. RoundOne, Objection],
        verdicts ?? PanelThatDisagrees,
        outcome ?? Disagree,
        DebateLimitReached.None,
        DebateJudgmentReason.Converged,
        []);

    public static RunRecord RunRecord(string id, string requirementSetHash) => new(
        new RunId(id),
        Project.ProjectId,
        Now.AddHours(-1),
        new string('d', 64),
        requirementSetHash,
        new EvidenceId("ev-request"),
        Commit,
        "main",
        new VerificationProfile("dotnet", []),
        Roster,
        new RunOptions(),
        null);

    public static StoredVerdict Stored(string id, Verdict verdict, string? run = "20261018-c4a1f2", bool valid = true, string? supersedes = null) =>
        new(id, verdict, run, "R1", AuditScope.CurrentPlan, valid, [], supersedes);

    public static ProjectAuditProposal AuditProposal(ProjectAuditRequest request, AgentDefinition? judge = null, bool uncommitted = true, params string[] problems) => new(
        request,
        Commit,
        uncommitted,
        judge ?? Roster.First(a => a.Id.Value == "juez-revision"),
        [VendorId.Anthropic],
        new VerificationProfile("dotnet", [new VerificationStep("test", "Pruebas", "dotnet", ["test"], EvidenceKind.TestRun, TestReportFormat.Trx, "r.trx", TimeSpan.FromMinutes(10))]),
        ["e2e"],
        MetricValue<long>.EstimatedBySystem(320_000, "D-30: judge reading the copy of HEAD"),
        problems,
        new string('e', 64),
        "Auditar el commit 7777777 con juez-revision (Codex).");

    public static AuditReport AuditReport(string? verdictId = "v-audit", bool intact = true, params VerdictViolation[] issues) => new(
        new RunId("aud-20261019-0f9e8d"),
        AuditScope.WholeProject,
        Commit,
        UncommittedChanges: true,
        ReviewVerdict(CodexJudge, VerdictDecision.ChangesRequested),
        verdictId,
        issues,
        Corrected: false,
        DecisionLogIntact: intact,
        ReviewDisposition.ChangesRequested,
        [new EvidenceRecord(new EvidenceId("ev-test"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "dotnet test (HEAD)", "agent-studio", Now, new string('f', 64), new TestRunSummary(5354, 0, 12, 0), subjectCommit: Commit)],
        [new PendingProblem("run.awaiting-human", "20261018-c4a1f2", "La ejecución 20261018-c4a1f2 espera a una persona."), new PendingProblem("memory.proposal-unreviewed", "m-12", "Una propuesta de memoria sin revisar.")],
        Now);
}
