using AgentStudio.Application.Decisions;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>The gate of the policies (docs/21 D-15 and D-16, F10-4): one gate, by use or by standing grant, never persistent for what warns people, merges or destroys.</summary>
public sealed class HostingPolicyGateTests
{
    private static HostingPolicyGrant Grant(HostingOperationClass @class, RemoteRepository? repository = null, RemoteBranch? baseBranch = null, string id = "g1") =>
        new(id, @class, repository ?? HostingP0Data.Repository, baseBranch, "person", HostingP0Data.At);

    private static void Grants(PublicationRig rig, HostingPolicyGrant grant) =>
        rig.Log.Add(DecisionKind.HostingPolicyGranted, new Actor(ActorKind.User, "person"), "grant", HostingPolicyGrantedPayload.Granting(grant));

    [Fact]
    public async Task WithoutAGrantTheUseAsksAnApprovalTiedToWhatItDoes()
    {
        await using var rig = new PublicationRig();

        var result = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(HostingAuthorizationSource.Approval, result.Source);
        var request = Assert.Single(rig.Approvals.Requests);
        Assert.Equal(HostingActions.PublishAction, request.Action);
        Assert.Contains(HostingP0Data.Repository.Id, request.Binding, StringComparison.Ordinal);
        Assert.Contains(HostingP0Data.Head, request.Binding, StringComparison.Ordinal);
        Assert.Equal(request.Binding, result.ApprovalBinding);
    }

    [Fact]
    public async Task AnAnswerTiedToAnotherThingDoesNotAct()
    {
        await using var rig = new PublicationRig();
        rig.Approvals.WrongAnswer = "something-else";

        var result = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(PublicationCodes.ApprovalMismatch, result.Refusal!.Code);
    }

    [Fact]
    public async Task ADeniedApprovalDoesNotAct()
    {
        await using var rig = new PublicationRig();
        rig.Approvals.Approve = false;

        var result = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(PublicationCodes.NotApproved, result.Refusal!.Code);
    }

    [Fact]
    public async Task TheStateIsCheckedAgainAfterTheApproval()
    {
        await using var rig = new PublicationRig();
        rig.Approvals.WhileDeciding = () => rig.Delivery.Ready = false;

        var result = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(PublicationCodes.StateChanged, result.Refusal!.Code);
    }

    [Fact]
    public async Task AStandingGrantSavesTheApprovalAndTheOperationNamesIt()
    {
        await using var rig = new PublicationRig();
        Grants(rig, Grant(HostingOperationClass.PublishRunBranch));

        var result = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(HostingAuthorizationSource.StandingGrant, result.Source);
        Assert.Equal("g1", result.GrantId);
        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public async Task AGrantOfAnotherRepositoryOrAnotherBaseBranchDoesNotCover()
    {
        await using var rig = new PublicationRig();
        Grants(rig, Grant(HostingOperationClass.PublishRunBranch, RemoteRepository.TryCreateGitHub("someone", "else")));
        Grants(rig, Grant(HostingOperationClass.PullRequestWrite, baseBranch: RemoteBranch.TryParse("release"), id: "g2"));

        var push = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);
        var open = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.OpenPullRequest, baseBranch: HostingP0Data.Main), CancellationToken.None);

        Assert.Equal(HostingAuthorizationSource.Approval, push.Source);
        Assert.Equal(HostingAuthorizationSource.Approval, open.Source);
        Assert.Equal(2, rig.Approvals.Requests.Count);
    }

    [Fact]
    public async Task AGrantToWriteOnThePullRequestDoesNotCoverAskingForReviewersMergingOrTheDestructive()
    {
        await using var rig = new PublicationRig();
        Grants(rig, Grant(HostingOperationClass.PullRequestWrite));

        var open = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.OpenPullRequest, baseBranch: HostingP0Data.Main), CancellationToken.None);
        var reviewers = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.RequestReviewers, HostingP0Data.PullRequest), CancellationToken.None);
        var merge = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.MergePullRequest, HostingP0Data.PullRequest, MergeMethod.Squash, HostingP0Data.Main), CancellationToken.None);
        var close = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.ClosePullRequest, HostingP0Data.PullRequest), CancellationToken.None);

        Assert.Equal(HostingAuthorizationSource.StandingGrant, open.Source);
        Assert.Equal(HostingAuthorizationSource.Approval, reviewers.Source);
        Assert.Equal(HostingAuthorizationSource.Approval, merge.Source);
        Assert.Equal(HostingAuthorizationSource.Approval, close.Source);
        Assert.Equal(
            [HostingActions.PullRequestWriteAction, HostingActions.MergeAction, HostingActions.ClosePullRequestAction],
            rig.Approvals.Requests.Select(r => r.Action));
        Assert.Contains("Squash", rig.Approvals.Requests[1].Binding, StringComparison.Ordinal);
        Assert.Contains("|12", rig.Approvals.Requests[1].Binding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGrantOfAClassThatAdmitsNoneIsRefused()
    {
        await using var rig = new PublicationRig();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Gate.GrantAsync(PublicationRig.Project, HostingOperationClass.MergePullRequest, HostingP0Data.Repository, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Gate.GrantAsync(PublicationRig.Project, HostingOperationClass.Destructive, HostingP0Data.Repository, null, CancellationToken.None));
        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public async Task AGrantIsAnEntryOfTheLogAndRevokingItAsksAgain()
    {
        await using var rig = new PublicationRig();

        var grant = await rig.Gate.GrantAsync(PublicationRig.Project, HostingOperationClass.PublishRunBranch, HostingP0Data.Repository, null, CancellationToken.None);

        Assert.NotNull(grant);
        Assert.Equal(HostingActions.GrantPolicyAction, rig.Approvals.Requests[0].Action);
        Assert.Equal(1, rig.Log.Count(DecisionKind.HostingPolicyGranted));
        Assert.Single(await rig.Gate.GrantsAsync(PublicationRig.Project, CancellationToken.None));
        var covered = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);
        Assert.Equal(HostingAuthorizationSource.StandingGrant, covered.Source);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        await rig.Gate.RevokeAsync(PublicationRig.Project, grant!.GrantId, CancellationToken.None);
        var asked = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.Equal(1, rig.Log.Count(DecisionKind.HostingPolicyRevoked));
        Assert.Empty(await rig.Gate.GrantsAsync(PublicationRig.Project, CancellationToken.None));
        Assert.Equal(HostingAuthorizationSource.Approval, asked.Source);
    }

    [Fact]
    public async Task AGrantGivenWhileTheProjectIsBusyIsParkedAndDoesNotCountUntilItIsWritten()
    {
        await using var rig = new PublicationRig();
        var run = rig.Activity.TryEnter("p1", AgentStudio.Application.Orchestration.ProjectActivityKind.Run);

        var grant = await rig.Gate.GrantAsync(PublicationRig.Project, HostingOperationClass.PublishRunBranch, HostingP0Data.Repository, null, CancellationToken.None);
        var meanwhile = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.Null(grant);
        Assert.Equal(0, rig.Log.Count(DecisionKind.HostingPolicyGranted));
        Assert.Equal(HostingAuthorizationSource.Approval, meanwhile.Source);
        Assert.Equal(1, (await rig.BoxAsync()).ParkedCount);

        run!.Dispose();
        await rig.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, rig.Log.Count(DecisionKind.HostingPolicyGranted));
        Assert.Equal(0, (await rig.BoxAsync()).ParkedCount);
    }

    [Fact]
    public async Task WithTheVerdictNotApprovedThePushTheOpeningAndTheMergeAreRefused()
    {
        await using var rig = new PublicationRig();
        rig.Delivery.Ready = false;

        foreach (var operation in new[]
                 {
                     PublicationRig.Operation(HostingOperationKind.PushRunBranch),
                     PublicationRig.Operation(HostingOperationKind.OpenPullRequest, baseBranch: HostingP0Data.Main),
                     PublicationRig.Operation(HostingOperationKind.MergePullRequest, HostingP0Data.PullRequest, MergeMethod.Squash, HostingP0Data.Main),
                 })
        {
            var result = await rig.Gate.AuthorizeAsync(operation, CancellationToken.None);
            Assert.False(result.Allowed);
            Assert.Equal(PublicationCodes.VerdictNotApproved, result.Refusal!.Code);
        }

        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public async Task ClosingNeedsNoApprovedVerdictButPublishingTheVerdictNeedsOneToTell()
    {
        await using var rig = new PublicationRig();
        rig.Delivery.Ready = false;

        var close = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.ClosePullRequest, HostingP0Data.PullRequest), CancellationToken.None);
        var status = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PublishVerdictStatus, HostingP0Data.PullRequest), CancellationToken.None);

        Assert.True(close.Allowed);
        Assert.False(status.Allowed);
        Assert.Equal(PublicationCodes.NoVerdict, status.Refusal!.Code);
    }

    [Fact]
    public async Task ALogThatDoesNotVerifyRefusesEverything()
    {
        await using var rig = new PublicationRig();
        rig.Log.Verifies = false;

        var result = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.ClosePullRequest, HostingP0Data.PullRequest), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(PublicationCodes.LogNotVerified, result.Refusal!.Code);
    }

    [Fact]
    public async Task APushIsRefusedWhenTheInspectionOfGitFailsAndAUseInTheMailboxNeverAsks()
    {
        await using var rig = new PublicationRig();
        Grants(rig, Grant(HostingOperationClass.PublishRunBranch));
        rig.Git.Inspection = new RemoteGitInspection(false, [new RemoteConfigRefusal("credential.helper", "local", "x")], false, null);
        HostingAuthorization refused;
        using (rig.Activity.TryEnter("p1", AgentStudio.Application.Orchestration.ProjectActivityKind.RemoteOperation))
        {
            refused = await rig.Gate.AuthorizeUseAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), new ApprovalLease(), mayAsk: false, holdsProject: true, CancellationToken.None);
        }

        Assert.False(refused.Allowed);
        Assert.Equal(PublicationCodes.ConfigRefused, refused.Refusal!.Code);

        var noAsk = await rig.Gate.AuthorizeUseAsync(PublicationRig.Operation(HostingOperationKind.MergePullRequest, HostingP0Data.PullRequest, MergeMethod.Squash, HostingP0Data.Main), new ApprovalLease(), mayAsk: false, holdsProject: false, CancellationToken.None);
        Assert.False(noAsk.Allowed);
        Assert.Equal(PublicationCodes.NeedsApproval, noAsk.Refusal!.Code);
        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public async Task AMergeMethodTheRepositoryDoesNotAllowIsRefusedBeforeAsking()
    {
        await using var rig = new PublicationRig();
        var merge = PublicationRig.Operation(HostingOperationKind.MergePullRequest, HostingP0Data.PullRequest, MergeMethod.Squash, HostingP0Data.Main) with { Method = null };

        var result = await rig.Gate.AuthorizeAsync(merge, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public void TheSixApprovalsAreOnesOfTheClassifier()
    {
        Assert.Equal(6, HostingActions.All.Count);
        Assert.All(Enum.GetValues<HostingOperationKind>(), kind => Assert.Contains(HostingActions.ActionOf(kind), HostingActions.All));
    }
}
