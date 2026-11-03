using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// What the live runs of 2026-10-29 found in the normal flow «pedir plan → aprobar → ejecutar → entregar»: a task that names
/// only the test step of a profile whose test does not build (B6), a plan that can run but never be delivered (B10), and a
/// notice of uncommitted changes in a clean repository, for the files Agent Studio itself keeps there (V21).
/// </summary>
public sealed class StepRequirementsAndStartNoticesTests
{
    private const string Uncommitted = "Tienes cambios sin confirmar";

    private const string Undeliverable = "no se podrá entregar a tu rama";

    /// <summary>The fixtures' profile where <paramref name="step"/> requires the build, as «test» does in the proposed «dotnet».</summary>
    private static VerificationProfile Requiring(string step) =>
        Profile() with { Steps = [.. Profile().Steps.Select(s => s.Id == step ? s with { Requires = ["build"] } : s)] };

    /// <summary>A plan that can be delivered: T1 writes, I1 integrates it and the judge reviews I1.</summary>
    private static PlanDefinition Deliverable() =>
        Plan(Work("T1"), Node("I1", WorkTaskKind.Integration).After(On("T1")), Node("R1", WorkTaskKind.Review).After(On("I1")));

    private static async Task<(RunResult Result, IReadOnlyList<string> Notices)> RunAsync(EngineHarness harness, PlanDefinition plan, VerificationProfile? profile = null)
    {
        await using var run = await harness.CreateOrchestrator().StartAsync(harness.Request(plan, profile: profile), TestContext.Current.CancellationToken);
        var result = await run.CompleteAsync(harness);

        // The events of a run are kept until read, and end with the run: the notices it started with come first.
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(RunTestSupport.Guard);
        var notices = new List<string>();
        await foreach (var e in run.Events.WithCancellation(guard.Token))
        {
            if (e is RunPhaseChangedEvent { Phase: RunPhase.Running, Detail: { } detail })
            {
                notices.Add(detail);
            }
        }

        return (result, notices);
    }

    // ---- B6: the steps a named step requires run first ------------------------------------------------------------------

    [Fact]
    public async Task A_writer_that_names_only_the_test_gets_the_build_its_test_requires_first()
    {
        // Run 20261029-ff3333: the plan named only «test» for the writer, and «dotnet test --no-build» found nothing built.
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var plan = Plan(Work("T1").Verified("test"), Node("I1", WorkTaskKind.Integration).After(On("T1")) with { VerificationSteps = ["test"] });

        var (result, _) = await RunAsync(harness, plan, Requiring("test"));

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(["build", "test"], harness.Verification.Calls.Single(c => c.Scope.Task == new TaskId("T1")).Steps);
        Assert.Equal(["build", "test"], Assert.Single(harness.Integrator.Requests).Verification.Select(s => s.Id));
    }

    [Fact]
    public async Task A_step_named_with_the_one_it_requires_runs_once_after_it()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));

        await RunAsync(harness, Plan(Work("T1").Verified("test", "build")), Requiring("test"));

        Assert.Equal(["build", "test"], Assert.Single(harness.Verification.Calls).Steps);
    }

    [Fact]
    public async Task A_tool_run_of_a_step_that_requires_the_build_builds_first_and_fails_when_the_build_does()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var plan = Plan(Work("T1"), Node("L1", WorkTaskKind.ToolRun).After(On("T1")));

        var (passed, _) = await RunAsync(harness, plan, Requiring("lint"));

        Assert.Equal(RunPhase.Succeeded, passed.Phase);
        Assert.Equal(["build", "lint"], harness.Verification.Calls.Single(c => c.Scope.Task == new TaskId("L1")).Steps);

        using var failing = new EngineHarness();
        failing.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        failing.Verification.Script = (scope, step) => scope.Task == new TaskId("L1") && step.Id == "build" ? StepResult.Fail : null;

        var (failed, _) = await RunAsync(failing, plan, Requiring("lint"));

        Assert.Equal(RunPhase.Failed, failed.Phase);
        Assert.All(failing.Verification.Calls.Where(c => c.Scope.Task == new TaskId("L1")), c => Assert.Equal(["build", "lint"], c.Steps));

        // The failure names the step that failed, the build lint requires, not lint.
        var reasons = failing.Payloads<TaskStatusChangedPayload>().Where(p => p.Payload.Task == new TaskId("L1") && p.Payload.To == WorkTaskStatus.Failed).Select(p => p.Payload.Reason).ToList();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.StartsWith("El paso build, que lint requiere, falló", r, StringComparison.Ordinal));
    }

    // ---- B10: a plan that could never be delivered says so when it starts -----------------------------------------------

    [Fact]
    public async Task A_plan_whose_work_no_integration_reviewed_by_the_judge_merges_says_it_cannot_be_delivered()
    {
        // Run 20261029-3f1cbc: A writes, B (the judge) reviews A; Approved, and «Entregar a mi rama» stayed off.
        using var harness = new EngineHarness();

        var (result, notices) = await RunAsync(harness, Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1"))));

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Null(result.IntegrationCommit);
        var notice = Assert.Single(notices, n => n.Contains(Undeliverable, StringComparison.Ordinal));
        Assert.Contains("T1", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_that_does_not_wait_for_the_last_integration_never_gets_its_judge_unseen_integration_delivered()
    {
        // T1 → I1 → R, and T2 → I2 after I1 too: R does not wait for I2. The judge is shown I1 (its dossier is built when the
        // review starts); I2 lands while it reviews, so its verdict comes after I2 and the run ends with I2's commit.
        using var harness = new EngineHarness();
        var writer2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var judge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shownToTheJudge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        harness.Claude.Script("ejecutor", "T2", 1, AgentScript.Completed(("src/T2/b.cs", "b")) with { Gate = writer2 });
        harness.Review.Gate = judge;
        harness.Review.OnReview = _ => shownToTheJudge.TrySetResult(); // after the review took the head of the log
        var plan = Plan(
            Work("T1"),
            Work("T2"),
            Node("I1", WorkTaskKind.Integration).After(On("T1")),
            Node("R", WorkTaskKind.Review).After(On("I1")),
            Node("I2", WorkTaskKind.Integration).After(On("I1"), On("T2")));

        // Statically, B10 already says so: R's worktree starts on I1 (its code frontier), never on I2, whatever finishes first.
        Assert.Equal(
            [(PlanWarningCode.NotDeliverable, "T1", false), (PlanWarningCode.NotDeliverable, "T2", false)],
            PlanValidator.Warnings(plan).Select(w => (w.Code, w.Task?.Value, w.DependsOnTiming)));

        await using var run = await harness.CreateOrchestrator().StartAsync(harness.Request(plan), TestContext.Current.CancellationToken);
        await shownToTheJudge.Task.WaitAsync(RunTestSupport.Guard, TestContext.Current.CancellationToken);
        writer2.SetResult();
        await harness.WaitForStatusAsync(run, "I2", WorkTaskStatus.Completed);
        judge.SetResult();
        var result = await run.CompleteAsync(harness);

        var integrations = harness.Payloads<IntegrationPerformedPayload>().Select(p => p.Payload).Where(p => p.ResultCommit is not null).ToList();
        var shown = integrations.Single(p => p.Task == new TaskId("I1")).ResultCommit!;
        var last = integrations.Single(p => p.Task == new TaskId("I2")).ResultCommit!;
        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(last, result.IntegrationCommit);
        var verdictAt = harness.Entries.Single(e => e.Kind == AgentStudio.Domain.Decisions.DecisionKind.VerdictIssued).Sequence;
        var lastAt = harness.Payloads<IntegrationPerformedPayload>().Single(p => p.Payload.Task == new TaskId("I2")).Entry.Sequence;
        Assert.True(lastAt < verdictAt, "I2 must land before the verdict for the order of the log to let it through.");

        var readiness = await DeliveryReadinessAsync(harness, run.Id, [.. harness.Payloads<VerdictIssuedPayload>().Select(p => p.Payload)]);

        Assert.False(readiness.Ready);
        Assert.Contains(shown[..10], readiness.Reason, StringComparison.Ordinal);
        Assert.Contains(last[..10], readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Of_two_reviews_the_one_that_finishes_last_gives_the_run_its_verdict_and_one_shown_an_earlier_integration_keeps_it_from_delivery()
    {
        // T1 → I1 → R1, and T2 → I2 after I1 too, with R2 after I2: R2 starts from I2 and is shown it, but R1 does not wait
        // for it and, finishing last, gives the run its verdict (the engine keeps the last one). R1 was shown I1.
        using var harness = new EngineHarness();
        var writer2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstJudge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shownToTheFirstJudge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        harness.Claude.Script("ejecutor", "T2", 1, AgentScript.Completed(("src/T2/b.cs", "b")) with { Gate = writer2 });
        harness.Review.GateFor = r => r.ReviewTask == new TaskId("R1") ? firstJudge.Task : null;
        harness.Review.OnReview = r =>
        {
            if (r.ReviewTask == new TaskId("R1"))
            {
                shownToTheFirstJudge.TrySetResult();
            }
        };
        var plan = Plan(
            Work("T1"),
            Work("T2"),
            Node("I1", WorkTaskKind.Integration).After(On("T1")),
            Node("R1", WorkTaskKind.Review).After(On("I1")),
            Node("I2", WorkTaskKind.Integration).After(On("I1"), On("T2")),
            Node("R2", WorkTaskKind.Review).After(On("I2")));

        // B10: R2 starts from the last integration, R1 does not, and either can finish last.
        Assert.Equal(
            [(PlanWarningCode.MayNotBeDeliverable, "T1", true), (PlanWarningCode.MayNotBeDeliverable, "T2", true)],
            PlanValidator.Warnings(plan).Select(w => (w.Code, w.Task?.Value, w.DependsOnTiming)));

        await using var run = await harness.CreateOrchestrator().StartAsync(harness.Request(plan), TestContext.Current.CancellationToken);
        await shownToTheFirstJudge.Task.WaitAsync(RunTestSupport.Guard, TestContext.Current.CancellationToken);
        writer2.SetResult();
        await harness.WaitForStatusAsync(run, "R2", WorkTaskStatus.Completed);
        firstJudge.SetResult();
        var result = await run.CompleteAsync(harness);

        var integrations = harness.Payloads<IntegrationPerformedPayload>().Select(p => p.Payload).Where(p => p.ResultCommit is not null).ToList();
        var first = integrations.Single(p => p.Task == new TaskId("I1")).ResultCommit!;
        var last = integrations.Single(p => p.Task == new TaskId("I2")).ResultCommit!;
        var issued = harness.Payloads<VerdictIssuedPayload>().Select(p => p.Payload).ToList();
        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(last, result.IntegrationCommit);
        Assert.Equal(issued.Single(v => v.Task == new TaskId("R1")).VerdictId, result.VerdictId);
        Assert.Equal(
            [("R1", first), ("R2", last)],
            harness.Payloads<WorktreeCreatedPayload>().Select(p => p.Payload).Where(w => w.Task is { } t && t.Value.StartsWith('R')).Select(w => (w.Task!.Value.Value, w.BaseCommit)).OrderBy(w => w.Item1, StringComparer.Ordinal));

        var readiness = await DeliveryReadinessAsync(harness, run.Id, issued);

        Assert.False(readiness.Ready);
        Assert.Contains("R1", readiness.Reason, StringComparison.Ordinal);
        Assert.Contains(first[..10], readiness.Reason, StringComparison.Ordinal);
        Assert.Contains(last[..10], readiness.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ningún juez", readiness.Reason, StringComparison.Ordinal);
    }

    /// <summary>What <c>DeliveryService</c> says of the run, with the verdicts the review announced kept as the real review keeps them.</summary>
    private static async Task<AgentStudio.Application.Git.DeliveryReadiness> DeliveryReadinessAsync(EngineHarness harness, RunId run, IReadOnlyList<VerdictIssuedPayload> issued)
    {
        var verdicts = new AgentStudio.Tests.Debate.Fakes.FakeVerdictStore();
        foreach (var verdict in issued)
        {
            await verdicts.AppendAsync(
                harness.Project.ProjectId,
                new AgentStudio.Application.Persistence.StoredVerdict(verdict.VerdictId, Integration.DeliveryRecords.Verdict(verdict.Decision), run.Value, verdict.Task?.Value, AgentStudio.Application.Judging.AuditScope.CurrentPlan, verdict.Valid, [], verdict.Supersedes),
                TestContext.Current.CancellationToken);
        }

        return await new AgentStudio.Orchestration.Integration.DeliveryService(harness.Worktrees, harness.Approvals, harness.Decisions, verdicts)
            .CheckAsync(harness.Project, run, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_plan_that_integrates_and_then_reviews_starts_without_that_notice()
    {
        using var harness = new EngineHarness();

        var (result, notices) = await RunAsync(harness, Deliverable());

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.NotNull(result.IntegrationCommit);
        Assert.NotNull(result.VerdictId);
        Assert.DoesNotContain(notices, n => n.Contains(Undeliverable, StringComparison.Ordinal));
    }

    // ---- V21: the notice of uncommitted changes is about the user's own changes -----------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_notice_of_uncommitted_changes_follows_what_the_repository_says_of_the_users_own_changes(bool dirty)
    {
        // Which files are the user's is the repository's answer (RepositoryState.HasUncommittedChanges leaves out what Agent
        // Studio keeps in .agentstudio/: AgentStudioFilesTests and GitWorktreeLifecycleTests); the run says what it heard.
        using var harness = new EngineHarness();
        harness.Worktrees.HasUncommittedChanges = dirty;

        var (result, notices) = await RunAsync(harness, Deliverable());

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(dirty ? 1 : 0, notices.Count(n => n.StartsWith(Uncommitted, StringComparison.Ordinal)));
    }
}
