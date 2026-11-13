using AgentStudio.Domain.Hosting;

namespace AgentStudio.Tests.Domain.Hosting;

/// <summary>The reconciler of docs/21 D-23, pure: repeated and out-of-order observations (F10-6), with no network and no disk.</summary>
public sealed class RemoteReconcilerTests
{
    private static RemoteSnapshot Open() => HostingP0Data.Open();

    private static RemoteSnapshot HeadMoved() => HostingP0Data.Open("5555555555555555555555555555555555555555") with { UpdatedAt = HostingP0Data.At.AddMinutes(1) };

    private static RemoteSnapshot Merged() => HostingP0Data.Merged() with { HeadCommit = HeadMoved().HeadCommit, UpdatedAt = HostingP0Data.At.AddMinutes(5) };

    [Fact]
    public void ThePullRequestObservedForTheFirstTimeIsOpened()
    {
        var result = RemoteReconciler.Reconcile(null, Open());

        Assert.False(result.Discarded);
        Assert.Equal([RemoteChangeKind.Opened], result.Changes);
    }

    [Fact]
    public void TheSameSnapshotTwiceIsOneChange()
    {
        var first = RemoteReconciler.Reconcile(Open(), HeadMoved());
        var second = RemoteReconciler.Reconcile(HeadMoved(), HeadMoved());

        Assert.Equal([RemoteChangeKind.HeadMoved], first.Changes);
        Assert.False(second.Discarded);
        Assert.Empty(second.Changes);
    }

    [Fact]
    public void AnOlderAnswerAfterTheMergeDoesNotGoBack()
    {
        var recorded = Merged();

        var stale = RemoteReconciler.Reconcile(recorded, HeadMoved());
        var staleSameTime = RemoteReconciler.Reconcile(recorded, HeadMoved() with { UpdatedAt = recorded.UpdatedAt });

        Assert.True(stale.Discarded);
        Assert.Empty(stale.Changes);
        Assert.True(staleSameTime.Discarded);
    }

    [Fact]
    public void AShorterListOfCommitsForTheSameHeadIsAnOldAnswer()
    {
        var recorded = Open();
        var lagging = recorded with { OriginalCommits = [HostingP0Data.Original1], UpdatedAt = recorded.UpdatedAt.AddMinutes(1) };

        Assert.True(RemoteReconciler.Reconcile(recorded, lagging).Discarded);

        // Another head is a different history: its list may be shorter.
        var forced = HeadMoved() with { OriginalCommits = [HostingP0Data.Original1] };
        Assert.False(RemoteReconciler.Reconcile(recorded, forced).Discarded);
    }

    [Fact]
    public void WhatOnlyGrowsNeverShrinks()
    {
        var recorded = Open() with { ForcePushes = 2, RevertCommits = [HostingP0Data.Revert], Deployment = new RemoteDeployment(9, HostingP0Data.Result, DeploymentState.Succeeded) };
        var later = recorded.UpdatedAt.AddMinutes(1);

        Assert.True(RemoteReconciler.Reconcile(recorded, recorded with { ForcePushes = 1, UpdatedAt = later }).Discarded);
        Assert.True(RemoteReconciler.Reconcile(recorded, recorded with { RevertCommits = [], UpdatedAt = later }).Discarded);
        Assert.True(RemoteReconciler.Reconcile(recorded, recorded with { Deployment = new RemoteDeployment(8, HostingP0Data.Result, DeploymentState.Failed), UpdatedAt = later }).Discarded);
        Assert.Contains(RemoteChangeKind.ForcePushed, RemoteReconciler.Reconcile(recorded, recorded with { ForcePushes = 3, UpdatedAt = later }).Changes);
    }

    [Fact]
    public void AChangeOfChecksAndAChangeOfReviewsAreNamed()
    {
        var recorded = Open();
        var failing = recorded with { Checks = new ChecksSummary(ChecksRollup.Failing, 2, 1, 0, false) };
        var approved = recorded with { Reviews = new ReviewsSummary(1, 0, 0, [RemoteLogin.TryParse("revisora")!]) };

        Assert.Equal([RemoteChangeKind.ChecksChanged], RemoteReconciler.Reconcile(recorded, failing).Changes);
        Assert.Equal([RemoteChangeKind.ReviewsChanged], RemoteReconciler.Reconcile(recorded, approved).Changes);
    }

    [Fact]
    public void ARevertAndADeploymentAreRecognised()
    {
        var recorded = Merged();
        var reverted = recorded with { RevertCommits = [HostingP0Data.Revert] };
        var deployed = recorded with { Deployment = new RemoteDeployment(1, HostingP0Data.Result, DeploymentState.Succeeded) };

        Assert.Equal([RemoteChangeKind.RevertSuspected], RemoteReconciler.Reconcile(recorded, reverted).Changes);
        Assert.Equal([RemoteChangeKind.DeploymentChanged], RemoteReconciler.Reconcile(recorded, deployed).Changes);
    }

    [Fact]
    public void ClosedAndReopenedAreRecognised()
    {
        var closed = Open() with { State = PullRequestState.Closed, UpdatedAt = HostingP0Data.At.AddMinutes(1) };
        var reopened = Open() with { UpdatedAt = HostingP0Data.At.AddMinutes(2) };

        Assert.Equal([RemoteChangeKind.Closed], RemoteReconciler.Reconcile(Open(), closed).Changes);
        Assert.Equal([RemoteChangeKind.Reopened], RemoteReconciler.Reconcile(closed, reopened).Changes);
    }

    [Fact]
    public void ForAnySeriesOfReadingsInAnyOrderWithRepetitionsTheFinalStateIsTheLatestAndItNeverGoesBack()
    {
        RemoteSnapshot[] series = [Open(), HeadMoved(), Merged()];
        var sequences = 0;
        foreach (var order in Sequences(series.Length, 5))
        {
            sequences++;
            RemoteSnapshot? recorded = null;
            foreach (var index in order)
            {
                var result = RemoteReconciler.Reconcile(recorded, series[index]);
                if (!result.Discarded && result.Changes.Count > 0)
                {
                    if (recorded is not null)
                    {
                        // monotone: merged is terminal and the moment of the recorded never goes back
                        Assert.True(series[index].UpdatedAt >= recorded.UpdatedAt);
                        Assert.False(recorded.State == PullRequestState.Merged && series[index].State != PullRequestState.Merged);
                    }

                    recorded = series[index];
                }
            }

            var latest = order.Max();
            Assert.Equal(series[latest].Fingerprint, recorded!.Fingerprint);
        }

        Assert.Equal(243, sequences);
    }

    private static IEnumerable<int[]> Sequences(int values, int length)
    {
        var total = (int)Math.Pow(values, length);
        for (var n = 0; n < total; n++)
        {
            var sequence = new int[length];
            var rest = n;
            for (var i = 0; i < length; i++)
            {
                sequence[i] = rest % values;
                rest /= values;
            }

            yield return sequence;
        }
    }
}
