using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Judging;
using AgentStudio.Debate.Judging.Debates;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using DebateAnswers = AgentStudio.Tests.Debate.Judging.DebateAnswers;
using DebateReplies = AgentStudio.Tests.Debate.Fakes.ScriptedDebateProvider;
using JudgeData = AgentStudio.Tests.Debate.Fakes.PromptData;

namespace AgentStudio.Tests.Integration.Phase5;

/// <summary>
/// The composition root of Phase 5 without the desktop (docs/15 section 9, «Integración»): the one of Phase 4
/// (<see cref="Phase4Composition"/>: every module of Phases 3 and 4 through its own extension, SQLite, the project memory,
/// the observability) plus, one line each, the arena of debates (<c>AddAgentStudioDebateArena</c>) and the audits with the
/// challenges (<c>AddAgentStudioAudits</c>); the judgment of the debates already comes with <c>AddAgentStudioJudging</c>. The
/// one thing the composition root has to give the audits is the trusted profile of a project, which the desktop reads with
/// its <see cref="FileVerificationProfileSource"/> (<c>.agentstudio/verification.json</c>): it is adapted here as the app will
/// adapt it. No CLI is asked anything, not even its version.
/// </summary>
internal static class Phase5Composition
{
    public static ServiceCollection Services(Phase4Paths paths, IApprovalPrompt prompt, Func<IServiceProvider, IProjectAgentProviders>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var services = Phase4Composition.Services(paths, prompt, providers).WithoutCliProbes();
        services
            .AddAgentStudioDebateArena()
            .AddAgentStudioAudits();
        services.AddSingleton<IVerificationProfileSource, FileVerificationProfileSource>();
        services.AddSingleton<IAuditVerificationProfiles, DesktopProfilesForAudits>();
        return services;
    }

    /// <summary>The project's trusted profile as the desktop reads it, for «Auditar proyecto completo» (ADR-0030: the composition adapts it).</summary>
    private sealed class DesktopProfilesForAudits(IVerificationProfileSource source) : IAuditVerificationProfiles
    {
        public Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken) => source.LoadAsync(project, cancellationToken);
    }
}

/// <summary>
/// The template of agents of the Phase 5 tests: the one of the engine tests (Claude orchestrates and executes, Codex's
/// <c>juez</c> reviews), plus participants of both vendors with distinctive ids and models (no blind dossier may show them)
/// and three judges: one of Claude and two of Codex with different models, so a challenge can change the judge.
/// </summary>
internal static class Phase5Roster
{
    /// <summary>The judge of the engine's reviews (Codex, its default model), from the fixture.</summary>
    public static readonly AgentId RunJudge = new("juez");

    /// <summary>A participant of Claude (Anthropic).</summary>
    public static readonly AgentId Architect = new("arquitecta-azul");

    /// <summary>A second participant of Claude, with another model.</summary>
    public static readonly AgentId GreyReviewer = new("revisor-gris");

    /// <summary>A participant of Codex (OpenAI).</summary>
    public static readonly AgentId GreenCritic = new("critico-verde");

    public static readonly AgentId ClaudeJudge = new("juez-claude");

    public static readonly AgentId CodexJudge = new("juez-codex");

    public static readonly AgentId RevisionJudge = new("juez-revision");

    /// <summary>The marker of each participant in its contributions: never its agent, CLI, vendor or model.</summary>
    public static readonly IReadOnlyDictionary<AgentId, string> Codes = new Dictionary<AgentId, string>
    {
        [Architect] = "ALFA",
        [GreyReviewer] = "CHARLIE",
        [GreenCritic] = "BRAVO",
    };

    /// <summary>The models of the participants: a blind dossier names none of them.</summary>
    public static readonly IReadOnlyList<string> ParticipantModels = ["haiku-azul-sello", "sonnet-gris-sello", "gpt-verde-sello"];

    public static IReadOnlyList<AgentDefinition> Agents() =>
    [
        .. Roster(),
        Define(Architect, "Arquitecta azul", AgentRole.Architect, ProviderId.ClaudeCode, VendorId.Anthropic, ParticipantModels[0]),
        Define(GreyReviewer, "Revisor gris", AgentRole.Critic, ProviderId.ClaudeCode, VendorId.Anthropic, ParticipantModels[1]),
        Define(GreenCritic, "Crítico verde", AgentRole.Critic, ProviderId.CodexCli, VendorId.OpenAI, ParticipantModels[2]),
        Define(ClaudeJudge, "Juez de Claude", AgentRole.Judge, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"),
        Define(CodexJudge, "Juez de Codex", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, "gpt-5-codex"),
        Define(RevisionJudge, "Juez de revisión", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, "gpt-5"),
    ];

    private static AgentDefinition Define(AgentId id, string name, AgentRole role, ProviderId provider, VendorId vendor, string model) =>
        new(
            id,
            name,
            role,
            new ModelBinding(provider, vendor, model),
            "Instrucciones de sistema de " + name,
            [],
            ["Read", "Grep", "Glob"],
            [],
            new ExecutionBudget(12, TimeSpan.FromMinutes(5)),
            1,
            MemoryPolicy.None,
            new ReviewPolicy(false, false));
}

/// <summary>
/// The two CLIs, scripted (Phase 4's <see cref="ScriptedCli"/>: Claude reports its context, Codex does not), answering
/// whatever Agent Studio launches on them the way the real one would answer it, from what the request carries:
/// <list type="bullet">
/// <item>an executor of the engine: what <see cref="Executor"/> says (by default it writes <c>src/feature.txt</c>);</item>
/// <item>a participant of a debate (<see cref="AgentRunRequest.DebateId"/> <c>deb-…</c>, not a judge): a valid contribution of
/// the kind its schema offers first, whose summary and argument carry <see cref="Marker"/> and nothing of its identity;</item>
/// <item>a judge (of a run, a debate or an audit): a verdict built from the dossier in its prompt, with the decision of
/// <see cref="Decision"/>: in a debate that chooses, the proposal of <see cref="Chosen"/> on the contributions; anywhere
/// else, the green test run the system captured of the commit under review.</item>
/// </list>
/// Every launch reports its usage exactly and a quota window of its own (named by its unit and task), so a test can see
/// that it went through the quota observation; <see cref="Hold"/> keeps a launch open until a test releases it.
/// </summary>
internal sealed class Phase5Clis
{
    /// <summary>The usage each participant's turn reports.</summary>
    public const long TurnInput = 1_000;

    public const long TurnOutput = 100;

    /// <summary>The usage each judge reports.</summary>
    public const long JudgeInput = 2_000;

    public const long JudgeOutput = 200;

    public Phase5Clis()
    {
        Claude = ScriptedCli.Claude(TimeProvider.System);
        Codex = ScriptedCli.Codex(TimeProvider.System);
        Claude.Script.Fallback = Answer;
        Codex.Script.Fallback = Answer;
    }

    public ScriptedCli Claude { get; }

    public ScriptedCli Codex { get; }

    /// <summary>The providers of a project: one per CLI through the real pipeline (journal, quota record, concurrency).</summary>
    public Func<IServiceProvider, IProjectAgentProviders> Providers => Phase4ScriptedProviders.Over(Claude, Codex);

    /// <summary>What an executor of the engine does, by its request and attempt.</summary>
    public Func<AgentRunRequest, int, AgentScript> Executor { get; set; } = (_, _) => AgentScript.Completed(("src/feature.txt", "función nueva\n"));

    /// <summary>The decision of a judge, by its request.</summary>
    public Func<AgentRunRequest, VerdictDecision> Decision { get; set; } = _ => VerdictDecision.Approved;

    /// <summary>The proposal a judge of a debate that chooses picks; null is «ninguna».</summary>
    public Func<AgentRunRequest, string?> Chosen { get; set; } = _ => "P1";

    /// <summary>Changes the contribution of a participant before it answers.</summary>
    public Action<AgentRunRequest, JsonObject>? Contribute { get; set; }

    /// <summary>A gate the launch waits on before it ends; null ends it at once.</summary>
    public Func<AgentRunRequest, TaskCompletionSource?> Hold { get; set; } = _ => null;

    /// <summary>Every launch that reached a CLI, both CLIs together.</summary>
    public IReadOnlyList<AgentRunRequest> Launches => [.. Claude.Script.Starts.Concat(Codex.Script.Starts).Select(s => s.Request)];

    /// <summary>The launches of one unit of the log (a debate <c>deb-…</c> or an audit <c>aud-…</c>).</summary>
    public IReadOnlyList<AgentRunRequest> LaunchesOf(string unit) => [.. Launches.Where(r => string.Equals(r.DebateId, unit, StringComparison.Ordinal))];

    /// <summary>The text a participant writes in a round: its code and the round, never who it is.</summary>
    public static string Marker(AgentId participant, int round) =>
        $"marca-{Phase5Roster.Codes.GetValueOrDefault(participant, "OTRO")}-r{round}";

    /// <summary>The quota window a launch announces: its unit and its task, so each launch keeps one of its own.</summary>
    public static string QuotaWindowOf(string? unit, string? task) => $"{unit ?? "ejecucion"}/{task ?? "plan"}";

    private static bool IsJudge(AgentRunRequest request) => request.Agent.Role is AgentRole.Judge or AgentRole.Auditor;

    private AgentScript Answer(AgentRunRequest request, int attempt)
    {
        var at = DateTimeOffset.UtcNow;
        AgentScript script;
        if (IsJudge(request))
        {
            script = Judge(request);
        }
        else if (request.DebateId is { } debate && DebateIds.IsDebate(debate))
        {
            script = Participant(request);
        }
        else
        {
            var work = Executor(request, attempt);
            return Hold(request) is { } held ? work with { Gate = held } : work;
        }

        var (input, output) = IsJudge(request) ? (JudgeInput, JudgeOutput) : (TurnInput, TurnOutput);
        return script with
        {
            Events =
            [
                new QuotaReported(at, [new QuotaWindow(QuotaWindowOf(request.DebateId, request.TaskId), MetricValue<double>.Exact(0.25, "scripted rate_limit_event"), null)]),
                new UsageReported(at, DebateReplies.Usage(input, output)),
            ],
            Gate = Hold(request),
        };
    }

    private AgentScript Participant(AgentRunRequest request)
    {
        var answer = DebateReplies.Answer(request);
        var round = DebateIds.TryReadTurn(request.TaskId, out var r, out _) ? r : 0;
        var marker = Marker(request.Agent.Id, round);
        answer["summary"] = $"{marker}: la contribución en una línea.";
        answer["argument"] = $"{marker}: el argumento de la contribución, que el juez valora.";
        Contribute?.Invoke(request, answer);
        var json = answer.ToJsonString();
        return new AgentScript { StructuredOutput = json, FinalText = json };
    }

    private AgentScript Judge(AgentRunRequest request)
    {
        if (!request.Prompt.Contains(JudgePrompt.DataOpen, StringComparison.Ordinal))
        {
            // A correction resumes the session with only the rules the first verdict broke: these tests never expect one.
            return AgentScript.Failing("Phase 5 integration: the judge was asked for a correction, so its first verdict was not valid.");
        }

        var answer = Phase5Answers.Verdict(request.Prompt, Decision(request), Chosen(request));
        var json = answer.ToJsonString();
        return new AgentScript { StructuredOutput = json, FinalText = json };
    }
}

/// <summary>The verdicts a judge that read its dossier could give, built from the dossier in its prompt.</summary>
internal static class Phase5Answers
{
    /// <summary>A verdict of <paramref name="decision"/> for the dossier of <paramref name="prompt"/>, whatever its kind.</summary>
    public static JsonObject Verdict(string prompt, VerdictDecision decision, string? chosen)
    {
        var data = JudgeData.Dossier(prompt);
        var choice = string.Equals(Text(data, "format"), DebateDossierDocument.Format, StringComparison.Ordinal)
            && string.Equals(Text(data, "family"), "choice", StringComparison.Ordinal);
        if (choice)
        {
            // A debate that chooses: the judge's own assessment, on the contributions it read (docs/15 D-25).
            return DebateAnswers.Choosing(prompt, chosen, decision);
        }

        // The review of a run, a debate that reviews work or its challenge, and a whole-project audit: Phase 3 rules, so a
        // mandatory requirement is satisfied by the green test run the system captured of the commit under review.
        var commit = Text(data, "candidateCommit") ?? Text(data, "headCommit");
        var runs = SystemTestRuns(data).Where(t => commit is null || string.Equals(t.Commit, commit, StringComparison.OrdinalIgnoreCase)).ToList();
        if (runs.Count == 0 && decision == VerdictDecision.Approved)
        {
            throw new InvalidOperationException($"The dossier ({Text(data, "format")}) lists no green test run the system captured of {commit ?? "any commit"}: no judge could approve it.");
        }

        var answer = runs.Count > 0 ? JudgeData.EchoingApproval(prompt, new EvidenceId(runs[^1].Id)) : Unverified(data);
        answer["decision"] = decision.ToString();
        answer["summary"] = decision == VerdictDecision.Approved ? "Cumple los requisitos con las pruebas del sistema." : $"El juez decide {decision}.";
        answer["requiresHumanIntervention"] = decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved;
        answer["correctiveActions"] = decision is VerdictDecision.ChangesRequested or VerdictDecision.Rejected
            ? new JsonArray("Repetir la tarea con lo que pide la revisión.")
            : new JsonArray();
        if (data.TryGetProperty("proposals", out var proposals) && proposals.ValueKind == JsonValueKind.Array && proposals.GetArrayLength() > 0)
        {
            answer["resolution"] = new JsonObject
            {
                ["comparison"] = new JsonArray([.. proposals.EnumerateArray().Select(p => (JsonNode)new JsonObject { ["proposal"] = p.GetString(), ["strengths"] = "Fuerte.", ["weaknesses"] = "Débil." })]),
                ["chosenProposal"] = chosen ?? VerdictJson.NoProposal,
            };
        }

        return answer;
    }

    /// <summary>A verdict that finds no objective evidence: every requirement not verifiable, nothing examined.</summary>
    private static JsonObject Unverified(JsonElement data) => new()
    {
        ["assessments"] = new JsonArray([.. data.GetProperty("requirements").EnumerateArray().Select(r => (JsonNode)new JsonObject
        {
            ["requirement"] = r.GetProperty("id").GetString(),
            ["status"] = "NotVerifiable",
            ["evidence"] = new JsonArray(),
            ["rationale"] = "No hay una ejecución de pruebas del sistema en verde.",
        })]),
        ["evidenceExamined"] = new JsonArray(),
        ["findings"] = new JsonArray(),
        ["pendingObjections"] = new JsonArray(),
        ["risks"] = new JsonArray(),
        ["correctiveActions"] = new JsonArray(),
        ["confidence"] = new JsonObject { ["level"] = "Low", ["justification"] = "Sin evidencia objetiva." },
        ["requiresHumanIntervention"] = false,
        ["decision"] = "Unresolved",
        ["summary"] = "Sin pruebas del sistema.",
        ["requirementSetHash"] = data.GetProperty("requirementSetHash").GetString(),
        ["decisionLogHeadHash"] = data.GetProperty("decisionLogHeadHash").GetString(),
    };

    /// <summary>
    /// Every green test run the system captured that the dossier lists, wherever it lists it (the evidence of a run, of the
    /// run a debate is about or of the run a challenge revises, and the verification of a whole-project audit), in order.
    /// </summary>
    public static IReadOnlyList<(string Id, string? Commit)> SystemTestRuns(JsonElement dossier)
    {
        var found = new List<(string Id, string? Commit)>();
        Walk(dossier, found);
        return [.. found.DistinctBy(f => f.Id)];
    }

    private static void Walk(JsonElement element, List<(string Id, string? Commit)> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (string.Equals(Text(element, "kind"), "TestRun", StringComparison.Ordinal)
                    && string.Equals(Text(element, "origin"), "CapturedBySystem", StringComparison.Ordinal)
                    && element.TryGetProperty("testRun", out var tests) && tests.ValueKind == JsonValueKind.Object
                    && tests.TryGetProperty("succeeded", out var succeeded) && succeeded.ValueKind == JsonValueKind.True
                    && (Text(element, "id") ?? Text(element, "evidence")) is { } id)
                {
                    found.Add((id, Text(element, "subjectCommit")));
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, found);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, found);
                }

                break;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Real runs of the engine for the Phase 5 tests (the F3-8 shape): T1 writes <c>src/feature.txt</c> and its trusted step
/// checks it; I1 integrates it and checks the result; R1 is the independent review by Codex's <c>juez</c>. The step is a
/// PowerShell script the user committed, run isolated at Low integrity by the real verifier, which reports in JUnit.
/// </summary>
internal static class Phase5Runs
{
    /// <summary>Passes when <c>src/feature.txt</c> is there, and writes its JUnit report where the step says.</summary>
    public const string CheckScript = """
        param([string] $Report)
        $ok = Test-Path -LiteralPath 'src/feature.txt'
        $case = if ($ok) { '<testcase name="feature" classname="check" />' } else { '<testcase name="feature" classname="check"><failure message="falta src/feature.txt" /></testcase>' }
        Set-Content -LiteralPath $Report -Value "<testsuites><testsuite name='check'>$case</testsuite></testsuites>" -Encoding utf8
        if ($ok) { exit 0 } else { exit 1 }

        """;

    /// <summary>
    /// Generous: the machine is shared, and the steps start real processes. A test with one run takes 20 to 30 seconds here;
    /// under the load of other suites one took 73 seconds, every step of it slower, and another one 3.5 minutes.
    /// </summary>
    public static readonly TimeSpan Guard = TimeSpan.FromMinutes(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The user's repository: a temporary git repository with the check script committed.</summary>
    public static TempGitRepo Repo() => Phase3Repos.WithScript("check.ps1", CheckScript);

    public static VerificationProfile Profile() =>
        new("fase 5", [Phase3Repos.ScriptStep("check", "Comprobar la función", "check.ps1", TimeSpan.FromMinutes(3))]);

    /// <summary>T1, its integration I1 and the review R1 of the integration.</summary>
    public static PlanDefinition Reviewed() => Plan(
        Work("T1", scope: ["src/"]).Verified("check"),
        Node("I1", WorkTaskKind.Integration).After(On("T1")) with { VerificationSteps = ["check"] },
        Node("R1", WorkTaskKind.Review).After(On("I1")));

    /// <summary>Starts a run of <paramref name="plan"/> approved on the canvas and waits for its end.</summary>
    public static async Task<RunResult> RunAsync(Phase4App app, PlanDefinition plan, string request = "Añade la función y que el juez la revise.")
    {
        await using var run = await app.Orchestrator.StartAsync(await app.RunRequestAsync(request, Requirements(), plan, Profile()), Ct);
        return await run.Completion.WaitAsync(Guard, Ct);
    }

    /// <summary>A reviewed run that ended Succeeded with the verdict of its judge; the assertion message has the log.</summary>
    public static async Task<RunResult> SucceededAsync(Phase4App app)
    {
        var result = await RunAsync(app, Reviewed());
        Assert.True(result.Phase == RunPhase.Succeeded && result.VerdictId is not null && result.IntegrationCommit is not null, Phase4Checks.Describe(result) + "\n" + Phase4Checks.Describe(await app.LoadLogAsync()));
        return result;
    }
}

/// <summary>What the Phase 5 tests read back from the stores of an app.</summary>
internal static class Phase5Checks
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A frozen set a debate that chooses is judged against: the judge's own assessment (docs/15 D-25).</summary>
    public static FrozenRequirementSet ChoiceRequirements() => FrozenRequirementSet.Freeze(
        [
            new Requirement(
                new RequirementId("RD-01"),
                "Elegir el diseño de la caché del servicio",
                RequirementPriority.Mandatory,
                [new AcceptanceCriterion("c1", "El juez valora qué propuesta resuelve mejor la pregunta", VerificationMethod.JudgeAssessment)]),
        ],
        Start);

    /// <summary>The entries of one unit of the log (a run, a debate or an audit), in log order.</summary>
    public static async Task<IReadOnlyList<DecisionEntry>> EntriesOfAsync(Phase4App app, string unit) =>
        RunLog.EntriesOf((await app.LoadLogAsync()).Entries, unit);

    /// <summary>The text of an evidence, only if it hashes to <paramref name="sha256"/>.</summary>
    public static async Task<string> TextAsync(Phase4App app, EvidenceId id, string sha256)
    {
        byte[] content;
        await using (var stream = await app.Get<IEvidenceStore>().OpenContentAsync(app.Project.ProjectId, id, Ct))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            content = copy.ToArray();
        }

        Assert.Equal(sha256, ContentHash.Sha256Hex(content));
        return Encoding.UTF8.GetString(content);
    }

    /// <summary>The lines of the project's prompt journal (ADR-0008), every day of it.</summary>
    public static IReadOnlyList<string> JournalLines(string projectRoot) =>
        [.. Phase3Checks.JournalFiles(projectRoot).SelectMany(File.ReadAllLines)];

    /// <summary>
    /// The end of a debate. A debate that stops to wait for a person (a turn that failed twice, a judgment that needs one)
    /// fails at once with what it waits for, instead of waiting for the guard.
    /// </summary>
    public static async Task<DebateResult> EndAsync(IDebateRun debate)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!debate.Completion.IsCompleted)
        {
            if (debate.Snapshot() is { Awaiting: { } awaiting } snapshot)
            {
                Assert.Fail($"The debate {debate.Id} waits for a person ({awaiting.Reason}): {awaiting.Message} | {string.Join(" | ", snapshot.Problems)}");
            }

            if (watch.Elapsed > Phase5Runs.Guard)
            {
                throw new TimeoutException($"The debate {debate.Id} did not end in {Phase5Runs.Guard} ({debate.Snapshot().Phase}).");
            }

            await Task.WhenAny(debate.Completion, Task.Delay(100, Ct));
        }

        return await debate.Completion;
    }

    /// <summary>Prepares and opens a debate, asserting the proposal has no problems.</summary>
    public static async Task<IDebateRun> OpenAsync(Phase4App app, DebateRequest request)
    {
        var coordinator = app.Get<IDebateCoordinator>();
        var proposal = await coordinator.PrepareAsync(request, Ct);
        Assert.True(proposal.Problems.Count == 0, string.Join(" | ", proposal.Problems));
        return await coordinator.OpenAsync(proposal, Ct);
    }

    /// <summary>
    /// The state of a debate replayed by the domain (<see cref="DebateStateReplayer"/>) from what the stores of
    /// <paramref name="app"/> hold: the header of the debate registry, the requirements, the question read against the hash
    /// <c>DebateOpened</c> anchored, and the events of the verified log with the verdicts of the store.
    /// </summary>
    public static async Task<(DebateReplay Replay, DebateLog Log, DebateRecord Record)> ReplayAsync(Phase4App app, RunId debate)
    {
        var projectId = app.Project.ProjectId;
        var record = await app.Get<IDebateRegistry>().FindAsync(projectId, debate, Ct) ?? throw new InvalidOperationException($"No header for {debate}.");
        var state = await app.LoadLogAsync();
        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        var log = DebateLog.Of(state.Entries, debate);
        var opened = log.Opened ?? throw new InvalidOperationException($"No DebateOpened for {debate}.");
        Assert.Equal(DebateHeaderHashOf(record), opened.HeaderHash);
        var question = await TextAsync(app, record.Question, opened.QuestionSha256);
        var requirements = await app.Get<IRequirementStore>().FindAsync(projectId, record.RequirementSetHash, Ct) ?? throw new InvalidOperationException("No requirements.");
        var stored = await app.Get<IVerdictStore>().ListAsync(projectId, null, Ct);
        var header = new DebateHeader(
            new DebateId(record.Id.Value),
            record.Mode,
            record.Subject.Kind,
            question,
            requirements,
            [.. record.Participants.Select(p => new DebateParticipant(p.Agent.Id, p.Role, p.Agent.Model))],
            new JudgingArrangement(record.Judging, [.. record.Judges.Select(j => new JudgeIdentity(j.Id, j.Model))], new HashSet<VendorId>(record.ProducerVendors), new HashSet<VendorId>(record.WaivedParticipantVendors)),
            record.Limits,
            record.CreatedAt);
        var replay = DebateStateReplayer.Replay(header, log.Events(record.CreatedAt, ids => [.. ids.Select(id => stored.First(v => v.Id == id)).Select(v => (v.Verdict, v.Valid))]));
        return (replay, log, record);
    }

    /// <summary>The JSON paths (<c>$.subject.run.decisions[0].payload</c>…) of every string of <paramref name="root"/> that contains <paramref name="token"/>.</summary>
    public static IReadOnlyList<string> PathsContaining(JsonElement root, string token)
    {
        var paths = new List<string>();
        void Walk(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Walk(property.Value, $"{path}.{property.Name}");
                    }

                    break;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, $"{path}[{index++}]");
                    }

                    break;
                case JsonValueKind.String when element.GetString()!.Contains(token, StringComparison.Ordinal):
                    paths.Add(path);
                    break;
            }
        }

        Walk(root, "$");
        return paths;
    }

    /// <summary>The prompt of a judge without its dossier: the rubric and where the material is.</summary>
    public static string OutsideDossier(string prompt)
    {
        var start = prompt.IndexOf(JudgePrompt.DataOpen, StringComparison.Ordinal);
        var end = prompt.IndexOf(JudgePrompt.DataClose, StringComparison.Ordinal);
        return start < 0 || end < start ? prompt : prompt[..start] + prompt[(end + JudgePrompt.DataClose.Length)..];
    }

    private static string DebateHeaderHashOf(DebateRecord record) => AgentStudio.Application.Debate.DebateHeaderHash.Of(record);
}
