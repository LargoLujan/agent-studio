using AgentStudio.Domain.Hosting;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;

namespace AgentStudio.Tests.Domain.Hosting;

/// <summary>
/// docs/21 D-28 and D-29, F10-7 and the projection part of F10-8: the succession of commits through a squash, a rebase, a merge commit and a revert; the cases the
/// observation cannot decide (a truncated list, an unknown mode); the rule of «integrado» in its one place; and that none of it depends on the order or the repetition
/// of the observations.
/// </summary>
public sealed class CommitSuccessionTests
{
    private const string Second = "5555555555555555555555555555555555555555";
    private const string Third = "6666666666666666666666666666666666666666";
    private const string ResultTwo = "7777777777777777777777777777777777777777";
    private const string Other = "8888888888888888888888888888888888888888";

    private static PullRequestRef Pr(int number) => new(Repository, number);

    private static RemoteSnapshot MergedPr(int number, IReadOnlyList<string> originals, IReadOnlyList<string> results, RemoteMergeMode mode, DateTimeOffset? at = null) =>
        Merged(mode) with { PullRequest = Pr(number), OriginalCommits = originals, ResultCommits = results, MergeCommit = results.Count > 0 ? results[^1] : null, UpdatedAt = at ?? At.AddMinutes(5) };

    // ---- the cases ----

    [Fact]
    public void A_merge_commit_leaves_the_merge_commit_and_the_originals_come_to_it()
    {
        var snapshot = Merged(RemoteMergeMode.MergeCommit);

        var succession = CommitSuccession.From([snapshot], _ => Head);

        var successor = succession.SuccessorOf(Original1)!;
        Assert.Equal(RemoteMergeMode.MergeCommit, successor.Mode);
        Assert.Equal([Result], successor.ResultCommits);
        Assert.Equal([Original1, Original2], succession.OriginalsOf(Result));
        Assert.True(successor.HeadWasJudgedCommit);
    }

    [Fact]
    public void A_revert_is_listed_with_the_successor_and_is_always_a_deduction_of_the_message()
    {
        var snapshot = Merged(RemoteMergeMode.Squash) with { RevertCommits = [Revert] };

        var successor = CommitSuccession.From([snapshot], _ => Head).SuccessorOf(Original1)!;

        Assert.Equal([Revert], successor.RevertedBy);
        Assert.Equal(Result, successor.ResultCommits.Single());
    }

    [Fact]
    public void A_mode_is_a_fact_only_when_the_log_says_the_app_merged_with_that_method()
    {
        var snapshot = Merged(RemoteMergeMode.Squash);

        var unknown = CommitSuccession.From([snapshot], _ => Head).SuccessorOf(Original1)!;
        var byApp = CommitSuccession.From([snapshot], _ => Head, _ => MergeMethod.Squash).SuccessorOf(Original1)!;
        var otherMethod = CommitSuccession.From([snapshot], _ => Head, _ => MergeMethod.Rebase).SuccessorOf(Original1)!;

        Assert.True(unknown.ModeIsDeduced, "A merge made outside is deduced from the commits (D-27).");
        Assert.False(byApp.ModeIsDeduced);
        Assert.True(otherMethod.ModeIsDeduced, "The platform said another mode than the one the app asked for: it is not shown as a fact.");
    }

    [Theory]
    [InlineData(RemoteMergeMode.SquashOrRebase)]
    [InlineData(RemoteMergeMode.Unknown)]
    public void An_ambiguous_or_unknown_mode_is_never_a_fact_whatever_the_log_says(RemoteMergeMode mode)
    {
        var snapshot = Merged(mode);

        var successor = CommitSuccession.From([snapshot], _ => Head, _ => MergeMethod.Squash).SuccessorOf(Original1)!;

        Assert.True(successor.ModeIsDeduced);
        Assert.Equal(mode, successor.Mode);
    }

    [Fact]
    public void An_unknown_mode_does_not_pair_the_commits_since_nothing_says_which_is_which()
    {
        var snapshot = MergedPr(12, [Original1, Original2], [Result, ResultTwo], RemoteMergeMode.Unknown);

        var succession = CommitSuccession.From([snapshot], _ => null);

        Assert.Equal([Result, ResultTwo], succession.SuccessorOf(Original1)!.ResultCommits);
        Assert.Equal([Result, ResultTwo], succession.SuccessorOf(Original2)!.ResultCommits);
        Assert.Equal([Original1, Original2], succession.OriginalsOf(Result));
    }

    [Fact]
    public void A_rebase_whose_counts_do_not_match_keeps_the_whole_list_and_a_truncated_one_is_not_paired_and_is_said()
    {
        var uneven = MergedPr(12, [Original1, Original2, Third], [Result, ResultTwo], RemoteMergeMode.Rebase);
        var truncated = MergedPr(13, [Original1, Original2], [Result, ResultTwo], RemoteMergeMode.Rebase) with { CommitsTruncated = true };

        var one = CommitSuccession.From([uneven], _ => null);
        var two = CommitSuccession.From([truncated], _ => null);

        Assert.Equal([Result, ResultTwo], one.SuccessorOf(Original1)!.ResultCommits);
        Assert.False(one.SuccessorOf(Original1)!.CommitsTruncated);
        Assert.False(one.OriginalsAreTruncated(Result));
        Assert.Equal([Result, ResultTwo], two.SuccessorOf(Original1)!.ResultCommits);
        Assert.True(two.SuccessorOf(Original1)!.CommitsTruncated);
        Assert.True(two.OriginalsAreTruncated(Result));
        Assert.Equal([Original1, Original2], two.OriginalsOf(ResultTwo));
    }

    [Fact]
    public void A_merged_pull_request_with_no_result_known_still_says_what_it_is()
    {
        var snapshot = MergedPr(12, [Original1], [], RemoteMergeMode.Unknown) with { MergeCommit = null };

        var succession = CommitSuccession.From([snapshot], _ => null);

        var successor = succession.SuccessorOf(Original1)!;
        Assert.Empty(successor.ResultCommits);
        Assert.Equal(Pr(12), succession.OfPullRequest(Pr(12))!.PullRequest);
    }

    [Fact]
    public void A_pull_request_that_was_not_merged_has_no_successor_and_is_not_asked_for()
    {
        var succession = CommitSuccession.From([Open(), Open() with { PullRequest = Pr(13), State = PullRequestState.Closed }], _ => Head);

        Assert.Null(succession.SuccessorOf(Original1));
        Assert.Null(succession.OfPullRequest(Pr(12)));
        Assert.Null(succession.OfPullRequest(Pr(13)));
    }

    [Fact]
    public void When_two_merged_pull_requests_name_the_same_commit_the_one_merged_later_wins_whatever_the_order()
    {
        var early = MergedPr(12, [Original1], [Result], RemoteMergeMode.Squash, At.AddMinutes(5));
        var late = MergedPr(13, [Original1, Second], [ResultTwo], RemoteMergeMode.Squash, At.AddHours(1));

        var one = CommitSuccession.From([early, late], _ => null).SuccessorOf(Original1)!;
        var other = CommitSuccession.From([late, early], _ => null).SuccessorOf(Original1)!;

        Assert.Equal(Pr(13), one.PullRequest);
        Assert.Equal(one.PullRequest, other.PullRequest);
        Assert.Equal([ResultTwo], other.ResultCommits);
    }

    // ---- the rule of «integrado» and «desplegado», in one place ----

    [Fact]
    public void A_merged_pull_request_integrates_only_when_its_head_is_the_commit_the_judge_saw()
    {
        var merged = Merged(RemoteMergeMode.Squash);

        Assert.Equal(PullRequestIntegration.Integrated, IntegrationByPullRequest.Of(merged, Head));
        Assert.Equal(PullRequestIntegration.MergedWithUnjudgedChanges, IntegrationByPullRequest.Of(merged, Head.ToUpperInvariant()));
        Assert.Equal(PullRequestIntegration.MergedWithUnjudgedChanges, IntegrationByPullRequest.Of(merged, Head[..7]));
        Assert.Equal(PullRequestIntegration.MergedWithUnjudgedChanges, IntegrationByPullRequest.Of(merged, Other));
        Assert.Equal(PullRequestIntegration.MergedWithUnjudgedChanges, IntegrationByPullRequest.Of(merged, null));
        Assert.Equal(PullRequestIntegration.NotMerged, IntegrationByPullRequest.Of(Open(), Head));
        Assert.Equal(PullRequestIntegration.NotMerged, IntegrationByPullRequest.Of(Open() with { State = PullRequestState.Closed }, Head));
    }

    [Fact]
    public void A_commit_that_differs_from_the_head_only_in_case_or_in_length_does_not_integrate()
    {
        // The comparison is exact: it is what IntegrationByPullRequest.Of reaches once the head is a valid (lowercase, full) one.
        var merged = Merged(RemoteMergeMode.Squash);
        var differsInCase = Head[..1] + char.ToUpperInvariant(Head[1]) + Head[2..];

        Assert.NotEqual(Head, differsInCase);
        Assert.Equal(PullRequestIntegration.MergedWithUnjudgedChanges, IntegrationByPullRequest.Of(merged, differsInCase));
        Assert.Equal(PullRequestIntegration.MergedWithUnjudgedChanges, IntegrationByPullRequest.Of(merged, Head[..39]));
        Assert.False(IntegrationByPullRequest.HeadIsJudgedCommit(merged, Head.ToUpperInvariant()));
        Assert.True(IntegrationByPullRequest.HeadIsJudgedCommit(merged, Head));
    }

    [Fact]
    public void A_revert_does_not_change_what_a_merge_counts_for()
    {
        var reverted = Merged(RemoteMergeMode.Squash) with { RevertCommits = [Revert] };

        Assert.Equal(PullRequestIntegration.Integrated, IntegrationByPullRequest.Of(reverted, Head));
    }

    [Fact]
    public void Deployed_exists_only_with_a_successful_deployment_of_a_commit_the_merge_left()
    {
        var merged = Merged(RemoteMergeMode.Squash);

        Assert.False(IntegrationByPullRequest.IsDeployed(merged));
        Assert.True(IntegrationByPullRequest.IsDeployed(merged with { Deployment = new RemoteDeployment(5, Result, DeploymentState.Succeeded) }));
        Assert.False(IntegrationByPullRequest.IsDeployed(merged with { Deployment = new RemoteDeployment(5, Result, DeploymentState.Failed) }));
        Assert.False(IntegrationByPullRequest.IsDeployed(merged with { Deployment = new RemoteDeployment(5, Result, DeploymentState.Pending) }));
        Assert.False(IntegrationByPullRequest.IsDeployed(merged with { Deployment = new RemoteDeployment(5, Other, DeploymentState.Succeeded) }), "A commit the merge did not leave.");
        Assert.False(IntegrationByPullRequest.IsDeployed(Open() with { Deployment = new RemoteDeployment(5, Head, DeploymentState.Succeeded) }));
    }

    // ---- idempotency and order ----

    private static List<RemoteSnapshot> Series()
    {
        var opened = Open();
        var checks = opened with { Checks = new ChecksSummary(ChecksRollup.Failing, 2, 1, 0, false), UpdatedAt = At.AddMinutes(1) };
        var merged = Merged(RemoteMergeMode.Squash);
        var reverted = merged with { RevertCommits = [Revert] };
        var deployed = reverted with { Deployment = new RemoteDeployment(9, Result, DeploymentState.Succeeded) };
        return [opened, checks, merged, reverted, deployed];
    }

    private static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, index) => index != i).ToList();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    [Fact]
    public void The_final_observation_is_the_same_for_every_order_and_every_repetition()
    {
        var series = Series();
        var expected = series[^1].Fingerprint;

        foreach (var order in Permutations(series))
        {
            Assert.Equal(expected, RemoteSnapshotOrder.Latest(order).Fingerprint);
            Assert.Equal(expected, RemoteSnapshotOrder.Latest([.. order, .. order]).Fingerprint);
        }
    }

    [Fact]
    public void A_pull_request_reopened_after_it_was_closed_is_open_whatever_the_order_the_observations_come_in()
    {
        var opened = Open();
        var closed = Open() with { State = PullRequestState.Closed, UpdatedAt = At.AddMinutes(1) };
        var reopened = Open() with { UpdatedAt = At.AddMinutes(2), Checks = new ChecksSummary(ChecksRollup.Pending, 0, 0, 1, false) };

        foreach (var order in Permutations([opened, closed, reopened]))
        {
            Assert.Equal(reopened.Fingerprint, RemoteSnapshotOrder.Latest(order).Fingerprint);
            Assert.Equal(reopened.Fingerprint, RemoteSnapshotOrder.Latest([.. order, closed]).Fingerprint);
        }
    }

    [Fact]
    public void A_pull_request_that_is_the_same_repository_in_another_case_is_another_pull_request_because_the_equality_is_exact()
    {
        var other = new PullRequestRef(RemoteRepository.TryCreateGitHub("largolujan", "AGENT-STUDIO")!, 12);
        var elsewhere = Merged(RemoteMergeMode.Squash) with { PullRequest = other, OriginalCommits = [Other], ResultCommits = [ResultTwo], MergeCommit = ResultTwo };

        var succession = CommitSuccession.From([Merged(RemoteMergeMode.Squash), elsewhere], _ => null);

        Assert.NotEqual(PullRequest.TraceId, other.TraceId);
        Assert.Equal([Result], succession.SuccessorOf(Original1)!.ResultCommits);
        Assert.Equal(other, succession.SuccessorOf(Other)!.PullRequest);
    }

    [Fact]
    public void A_revert_or_a_deployment_that_does_not_move_the_updated_at_still_wins_the_tie()
    {
        // The platform does not move updated_at for either (D-23, D-24): only what only grows can tell which observation is later.
        var merged = Merged(RemoteMergeMode.Squash);
        var reverted = merged with { RevertCommits = [Revert] };
        var deployed = reverted with { Deployment = new RemoteDeployment(9, Result, DeploymentState.Succeeded) };

        Assert.Equal(deployed.Fingerprint, RemoteSnapshotOrder.Latest([merged, deployed, reverted]).Fingerprint);
        Assert.Equal(deployed.Fingerprint, RemoteSnapshotOrder.Latest([deployed, reverted, merged]).Fingerprint);
        Assert.Equal(reverted.Fingerprint, RemoteSnapshotOrder.Latest([reverted, merged]).Fingerprint);
    }

    [Fact]
    public void Merged_is_terminal_even_when_an_open_observation_is_more_recent_and_the_succession_does_not_change_with_the_order_or_the_repetition()
    {
        var series = Series();
        var stale = Open() with { UpdatedAt = At.AddHours(3), Checks = new ChecksSummary(ChecksRollup.Pending, 0, 0, 3, false) };
        series.Add(stale);
        var expected = CommitSuccession.From(series, _ => Head);

        foreach (var order in Permutations(series).Take(240))
        {
            var succession = CommitSuccession.From([.. order, .. order], _ => Head);
            var successor = succession.SuccessorOf(Original1)!;
            Assert.Equal(PullRequestState.Merged, RemoteSnapshotOrder.Latest(order).State);
            Assert.Equal(expected.SuccessorOf(Original1)!.ResultCommits, successor.ResultCommits);
            Assert.Equal([Revert], successor.RevertedBy);
            Assert.Equal(expected.OriginalsOf(Result), succession.OriginalsOf(Result));
        }
    }
}
