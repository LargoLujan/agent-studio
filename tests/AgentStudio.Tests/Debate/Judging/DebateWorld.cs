using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Debate;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Judging;
using AgentStudio.Debate.Judging.Debates;
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
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate.Judging;

/// <summary>The immutable headers of debates in memory, with the contract of the SQLite registry: insert only, the same row twice is a no-op.</summary>
internal sealed class InMemoryDebateRegistry : IDebateRegistry
{
    private readonly Lock _gate = new();
    private readonly List<DebateRecord> _debates = [];

    public Task CreateAsync(DebateRecord debate, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_debates.FirstOrDefault(d => d.ProjectId == debate.ProjectId && d.Id == debate.Id) is { } existing)
            {
                return DebateHeaderHash.Of(existing) == DebateHeaderHash.Of(debate)
                    ? Task.CompletedTask
                    : throw new InvalidOperationException($"Debate '{debate.Id}' is already registered with another header.");
            }

            _debates.Add(debate);
            return Task.CompletedTask;
        }
    }

    public Task<DebateRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_debates.FirstOrDefault(d => d.ProjectId == projectId && d.Id == id));
        }
    }

    public Task<IReadOnlyList<DebateRecord>> ListAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DebateRecord>>([.. _debates.Where(d => d.ProjectId == projectId)]);
        }
    }

    /// <summary>Rewrites a stored header behind the store's back, as someone who drops the update trigger of the table would.</summary>
    public void Replace(RunId id, Func<DebateRecord, DebateRecord> change)
    {
        lock (_gate)
        {
            var index = _debates.FindIndex(d => d.Id == id);
            _debates[index] = change(_debates[index]);
        }
    }
}

/// <summary>A participant of a debate the world writes: its definition, role and label.</summary>
internal sealed record Seat(AgentDefinition Agent, DebateRole Role, string Label);

/// <summary>What can vary between the debates the judging tests prepare.</summary>
internal sealed record DebateSpec
{
    public DebateMode Mode { get; init; } = DebateMode.Free;

    public DebateSubject Subject { get; init; } = new(DebateSubjectKind.None, null);

    public string? SnapshotCommit { get; init; }

    public IReadOnlyList<Seat> Participants { get; init; } = [];

    public IReadOnlyList<AgentDefinition> Judges { get; init; } = [];

    public DebateJudgingKind Judging { get; init; } = DebateJudgingKind.IndependentJudge;

    public IReadOnlyList<VendorId> ProducerVendors { get; init; } = [];

    public IReadOnlyList<VendorId> WaivedParticipantVendors { get; init; } = [];

    public string Question { get; init; } = "¿Qué diseño de caché adoptamos para la librería de cálculo?";

    public FrozenRequirementSet? Requirements { get; init; }

    public string? Challenges { get; init; }

    public DebateLimits Limits { get; init; } = DebateLimits.Default;
}

/// <summary>
/// A debate in the in-memory stores, written the way the coordinator writes it (docs/15 D-02, D-03 and D-17): the question as
/// evidence, the header row and <c>DebateOpened</c> with both hashes; every turn of a round recorded around its launch
/// (the prompt announced only by <c>AgentRunStarted</c>, the end without a report, so nothing of a participant is stored
/// during the round); and, when the round closes, each contribution captured, registered and published in the order of
/// the header, then <c>DebateRoundClosed</c>. It shares the stores of a <see cref="JudgingWorld"/>, so the finished run of
/// that world can be the subject of a debate.
/// </summary>
/// <remarks>
/// The participants carry distinctive identities on purpose (<see cref="IdentityTokens"/>): the titles and the authors the
/// world writes name them, as a careless writer would, and a blind dossier must not show any of them.
/// </remarks>
internal sealed class DebateWorld : IDisposable
{
    public static readonly RunId Debate = new("deb-20261019-a1b2c3");
    public static readonly RunId Revision = new("deb-20261019-d4e5f6");

    /// <summary>A participant on Claude Code (Anthropic) with a distinctive model.</summary>
    public static readonly AgentDefinition Architect = Agent("arquitecto-azul", "Arquitecta Azul", AgentRole.Architect, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5-sello"));

    /// <summary>A second participant on Claude Code, with another model: a debate of one vendor.</summary>
    public static readonly AgentDefinition Designer = Agent("disenador-rojo", "Diseñador Rojo", AgentRole.Architect, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-sonnet-4-5-sello"));

    /// <summary>A participant on Codex (OpenAI): a debate that mixes vendors.</summary>
    public static readonly AgentDefinition Critic = Agent("critico-verde", "Crítico Verde", AgentRole.Critic, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5-codex-sello"));

    /// <summary>A second participant on Codex, with another model: a debate of the other vendor.</summary>
    public static readonly AgentDefinition Reviewer = Agent("revisor-gris", "Revisor Gris", AgentRole.Critic, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5-mini-sello"));

    public static readonly AgentDefinition CodexJudge = Agent("juez-revision", "Juez de revisión", AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5"));

    public static readonly AgentDefinition ClaudeJudge = Agent("juez-claude", "Juez Claude", AgentRole.Judge, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"));


    private DebateWorld(JudgingWorld world)
    {
        World = world;
        Builder = new DebateDossierBuilder(world.Recorder, Debates, world.Requirements, world.Evidence, world.Verdicts, RunBuilder(world), new JudgingOptions(world.DossierRoot));
        Frozen = FrozenRequirementSet.Freeze(
        [
            Req("RQ-10", RequirementPriority.Mandatory, "La caché debe poder probarse sin red", Crit("c1", VerificationMethod.JudgeAssessment, "El juez valora que se pueda probar")),
            Req("RQ-11", RequirementPriority.Desirable, "La caché debe ser sencilla", Crit("c2", VerificationMethod.JudgeAssessment, "El juez valora la sencillez")),
        ],
            T0);
    }

    public JudgingWorld World { get; }

    public InMemoryDebateRegistry Debates { get; } = new();

    public DebateDossierBuilder Builder { get; }

    /// <summary>The requirements of a debate that chooses: the judge's own assessment.</summary>
    public FrozenRequirementSet Frozen { get; }

    public string Project => JudgingWorld.Project;

    /// <summary>A debate between Claude and Codex, judged by a cross-vendor panel with both vendors waived.</summary>
    public static DebateSpec MixedPanel() => new()
    {
        Participants = [new Seat(Architect, DebateRole.Proponent, "P1"), new Seat(Critic, DebateRole.Critic, "P2")],
        Judges = [ClaudeJudge, CodexJudge],
        Judging = DebateJudgingKind.CrossVendorPanel,
        WaivedParticipantVendors = [VendorId.Anthropic, VendorId.OpenAI],
    };

    /// <summary>A debate of two Claude participants with different models, judged by Codex: the disposition by default.</summary>
    public static DebateSpec OneVendor() => new()
    {
        Participants = [new Seat(Architect, DebateRole.Proponent, "P1"), new Seat(Designer, DebateRole.Critic, "P2")],
        Judges = [CodexJudge],
    };

    /// <summary>Every identity of the participants of the world, which no blind dossier may show.</summary>
    public static IReadOnlyList<string> IdentityTokens { get; } =
    [
        .. new[] { Architect, Designer, Critic, Reviewer }.SelectMany(a => new[] { a.Id.Value, a.Name, a.Model.Provider.Value, a.Model.Vendor.Value, a.Model.Model! }).Distinct(),
    ];

    public static async Task<DebateWorld> CreateAsync(JudgingWorldOptions? options = null)
    {
        var world = await JudgingWorld.CreateAsync(options);
        var debates = new DebateWorld(world);
        await world.Requirements.SaveAsync(world.ProjectContext.ProjectId, debates.Frozen, TestContext.Current.CancellationToken);
        return debates;
    }

    public static AgentDefinition Agent(string id, string name, AgentRole role, ModelBinding model) =>
        new(new AgentId(id), name, role, model, string.Empty, [], role == AgentRole.Judge ? ["Read", "Grep", "Glob"] : ["Read", "Grep", "Glob"],
            [], new ExecutionBudget(12, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));

    public static JudgeDossierBuilder RunBuilder(JudgingWorld world) =>
        new(world.Recorder, world.Runs, world.Plans, world.Requirements, world.Evidence, new JudgingOptions(world.DossierRoot));

    /// <summary>Opens a debate: the question as evidence, the header row and <c>DebateOpened</c>, as the coordinator does after the approval.</summary>
    public async Task<DebateRecord> OpenAsync(DebateSpec spec, RunId? id = null)
    {
        var debate = id ?? Debate;
        var requirements = spec.Requirements ?? Frozen;
        var question = await CaptureAsync(debate, EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, "Pregunta del debate", spec.Question, "usuario", null);
        await RegisterAsync(debate, question, null);
        var header = new DebateRecord(
            debate,
            Project,
            World.Clock.GetUtcNow(),
            spec.Mode,
            requirements.ContentHash,
            question.Id,
            spec.Subject,
            spec.SnapshotCommit,
            [.. spec.Participants.Select(p => new DebateParticipantDefinition(p.Agent, p.Role, p.Label))],
            spec.Judges,
            spec.Judging,
            spec.ProducerVendors,
            spec.WaivedParticipantVendors,
            spec.Limits,
            spec.Participants.Count * spec.Limits.MaxRounds + 2,
            spec.Challenges);
        await Debates.CreateAsync(header, TestContext.Current.CancellationToken);
        await World.RecordAsync(JudgingWorld.System, DecisionKind.DebateOpened, $"Debate {debate} abierto",
            new DebateOpenedPayload(
                spec.Mode,
                DebateHeaderHash.Of(header),
                DebateHeaderHash.Proposal(header, question.ContentSha256),
                requirements.ContentHash,
                question.Id,
                question.ContentSha256,
                spec.Subject.Kind,
                spec.Subject.Reference,
                spec.SnapshotCommit,
                [.. spec.Participants.Select(p => new DebateParticipantEntry(p.Agent.Id, p.Role, p.Agent.Model.Provider, p.Agent.Model.Vendor, p.Agent.Model.Model, p.Label))],
                [.. spec.Judges.Select(j => new DebateJudgeEntry(j.Id, j.Model.Provider, j.Model.Vendor, j.Model.Model))],
                spec.Judging,
                spec.ProducerVendors,
                spec.WaivedParticipantVendors,
                spec.Limits.MaxRounds,
                (long)spec.Limits.MaxDuration.TotalSeconds,
                spec.Limits.MaxTotalTokens,
                header.MaxAgentRuns,
                spec.Challenges) { RunId = debate.Value });
        return header;
    }

    /// <summary>A contribution the world publishes: who, what kind, the contribution it replies to (by index), the quotes it resolved and those it could not.</summary>
    internal sealed record Turn(Seat Seat, ContributionKind Kind, string Summary, int? RepliesTo = null, IReadOnlyList<EvidenceId>? Cites = null, IReadOnlyList<string>? Unresolved = null, bool Finished = false, bool WithMemory = false, string? Argument = null);

    /// <summary>
    /// One sealed round: every turn launched and finished (nothing of a participant stored), then every contribution
    /// captured, registered and published in order, and the round closed. Returns the published payloads.
    /// </summary>
    public async Task<IReadOnlyList<DebateContributionRecordedPayload>> RoundAsync(DebateRecord header, int round, IReadOnlyList<Turn> turns, DebatePhase next, DebateJudgmentReason? judgment = null, DebateLimitReached limit = DebateLimitReached.None)
    {
        var debate = header.Id;
        var answered = new List<DateTimeOffset>();
        foreach (var turn in turns)
        {
            var task = new TaskId(string.Create(CultureInfo.InvariantCulture, $"r{round}-{turn.Seat.Agent.Id}"));
            var prompt = await CaptureAsync(debate, EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, $"Prompt del turno {task} de {turn.Seat.Agent.Name}",
                $"Eres {turn.Seat.Label}. Responde a la pregunta.", JudgeRunRecorder.SystemId, task.Value);
            var pack = turn.WithMemory ? new ContextPackReference(HashA, ["mem-1"], [], 120) : null;
            if (turn.WithMemory)
            {
                await RegisterAsync(debate, prompt, task); // a prompt with memory registered as evidence too: it must stay out
            }

            await World.RecordAsync(JudgingWorld.System, DecisionKind.AgentRunStarted, $"Turno {task}",
                new AgentRunStartedPayload(task, 1, turn.Seat.Agent.Id, turn.Seat.Agent.Model.Provider, turn.Seat.Agent.Model.Vendor, turn.Seat.Agent.Model.Model,
                    World.Workspace, prompt.Id, prompt.ContentSha256, [], AgentRunPurpose.DebateParticipant, pack) { RunId = debate.Value, V = AgentRunStartedPayload.PurposeVersion });
            await World.RecordAsync(JudgingWorld.System, DecisionKind.AgentRunFinished, $"Turno {task} terminado",
                new AgentRunFinishedPayload(task, 1, AttemptOutcome.Succeeded, 0, null, null, null, null) { RunId = debate.Value, V = AgentRunFinishedPayload.UsageVersion });
            answered.Add(World.Clock.GetUtcNow());
        }

        var published = new List<DebateContributionRecordedPayload>();
        var index = (await World.LoadLogAsync()).Entries.Count(e => e.Kind == DecisionKind.DebateContributionRecorded && NamesDebate(e, debate));
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            var task = string.Create(CultureInfo.InvariantCulture, $"r{round}-{turn.Seat.Agent.Id}");
            var content = Contribution(turn);
            var evidence = await CaptureAsync(debate, EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, $"Contribución de {turn.Seat.Agent.Id} ({turn.Seat.Agent.Model.Model})",
                content, turn.Seat.Agent.Id.Value, task);
            await RegisterAsync(debate, evidence, new TaskId(task));
            var payload = new DebateContributionRecordedPayload(
                index + i, round, turn.Seat.Agent.Id, turn.Kind, turn.RepliesTo, evidence.Id, evidence.ContentSha256, turn.Cites ?? [], turn.Unresolved ?? [], 0, turn.Finished, task, 1, answered[i])
            {
                RunId = debate.Value,
            };
            await World.RecordAsync(JudgingWorld.System, DecisionKind.DebateContributionRecorded, $"Contribución de {turn.Seat.Agent.Name} publicada", payload);
            published.Add(payload);
        }

        await World.RecordAsync(JudgingWorld.System, DecisionKind.DebateRoundClosed, $"Ronda {round} cerrada",
            new DebateRoundClosedPayload(round, next, judgment, limit,
                new MetricSnapshot(1000 * round, MetricProvenance.Exact, $"{turns[0].Seat.Agent.Model.Provider}: result.usage", MetricOrigin.ReportedByProvider), false, 60 * round,
                $"Ronda {round} cerrada por {turns[0].Seat.Agent.Name}") { RunId = debate.Value });
        return published;
    }

    /// <summary>The usual debate that chooses: P1 and P2 propose, P2 objects to P1, and the debate goes to judgment.</summary>
    public async Task<DebateRecord> ChoiceDebateAsync(DebateSpec spec, RunId? id = null)
    {
        var header = await OpenAsync(spec, id);
        var (p1, p2) = (spec.Participants[0], spec.Participants[1]);
        var quote = await QuoteAsync(header.Id, "src/Cache.cs", 1, 3, "public sealed class Cache { }");
        await RoundAsync(header, 1, [new Turn(p1, ContributionKind.Proposal, "Una caché en memoria con un puerto", Cites: [quote.Id]), new Turn(p2, ContributionKind.Proposal, "Una caché en disco")], DebatePhase.Deliberation);
        await RoundAsync(header, 2, [new Turn(p2, ContributionKind.Objection, "La de P1 no persiste", RepliesTo: 0, Unresolved: ["src/Disk.cs:200-210"])], DebatePhase.AwaitingJudgment, DebateJudgmentReason.Requested);
        return header;
    }

    /// <summary>A verdict of the review of the run of the world, stored and announced as the review keeps it.</summary>
    public async Task<StoredVerdict> RunVerdictAsync()
    {
        var dossier = await RunBuilder(World).BuildAsync(new DossierRequest(Project, AuditScope.CurrentPlan, JudgingWorld.Run.Value), TestContext.Current.CancellationToken);
        var verdict = ValidVerdict(dossier) with { Judge = new JudgeIdentity(JudgingWorld.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5")) };
        var keeper = new VerdictKeeper(World.Verdicts, World.Recorder, Project, JudgingWorld.Run.Value, JudgingWorld.Review, AuditScope.CurrentPlan);
        var id = await keeper.KeepAsync(JudgingWorld.Judge, dossier, verdict, [], null, "Juez en 'openai'");
        return World.Verdicts.Stored.Single(v => v.Id == id);
    }

    /// <summary>A revision debate of <paramref name="challenged"/>, opened as the challenge service opens it: the reasons, <c>VerdictChallenged</c>, then the adversarial debate.</summary>
    public async Task<DebateRecord> RevisionAsync(StoredVerdict challenged, string requirementSetHash, DebateSpec? spec = null)
    {
        var reasons = await CaptureAsync(Revision, EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman, "Razones de la impugnación", "El test no cubre el desbordamiento.", "usuario", null);
        await RegisterAsync(Revision, reasons, null);
        await World.RecordAsync(new Actor(ActorKind.User, "manuel"), DecisionKind.VerdictChallenged, "Veredicto impugnado",
            new VerdictChallengedPayload(challenged.Id, "manuel", reasons.Id, reasons.ContentSha256, Revision.Value) { RunId = challenged.RunId! });
        var revision = (spec ?? OneVendor()) with
        {
            Mode = DebateMode.Adversarial,
            Subject = new DebateSubject(DebateSubjectKind.Verdict, challenged.Id),
            Challenges = challenged.Id,
            Requirements = await World.Requirements.FindAsync(Project, requirementSetHash, TestContext.Current.CancellationToken),
        };
        var header = await OpenAsync(revision, Revision);
        await RoundAsync(header, 1, [new Turn(revision.Participants[0], ContributionKind.Proposal, "El veredicto se sostiene"), new Turn(revision.Participants[1], ContributionKind.Proposal, "La impugnación tiene razón")],
            DebatePhase.AwaitingJudgment, DebateJudgmentReason.Requested);
        return header;
    }

    /// <summary>A quote of code as the citation resolver captures it: the lines, from the commit, registered in the debate.</summary>
    public async Task<EvidenceRecord> QuoteAsync(RunId debate, string path, int from, int to, string lines)
    {
        var quote = await CaptureAsync(debate, EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, $"Cita {path}:{from}-{to} pedida por {Architect.Id}", lines, JudgeRunRecorder.SystemId, null, CommitA);
        await RegisterAsync(debate, quote, null);
        return quote;
    }

    public async Task<EvidenceRecord> CaptureAsync(RunId debate, EvidenceKind kind, EvidenceOrigin origin, string title, string content, string producedBy, string? task, string? commit = null)
    {
        World.Clock.Advance(TimeSpan.FromSeconds(1));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await World.Evidence.CaptureAsync(
            Project,
            new EvidenceDraft(kind, origin, title, producedBy, RunId: debate.Value, TaskId: task, SubjectCommit: commit),
            stream,
            TestContext.Current.CancellationToken);
    }

    public Task<DecisionEntry> RegisterAsync(RunId debate, EvidenceRecord evidence, TaskId? task) =>
        World.RecordAsync(JudgingWorld.System, DecisionKind.EvidenceRegistered, "Evidencia registrada", EvidenceRegisteredPayload.Anchoring(evidence, debate.Value, task, 1));

    public Task<DebateDossierPackage> BuildAsync(RunId? debate = null) =>
        Builder.BuildPackageAsync(World.ProjectContext, debate ?? Debate, TestContext.Current.CancellationToken);

    /// <summary>Everything a judge reads of a package: the dossier files, the question and the prompts of both seats.</summary>
    public static string Visible(DebateDossierPackage package)
    {
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(package.Files.Folder, "*.*", SearchOption.TopDirectoryOnly))
        {
            text.AppendLine(File.ReadAllText(file));
        }

        text.AppendLine(DebateJudgePrompt.Evaluation(package, 0, package.Files.Folder));
        text.AppendLine(DebateJudgePrompt.Evaluation(package, 1, package.Files.Folder));
        return text.ToString();
    }

    /// <summary>The verdict of a judge that read the dossier: every requirement satisfied by <paramref name="cited"/>, the proposal chosen, the comparison of every proposal.</summary>
    public static Verdict Choosing(JudgeDossier dossier, JudgeIdentity judge, string? chosen, IReadOnlyList<EvidenceId> cited, VerdictDecision decision = VerdictDecision.Approved, ConfidenceLevel confidence = ConfidenceLevel.High) =>
        new(
            decision,
            "La propuesta elegida cumple los requisitos.",
            [.. dossier.Requirements.Requirements.Select(r => new RequirementAssessment(r.Id, AssessmentStatus.Satisfied, cited, "Las contribuciones lo sostienen."))],
            cited,
            [],
            [],
            [],
            new Confidence(confidence, "Las contribuciones y la cita coinciden."),
            decision is VerdictDecision.ChangesRequested or VerdictDecision.Rejected ? ["Rehacer la propuesta"] : [],
            decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved,
            judge,
            dossier.Requirements.ContentHash,
            dossier.DecisionLogHeadHash,
            T0)
        {
            Resolution = new DebateResolution(chosen, [.. (dossier.Debate?.Proposals ?? []).Select(p => new ProposalComparison(p, "Fuerte.", "Débil."))]),
        };

    public void Dispose() => World.Dispose();

    private static bool NamesDebate(DecisionEntry entry, RunId debate) => entry.PayloadJson.Contains($"\"runId\":\"{debate}\"", StringComparison.Ordinal);

    private static string Contribution(Turn turn) => new JsonObject
    {
        ["kind"] = turn.Kind.ToString(),
        ["repliesTo"] = turn.RepliesTo is { } r ? "C" + (r + 1).ToString(CultureInfo.InvariantCulture) : "ninguna",
        ["summary"] = turn.Summary,
        ["argument"] = turn.Argument ?? $"{turn.Summary}, porque así se cumple RQ-10.",
        ["claims"] = new JsonArray(new JsonObject { ["claim"] = turn.Summary, ["support"] = new JsonArray() }),
        ["citations"] = new JsonArray(),
        ["concedes"] = new JsonArray(),
        ["finished"] = turn.Finished,
    }.ToJsonString();
}
