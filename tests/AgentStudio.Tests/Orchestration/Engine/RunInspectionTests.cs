using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// A run that does not run in this process is read back to look at it (<see cref="IRunInspector"/>): the snapshot its log
/// leaves is the one the live run published, its history comes in log order with the reason of every transition and the
/// evidence it captured, its end is what <c>RunFinished</c> recorded, and nothing is launched or written.
/// </summary>
public sealed class RunInspectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<OrchestrationEvent>> ReadAllAsync(IOrchestrationRun run)
    {
        var events = new List<OrchestrationEvent>();
        await foreach (var item in run.Events.WithCancellation(Ct))
        {
            events.Add(item);
        }

        return events;
    }

    [Fact]
    public async Task A_finished_run_is_read_back_with_the_state_the_live_run_left_and_its_history_in_log_order()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/calc.cs", "v1")));
        harness.Claude.Script("ejecutor", "T1", 2, AgentScript.Completed());
        harness.Verification.Script = (scope, step) => scope.Attempt == 1 && step.Id == "test" ? StepResult.Fail : null;
        RunSnapshot live;
        RunResult finished;
        RunId id;
        await using (var run = await harness.StartAsync(Plan(Work("T1").Verified("build", "test"))))
        {
            finished = await run.CompleteAsync(harness);
            live = run.Snapshot();
            id = run.Id;
        }

        var written = harness.Entries.Count;
        await using var inspected = await harness.CreateOrchestrator().InspectAsync(harness.Project, id, Ct);
        var history = await ReadAllAsync(inspected);
        var result = await inspected.Completion;

        Assert.Equal(id, inspected.Id);
        Assert.Equal(RunPhase.Succeeded, inspected.Snapshot().Phase);
        Assert.Equal(live.AgentRunsUsed, inspected.Snapshot().AgentRunsUsed);
        var task = inspected.Snapshot().View("T1");
        var expected = live.View("T1");
        Assert.Equal(
            (expected.Status, expected.Attempt, expected.RetryCount, expected.Agent, expected.BlockReason),
            (task.Status, task.Attempt, task.RetryCount, task.Agent, task.BlockReason));
        Assert.Equal(expected.Evidence, task.Evidence);
        Assert.Equal((finished.Phase, finished.IntegrationCommit, finished.VerdictId, finished.Summary), (result.Phase, result.IntegrationCommit, result.VerdictId, result.Summary));

        // Every transition, in the order of the log and with the reason it was recorded with: the failed verification says
        // which step failed.
        var transitions = history.OfType<TaskStatusChangedEvent>().ToList();
        Assert.Equal(harness.Transitions(), transitions.Select(t => $"{t.Task} {t.From}>{t.To}"));
        Assert.Equal(harness.Payloads<TaskStatusChangedPayload>().Select(p => p.Payload.Reason), transitions.Select(t => t.Reason));
        Assert.Contains("La verificación falló en: test", transitions.Single(t => t.To == WorkTaskStatus.Failed).Reason, StringComparison.Ordinal);

        // Every evidence the log registered for the run, with its task, as the store lists it.
        var registered = harness.Payloads<EvidenceRegisteredPayload>().Where(p => p.Payload.RunId == id.Value).Select(p => (p.Payload.Task, p.Payload.Evidence)).ToList();
        Assert.Equal(registered, history.OfType<EvidenceCapturedEvent>().Select(e => (e.Task, e.Evidence.Id)));
        Assert.Contains(history.OfType<EvidenceCapturedEvent>(), e => e.Task == new TaskId("T1") && e.Evidence.Kind == EvidenceKind.TestRun);

        // Nothing runs: no command is taken, and reading wrote nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(inspected.PauseAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspected.RetryBlockedAsync(new TaskId("T1")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspected.CancelAsync("no"));
        Assert.Equal(written, harness.Entries.Count);
    }

    [Fact]
    public async Task A_run_cut_by_a_close_is_read_back_interrupted_its_pause_said_in_spanish_and_nothing_is_written()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.PauseAsync();
        await run.DisposeAsync();
        await run.CompleteAsync(harness);
        var written = harness.Entries.Count;

        await using var inspected = await harness.CreateOrchestrator().InspectAsync(harness.Project, run.Id, Ct);
        var history = await ReadAllAsync(inspected);
        var result = await inspected.Completion;

        Assert.Equal(RunPhase.Interrupted, result.Phase);
        Assert.Equal("Interrumpida: no terminó y no hay ningún proceso detrás. Se puede reanudar.", result.Summary);

        // What was running when the app stopped is shown interrupted, as a resume records it, after the history of the log.
        var task = inspected.Snapshot().View("T1");
        Assert.Equal((WorkTaskStatus.Blocked, "Interrupted"), (task.Status, task.BlockReason));
        var transitions = history.OfType<TaskStatusChangedEvent>().ToList();
        Assert.Equal(["T1 Pending>Running", "T1 Running>Blocked"], transitions.Select(t => $"{t.Task} {t.From}>{t.To}"));
        Assert.Equal("Interrumpida: la aplicación se cerró mientras se trabajaba en ella.", transitions[^1].Reason);
        Assert.Contains(history.OfType<RunPhaseChangedEvent>(), p => p is { Phase: RunPhase.Paused, Detail: "La pausaste tú." });
        Assert.Empty(result.Health.StalledTasks);
        Assert.Equal(written, harness.Entries.Count);
    }

    [Fact]
    public async Task The_deliveries_of_a_run_are_in_its_history_with_the_branch_they_went_to()
    {
        using var harness = new EngineHarness();
        RunId id;
        await using (var run = await harness.StartAsync(Plan(Work("T1"))))
        {
            await run.CompleteAsync(harness);
            id = run.Id;
        }

        await harness.Decisions.RecordAsync(
            harness.Project.ProjectId,
            new Actor(ActorKind.System, "agent-studio"),
            DecisionKind.IntegrationPerformed,
            "Entrega en main",
            new IntegrationPerformedPayload(null, "refs/heads/main", ["c0ffee"], "c0ffee0000000000000000000000000000000000", [], IsDelivery: true) { RunId = id.Value },
            cancellationToken: Ct);

        await using var inspected = await harness.CreateOrchestrator().InspectAsync(harness.Project, id, Ct);
        var history = await ReadAllAsync(inspected);

        var delivered = Assert.Single(history.OfType<RunDeliveredEvent>());
        Assert.Equal(("refs/heads/main", "c0ffee0000000000000000000000000000000000"), (delivered.TargetRef, delivered.Commit));
    }

    [Fact]
    public async Task A_log_that_names_a_task_out_of_the_plan_is_shown_up_to_its_problem_and_never_refused_for_it()
    {
        using var harness = new EngineHarness();
        RunId id;
        await using (var run = await harness.StartAsync(Plan(Work("T1"))))
        {
            await run.CompleteAsync(harness);
            id = run.Id;
        }

        await harness.Decisions.RecordAsync(
            harness.Project.ProjectId,
            new Actor(ActorKind.System, "agent-studio"),
            DecisionKind.TaskStatusChanged,
            "ZZ: Pending → Running",
            new TaskStatusChangedPayload(new TaskId("ZZ"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "?", null, [], 1) { RunId = id.Value },
            cancellationToken: Ct);

        await using var inspected = await harness.CreateOrchestrator().InspectAsync(harness.Project, id, Ct);
        var history = await ReadAllAsync(inspected);

        Assert.Equal(WorkTaskStatus.Completed, inspected.Snapshot().View("T1").Status);
        Assert.Contains("no se puede reproducir entero", Assert.IsType<RunPhaseChangedEvent>(history[^1]).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_runs_in_this_process_is_not_inspected_its_monitor_shows_it()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var orchestrator = harness.CreateOrchestrator();
        var run = await orchestrator.StartAsync(harness.Request(Plan(Work("T1"))), Ct);
        try
        {
            await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.InspectAsync(harness.Project, run.Id, Ct));
            Assert.Contains("running in this process", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            await run.DisposeAsync();
            await run.CompleteAsync(harness);
        }
    }

    [Fact]
    public async Task A_run_that_is_not_registered_is_refused()
    {
        using var harness = new EngineHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateOrchestrator().InspectAsync(harness.Project, new RunId("20261015-000000"), Ct));
    }
}
