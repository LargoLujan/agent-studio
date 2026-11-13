using AgentStudio.Domain.Hosting;

namespace AgentStudio.Tests.Domain.Hosting;

/// <summary>
/// docs/21 D-15 and D-16 (P0 of Phase 10): the rule of the classes of remote operation is code of the type, pure. Merging, the destructive operations and asking for
/// reviewers can never be covered by a standing grant; a grant covers only its class and its repository (and its base branch when it names one); the verdict is
/// required by operation, not by class.
/// </summary>
public sealed class HostingPolicyTests
{
    private static readonly RemoteRepository Repository = HostingP0Data.Repository;
    private static readonly RemoteRepository Other = RemoteRepository.TryCreateGitHub("LargoLujan", "other")!;
    private static readonly RemoteBranch Main = HostingP0Data.Main;
    private static readonly RemoteBranch Develop = RemoteBranch.TryParse("develop")!;

    [Theory]
    [InlineData(HostingOperationKind.PushRunBranch, HostingOperationClass.PublishRunBranch)]
    [InlineData(HostingOperationKind.OpenPullRequest, HostingOperationClass.PullRequestWrite)]
    [InlineData(HostingOperationKind.UpdatePullRequest, HostingOperationClass.PullRequestWrite)]
    [InlineData(HostingOperationKind.RequestReviewers, HostingOperationClass.PullRequestWrite)]
    [InlineData(HostingOperationKind.PublishVerdictStatus, HostingOperationClass.PullRequestWrite)]
    [InlineData(HostingOperationKind.PublishVerdictComment, HostingOperationClass.PullRequestWrite)]
    [InlineData(HostingOperationKind.MergePullRequest, HostingOperationClass.MergePullRequest)]
    [InlineData(HostingOperationKind.ClosePullRequest, HostingOperationClass.Destructive)]
    [InlineData(HostingOperationKind.DeleteRemoteBranch, HostingOperationClass.Destructive)]
    public void Each_operation_is_of_the_class_the_design_says(HostingOperationKind operation, HostingOperationClass expected) =>
        Assert.Equal(expected, HostingPolicy.ClassOf(operation));

    [Theory]
    [InlineData(HostingOperationKind.PushRunBranch, true)]
    [InlineData(HostingOperationKind.OpenPullRequest, true)]
    [InlineData(HostingOperationKind.UpdatePullRequest, true)]
    [InlineData(HostingOperationKind.PublishVerdictStatus, true)]
    [InlineData(HostingOperationKind.PublishVerdictComment, true)]
    [InlineData(HostingOperationKind.RequestReviewers, false)]
    [InlineData(HostingOperationKind.MergePullRequest, false)]
    [InlineData(HostingOperationKind.ClosePullRequest, false)]
    [InlineData(HostingOperationKind.DeleteRemoteBranch, false)]
    public void Only_publishing_and_writing_on_the_pull_request_can_be_persistent_and_never_asking_for_reviewers(HostingOperationKind operation, bool persistent) =>
        Assert.Equal(persistent, HostingPolicy.CanBePersistent(operation));

    [Theory]
    [InlineData(HostingOperationKind.PushRunBranch, true)]
    [InlineData(HostingOperationKind.OpenPullRequest, true)]
    [InlineData(HostingOperationKind.UpdatePullRequest, true)]
    [InlineData(HostingOperationKind.RequestReviewers, true)]
    [InlineData(HostingOperationKind.MergePullRequest, true)]
    [InlineData(HostingOperationKind.PublishVerdictStatus, false)]
    [InlineData(HostingOperationKind.PublishVerdictComment, false)]
    [InlineData(HostingOperationKind.ClosePullRequest, false)]
    [InlineData(HostingOperationKind.DeleteRemoteBranch, false)]
    public void An_approved_verdict_is_required_by_operation_not_by_class(HostingOperationKind operation, bool required) =>
        Assert.Equal(required, HostingPolicy.RequiresApprovedVerdict(operation));

    [Fact]
    public void Writing_the_verdict_and_opening_the_pull_request_share_a_class_and_differ_in_the_verdict_they_need()
    {
        Assert.Equal(HostingPolicy.ClassOf(HostingOperationKind.OpenPullRequest), HostingPolicy.ClassOf(HostingOperationKind.PublishVerdictStatus));
        Assert.NotEqual(
            HostingPolicy.RequiresApprovedVerdict(HostingOperationKind.OpenPullRequest),
            HostingPolicy.RequiresApprovedVerdict(HostingOperationKind.PublishVerdictStatus));
    }

    [Fact]
    public void An_operation_that_does_not_exist_is_refused_by_every_rule()
    {
        var unknown = (HostingOperationKind)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => HostingPolicy.ClassOf(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostingPolicy.CanBePersistent(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostingPolicy.RequiresApprovedVerdict(unknown));
    }

    [Fact]
    public void Only_the_classes_with_an_operation_that_may_be_persistent_admit_a_standing_grant()
    {
        Assert.True(HostingPolicy.ClassAdmitsGrant(HostingOperationClass.PublishRunBranch));
        Assert.True(HostingPolicy.ClassAdmitsGrant(HostingOperationClass.PullRequestWrite));
        Assert.False(HostingPolicy.ClassAdmitsGrant(HostingOperationClass.MergePullRequest));
        Assert.False(HostingPolicy.ClassAdmitsGrant(HostingOperationClass.Destructive));
        Assert.False(HostingPolicy.ClassAdmitsGrant((HostingOperationClass)42));
    }

    [Theory]
    [InlineData(HostingOperationClass.MergePullRequest)]
    [InlineData(HostingOperationClass.Destructive)]
    public void A_standing_grant_of_a_class_that_admits_none_cannot_be_built(HostingOperationClass @class) =>
        Assert.Throws<ArgumentException>(() => new HostingPolicyGrant("g1", @class, Repository, null, "manuel", HostingP0Data.At));

    [Fact]
    public void A_grant_to_publish_the_branch_never_names_a_base_branch_and_the_other_fields_are_required()
    {
        Assert.Throws<ArgumentException>(() => new HostingPolicyGrant("g1", HostingOperationClass.PublishRunBranch, Repository, Main, "manuel", HostingP0Data.At));
        Assert.NotNull(new HostingPolicyGrant("g1", HostingOperationClass.PullRequestWrite, Repository, Main, "manuel", HostingP0Data.At));
        Assert.Throws<ArgumentException>(() => new HostingPolicyGrant(" ", HostingOperationClass.PullRequestWrite, Repository, null, "manuel", HostingP0Data.At));
        Assert.Throws<ArgumentException>(() => new HostingPolicyGrant("g1", HostingOperationClass.PullRequestWrite, Repository, null, "", HostingP0Data.At));
        Assert.Throws<ArgumentNullException>(() => new HostingPolicyGrant("g1", HostingOperationClass.PullRequestWrite, null!, null, "manuel", HostingP0Data.At));
    }

    [Fact]
    public void A_grant_covers_only_its_class_and_its_repository()
    {
        var grant = new HostingPolicyGrant("g1", HostingOperationClass.PullRequestWrite, Repository, null, "manuel", HostingP0Data.At);

        Assert.True(grant.Covers(HostingOperationClass.PullRequestWrite, Repository, Main));
        Assert.True(grant.Covers(HostingOperationClass.PullRequestWrite, Repository, null));
        Assert.False(grant.Covers(HostingOperationClass.PublishRunBranch, Repository, Main));
        Assert.False(grant.Covers(HostingOperationClass.MergePullRequest, Repository, Main));
        Assert.False(grant.Covers(HostingOperationClass.Destructive, Repository, Main));
        Assert.False(grant.Covers(HostingOperationClass.PullRequestWrite, Other, Main));
    }

    [Fact]
    public void A_grant_with_a_base_branch_covers_only_that_base_and_one_without_covers_any()
    {
        var toMain = new HostingPolicyGrant("g1", HostingOperationClass.PullRequestWrite, Repository, Main, "manuel", HostingP0Data.At);
        var toAny = new HostingPolicyGrant("g2", HostingOperationClass.PullRequestWrite, Repository, null, "manuel", HostingP0Data.At);

        Assert.True(toMain.Covers(HostingOperationClass.PullRequestWrite, Repository, Main));
        Assert.False(toMain.Covers(HostingOperationClass.PullRequestWrite, Repository, Develop));
        Assert.False(toMain.Covers(HostingOperationClass.PullRequestWrite, Repository, null));
        Assert.True(toAny.Covers(HostingOperationClass.PullRequestWrite, Repository, Develop));
    }

    [Fact]
    public void Publishing_the_branch_has_no_base_so_its_grant_covers_it_whatever_base_the_pull_request_will_have()
    {
        var grant = new HostingPolicyGrant("g1", HostingOperationClass.PublishRunBranch, Repository, null, "manuel", HostingP0Data.At);

        Assert.True(grant.Covers(HostingOperationClass.PublishRunBranch, Repository, null));
        Assert.True(grant.Covers(HostingOperationClass.PublishRunBranch, Repository, Develop));
        Assert.False(grant.Covers(HostingOperationClass.PublishRunBranch, Other, null));
    }

    [Fact]
    public void Every_operation_that_may_be_persistent_belongs_to_a_class_that_admits_a_grant_and_the_others_to_one_that_cannot_cover_them()
    {
        foreach (var operation in Enum.GetValues<HostingOperationKind>())
        {
            var admits = HostingPolicy.ClassAdmitsGrant(HostingPolicy.ClassOf(operation));
            if (HostingPolicy.CanBePersistent(operation))
            {
                Assert.True(admits, operation.ToString());
            }
        }

        // A class that admits a grant still holds operations that it never covers: asking for reviewers.
        Assert.True(HostingPolicy.ClassAdmitsGrant(HostingPolicy.ClassOf(HostingOperationKind.RequestReviewers)));
        Assert.False(HostingPolicy.CanBePersistent(HostingOperationKind.RequestReviewers));
    }

    [Fact]
    public void The_enums_of_the_policy_are_appended_never_reordered()
    {
        Assert.Equal(["PublishRunBranch", "PullRequestWrite", "MergePullRequest", "Destructive"], Enum.GetNames<HostingOperationClass>());
        Assert.Equal(
            ["PushRunBranch", "OpenPullRequest", "UpdatePullRequest", "RequestReviewers", "PublishVerdictStatus", "PublishVerdictComment", "MergePullRequest", "ClosePullRequest", "DeleteRemoteBranch"],
            Enum.GetNames<HostingOperationKind>());
        Assert.Equal(["MergeCommit", "Squash", "Rebase"], Enum.GetNames<MergeMethod>());
        Assert.Equal(["GitHub", "AzureDevOps"], Enum.GetNames<HostingProviderKind>());
    }
}
