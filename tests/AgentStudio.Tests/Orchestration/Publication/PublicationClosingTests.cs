using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>
/// The remains of the mailbox and the close (docs/21 §10.9, P79): what the person asks of the mailbox can be left waiting (the consumer goes on with its stretch of git), a closed mailbox answers
/// «not available» instead of throwing, and no write of a flow starts once the close was asked for, even when it was asked after the check at the start of its stage.
/// </summary>
public sealed class PublicationClosingTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static PublicationRequest Request() => new(PublicationRig.Project, HostingP0Data.Run, HostingP0Data.Main, new PullRequestText("title", "body"), false);

    private static HostingResult<PullRequestObservation> Seen(RemoteSnapshot snapshot) => new(HostingOutcome.Ok, PublicationRig.Observation(snapshot));

    // ---- P79 (a): the wait of whoever asks ends with its token, and a closed mailbox says so ----

    [Fact]
    public async Task ACardThatIsCancelledStopsWaitingWhileTheConsumerGoesOnWithItsStretchOfGit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Git.InspectHold = () => release.Task;
        using var cancel = new CancellationTokenSource();

        var preview = rig.Service.PreviewAsync(PublicationRig.Project, HostingP0Data.Run, HostingP0Data.Main, cancel.Token);
        await rig.Git.InspectEntered.Task.WaitAsync(Guard, ct);
        await cancel.CancelAsync();

        // The card stops waiting at once…
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.WaitAsync(Guard, ct));

        // …and the consumer is still inside git with the project taken: nothing of it was cut.
        Assert.Equal(AgentStudio.Application.Orchestration.ProjectActivityKind.RemoteOperation, rig.Activity.Current("p1"));

        release.SetResult();
        await rig.IdleAsync();
        Assert.Null(rig.Activity.Current("p1"));
    }

    [Fact]
    public async Task AnInspectionThatIsCancelledStopsWaitingBehindTheStretchInProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new PublicationRig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Git.InspectHold = () => release.Task;
        using var cancel = new CancellationTokenSource();

        var inspection = rig.Service.InspectAsync(PublicationRig.Project, cancel.Token);
        await rig.Git.InspectEntered.Task.WaitAsync(Guard, ct);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection.WaitAsync(Guard, ct));
        release.SetResult();
        await rig.IdleAsync();
        Assert.Null(rig.Activity.Current("p1"));
    }

    [Fact]
    public async Task WorkThatWasCancelledBeforeItsTurnIsNeverRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new PublicationRig();
        var box = await rig.BoxAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = box.ExecAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return 1;
        }, ct);
        await entered.Task.WaitAsync(Guard, ct);
        var ran = false;
        using var cancel = new CancellationTokenSource();
        var second = box.ExecAsync(_ =>
        {
            ran = true;
            return Task.FromResult(2);
        }, cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Guard, ct));
        release.SetResult();
        Assert.Equal(1, (await first.WaitAsync(Guard, ct)).Value);
        await rig.IdleAsync();

        Assert.False(ran, "the work of a caller that stopped waiting was run");
    }

    [Fact]
    public async Task AClosedMailboxAnswersNotAvailableInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new PublicationRig();
        var box = await rig.BoxAsync();
        await box.DisposeAsync();

        var answer = await box.ExecAsync(_ => Task.FromResult(7), ct).WaitAsync(Guard, ct);

        Assert.False(answer.Available);
        Assert.Equal(0, rig.Trace.Count("take"));
    }

    [Fact]
    public async Task WorkQueuedWhenTheCloseStartsAnswersNotAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        var box = await rig.BoxAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = box.ExecAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return 1;
        }, ct);
        await entered.Task.WaitAsync(Guard, ct);
        var queued = box.ExecAsync(_ => Task.FromResult(2), ct);

        var closing = box.DisposeAsync().AsTask();
        release.SetResult();
        await closing.WaitAsync(Guard, ct);

        // What was already running ended; what had not started is not run and says so.
        Assert.True((await first.WaitAsync(Guard, ct)).Available);
        Assert.False((await queued.WaitAsync(Guard, ct)).Available);
    }

    [Fact]
    public async Task ThePublicationCardOfAMailboxThatIsClosingSaysSoInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        var box = await rig.BoxAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = box.ExecAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return 1;
        }, ct);
        await entered.Task.WaitAsync(Guard, ct);
        var closing = box.DisposeAsync().AsTask();

        // The card is asked once the close started (the mailbox accepts nothing new): it is «not ready» with the reason, and nothing is thrown.
        var preview = rig.Service.PreviewAsync(PublicationRig.Project, HostingP0Data.Run, HostingP0Data.Main, ct);
        release.SetResult();
        await closing.WaitAsync(Guard, ct);
        await busy.WaitAsync(Guard, ct);
        var card = await preview.WaitAsync(Guard, ct);

        Assert.False(card.Ready);
        Assert.Equal(PublicationCodes.Closing, card.NotReadyReason!.Code);
    }

    // ---- P79 (b): no write starts with the close already asked for ----

    [Fact]
    public async Task ACloseAskedAfterTheLookForThePullRequestOpensNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        var box = await rig.BoxAsync();
        Task? closing = null;

        // The close arrives while the flow looks for the pull request, after the check of the start of the stage and before it would open it.
        rig.Provider.OnFind = () => closing ??= box.DisposeAsync().AsTask();
        var ticket = await rig.Service.PublishAsync(Request(), ct);

        // The courtesy second after the card keeps the first stretch waiting for the project: the clock moves until the flow reaches the platform.
        for (var i = 0; i < 6 && closing is null; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.NotNull(closing);
        await closing.WaitAsync(Guard, ct);

        Assert.Equal(1, rig.Trace.Count("gh:find"));
        Assert.Equal(0, rig.Trace.Count("gh:open"));
        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);
        Assert.Equal(0, rig.Trace.Count("gh:verdict"));
    }

    [Fact]
    public async Task ACloseAskedInTheStageOfTheVerdictPublishesNoVerdictButKeepsThePullRequestFollowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        var box = await rig.BoxAsync();
        Task? closing = null;

        // The pull request is already open: the close arrives while the gate of the verdict asks the platform what it can do, before the verdict would be written.
        rig.Provider.OnProbe = () =>
        {
            if (rig.Trace.Count("gh:open") == 1)
            {
                closing ??= box.DisposeAsync().AsTask();
            }
        };
        var ticket = await rig.Service.PublishAsync(Request(), ct);

        for (var i = 0; i < 6 && closing is null; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.NotNull(closing);
        await closing.WaitAsync(Guard, ct);

        Assert.Equal(1, rig.Trace.Count("gh:open"));
        Assert.Equal(0, rig.Trace.Count("gh:verdict"));
        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);

        // Parked work is not relaunched on its own after a restart: the open pull request has to be followed already, in the state kept at the close.
        Assert.Equal(HostingP0Data.PullRequest, Assert.Single(rig.Store.State.Followed).PullRequest);
    }

    [Fact]
    public async Task ACloseAskedAfterTheLookForTheOpenPullRequestUpdatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Provider.Found = new HostingResult<PullRequestRef?>(HostingOutcome.Ok, HostingP0Data.PullRequest);
        var box = await rig.BoxAsync();
        Task? closing = null;
        rig.Provider.OnFind = () => closing ??= box.DisposeAsync().AsTask();

        var ticket = await rig.Service.PublishAsync(Request(), ct);
        for (var i = 0; i < 6 && closing is null; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        Assert.NotNull(closing);
        await closing.WaitAsync(Guard, ct);

        Assert.Equal(0, rig.Trace.Count("gh:update"));
        Assert.Equal(0, rig.Trace.Count("gh:open"));
        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);
    }

    [Fact]
    public async Task ACloseAskedWhileTheFlowReadsBeforeMergingMergesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        var box = await rig.BoxAsync();
        Task? closing = null;

        // The third read is the one of the flow, right before the merge: the first two are those of the click.
        rig.Provider.Observe = (_, _) =>
        {
            if (rig.Trace.Count("gh:observe") >= 3)
            {
                closing ??= box.DisposeAsync().AsTask();
            }

            return Seen(HostingP0Data.Open());
        };

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, ct);
        await box.WhenIdleAsync().WaitAsync(Guard, ct);
        Assert.NotNull(closing);
        await closing.WaitAsync(Guard, ct);

        Assert.Equal(0, rig.Trace.Count("gh:merge"));
        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);
    }

    [Fact]
    public async Task ACloseAskedWhileTheFlowReadsBeforeClosingThePullRequestClosesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        var box = await rig.BoxAsync();
        Task? closing = null;
        rig.Provider.Observe = (_, _) =>
        {
            closing ??= box.DisposeAsync().AsTask();
            return Seen(HostingP0Data.Open());
        };

        var ticket = await rig.Service.CloseAsync(PublicationRig.Project, HostingP0Data.PullRequest, ct);
        await box.WhenIdleAsync().WaitAsync(Guard, ct);
        Assert.NotNull(closing);
        await closing.WaitAsync(Guard, ct);

        Assert.Equal(0, rig.Trace.Count("gh:close"));
        Assert.Equal(RemoteWorkState.PendingSync, box.StatusOf(ticket.Id)!.State);
    }
}
