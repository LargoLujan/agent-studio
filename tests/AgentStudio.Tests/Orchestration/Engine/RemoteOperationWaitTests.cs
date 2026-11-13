using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Orchestration.Planning;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// docs/21 D-25 and F10-6 (P0 of Phase 10), the site of the orchestrator (<c>Orchestrator.Enter</c>: planning, start and resume): when what holds the project is a
/// remote operation of Agent Studio it waits for it (it has written nothing yet, so nothing is lost); with a debate or an audit in the way it refuses at once, with the
/// same error as ever; and past the cap it refuses with <c>project.busy-remote</c>, still having written nothing. The clock is the one of the harness, moved by the test.
/// </summary>
public sealed class RemoteOperationWaitTests
{
    private const string ProjectId = "p1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ValidPlan() =>
        PlanJson.Plan(
            PlanJson.Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"], steps: ["build", "test"]),
            PlanJson.Task("I1", kind: "Integration", agent: null, workspace: "None", dependsOn: [PlanJson.Dependency("T1")], steps: ["build", "test"]),
            PlanJson.Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [PlanJson.Dependency("I1")])).ToJsonString();

    [Fact]
    public async Task A_start_waits_for_a_remote_operation_and_then_runs_without_losing_anything()
    {
        using var harness = new EngineHarness();
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);
        var remote = activity.TryEnter(ProjectId, ProjectActivityKind.RemoteOperation)!;

        var starting = orchestrator.StartAsync(harness.Request(Plan(Work("T1"))), Ct);
        Assert.False(starting.IsCompleted);
        Assert.Empty(harness.Entries);
        Assert.Empty(await harness.Runs.ListAsync(ProjectId, Ct));

        remote.Dispose();
        await RemoteWaitClock.AdvanceUntilAsync(harness.Time, starting, ProjectActivityEntry.RemoteWaitPoll, () => activity.Current(ProjectId) == ProjectActivityKind.Run);
        await using var run = await starting;

        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);
        Assert.Contains(harness.Entries, e => e.Kind == AgentStudio.Domain.Decisions.DecisionKind.RunStarted);
    }

    [Fact]
    public async Task A_resume_waits_for_a_remote_operation_and_then_resumes_the_run()
    {
        using var harness = new EngineHarness();
        var stored = new RunId("20261019-c0ffee");
        await harness.StoreStartedRunAsync(stored, Plan(Work("T1")));
        var entries = harness.Entries.Count;
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);
        var remote = activity.TryEnter(ProjectId, ProjectActivityKind.RemoteOperation)!;

        var resuming = orchestrator.ResumeAsync(harness.Project, stored, Ct);
        Assert.False(resuming.IsCompleted);
        Assert.Equal(entries, harness.Entries.Count);

        remote.Dispose();
        await RemoteWaitClock.AdvanceUntilAsync(harness.Time, resuming, ProjectActivityEntry.RemoteWaitPoll, () => activity.Current(ProjectId) == ProjectActivityKind.Run);
        await using var resumed = await resuming;

        Assert.Equal(RunPhase.Succeeded, (await resumed.CompleteAsync(harness)).Phase);
    }

    [Fact]
    public async Task A_planning_waits_for_a_remote_operation_and_then_plans()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = ValidPlan() });
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);
        var remote = activity.TryEnter(ProjectId, ProjectActivityKind.RemoteOperation)!;

        var planning = orchestrator.PlanAsync(harness.PlanningRequest(), null, Ct);
        Assert.False(planning.IsCompleted);
        Assert.Empty(harness.Claude.Starts);

        remote.Dispose();
        await RemoteWaitClock.AdvanceUntilAsync(harness.Time, planning, ProjectActivityEntry.RemoteWaitPoll, () => activity.Current(ProjectId) == ProjectActivityKind.Planning);

        Assert.NotNull(await planning);
        Assert.Single(harness.Claude.Starts);
        Assert.False(activity.IsBusy(ProjectId));
    }

    [Theory]
    [InlineData(ProjectActivityKind.Debate)]
    [InlineData(ProjectActivityKind.Audit)]
    public async Task With_a_debate_or_an_audit_in_the_way_each_of_them_is_refused_at_once_with_the_error_of_always(ProjectActivityKind holder)
    {
        using var harness = new EngineHarness();
        var stored = new RunId("20261019-c0ffee");
        await harness.StoreStartedRunAsync(stored, Plan(Work("T1")));
        var entries = harness.Entries.Count;
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);
        using var held = activity.TryEnter(ProjectId, holder);

        var planning = orchestrator.PlanAsync(harness.PlanningRequest(), null, Ct);
        var starting = orchestrator.StartAsync(harness.Request(Plan(Work("T1"))), Ct);
        var resuming = orchestrator.ResumeAsync(harness.Project, stored, Ct);

        // Nothing moved the clock: a caller that waited would still be waiting.
        foreach (var refusal in new Task[] { planning, starting, resuming })
        {
            Assert.True(refusal.IsFaulted, "It must refuse without waiting for the clock.");
            var error = Assert.IsType<InvalidOperationException>(refusal.Exception!.GetBaseException());
            Assert.IsNotType<UserTextException>(error);
            Assert.Contains($"is held by {holder} in this process", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(entries, harness.Entries.Count);
        Assert.Empty(harness.Claude.Starts);
        Assert.Equal(holder, activity.Current(ProjectId));
    }

    [Fact]
    public async Task A_remote_operation_that_never_lets_go_refuses_a_start_with_its_code_after_the_cap_having_written_nothing()
    {
        using var harness = new EngineHarness();
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);
        using var remote = activity.TryEnter(ProjectId, ProjectActivityKind.RemoteOperation);
        var started = harness.Time.GetUtcNow();

        var starting = orchestrator.StartAsync(harness.Request(Plan(Work("T1"))), Ct);
        await RemoteWaitClock.AdvanceUntilAsync(harness.Time, starting, ProjectActivityEntry.RemoteWaitPoll, () => activity.Current(ProjectId) == ProjectActivityKind.Run);
        var refused = await Assert.ThrowsAsync<UserTextException>(() => starting);

        Assert.Equal(ProjectActivityEntry.BusyRemoteCode, refused.Text.Code);
        Assert.True(harness.Time.GetUtcNow() - started >= ProjectActivityEntry.RemoteWaitCap);
        Assert.Empty(harness.Entries);
        Assert.Empty(await harness.Runs.ListAsync(ProjectId, Ct));
        Assert.Empty(harness.Worktrees.Calls);
        Assert.Empty(harness.Claude.Starts);
        Assert.Equal(ProjectActivityKind.RemoteOperation, activity.Current(ProjectId));
    }

    [Fact]
    public async Task The_hold_of_the_orchestrator_itself_never_waits_because_runs_share_the_project()
    {
        using var harness = new EngineHarness();
        var activity = new OrchestrationActivity();
        var orchestrator = harness.WithActivity(activity);

        await using var run = await orchestrator.StartAsync(harness.Request(Plan(Work("T1"))), Ct);

        Assert.Equal(ProjectActivityKind.Run, activity.Current(ProjectId));
        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);
    }
}
