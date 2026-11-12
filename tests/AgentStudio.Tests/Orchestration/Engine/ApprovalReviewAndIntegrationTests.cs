using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>docs/13 section 4.2: HumanApproval, Review, Integration and ToolRun nodes.</summary>
public sealed class ApprovalReviewAndIntegrationTests
{
    /// <summary>The F3-8 plan: T1 writes and is tested, I1 integrates, R1 is reviewed by the judge.</summary>
    private static PlanDefinition F38Plan() => Plan(
        Work("T1", scope: ["src/Calc/", "tests/Calc.Tests/"]).Verified("build", "test"),
        Node("I1", WorkTaskKind.Integration).After(On("T1")) with { Covers = [], MaxRetries = 0 },
        Node("R1", WorkTaskKind.Review).After(On("I1")));

    /// <summary>The executor asks for system-captured tests and an independent review, as in the roster a project gets by default.</summary>
    private static async Task RequireReviewAsync(EngineHarness harness)
    {
        var executor = Roster().Single(a => a.Id == new AgentId("ejecutor"));
        await harness.Roster.SaveAsync(harness.Project.ProjectId, executor with { Review = new ReviewPolicy(true, true) }, TestContext.Current.CancellationToken);
    }

    /// <summary>What every warning of a plan says a writer at medium integrity means for the run (ADR-0018, decision 3).</summary>
    private const string WarningMeaning = " Pueden escribir en cualquier sitio donde puedas escribir tú, y lo que hagan queda fuera del modelo de amenaza (ADR-0018). Por eso la comprobación de tu repositorio será estricta: una carpeta ignorada que no se pueda verificar bloquea la ejecución.";

    [Fact]
    public async Task The_approval_request_of_the_plan_warns_about_the_steps_that_run_without_isolation()
    {
        using var harness = new EngineHarness();
        var steps = Profile().Steps;
        var profile = Profile() with { Steps = [steps[0] with { Isolation = ProcessIsolation.None }, .. steps.Skip(1)] };
        var plan = Plan(Work("T1").Verified("build"));

        await using var run = await harness.CreateOrchestrator().StartAsync(harness.Request(plan, profile: profile), TestContext.Current.CancellationToken);
        await run.CompleteAsync(harness);

        var asked = Assert.Single(harness.Payloads<ApprovalRequestedPayload>(), p => p.Payload.Action == "plan.approve").Payload;
        Assert.Equal(
            $"Ejecutar el plan {plan.ContentHash[..12]} (1 tareas). Atención: este plan tiene escritores con integridad Medium. T1: el paso «build» se ejecuta sin aislamiento." + WarningMeaning,
            asked.Description);
    }

    /// <summary>A-04: the approval request named only the steps without isolation; an executor given a shell is a writer at medium integrity too.</summary>
    [Fact]
    public async Task The_approval_request_of_the_plan_names_the_task_whose_executor_has_Bash()
    {
        using var harness = new EngineHarness();
        await harness.Roster.SaveAsync(harness.Project.ProjectId, Agent("ejecutorShell", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Bash"]), TestContext.Current.CancellationToken);
        var plan = Plan(Work("T1"), Work("T2", agent: "ejecutorShell"));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var asked = Assert.Single(harness.Payloads<ApprovalRequestedPayload>(), p => p.Payload.Action == "plan.approve").Payload;
        Assert.Equal(
            $"Ejecutar el plan {plan.ContentHash[..12]} (2 tareas). Atención: este plan tiene escritores con integridad Medium. T2: el agente «ejecutorShell» tiene Bash." + WarningMeaning,
            asked.Description);
    }

    /// <summary>The engine asks the installed Claude Code when the run starts: one whose confinement was never measured makes its writing executor a writer at medium integrity.</summary>
    [Fact]
    public async Task The_approval_request_of_a_run_on_a_Claude_Code_never_measured_names_its_writing_executor()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var plan = Plan(Work("T1"), Work("T2", workspace: WorkspaceMode.ReadOnly));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var asked = Assert.Single(harness.Payloads<ApprovalRequestedPayload>(), p => p.Payload.Action == "plan.approve").Payload;
        Assert.Equal(
            $"Ejecutar el plan {plan.ContentHash[..12]} (2 tareas). Atención: este plan tiene escritores con integridad Medium. T1: el agente «ejecutor» escribe con una versión de Claude Code cuyo confinamiento no se ha medido." + WarningMeaning,
            asked.Description);
    }

    [Fact]
    public async Task The_approval_request_of_a_plan_whose_profile_isolates_every_step_carries_no_warning()
    {
        using var harness = new EngineHarness();
        var plan = Plan(Work("T1"));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var asked = Assert.Single(harness.Payloads<ApprovalRequestedPayload>(), p => p.Payload.Action == "plan.approve").Payload;
        Assert.Equal($"Ejecutar el plan {plan.ContentHash[..12]} (1 tareas)", asked.Description);
    }

    [Fact]
    public async Task A_run_whose_required_review_was_skipped_by_a_condition_fails_instead_of_succeeding()
    {
        using var harness = new EngineHarness();
        await RequireReviewAsync(harness);
        var plan = Plan(
            Work("T1").Verified("build", "test"),
            Node("C1", WorkTaskKind.Condition).After(On("T1")) with { Condition = new TaskSucceeded(new TaskId("T1")) },
            Node("R1", WorkTaskKind.Review).After(On("C1", "no")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Skipped, run.Snapshot().View("R1").Status);
        Assert.Contains("revisión independiente aprobada de T1", result.Summary, StringComparison.Ordinal);
        var (_, alert) = Assert.Single(harness.Alerts("run.review-required"));
        Assert.Equal([new TaskId("T1")], alert.Tasks);
        Assert.Empty(harness.Review.Requests);
    }

    [Theory]
    [InlineData(ReviewDisposition.Approved)]
    [InlineData(ReviewDisposition.NeedsHuman)]
    public async Task A_run_whose_required_review_was_approved_by_the_judge_or_by_the_person_it_escalated_to_succeeds(ReviewDisposition disposition)
    {
        using var harness = new EngineHarness();
        await RequireReviewAsync(harness);
        harness.Review.Script = _ => disposition;
        var plan = Plan(Work("T1").Verified("build", "test"), Node("R1", WorkTaskKind.Review).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Empty(harness.Alerts("run.review-required"));
    }

    [Fact]
    public async Task A_plan_that_breaks_the_review_policy_is_refused_before_anything_runs()
    {
        using var harness = new EngineHarness();
        await RequireReviewAsync(harness);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.StartAsync(Plan(Work("T1"))));

        Assert.Contains(nameof(PlanViolationCode.VerificationRequiredByPolicy), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PlanViolationCode.ReviewRequiredByPolicy), error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Claude.Starts);
    }

    /// <summary>The executor asks for system-captured tests only (no independent review), so a plan needs no review task.</summary>
    private static async Task RequireTestsAsync(EngineHarness harness)
    {
        var executor = Roster().Single(a => a.Id == new AgentId("ejecutor"));
        await harness.Roster.SaveAsync(harness.Project.ProjectId, executor with { Review = new ReviewPolicy(false, true) }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_plan_whose_writer_asks_for_tests_but_only_builds_is_refused_before_anything_runs()
    {
        using var harness = new EngineHarness();
        await RequireTestsAsync(harness);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.StartAsync(Plan(Work("T1").Verified("build"))));

        Assert.Contains(nameof(PlanViolationCode.TestStepRequiredByPolicy), error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Claude.Starts);
    }

    [Fact]
    public async Task A_run_whose_final_integrated_commit_was_only_built_fails_with_an_alert_when_a_policy_asks_for_tests()
    {
        using var harness = new EngineHarness();
        await RequireTestsAsync(harness);
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/calc.cs", "Multiply")));
        var plan = Plan(
            Work("T1").Verified("build", "test"),
            Node("I1", WorkTaskKind.Integration).After(On("T1")) with { Covers = [], VerificationSteps = ["build"] });

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Completed, run.Snapshot().View("I1").Status);
        var commit = Assert.IsType<string>(result.IntegrationCommit);
        Assert.Contains("pruebas", result.Summary, StringComparison.Ordinal);
        Assert.Contains(commit, result.Summary, StringComparison.Ordinal);
        Assert.Equal(OrchestrationUserTexts.VerdictTestsRequired, Assert.IsType<UserText>(result.SummaryText).Code);
        Assert.Equal(result.SummaryText, LoggedTexts.RunSummary(result.Summary));
        var (_, alert) = Assert.Single(harness.Alerts("run.tests-required"));
        Assert.Contains(commit, alert.Message, StringComparison.Ordinal);
        Assert.Equal(RunPhase.Failed, Assert.Single(harness.Payloads<RunFinishedPayload>()).Payload.Phase);
    }

    [Fact]
    public async Task A_writer_after_the_integration_holds_the_final_commit_and_its_build_alone_does_not_satisfy_a_policy_that_asks_for_tests()
    {
        using var harness = new EngineHarness();
        await RequireTestsAsync(harness);
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/calc.cs", "Multiply")));
        harness.Claude.Script("ejecutor2", "T3", 1, AgentScript.Completed(("src/T3/doc.cs", "Docs")));
        var plan = Plan(
            Work("T1").Verified("build", "test"),
            Node("I1", WorkTaskKind.Integration).After(On("T1")) with { Covers = [] },
            Work("T3", agent: "ejecutor2").Verified("build").After(On("I1")) with { Covers = [] });

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        var (_, alert) = Assert.Single(harness.Alerts("run.tests-required"));
        Assert.Contains(harness.Worktrees.Head("T3"), alert.Message, StringComparison.Ordinal);
        Assert.Equal([new TaskId("T3")], alert.Tasks);
        Assert.Equal(UserText.Of(OrchestrationUserTexts.VerdictTestsRequired, UserText.Of(OrchestrationUserTexts.VerdictUntestedCommit, "T3", harness.Worktrees.Head("T3"))), result.SummaryText);
        Assert.Equal(result.SummaryText, LoggedTexts.RunSummary(result.Summary));
    }

    [Fact]
    public async Task A_run_whose_final_integrated_commit_passed_its_tests_succeeds_when_a_policy_asks_for_tests()
    {
        using var harness = new EngineHarness();
        await RequireTestsAsync(harness);
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/calc.cs", "Multiply")));
        var plan = Plan(
            Work("T1").Verified("build", "test"),
            Node("I1", WorkTaskKind.Integration).After(On("T1")) with { Covers = [] });

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Empty(harness.Alerts("run.tests-required"));
    }

    [Fact]
    public async Task A_human_approval_waits_for_the_gate_and_lets_the_dependents_run_when_approved()
    {
        using var harness = new EngineHarness();
        var hold = new TaskCompletionSource();
        harness.Approvals.Hold = hold;
        var plan = Plan(Work("T1"), Node("A1", WorkTaskKind.HumanApproval).After(On("T1")), Work("T2").After(On("A1")));

        await using var run = await harness.StartAsync(plan);
        await harness.WaitForStatusAsync(run, "A1", WorkTaskStatus.AwaitingApproval);
        await RunTestSupport.WaitUntilAsync(() => harness.Approvals.Requests.Count == 1, "approval request");

        var request = Assert.Single(harness.Approvals.Requests);
        Assert.Equal(RiskLevel.Critical, request.Risk);
        Assert.Equal(harness.Project.ProjectId, request.ProjectId);
        Assert.Equal(run.Id.Value, request.RunId);
        Assert.Equal("A1", request.TaskId);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T2").Status);

        hold.SetResult();
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(["A1 Pending>AwaitingApproval", "A1 AwaitingApproval>Completed"], harness.Transitions("A1"));
    }

    [Fact]
    public async Task A_denied_approval_fails_the_task_and_the_run_fails_with_its_dependents_unreachable()
    {
        using var harness = new EngineHarness();
        harness.Approvals.Approve = _ => false;
        var plan = Plan(Work("T1"), Node("A1", WorkTaskKind.HumanApproval).After(On("T1")), Work("T2").After(On("A1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("A1").Status);
        Assert.Equal([new TaskId("T2")], result.Health.UnreachableTasks);
        Assert.Contains("A1", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Claude.Starts, s => s.Request.TaskId == "T2");
    }

    [Fact]
    public async Task A_gate_that_fails_is_a_denial()
    {
        using var harness = new EngineHarness();
        harness.Approvals.Approve = _ => throw new InvalidOperationException("sin ventana");
        var plan = Plan(Work("T1"), Node("A1", WorkTaskKind.HumanApproval).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("sin ventana", harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_F3_8_plan_integrates_the_work_and_asks_the_review_with_identifiers_only()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/Calc/Calc.cs", "Multiply")));

        await using var run = await harness.StartAsync(F38Plan());
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var integration = Assert.Single(harness.Integrator.Requests);
        Assert.Equal(harness.Worktrees.InitialCommit, integration.BaseCommit);

        // The commit T1 was verified at, never the name of its ref (which an agent with a shell could move).
        Assert.Equal([new IntegrationSource(new TaskId("T1"), harness.Worktrees.Head("T1"))], integration.Sources);
        var (_, performed) = Assert.Single(harness.Payloads<IntegrationPerformedPayload>());
        Assert.Equal([harness.Worktrees.Head("T1")], performed.Sources);
        Assert.Equal($"agentstudio/run-{run.Id}", performed.TargetRef);
        Assert.False(performed.IsDelivery);
        Assert.Equal(result.IntegrationCommit, performed.ResultCommit);
        Assert.Equal("Multiply", harness.Worktrees.Files(result.IntegrationCommit!)["src/Calc/Calc.cs"]);

        var review = Assert.Single(harness.Review.Requests);
        Assert.Equal(new ReviewRequest(harness.Project, run.Id, new TaskId("R1"), new AgentId("juez"), review.WorkspacePath), review);
        var reviewWorktree = harness.Payloads<WorktreeCreatedPayload>().Single(p => p.Payload.Task == new TaskId("R1")).Payload;
        Assert.Equal(reviewWorktree.Path, review.WorkspacePath);
        Assert.Equal(result.IntegrationCommit, reviewWorktree.BaseCommit);
        Assert.Equal("verdict-1", result.VerdictId);
        Assert.Equal("verdict-1", Assert.Single(harness.Payloads<RunFinishedPayload>()).Payload.VerdictId);
        Assert.Equal(2, run.Snapshot().AgentRunsUsed);
    }

    [Fact]
    public async Task The_review_is_queued_on_the_provider_of_its_judge_before_it_runs()
    {
        // docs/03 §5.1 and docs/18 P3: the monitor learns the provider of an attempt from its queue, so the context of a judge
        // on Codex is «no expuesto» from its start, not «sin observaciones todavía».
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/Calc/Calc.cs", "Multiply")));

        await using var run = await harness.StartAsync(F38Plan());
        var events = new List<OrchestrationEvent>();
        var reading = Task.Run(
            async () =>
            {
                await foreach (var e in run.Events.WithCancellation(TestContext.Current.CancellationToken))
                {
                    events.Add(e);
                }
            },
            TestContext.Current.CancellationToken);
        await run.CompleteAsync(harness);
        await reading.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var judge = Roster().Single(a => a.Id == new AgentId("juez")).Model.Provider;
        var queued = Assert.Single(events.OfType<TaskQueuedEvent>(), e => e.Task == new TaskId("R1"));
        Assert.Equal(judge, queued.Provider);
        var running = events.FindIndex(e => e is TaskStatusChangedEvent { To: WorkTaskStatus.Running } change && change.Task == new TaskId("R1"));
        Assert.True(events.IndexOf(queued) < running, "The review was queued after it started running.");
    }

    [Theory]
    [InlineData(ReviewDisposition.ChangesRequested)]
    [InlineData(ReviewDisposition.Rejected)]
    public async Task A_review_that_does_not_approve_fails_without_retrying(ReviewDisposition disposition)
    {
        using var harness = new EngineHarness();
        harness.Review.Script = _ => disposition;

        await using var run = await harness.StartAsync(F38Plan());
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("R1").Status);
        Assert.Single(harness.Review.Requests);
        Assert.Contains("verdict-1", harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verdict_that_needs_a_human_waits_for_the_approval_gate()
    {
        using var harness = new EngineHarness();
        harness.Review.Script = _ => ReviewDisposition.NeedsHuman;
        var hold = new TaskCompletionSource();
        harness.Approvals.Hold = hold;

        await using var run = await harness.StartAsync(F38Plan());
        await harness.WaitForStatusAsync(run, "R1", WorkTaskStatus.AwaitingApproval);
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);
        Assert.Equal("orchestration.review-escalation", Assert.Single(harness.Approvals.Requests).Action);

        hold.SetResult();
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(["R1 Pending>Running", "R1 Running>AwaitingApproval", "R1 AwaitingApproval>Completed"], harness.Transitions("R1"));
    }

    [Fact]
    public async Task A_conflict_fails_the_integration_with_evidence_and_is_never_resolved_by_the_system()
    {
        using var harness = new EngineHarness();
        harness.Integrator.ForceConflict = _ => ["src/Calc/Calc.cs"];

        await using var run = await harness.StartAsync(F38Plan());
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("I1").Status);
        Assert.Equal([new TaskId("R1")], result.Health.UnreachableTasks);
        var (_, performed) = Assert.Single(harness.Payloads<IntegrationPerformedPayload>());
        Assert.Null(performed.ResultCommit);
        Assert.Equal(IntegrationNames.CandidateRef(run.Id, new TaskId("I1")), performed.TargetRef);
        Assert.Equal(["src/Calc/Calc.cs"], performed.ConflictPaths);
        var failed = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload;
        Assert.Contains("src/Calc/Calc.cs", failed.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(failed.Evidence);
        Assert.Single(harness.Integrator.Requests);
        Assert.Empty(harness.Review.Requests);
        Assert.Null(result.IntegrationCommit);
    }

    [Fact]
    public async Task An_integration_whose_verification_failed_is_recorded_on_its_candidate_never_on_the_run_branch()
    {
        using var harness = new EngineHarness();
        harness.Integrator.FailVerification = _ => true;

        await using var run = await harness.StartAsync(F38Plan());
        var result = await run.CompleteAsync(harness);

        // The run's branch only points at verified integrations (ADR-0012): the record of a failed one names the candidate
        // ref that holds what was rejected, and the run ends without an integration commit.
        Assert.Equal(RunPhase.Failed, result.Phase);
        var (_, performed) = Assert.Single(harness.Payloads<IntegrationPerformedPayload>());
        Assert.Equal(IntegrationNames.CandidateRef(run.Id, new TaskId("I1")), performed.TargetRef);
        Assert.NotNull(performed.ResultCommit);
        Assert.Empty(performed.ConflictPaths);
        Assert.Null(result.IntegrationCommit);
        Assert.Null(Assert.Single(harness.Payloads<RunFinishedPayload>()).Payload.IntegrationCommit);
        Assert.Empty(harness.Review.Requests);
    }

    [Fact]
    public async Task A_task_ref_moved_after_the_task_was_verified_fails_the_integration_with_an_alert_and_nothing_is_merged()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        harness.Evidence.FailWhen = draft =>
        {
            // T1 is committed and verified; then something with a shell moves its hidden ref.
            if (draft.Title == "Registro del intento T1 #1")
            {
                harness.Worktrees.MoveRef($"refs/agentstudio/{draft.RunId}/T1", harness.Worktrees.InitialCommit);
            }

            return null;
        };
        var plan = Plan(Work("T1"), Node("I1", WorkTaskKind.Integration).After(On("T1")), Work("T2"));

        await using var run = await harness.StartAsync(plan, new RunOptions(MaxParallelTasks: 1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("I1").Status);
        Assert.Equal(0, run.Snapshot().View("I1").RetryCount);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T2").Status);
        Assert.Empty(harness.Integrator.Requests);
        Assert.Null(result.IntegrationCommit);
        var (_, alert) = Assert.Single(harness.Alerts("integration.source-moved"));
        Assert.Equal([new TaskId("I1"), new TaskId("T1")], alert.Tasks);
        Assert.Contains(harness.Worktrees.InitialCommit, alert.Message, StringComparison.Ordinal);
        Assert.Contains(harness.Worktrees.Head("T1"), alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_integrations_of_one_run_never_run_at_the_same_time_even_if_the_plan_lets_them()
    {
        using var harness = new EngineHarness();
        var together = new TaskCompletionSource();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")) with { Gate = together });
        harness.Claude.Script("ejecutor2", "T2", 1, AgentScript.Completed(("src/T2/b.cs", "b")) with { Gate = together });
        var inside = 0;
        var most = 0;
        var counting = new Lock();
        harness.Integrator.FailVerification = _ =>
        {
            var now = Interlocked.Increment(ref inside);
            lock (counting)
            {
                most = Math.Max(most, now);
            }

            Thread.Sleep(200); // long enough for a second integration to be dispatched next to this one
            Interlocked.Decrement(ref inside);
            return false;
        };

        // Today the validator rejects this plan (IntegrationsNotOrdered); a run an earlier version started may still hold it.
        var plan = Plan(Work("T1"), Work("T2", agent: "ejecutor2"), Node("I1", WorkTaskKind.Integration).After(On("T1")), Node("I2", WorkTaskKind.Integration).After(On("T2")));
        var legacy = new RunId("20261014-abcdef");
        await harness.StoreStartedRunAsync(legacy, plan);
        await using var run = await harness.CreateOrchestrator().ResumeAsync(harness.Project, legacy, TestContext.Current.CancellationToken);
        await RunTestSupport.WaitUntilAsync(() => harness.Claude.Starts.Count == 2, "both agents started");
        together.SetResult();
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Integrator.Requests.Count);
        Assert.Equal(1, most);
        var transitions = harness.Transitions();
        var first = harness.Integrator.Requests[0].Task.Value;
        var second = harness.Integrator.Requests[1].Task.Value;
        Assert.True(transitions.IndexOf($"{first} Running>Completed") < transitions.IndexOf($"{second} Pending>Running"), string.Join(" | ", transitions));
    }

    [Fact]
    public async Task Parallel_work_is_merged_in_topological_then_id_order()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T2", 1, AgentScript.Completed(("src/T2/b.cs", "b")));
        harness.Claude.Script("ejecutor2", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var plan = Plan(
            Work("T2"),
            Work("T1", agent: "ejecutor2"),
            Node("I1", WorkTaskKind.Integration).After(On("T2"), On("T1")) with { Covers = [] });

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(["T1", "T2"], Assert.Single(harness.Integrator.Requests).Sources.Select(s => s.Task.Value));
        var merged = harness.Worktrees.Files(result.IntegrationCommit!);
        Assert.Equal("a", merged["src/T1/a.cs"]);
        Assert.Equal("b", merged["src/T2/b.cs"]);
    }

    [Fact]
    public async Task A_tool_run_executes_the_profile_step_on_the_code_it_builds_on()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var plan = Plan(Work("T1"), Node("L1", WorkTaskKind.ToolRun).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var call = harness.Verification.Calls.Single(c => c.Scope.Task == new TaskId("L1"));
        Assert.Equal(["lint"], call.Steps);
        var worktree = harness.Payloads<WorktreeCreatedPayload>().Single(p => p.Payload.Task == new TaskId("L1")).Payload;
        Assert.Equal(worktree.Path, call.WorkingDirectory);
        Assert.Equal(harness.Worktrees.Head("T1"), worktree.BaseCommit);
        var evidence = run.Snapshot().View("L1").Evidence;
        Assert.Contains(evidence, e => harness.Evidence.Record(e).Kind == EvidenceKind.CommandOutput);
    }

    [Fact]
    public async Task A_failing_tool_is_retried_and_then_fails_the_run()
    {
        using var harness = new EngineHarness();
        harness.Verification.Script = (scope, step) => step.Id == "lint" ? StepResult.Fail : null;
        var plan = Plan(Work("T1"), Node("L1", WorkTaskKind.ToolRun).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(2, harness.Verification.Calls.Count(c => c.Scope.Task == new TaskId("L1")));
        Assert.Contains($"checkout L1 {harness.Worktrees.Head("T1")}", harness.Worktrees.Calls);
    }
}
