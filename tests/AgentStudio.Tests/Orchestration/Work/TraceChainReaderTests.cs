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

    private static List<TraceRef> Nodes(TraceChain chain) => [.. chain.Steps.Select(s => s.Node)];

    private static TraceRef T2 => TraceRef.Task(WorkWorld.Run1, "T2");

    /// <summary>A run of two requirements, each with its own completed task.</summary>
    private static async Task<WorkWorld> TwoRequirementsAsync()
    {
        var (world, _) = await StartedAsync();
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
        await world.LaunchAsync(WorkWorld.Run1, "T2", 1, agent: "ejecutor2");
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
        return world;
    }

    [Fact]
    public async Task A_verdict_that_assessed_only_one_requirement_is_not_in_the_chain_of_the_other()
    {
        // Review of an independent check: the verdict of the review task hung from every task of the run, so the chain of R2 showed a rejection that only judged R1.
        using var world = await TwoRequirementsAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, task: "T7", assessing: ["R1"]);
        var reader = Reader(world);

        var fromR1 = Nodes(await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct));
        var fromR2 = Nodes(await reader.ChainAsync(world.Project, TraceRef.Requirement("R2"), Ct));
        var fromT2 = Nodes(await reader.ChainAsync(world.Project, T2, Ct));

        Assert.Contains(TraceRef.Verdict("v1"), fromR1);
        Assert.DoesNotContain(TraceRef.Verdict("v1"), fromR2);
        Assert.DoesNotContain(TraceRef.Verdict("v1"), fromT2);
        Assert.DoesNotContain(T2, fromR1);
        Assert.DoesNotContain(TraceRef.Requirement("R2"), fromR1);
        Assert.Equal(fromR1.ToHashSet(), Nodes(await reader.ChainAsync(world.Project, TraceRef.Verdict("v1"), Ct)).ToHashSet());
    }

    [Fact]
    public async Task A_verdict_with_no_assessments_judges_every_task_of_its_scope_and_one_on_an_ordinary_task_only_that_task()
    {
        using (var whole = await TwoRequirementsAsync())
        {
            await whole.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7", assessing: [], id: "whole");

            Assert.Contains(TraceRef.Verdict("whole"), Nodes(await Reader(whole).ChainAsync(whole.Project, TraceRef.Requirement("R2"), Ct)));
            Assert.Contains(TraceRef.Verdict("whole"), Nodes(await Reader(whole).ChainAsync(whole.Project, TraceRef.Requirement("R1"), Ct)));
        }

        using var world = await TwoRequirementsAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T1", id: "of-t1");
        var reader = Reader(world);

        Assert.DoesNotContain(TraceRef.Verdict("of-t1"), Nodes(await reader.ChainAsync(world.Project, TraceRef.Requirement("R2"), Ct)));
        Assert.Contains(TraceRef.Verdict("of-t1"), Nodes(await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct)));
    }

    [Fact]
    public async Task The_chain_of_a_verdict_that_judged_two_requirements_is_the_same_from_either_of_them_and_from_the_verdict()
    {
        using var world = await TwoRequirementsAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7");
        var reader = Reader(world);

        var fromR1 = Shape(await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct));

        Assert.Equal(fromR1, Shape(await reader.ChainAsync(world.Project, TraceRef.Requirement("R2"), Ct)));
        Assert.Equal(fromR1, Shape(await reader.ChainAsync(world.Project, TraceRef.Verdict("v1"), Ct)));
        Assert.Equal(fromR1, Shape(await reader.ChainAsync(world.Project, T1, Ct)));
        Assert.Equal(fromR1, Shape(await reader.ChainAsync(world.Project, T2, Ct)));
        Assert.Contains((TraceChainStage.Requirement, TraceRef.Requirement("R1")), fromR1);
        Assert.Contains((TraceChainStage.Requirement, TraceRef.Requirement("R2")), fromR1);
        Assert.Contains((TraceChainStage.Attempt, TraceRef.Attempt(WorkWorld.Run1, "T2", 1)), fromR1);
    }

    /// <summary>Two requirements, each with its task, attempt, diff, test run and commit; the integration task merged both and the run was delivered.</summary>
    private static async Task<WorkWorld> DeliveredTwoRequirementsAsync(VerdictDecision decision, IReadOnlyList<string>? assessing, bool deliver)
    {
        var (world, _) = await StartedAsync();
        await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.CodeDiff, "diff1");
        await world.EvidenceAsync(WorkWorld.Run1, "T1", 1, EvidenceKind.TestRun, "test1", CommitB);
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
        await world.LaunchAsync(WorkWorld.Run1, "T2", 1, agent: "ejecutor2");
        await world.EvidenceAsync(WorkWorld.Run1, "T2", 1, EvidenceKind.CodeDiff, "diff2");
        await world.EvidenceAsync(WorkWorld.Run1, "T2", 1, EvidenceKind.TestRun, "test2", CommitC);
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
        await world.VerdictAsync(WorkWorld.Run1, decision, task: "T7", assessing: assessing);
        if (deliver)
        {
            await world.RecordAsync(WorkWorld.Run1, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(AgentStudio.Tests.Domain.TestData.Tid("T3"), "agentstudio/run-1", [CommitB, CommitC], CommitA, [], false) { RunId = WorkWorld.Run1 });
            await world.DeliverAsync(WorkWorld.Run1, CommitA);
        }

        return world;
    }

    private static IEnumerable<TraceRef> EveryNodeOfBothThreads() =>
    [
        TraceRef.Requirement("R1"), TraceRef.Requirement("R2"), T1, T2, Attempt(1), TraceRef.Attempt(WorkWorld.Run1, "T2", 1),
        TraceRef.Commit(CommitB), TraceRef.Commit(CommitC), TraceRef.Evidence("diff1"), TraceRef.Evidence("test1"), TraceRef.Evidence("diff2"), TraceRef.Evidence("test2"),
        TraceRef.Verdict("v1"), TraceRef.Commit(CommitA),
    ];

    [Fact]
    public async Task The_chain_of_two_requirements_that_share_a_verdict_and_a_delivery_is_the_same_from_every_node_it_holds()
    {
        using var world = await DeliveredTwoRequirementsAsync(VerdictDecision.Approved, null, deliver: true);
        var reader = Reader(world);

        var whole = Shape(await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct));

        foreach (var node in EveryNodeOfBothThreads())
        {
            Assert.Equal(whole, Shape(await reader.ChainAsync(world.Project, node, Ct)));
        }

        Assert.Equal(14, whole.Count(step => step.Item1 != TraceChainStage.Milestone));
        Assert.Contains((TraceChainStage.Requirement, TraceRef.Requirement("R2")), whole);
    }

    [Fact]
    public async Task The_chain_of_a_verdict_that_judged_one_requirement_is_the_same_from_every_node_of_that_thread_and_the_other_thread_has_neither_the_verdict_nor_its_nodes()
    {
        using var world = await DeliveredTwoRequirementsAsync(VerdictDecision.Rejected, ["R1"], deliver: false);
        var reader = Reader(world);
        var theOther = new[] { TraceRef.Requirement("R2"), T2, TraceRef.Attempt(WorkWorld.Run1, "T2", 1), TraceRef.Commit(CommitC), TraceRef.Evidence("diff2"), TraceRef.Evidence("test2") };
        var thread = new[] { TraceRef.Requirement("R1"), T1, Attempt(1), TraceRef.Commit(CommitB), TraceRef.Evidence("diff1"), TraceRef.Evidence("test1"), TraceRef.Verdict("v1") };

        var first = Shape(await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct));
        var second = Shape(await reader.ChainAsync(world.Project, TraceRef.Requirement("R2"), Ct));

        foreach (var node in thread)
        {
            Assert.Equal(first, Shape(await reader.ChainAsync(world.Project, node, Ct)));
        }

        foreach (var node in theOther)
        {
            Assert.Equal(second, Shape(await reader.ChainAsync(world.Project, node, Ct)));
        }

        Assert.DoesNotContain(first, step => theOther.Contains(step.Item2));
        Assert.DoesNotContain(second, step => step.Item2 == TraceRef.Verdict("v1") || thread.Contains(step.Item2));
    }

    [Fact]
    public async Task Several_verdicts_are_all_in_the_chain_in_order_and_only_the_last_is_in_force()
    {
        // A retry after a rejection: the board counts the last verdict, so the chain must not present the first as the one that stands.
        using var world = await TwoRequirementsAsync();
        // The ids are in the opposite order to the log on purpose: the verdicts come in the order they were issued, not by id.
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, task: "T7", assessing: ["R1"], id: "z-first");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7", assessing: ["R1"], id: "a-second");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7", assessing: ["R2"], id: "other");

        var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

        var verdicts = chain.Steps.Where(s => s.Stage == TraceChainStage.Verdict).ToList();
        Assert.Equal(["z-first", "a-second"], verdicts.Select(s => s.Node.Id));
        Assert.Equal<TraceVerdictStanding?>([TraceVerdictStanding.Superseded, TraceVerdictStanding.InForce], verdicts.Select(s => s.Standing));
        Assert.DoesNotContain(TraceRef.Verdict("other"), Nodes(chain));
        Assert.All(chain.Steps.Where(s => s.Stage != TraceChainStage.Verdict), s => Assert.Null(s.Standing));
    }

    private static async Task ChallengeAsync(WorkWorld world, string verdict = "v1")
    {
        const string debate = "deb-20261111-aaaaaa";
        await world.RecordAsync(WorkWorld.Run1, DecisionKind.VerdictChallenged, new VerdictChallengedPayload(verdict, "manuel", new AgentStudio.Domain.Common.EvidenceId("motivos"), new string('e', 64), debate) { RunId = WorkWorld.Run1 });
        await world.RecordAsync(debate, DecisionKind.DebateOpened, new DebateOpenedPayload(
            AgentStudio.Domain.Debate.DebateMode.Adversarial, new string('1', 64), new string('2', 64), new string('3', 64), new AgentStudio.Domain.Common.EvidenceId("pregunta"), new string('4', 64),
            AgentStudio.Domain.Debate.DebateSubjectKind.Verdict, verdict, null, [], [], AgentStudio.Domain.Debate.DebateJudgingKind.IndependentJudge, [], [], 3, 600, null, 8, verdict) { RunId = debate });
    }

    [Fact]
    public async Task A_verdict_whose_challenge_closed_with_another_decision_is_shown_revised_and_not_in_force()
    {
        using var world = await TwoRequirementsAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7");
        await ChallengeAsync(world);
        const string debate = "deb-20261111-aaaaaa";
        await world.VerdictAsync(debate, VerdictDecision.Rejected, id: "v2");
        await world.RecordAsync(debate, DecisionKind.DebateClosed, new DebateClosedPayload(AgentStudio.Domain.Debate.DebateOutcome.Resolved, ["v2"], "judge", null, "cerrado") { RunId = debate });

        var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

        Assert.Equal(TraceVerdictStanding.Revised, chain.Steps.Single(s => s.Stage == TraceChainStage.Verdict).Standing);
    }

    [Fact]
    public async Task A_verdict_whose_challenge_closed_confirming_it_is_in_force()
    {
        using var world = await TwoRequirementsAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7");
        await ChallengeAsync(world);
        const string debate = "deb-20261111-aaaaaa";
        await world.VerdictAsync(debate, VerdictDecision.Approved, id: "v2");
        await world.RecordAsync(debate, DecisionKind.DebateClosed, new DebateClosedPayload(AgentStudio.Domain.Debate.DebateOutcome.Resolved, ["v2"], "judge", null, "cerrado") { RunId = debate });

        var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

        Assert.Equal(TraceVerdictStanding.InForce, chain.Steps.Single(s => s.Stage == TraceChainStage.Verdict).Standing);
    }

    [Fact]
    public async Task The_verdict_of_a_follow_up_run_supersedes_that_of_the_run_it_follows_and_a_later_run_without_one_leaves_the_earlier_superseded()
    {
        // As on the board: where several runs cover a requirement, the last one that started decides.
        const string followUp = "20261112-d4e5f6";
        using var world = await TwoRequirementsAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, task: "T7", id: "old");
        await world.StartRunAsync(followUp, follows: WorkWorld.Run1);
        var reader = Reader(world);

        var withoutVerdict = await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);
        Assert.Equal(TraceVerdictStanding.Superseded, withoutVerdict.Steps.Single(s => s.Stage == TraceChainStage.Verdict).Standing);

        await world.VerdictAsync(followUp, VerdictDecision.Approved, task: "T7", id: "new");
        var chain = await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

        Assert.Equal<(string, TraceVerdictStanding?)>([("old", TraceVerdictStanding.Superseded), ("new", TraceVerdictStanding.InForce)], chain.Steps.Where(s => s.Stage == TraceChainStage.Verdict).Select(s => (s.Node.Id, s.Standing)));    }

    [Fact]
    public async Task A_verdict_still_in_force_for_one_requirement_is_not_superseded_by_a_later_one_that_judged_another()
    {
        using var world = await TwoRequirementsAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7", id: "both");
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, task: "T7", assessing: ["R1"], id: "r1-again");

        var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

        var standings = chain.Steps.Where(s => s.Stage == TraceChainStage.Verdict).ToDictionary(s => s.Node.Id, s => s.Standing);
        Assert.Equal(TraceVerdictStanding.InForce, standings["both"]);
        Assert.Equal(TraceVerdictStanding.InForce, standings["r1-again"]);
    }

    [Fact]
    public async Task A_challenged_verdict_that_is_the_last_is_shown_challenged_and_not_in_force()
    {
        using var world = await TwoRequirementsAsync();
        await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7");
        await ChallengeAsync(world);

        var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

        Assert.Equal(TraceVerdictStanding.Challenged, chain.Steps.Single(s => s.Stage == TraceChainStage.Verdict).Standing);
    }

    [Fact]
    public async Task The_delivery_is_only_of_the_completed_tasks_the_integration_merged_not_a_failed_one_nor_a_branch_of_the_graph_it_does_not_depend_on()
    {
        // T3 integrates T1 and T2. T1 failed. T6 is a code task of another branch of the plan (it depends on T4, after the integration) that completed too.
        var (world, _) = await StartedAsync();
        using (world)
        {
            await world.LaunchAsync(WorkWorld.Run1, "T1", 1);
            await world.TransitionAsync(WorkWorld.Run1, "T1", AgentStudio.Domain.Work.WorkTaskStatus.Pending, AgentStudio.Domain.Work.WorkTaskStatus.Running);
            await world.TransitionAsync(WorkWorld.Run1, "T1", AgentStudio.Domain.Work.WorkTaskStatus.Running, AgentStudio.Domain.Work.WorkTaskStatus.Failed);
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T6");
            await world.RecordAsync(WorkWorld.Run1, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(AgentStudio.Tests.Domain.TestData.Tid("T3"), "agentstudio/run-1", [CommitB], CommitC, [], false) { RunId = WorkWorld.Run1 });
            await world.DeliverAsync(WorkWorld.Run1, CommitA);
            var reader = Reader(world);

            var fromDelivery = Nodes(await reader.ChainAsync(world.Project, TraceRef.Commit(CommitA), Ct));

            Assert.Contains(T2, fromDelivery);
            Assert.Contains(TraceRef.Requirement("R2"), fromDelivery);
            Assert.DoesNotContain(T1, fromDelivery);
            Assert.DoesNotContain(TraceRef.Task(WorkWorld.Run1, "T6"), fromDelivery);
            Assert.DoesNotContain(TraceRef.Commit(CommitA), Nodes(await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct)));
            Assert.Contains(TraceRef.Commit(CommitA), Nodes(await reader.ChainAsync(world.Project, TraceRef.Requirement("R2"), Ct)));
        }
    }

    [Fact]
    public async Task An_integration_the_engine_did_not_accept_delivers_nothing()
    {
        // The engine records an integration it did not accept (failed, or its evidence could not be anchored) on the hidden candidate ref, even if it left a commit.
        var (world, _) = await StartedAsync();
        using (world)
        {
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.RecordAsync(WorkWorld.Run1, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(AgentStudio.Tests.Domain.TestData.Tid("T3"), $"refs/agentstudio/{WorkWorld.Run1}/_candidate/T3", [CommitB], CommitC, [], false) { RunId = WorkWorld.Run1 });
            await world.DeliverAsync(WorkWorld.Run1, CommitA);

            var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

            Assert.DoesNotContain(chain.Steps, s => s.Stage == TraceChainStage.Delivery);
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
