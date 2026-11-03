using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Providers.Composition;
using AgentStudio.Security.Approvals;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>
/// A project with the arena of debates over in-memory stores: the real decision recorder, approval gate, redactor and provider
/// pipeline (journal and concurrency limit), two scripted CLIs (<c>claude-code</c> on Anthropic and <c>codex-cli</c> on OpenAI),
/// a scripted judgment, and a template with participants and judges of both vendors.
/// </summary>
internal sealed class ScriptedDebateWorld : IDisposable
{
    public const string Project = "7a2d3c4e-0000-4000-8000-000000000005";

    public static readonly AgentId Arquitecto = new("arquitecto");
    public static readonly AgentId Revisor = new("revisor");
    public static readonly AgentId Critico = new("critico");
    public static readonly AgentId Analista = new("analista");
    public static readonly AgentId JuezClaude = new("juez-claude");
    public static readonly AgentId JuezCodex = new("juez-codex");
    public static readonly AgentId JuezRevision = new("juez-revision");
    public static readonly RunId FinishedRun = new("20261019-0f0f0f");
    public static readonly string Candidate = new('c', 40);
    public static readonly Actor System = new(ActorKind.System, "agent-studio");

    public ScriptedDebateWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "agentstudio-test-" + Guid.NewGuid().ToString("N"));
        RepoRoot = Path.Combine(Root, "repo");
        DebatesRoot = Path.Combine(Root, "debates");
        Directory.CreateDirectory(RepoRoot);
        Recorder = new DecisionRecorder(LogStore, Clock);
        Evidence = new FakeEvidenceStore(Clock);
        Approvals = new ApprovalGate(Prompt, Recorder, Clock, new ApprovalGateOptions());
        Judgment = new ScriptedDebateJudgment(Recorder, Debates, Verdicts, Clock);
        Frozen = FrozenRequirementSet.Freeze([Req("R1", RequirementPriority.Mandatory, "Elegir la mejor forma de cachear", Crit("c1", VerificationMethod.JudgeAssessment, "El juez valora la propuesta"))], T0);
        Roster = new ScriptedDebateRoster(
        [
            JudgingWorld.Agent(Arquitecto, AgentRole.Architect, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku")),
            JudgingWorld.Agent(Revisor, AgentRole.Critic, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet")),
            JudgingWorld.Agent(Critico, AgentRole.Critic, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5-codex")),
            JudgingWorld.Agent(Analista, AgentRole.Tester, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5")),
            JudgingWorld.Agent(JuezClaude, AgentRole.Judge, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet")),
            JudgingWorld.Agent(JuezCodex, AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5-codex")),
            JudgingWorld.Agent(JuezRevision, AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5")),
        ]);
        Worktrees.Commits[Worktrees.HeadCommit] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Cache.cs"] = string.Concat(Enumerable.Range(1, 20).Select(i => $"// línea {i}\n")),
            ["README.md"] = "# Proyecto\n",
        };
        Worktrees.Commits[Candidate] = new Dictionary<string, string>(StringComparer.Ordinal) { ["src/Calc.cs"] = "public static class Calc { }\n" };
        var providers = new FakeProjectAgentProviders(
            AgentProviderPipeline.Compose(Claude, Journal, Clock),
            AgentProviderPipeline.Compose(Codex, Journal, Clock));
        Options = new DebateArenaOptions(DebatesRoot);
        Ports = new DebateArenaPorts(Recorder, Evidence, Verdicts, Requirements, Runs, Debates, Roster, providers, Worktrees, Approvals, Activity, Judgment, Redactor, Options, Clock);
        Coordinator = new DebateCoordinator(Ports);
    }

    public FakeTimeProvider Clock { get; } = new(T0);

    public InMemoryDecisionLogStore LogStore { get; } = new();

    public DecisionRecorder Recorder { get; }

    public FakeEvidenceStore Evidence { get; }

    public FakeVerdictStore Verdicts { get; } = new();

    public FakeRequirementStore Requirements { get; } = new();

    public FakeRunRegistry Runs { get; } = new();

    public FakeDebateRegistry Debates { get; } = new();

    public ScriptedDebateRoster Roster { get; }

    public ScriptedDebateWorktrees Worktrees { get; } = new();

    public ScriptedApprovalPrompt Prompt { get; } = new();

    public ApprovalGate Approvals { get; }

    public FakeProjectActivity Activity { get; } = new();

    public ScriptedDebateJudgment Judgment { get; }

    public SecretRedactor Redactor { get; } = new(@"C:\Users\persona");

    public InMemoryPromptJournal Journal { get; } = new();

    public ScriptedDebateProvider Claude { get; } = new(ProviderId.ClaudeCode, VendorId.Anthropic);

    public ScriptedDebateProvider Codex { get; } = new(ProviderId.CodexCli, VendorId.OpenAI);

    public DebateArenaOptions Options { get; }

    public DebateArenaPorts Ports { get; set; }

    public DebateCoordinator Coordinator { get; set; }

    public FrozenRequirementSet Frozen { get; }

    public string Root { get; }

    public string RepoRoot { get; }

    public string DebatesRoot { get; }

    public ProjectContext Context => new(Project, RepoRoot);

    /// <summary>A debate request of this project; free about nothing, with the given participants and judges, by default.</summary>
    public DebateRequest Request(
        DebateMode mode,
        IReadOnlyList<(AgentId Agent, DebateRole Role)> participants,
        IReadOnlyList<AgentId> judges,
        DebateJudgingKind judging = DebateJudgingKind.IndependentJudge,
        DebateSubject? subject = null,
        DebateLimits? limits = null,
        FrozenRequirementSet? requirements = null,
        int? maxAgentRuns = null,
        string question = "¿Cómo cacheamos las respuestas del servicio?") =>
        new(
            Context,
            mode,
            question,
            requirements ?? Frozen,
            subject ?? new DebateSubject(DebateSubjectKind.None, null),
            [.. participants.Select(p => new DebateParticipantRequest(p.Agent, p.Role))],
            judges,
            judging,
            limits ?? DebateLimits.Default,
            maxAgentRuns);

    /// <summary>Two participants of Anthropic (with different models) and the judge of OpenAI: the default disposition (D-13).</summary>
    public DebateRequest FreeOnAnthropic(DebateLimits? limits = null, int? maxAgentRuns = null, DebateSubject? subject = null) =>
        Request(DebateMode.Free, [(Arquitecto, DebateRole.Proponent), (Revisor, DebateRole.Critic)], [JuezCodex], limits: limits, maxAgentRuns: maxAgentRuns, subject: subject);

    public async Task<IDebateRun> OpenAsync(DebateRequest request)
    {
        var proposal = await Coordinator.PrepareAsync(request, TestContext.Current.CancellationToken);
        Assert.True(proposal.Problems.Count == 0, string.Join(" | ", proposal.Problems));
        return await Coordinator.OpenAsync(proposal, TestContext.Current.CancellationToken);
    }

    /// <summary>The result of a debate, never waiting forever.</summary>
    public static async Task<DebateResult> CompletedAsync(IDebateRun run) =>
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    public async Task<DecisionLogState> LogAsync() => await Recorder.LoadAsync(Project, TestContext.Current.CancellationToken);

    public async Task<DebateLog> DebateLogAsync(RunId debate) => DebateLog.Of((await LogAsync()).Entries, debate);

    /// <summary>A finished run in the log, by Anthropic (planned and executed by Claude), whose last integration is <see cref="Candidate"/>.</summary>
    public async Task RecordFinishedRunAsync(FrozenRequirementSet? requirements = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = FinishedRun.Value;
        var roster = new[]
        {
            new RosterEntry(new AgentId("orquestador"), AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic),
            new RosterEntry(new AgentId("ejecutor"), AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic),
        };
        await Recorder.RecordAsync(Project, System, DecisionKind.RunStarted, "Ejecución iniciada",
            new RunStartedPayload(HashA, (requirements ?? Frozen).ContentHash, Worktrees.HeadCommit, "main", HashB, roster, null) { RunId = run }, cancellationToken: ct);
        await Recorder.RecordAsync(Project, System, DecisionKind.IntegrationPerformed, "Integración",
            new IntegrationPerformedPayload(new TaskId("I1"), "agentstudio/run-" + run, ["refs/agentstudio/" + run + "/T1"], Candidate, [], false) { RunId = run }, cancellationToken: ct);
    }

    public void Dispose() => TempFolders.Delete(Root);
}
