using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>The third round of the review of WP3: the gate at every retry, an orderly close that does not hang, the file that writes no history, not taking credit for what another did.</summary>
public sealed class PublicationThirdRoundTests
{
    private static readonly RemoteBranch Main = HostingP0Data.Main;

    private static PublicationRequest Request() => new(PublicationRig.Project, HostingP0Data.Run, Main, new PullRequestText("title", "body"), false);

    private static HostingPolicyGrant Grant(HostingOperationClass @class) =>
        new("g-" + @class, @class, HostingP0Data.Repository, null, "person", HostingP0Data.At);

    private static void Grants(PublicationRig rig, params HostingPolicyGrant[] grants)
    {
        foreach (var grant in grants)
        {
            rig.Log.Add(DecisionKind.HostingPolicyGranted, new Actor(ActorKind.User, "person"), "grant", HostingPolicyGrantedPayload.Granting(grant));
        }
    }

    private static IReadOnlyList<HostingOperationKind> Done(PublicationRig rig) =>
        [.. RemoteLogReading.ReadEntries(rig.Log.Entries).Operations.Select(o => o.Payload.Operation)];

    private static HostingResult<PullRequestObservation> Seen(RemoteSnapshot snapshot) => new(HostingOutcome.Ok, PublicationRig.Observation(snapshot));

    // ---- 1: the whole gate at every retry of the verdict ----

    [Fact]
    public async Task ARetryOfTheVerdictPassesTheWholeGateAgainAndAWithdrawnGrantStopsIt()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        Grants(rig, Grant(HostingOperationClass.PublishRunBranch), Grant(HostingOperationClass.PullRequestWrite));
        rig.Provider.VerdictResult = new HostingResult<PublishedVerdict>(HostingOutcome.Unreachable);

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        var box = await rig.BoxAsync();
        for (var i = 0; i < 6 && box.StatusOf(ticket.Id)!.State != RemoteWorkState.PendingSync; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);
        Assert.Equal(1, rig.Trace.Count("gh:verdict"));

        // Between the first attempt and the retry the grant is withdrawn: nothing is published.
        await rig.Gate.RevokeAsync(PublicationRig.Project, "g-" + HostingOperationClass.PullRequestWrite, CancellationToken.None);
        rig.Provider.VerdictResult = new HostingResult<PublishedVerdict>(HostingOutcome.Ok, new PublishedVerdict(true, 7));
        await rig.AdvanceAsync(TimeSpan.FromMinutes(1));

        var status = box.StatusOf(ticket.Id)!;
        Assert.Equal(RemoteWorkState.Failed, status.State);
        Assert.Equal(PublicationCodes.NeedsApproval, status.Message!.Code);
        Assert.Equal(1, rig.Trace.Count("gh:verdict"));
    }

    [Fact]
    public async Task ARetryOfTheVerdictStopsWhenTheVerdictWasChallengedMeanwhile()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Provider.VerdictResult = new HostingResult<PublishedVerdict>(HostingOutcome.Unreachable);

        var ticket = await rig.Service.PublishAsync(Request(), CancellationToken.None);
        var box = await rig.BoxAsync();
        for (var i = 0; i < 6 && box.StatusOf(ticket.Id)!.State != RemoteWorkState.PendingSync; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        rig.Log.Verifies = false;
        await rig.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(RemoteWorkState.Failed, box.StatusOf(ticket.Id)!.State);
        Assert.Equal(1, rig.Trace.Count("gh:verdict"));
    }

    // ---- 2: an orderly close does not hang on a long call ----

    [Fact]
    public async Task ClosingWithALongCallOfTheProviderInProgressEndsAndWritesWhatWasParked()
    {
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        await rig.Service.PublishAsync(Request(), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        var box = await rig.BoxAsync();
        Assert.True(box.ParkedCount > 0);
        var before = rig.Log.Count(DecisionKind.HostingOperationPerformed);
        rig.Provider.ObserveDelay = ct => Task.Delay(Timeout.Infinite, ct);
        rig.Provider.Observe = (_, _) => Seen(HostingP0Data.Open());
        var sync = rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        while (rig.Trace.Count("gh:observe") == 0)
        {
            await Task.Yield();
        }

        await rig.DisposeAsync();

        Assert.True(sync.IsCompleted);
        Assert.True(rig.Log.Count(DecisionKind.HostingOperationPerformed) > before);
        Assert.Null(rig.Activity.Current("p1"));
    }

    // ---- 3: the file writes no history ----

    [Fact]
    public async Task RevocationsOfIdsThatAreNoGrantAreDroppedWithoutAnEntry()
    {
        var state = RemoteSyncState.Empty with { PendingRevocations = ["invented-1", "invented-2"] };
        await using var rig = new PublicationRig(state);

        await rig.Sync.AttachAsync(PublicationRig.Project, CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, rig.Log.Count(DecisionKind.HostingPolicyRevoked));
        Assert.Empty((await rig.BoxAsync()).PendingRevocations);
        await rig.IdleAsync();
        Assert.Empty(rig.Store.State.PendingRevocations ?? []);
    }

    [Fact]
    public async Task ARevocationOfARealGrantIsStillWrittenAfterARestart()
    {
        await using var rig = new PublicationRig(RemoteSyncState.Empty with { PendingRevocations = ["g-" + HostingOperationClass.PublishRunBranch] });
        Grants(rig, Grant(HostingOperationClass.PublishRunBranch));

        await rig.Sync.AttachAsync(PublicationRig.Project, CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, rig.Log.Count(DecisionKind.HostingPolicyRevoked));
        Assert.Empty((await rig.BoxAsync()).PendingRevocations);
    }

    // ---- 4: not taking credit for what another did ----

    private static async Task<PublicationRig> RigOfAMergeAsync()
    {
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        return rig;
    }

    [Fact]
    public async Task APullRequestAlreadyMergedByAnotherWayIsNotCreditedToThisApproval()
    {
        await using var rig = await RigOfAMergeAsync();
        rig.Provider.Observe = (_, _) => Seen(HostingP0Data.Merged() with { HeadCommit = HostingP0Data.Head, UpdatedAt = HostingP0Data.At.AddMinutes(1) });

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        for (var i = 0; i < 6; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        var status = (await rig.BoxAsync()).StatusOf(ticket.Id)!;
        Assert.Equal(RemoteWorkState.Done, status.State);
        Assert.Equal(PublicationCodes.AlreadyMerged, status.Message!.Code);
        Assert.Equal(0, rig.Trace.Count("gh:merge"));
        Assert.DoesNotContain(HostingOperationKind.MergePullRequest, Done(rig));
    }

    [Fact]
    public async Task APullRequestAlreadyClosedByAnotherWayIsNotCreditedToThisApproval()
    {
        await using var rig = await RigOfAMergeAsync();
        rig.Provider.Observe = (_, _) => Seen(HostingP0Data.Open() with { State = PullRequestState.Closed, UpdatedAt = HostingP0Data.At.AddMinutes(1) });

        var ticket = await rig.Service.CloseAsync(PublicationRig.Project, HostingP0Data.PullRequest, CancellationToken.None);
        for (var i = 0; i < 6; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        var status = (await rig.BoxAsync()).StatusOf(ticket.Id)!;
        Assert.Equal(RemoteWorkState.Done, status.State);
        Assert.Equal(PublicationCodes.AlreadyClosed, status.Message!.Code);
        Assert.Equal(0, rig.Trace.Count("gh:close"));
        Assert.DoesNotContain(HostingOperationKind.ClosePullRequest, Done(rig));
    }

    [Fact]
    public async Task AClosureThatThisFlowDidIsAnnotatedAfterTheObservationConfirmsIt()
    {
        await using var rig = await RigOfAMergeAsync();
        rig.Provider.Observe = (_, _) => Seen(
            rig.Trace.Count("gh:close") > 0
                ? HostingP0Data.Open() with { State = PullRequestState.Closed, UpdatedAt = HostingP0Data.At.AddMinutes(1) }
                : HostingP0Data.Open());

        var ticket = await rig.Service.CloseAsync(PublicationRig.Project, HostingP0Data.PullRequest, CancellationToken.None);
        for (var i = 0; i < 8; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(RemoteWorkState.Done, (await rig.BoxAsync()).StatusOf(ticket.Id)!.State);
        Assert.Equal(1, rig.Trace.Count("gh:close"));
        Assert.Contains(HostingOperationKind.ClosePullRequest, Done(rig));
    }

    // ---- 6: a merge whose reading fails on a retry stays pending ----

    [Fact]
    public async Task AMergeWhoseReadingFailsOnTheRetryStaysPendingAndTheNextRoundAnnotatesIt()
    {
        await using var rig = await RigOfAMergeAsync();
        var script = new Queue<HostingResult<PullRequestObservation>>(
        [
            Seen(HostingP0Data.Open()), // before asking for the approval
            Seen(HostingP0Data.Open()), // after it
            Seen(HostingP0Data.Open()), // the flow, before it merges
            new HostingResult<PullRequestObservation>(HostingOutcome.Unreachable), // after asking for the merge: the answer was lost
            new HostingResult<PullRequestObservation>(HostingOutcome.Unreachable), // the retry cannot read either
        ]);
        rig.Provider.Merged = new HostingResult<MergeOutcome>(HostingOutcome.Unreachable);
        rig.Provider.Observe = (_, _) => script.Count > 0 ? script.Dequeue() : Seen(HostingP0Data.Merged() with { HeadCommit = HostingP0Data.Head, UpdatedAt = HostingP0Data.At.AddMinutes(1) });

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        var box = await rig.BoxAsync();
        for (var i = 0; i < 6 && box.StatusOf(ticket.Id)!.State != RemoteWorkState.PendingSync; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);
        await rig.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);

        await rig.AdvanceAsync(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 4; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(RemoteWorkState.Done, box.StatusOf(ticket.Id)!.State);
        Assert.Equal(1, rig.Trace.Count("gh:merge"));
        Assert.Contains(HostingOperationKind.MergePullRequest, Done(rig));
    }

    // ---- 5: the card ----

    [Fact]
    public async Task AnEmptyGraphIsNotABranchWithoutCommitsAndTheCardIsNotReady()
    {
        await using var rig = new PublicationRig();
        rig.History.Graph = [];

        var preview = await rig.Service.PreviewAsync(PublicationRig.Project, HostingP0Data.Run, Main, CancellationToken.None);

        Assert.False(preview.Ready);
        Assert.Equal(PublicationCodes.HistoryUnreadable, preview.NotReadyReason!.Code);
    }

    [Fact]
    public void WhenTheReadReachesItsLimitBeforeTheBaseHeadTheOwnCountIsUnknownNotANumber()
    {
        var graph = Enumerable.Range(0, 500).Select(i => new AgentStudio.Application.Git.CommitNode(i.ToString("x40"), [(i + 1).ToString("x40")], HostingP0Data.At, "s", "a", [])).ToList();
        var annotated = graph.Select(c => new AgentStudio.Application.Git.AnnotatedCommit(c, null)).ToList();

        var counts = CommitCounts.Of(graph, annotated, (999).ToString("x40"));

        Assert.Null(counts.OwnUnpublished);
        Assert.Equal(0, counts.System);
    }
}
