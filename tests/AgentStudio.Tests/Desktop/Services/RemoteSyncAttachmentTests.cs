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
    public void Without_a_mailbox_the_blocking_shutdown_returns_at_once()
    {
        var attachment = new RemoteSyncAttachment(null, null, null);

        attachment.ShutdownBlocking(TimeSpan.FromSeconds(60));

        Assert.False(attachment.IsAvailable);
    }
}
