using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// Every verification the engine asks for names the commit its worktree is on, so the evidence it captures is bound to
/// that commit and the judge can tell the tests of the commit under review from those of an older one.
/// </summary>
public sealed class VerificationCommitTests
{
    [Fact]
    public async Task The_verification_of_an_attempt_and_of_a_tool_names_the_commit_their_worktree_is_on()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var plan = Plan(Work("T1").Verified("build", "test"), Node("L1", WorkTaskKind.ToolRun).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var head = harness.Worktrees.Head("T1");
        Assert.NotEqual(harness.Worktrees.InitialCommit, head);
        Assert.Equal(head, harness.Verification.Calls.Single(c => c.Scope.Task == new TaskId("T1")).Scope.SubjectCommit);
        Assert.Equal(head, harness.Verification.Calls.Single(c => c.Scope.Task == new TaskId("L1")).Scope.SubjectCommit);
    }

    [Fact]
    public async Task Every_evidence_the_engine_registers_anchors_in_the_log_the_metadata_that_decides_approval()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var plan = Plan(
            Work("T1").Verified("build", "test"),
            Node("I1", WorkTaskKind.Integration).After(On("T1")) with { Covers = [] },
            Node("R1", WorkTaskKind.Review).After(On("I1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var registered = harness.Payloads<EvidenceRegisteredPayload>().Select(p => p.Payload).ToList();
        Assert.NotEmpty(registered);
        Assert.All(registered, p =>
        {
            var row = harness.Evidence.Record(p.Evidence);
            Assert.Equal(EvidenceRegisteredPayload.MetadataVersion, p.V);
            Assert.Equal((row.Title, row.ProducedBy, row.CapturedAt, row.SubjectCommit), (p.Title, p.ProducedBy, p.CapturedAt, p.SubjectCommit));
            Assert.Equal(TestRunSnapshot.From(row.TestRun), p.TestRun);
        });
        var integrated = Assert.Single(registered, p => p is { Kind: EvidenceKind.TestRun, Task.Value: "I1" });
        Assert.Equal(result.IntegrationCommit, integrated.SubjectCommit);
        Assert.Equal(new TestRunSnapshot(3, 0, 0, 0), integrated.TestRun);
    }

    /// <summary>A plan whose integration gets no retry, so the first attempt decides how the run ends.</summary>
    private static PlanDefinition IntegratedOnce() => Plan(
        Work("T1").Verified("build", "test"),
        Node("I1", WorkTaskKind.Integration).After(On("T1")) with { MaxRetries = 0 });

    /// <summary>
    /// The log anchors the evidence of an integration as the integrator captured it and handed it back, never as the store
    /// holds it later: a row rewritten behind the triggers between the capture and its registration (its own report of the
    /// merge, or a test run of the candidate) never takes its place, and the integration fails saying which and why.
    /// </summary>
    [Theory]
    [InlineData("report")]
    [InlineData("tests")]
    public async Task The_evidence_of_an_integration_is_anchored_as_the_integrator_captured_it_and_a_row_changed_before_that_fails_it(string which)
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        EvidenceRecord? captured = null;
        harness.Integrator.OnResult = (_, integration) =>
        {
            captured = which == "report"
                ? integration.EvidenceRecords![0]
                : integration.Verification.Single(v => v.Evidence.Kind == EvidenceKind.TestRun).Evidence;
            harness.Evidence.ChangeRow(captured.Id, r => new EvidenceRecord(r.Id, r.Kind, r.Origin, "Integración verificada", r.ProducedBy, r.CapturedAt, r.ContentSha256, r.TestRun, r.Covers, r.SubjectCommit));
            return integration;
        };

        await using var run = await harness.StartAsync(IntegratedOnce());
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("I1").Status);
        var anchored = Assert.Single(harness.Payloads<EvidenceRegisteredPayload>(), p => p.Payload.Evidence == captured!.Id).Payload;
        Assert.Equal(captured!.Title, anchored.Title);
        var failed = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.Task == new TaskId("I1") && p.Payload.To == WorkTaskStatus.Failed).Payload;
        Assert.Contains(captured.Id.Value, failed.Reason, StringComparison.Ordinal);
        Assert.Contains("title: the store holds 'Integración verificada'", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_integration_evidence_handed_back_without_its_record_is_never_anchored_from_the_store()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        EvidenceId? report = null;
        harness.Integrator.OnResult = (_, integration) =>
        {
            report = integration.Evidence[0];
            return integration with { EvidenceRecords = null };
        };

        await using var run = await harness.StartAsync(IntegratedOnce());
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.DoesNotContain(harness.Payloads<EvidenceRegisteredPayload>(), p => p.Payload.Evidence == report);
        var failed = harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.Task == new TaskId("I1") && p.Payload.To == WorkTaskStatus.Failed).Payload;
        Assert.Contains(report!.Value.Value, failed.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The log names the run's branch only as the target of an integration whose evidence it anchored as captured: one
    /// handed back without the record of its evidence is recorded on its candidate ref, like a rejected candidate. That is
    /// the integrator's own slip, not tampering: no alert, and the attempt is retried.
    /// </summary>
    [Fact]
    public async Task An_integration_handed_back_without_the_record_of_its_evidence_is_recorded_on_its_candidate_and_retried()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        harness.Integrator.OnResult = (request, integration) => request.Attempt == 1 ? integration with { EvidenceRecords = null } : integration;
        var plan = Plan(Work("T1").Verified("build", "test"), Node("I1", WorkTaskKind.Integration).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Integrator.Requests.Count);
        Assert.Empty(harness.Alerts("integration.evidence-tampered"));
        var performed = harness.Payloads<IntegrationPerformedPayload>().Select(p => p.Payload).ToList();
        Assert.Equal([IntegrationNames.CandidateRef(run.Id, new TaskId("I1")), IntegrationNames.BranchName(run.Id)], performed.Select(p => p.TargetRef));
        Assert.Equal(result.IntegrationCommit, performed[1].ResultCommit);
    }

    [Fact]
    public async Task Every_verification_names_the_project_repository_the_commit_is_copied_from_never_the_worktree()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")));
        var plan = Plan(Work("T1").Verified("build", "test"), Node("L1", WorkTaskKind.ToolRun).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        // The worktree is only the working directory: an isolated step copies the commit from the project's repository.
        Assert.Equal(2, harness.Verification.Calls.Count);
        Assert.All(harness.Verification.Calls, c => Assert.Equal(harness.RepositoryRoot, c.Scope.RepositoryRoot));
        Assert.All(harness.Verification.Calls, c => Assert.NotEqual(harness.RepositoryRoot, c.WorkingDirectory));
    }
}
