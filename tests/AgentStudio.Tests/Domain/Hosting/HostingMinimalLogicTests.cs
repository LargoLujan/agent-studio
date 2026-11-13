using AgentStudio.Domain.Hosting;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;

namespace AgentStudio.Tests.Domain.Hosting;

/// <summary>
/// The minimal logic that the P0 of Phase 10 gives to the two pieces that WP3 (<c>RemoteReconciler</c>) and WP4 (<c>CommitSuccession</c>) complete: the shape and
/// the common cases, so that every package compiles and passes its own tests against something that behaves. Their own test files are those packages'.
/// </summary>
public sealed class HostingMinimalLogicTests
{
    // ---- the reconciler ----

    [Fact]
    public void The_first_observation_opens_and_the_same_fingerprint_is_no_change()
    {
        var first = RemoteReconciler.Reconcile(null, Open());
        var again = RemoteReconciler.Reconcile(Open(), Open() with { UpdatedAt = At.AddHours(1) });

        Assert.False(first.Discarded);
        Assert.Equal([RemoteChangeKind.Opened], first.Changes);
        Assert.False(again.Discarded);
        Assert.Empty(again.Changes);
    }

    [Fact]
    public void A_merge_a_moved_head_and_a_revert_are_the_changes_between_two_snapshots()
    {
        var merged = Merged() with { RevertCommits = [Revert], ForcePushes = 1, HeadCommit = Original1 };

        var result = RemoteReconciler.Reconcile(Open(), merged);

        Assert.False(result.Discarded);
        Assert.Equal(
            [RemoteChangeKind.Merged, RemoteChangeKind.HeadMoved, RemoteChangeKind.ForcePushed, RemoteChangeKind.HeadBranchDeleted, RemoteChangeKind.ReviewsChanged, RemoteChangeKind.RevertSuspected],
            result.Changes);
    }

    [Fact]
    public void Closed_and_reopened_are_read_from_the_state()
    {
        var closed = Open() with { State = PullRequestState.Closed, UpdatedAt = At.AddMinutes(1) };
        var reopened = Open() with { UpdatedAt = At.AddMinutes(2) };

        Assert.Equal([RemoteChangeKind.Closed], RemoteReconciler.Reconcile(Open(), closed).Changes);
        Assert.Equal([RemoteChangeKind.Reopened], RemoteReconciler.Reconcile(closed, reopened).Changes);
    }

    [Fact]
    public void An_older_observation_and_one_that_goes_back_in_what_only_grows_are_discarded()
    {
        var current = Merged() with { ForcePushes = 2, RevertCommits = [Revert], Deployment = new RemoteDeployment(9, Result, DeploymentState.Succeeded) };

        Assert.True(RemoteReconciler.Reconcile(current, current with { HeadCommit = Original1, UpdatedAt = At }).Discarded);
        Assert.True(RemoteReconciler.Reconcile(current, Open() with { UpdatedAt = At.AddHours(1) }).Discarded, "Merged is terminal.");
        Assert.True(RemoteReconciler.Reconcile(current, current with { ForcePushes = 1, UpdatedAt = At.AddHours(1) }).Discarded);
        Assert.True(RemoteReconciler.Reconcile(current, current with { RevertCommits = [], UpdatedAt = At.AddHours(1) }).Discarded);
        Assert.True(RemoteReconciler.Reconcile(current, current with { Deployment = new RemoteDeployment(8, Result, DeploymentState.Succeeded), UpdatedAt = At.AddHours(1) }).Discarded);
        Assert.Empty(RemoteReconciler.Reconcile(current, current with { HeadCommit = Original1, UpdatedAt = At }).Changes);
    }

    [Fact]
    public void A_change_of_checks_and_a_deployment_are_reported()
    {
        var changed = Open() with { Checks = new ChecksSummary(ChecksRollup.Failing, 2, 1, 0, false), Deployment = new RemoteDeployment(1, Head, DeploymentState.Pending), UpdatedAt = At.AddMinutes(1) };

        Assert.Equal([RemoteChangeKind.ChecksChanged, RemoteChangeKind.DeploymentChanged], RemoteReconciler.Reconcile(Open(), changed).Changes);
    }

    // ---- the succession of commits ----

    [Fact]
    public void A_squash_leaves_one_result_for_every_original_and_one_result_comes_from_all_of_them()
    {
        var succession = CommitSuccession.From([Merged(RemoteMergeMode.Squash)], pull => Head);

        var successor = succession.SuccessorOf(Original1)!;
        Assert.Equal((PullRequest, RemoteMergeMode.Squash, true), (successor.PullRequest, successor.Mode, successor.HeadWasJudgedCommit));
        Assert.Equal([Result], successor.ResultCommits);
        Assert.Equal([Result], succession.SuccessorOf(Original2)!.ResultCommits);
        Assert.Equal([Original1, Original2], succession.OriginalsOf(Result));
        Assert.Null(succession.SuccessorOf(Revert));
        Assert.Empty(succession.OriginalsOf(Revert));
    }

    [Fact]
    public void A_rebase_pairs_the_originals_with_the_results_in_order_when_the_counts_match()
    {
        var second = "5555555555555555555555555555555555555555";
        var rebased = Merged(RemoteMergeMode.Rebase) with { ResultCommits = [Result, second], MergeCommit = second };

        var succession = CommitSuccession.From([rebased], _ => null);

        Assert.Equal([Result], succession.SuccessorOf(Original1)!.ResultCommits);
        Assert.Equal([second], succession.SuccessorOf(Original2)!.ResultCommits);
        Assert.Equal([Original2], succession.OriginalsOf(second));
        Assert.False(succession.SuccessorOf(Original1)!.HeadWasJudgedCommit, "The log did not say which commit the judge saw.");
    }

    [Fact]
    public void A_head_that_is_not_the_judged_commit_is_said_and_a_revert_is_listed_and_open_or_closed_pull_requests_succeed_nothing()
    {
        var merged = Merged(head: Original2) with { RevertCommits = [Revert] };

        var succession = CommitSuccession.From([merged, Open()], _ => Head);

        var successor = succession.SuccessorOf(Original1)!;
        Assert.False(successor.HeadWasJudgedCommit);
        Assert.Equal([Revert], successor.RevertedBy);
        Assert.Null(CommitSuccession.From([Open()], _ => Head).SuccessorOf(Original1));
        Assert.Null(CommitSuccession.Empty.SuccessorOf(Original1));
    }

    [Fact]
    public void Of_several_observations_of_the_same_pull_request_the_latest_counts()
    {
        var early = Merged(RemoteMergeMode.Unknown) with { RevertCommits = [] };
        var late = Merged(RemoteMergeMode.Squash) with { RevertCommits = [Revert], UpdatedAt = At.AddHours(2) };

        var succession = CommitSuccession.From([late, early], _ => Head);

        Assert.Equal(RemoteMergeMode.Squash, succession.SuccessorOf(Original1)!.Mode);
        Assert.Equal([Revert], succession.SuccessorOf(Original1)!.RevertedBy);
    }
}
