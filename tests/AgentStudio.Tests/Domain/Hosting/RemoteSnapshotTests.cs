using AgentStudio.Domain.Hosting;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;

namespace AgentStudio.Tests.Domain.Hosting;

/// <summary>
/// docs/21 D-23 (P0 of Phase 10): the snapshot of a pull request and its fingerprint, which the log anchors. The canonical form is pinned as a golden hash: if
/// it changes, every observation already recorded would look new. Also what a snapshot must hold to be believed, and the capabilities that a repository narrows.
/// </summary>
public sealed class RemoteSnapshotTests
{
    [Fact]
    public void The_fingerprint_of_a_snapshot_is_pinned_as_the_log_anchors_it()
    {
        // SHA-256 of "remote-snapshot/1" and one key=value line per field: if this changes, a recorded observation would look new.
        Assert.Equal("b7a7cf545f8837754270a86fc6049b5bb09c6c48d7dada7de1b4dc9e75bd1943", Open().Fingerprint);
        Assert.Equal("4be84a0c12e39704d7a2848fd4d79be80c109e08d293673609a55de632234546", Merged().Fingerprint);
    }

    [Fact]
    public void The_fingerprint_does_not_depend_on_when_it_was_updated_nor_on_the_order_of_the_sets_but_on_everything_else()
    {
        var baseline = Open() with { Reviews = new ReviewsSummary(2, 0, 0, [RemoteLogin.TryParse("ana")!, RemoteLogin.TryParse("beto")!]) };

        Assert.Equal(baseline.Fingerprint, (baseline with { UpdatedAt = At.AddDays(3) }).Fingerprint);
        Assert.Equal(baseline.Fingerprint, (baseline with { Reviews = baseline.Reviews with { Approvers = [RemoteLogin.TryParse("beto")!, RemoteLogin.TryParse("ana")!] } }).Fingerprint);
        Assert.Equal(64, baseline.Fingerprint.Length);
        Assert.Equal(baseline.Fingerprint, baseline.Fingerprint.ToLowerInvariant());

        var changes = new[]
        {
            baseline with { State = PullRequestState.Draft },
            baseline with { HeadCommit = Original1 },
            baseline with { BaseBranch = RemoteBranch.TryParse("develop")! },
            baseline with { HeadBranchExists = false },
            baseline with { MergeCommit = Result, State = PullRequestState.Merged },
            baseline with { OriginalCommits = [Original2, Original1] },
            baseline with { ResultCommits = [Result] },
            baseline with { CommitsTruncated = true },
            baseline with { Checks = baseline.Checks with { Failing = 1 } },
            baseline with { Reviews = baseline.Reviews with { ChangesRequested = 1 } },
            baseline with { Reviews = baseline.Reviews with { Approvers = [RemoteLogin.TryParse("ana")!] } },
            baseline with { ForcePushes = 1 },
            baseline with { TimelineTruncated = true },
            baseline with { RevertCommits = [Revert] },
            baseline with { Deployment = new RemoteDeployment(7, Result, DeploymentState.Succeeded) },
            baseline with { PullRequest = new PullRequestRef(Repository, 13) },
        };
        Assert.Equal(changes.Length, changes.Select(c => c.Fingerprint).Append(baseline.Fingerprint).Distinct().Count() - 1);
    }

    [Fact]
    public void A_snapshot_with_what_the_platform_says_is_valid_and_a_merged_one_carries_its_merge()
    {
        Open().EnsureValid();
        Merged().EnsureValid();
        (Open() with { Deployment = new RemoteDeployment(7, Result, DeploymentState.Succeeded), RevertCommits = [Revert] }).EnsureValid();
    }

    [Fact]
    public void A_snapshot_that_is_not_what_a_snapshot_holds_is_refused()
    {
        var bad = new[]
        {
            Open() with { HeadCommit = "abc123" },
            Open() with { HeadCommit = Head.ToUpperInvariant() },
            Open() with { MergeCommit = Result },
            Open() with { MergeMode = RemoteMergeMode.Squash },
            Open() with { MergedBy = RemoteLogin.TryParse("manuel") },
            Merged() with { MergeCommit = "nope" },
            Open() with { OriginalCommits = [Original1, "short"] },
            Open() with { ResultCommits = [.. Enumerable.Repeat(Original1, RemoteSnapshot.MaxCommits + 1)] },
            Open() with { RevertCommits = ["x"] },
            Open() with { Deployment = new RemoteDeployment(-1, Result, DeploymentState.Pending) },
            Open() with { Deployment = new RemoteDeployment(1, "x", DeploymentState.Pending) },
            Open() with { Checks = new ChecksSummary(ChecksRollup.None, -1, 0, 0, false) },
            Open() with { ForcePushes = -1 },
            Open() with { Reviews = new ReviewsSummary(0, 0, -1, []) },
            Open() with { State = (PullRequestState)9 },
        };

        Assert.All(bad, snapshot => Assert.Throws<ArgumentException>(snapshot.EnsureValid));
    }

    // ---- capabilities and the facts of a repository ----

    private static readonly HostingCapabilities Everything = new(
        HostingCapability.ReadRepository | HostingCapability.OpenPullRequest | HostingCapability.Merge | HostingCapability.Comment | HostingCapability.DeleteBranch | HostingCapability.ReadChecks,
        [MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase],
        false);

    [Fact]
    public void What_a_provider_does_not_declare_it_does_not_have_and_none_has_nothing()
    {
        Assert.False(HostingCapabilities.None.Has(HostingCapability.ReadRepository));
        Assert.False(Everything.Has(HostingCapability.ClosePullRequest));
        Assert.True(Everything.Has(HostingCapability.OpenPullRequest | HostingCapability.Merge));
        Assert.False(Everything.Has(HostingCapability.OpenPullRequest | HostingCapability.ClosePullRequest));
        Assert.False(Everything.Has(HostingCapability.None));
        Assert.Empty(HostingCapabilities.None.MergeMethods);
    }

    [Fact]
    public void A_repository_narrows_the_merge_methods_to_the_ones_it_allows()
    {
        var facts = new RemoteRepositoryFacts(Main, [MergeMethod.Squash], true, false);

        var narrowed = Everything.NarrowedBy(facts);

        Assert.Equal([MergeMethod.Squash], narrowed.MergeMethods);
        Assert.True(narrowed.Has(HostingCapability.Merge));
        Assert.Equal(Everything.Supported, narrowed.Supported);
    }

    [Fact]
    public void A_base_branch_that_asks_for_a_linear_history_takes_out_the_merge_commit_and_only_when_its_rules_were_read()
    {
        var facts = new RemoteRepositoryFacts(Main, [MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase], true, false);
        var linear = new BranchRules(true, [new BranchRule(BranchRuleKind.LinearHistory, null)]);

        var narrowed = Everything.NarrowedBy(facts, linear);
        Assert.DoesNotContain(MergeMethod.MergeCommit, narrowed.MergeMethods);
        Assert.Contains(MergeMethod.Squash, narrowed.MergeMethods);
        Assert.Contains(MergeMethod.Rebase, narrowed.MergeMethods);

        // Rules that could not be read narrow nothing (the platform applies them anyway), and rules without that one leave every method.
        Assert.Contains(MergeMethod.MergeCommit, Everything.NarrowedBy(facts, BranchRules.Unreadable).MergeMethods);
        Assert.Contains(MergeMethod.MergeCommit, Everything.NarrowedBy(facts, new BranchRules(true, [new BranchRule(BranchRuleKind.Protected, null)])).MergeMethods);
        Assert.Contains(MergeMethod.MergeCommit, Everything.NarrowedBy(facts).MergeMethods);

        // It only narrows: with only the merge commit allowed, a linear history leaves no merge at all.
        var onlyCommit = Everything.NarrowedBy(new RemoteRepositoryFacts(Main, [MergeMethod.MergeCommit], true, false), linear);
        Assert.False(onlyCommit.Has(HostingCapability.Merge));
        Assert.Equal(Everything.Supported & narrowed.Supported, narrowed.Supported);
    }

    [Fact]
    public void A_repository_that_allows_no_method_the_provider_knows_leaves_no_merge()
    {
        var narrowed = Everything.NarrowedBy(new RemoteRepositoryFacts(Main, [], true, false));

        Assert.False(narrowed.Has(HostingCapability.Merge));
        Assert.True(narrowed.Has(HostingCapability.OpenPullRequest));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void An_account_that_cannot_write_or_an_archived_repository_leaves_only_the_reads(bool canPush, bool archived)
    {
        var narrowed = Everything.NarrowedBy(new RemoteRepositoryFacts(Main, [MergeMethod.Squash], canPush, archived));

        Assert.True(narrowed.Has(HostingCapability.ReadRepository));
        Assert.True(narrowed.Has(HostingCapability.ReadChecks));
        Assert.False(narrowed.Has(HostingCapability.OpenPullRequest));
        Assert.False(narrowed.Has(HostingCapability.Merge));
        Assert.False(narrowed.Has(HostingCapability.DeleteBranch));
        Assert.Empty(narrowed.MergeMethods);
    }

    [Fact]
    public void The_rules_of_a_branch_say_when_they_could_not_be_read_and_that_is_never_no_rules()
    {
        Assert.False(BranchRules.Unreadable.Readable);
        Assert.Empty(BranchRules.Unreadable.Rules);
        var rules = new BranchRules(true, [new BranchRule(BranchRuleKind.RequiredReviews, 2), new BranchRule(BranchRuleKind.LinearHistory, null)]);
        Assert.True(rules.Has(BranchRuleKind.LinearHistory));
        Assert.False(rules.Has(BranchRuleKind.SignedCommits));
    }

    [Fact]
    public void The_enums_of_the_snapshot_are_appended_never_reordered()
    {
        Assert.Equal(["Open", "Draft", "Merged", "Closed"], Enum.GetNames<PullRequestState>());
        Assert.Equal(["MergeCommit", "Squash", "Rebase", "SquashOrRebase", "Unknown"], Enum.GetNames<RemoteMergeMode>());
        Assert.Equal(["None", "Pending", "Passing", "Failing"], Enum.GetNames<ChecksRollup>());
        Assert.Equal(["Pending", "Succeeded", "Failed", "Inactive", "Unknown"], Enum.GetNames<DeploymentState>());
        Assert.Equal(
            ["Opened", "HeadMoved", "ForcePushed", "Merged", "Closed", "Reopened", "HeadBranchDeleted", "ChecksChanged", "ReviewsChanged", "RevertSuspected", "DeploymentChanged", "BaseChanged"],
            Enum.GetNames<RemoteChangeKind>());
        Assert.Equal(["Protected", "RequiredReviews", "RequiredChecks", "SignedCommits", "LinearHistory", "MergeQueue", "Unknown"], Enum.GetNames<BranchRuleKind>());
        Assert.Equal(
            ["None", "ReadRepository", "ReadBranchRules", "OpenPullRequest", "UpdatePullRequest", "Comment", "RequestReviewers", "ReadReviews", "ReadChecks", "ReadWorkflowRuns", "Merge",
             "ClosePullRequest", "DeleteBranch", "CommitStatus", "ReadDeployments", "ConditionalRequests", "DraftPullRequest"],
            Enum.GetNames<HostingCapability>());
        Assert.Equal(32768, (int)HostingCapability.DraftPullRequest);
    }
}
