using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate;

/// <summary>
/// docs/17 D-02: the launch of a judge carries the <c>AgentRunStarted</c> its recorder wrote (the run, the entry task of
/// the scope and the attempt), so the journal can name it; an observer that records nothing names nothing.
/// </summary>
public sealed class JudgeJournalAnchorTests
{
    private const string Project = "p1";
    private const string Run = "20261015-a1b2c3";

    private static readonly AgentDefinition CodexJudge = JudgingWorld.Agent(new AgentId("juez-revision"), AgentRole.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5"));

    private sealed class SilentObserver : IJudgeRunObserver
    {
        public Task RunStartingAsync(JudgeRunStart start, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunFinishedAsync(JudgeRunEnd end, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_launch_of_the_judge_of_a_run_carries_the_start_its_recorder_wrote()
    {
        var clock = new FakeTimeProvider(T0);
        var log = new InMemoryDecisionLogStore();
        var recorder = new JudgeRunRecorder(new DecisionRecorder(log, clock), new FakeEvidenceStore(clock), JudgeRunScope.ForRunReview(Project, Run, new TaskId("revision")), CodexJudge, clock, reportsContext: false);
        var dossier = Dossier();
        var provider = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (_, _) => JudgeAnswers.Run(JudgeAnswers.Approval(dossier, Eid("E1"))));
        var judge = new AgentJudge(provider, new JudgeLaunch(CodexJudge, "C:\\wt\\_integration", null, null, recorder, "Revisión de prueba", "T2"), clock);

        await judge.EvaluateAsync(dossier, TestContext.Current.CancellationToken);

        var started = DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(Assert.Single(log.Of(Project), e => e.Kind == DecisionKind.AgentRunStarted).PayloadJson);
        Assert.Equal((Run, (TaskId?)null, 1), (started.RunId, started.Task, started.Attempt));
        Assert.Equal(new AgentRunAnchor(Run, null, 1), Assert.Single(provider.Requests).Anchor);
    }

    [Fact]
    public async Task The_judge_of_a_debate_names_its_own_task()
    {
        var clock = new FakeTimeProvider(T0);
        var recorder = new JudgeRunRecorder(new DecisionRecorder(new InMemoryDecisionLogStore(), clock), new FakeEvidenceStore(clock), JudgeRunScope.ForDebate(Project, "deb-20261019-a1b2c3", CodexJudge.Id), CodexJudge, clock, reportsContext: false);
        var start = new JudgeRunStart(2, new AgentRunRequest(CodexJudge, "prompt", "C:\\snapshot", new PermissionProfile(false, false, false, [], [], [])), ProviderId.CodexCli, VendorId.OpenAI);

        await recorder.RunStartingAsync(start, TestContext.Current.CancellationToken);

        Assert.Equal(new AgentRunAnchor("deb-20261019-a1b2c3", "juez-juez-revision", 2), ((IJudgeRunObserver)recorder).AnchorOf(start));
    }

    [Fact]
    public void An_observer_that_records_no_start_names_none()
    {
        IJudgeRunObserver observer = new SilentObserver();
        var start = new JudgeRunStart(1, new AgentRunRequest(CodexJudge, "prompt", "C:\\snapshot", new PermissionProfile(false, false, false, [], [], [])), ProviderId.CodexCli, VendorId.OpenAI);

        Assert.Null(observer.AnchorOf(start));
    }
}
