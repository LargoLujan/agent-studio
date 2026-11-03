using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>F3-1: conditional work, both branches, skip propagation and the AnyCompleted join (ADR-0011).</summary>
public sealed class ConditionalTests
{
    /// <summary>
    /// T1 reports a route; C1 asks whether it is "corta"; the yes branch is Y1 then Y2, the no branch N1; J1 joins both
    /// with AnyCompleted.
    /// </summary>
    private static PlanDefinition Branching() => Plan(
        Work("T1"),
        Node("C1", WorkTaskKind.Condition).After(On("T1")) with { Condition = new OutputEquals(new TaskId("T1"), "ruta", "corta") },
        Work("Y1").After(On("C1", "yes")),
        Work("Y2").After(On("Y1")),
        Work("N1").After(On("C1", "no")),
        Work("J1", workspace: WorkspaceMode.None).After(On("Y2"), On("N1")) with { Join = JoinPolicy.AnyCompleted });

    private static void Route(EngineHarness harness, string route) =>
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { StructuredOutput = AgentScript.Report(outputs: [("ruta", route)]) });

    [Fact]
    public async Task The_yes_branch_runs_and_the_no_branch_is_skipped()
    {
        using var harness = new EngineHarness();
        Route(harness, "corta");

        await using var run = await harness.StartAsync(Branching());
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var snapshot = run.Snapshot();
        Assert.Equal(WorkTaskStatus.Completed, snapshot.View("Y1").Status);
        Assert.Equal(WorkTaskStatus.Completed, snapshot.View("Y2").Status);
        Assert.Equal(WorkTaskStatus.Skipped, snapshot.View("N1").Status);
        Assert.Equal(WorkTaskStatus.Completed, snapshot.View("J1").Status);
        Assert.DoesNotContain(harness.Claude.Starts, s => s.Request.TaskId == "N1");
        var (_, evaluated) = Assert.Single(harness.Payloads<ConditionEvaluatedPayload>());
        Assert.Equal(new TaskId("C1"), evaluated.Task);
        Assert.True(evaluated.Result);
        Assert.Equal("yes", evaluated.Branch);
        Assert.Contains(new ConditionFact("T1.output.ruta", "corta"), evaluated.Facts);
        Assert.Contains(new ConditionFact("T1.status", "Completed"), evaluated.Facts);
        Assert.Contains("N1 Pending>Skipped", harness.Transitions());
    }

    [Fact]
    public async Task The_no_branch_runs_and_skips_cascade_down_the_yes_branch()
    {
        using var harness = new EngineHarness();
        Route(harness, "larga");

        await using var run = await harness.StartAsync(Branching());
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var snapshot = run.Snapshot();
        Assert.Equal(WorkTaskStatus.Skipped, snapshot.View("Y1").Status);
        Assert.Equal(WorkTaskStatus.Skipped, snapshot.View("Y2").Status);
        Assert.Equal(WorkTaskStatus.Completed, snapshot.View("N1").Status);
        Assert.Equal(WorkTaskStatus.Completed, snapshot.View("J1").Status);
        var transitions = harness.Transitions();
        Assert.True(transitions.IndexOf("Y1 Pending>Skipped") < transitions.IndexOf("Y2 Pending>Skipped"));
        Assert.Equal("no", Assert.Single(harness.Payloads<ConditionEvaluatedPayload>()).Payload.Branch);
        Assert.Equal(["T1", "N1", "J1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
    }

    [Fact]
    public async Task The_condition_outcome_is_recorded_just_before_the_condition_completes()
    {
        using var harness = new EngineHarness();
        Route(harness, "corta");

        await using var run = await harness.StartAsync(Branching());
        await run.CompleteAsync(harness);

        var entries = harness.Entries
            .Select(e => (e.Kind, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)))
            .Where(x => x.Payload is ConditionEvaluatedPayload or TaskStatusChangedPayload { Task.Value: "C1" })
            .Select(x => x.Kind)
            .ToList();
        Assert.Equal([DecisionKind.TaskStatusChanged, DecisionKind.ConditionEvaluated, DecisionKind.TaskStatusChanged], entries);
    }

    [Fact]
    public async Task TestsPassed_reads_the_test_run_the_system_captured()
    {
        using var harness = new EngineHarness();
        var plan = Plan(
            Work("T1").Verified("build", "test"),
            Node("C1", WorkTaskKind.Condition).After(On("T1")) with { Condition = new TestsPassed(new TaskId("T1")) },
            Work("Y1").After(On("C1", "yes")),
            Work("N1").After(On("C1", "no")));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var (_, evaluated) = Assert.Single(harness.Payloads<ConditionEvaluatedPayload>());
        Assert.Equal("yes", evaluated.Branch);
        Assert.Contains(new ConditionFact("T1.tests", "passed=3 failed=0 skipped=0 exit=0"), evaluated.Facts);
        Assert.Equal(WorkTaskStatus.Skipped, run.Snapshot().View("N1").Status);
    }

    [Fact]
    public async Task PathsChanged_reads_the_paths_of_the_real_diff()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/api/Calc.cs", "class Calc {}")));
        var plan = Plan(
            Work("T1"),
            Node("C1", WorkTaskKind.Condition).After(On("T1")) with { Condition = new PathsChanged(new TaskId("T1"), "src/T1/api/") },
            Work("Y1").After(On("C1", "yes")),
            Work("N1").After(On("C1", "no")));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var (_, evaluated) = Assert.Single(harness.Payloads<ConditionEvaluatedPayload>());
        Assert.Equal("yes", evaluated.Branch);
        Assert.Contains(new ConditionFact("T1.changedPaths", "src/T1/api/Calc.cs"), evaluated.Facts);
    }

    [Fact]
    public async Task AnyCompleted_waits_for_every_live_edge_and_a_broken_one_makes_the_join_unreachable()
    {
        using var harness = new EngineHarness();
        Route(harness, "larga");
        harness.Claude.Script("ejecutor", "N1", 1, AgentScript.Failing());
        var plan = Plan(
            Work("T1"),
            Node("C1", WorkTaskKind.Condition).After(On("T1")) with { Condition = new OutputEquals(new TaskId("T1"), "ruta", "corta") },
            Work("Y1").After(On("C1", "yes")),
            Work("N1").After(On("C1", "no")) with { MaxRetries = 0 },
            Work("J1", workspace: WorkspaceMode.None).After(On("Y1"), On("N1")) with { Join = JoinPolicy.AnyCompleted });

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("J1").Status);
        Assert.Contains(new TaskId("J1"), result.Health.UnreachableTasks);
        Assert.Single(harness.Alerts("task.unreachable"));
    }

    [Fact]
    public async Task Replaying_the_log_of_a_conditional_run_gives_the_live_state()
    {
        using var harness = new EngineHarness();
        Route(harness, "larga");

        await using var run = await harness.StartAsync(Branching());
        await run.CompleteAsync(harness);
        var replay = await harness.ReplayAsync(run.Id);

        Assert.True(replay.IsResumable, string.Join("; ", replay.Problems));
        Assert.Equal(RunTestSupport.StateOf(run.Snapshot(), replay.Graph), RunTestSupport.StatusOf(replay.Graph));
        Assert.Equal("no", replay.Graph.TakenBranch(new TaskId("C1")));
    }
}
