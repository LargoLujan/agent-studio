using AgentStudio.Application.Hosting;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Hosting;
using AgentStudio.Tests.Domain.Hosting;
using AgentStudio.Tests.Orchestration.Publication;

namespace AgentStudio.Tests.Desktop.Services;

/// <summary>
/// The exit of the app and the mailbox of GitHub (docs/21 D-25): <c>App.OnExit</c> is an <c>async void</c> that WPF does not wait for, so what must end before the process goes
/// (a <c>push</c>, a merge that was sent) is waited for with a call that blocks. The test holds a merge on the platform and checks that the blocking shutdown does not return
/// while it is out, and that it returns with the merge written.
/// </summary>
public sealed class RemoteSyncAttachmentTests
{
    private static RemoteSnapshot Merged() => HostingP0Data.Merged() with { HeadCommit = HostingP0Data.Head, UpdatedAt = HostingP0Data.At.AddMinutes(1) };

    [Fact]
    public async Task The_blocking_shutdown_of_the_exit_returns_only_when_a_merge_that_was_sent_has_ended_and_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.MergeHold = _ => release.Task;
        rig.Provider.Observe = (_, _) =>
            new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(rig.Trace.Count("gh:merge") > 0 ? Merged() : HostingP0Data.Open()));
        var merging = rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        await rig.Provider.MergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var box = await rig.BoxAsync();
        var attachment = new RemoteSyncAttachment(null, rig.Sync, null);

        // What App.OnExit does: it blocks its thread (here, one of the pool) until the mailboxes stopped.
        var exit = Task.Run(() => attachment.ShutdownBlocking(TimeSpan.FromSeconds(60)), ct);
        Assert.True(SpinWait.SpinUntil(() => box.Closing, TimeSpan.FromSeconds(30)), "the close never started");

        // The merge is still out, so the exit has not returned. An exit that only awaited would have let the process end here.
        Assert.NotSame(exit, await Task.WhenAny(exit, Task.Delay(TimeSpan.FromMilliseconds(250), ct)));

        release.SetResult();
        await exit.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await merging.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Contains(HostingOperationKind.MergePullRequest, RemoteLogReading.ReadEntries(rig.Log.Entries).Operations.Select(o => o.Payload.Operation));
        Assert.Null(rig.Activity.Current("p1"));
    }

    [Fact]
    public async Task The_safety_net_of_the_exit_does_not_wait_again_after_the_managed_exit_waited()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.MergeHold = _ => release.Task;
        rig.Provider.Observe = (_, _) =>
            new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(rig.Trace.Count("gh:merge") > 0 ? Merged() : HostingP0Data.Open()));
        var merging = rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, CancellationToken.None);
        await rig.Provider.MergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var attachment = new RemoteSyncAttachment(null, rig.Sync, null);
        Assert.True(attachment.IsBusy);

        // The managed exit waits (here, a wait that runs out while the merge is still out, which is logged and left to the end of the process)…
        await attachment.ShutdownAsync(TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(30), ct);

        // …and OnExit, the safety net, does not wait again for the sixty seconds it is given: it returns while the merge is still held.
        var safetyNet = Task.Run(() => attachment.ShutdownBlocking(TimeSpan.FromSeconds(60)), ct);
        await safetyNet.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.False(release.Task.IsCompleted);

        release.SetResult();
        await merging.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await rig.DisposeAsync();
    }

    [Fact]
    public async Task The_managed_exit_and_the_safety_net_share_one_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new PublicationRig();
        var attachment = new RemoteSyncAttachment(null, rig.Sync, null);

        var first = attachment.ShutdownAsync(TimeSpan.FromSeconds(60));
        var second = attachment.ShutdownAsync(TimeSpan.FromSeconds(1));
        await first.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Same(first, second);
        Assert.False(attachment.IsBusy);
    }

    [Fact]
    public void Without_a_mailbox_the_blocking_shutdown_returns_at_once()
    {
        var attachment = new RemoteSyncAttachment(null, null, null);

        attachment.ShutdownBlocking(TimeSpan.FromSeconds(60));

        Assert.False(attachment.IsAvailable);
    }
}
