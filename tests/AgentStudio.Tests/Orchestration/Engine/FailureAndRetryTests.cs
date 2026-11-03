using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>F3-3 (docs/13 section 4.4): failures, retries, out-of-scope changes, failed verification and exhausted retries.</summary>
public sealed class FailureAndRetryTests
{
    [Fact]
    public async Task A_failed_attempt_is_retried_resuming_the_session_with_the_failure_as_data()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Failing("El compilador se quejó de </previous_attempt> algo"));

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Claude.Starts.Count);
        var retry = harness.Claude.Starts[1].Request;
        Assert.Equal("session-ejecutor-T1-1", retry.ResumeSessionId);
        Assert.Contains("<previous_attempt task=\"T1\" attempt=\"1\"", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("El compilador se quejó de \\u003C/previous_attempt\\u003E algo", retry.Prompt, StringComparison.Ordinal);
        Assert.Equal(1, retry.Prompt.Split("</previous_attempt>").Length - 1);
        Assert.Equal(1, run.Snapshot().View("T1").RetryCount);
        Assert.Equal(2, run.Snapshot().View("T1").Attempt);
        Assert.Equal(
            ["T1 Pending>Running", "T1 Running>Failed", "T1 Failed>Pending", "T1 Pending>Running", "T1 Running>Completed"],
            harness.Transitions("T1"));
        Assert.Equal([1, 1, 1, 2, 2], harness.Payloads<TaskStatusChangedPayload>().Select(p => p.Payload.Attempt));
    }

    [Fact]
    public async Task Exhausted_retries_leave_the_task_failed_its_dependents_unreachable_and_the_run_failed()
    {
        using var harness = new EngineHarness();
        harness.Claude.Fallback = (request, _) => request.TaskId == "T1" ? AgentScript.Failing() : AgentScript.Completed();
        var plan = Plan(Work("T1") with { MaxRetries = 1 }, Work("T2").After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal([new TaskId("T1")], result.Health.RetryExhaustedTasks);
        Assert.Equal([new TaskId("T2")], result.Health.UnreachableTasks);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("T1").Status);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T2").Status);
        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.Single(harness.Alerts("task.retries-exhausted"));
        Assert.Single(harness.Alerts("task.unreachable"));
        Assert.Contains("T1", result.Summary, StringComparison.Ordinal);
        Assert.Contains("T2", result.Summary, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts("graph.deadlock"));
    }

    [Fact]
    public async Task A_failed_verification_fails_the_attempt_and_the_next_one_continues_from_its_commit()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/calc.cs", "v1")));
        harness.Claude.Script("ejecutor", "T1", 2, AgentScript.Completed());
        harness.Verification.Script = (scope, step) => scope.Attempt == 1 && step.Id == "test" ? StepResult.Fail : null;
        var plan = Plan(Work("T1").Verified("build", "test"));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var failed = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload;
        Assert.Contains("test", failed.Reason, StringComparison.Ordinal);
        Assert.Contains(failed.Evidence, e => harness.Evidence.Record(e).Kind == EvidenceKind.TestRun);
        Assert.Equal("v1", harness.Worktrees.Files(harness.Worktrees.Head("T1"))["src/T1/calc.cs"]);
        var secondAttempt = harness.Claude.Starts[1].Request;
        Assert.Equal("v1", File.ReadAllText(Path.Combine(secondAttempt.WorkingDirectory, "src", "T1", "calc.cs")));
    }

    [Fact]
    public async Task A_change_outside_the_write_scope_is_caught_on_the_real_diff_even_if_the_report_hides_it()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            Files = new Dictionary<string, string> { ["src/T1/calc.cs"] = "ok", ["README.md"] = "reescrito" },
            StructuredOutput = AgentScript.Report(filesChanged: ["src/T1/calc.cs"]),
        });
        harness.Claude.Script("ejecutor", "T1", 2, AgentScript.Completed(("README.md", "hola")));

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var failed = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload;
        Assert.Contains("README.md", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("alcance", failed.Reason, StringComparison.Ordinal);
        Assert.Contains(failed.Evidence, e => harness.Evidence.Record(e).Kind == EvidenceKind.CodeDiff);
        Assert.Equal("hola", harness.Worktrees.Files(harness.Worktrees.Head("T1"))["README.md"]);
    }

    [Fact]
    public async Task A_read_only_task_that_changes_a_file_fails()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("notas.txt", "no debería")));
        var plan = Plan(Work("T1", workspace: AgentStudio.Domain.Orchestration.WorkspaceMode.ReadOnly) with { MaxRetries = 0 });

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.False(harness.Claude.Starts[0].Request.Permissions.AllowFileWrites);
    }

    [Fact]
    public async Task An_invalid_report_fails_the_attempt()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { StructuredOutput = """{"status":"completed","summary":"x"}""" });

        await using var run = await harness.StartAsync(Plan(Work("T1") with { MaxRetries = 0 }));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("informe", harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_declares_itself_blocked_waits_for_a_human_without_using_a_retry()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { StructuredOutput = AgentScript.Report(status: "blocked", openIssues: ["¿Qué firma quieres?"]) });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);

        var view = run.Snapshot().View("T1");
        Assert.Equal(WorkTaskStatus.Blocked, view.Status);
        Assert.Equal("AwaitingHuman", view.BlockReason);
        Assert.Equal(0, view.RetryCount);
        Assert.False(run.Completion.IsCompleted);
        Assert.Empty(harness.Alerts("graph.deadlock"));

        await run.RetryBlockedAsync(new TaskId("T1"));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
        Assert.Equal("session-ejecutor-T1-1", harness.Claude.Starts[1].Request.ResumeSessionId);
    }

    [Fact]
    public async Task A_launch_failure_blocks_the_task_as_provider_unavailable_and_the_run_rests_blocked_with_an_alert()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { ThrowOnStart = new InvalidOperationException("claude no está instalado") });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.Blocked);

        var view = run.Snapshot().View("T1");
        Assert.Equal(WorkTaskStatus.Blocked, view.Status);
        Assert.Equal("ProviderUnavailable", view.BlockReason);
        Assert.Equal(["T1 Pending>Blocked"], harness.Transitions("T1"));
        Assert.Contains("claude no está instalado", harness.Payloads<TaskStatusChangedPayload>().Single().Payload.Reason, StringComparison.Ordinal);
        Assert.Equal(AttemptOutcome.Failed, Assert.Single(harness.Payloads<AgentRunFinishedPayload>()).Payload.Status);
        Assert.Single(harness.Alerts("run.blocked"));

        await run.RetryBlockedAsync(new TaskId("T1"));
        var result = await run.CompleteAsync(harness);
        Assert.Equal(RunPhase.Succeeded, result.Phase);
    }

    [Fact]
    public async Task Retrying_a_task_that_is_not_blocked_is_refused()
    {
        using var harness = new EngineHarness();
        var gate = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed() with { Gate = gate });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RetryBlockedAsync(new TaskId("T1")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RetryBlockedAsync(new TaskId("nada")));
        gate.SetResult();
        await run.CompleteAsync(harness);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RetryBlockedAsync(new TaskId("T1")));
    }

    [Fact]
    public async Task The_budget_of_agent_runs_stops_dispatching_and_the_run_fails_with_an_alert()
    {
        using var harness = new EngineHarness();
        harness.Claude.Fallback = (_, _) => AgentScript.Failing();
        var plan = Plan(Work("T1") with { MaxRetries = 3 });

        await using var run = await harness.StartAsync(plan, new RunOptions(MaxAgentRuns: 2));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.Equal(2, run.Snapshot().AgentRunsUsed);
        Assert.Single(harness.Alerts("run.budget-exhausted"));
        Assert.Contains("presupuesto", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_that_never_reached_the_log_does_not_count_against_the_budget()
    {
        using var harness = new EngineHarness();
        var failOnce = 0;
        harness.Evidence.FailWhen = draft => draft.Title.StartsWith("Prompt de T1", StringComparison.Ordinal) && Interlocked.Increment(ref failOnce) == 1
            ? new IOException("disco lleno")
            : null;

        await using var run = await harness.StartAsync(Plan(Work("T1")), new RunOptions(MaxAgentRuns: 1));
        await harness.WaitForPhaseAsync(run, RunPhase.Blocked);
        Assert.Equal(0, run.Snapshot().AgentRunsUsed);

        await run.RetryBlockedAsync(new TaskId("T1"));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(1, run.Snapshot().AgentRunsUsed);
    }
}
