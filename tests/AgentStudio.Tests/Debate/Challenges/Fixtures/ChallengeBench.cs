using System.Runtime.CompilerServices;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Challenges;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Challenges.Fixtures;

/// <summary>
/// The challenge service over the log of a <see cref="PreparedRun"/> (Claude's F3-8 run, reviewed by Codex's
/// <see cref="AuditWorld.Judge"/> with the verdict <see cref="RunVerdict"/>), the verdicts of the store, a template with
/// another Codex judge (<see cref="Revisor"/>, another model), Claude's judge and two participants of each vendor, a
/// double of the coordinator of debates (WP1) that records what the real one records when it opens, and an approval gate
/// that records its answers in the log as the real one does.
/// </summary>
internal sealed class ChallengeBench
{
    public const string RunVerdict = "veredicto-1";

    public static readonly AgentId Revisor = new("juez-revision");

    public static readonly AgentId ClaudeJudge = new("juez-claude");

    public static readonly AgentId Architect = new("arquitecto");

    public static readonly AgentId Critic = new("critico");

    public static readonly AgentId SecondArchitect = new("arquitecto-2");

    public static readonly JudgeIdentity CodexJudge = new(Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI));

    public ChallengeBench(PreparedRun world, IReadOnlyList<AgentDefinition>? template = null)
    {
        World = world;
        Template = new FakeTemplate(template ?? DefaultTemplate());
        Gate = new RecordingGate(world);
        Coordinator = new FakeCoordinator(world, Template);
        Recorder = new LoadSpyRecorder(World.Recorder);
        Service = new VerdictChallengeService(Recorder, Verdicts, World.Requirements, World.Evidence, Template, Coordinator, Gate);
    }

    public PreparedRun World { get; }

    public FakeTemplate Template { get; }

    public FakeVerdictStore Verdicts { get; } = new();

    public RecordingGate Gate { get; }

    public FakeCoordinator Coordinator { get; }

    /// <summary>The log of the world as the service reads and writes it, telling which loads it asks for.</summary>
    public LoadSpyRecorder Recorder { get; }

    public VerdictChallengeService Service { get; }

    public ProjectContext Project { get; } = new(AuditWorld.Project, @"C:\proyecto");

    public IReadOnlyList<DecisionEntry> Entries => World.Entries;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IReadOnlyList<AgentDefinition> DefaultTemplate() =>
    [
        .. Roster(),
        Agent(Revisor, AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5.1-codex-max"), ["Read", "Grep", "Glob"]),
        Agent(ClaudeJudge, AgentRole.Judge, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"), ["Read", "Grep", "Glob"]),
        Agent(Architect, AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet"), ["Read", "Grep", "Glob"]),
        Agent(SecondArchitect, AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"), ["Read", "Grep", "Glob"]),
        Agent(Critic, AgentRole.Executor, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI), ["Read", "Grep", "Glob"]),
    ];

    /// <summary>The clean F3-8 run, delivered, with its verdict in the store as the review kept it.</summary>
    public static async Task<ChallengeBench> OverRunAsync(IReadOnlyList<AgentDefinition>? template = null, VerdictDecision decision = VerdictDecision.Approved)
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        await run.IntegrateAsync("I1");
        await run.ReviewAsync("R1", decision: decision);
        await run.FinishAsync();
        var bench = new ChallengeBench(run, template);
        await bench.Verdicts.AppendAsync(AuditWorld.Project, new StoredVerdict(RunVerdict, VerdictOf(decision, CodexJudge, run.Frozen.ContentHash), Run.Value, "R1", AuditScope.CurrentPlan, true, [], null), Ct);
        return bench;
    }

    public static Verdict VerdictOf(VerdictDecision decision, JudgeIdentity judge, string requirements) => new(
        decision,
        $"Veredicto {decision}.",
        [],
        [],
        [],
        [],
        [],
        new Confidence(ConfidenceLevel.Medium, "Material suficiente."),
        decision is VerdictDecision.ChangesRequested or VerdictDecision.Rejected ? ["Repetir T1"] : [],
        decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved,
        judge,
        requirements,
        new string('e', 64),
        T0);

    /// <summary>The revision asked by the card: an adversarial debate of Claude's architect (who defends the verdict) and Codex's critic.</summary>
    public DebateRequest Revision(IReadOnlyList<AgentId>? judges = null, params (AgentId Agent, DebateRole Role)[] participants) =>
        new(
            Project,
            DebateMode.Free,
            "lo que escriba la tarjeta",
            World.Frozen,
            new DebateSubject(DebateSubjectKind.None, null),
            participants.Length == 0
                ? [new DebateParticipantRequest(Architect, DebateRole.Proponent), new DebateParticipantRequest(Critic, DebateRole.Critic)]
                : [.. participants.Select(p => new DebateParticipantRequest(p.Agent, p.Role))],
            judges ?? [],
            DebateJudgingKind.IndependentJudge,
            DebateLimits.Default);

    public VerdictChallengeRequest Request(string verdict = RunVerdict, string reasons = "El juez aprobó sin mirar que T1 no prueba el desbordamiento.", DebateRequest? revision = null) =>
        new(Project, verdict, reasons, revision ?? Revision());

    public Task<DebateProposal> PrepareAsync(VerdictChallengeRequest request) => Service.PrepareAsync(request, Ct);

    public Task<IDebateRun> ChallengeAsync(DebateProposal proposal) => Service.ChallengeAsync(proposal, Ct);

    public Task<ChallengeStatus> StatusAsync(string verdict = RunVerdict) => Service.StatusAsync(Project, verdict, Ct);

    public Task AcceptAsync(string revisionVerdict) => Service.AcceptRevisionAsync(Project, revisionVerdict, Ct);

    /// <summary>
    /// The judgment of a revision as the judgment of debates keeps it (WP2): each verdict stored and announced in the unit of
    /// the challenged verdict, superseding it, and then <c>DebateClosed</c> with them.
    /// </summary>
    public async Task<IReadOnlyList<string>> JudgeRevisionAsync(string debate, string challenged, string unit, DebateOutcome outcome, string rule, params (AgentId Judge, VerdictDecision Decision, bool Valid)[] verdicts)
    {
        var ids = new List<string>();
        foreach (var (judge, decision, valid) in verdicts)
        {
            var id = $"{debate}-{judge}";
            var identity = Template.Find(judge) is { } agent ? new JudgeIdentity(agent.Id, agent.Model) : CodexJudge;
            await Verdicts.AppendAsync(AuditWorld.Project, new StoredVerdict(id, VerdictOf(decision, identity, World.Frozen.ContentHash), unit, "juez-" + judge, AuditScope.CurrentPlan, valid, [], challenged), Ct);
            await World.RecordAsync(new Actor(ActorKind.Judge, judge.Value), DecisionKind.VerdictIssued, $"Veredicto {decision}",
                new VerdictIssuedPayload(id, new TaskId("juez-" + judge), decision, valid, [], World.Frozen.ContentHash, new string('e', 64), challenged, "Revisión.") { RunId = unit });
            ids.Add(id);
        }

        await World.RecordAsync(PreparedRun.System, DecisionKind.DebateClosed, "Revisión cerrada",
            new DebateClosedPayload(outcome, outcome == DebateOutcome.Aborted ? [] : ids, rule, null, "Cierre.") { RunId = debate });
        return ids;
    }
}

/// <summary>
/// The coordinator of debates (WP1) as a double: it prepares with <see cref="DebateJudgingRules.Possible"/> over the template
/// and the producers it is told (the real one inherits them from the challenged object), and opens as the real one records an
/// opening: the approval of <c>debate.open</c> by a person, then <c>DebateOpened</c> naming the challenged verdict.
/// </summary>
internal sealed class FakeCoordinator(PreparedRun world, FakeTemplate template) : IDebateCoordinator
{
    private int _debates;

    public List<DebateRequest> Prepared { get; } = [];

    public List<DebateProposal> Opened { get; } = [];

    /// <summary>The producers the coordinator inherits for the revision: Claude produced the F3-8 run.</summary>
    public IReadOnlySet<VendorId> Producers { get; set; } = new HashSet<VendorId> { VendorId.Anthropic };

    public bool Approve { get; set; } = true;

    public List<FakeDebateRun> Runs { get; } = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Task<DebateProposal> PrepareAsync(DebateRequest request, CancellationToken ct)
    {
        Prepared.Add(request);
        var participants = request.Participants.Select(p => template.Find(p.Agent) is { } agent ? new DebateParticipant(agent.Id, p.Role, agent.Model) : null).OfType<DebateParticipant>().ToList();
        var candidates = request.Judges.Select(template.Find).OfType<AgentDefinition>().Select(j => new JudgeIdentity(j.Id, j.Model)).ToList();
        var arrangement = DebateJudgingRules.Possible(request.Mode, request.Subject.Kind, participants, Producers, candidates).FirstOrDefault(a => a.Kind == request.Judging);
        IReadOnlyList<string> problems = arrangement is null ? [$"No cabe ninguna disposición {request.Judging}."] : [];
        var hash = ContentHash.Sha256Hex(ContentHash.Canonical(
        [
            request.Mode.ToString(), request.Question, request.Requirements.ContentHash, request.Subject.Kind.ToString(), request.Subject.Reference, request.Judging.ToString(),
            string.Join(",", request.Participants.Select(p => $"{p.Agent}:{p.Role}")), string.Join(",", request.Judges.Select(j => j.Value)),
            string.Join(",", Producers.Select(v => v.Value).Order(StringComparer.Ordinal)),
        ]));
        return Task.FromResult(new DebateProposal(
            request,
            arrangement ?? new JudgingArrangement(request.Judging, [], Producers, new HashSet<VendorId>()),
            problems,
            new DebateEstimate(10, MetricValue<long>.EstimatedBySystem(400_000, "estimate: test"), ["Estimado por Agent Studio."], [UserText.Of(DebateUserTexts.NoteEstimated)]),
            hash,
            "Abrir un debate de revisión.",
            [.. problems.Select(p => UserText.Of(DebateUserTexts.ProblemNoArrangement))]));
    }

    public async Task<IDebateRun> OpenAsync(DebateProposal proposal, CancellationToken ct)
    {
        if (proposal.Problems.Count > 0)
        {
            throw new InvalidOperationException("El debate no se puede abrir: " + string.Join(" ", proposal.Problems));
        }

        Opened.Add(proposal);
        var debate = $"deb-20261019-{++_debates:x6}";
        await world.RecordAsync(PreparedRun.System, DecisionKind.ApprovalRequested, "Aprobación pedida: debate.open",
            new ApprovalRequestedPayload(ChallengeLog.DebateOpenAction, proposal.ApprovalText, RiskLevel.Critical, null, []) { RunId = debate });
        if (!Approve)
        {
            await world.RecordAsync(PreparedRun.Person, DecisionKind.ApprovalDenied, "Denegado por usuario: debate.open",
                new ApprovalAnsweredPayload(ChallengeLog.DebateOpenAction, false, "usuario", "no", null) { RunId = debate });
            throw new InvalidOperationException("No se aprobó la apertura del debate: no se abre.");
        }

        await world.RecordAsync(PreparedRun.Person, DecisionKind.ApprovalGranted, "Aprobado por usuario: debate.open",
            new ApprovalAnsweredPayload(ChallengeLog.DebateOpenAction, true, "usuario", null, null) { RunId = debate });
        var request = proposal.Request;
        var arrangement = proposal.Judging;
        await world.RecordAsync(PreparedRun.System, DecisionKind.DebateOpened, $"Abre el debate {debate}",
            DebateEntries.Opened(
                debate,
                request.Subject.Reference,
                request.Mode,
                [.. arrangement.ProducerVendors],
                [.. request.Participants.Select((p, i) => template.Find(p.Agent) is { } a ? new DebateParticipantEntry(a.Id, p.Role, a.Model.Provider, a.Model.Vendor, a.Model.Model, $"P{i + 1}") : null).OfType<DebateParticipantEntry>()],
                [.. arrangement.WaivedParticipantVendors],
                arrangement.Kind,
                [.. arrangement.Judges.Select(j => new DebateJudgeEntry(j.Agent, j.Model.Provider, j.Model.Vendor, j.Model.Model))]));
        var run = new FakeDebateRun(new RunId(debate));
        Runs.Add(run);
        return run;
    }

    public Task<IDebateRun> ResumeAsync(ProjectContext project, RunId debate, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DebateSummary>> ListAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException();

    public Task<DebateTranscript> ReadAsync(ProjectContext project, RunId debate, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A debate going on, as the double opens it: it only remembers what it was asked.</summary>
internal sealed class FakeDebateRun(RunId id) : IDebateRun
{
    private readonly TaskCompletionSource<DebateResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunId Id => id;

    public IAsyncEnumerable<DebateProgress> Events => None();

    public Task<DebateResult> Completion => _completion.Task;

    public string? Aborted { get; private set; }

    public DebateSnapshot Snapshot() => throw new NotSupportedException();

    public Task RequestJudgmentAsync() => Task.CompletedTask;

    public Task RetryTurnAsync(AgentId participant) => Task.CompletedTask;

    public Task AddHumanEvidenceAsync(string title, string text) => Task.CompletedTask;

    public Task RunTestsAsync() => Task.CompletedTask;

    public Task DecideAsync(VerdictDecision? decision, string? chosenProposal, string comment) => Task.CompletedTask;

    public Task AbortAsync(string reason)
    {
        Aborted = reason;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<DebateProgress> None([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>The approval gate as the real one behaves: it records the question and a person's answer in the log, and answers with the binding.</summary>
internal sealed class RecordingGate(PreparedRun world) : IApprovalGate
{
    public List<ApprovalRequest> Requests { get; } = [];

    public bool Approve { get; set; } = true;

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        await world.RecordAsync(PreparedRun.System, DecisionKind.ApprovalRequested, $"Aprobación pedida: {request.Action}",
            new ApprovalRequestedPayload(request.Action, request.Description, request.Risk, null, request.AffectedPaths) { RunId = request.RunId! });
        await world.RecordAsync(PreparedRun.Person, Approve ? DecisionKind.ApprovalGranted : DecisionKind.ApprovalDenied, $"{(Approve ? "Aprobado" : "Denegado")} por usuario: {request.Action}",
            new ApprovalAnsweredPayload(request.Action, Approve, "usuario", null, null) { RunId = request.RunId! });
        return new ApprovalDecision(Approve, "usuario", DateTimeOffset.UnixEpoch, null, request.Binding);
    }
}
