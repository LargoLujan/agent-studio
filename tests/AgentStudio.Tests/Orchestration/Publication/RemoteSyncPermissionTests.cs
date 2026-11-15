using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>P85 and P94: a manual click reports what is followed; withdrawing the switch cancels only automatic reads.</summary>
public sealed class RemoteSyncPermissionTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Follow(PublicationRig rig)
    {
        await rig.Sync.FollowAsync(PublicationRig.Project, HostingP0Data.PullRequest, HostingP0Data.Run, Ct);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnEmptyMailboxReportsZeroWithoutInventingAGoodSyncOrAConnection()
    {
        await using var rig = new PublicationRig();

        var count = await rig.Sync.SyncNowAsync(PublicationRig.Project, Ct);
        await rig.IdleAsync();

        Assert.Equal(0, count);
        Assert.Equal(0, rig.Trace.Count("gh:observe"));
        Assert.Equal(new RemoteSyncHealth(null, null, null), rig.Sync.Health("p1"));
        Assert.Empty(rig.Log.Observations());
        Assert.Null(rig.Store.State.LastGoodSync);
    }

    [Fact]
    public async Task AClosedMailboxDoesNotReportASuccessfulEmptyClick()
    {
        await using var rig = new PublicationRig();
        var box = await rig.BoxAsync();
        await box.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => box.SyncNowAsync());
    }

    [Fact]
    public async Task TheCountComesFromTheMailboxAndAnEmptyClickPreservesTheLastRealSync()
    {
        await using var rig = new PublicationRig();
        await Follow(rig);

        Assert.Equal(1, await rig.Sync.SyncNowAsync(PublicationRig.Project, Ct));
        var health = rig.Sync.Health("p1");
        Assert.NotNull(health.LastGoodSync);
        await rig.Sync.UnfollowAsync(PublicationRig.Project, HostingP0Data.PullRequest, Ct);
        rig.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0, await rig.Sync.SyncNowAsync(PublicationRig.Project, Ct));
        Assert.Equal(health, rig.Sync.Health("p1"));
        Assert.Equal(1, rig.Trace.Count("gh:observe"));
    }

    [Fact]
    public async Task WithdrawingThePermissionCancelsTheActiveAutomaticReadWithoutChangingHealth()
    {
        await using var rig = new PublicationRig();
        await Follow(rig);
        rig.Provider.ObserveDelay = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var health = rig.Sync.Health("p1");

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Provider.ObserveEntered.Task.WaitAsync(Guard, Ct);
        var reading = rig.Provider.LastObserveToken;
        rig.Policy.Allowed = false;
        await rig.IdleAsync();

        Assert.True(reading.IsCancellationRequested);
        Assert.Equal(health, rig.Sync.Health("p1"));
        Assert.Empty(rig.Log.Observations());
        var followed = Assert.Single(rig.Store.State.Followed);
        Assert.Empty(followed.Cursor.ETags);
        Assert.Null(followed.Cursor.LastSnapshot);
        Assert.Null(followed.PendingConfirmation);
    }

    [Fact]
    public async Task ALateAnswerIsDiscardedEvenIfThePersonHasAlreadyAllowedANewRound()
    {
        await using var rig = new PublicationRig();
        await Follow(rig);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.ObserveDelay = _ => release.Task; // A provider that ignores cancellation must still lose its answer.
        var snapshot = HostingP0Data.Open(HostingP0Data.Head);
        var cursor = new ObservationCursor(new Dictionary<ObservedResource, string> { [ObservedResource.PullRequest] = "\"late\"" }, snapshot);
        rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.Ok, new PullRequestObservation(snapshot, cursor, RemoteDisplayTexts.None));
        var health = rig.Sync.Health("p1");

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        try
        {
            await rig.Provider.ObserveEntered.Task.WaitAsync(Guard, Ct);
            var withdrawn = rig.Provider.LastObserveToken;
            rig.Policy.Allowed = false;
            rig.Policy.Allowed = true;
            Assert.True(withdrawn.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
        }

        await rig.IdleAsync();
        Assert.Equal(health, rig.Sync.Health("p1"));
        Assert.Empty(rig.Log.Observations());
        var followed = Assert.Single(rig.Store.State.Followed);
        Assert.Empty(followed.Cursor.ETags);
        Assert.Null(followed.Cursor.LastSnapshot);
        Assert.Null(followed.PendingConfirmation);

        // A later round gets a fresh token and starts from the cursor preceding the withdrawn answer.
        rig.Provider.ObserveDelay = null;
        rig.Provider.Observe = (_, previous) =>
        {
            Assert.Empty(previous!.ETags);
            Assert.Null(previous.LastSnapshot);
            return new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(snapshot));
        };
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.IdleAsync();
        Assert.False(rig.Provider.LastObserveToken.IsCancellationRequested);
        Assert.Single(rig.Log.Observations());
        Assert.NotNull(rig.Sync.Health("p1").LastGoodSync);
    }

    [Fact]
    public async Task WithdrawingTheSwitchDoesNotCancelAManualClickAlreadyInProgress()
    {
        await using var rig = new PublicationRig();
        await Follow(rig);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.ObserveDelay = token => release.Task.WaitAsync(token);
        rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(HostingP0Data.Open(HostingP0Data.Head)));
        var syncing = rig.Sync.SyncNowAsync(PublicationRig.Project, Ct);
        try
        {
            await rig.Provider.ObserveEntered.Task.WaitAsync(Guard, Ct);
            rig.Policy.Allowed = false;
            Assert.False(rig.Provider.LastObserveToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(1, await syncing.WaitAsync(Guard, Ct));
        Assert.Single(rig.Log.Observations());
        Assert.NotNull(rig.Sync.Health("p1").LastGoodSync);
        Assert.Equal(1, await rig.Sync.SyncNowAsync(PublicationRig.Project, Ct));
    }

    [Fact]
    public async Task AnAcceptedReadCanFinishItsLocalAnnotationAfterTheSwitchIsWithdrawn()
    {
        await using var rig = new PublicationRig();
        await Follow(rig);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Log.Hold = release;
        rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(HostingP0Data.Open(HostingP0Data.Head)));
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        try
        {
            await rig.Log.Entered.Task.WaitAsync(Guard, Ct);
            rig.Policy.Allowed = false;
            Assert.False(rig.Provider.LastObserveToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
        }

        await rig.IdleAsync();
        Assert.Single(rig.Log.Observations());
        Assert.Equal(1, rig.Trace.Count("gh:observe"));
    }

    [Fact]
    public async Task AFailingCancellationCallbackDoesNotFailThePermissionOrTheMailbox()
    {
        await using var rig = new PublicationRig();
        await Follow(rig);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.ObserveDelay = async token =>
        {
            using var registration = token.Register(() =>
            {
                callback.TrySetResult();
                throw new InvalidOperationException("The provider's cancellation callback failed.");
            });
            ready.TrySetResult();
            await callback.Task;
            token.ThrowIfCancellationRequested();
        };
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await ready.Task.WaitAsync(Guard, Ct);

        rig.Policy.Allowed = false;
        await callback.Task.WaitAsync(Guard, Ct);
        await rig.IdleAsync();
        Assert.Null(rig.Sync.Health("p1").LastOutcome);

        rig.Provider.ObserveDelay = null;
        Assert.Equal(1, await rig.Sync.SyncNowAsync(PublicationRig.Project, Ct));
        Assert.NotNull(rig.Sync.Health("p1").LastGoodSync);
    }

    [Fact]
    public async Task AnotherProjectsNotificationDoesNotCancelThisReadAndClosingUnsubscribes()
    {
        await using var rig = new PublicationRig();
        await Follow(rig);
        rig.Provider.ObserveDelay = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Provider.ObserveEntered.Task.WaitAsync(Guard, Ct);

        await rig.Policy.SetAsync("another-project", false, Ct);
        Assert.False(rig.Provider.LastObserveToken.IsCancellationRequested);
        Assert.Equal(1, rig.Policy.Subscribers);
        rig.Policy.Announce("p1");
        await rig.IdleAsync();
        Assert.True(rig.Provider.LastObserveToken.IsCancellationRequested);

        await (await rig.BoxAsync()).DisposeAsync();
        Assert.Equal(0, rig.Policy.Subscribers);
    }
}
