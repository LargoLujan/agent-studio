using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Application.Work;
using AgentStudio.Orchestration.Work;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>A recorder whose log never verifies, to see that the readers fail closed.</summary>
internal sealed class BrokenLogRecorder(IDecisionRecorder inner) : IDecisionRecorder
{
    private static DecisionLogState Break(DecisionLogState state) => state with { Verification = new DecisionLogVerification(false, 1, "hash roto") };

    public async Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) => Break(await inner.LoadAsync(projectId, cancellationToken));

    public async Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => Break(await inner.LoadAsync(projectId, load, cancellationToken));

    public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default) =>
        inner.RecordAsync(projectId, actor, kind, summary, payload, corrects, cancellationToken);
}

/// <summary>
/// docs/16 D-29 and F9-4, docs/04 §23.3: the chain requirement, task, attempt, change, verification, verdict and delivery is read from the verified log in both
/// directions, through the nodes of the chain only, and fails closed on a log that does not verify.
/// </summary>
public sealed class TraceChainReaderTests
{
    private static readonly string CommitA = new('a', 40);
    private static readonly string CommitB = new('b', 40);
    private static readonly string CommitC = new('c', 40);

    private static CancellationToken Ct => WorkWorld.Ct;

    private static TraceChainReader Reader(WorkWorld world) => new(world.Instance.Recorder, world.Instance.Requirements, world.Instance.Plans, world.Instance.Verdicts);

    private static TraceRef T1 => TraceRef.Task(WorkWorld.Run1, "T1");

    private static TraceRef Attempt(int n) => TraceRef.Attempt(WorkWorld.Run1, "T1", n);

    private static async Task<(WorkWorld World, TraceRef Milestone)> StartedAsync()
    {
        var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        return (world, TraceRef.Milestone(view.Header.Id.Value));
    }

    /// <summary>The whole chain of T1 (R1): one attempt with a diff and a test run on commit B, a valid verdict and the delivery of commit A.</summary>
    private static async Task CompleteT1Async(WorkWorld world)
    {
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.CodeDiff, "diff1");
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.TestRun, "test1", CommitB);
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T1");
        await world.DeliverAsync(WorkWorld.Run1, CommitA);
    }

    private static List<(TraceChainStage, TraceRef)> Shape(TraceChain chain) => [.. chain.Steps.Select(s => (s.Stage, s.Node))];

    [Fact]
    public async Task The_delivery_of_a_run_whose_integration_task_merged_the_work_of_its_prerequisites_is_in_the_chain_of_what_they_covered()
    {
        // Found with real runs: the engine records the integration under the task that integrates (T3 here, which covers nothing) and delivers
        // what it merged; the chain took only the integration task itself as integrated, so no requirement ever reached its delivery.
        var (world, _) = await StartedAsync();
        using (world)
        {
            await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.RecordAsync(WorkWorld.Run1, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(AgentStudio.Tests.Domain.TestData.Tid("T3"), "agentstudio/run-1", [CommitB], CommitC, [], false) { RunId = WorkWorld.Run1 });
            await world.DeliverAsync(WorkWorld.Run1, CommitA);

            var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

            Assert.Contains((TraceChainStage.Delivery, TraceRef.Commit(CommitA)), Shape(chain));
        }
    }

    [Fact]
    public async Task The_verdict_the_engine_issues_on_the_review_task_of_the_run_is_in_the_chain_of_the_requirement_the_run_covered()
    {
        // Found by the integration test of a milestone with real runs: VerdictKeeper issues the verdict of a run on its review task (T7 here),
        // which covers nothing, so a chain that walks from a requirement through the tasks that cover it never reached the verdict.
        var (world, _) = await StartedAsync();
        using (world)
        {
            await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            var verdict = await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7");

            var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

            Assert.Contains((TraceChainStage.Verdict, TraceRef.Verdict(verdict)), Shape(chain));
        }
    }

    [Fact]
    public async Task The_chain_of_a_requirement_goes_through_its_task_attempt_change_verification_verdict_and_delivery_and_not_through_the_others()
    {
        var (world, milestone) = await StartedAsync();
        using (world)
        {
            await CompleteT1Async(world);

            var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

            Assert.Equal(
                [
                    (TraceChainStage.Requirement, TraceRef.Requirement("R1")),
                    (TraceChainStage.Milestone, milestone),
                    (TraceChainStage.Task, T1),
                    (TraceChainStage.Attempt, Attempt(1)),
                    (TraceChainStage.Change, TraceRef.Commit(CommitB)),
                    (TraceChainStage.Change, TraceRef.Evidence("diff1")),
                    (TraceChainStage.Verification, TraceRef.Evidence("test1")),
                    (TraceChainStage.Verdict, TraceRef.Verdict("v1")),
                    (TraceChainStage.Delivery, TraceRef.Commit(CommitA)),
                ],
                Shape(chain));
            Assert.All(chain.Steps, s => Assert.False(s.Suggested));
            Assert.Equal("Add Calc.Multiply", chain.Steps[0].Title);
            Assert.Equal("Task T1", chain.Steps.Single(s => s.Stage == TraceChainStage.Task).Title);
        }
    }

    [Fact]
    public async Task The_chain_reads_in_both_directions_from_a_task_a_commit_or_the_delivery()
    {
        var (world, milestone) = await StartedAsync();
        using (world)
        {
            await CompleteT1Async(world);
            var reader = Reader(world);
            var whole = Shape(await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct));

            Assert.Equal(whole, Shape(await reader.ChainAsync(world.Project, T1, Ct)));
            Assert.Equal(whole, Shape(await reader.ChainAsync(world.Project, TraceRef.Commit(CommitA), Ct)));
            Assert.Equal(whole, Shape(await reader.ChainAsync(world.Project, TraceRef.Verdict("v1"), Ct)));
            Assert.Equal(whole, Shape(await reader.ChainAsync(world.Project, Attempt(1), Ct)));
            Assert.Equal(whole, Shape(await reader.ChainAsync(world.Project, TraceRef.Commit(CommitB), Ct)));
            Assert.Equal(whole, Shape(await reader.ChainAsync(world.Project, TraceRef.Evidence("test1"), Ct)));
            Assert.Equal(whole, Shape(await reader.ChainAsync(world.Project, TraceRef.Evidence("diff1"), Ct)));
            Assert.Contains((TraceChainStage.Milestone, milestone), whole);
            Assert.DoesNotContain(whole, step => step.Item2 == TraceRef.Requirement("R2") || step.Item2 == TraceRef.Task(WorkWorld.Run1, "T2"));
        }
    }

    [Fact]
    public async Task The_chain_of_a_commit_a_person_linked_to_a_requirement_has_it_as_a_change_and_a_link_retracted_leaves_it()
    {
        var (world, _) = await StartedAsync();
        using (world)
        {
            var link = await world.Trace.RecordAsync(world.Project, TraceRef.Commit(CommitC), TraceRef.Requirement("R2"), TraceAssertedBy.User, "cumple R2", "manuel", Ct);

            var linked = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R2"), Ct);

            Assert.Contains((TraceChainStage.Change, TraceRef.Commit(CommitC)), Shape(linked));
            Assert.All(linked.Steps, s => Assert.False(s.Suggested));

            await world.Trace.RetractAsync(world.Project, link.Id, "manuel", "no era", Ct);
            Assert.DoesNotContain(TraceRef.Commit(CommitC), (await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R2"), Ct)).Steps.Select(s => s.Node));
        }
    }

    [Fact]
    public async Task Several_attempts_are_each_a_step_and_the_chain_of_an_attempt_holds_only_its_own_change_and_verification()
    {
        var (world, _) = await StartedAsync();
        using (world)
        {
            await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
            await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.TestRun, "test1", CommitB);
            await world.LaunchAsync(WorkWorld.Run1, "T1", 2);
            await world.EvidenceAsync(WorkWorld.Run1, "T1", 2, EvidenceKind.TestRun, "test2", CommitC);
            var reader = Reader(world);

            var task = (await reader.ChainAsync(world.Project, T1, Ct)).Steps.Select(s => s.Node).ToList();
            var second = (await reader.ChainAsync(world.Project, Attempt(2), Ct)).Steps.Select(s => s.Node).ToList();

            Assert.Contains(Attempt(1), task);
            Assert.Contains(Attempt(2), task);
            Assert.Contains(TraceRef.Commit(CommitB), task);
            Assert.Contains(TraceRef.Commit(CommitC), task);
            Assert.Contains(Attempt(2), second);
            Assert.Contains(TraceRef.Commit(CommitC), second);
            Assert.Contains(TraceRef.Evidence("test2"), second);
            Assert.DoesNotContain(Attempt(1), second);
            Assert.DoesNotContain(TraceRef.Commit(CommitB), second);
            Assert.DoesNotContain(TraceRef.Evidence("test1"), second);
            Assert.Contains(T1, second);
        }
    }

    [Fact]
    public async Task A_delivery_with_a_conflict_and_a_verdict_that_is_not_valid_are_not_in_the_chain()
    {
        var (world, _) = await StartedAsync();
        using (world)
        {
            await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
            await world.DeliverAsync(WorkWorld.Run1, conflict: true);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, validInLog: false, validInStore: false, task: "T1");
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, id: "unstored", store: false, task: "T1");

            var chain = await Reader(world).ChainAsync(world.Project, T1, Ct);

            Assert.DoesNotContain(chain.Steps, s => s.Stage is TraceChainStage.Delivery or TraceChainStage.Verdict);
            Assert.Contains(chain.Steps, s => s.Stage == TraceChainStage.Attempt);
        }
    }

    [Fact]
    public async Task The_delivery_hangs_only_off_the_tasks_that_were_delivered_and_a_verdict_of_a_task_only_off_that_task()
    {
        var (world, _) = await StartedAsync();
        using (world)
        {
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
            await world.RecordAsync(WorkWorld.Run1, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(new AgentStudio.Domain.Common.TaskId("T1"), "integration", [], CommitB, [], false) { RunId = WorkWorld.Run1 });
            await world.RecordAsync(WorkWorld.Run1, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(new AgentStudio.Domain.Common.TaskId("T2"), "integration", [], null, ["x.txt"], false) { RunId = WorkWorld.Run1 });
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T2");
            await world.DeliverAsync(WorkWorld.Run1, CommitA);

            var fromDelivery = (await Reader(world).ChainAsync(world.Project, TraceRef.Commit(CommitA), Ct)).Steps.Select(s => s.Node).ToList();
            var fromT1 = (await Reader(world).ChainAsync(world.Project, T1, Ct)).Steps.Select(s => s.Node).ToList();

            Assert.Contains(T1, fromDelivery);
            Assert.DoesNotContain(TraceRef.Task(WorkWorld.Run1, "T2"), fromDelivery);
            Assert.DoesNotContain(TraceRef.Requirement("R2"), fromDelivery);
            Assert.DoesNotContain(TraceRef.Verdict("v1"), fromT1);
            Assert.Contains(TraceRef.Commit(CommitA), fromT1);
        }
    }

    [Fact]
    public async Task A_node_the_log_does_not_know_has_an_empty_chain_and_a_broken_log_is_not_read()
    {
        var (world, _) = await StartedAsync();
        using (world)
        {
            var unknown = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R99"), Ct);
            Assert.Empty(unknown.Steps);

            var broken = new TraceChainReader(new BrokenLogRecorder(world.Instance.Recorder), world.Instance.Requirements, world.Instance.Plans, world.Instance.Verdicts);
            await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => broken.ChainAsync(world.Project, T1, Ct));
        }
    }
}
