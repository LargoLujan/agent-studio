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

    /// <summary>V1 approves the commit X for R1 and R2; after a rework there is another integration, Y, and V2 (rejected, of R2 only) is the last verdict of the run.</summary>
    private static async Task<(WorkWorld World, MilestoneId Milestone)> ReworkedAsync()
    {
        var (world, milestone) = await JudgedRunAsync();
        await world.JudgeAsync(VerdictDecision.Rejected, Unjudged, id: "v2", assessing: ["R2"]);
        return (world, milestone);
    }

    [Fact]
    public async Task The_commit_that_counts_is_the_one_the_verdict_that_decides_the_requirement_saw_and_not_the_last_of_the_run()
    {
        var (world, milestone) = await ReworkedAsync();
        using (world)
        {
            // The pull request carries Y, which only the verdict of R2 saw: R1 was approved on X.
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Unjudged));

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));

            Assert.Equal((BoardColumn.Done, BoardCardReasons.MergedWithUnjudgedChanges), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task And_a_pull_request_with_the_commit_the_deciding_verdict_saw_integrates_it_though_another_verdict_came_after()
    {
        var (world, milestone) = await ReworkedAsync();
        using (world)
        {
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Head));

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));

            Assert.Equal((BoardColumn.Integrated, null), (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task Of_two_pull_requests_of_the_same_run_the_one_with_the_judged_commit_integrates_and_the_other_does_not_take_it_back()
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Unjudged) with { PullRequest = new PullRequestRef(Repository, 13) });
            Assert.Equal(BoardCardReasons.MergedWithUnjudgedChanges, R1(await world.Board.ProjectAsync(world.Project, milestone, Ct)).Reason);

            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Head));

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));
            Assert.Equal((BoardColumn.Integrated, null), (card.Column, card.Reason));
        }
    }

    [Theory]
    [InlineData("9C4DA5D1F2E3A4B5C6D7E8F9A0B1C2D3E4F5A6B7")]
    [InlineData("9c4da5d")]
    public async Task A_head_in_capitals_or_abbreviated_is_not_an_observation_and_never_integrates(string rawHead)
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            await world.ObserveWithHeadAsync(Merged(RemoteMergeMode.Squash, Head), rawHead);

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));

            Assert.Equal((BoardColumn.Done, null), (card.Column, card.Reason));
        }
    }

    [Theory]
    [InlineData(Head, true)]
    [InlineData(Unjudged, false)]
    public async Task A_pull_request_closed_then_reopened_and_then_merged_integrates_only_with_the_judged_head(string mergedHead, bool integrates)
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            var before = Shape(await world.Board.ProjectAsync(world.Project, milestone, Ct));
            await world.ObserveAsync(Open(Head));
            await world.ObserveAsync(Open(Head) with { State = PullRequestState.Closed, UpdatedAt = At.AddMinutes(1) });
            await world.ObserveAsync(Open(Head) with { UpdatedAt = At.AddMinutes(2) });
            Assert.Equal(before, Shape(await world.Board.ProjectAsync(world.Project, milestone, Ct)));

            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, mergedHead));

            var card = R1(await world.Board.ProjectAsync(world.Project, milestone, Ct));
            (BoardColumn Column, string? Reason) expected = integrates ? (BoardColumn.Integrated, null) : (BoardColumn.Done, BoardCardReasons.MergedWithUnjudgedChanges);
            Assert.Equal(expected, (card.Column, card.Reason));
        }
    }

    [Fact]
    public async Task The_card_says_which_pull_request_integrated_it_and_none_when_the_local_delivery_did_or_when_it_is_not_integrated()
    {
        var (world, milestone) = await JudgedRunAsync();
        using (world)
        {
            Assert.Null(R1(await world.Board.ProjectAsync(world.Project, milestone, Ct)).IntegratedByPullRequest);

            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Unjudged) with { PullRequest = new PullRequestRef(Repository, 13) });
            Assert.Null(R1(await world.Board.ProjectAsync(world.Project, milestone, Ct)).IntegratedByPullRequest);

            await world.ObserveAsync(Merged(RemoteMergeMode.Squash, Head));
            Assert.Equal(PullRequest.TraceId, R1(await world.Board.ProjectAsync(world.Project, milestone, Ct)).IntegratedByPullRequest);

            await world.DeliverAsync(Run);
            Assert.Null(R1(await world.Board.ProjectAsync(world.Project, milestone, Ct)).IntegratedByPullRequest);
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
