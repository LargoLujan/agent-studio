using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>The last round of the review of WP3: a write that was sent is never cut by the close, the last flush is bounded and does not outlive the close, and a merge is credited only when it could be ours.</summary>
public sealed class PublicationFourthRoundTests
{
    private static HostingResult<PullRequestObservation> Seen(RemoteSnapshot snapshot) => new(HostingOutcome.Ok, PublicationRig.Observation(snapshot));

    private static RemoteSnapshot Open() => HostingP0Data.Open();

    private static RemoteSnapshot Merged() => HostingP0Data.Merged() with { HeadCommit = HostingP0Data.Head, UpdatedAt = HostingP0Data.At.AddMinutes(1) };

    private static IReadOnlyList<HostingOperationKind> Done(PublicationRig rig) =>
        [.. RemoteLogReading.ReadEntries(rig.Log.Entries).Operations.Select(o => o.Payload.Operation)];

    private static async Task<PublicationRig> RigOfAMergeAsync()
    {
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(Open());
        return rig;
    }

    private static async Task SettleAsync(PublicationRig rig, int seconds = 12)
    {
        for (var i = 0; i < seconds; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
        }
    }

    // ---- 1: the close does not cut a write that was sent ----

    [Fact]
    public async Task ClosingWithAMergeInProgressLeavesItAnnotatedOrParkedAndNeverLost()
    {
        var rig = await RigOfAMergeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.MergeHold = _ => release.Task;
        rig.Provider.Observe = (_, _) => Seen(rig.Trace.Count("gh:merge") > 0 ? Merged() : Open());
        var merging = rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        await rig.Provider.MergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The close starts while the merge is out; the merge is let finish, and it is not given the token of the stop.
        var closing = rig.DisposeAsync().AsTask();
        release.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await merging.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(rig.Provider.LastMergeToken.CanBeCanceled);
        Assert.Contains(HostingOperationKind.MergePullRequest, Done(rig));
        Assert.Null(rig.Activity.Current("p1"));
    }

    // ---- 2: the last flush is bounded and does not outlive the close ----

    [Fact]
    public async Task TheLastFlushIsCancelledByItsLimitAndNothingIsWrittenOnceTheCloseReturned()
    {
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        await rig.Service.PublishAsync(new PublicationRequest(PublicationRig.Project, HostingP0Data.Run, HostingP0Data.Main, new PullRequestText("t", "b"), false), CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
        var box = await rig.BoxAsync();
        Assert.True(box.ParkedCount >= 2, "several entries are parked before the close");
        var before = rig.Log.Count(DecisionKind.HostingOperationPerformed);
        var timers = rig.Time.Timers;
        rig.Log.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = rig.Log.Writes;

        var closing = rig.DisposeAsync().AsTask();
        await rig.Log.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The first write of the flush is held; the limit passes on the clock of the test, which cancels the flush; then the write ends.
        await rig.Time.WaitForTimerAsync(timers + 1);
        rig.Time.Advance(PublicationCodes.ClosingLimit + TimeSpan.FromSeconds(1));
        rig.Log.Hold.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var written = rig.Log.Writes;
        Assert.Equal(writes + 1, written);
        Assert.Equal(before + 1, rig.Log.Count(DecisionKind.HostingOperationPerformed));
        await Task.Delay(TimeSpan.Zero, TestContext.Current.CancellationToken);
        Assert.Equal(written, rig.Log.Writes);
    }

    // ---- 3: a merge is credited only if it could be ours ----

    [Fact]
    public async Task AMergeRefusedClearlyIsNotCreditedWhenAnotherPersonMergesRightAfter()
    {
        await using var rig = await RigOfAMergeAsync();
        rig.Provider.Merged = new HostingResult<MergeOutcome>(HostingOutcome.Refused);
        rig.Provider.Observe = (_, _) => Seen(rig.Trace.Count("gh:merge") > 0 ? Merged() : Open());

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        await SettleAsync(rig);

        var status = (await rig.BoxAsync()).StatusOf(ticket.Id)!;
        Assert.Equal(RemoteWorkState.Failed, status.State);
        Assert.Equal(PublicationCodes.ProviderRefused, status.Message!.Code);
        Assert.DoesNotContain(HostingOperationKind.MergePullRequest, Done(rig));
    }

    [Fact]
    public async Task ARateLimitedMergeIsNotCreditedAtTheRetryEitherWhenItIsMergedMeanwhile()
    {
        await using var rig = await RigOfAMergeAsync();
        rig.Provider.Merged = new HostingResult<MergeOutcome>(HostingOutcome.RateLimited, RetryAt: rig.Time.GetUtcNow().AddSeconds(30));
        rig.Provider.Observe = (_, _) => Seen(rig.Trace.Count("gh:merge") > 0 ? Merged() : Open());

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        await SettleAsync(rig, 60);

        var status = (await rig.BoxAsync()).StatusOf(ticket.Id)!;
        Assert.Equal(RemoteWorkState.Done, status.State);
        Assert.Equal(PublicationCodes.AlreadyMerged, status.Message!.Code);
        Assert.DoesNotContain(HostingOperationKind.MergePullRequest, Done(rig));
    }

    [Fact]
    public async Task AClosureRefusedClearlyIsNotCreditedWhenAnotherPersonClosesRightAfter()
    {
        await using var rig = await RigOfAMergeAsync();
        rig.Provider.Closed = new HostingResult<bool>(HostingOutcome.Forbidden);
        rig.Provider.Observe = (_, _) => Seen(rig.Trace.Count("gh:close") > 0 ? Open() with { State = PullRequestState.Closed, UpdatedAt = HostingP0Data.At.AddMinutes(1) } : Open());

        var ticket = await rig.Service.CloseAsync(PublicationRig.Project, HostingP0Data.PullRequest, CancellationToken.None);
        await SettleAsync(rig);

        Assert.Equal(RemoteWorkState.Failed, (await rig.BoxAsync()).StatusOf(ticket.Id)!.State);
        Assert.DoesNotContain(HostingOperationKind.ClosePullRequest, Done(rig));
    }

    [Fact]
    public async Task AClosureWhoseAnswerWasLostAndThePullRequestIsClosedIsCredited()
    {
        await using var rig = await RigOfAMergeAsync();
        rig.Provider.Closed = new HostingResult<bool>(HostingOutcome.Unreachable);
        rig.Provider.Observe = (_, _) => Seen(rig.Trace.Count("gh:close") > 0 ? Open() with { State = PullRequestState.Closed, UpdatedAt = HostingP0Data.At.AddMinutes(1) } : Open());

        var ticket = await rig.Service.CloseAsync(PublicationRig.Project, HostingP0Data.PullRequest, CancellationToken.None);
        await SettleAsync(rig);

        Assert.Equal(RemoteWorkState.Done, (await rig.BoxAsync()).StatusOf(ticket.Id)!.State);
        Assert.Contains(HostingOperationKind.ClosePullRequest, Done(rig));
    }

    // ---- 4: after calling, the platform is read before the gate decides ----

    [Fact]
    public async Task AMergeAskedForIsAnnotatedEvenIfTheGateRefusesAtTheRetry()
    {
        await using var rig = await RigOfAMergeAsync();
        var released = false;
        rig.Provider.Merged = new HostingResult<MergeOutcome>(HostingOutcome.Unreachable);
        rig.Provider.Observe = (_, _) => released
            ? Seen(Merged())
            : rig.Trace.Count("gh:merge") > 0 ? new HostingResult<PullRequestObservation>(HostingOutcome.Unreachable) : Seen(Open());

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        var box = await rig.BoxAsync();
        for (var i = 0; i < 12 && box.StatusOf(ticket.Id)!.State != RemoteWorkState.PendingSync; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);

        // The verdict is challenged after the merge happened: the gate refuses the retry, but what happened is annotated first.
        rig.Delivery.Ready = false;
        released = true;
        await rig.AdvanceAsync(TimeSpan.FromMinutes(1));
        await SettleAsync(rig);

        Assert.Equal(RemoteWorkState.Done, box.StatusOf(ticket.Id)!.State);
        Assert.Equal(1, rig.Trace.Count("gh:merge"));
        Assert.Contains(HostingOperationKind.MergePullRequest, Done(rig));
    }

    // ---- 5: the signal of rest is a real one ----

    [Fact]
    public async Task TheSignalOfRestIsCompleteWhenIdleIncompleteWhileBusyAndCompletesWhenTheWorkEnds()
    {
        await using var rig = new PublicationRig();
        var box = await rig.BoxAsync();
        Assert.True(box.WhenIdleAsync().IsCompleted);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.ObserveDelay = _ => release.Task;
        rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.NotModified);
        await rig.Sync.FollowAsync(PublicationRig.Project, HostingP0Data.PullRequest, HostingP0Data.Run, CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        var sync = rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        await rig.Provider.ObserveEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var signal = box.WhenIdleAsync();
        Assert.False(signal.IsCompleted);
        release.SetResult();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await sync.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(box.WhenIdleAsync().IsCompleted);
    }
}
