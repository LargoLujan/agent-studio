using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/16 D-19 and F9-2, ADR-0038: the board is projected from the verified log with the rules of <see cref="BoardRules"/>, and no card
/// reaches «hecho» or «integrado» without an approved, valid verdict. The plan of the fixtures: T1 covers R1, T2 covers R2.
/// </summary>
public sealed class BoardProjectorTests
{
    private static CancellationToken Ct => WorkWorld.Ct;

    /// <summary>A milestone with the run linked and started.</summary>
    private static async Task<(WorkWorld World, MilestoneId Milestone)> LinkedAsync(bool start = true)
    {
        var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        if (start)
        {
            await world.StartRunAsync();
        }
        else
        {
            await world.MentionUnitAsync(WorkWorld.Run1);
        }

        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", Ct);
        return (world, view.Header.Id);
    }

    private static BoardColumn Column(MilestoneBoard board, string requirement) => board.Requirements.Single(c => c.Id == requirement).Column;

    [Fact]
    public async Task Requirements_no_task_covers_are_in_the_backlog_column_and_the_board_says_which_log_it_describes()
    {
        using var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();

        var board = await world.Board.ProjectAsync(world.Project, view.Header.Id, Ct);

        Assert.All(board.Requirements, c => Assert.Equal(BoardColumn.Backlog, c.Column));
        Assert.Equal(["R1", "R2"], board.Requirements.Select(c => c.Id));
        var log = await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct);
        Assert.Equal((log.Entries.Count, log.HeadHash), (board.LogEntries, board.LogHeadHash));
        Assert.Empty(board.Tasks);
    }

    [Fact]
    public async Task A_run_linked_but_not_started_puts_nothing_on_the_board()
    {
        var (world, milestone) = await LinkedAsync(start: false);
        using (world)
        {
            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.All(board.Requirements, c => Assert.Equal(BoardColumn.Backlog, c.Column));
        }
    }

    [Fact]
    public async Task A_plan_that_was_approved_and_has_not_begun_is_planned_and_a_running_task_is_in_progress()
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            var planned = await world.Board.ProjectAsync(world.Project, milestone, Ct);
            Assert.All(planned.Requirements, c => Assert.Equal((BoardColumn.Planned, WorkWorld.Run1), (c.Column, c.Run)));
            Assert.Equal(BoardColumn.Planned, planned.Tasks.Single(c => c.Id == "T1").Column);

            await world.TransitionAsync(WorkWorld.Run1, "T1", WorkTaskStatus.Pending, WorkTaskStatus.Running);
            var running = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.Equal(BoardColumn.InProgress, Column(running, "R1"));
            Assert.Equal(BoardColumn.Planned, Column(running, "R2"));
            Assert.Equal(BoardColumn.InProgress, running.Tasks.Single(c => c.Id == "T1").Column);
        }
    }

    [Fact]
    public async Task A_task_that_waits_for_the_quota_or_an_answer_says_why()
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.TransitionAsync(WorkWorld.Run1, "T1", WorkTaskStatus.Pending, WorkTaskStatus.Running);
            await world.TransitionAsync(WorkWorld.Run1, "T1", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "RateLimitedUntil(2026-11-12T10:00:00.0000000+00:00)");

            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.Equal(BoardCardReasons.WaitingForQuota, board.Requirements.Single(c => c.Id == "R1").Reason);
            Assert.Equal(BoardCardReasons.WaitingForQuota, board.Tasks.Single(c => c.Id == "T1").Reason);
        }
    }

    [Fact]
    public async Task A_run_that_ended_without_a_verdict_leaves_the_requirement_in_review_and_a_failed_one_returns_it()
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            Assert.Equal(BoardColumn.InReview, Column(await world.Board.ProjectAsync(world.Project, milestone, Ct), "R1"));
        }

        var (failed, failedMilestone) = await LinkedAsync();
        using (failed)
        {
            await failed.FinishAsync(WorkWorld.Run1, RunPhase.Failed);
            var board = await failed.Board.ProjectAsync(failed.Project, failedMilestone, Ct);
            var card = board.Requirements.Single(c => c.Id == "R1");
            Assert.Equal((BoardColumn.Returned, BoardCardReasons.RunFailed), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task An_approved_valid_verdict_that_satisfies_the_requirement_makes_it_done_and_the_delivery_integrates_it()
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved);

            var done = await world.Board.ProjectAsync(world.Project, milestone, Ct);
            Assert.Equal(BoardColumn.Done, Column(done, "R1"));
            Assert.Equal(BoardColumn.Done, done.Tasks.Single(c => c.Id == "T1").Column);

            await world.DeliverAsync(WorkWorld.Run1);
            Assert.Equal(BoardColumn.Integrated, Column(await world.Board.ProjectAsync(world.Project, milestone, Ct), "R1"));
        }
    }

    [Fact]
    public async Task A_verdict_counts_only_for_the_requirements_it_assessed()
    {
        // The rule the chain and the participation share (VerdictReach): a verdict that assessed only R1 says nothing of R2, whatever the last verdict of the run was.
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T2");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, task: "T7", assessing: ["R1"]);

            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.Equal(BoardColumn.Done, Column(board, "R1"));
            Assert.Equal(BoardColumn.InReview, Column(board, "R2"));

            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Rejected, task: "T7", assessing: ["R2"], id: "v2");

            board = await world.Board.ProjectAsync(world.Project, milestone, Ct);
            Assert.Equal(BoardColumn.Done, Column(board, "R1"));
            Assert.Equal(BoardColumn.Returned, Column(board, "R2"));
        }
    }

    [Theory]
    [InlineData(VerdictDecision.ChangesRequested, BoardColumn.Returned)]
    [InlineData(VerdictDecision.Rejected, BoardColumn.Returned)]
    [InlineData(VerdictDecision.EscalateToHuman, BoardColumn.InReview)]
    [InlineData(VerdictDecision.Unresolved, BoardColumn.InReview)]
    public async Task A_verdict_that_is_not_an_approval_never_makes_a_card_done(VerdictDecision decision, BoardColumn expected)
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.DeliverAsync(WorkWorld.Run1);
            await world.VerdictAsync(WorkWorld.Run1, decision);

            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.Equal(expected, Column(board, "R1"));
            Assert.DoesNotContain(board.Requirements.Concat(board.Tasks), c => c.Column is BoardColumn.Done or BoardColumn.Integrated);
        }
    }

    [Theory]
    [InlineData(false, true, "log")]
    [InlineData(true, false, "store")]
    [InlineData(true, true, "missing")]
    public async Task A_verdict_that_is_not_valid_in_the_log_or_in_the_store_or_is_not_stored_is_no_verdict(bool validInLog, bool validInStore, string case_)
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            await world.DeliverAsync(WorkWorld.Run1);
            await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved, validInLog, validInStore, store: case_ != "missing");

            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.Equal(BoardColumn.InReview, Column(board, "R1"));
            Assert.DoesNotContain(board.Requirements.Concat(board.Tasks), c => c.Column is BoardColumn.Done or BoardColumn.Integrated);
        }
    }

    [Fact]
    public async Task A_challenged_verdict_is_set_aside_and_the_card_says_so()
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.RunAndCompleteAsync(WorkWorld.Run1, "T1");
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Succeeded);
            var verdict = await world.VerdictAsync(WorkWorld.Run1, VerdictDecision.Approved);
            await world.RecordAsync(WorkWorld.Run1, DecisionKind.VerdictChallenged, new VerdictChallengedPayload(verdict, "manuel", new EvidenceId("motivos"), new string('e', 64), "deb-20261111-aaaaaa") { RunId = WorkWorld.Run1 });

            var card = (await world.Board.ProjectAsync(world.Project, milestone, Ct)).Requirements.Single(c => c.Id == "R1");

            Assert.Equal((BoardColumn.InReview, BoardCardReasons.Challenged), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task A_follow_up_run_replaces_what_the_run_it_follows_left()
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.FinishAsync(WorkWorld.Run1, RunPhase.Failed);
            const string Follow = "20261112-a1b2c3";
            await world.StartRunAsync(Follow, WorkWorld.Run1);

            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            var card = board.Requirements.Single(c => c.Id == "R1");
            Assert.Equal((BoardColumn.Planned, Follow), (card.Column, card.Run));
            Assert.Equal(2, board.Tasks.Count(t => t.Id == "T1"));
        }
    }

    [Fact]
    public async Task A_milestone_with_problems_has_no_cards_and_a_log_that_does_not_replay_is_not_projected()
    {
        var (world, milestone) = await LinkedAsync();
        using (world)
        {
            await world.TransitionAsync(WorkWorld.Run1, "T1", WorkTaskStatus.Completed, WorkTaskStatus.Running);

            await Assert.ThrowsAsync<InvalidOperationException>(() => world.Board.ProjectAsync(world.Project, milestone, Ct));
        }
    }
}
