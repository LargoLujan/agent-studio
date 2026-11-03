using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// R3-08: what feeds a prompt is the evidence as the decision log anchored it (<c>EvidenceRegistered</c>), never what the
/// row of the store says now. Someone who rewrites a report and the hash its row keeps sees the store find nothing wrong;
/// moving the row to another run also hides it from the run's listing. Neither gets anything dispatched.
/// </summary>
public sealed class AnchoredEvidenceReadTests
{
    /// <summary>A report that says T1 built another API than it did.</summary>
    private const string Forged = """{"status":"completed","summary":"Trabajo terminado.","filesChanged":[],"claims":["Hecho."],"openIssues":[],"outputs":[{"key":"api","value":"Calc.Divide"}]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EvidenceId ReportOf(EngineHarness harness, string task) =>
        harness.Evidence.All().Single(e => e.Record.Kind == EvidenceKind.AgentStatement && e.Draft.TaskId == task).Record.Id;

    private static void ScriptT1(EngineHarness harness) =>
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { StructuredOutput = AgentScript.Report(outputs: [("api", "Calc.Multiply")]) });

    [Fact]
    public async Task A_report_moved_to_another_run_with_other_content_and_its_hash_leaves_the_run_not_resumable_with_an_alert()
    {
        using var harness = new EngineHarness();
        ScriptT1(harness);
        harness.Claude.Script("ejecutor", "T2", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(Plan(Work("T1"), Work("T2").After(On("T1"))));
        await harness.WaitForStatusAsync(first, "T2", WorkTaskStatus.Running);
        var report = ReportOf(harness, "T1");
        await first.DisposeAsync();
        Assert.Equal(RunPhase.Interrupted, (await first.CompleteAsync(harness)).Phase);

        // With the app closed: the same id, in another run, holding other content with its matching hash.
        harness.Evidence.Rewrite(report, Forged);
        harness.Evidence.MoveToRun(report, "20261015-0000ff");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct));

        Assert.Contains(report.Value, error.Message, StringComparison.Ordinal);
        var (_, alert) = Assert.Single(harness.Alerts("run.not-resumable"));
        Assert.Contains(report.Value, alert.Message, StringComparison.Ordinal);
        Assert.Contains("T1", alert.Message, StringComparison.Ordinal);
        Assert.Equal(["T1", "T2"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        Assert.DoesNotContain(harness.Payloads<AgentRunFinishedPayload>(), p => p.Payload.Status == AttemptOutcome.Interrupted);
    }

    [Fact]
    public async Task A_report_rewritten_with_its_hash_after_it_was_registered_is_never_given_to_the_task_that_consumes_it()
    {
        using var harness = new EngineHarness();
        ScriptT1(harness);
        var held = new TaskCompletionSource();
        harness.Approvals.Hold = held;
        await using var run = await harness.StartAsync(Plan(Work("T1"), Node("A1", WorkTaskKind.HumanApproval).After(On("T1")), Work("T2").After(On("A1"))));
        await harness.WaitForStatusAsync(run, "A1", WorkTaskStatus.AwaitingApproval);
        var report = ReportOf(harness, "T1");
        var anchored = harness.Evidence.Record(report).ContentSha256;

        // While the run waits for the approval, someone with the evidence folder and the database rewrites the report and
        // the hash its row keeps: the store itself sees nothing wrong, and the row stays in the run.
        harness.Evidence.Rewrite(report, Forged);
        held.SetResult();
        await harness.WaitUntilAsync(run, s => s.View("T2").Status != WorkTaskStatus.Pending, "T2 leaves Pending");

        Assert.Equal(WorkTaskStatus.Blocked, run.Snapshot().View("T2").Status);
        await harness.WaitForPhaseAsync(run, RunPhase.AwaitingHuman);
        Assert.Equal(["T1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        var (_, alert) = Assert.Single(harness.Alerts("upstream.evidence-tampered"));
        Assert.Equal([new TaskId("T2"), new TaskId("T1")], alert.Tasks);
        Assert.Contains(report.Value, alert.Message, StringComparison.Ordinal);
        Assert.Contains(anchored, alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Payloads<InstructionsIssuedPayload>(), p => p.Payload.Task == new TaskId("T2"));
    }

    [Fact]
    public async Task A_report_read_as_the_log_anchored_it_reaches_the_task_that_consumes_it()
    {
        using var harness = new EngineHarness();
        ScriptT1(harness);
        await using var run = await harness.StartAsync(Plan(Work("T1"), Node("A1", WorkTaskKind.HumanApproval).After(On("T1")), Work("T2").After(On("A1"))));

        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Contains("Calc.Multiply", Assert.Single(harness.Claude.Starts, s => s.Request.TaskId == "T2").Request.Prompt, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts("upstream.evidence-tampered"));
    }
}
