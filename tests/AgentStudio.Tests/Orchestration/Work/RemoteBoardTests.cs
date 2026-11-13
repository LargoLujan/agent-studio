using AgentStudio.Application.Work;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work.Milestones;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;
using static AgentStudio.Tests.Orchestration.WorkTracking.RemoteTestSupport;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/21 D-29 and F10-8: «integrado» by pull request needs the merged head to be the commit the judge saw; otherwise the card stays in «hecho» with its reason. The rest
/// of the board is what it was: projects with no remote, and a log with remote entries that do not validate, project as in Phase 9.
/// </summary>
public sealed class RemoteBoardTests
{
    private const string Unjudged = "8888888888888888888888888888888888888888";

    private const string Run = WorkWorld.Run1;

    private static CancellationToken Ct => WorkWorld.Ct;

    private static BoardCard R1(MilestoneBoard board) => board.Requirements.Single(c => c.Id == "R1");

    [Fact]
    public async Task A_pull_request_merged_with_the_commit_the_judge_saw_integrates_the_requirement()
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            Assert.Equal(BoardColumn.Done, R1(await world.Board.ProjectAsync(world.Project, milestone, Ct)).Column);

            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Head));

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));
            Assert.Equal((BoardColumn.Integrated, null), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task A_pull_request_merged_with_another_head_leaves_the_card_in_done_and_says_why()
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Unjudged));

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));

            Assert.Equal((BoardColumn.Done, BoardCardReasons.MergedWithUnjudgedChanges), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task An_open_pull_request_and_a_closed_one_change_nothing_on_the_board()
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            var before = Shape(await world.Board.ProjectAsync(world.Project, milestone, Ct));

            await world.ObserveAsync(Open(Head));
            Assert.Equal(before, Shape(await world.Board.ProjectAsync(world.Project, milestone, Ct)));

            await world.ObserveAsync(Open(Head) with { PullRequest = new PullRequestRef(Repository, 13), State = PullRequestState.Closed, UpdatedAt = At.AddMinutes(9) });
            Assert.Equal(before, Shape(await world.Board.ProjectAsync(world.Project, milestone, Ct)));
        }
    }

    [Fact]
    public async Task The_local_delivery_still_integrates_whatever_the_pull_request_was_merged_with()
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Unjudged));
            await world.DeliverAsync(Run);

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));

            Assert.Equal((BoardColumn.Integrated, null), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task A_revert_that_is_observed_does_not_take_integrated_away()
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Head));
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Head) with { RevertCommits = [Revert] }, Run, RemoteChangeKind.RevertSuspected);

            Assert.Equal(BoardColumn.Integrated, R1(await world.Board.ProjectAsync(world.Project, milestone, Ct)).Column);
        }
    }

    [Fact]
    public async Task A_merged_pull_request_of_a_run_nobody_judged_integrates_nothing()
    {
        var world = await WorkWorld.OpenAsync();
        using (world)
        {
            var view = await world.OpenMilestoneAsync();
            await world.StartRunAsync();
            await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, Run), "manuel", Ct);
            await world.RunAndCompleteAsync(Run, "T1");
            await world.FinishAsync(Run, AgentStudio.Application.Orchestration.RunPhase.Succeeded);
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Head));

            var board = await world.Board.ProjectAsync(world.Project, view.Header.Id, Ct);

            Assert.Equal(BoardColumn.InReview, R1(board).Column);
            Assert.DoesNotContain(board.Requirements.Concat(board.Tasks), c => c.Column == BoardColumn.Integrated);
        }
    }

    [Theory]
    [InlineData(VerdictDecision.Approved, true)]
    [InlineData(VerdictDecision.ChangesRequested, false)]
    [InlineData(VerdictDecision.Rejected, false)]
    [InlineData(VerdictDecision.EscalateToHuman, false)]
    [InlineData(VerdictDecision.Unresolved, false)]
    public async Task No_card_is_integrated_by_a_pull_request_without_an_approved_verdict_of_that_very_commit(VerdictDecision decision, bool integrated)
    {
        var (world, milestone) = await JudgedRunAsync(decision);
        using (world)
        {
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Head));

            var board = await world.Board.ProjectAsync(world.Project, milestone, Ct);

            Assert.Equal(integrated, board.Requirements.Concat(board.Tasks).Any(c => c.Column == BoardColumn.Integrated));
        }
    }

    [Fact]
    public async Task A_remote_entry_that_does_not_validate_is_left_out_and_the_board_goes_on()
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            await world.ObserveWithAWrongFingerprintAsync(Merged(RemoteMergeMode.Squash, Head));

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));

            Assert.Equal((BoardColumn.Done, null), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task Repeated_and_reordered_observations_give_the_same_board()
    {
        var opened = Open(Head);
        var merged = Merged(RemoteMergeMode.Squash, Head);
        var reverted = merged with { RevertCommits = [Revert] };
        var deployed = reverted with { Deployment = new RemoteDeployment(9, Result, DeploymentState.Succeeded) };
        var orders = new[]
        {
            new[] { opened, merged, reverted, deployed },
            new[] { deployed, reverted, merged, opened },
            new[] { merged, merged, opened, deployed, opened, reverted, deployed },
            new[] { opened, deployed, merged },
        };

        List<(string Id, BoardColumn Column, string? Reason)>? expected = null;
        foreach (var order in orders)
        {
            var (world, milestone) = await JudgedRunAsync();
            using (world)
            {
                foreach (var snapshot in order)
                {
                    await world.ObserveAsync(snapshot);
                }

                var shape = Shape(await world.Board.ProjectAsync(world.Project, milestone, Ct));
                expected ??= shape;
                Assert.Equal(expected, shape);
                Assert.Equal(BoardColumn.Integrated, shape.Single(c => c.Id == "R1").Column);
            }
        }
    }
}
