using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Orchestration.Integration;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using JudgeRunLog = AgentStudio.Debate.Judging.RunLog;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The engine reads the rows of an integration's evidence back milliseconds after the integrator captured them, in the
/// same process: a row that no longer matches its capture, or that is gone, was rewritten behind the store's triggers.
/// That is an integrity breach, like a source ref that moved or a user repository that changed (ADR-0010), never a
/// failure to retry: nothing more is dispatched or retried, an alert says which evidence changed, and the run never ends
/// Succeeded, so that integration is never reviewed as the candidate nor delivered.
/// </summary>
public sealed class IntegrationEvidenceTamperingTests
{
    private const string Alert = "integration.evidence-tampered";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// T1 is integrated by I1 (which may be retried once) and reviewed by R1; T2 is independent and, with one parallel
    /// slot, waits behind them: it would only start if dispatching went on after the integration.
    /// </summary>
    private static PlanDefinition ReviewedPlan() => Plan(
        Work("T1").Verified("build", "test"),
        Node("I1", WorkTaskKind.Integration).After(On("T1")),
        Node("R1", WorkTaskKind.Review).After(On("I1")),
        Work("T2"));

    private static readonly RunOptions OneAtATime = new(MaxParallelTasks: 1);

    /// <summary>
    /// Rewrites, behind the store's back, one row of the evidence the first integration attempt captured: after the
    /// integrator captured it and before the engine reads it back. Later attempts are left alone, so a retry would get
    /// clean evidence. Returns the record as it was captured (null until it happens).
    /// </summary>
    private static Func<EvidenceRecord?> TamperWithTheFirstIntegration(EngineHarness harness, string how)
    {
        EvidenceRecord? captured = null;
        harness.Integrator.OnResult = (request, integration) =>
        {
            if (request.Attempt != 1)
            {
                return integration;
            }

            var report = integration.EvidenceRecords![0];
            var tests = integration.Verification.Single(v => v.Evidence.Kind == EvidenceKind.TestRun).Evidence;
            switch (how)
            {
                case "report-title":
                    captured = report;
                    harness.Evidence.ChangeRow(report.Id, r => new EvidenceRecord(r.Id, r.Kind, r.Origin, "Integración verificada", r.ProducedBy, r.CapturedAt, r.ContentSha256, r.TestRun, r.Covers, r.SubjectCommit));
                    break;
                case "test-counters":
                    captured = tests;
                    harness.Evidence.ChangeRow(tests.Id, r => new EvidenceRecord(r.Id, r.Kind, r.Origin, r.Title, r.ProducedBy, r.CapturedAt, r.ContentSha256, new TestRunSummary(300, 0, 0, 0), r.Covers, r.SubjectCommit));
                    break;
                case "row-removed":
                    captured = report;
                    harness.Evidence.Lose(report.Id);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(how), how, null);
            }

            return integration;
        };

        return () => captured;
    }

    [Theory]
    [InlineData("report-title")]
    [InlineData("test-counters")]
    [InlineData("row-removed")]
    public async Task A_row_of_the_integration_evidence_rewritten_after_its_capture_stops_the_run_with_an_alert(string how)
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var captured = TamperWithTheFirstIntegration(harness, how);

        await using var run = await harness.StartAsync(ReviewedPlan(), OneAtATime);
        var result = await run.CompleteAsync(harness);

        // The run stops: nothing is retried, nothing more is dispatched, the judge is never launched.
        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("evidencia de la integración", result.Summary, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("I1").Status);
        Assert.Equal(0, run.Snapshot().View("I1").RetryCount);
        Assert.Single(harness.Integrator.Requests);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T2").Status);
        Assert.Equal(["T1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        Assert.Empty(harness.Review.Requests);

        // The alert names the integration and the evidence that changed, and comes before the integration fails.
        var (alertEntry, alert) = Assert.Single(harness.Alerts(Alert));
        Assert.Equal([new TaskId("I1")], alert.Tasks);
        Assert.Contains(captured()!.Id.Value, alert.Message, StringComparison.Ordinal);
        var (failedEntry, failed) = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.Task == new TaskId("I1") && p.Payload.To == WorkTaskStatus.Failed);
        Assert.True(alertEntry.Sequence < failedEntry.Sequence);
        Assert.Contains(captured()!.Id.Value, failed.Reason, StringComparison.Ordinal);

        // The end of the run says Failed and carries no integration.
        var (_, finished) = Assert.Single(harness.Payloads<RunFinishedPayload>());
        Assert.Equal(RunPhase.Failed, finished.Phase);
        Assert.Null(finished.IntegrationCommit);
        Assert.Null(result.IntegrationCommit);
    }

    /// <summary>
    /// The integrator had already fast-forwarded the run's branch to the merged commit when the engine saw the rewritten
    /// row: the log records that integration on the task's candidate ref, as a rejected candidate, so it is never the
    /// commit a judge reviews.
    /// </summary>
    [Fact]
    public async Task An_integration_whose_evidence_was_rewritten_is_recorded_on_its_candidate_and_is_never_the_commit_under_review()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        TamperWithTheFirstIntegration(harness, "test-counters");

        await using var run = await harness.StartAsync(ReviewedPlan(), OneAtATime);
        await run.CompleteAsync(harness);

        var (_, performed) = Assert.Single(harness.Payloads<IntegrationPerformedPayload>());
        Assert.Equal(IntegrationNames.CandidateRef(run.Id, new TaskId("I1")), performed.TargetRef);
        Assert.NotNull(performed.ResultCommit);
        Assert.Empty(performed.ConflictPaths);
        Assert.Null(JudgeRunLog.CandidateCommit(JudgeRunLog.EntriesOf(harness.Entries, run.Id.Value)));
    }

    /// <summary>
    /// Everything else would let the run through: the retry would get clean evidence, the judge would approve and the
    /// verdict store keeps every verdict the log announces. The rewritten row alone keeps the run from being delivered.
    /// </summary>
    [Fact]
    public async Task A_run_whose_integration_evidence_was_rewritten_once_is_never_delivered()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        TamperWithTheFirstIntegration(harness, "test-counters");

        await using var run = await harness.StartAsync(ReviewedPlan(), OneAtATime);
        await run.CompleteAsync(harness);

        var verdicts = new FakeVerdictStore();
        foreach (var (_, issued) in harness.Payloads<VerdictIssuedPayload>())
        {
            await verdicts.AppendAsync(
                harness.Project.ProjectId,
                new StoredVerdict(issued.VerdictId, DeliveryRecords.Verdict(issued.Decision), run.Id.Value, issued.Task?.Value, AuditScope.CurrentPlan, issued.Valid, [], issued.Supersedes),
                Ct);
        }

        var delivery = new DeliveryService(harness.Worktrees, harness.Approvals, harness.Decisions, verdicts);
        var readiness = await delivery.CheckAsync(harness.Project, run.Id, Ct);

        Assert.False(readiness.Ready);
        Assert.Null(readiness.Commit);
        Assert.Contains(nameof(RunPhase.Failed), readiness.Reason, StringComparison.Ordinal);

        var delivered = await delivery.DeliverAsync(harness.Project, run.Id, DeliveryMode.FastForwardOnly, Ct);

        Assert.False(delivered.Delivered);
        Assert.Equal(readiness.Reason, delivered.Message);
        Assert.Empty(harness.Approvals.Requests);
    }

    /// <summary>
    /// The app dies after the alert and before <c>RunFinished</c>, and whoever rewrote the row puts it back as it was
    /// captured, so the log and the store agree again. The breach is in the log: the resumed run retries nothing and
    /// dispatches nothing, and ends Failed.
    /// </summary>
    [Fact]
    public async Task A_run_stopped_by_rewritten_integration_evidence_resumes_into_no_new_work_even_with_the_row_put_back()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var captured = TamperWithTheFirstIntegration(harness, "report-title");
        harness.LogStore.FailWhen = entry => entry.Kind == DecisionKind.RunFinished;
        RunId id;
        await using (var run = await harness.StartAsync(ReviewedPlan(), OneAtATime))
        {
            id = run.Id;
            Assert.Equal(RunPhase.Interrupted, (await run.CompleteAsync(harness)).Phase);
        }

        harness.LogStore.FailWhen = null;
        var original = captured()!;
        harness.Evidence.ChangeRow(original.Id, _ => original);

        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, id, Ct);
        var result = await resumed.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("evidencia de la integración", result.Summary, StringComparison.Ordinal);
        Assert.Single(harness.Integrator.Requests);
        Assert.Empty(harness.Review.Requests);
        Assert.Equal(["T1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        Assert.Equal(RunPhase.Failed, Assert.Single(harness.Payloads<RunFinishedPayload>()).Payload.Phase);
    }

    /// <summary>
    /// A follow-up run would build on the last integration of the run it follows, here the one whose evidence was
    /// rewritten. A run stopped by that breach is not followed: the follow-up does not start, and nothing is written.
    /// </summary>
    [Fact]
    public async Task A_run_stopped_by_rewritten_integration_evidence_cannot_be_followed()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        TamperWithTheFirstIntegration(harness, "test-counters");
        var orchestrator = harness.CreateOrchestrator();
        RunId first;
        await using (var run = await orchestrator.StartAsync(harness.Request(ReviewedPlan(), OneAtATime), Ct))
        {
            first = run.Id;
            Assert.Equal(RunPhase.Failed, (await run.CompleteAsync(harness)).Phase);
        }

        var entries = harness.Entries.Count;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StartAsync(harness.Request(Plan(Work("F1")), follows: first), Ct));

        Assert.Contains(first.Value, refused.Message, StringComparison.Ordinal);
        Assert.Contains(Alert, refused.Message, StringComparison.Ordinal);
        Assert.Equal(entries, harness.Entries.Count);
        Assert.Single(harness.Payloads<RunStartedPayload>());
        Assert.Equal(["T1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
    }
}
