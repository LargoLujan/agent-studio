using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate;

/// <summary>
/// docs/15 D-23 and D-44: the recorder of the launches of a judge, the keeper of its verdicts and the brief of what it is
/// asked moved out of the review of a run so the judgment of a debate and the audit use them. The review records exactly
/// what it did (its own tests pin it); a debate and an audit record their judges with their own task and purpose.
/// </summary>
public sealed class JudgeRunRecorderTests
{
    private const string Project = "p1";
    private const string Debate = "deb-20261019-a1b2c3";

    private static readonly AgentDefinition CodexJudge = JudgingWorld.Agent(new AgentId("juez-revision"), AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5"));

    private sealed class World
    {
        public FakeTimeProvider Clock { get; } = new(T0);

        public InMemoryDecisionLogStore Log { get; } = new();

        public DecisionRecorder Recorder { get; }

        public FakeEvidenceStore Evidence { get; }

        public FakeVerdictStore Verdicts { get; } = new();

        public World()
        {
            Recorder = new DecisionRecorder(Log, Clock);
            Evidence = new FakeEvidenceStore(Clock);
        }

        public IReadOnlyList<(DecisionEntry Entry, T Payload)> Payloads<T>()
            where T : DecisionPayload =>
            [.. Log.Of(Project).Where(e => DecisionPayloadSerializer.PayloadTypeFor(e.Kind) == typeof(T)).Select(e => (e, DecisionPayloadSerializer.Deserialize<T>(e.PayloadJson)))];
    }

    private static JudgeRunStart Start(int attempt, string? resume = null) =>
        new(attempt, new AgentRunRequest(CodexJudge, $"prompt {attempt}", "C:\\snapshot", new PermissionProfile(false, false, false, [], [], []), ResumeSessionId: resume), ProviderId.CodexCli, VendorId.OpenAI);

    private static JudgeRunEnd End(int attempt) =>
        new(attempt, new AgentRunOutcome(AgentRunStatus.Succeeded, 0, "{}", "{\"decision\":\"Approved\"}", "thread-1", null), null, null);

    [Fact]
    public async Task The_judge_of_a_debate_is_recorded_in_the_debate_with_its_own_task_and_purpose()
    {
        var world = new World();
        var recorder = new JudgeRunRecorder(world.Recorder, world.Evidence, JudgeRunScope.ForDebate(Project, Debate, CodexJudge.Id), CodexJudge, world.Clock, reportsContext: false);
        var ct = TestContext.Current.CancellationToken;

        await recorder.RunStartingAsync(Start(1), ct);
        recorder.Report(new SessionStarted(T0, "thread-1", null));
        await recorder.RunFinishedAsync(End(1), ct);
        await recorder.RunStartingAsync(Start(2, resume: "thread-1"), ct);
        await recorder.RunFinishedAsync(End(2), ct);

        var task = new TaskId("juez-juez-revision");
        var starts = world.Payloads<AgentRunStartedPayload>();
        var ends = world.Payloads<AgentRunFinishedPayload>();
        Assert.Equal([(Debate, task, 1, AgentRunPurpose.DebateJudge), (Debate, task, 2, AgentRunPurpose.DebateJudge)], starts.Select(s => (s.Payload.RunId, s.Payload.Task!.Value, s.Payload.Attempt, s.Payload.Purpose!.Value)));
        Assert.Equal([(Debate, task, 1, (string?)null), (Debate, task, 2, "thread-1")], ends.Select(e => (e.Payload.RunId, e.Payload.Task!.Value, e.Payload.Attempt, e.Payload.ResumedSession)));
        Assert.All(world.Evidence.Drafts, r => Assert.Equal((Debate, "juez-juez-revision"), (r.RunId, r.TaskId)));
        Assert.Equal(T0, ends[0].Payload.LaunchedAt);
    }

    /// <summary>docs/18 P47: the judge's effort is recorded with its start (version 3), so the estimate of a review tells attempts at another effort; none when it sets none.</summary>
    [Fact]
    public async Task The_judge_records_its_effort_only_when_it_sets_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var withEffort = new World();
        var without = new World();
        var strong = CodexJudge with { Effort = ReasoningEffort.XHigh };

        await new JudgeRunRecorder(withEffort.Recorder, withEffort.Evidence, JudgeRunScope.ForRunReview(Project, "20261015-a1b2c3", new TaskId("T2")), strong, withEffort.Clock, reportsContext: false)
            .RunStartingAsync(Start(1), ct);
        await new JudgeRunRecorder(without.Recorder, without.Evidence, JudgeRunScope.ForRunReview(Project, "20261015-a1b2c3", new TaskId("T2")), CodexJudge, without.Clock, reportsContext: false)
            .RunStartingAsync(Start(1), ct);

        var (entry, start) = Assert.Single(withEffort.Payloads<AgentRunStartedPayload>());
        Assert.Equal((AgentRunStartedPayload.ContextPartsVersion, ReasoningEffort.XHigh, AgentRunPurpose.Judge), (start.V, start.Effort, start.Purpose));
        Assert.Contains("\"effort\":\"xhigh\"", entry.PayloadJson, StringComparison.Ordinal);
        var (noneEntry, none) = Assert.Single(without.Payloads<AgentRunStartedPayload>());
        Assert.Equal((AgentRunStartedPayload.PurposeVersion, (ReasoningEffort?)null), (none.V, none.Effort));
        Assert.DoesNotContain("effort", noneEntry.PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>The turn of a participant in a debate writes its agent's effort the same way: version 3, only when it is set.</summary>
    [Fact]
    public async Task The_turn_of_a_participant_records_its_effort_only_when_it_sets_one()
    {
        var world = new World();
        var turns = new DebateTurnRecorder(world.Recorder, world.Evidence, Project, new RunId(Debate));
        var provider = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (_, _) => throw new InvalidOperationException("never launched"));
        var ct = TestContext.Current.CancellationToken;
        var prompt = await turns.CapturePromptAsync(new TaskId("T1"), 1, "Prompt del turno", "texto", ct);

        await turns.RecordTurnStartedAsync(new TaskId("T1"), 1, CodexJudge with { Effort = ReasoningEffort.Max }, provider, "C:\\wt", prompt, "A", ct);
        await turns.RecordTurnStartedAsync(new TaskId("T2"), 1, CodexJudge, provider, "C:\\wt", prompt, "B", ct);

        var starts = world.Payloads<AgentRunStartedPayload>();
        Assert.Equal([(ReasoningEffort.Max, 3), ((ReasoningEffort?)null, 2)], starts.Select(s => (s.Payload.Effort, s.Payload.V)));
        Assert.Contains("\"effort\":\"max\"", starts[0].Entry.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("effort", starts[1].Entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_judge_of_an_audit_is_recorded_in_the_audit_as_an_audit()
    {
        var world = new World();
        var recorder = new JudgeRunRecorder(world.Recorder, world.Evidence, JudgeRunScope.ForAudit(Project, "aud-20261019-0a1b2c", CodexJudge.Id), CodexJudge, world.Clock, reportsContext: false);

        await recorder.RunStartingAsync(Start(1), TestContext.Current.CancellationToken);

        var start = Assert.Single(world.Payloads<AgentRunStartedPayload>()).Payload;
        Assert.Equal(("aud-20261019-0a1b2c", AgentRunPurpose.ProjectAudit, "juez-juez-revision"), (start.RunId, start.Purpose!.Value, start.Task!.Value.Value));
    }

    /// <summary>The review of a run keeps recording as Phase 3 did: no task in the entries, the review task in the evidence.</summary>
    [Fact]
    public async Task The_judge_of_a_run_review_is_recorded_as_before()
    {
        var world = new World();
        var recorder = new JudgeRunRecorder(world.Recorder, world.Evidence, JudgeRunScope.ForRunReview(Project, "20261015-a1b2c3", new TaskId("T2")), CodexJudge, world.Clock, reportsContext: true);

        await recorder.RunStartingAsync(Start(1), TestContext.Current.CancellationToken);
        await recorder.RunFinishedAsync(End(1), TestContext.Current.CancellationToken);

        var start = Assert.Single(world.Payloads<AgentRunStartedPayload>()).Payload;
        var end = Assert.Single(world.Payloads<AgentRunFinishedPayload>()).Payload;
        Assert.Equal((null, AgentRunPurpose.Judge, null), (start.Task, start.Purpose!.Value, end.Task));
        Assert.All(world.Evidence.Drafts, r => Assert.Equal("T2", r.TaskId));
        Assert.Equal(0, end.Compactions);
    }

    [Fact]
    public async Task A_verdict_is_kept_in_its_unit_task_and_scope_and_announced_with_the_material_it_examined()
    {
        var world = new World();
        var dossier = Dossier();
        var keeper = new VerdictKeeper(world.Verdicts, world.Recorder, Project, Debate, JudgeRunScope.JudgeTask(CodexJudge.Id), AuditScope.Debate);
        var verdict = ValidVerdict(dossier) with { Resolution = new DebateResolution("P1", []) };

        var first = await keeper.KeepAsync(CodexJudge.Id, dossier, verdict, [new VerdictViolation(VerdictViolationCode.DebateResolutionUnknown, "x")], null, "Juez en 'openai'");
        var second = await keeper.KeepAsync(CodexJudge.Id, dossier, verdict, [], first, "Juez en 'openai'");

        Assert.Equal(
            [(Debate, "juez-juez-revision", AuditScope.Debate, false, (string?)null), (Debate, "juez-juez-revision", AuditScope.Debate, true, first)],
            world.Verdicts.Stored.Select(v => (v.RunId, v.TaskId, v.Scope, v.Valid, v.Supersedes)));
        var issued = world.Payloads<VerdictIssuedPayload>();
        Assert.Equal([first, second], issued.Select(i => i.Payload.VerdictId));
        Assert.All(issued, i => Assert.Equal((Debate, dossier.Requirements.ContentHash, dossier.DecisionLogHeadHash, ActorKind.Judge, "juez-revision"), (i.Payload.RunId, i.Payload.RequirementSetHash, i.Payload.DecisionLogHeadHash, i.Entry.Actor.Kind, i.Entry.Actor.Id)));
        Assert.Equal([VerdictViolationCode.DebateResolutionUnknown], issued[0].Payload.Violations);
        Assert.Same(verdict, world.Verdicts.Stored[1].Verdict);
    }

    /// <summary>A brief that asks for something else, so what the judge sends can be told apart from the review of a run.</summary>
    private sealed class MarkedBrief(Verdict answer) : JudgeBrief
    {
        public override string Purpose => "Juicio del debate";

        public override IReadOnlyList<string> Tags { get; } = ["#juicio"];

        public override string? DebateId => Debate;

        public override string Evaluation(JudgeDossier dossier, DossierFiles? files, string workingDirectory) => "EVALUA " + workingDirectory;

        public override string Correction(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, bool resumed, DossierFiles? files, string workingDirectory) =>
            $"CORRIGE {violations.Count} {resumed}";

        public override string Schema(JudgeDossier dossier) => "{\"type\":\"object\"}";

        public override Verdict Read(string json, JudgeIdentity judge, DateTimeOffset issuedAt) => answer with { Judge = judge, IssuedAt = issuedAt };
    }

    [Fact]
    public async Task A_brief_decides_what_the_judge_is_asked_and_how_it_is_read_but_never_its_permissions_nor_its_identity()
    {
        var dossier = Dossier();
        var claimed = ValidVerdict(dossier) with { Judge = new JudgeIdentity(new AgentId("otro"), new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic)) };
        var provider = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (_, _) =>
            new ScriptedRun(new AgentRunOutcome(AgentRunStatus.Succeeded, 0, "{}", "{}", "thread-9", null), new SessionStarted(T0, "thread-9", "gpt-5.5")));
        var clock = new FakeTimeProvider(T0.AddHours(1));
        var judge = new AgentJudge(provider, new JudgeLaunch(CodexJudge, "C:\\snapshot", null, Brief: new MarkedBrief(claimed)), clock);

        var verdict = await judge.EvaluateAsync(dossier, TestContext.Current.CancellationToken);
        await judge.ReviseAsync(dossier, verdict, [new VerdictViolation(VerdictViolationCode.MalformedVerdict, "x")], TestContext.Current.CancellationToken);

        var asked = provider.Requests[0];
        Assert.Equal(("EVALUA C:\\snapshot", "{\"type\":\"object\"}", "Juicio del debate", Debate), (asked.Prompt, asked.OutputJsonSchema, asked.Purpose, asked.DebateId));
        Assert.Equal(["#juicio"], asked.Tags!);
        Assert.Equal("CORRIGE 1 True", provider.Requests[1].Prompt);
        Assert.All(provider.Requests, r => Assert.Equal((false, false, false, true), (r.Permissions.AllowFileWrites, r.Permissions.AllowShell, r.Permissions.AllowNetwork, r.PersistSession)));
        Assert.Equal((CodexJudge.Id, VendorId.OpenAI, "gpt-5.5", T0.AddHours(1)), (verdict.Judge.Agent, verdict.Judge.Model.Vendor, verdict.Judge.Model.Model, verdict.IssuedAt));
    }

    [Fact]
    public void The_review_of_a_run_is_the_brief_of_phase_3()
    {
        var dossier = Dossier();
        var brief = JudgeBrief.RunReview;

        Assert.Equal(JudgePrompt.Evaluation(dossier, null, "C:\\wt"), brief.Evaluation(dossier, null, "C:\\wt"));
        Assert.Equal(VerdictSchema.Build(dossier.Requirements), brief.Schema(dossier));
        Assert.Equal(["#revision"], brief.Tags);
        Assert.Equal("Revisión independiente (juez)", brief.Purpose);
        Assert.Null(brief.DebateId);
    }
}
