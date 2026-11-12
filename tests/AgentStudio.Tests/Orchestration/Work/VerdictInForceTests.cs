using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// The rule of the verdict in force (VerdictReach, docs/04 §23.6, ADR-0039) as the cards of tasks follow it, and its limits: the cards of tasks and of requirements never
/// contradict each other, a cycle in <c>Supersedes</c> never brings the process down, a judge's correction counts as part of a revision only if it came after the challenge,
/// and a delivery marks as delivered only what had been completed when it was made. Every verdict of a revision is written as the product writes it
/// (<see cref="WorkWorld.RevisionVerdictAsync"/>).
/// </summary>
public sealed class VerdictInForceTests
{
    private static CancellationToken Ct => WorkWorld.Ct;

    /// <summary>A milestone with a run whose T1 (R1) and T2 (R2) completed, and the run finished.</summary>
    private static async Task<(WorkWorld World, MilestoneId Milestone)> CompletedRunAsync()
    {
        var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
        await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
        await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
        return (world, view.Header.Id);
    }

    /// <summary>A person challenges the verdict and its revision debate opens (and does not close).</summary>
    private static async Task ChallengeAsync(WorkWorld world, string verdict, string debate)
    {
        await world.RecordAsync(WorkWorld.Run1, DecisionKind.VerdictChallenged, new VerdictChallengedPayload(verdict, "manuel", new EvidenceId("motivos"), new string('e', 64), debate) { RunId = WorkWorld.Run1 });
        await world.RecordAsync(debate, DecisionKind.DebateOpened, new DebateOpenedPayload(
            DebateMode.Adversarial, new string('1', 64), new string('2', 64), Dossier().Requirements.ContentHash, new EvidenceId("pregunta"), new string('4', 64),
            DebateSubjectKind.Verdict, verdict, null, [], [], DebateJudgingKind.IndependentJudge, [], [], 3, 600, null, 8, verdict) { RunId = debate });
    }

    private static async Task<MilestoneBoard> BoardAsync(WorkWorld world, MilestoneId milestone) => await world.Board.ProjectAsync(world.Project, milestone, Ct);

    private static BoardColumn TaskColumn(MilestoneBoard board, string id) => board.Tasks.Single(c => c.Id == id).Column;

    private static BoardColumn RequirementColumn(MilestoneBoard board, string id) => board.Requirements.Single(c => c.Id == id).Column;

    // ---- 1. The cards of tasks follow the verdict that judged them ----

    [Fact]
    public async Task A_later_challenged_verdict_leaves_the_tasks_in_review_as_it_leaves_the_requirement()
    {
        var (world, milestone) = await CompletedRunAsync();
        using (world)
        {
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, id: "v2");
            await ChallengeAsync(world, "v2", "deb-20261111-aaaaaa");

            var board = await BoardAsync(world, milestone);

            Assert.Equal(BoardColumn.InReview, RequirementColumn(board, "R1"));
            Assert.Equal(BoardColumn.InReview, TaskColumn(board, "T1"));
            Assert.Equal(BoardColumn.InReview, TaskColumn(board, "T2"));
        }
    }

    [Fact]
    public async Task A_verdict_issued_on_one_task_does_not_leave_the_other_done()
    {
        var (world, milestone) = await CompletedRunAsync();
        using (world)
        {
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T1", assessing: ["R1"]);

            var board = await BoardAsync(world, milestone);

            Assert.Equal(BoardColumn.Done, TaskColumn(board, "T1"));
            Assert.Equal(BoardColumn.InReview, TaskColumn(board, "T2"));
        }
    }

    [Fact]
    public async Task Each_task_follows_the_last_verdict_that_judged_it()
    {
        var (world, milestone) = await CompletedRunAsync();
        using (world)
        {
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, id: "v2", task: "T1", assessing: ["R1"]);

            var board = await BoardAsync(world, milestone);

            Assert.Equal(BoardColumn.Returned, TaskColumn(board, "T1"));
            Assert.Equal(BoardColumn.Done, TaskColumn(board, "T2"));
            Assert.Equal(BoardColumn.Returned, RequirementColumn(board, "R1"));
            Assert.Equal(BoardColumn.Done, RequirementColumn(board, "R2"));
        }
    }

    // ---- 2. A cycle in Supersedes ----

    /// <summary>A store that holds exactly these verdicts: the real one refuses a cycle (a foreign key), so a corrupt store is simulated.</summary>
    private sealed class FixedVerdicts(IReadOnlyList<StoredVerdict> all) : IVerdictStore
    {
        public Task<string> AppendAsync(string projectId, StoredVerdict verdict, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<StoredVerdict>> ListAsync(string projectId, string? runId, CancellationToken cancellationToken) => Task.FromResult(all);
    }

    private static StoredVerdict Stored(string id, string supersedes) =>
        new(id, ValidVerdict(Dossier(), VerdictDecision.Approved), WorkWorld.Run1, "T7", AuditScope.CurrentPlan, true, [], supersedes);

    private static BoardProjector BoardOver(WorkWorld world, IVerdictStore store) =>
        new(world.Instance.Recorder, world.Milestones, world.Backlog, world.Instance.Requirements, world.Instance.Runs, world.Instance.Plans, store);

    [Fact]
    public async Task A_cycle_of_challenged_verdicts_that_supersede_each_other_does_not_overflow_the_stack()
    {
        var (world, milestone) = await CompletedRunAsync();
        using (world)
        {
            // Both are announced after both challenges, so that the order of the log does not stop the walk before it comes back to where it started.
            await ChallengeAsync(world, "v1", "deb-20261111-aaaaaa");
            await ChallengeAsync(world, "v2", "deb-20261111-bbbbbb");
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, store: false, supersedes: "v2");
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, id: "v2", store: false, supersedes: "v1");

            var board = await BoardOver(world, new FixedVerdicts([Stored("v1", "v2"), Stored("v2", "v1")])).ProjectAsync(world.Project, milestone, Ct);

            Assert.NotEqual(BoardColumn.Done, RequirementColumn(board, "R1"));
        }
    }

    [Fact]
    public async Task A_challenged_verdict_that_supersedes_itself_does_not_loop()
    {
        var (world, milestone) = await CompletedRunAsync();
        using (world)
        {
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, store: false, supersedes: "v1");
            await ChallengeAsync(world, "v1", "deb-20261111-aaaaaa");

            var board = await BoardOver(world, new FixedVerdicts([Stored("v1", "v1")])).ProjectAsync(world.Project, milestone, Ct);

            Assert.Equal(BoardColumn.InReview, RequirementColumn(board, "R1"));
        }
    }

    // ---- 3. The order of a judge's correction and the challenge ----

    [Fact]
    public async Task A_correction_of_the_judge_issued_before_the_challenge_replaces_the_verdict_but_the_open_challenge_of_the_replaced_one_still_holds_the_card()
    {
        var (world, milestone) = await CompletedRunAsync();
        using (world)
        {
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, id: "v1b", supersedes: "v1");
            await ChallengeAsync(world, "v1", "deb-20261111-aaaaaa");

            var board = await BoardAsync(world, milestone);

            // The correction is not a verdict of the revision (it was issued before the challenge, so it competes as usual), but the delivery stops for the open revision of the attempt
            // it replaced, since both are about the same work, and so does the card (docs/04 §23.6).
            Assert.Equal(BoardColumn.InReview, RequirementColumn(board, "R1"));
            Assert.Equal(BoardColumn.InReview, TaskColumn(board, "T1"));
        }
    }

    [Fact]
    public async Task A_correction_of_the_judge_issued_after_the_challenge_is_of_the_revision_and_waits_for_it()
    {
        var (world, milestone) = await CompletedRunAsync();
        using (world)
        {
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected);
            await ChallengeAsync(world, "v1", "deb-20261111-aaaaaa");
            await world.RevisionVerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, "v1", id: "v1b");

            var board = await BoardAsync(world, milestone);

            Assert.Equal(BoardColumn.InReview, RequirementColumn(board, "R1"));
            Assert.Equal(BoardColumn.InReview, TaskColumn(board, "T1"));
        }
    }

    // ---- 5. The chain: a delivery delivers what was completed when it was made ----

    [Fact]
    public async Task A_delivery_is_not_in_the_chain_of_a_task_completed_after_it()
    {
        var world = await WorkWorld.OpenAsync();
        using (world)
        {
            var view = await world.OpenMilestoneAsync();
            await world.StartRunAsync();
            await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            var commit = new string('a', 40);
            await world.DeliverAsync(WorkWorld.Run1, commit);
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
            var reader = new TraceChainReader(world.Instance.Recorder, world.Instance.Requirements, world.Instance.Plans, world.Instance.Verdicts);

            var fromR1 = await reader.ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);
            var fromR2 = await reader.ChainAsync(world.Project, TraceRef.Requirement("R2"), Ct);

            Assert.Contains((TraceChainStage.Delivery, TraceRef.Commit(commit)), fromR1.Steps.Select(s => (s.Stage, s.Node)));
            Assert.DoesNotContain(fromR2.Steps, s => s.Stage == TraceChainStage.Delivery);
        }
    }
}
