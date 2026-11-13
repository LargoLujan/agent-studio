using System.IO;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

// Orchestration shares its internals with the tests (Phase 10): here RunLog is the one of the judge.
using RunLog = AgentStudio.Debate.Judging.RunLog;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>
/// docs/15 D-37: a run kept to the protocol gives no finding. The strongest proof is the engine itself: runs it records
/// with every port faked (<see cref="EngineHarness"/>) are audited as they are, so what the auditor reads is what the
/// engine writes, and the strings it shares with the Orchestration module (which it cannot see, ADR-0002) are pinned.
/// </summary>
public sealed class OrchestratorAuditCleanRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_prepared_run_kept_to_the_protocol_and_delivered_gives_no_finding()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();

        var outcome = await run.AuditAsync();

        Assert.True(outcome.Log.IsValid, outcome.Log.Problem);
        Assert.Empty(outcome.Findings);
        Assert.Null(outcome.Replan);
        Assert.False(outcome.HasSomethingToRecord);
    }

    [Fact]
    public async Task The_auditor_reads_the_prompts_of_the_executors_and_the_attempt_records_of_the_run()
    {
        var run = await PreparedRun.CleanAsync();
        var log = await run.Recorder.LoadAsync(AuditWorld.Project, Ct);
        var titles = (await run.Evidence.ListByRunAsync(AuditWorld.Project, AuditWorld.Run.Value, Ct)).ToDictionary(e => e.Id, e => e.Title);

        var wanted = OrchestratorAudit.EvidenceToRead(log.Entries, AuditWorld.Run);

        Assert.Equal(["Prompt de T1 (intento 1)", "Registro del intento T1 #1", "Registro del intento I1 #1"], wanted.Select(w => titles[w.Evidence]));
        Assert.All(wanted, w => Assert.Equal(RunLog.AnchoredSha256(RunLog.EntriesOf(log.Entries, AuditWorld.Run.Value), w.Evidence), w.Sha256));
    }

    [Fact]
    public async Task The_F38_run_the_engine_records_gives_no_finding()
    {
        using var harness = new EngineHarness();
        await RequireReviewAsync(harness);
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/Calc/Calc.cs", "public static class Calc { }"), ("tests/Calc.Tests/CalcTests.cs", "public class CalcTests { }")));
        var plan = Plan(
            Work("T1", scope: ["src/Calc/", "tests/Calc.Tests/"]).Verified("build", "test"),
            Node("I1", WorkTaskKind.Integration).After(On("T1")) with { Covers = [], MaxRetries = 0 },
            Node("R1", WorkTaskKind.Review).After(On("I1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);
        var outcome = await AuditAsync(harness, run.Id);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.True(outcome.Log.IsValid, outcome.Log.Problem);
        Assert.Empty(outcome.Findings);
        Assert.Null(outcome.Replan);

        // The audit did read the engine's own records: the prompt of T1 and the attempt records of T1 and I1.
        var log = await harness.Decisions.LoadAsync(harness.Project.ProjectId, Ct);
        var read = OrchestratorAudit.EvidenceToRead(log.Entries, run.Id).Select(w => harness.Evidence.Record(w.Evidence).Title).ToList();
        Assert.Equal(["Prompt de T1 (intento 1)", $"{OrchestratorAudit.AttemptRecordTitlePrefix} T1 #1", $"{OrchestratorAudit.AttemptRecordTitlePrefix} I1 #1"], read);
        Assert.Contains($"\"kind\":\"{OrchestratorAudit.AttemptRecordKind}\"", harness.Evidence.Text(OrchestratorAudit.EvidenceToRead(log.Entries, run.Id)[1].Evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_the_engine_records_with_a_retry_a_condition_a_skip_an_approval_and_a_merge_point_gives_no_finding()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Failing());
        harness.Claude.Script("ejecutor", "T2", 1, AgentScript.Completed(("src/T2/Code.cs", "class T2 { }")));
        var plan = Plan(
            Work("T1"),
            Node("C1", WorkTaskKind.Condition).After(On("T1")) with { Condition = new TaskSucceeded(new TaskId("T1")) },
            Work("T2").After(On("C1", ConditionBranches.Yes)),
            Work("T3").After(On("C1", ConditionBranches.No)),
            Node("H1", WorkTaskKind.HumanApproval).After(On("T2")),
            Work("T4", workspace: WorkspaceMode.None).After(On("H1"), On("T3")) with { Join = JoinPolicy.AnyCompleted });

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);
        var outcome = await AuditAsync(harness, run.Id);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(WorkTaskStatus.Skipped, run.Snapshot().View("T3").Status);
        Assert.Equal(2, run.Snapshot().View("T1").Attempt);
        Assert.True(outcome.Log.IsValid, outcome.Log.Problem);
        Assert.Empty(outcome.Findings);
    }

    [Fact]
    public async Task An_attempt_the_engine_failed_for_writing_outside_its_scope_gives_no_finding()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/Code.cs", "class T1 { }"), ("docs/fuera.md", "fuera del alcance")));

        await using var run = await harness.StartAsync(Plan(Work("T1") with { MaxRetries = 0 }));
        var result = await run.CompleteAsync(harness);
        var outcome = await AuditAsync(harness, run.Id);

        Assert.Equal(RunPhase.Failed, result.Phase);
        var log = await harness.Decisions.LoadAsync(harness.Project.ProjectId, Ct);
        var record = OrchestratorAudit.EvidenceToRead(log.Entries, run.Id).Single(w => harness.Evidence.Record(w.Evidence).Title.StartsWith(OrchestratorAudit.AttemptRecordTitlePrefix, StringComparison.Ordinal));
        Assert.Contains("docs/fuera.md", harness.Evidence.Text(record.Evidence), StringComparison.Ordinal);
        Assert.Empty(outcome.Findings);
    }

    [Fact]
    public async Task A_review_that_needs_a_person_asks_with_the_action_the_auditor_reads()
    {
        using var harness = new EngineHarness();
        harness.Review.Script = _ => ReviewDisposition.NeedsHuman;
        var plan = Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        Assert.Contains(harness.Approvals.Requests, r => r.Action == OrchestratorAudit.ReviewEscalationAction && r.TaskId == "R1");
        Assert.Empty((await AuditAsync(harness, run.Id)).Findings);
    }

    [Fact]
    public void The_strings_the_auditor_reads_are_the_ones_the_engine_writes()
    {
        var run = new RunId("20261019-a0d170");

        Assert.Equal(Orchestrator.PlanApprovalAction, OrchestratorAudit.PlanApprovalAction);
        Assert.Equal(DeliveryService.ApprovalAction, OrchestratorAudit.DeliveryApprovalAction);
        Assert.Equal(WorkspaceCleanup.ApprovalAction, OrchestratorAudit.DiscardApprovalAction);
        Assert.Equal(IntegrationNames.BranchName(run), OrchestratorAudit.RunBranch(run));
    }

    /// <summary>The executor asks for system-captured tests and an independent review, as in the roster a project gets by default.</summary>
    private static async Task RequireReviewAsync(EngineHarness harness)
    {
        var executor = Roster().Single(a => a.Id == new AgentId("ejecutor"));
        await harness.Roster.SaveAsync(harness.Project.ProjectId, executor with { Review = new ReviewPolicy(true, true) }, Ct);
    }

    /// <summary>The audit of an engine's run, read from the harness's stores as the auditor's service will read them.</summary>
    private static async Task<OrchestratorAuditOutcome> AuditAsync(EngineHarness harness, RunId run)
    {
        var project = harness.Project.ProjectId;
        var log = await harness.Decisions.LoadAsync(project, Ct);
        var started = RunLog.RunStarted(RunLog.EntriesOf(log.Entries, run.Value)) ?? throw new InvalidOperationException($"Run {run} did not start.");
        var contents = new Dictionary<EvidenceId, ReadOnlyMemory<byte>>();
        foreach (var wanted in OrchestratorAudit.EvidenceToRead(log.Entries, run))
        {
            await using var stream = await harness.Evidence.OpenContentAsync(project, wanted.Evidence, Ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct);
            contents[wanted.Evidence] = buffer.ToArray();
        }

        return OrchestratorAudit.Audit(new OrchestratorAuditInput(
            run,
            log,
            await harness.Runs.FindAsync(project, run, Ct),
            await harness.Plans.FindAsync(project, started.PlanHash, Ct),
            await harness.RequirementStore.FindAsync(project, started.RequirementSetHash, Ct),
            contents));
    }
}
