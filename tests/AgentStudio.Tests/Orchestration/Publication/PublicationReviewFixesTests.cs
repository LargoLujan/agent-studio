using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>What the independent review of WP3 found, each with its test: the verdict published under the approval of the click, the network without permission, the base of a merge, a revocation, and the rest.</summary>
public sealed class PublicationReviewFixesTests
{
    private static readonly RemoteBranch Main = HostingP0Data.Main;

    private static PublicationRequest Request() => new(PublicationRig.Project, HostingP0Data.Run, Main, new PullRequestText("title", "body"), false);

    private static HostingPolicyGrant Grant(HostingOperationClass @class, RemoteBranch? baseBranch = null) =>
        new("g-" + @class, @class, HostingP0Data.Repository, baseBranch, "person", HostingP0Data.At);

    private static void Grants(PublicationRig rig, params HostingPolicyGrant[] grants)
    {
        foreach (var grant in grants)
        {
            rig.Log.Add(DecisionKind.HostingPolicyGranted, new Actor(ActorKind.User, "person"), "grant", HostingPolicyGrantedPayload.Granting(grant));
        }
    }

    private static async Task<RemoteWorkStatus> RunPublicationAsync(PublicationRig rig)
    {
        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);

        // Every write to the log takes the project and the courtesy second follows each one: the notes go one by one, a second apart.
        for (var i = 0; i < 8; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        return (await rig.BoxAsync()).StatusOf(ticket.Id)!;
    }

    private static IReadOnlyList<HostingOperationKind> Done(PublicationRig rig) =>
        [.. RemoteLogReading.ReadEntries(rig.Log.Entries).Operations.Select(o => o.Payload.Operation)];

    // ---- 1: the verdict under the approval of the click ----

    [Fact]
    public async Task WithAnApprovalByUseTheVerdictIsPublishedAndThePullRequestFollowed()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();

        var status = await RunPublicationAsync(rig);

        Assert.Equal(RemoteWorkState.Done, status.State);
        Assert.Equal(2, rig.Approvals.Requests.Count);
        Assert.Equal(1, rig.Trace.Count("gh:verdict"));
        Assert.Equal(
            [HostingOperationKind.PushRunBranch, HostingOperationKind.OpenPullRequest, HostingOperationKind.PublishVerdictStatus, HostingOperationKind.PublishVerdictComment],
            Done(rig));
        await rig.IdleAsync();
        Assert.Equal(HostingP0Data.PullRequest, Assert.Single(rig.Store.State.Followed).PullRequest);

        var (notice, description, body) = rig.Provider.LastVerdict!.Value;
        Assert.Equal(VerdictStanding.Approved, notice.Standing);
        Assert.Equal(HostingP0Data.Head, notice.JudgedCommit);
        Assert.StartsWith(VerdictNoticeBuilder.MarkOf(HostingP0Data.Run), body, StringComparison.Ordinal);
        Assert.StartsWith(PublicationCodes.StatusApproved, description, StringComparison.Ordinal);
        Assert.Contains("hosting.pr.body-requirement-met:`R1`", body, StringComparison.Ordinal);
        Assert.NotEmpty(notice.Requirements);
    }

    [Fact]
    public async Task TheEntriesOfTheOperationsNameTheApprovalThatAnsweredThemAndNotAnotherOne()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();

        await RunPublicationAsync(rig);

        var operations = RemoteLogReading.ReadEntries(rig.Log.Entries).Operations;
        var grants = rig.Log.Entries.Where(e => e.Kind == DecisionKind.ApprovalGranted).Select(e => e.Sequence).ToList();
        Assert.Equal(grants[0], operations.First(o => o.Payload.Operation == HostingOperationKind.PushRunBranch).Payload.ApprovalSequence);
        Assert.All(
            operations.Where(o => o.Payload.Operation != HostingOperationKind.PushRunBranch),
            o => Assert.Equal(grants[1], o.Payload.ApprovalSequence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithStandingGrantsWithoutOrWithABaseTheVerdictIsPublishedToo(bool withBase)
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        Grants(rig, Grant(HostingOperationClass.PublishRunBranch), Grant(HostingOperationClass.PullRequestWrite, withBase ? Main : null));

        var status = await RunPublicationAsync(rig);

        Assert.Equal(RemoteWorkState.Done, status.State);
        Assert.Empty(rig.Approvals.Requests);
        Assert.Equal(1, rig.Trace.Count("gh:verdict"));
        Assert.Contains(HostingOperationKind.PublishVerdictComment, Done(rig));
    }

    [Fact]
    public async Task ACommitAnotherThanTheJudgedOneIsNotPublishedAsTheVerdict()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        var other = PublicationRig.Operation(HostingOperationKind.PublishVerdictStatus, HostingP0Data.PullRequest) with { Commit = new string('7', 40) };

        var result = await rig.Gate.AuthorizeAsync(other, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(PublicationCodes.CommitNotJudged, result.Refusal!.Code);
    }

    [Fact]
    public async Task APullRequestIsFollowedEvenWhenThePublicationOfTheVerdictFails()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Provider.VerdictResult = new HostingResult<PublishedVerdict>(HostingOutcome.Refused);

        var status = await RunPublicationAsync(rig);

        Assert.Equal(RemoteWorkState.Failed, status.State);
        await rig.IdleAsync();
        Assert.Equal(HostingP0Data.PullRequest, Assert.Single(rig.Store.State.Followed).PullRequest);
        Assert.Contains(HostingOperationKind.OpenPullRequest, Done(rig));
    }

    [Fact]
    public async Task WithoutTheCapabilityOfTheStatusOnlyTheCommentIsPublished()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Provider.Capabilities &= ~HostingCapability.CommitStatus;

        var status = await RunPublicationAsync(rig);

        Assert.Equal(RemoteWorkState.Done, status.State);
        Assert.Equal(1, rig.Trace.Count("gh:verdict"));
        var kinds = Done(rig);
        Assert.Contains(HostingOperationKind.PublishVerdictComment, kinds);
        Assert.DoesNotContain(HostingOperationKind.PublishVerdictStatus, kinds);
    }

    [Fact]
    public async Task WithNeitherCapabilityNothingOfTheVerdictIsPublishedAndThePullRequestIsKept()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Provider.Capabilities &= ~(HostingCapability.CommitStatus | HostingCapability.Comment);

        var status = await RunPublicationAsync(rig);

        Assert.Equal(RemoteWorkState.Failed, status.State);
        Assert.Equal(PublicationCodes.CapabilityMissing, status.Message!.Code);
        Assert.Equal(0, rig.Trace.Count("gh:verdict"));
        await rig.IdleAsync();
        Assert.Single(rig.Store.State.Followed);
    }

    [Fact]
    public async Task UpdatingTheVerdictOfAPullRequestAsksOnceAndPublishesBothChannels()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());

        var ticket = await rig.Service.UpdateVerdictAsync(PublicationRig.Project, HostingP0Data.PullRequest, CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RemoteWorkState.Done, (await rig.BoxAsync()).StatusOf(ticket.Id)!.State);
        Assert.Single(rig.Approvals.Requests);
        Assert.Equal(HostingActions.PullRequestWriteAction, rig.Approvals.Requests[0].Action);
        Assert.Contains("+verdict", rig.Approvals.Requests[0].Description, StringComparison.Ordinal);
        Assert.Equal(1, rig.Trace.Count("gh:verdict"));
        Assert.Equal([HostingOperationKind.PublishVerdictStatus, HostingOperationKind.PublishVerdictComment], Done(rig));
    }

    [Fact]
    public async Task UpdatingTheVerdictOfAChallengedRunPublishesInReview()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync(AgentStudio.Domain.Judging.VerdictDecision.Rejected);
        rig.Observed(HostingP0Data.Open());

        await rig.Service.UpdateVerdictAsync(PublicationRig.Project, HostingP0Data.PullRequest, CancellationToken.None);

        Assert.Equal(VerdictStanding.Rejected, rig.Provider.LastVerdict!.Value.Notice.Standing);
        Assert.StartsWith(PublicationCodes.StatusRejected, rig.Provider.LastVerdict.Value.Status, StringComparison.Ordinal);
    }

    // ---- 2: the network needs the permission when it is launched ----

    [Fact]
    public async Task WithoutThePermissionTheObservationsOfTheFileAreNotLaunchedEitherEvenAfterADay()
    {
        var pending = new PendingRemoteWork(new RemoteWorkTicket("p-1", null, HostingP0Data.PullRequest), null, HostingP0Data.Open(), "observe", PendingReason.ProjectBusy, new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero), null);
        var state = RemoteSyncState.Empty with { Repository = HostingP0Data.Repository, Pending = [pending] };
        await using var rig = new PublicationRig(state);
        rig.Policy.Allowed = false;

        await rig.Sync.AttachAsync(PublicationRig.Project, CancellationToken.None);
        for (var i = 0; i < 24; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromHours(1));
        }

        Assert.Equal(0, rig.Trace.Count("gh:"));
        Assert.Empty(rig.Log.Observations());
    }

    // ---- 3: merging checks the head and the base ----

    private static async Task<PublicationRig> MergeRigAsync(RemoteSnapshot onThePlatform)
    {
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(onThePlatform));
        return rig;
    }

    [Fact]
    public async Task AMergeIsNotAskedWhenTheBaseOfThePullRequestChanged()
    {
        await using var rig = await MergeRigAsync(HostingP0Data.Open() with { BaseBranch = RemoteBranch.TryParse("release")!, UpdatedAt = HostingP0Data.At.AddMinutes(1) });

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);

        var status = (await rig.BoxAsync()).StatusOf(ticket.Id)!;
        Assert.Equal(RemoteWorkState.Failed, status.State);
        Assert.Equal(PublicationCodes.BaseChanged, status.Message!.Code);
        Assert.Empty(rig.Approvals.Requests);
        Assert.Equal(0, rig.Trace.Count("gh:merge"));
    }

    [Fact]
    public async Task AMergeIsNotAskedWhenThePullRequestCannotBeReadOrItsHeadMoved()
    {
        await using var unreadable = await MergeRigAsync(HostingP0Data.Open());
        unreadable.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.Unreachable);
        var first = await unreadable.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        Assert.Equal(PublicationCodes.PullRequestUnreadable, (await unreadable.BoxAsync()).StatusOf(first.Id)!.Message!.Code);
        Assert.Empty(unreadable.Approvals.Requests);

        await using var moved = await MergeRigAsync(HostingP0Data.Open("5555555555555555555555555555555555555555") with { UpdatedAt = HostingP0Data.At.AddMinutes(1) });
        var second = await moved.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        Assert.Equal(PublicationCodes.BranchMoved, (await moved.BoxAsync()).StatusOf(second.Id)!.Message!.Code);
        Assert.Empty(moved.Approvals.Requests);
    }

    [Fact]
    public async Task TheBaseChangingBetweenTheApprovalAndTheMergeStopsIt()
    {
        await using var rig = await MergeRigAsync(HostingP0Data.Open());
        rig.Approvals.WhileDeciding = () =>
            rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(
                HostingOutcome.Ok, PublicationRig.Observation(HostingP0Data.Open() with { BaseBranch = RemoteBranch.TryParse("release")!, UpdatedAt = HostingP0Data.At.AddMinutes(1) }));

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);

        Assert.Equal(PublicationCodes.BaseChanged, (await rig.BoxAsync()).StatusOf(ticket.Id)!.Message!.Code);
        Assert.Single(rig.Approvals.Requests);
        Assert.Equal(0, rig.Trace.Count("gh:merge"));
    }

    [Fact]
    public async Task AMergeThatTheApprovalCoversIsDoneAndAnnotatedAsWhatTheplatformSaw()
    {
        await using var rig = await MergeRigAsync(HostingP0Data.Open());
        rig.Provider.Merged = new HostingResult<MergeOutcome>(HostingOutcome.Ok, new MergeOutcome(true, HostingP0Data.Result, null));
        rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(
            HostingOutcome.Ok, PublicationRig.Observation(rig.Trace.Count("gh:merge") > 0 ? HostingP0Data.Merged() with { HeadCommit = HostingP0Data.Head } : HostingP0Data.Open()));
        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        for (var i = 0; i < 6; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(RemoteWorkState.Done, (await rig.BoxAsync()).StatusOf(ticket.Id)!.State);
        Assert.Equal(1, rig.Trace.Count("gh:merge"));
        Assert.Contains(HostingOperationKind.MergePullRequest, Done(rig));
    }

    [Fact]
    public void AChangeOfTheBaseBranchIsAChangeOfTheReconciler()
    {
        var recorded = HostingP0Data.Open();
        var moved = recorded with { BaseBranch = RemoteBranch.TryParse("release")!, UpdatedAt = recorded.UpdatedAt.AddMinutes(1) };

        Assert.Equal([RemoteChangeKind.BaseChanged], RemoteReconciler.Reconcile(recorded, moved).Changes);
    }

    // ---- 4: a revocation counts from the instant it is asked, and survives a restart ----

    [Fact]
    public async Task ARevocationCountsAtOnceEvenWithTheProjectBusyAndSurvivesARestart()
    {
        await using var rig = new PublicationRig();
        Grants(rig, Grant(HostingOperationClass.PublishRunBranch));
        var run = rig.Activity.TryEnter("p1", ProjectActivityKind.Run);

        await rig.Gate.RevokeAsync(PublicationRig.Project, "g-PublishRunBranch", CancellationToken.None);
        var asked = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.Equal(HostingAuthorizationSource.Approval, asked.Source);
        Assert.Equal(0, rig.Log.Count(DecisionKind.HostingPolicyRevoked));
        Assert.Empty(await rig.Gate.GrantsAsync(PublicationRig.Project, CancellationToken.None));
        var listed = Assert.Single(rig.Sync.Pending("p1"));
        Assert.Equal(PublicationCodes.AnnotationPending, listed.Message!.Code);
        await rig.IdleAsync();
        Assert.Equal(["g-PublishRunBranch"], rig.Store.State.PendingRevocations);

        // The app closes with the project still busy, and opens again with it free: the revocation is written, and the grant never counted in between.
        var saved = rig.Store.State;
        run!.Dispose();
        await using var restarted = new PublicationRig(saved);
        foreach (var entry in rig.Log.Entries)
        {
            restarted.Log.Add(entry.Kind, entry.Actor, entry.Summary, DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson)!);
        }

        var again = await restarted.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);
        Assert.Equal(HostingAuthorizationSource.Approval, again.Source);
        await restarted.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        await restarted.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, restarted.Log.Count(DecisionKind.HostingPolicyRevoked));
        Assert.Empty((await restarted.BoxAsync()).PendingRevocations);
    }

    [Fact]
    public async Task AGrantGivenWhileTheProjectIsBusyIsListedAsPendingAndNotPersisted()
    {
        await using var rig = new PublicationRig();
        using var run = rig.Activity.TryEnter("p1", ProjectActivityKind.Run);

        var grant = await rig.Gate.GrantAsync(PublicationRig.Project, HostingOperationClass.PublishRunBranch, HostingP0Data.Repository, null, CancellationToken.None);

        Assert.Null(grant);
        Assert.Equal(PublicationCodes.AnnotationPending, Assert.Single(rig.Sync.Pending("p1")).Message!.Code);
        await rig.IdleAsync();
        Assert.Empty(rig.Store.State.PendingRevocations ?? []);
        Assert.DoesNotContain(rig.Store.State.Pending, p => p.Stage.Contains("grant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGrantOrRevocationWrittenByAnyoneButAPersonDoesNotCount()
    {
        await using var rig = new PublicationRig();
        rig.Log.Add(DecisionKind.HostingPolicyGranted, new Actor(ActorKind.Agent, "an-agent"), "grant", HostingPolicyGrantedPayload.Granting(Grant(HostingOperationClass.PublishRunBranch)));

        var result = await rig.Gate.AuthorizeAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), CancellationToken.None);

        Assert.Equal(HostingAuthorizationSource.Approval, result.Source);
    }

    // ---- 5: the orderly close writes what is parked ----

    [Fact]
    public async Task ClosingInAnOrderlyWayTriesOnceMoreToWriteWhatWasDoneOnThePlatform()
    {
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        var box = await rig.BoxAsync();
        Assert.Equal(RemoteWorkState.Done, box.StatusOf(ticket.Id)!.State);
        Assert.True(box.ParkedCount > 0);
        var before = rig.Log.Count(DecisionKind.HostingOperationPerformed);

        await rig.DisposeAsync();

        Assert.True(rig.Log.Count(DecisionKind.HostingOperationPerformed) > before);
        Assert.Null(rig.Activity.Current("p1"));
    }

    // ---- 6 and 7: the gate is obeyed, and an answer that says nothing certain is not a failure ----

    [Fact]
    public async Task AVerdictChallengedAfterThePushStopsTheOpeningOfThePullRequest()
    {
        await using var rig = new PublicationRig();
        rig.Git.OnPush = () => rig.Delivery.Ready = false;

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        var status = (await rig.BoxAsync()).StatusOf(ticket.Id)!;
        Assert.Equal(RemoteWorkState.Failed, status.State);
        Assert.Equal(PublicationCodes.VerdictNotApproved, status.Message!.Code);
        Assert.Equal(0, rig.Trace.Count("gh:open"));
        Assert.Equal(0, rig.Trace.Count("gh:update"));
    }

    [Theory]
    [InlineData(HostingOutcome.Unreachable)]
    [InlineData(HostingOutcome.Forbidden)]
    [InlineData(HostingOutcome.InvalidAnswer)]
    public async Task APullRequestThatTheAnswerDoesNotConfirmButExistsIsTakenAsOpened(HostingOutcome outcome)
    {
        await using var rig = new PublicationRig();
        rig.Provider.Opened = new HostingResult<PullRequestRef>(outcome);
        rig.Provider.FoundQueue.Enqueue(new HostingResult<PullRequestRef?>(HostingOutcome.Ok, null));
        rig.Provider.FoundQueue.Enqueue(new HostingResult<PullRequestRef?>(HostingOutcome.Ok, HostingP0Data.PullRequest));

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(RemoteWorkState.Done, (await rig.BoxAsync()).StatusOf(ticket.Id)!.State);
        Assert.Equal(1, rig.Trace.Count("gh:open"));
        Assert.Contains(HostingOperationKind.OpenPullRequest, Done(rig));
    }

    [Fact]
    public async Task ADeletionWhoseAnswerWasLostIsDoneWhenTheBranchIsGone()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Deleted = new HostingResult<bool>(HostingOutcome.Unreachable);
        rig.Git.Reads.Enqueue(HostingP0Data.Head);
        rig.Git.Reads.Enqueue(null);

        var ticket = await rig.Service.DeleteRunBranchAsync(PublicationRig.Project, HostingP0Data.Run, CancellationToken.None);
        var box = await rig.BoxAsync();
        for (var i = 0; i < 10 && box.StatusOf(ticket.Id)!.State != RemoteWorkState.Done; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(RemoteWorkState.Done, box.StatusOf(ticket.Id)!.State);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(HostingOperationKind.DeleteRemoteBranch, Done(rig));
    }

    [Fact]
    public async Task ADeletionThatCannotBeConfirmedStaysPendingAndIsReadBeforeItIsAskedAgain()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Deleted = new HostingResult<bool>(HostingOutcome.Unreachable);
        rig.Git.RemoteHead = HostingP0Data.Head;

        var ticket = await rig.Service.DeleteRunBranchAsync(PublicationRig.Project, HostingP0Data.Run, CancellationToken.None);
        var box = await rig.BoxAsync();
        for (var i = 0; i < 10 && box.StatusOf(ticket.Id)!.State != RemoteWorkState.PendingSync; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);
        var reads = rig.Trace.Count("git:read");

        // The branch is gone by the next round: it is read first and not deleted again.
        rig.Git.Reads.Enqueue(null);
        for (var i = 0; i < 10 && box.StatusOf(ticket.Id)!.State != RemoteWorkState.Done; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromMinutes(1));
        }

        Assert.True(rig.Trace.Count("git:read") > reads);
        Assert.Equal(RemoteWorkState.Done, box.StatusOf(ticket.Id)!.State);
    }

    // ---- 8: the reviewers are in the tie ----

    [Fact]
    public async Task TheApprovalToAskForReviewersSaysWhoIsWarned()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        var people = new[] { RemoteLogin.TryParse("zoe")!, RemoteLogin.TryParse("ana")! };

        await rig.Service.RequestReviewersAsync(PublicationRig.Project, HostingP0Data.PullRequest, people, CancellationToken.None);

        var request = Assert.Single(rig.Approvals.Requests);
        Assert.Equal(HostingActions.PullRequestWriteAction, request.Action);
        Assert.Contains("reviewers=ana,zoe", request.Description, StringComparison.Ordinal);
        Assert.Contains("reviewers=ana,zoe", request.Binding, StringComparison.Ordinal);
    }

    // ---- 9 and the low ones: texts, the fence ----

    [Fact]
    public void FormatAndBidirectionalCharactersAreTakenOutOfWhatIsFenced()
    {
        Assert.Equal("`ab`", PullRequestTextComposer.Fence("a‎b"));
        Assert.Equal("`ab`", PullRequestTextComposer.Fence("a‮b"));
        Assert.Equal("`ab`", PullRequestTextComposer.Fence("a​b"));
    }

    // ---- 11: the counts of the card ----

    private static CommitNode Node(string id, params string[] parents) => new(id, parents, HostingP0Data.At, "subject", "author", []);

    private static string Id(char c) => new(c, 40);

    [Fact]
    public async Task TheCardCountsTheCommitsOfTheRunAndThoseThatWouldGoUpAgainstTheBaseOfTheRemote()
    {
        await using var rig = new PublicationRig();
        // base B <- own X <- run r1 <- run r2 (newest first in the graph)
        rig.History.Graph = [Node(Id('d'), Id('c')), Node(Id('c'), Id('b')), Node(Id('b'), Id('a')), Node(Id('a'))];
        rig.Annotator.OfTheRun.Add(Id('d'));
        rig.Annotator.OfTheRun.Add(Id('c'));
        rig.Git.RemoteHead = Id('a');

        var preview = await rig.Service.PreviewAsync(PublicationRig.Project, HostingP0Data.Run, Main, CancellationToken.None);

        Assert.Equal(2, preview.SystemCommits);
        Assert.Equal(1, preview.OwnUnpublishedCommits);
    }

    [Fact]
    public async Task TheCardSaysItCannotKnowWhenTheBaseHeadIsNotInTheHistoryRead()
    {
        await using var rig = new PublicationRig();
        rig.History.Graph = [Node(Id('d'), Id('c')), Node(Id('c'))];
        rig.Annotator.OfTheRun.Add(Id('d'));
        rig.Git.RemoteHead = Id('9');

        var preview = await rig.Service.PreviewAsync(PublicationRig.Project, HostingP0Data.Run, Main, CancellationToken.None);

        Assert.Equal(1, preview.SystemCommits);
        Assert.Null(preview.OwnUnpublishedCommits);
    }

    [Fact]
    public async Task WithTheHistoryUnreadableTheCardIsNotReadyAndSaysWhy()
    {
        await using var rig = new PublicationRig();
        rig.History.Fails = true;

        var preview = await rig.Service.PreviewAsync(PublicationRig.Project, HostingP0Data.Run, Main, CancellationToken.None);

        Assert.False(preview.Ready);
        Assert.Equal(PublicationCodes.HistoryUnreadable, preview.NotReadyReason!.Code);
    }
}
