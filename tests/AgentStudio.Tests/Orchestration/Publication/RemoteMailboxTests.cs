using AgentStudio.Application.Decisions;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>The mailbox of a project and the poll (docs/21 D-23 to D-26, F10-6 and F10-13): a clock the test moves, doubles that fail when the project is not as the rule says.</summary>
public sealed class RemoteMailboxTests
{
    private static RemoteSnapshot Snap(int n) =>
        HostingP0Data.Open(new string((char)('a' + n), 40)) with { UpdatedAt = HostingP0Data.At.AddMinutes(n) };

    private static RemoteSnapshot Checks(RemoteSnapshot snapshot, bool failing) =>
        snapshot with { Checks = failing ? new ChecksSummary(ChecksRollup.Failing, 2, 1, 0, false) : new ChecksSummary(ChecksRollup.Passing, 3, 0, 0, false) };

    private static Func<PullRequestRef, ObservationCursor?, HostingResult<PullRequestObservation>> Answer(Func<RemoteSnapshot> snapshot) =>
        (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(snapshot()));

    private static async Task Follow(PublicationRig rig)
    {
        await rig.Sync.FollowAsync(PublicationRig.Project, HostingP0Data.PullRequest, HostingP0Data.Run, CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EveryAnswerNotModifiedWritesNothingAndDoesNotTakeTheProject()
    {
        await using var rig = new PublicationRig();
        await Follow(rig);
        var takes = rig.Activity.Takes;

        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        Assert.Equal(2, rig.Trace.Count("gh:observe"));
        Assert.Equal(takes, rig.Activity.Takes);
        Assert.Empty(rig.Log.Observations());
    }

    [Fact]
    public async Task ANewFingerprintTakesTheProjectOnceAndARepeatedOneNeverDoes()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Observe = Answer(() => Snap(1));
        await Follow(rig);

        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        var takes = rig.Activity.Takes;
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        var recorded = Assert.Single(rig.Log.Observations());
        Assert.Equal(Snap(1).Fingerprint, recorded.Fingerprint);
        Assert.Equal([RemoteChangeKind.Opened], recorded.Changes);
        Assert.Equal(takes, rig.Activity.Takes);
        Assert.Equal(DecisionPayload.NoRun, recorded.RunId);
        Assert.Equal(HostingP0Data.Run.Value, recorded.Run);
    }

    [Fact]
    public async Task ARepeatedObservationAfterTheCacheWasLostIsNotWrittenTwice()
    {
        await using var first = new PublicationRig();
        first.Provider.Observe = Answer(() => Snap(1));
        await Follow(first);
        await first.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        // A new session (the files deleted) over the same log: the last fingerprint is read from the log.
        await using var second = new PublicationRig();
        foreach (var entry in first.Log.Entries.Where(e => e.Kind == DecisionKind.RemoteStateObserved))
        {
            second.Log.Add(entry.Kind, entry.Actor, entry.Summary, DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson)!);
        }

        second.Provider.Observe = Answer(() => Snap(1));
        await Follow(second);
        await second.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        Assert.Single(second.Log.Observations());
    }

    [Fact]
    public async Task ChecksThatOscillateLeaveNoEntryAndAChangeThatRepeatsIsWrittenOnce()
    {
        await using var rig = new PublicationRig();
        var current = Checks(Snap(1), failing: false);
        rig.Provider.Observe = Answer(() => current);
        await Follow(rig);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        Assert.Single(rig.Log.Observations());

        foreach (var failing in new[] { true, false, true, false })
        {
            current = Checks(Snap(1), failing);
            await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        }

        Assert.Single(rig.Log.Observations());

        current = Checks(Snap(1), failing: true);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        Assert.Single(rig.Log.Observations());
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        Assert.Equal(2, rig.Log.Observations().Count);
        Assert.Equal([RemoteChangeKind.ChecksChanged], rig.Log.Observations()[1].Changes);
    }

    [Fact]
    public async Task OutOfOrderObservationsNeverGoBack()
    {
        await using var rig = new PublicationRig();
        var current = Snap(2);
        rig.Provider.Observe = Answer(() => current);
        await Follow(rig);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        current = Snap(1);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        var recorded = Assert.Single(rig.Log.Observations());
        Assert.Equal(Snap(2).Fingerprint, recorded.Fingerprint);
    }

    [Fact]
    public async Task WithTheProjectBusyNothingTouchesGitOrTheLogAndTheProviderStillReads()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Observe = Answer(() => Snap(1));
        await Follow(rig);
        var gitBefore = rig.Trace.Count("git:");
        var recordsBefore = rig.Log.Entries.Count;

        foreach (var kind in new[] { ProjectActivityKind.Run, ProjectActivityKind.Debate, ProjectActivityKind.Audit })
        {
            using (rig.Activity.TryEnter("p1", kind))
            {
                await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
                Assert.Equal(gitBefore, rig.Trace.Count("git:"));
                Assert.Equal(recordsBefore, rig.Log.Entries.Count);
            }
        }

        Assert.True(rig.Trace.Count("gh:observe") >= 3);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Single(rig.Log.Observations());
    }

    [Fact]
    public async Task WhatIsParkedIsMeltedByPullRequestAndWrittenOnceWhenTheProjectIsFree()
    {
        await using var rig = new PublicationRig();
        var current = Snap(1);
        rig.Provider.Observe = Answer(() => current);
        await Follow(rig);
        var lease = rig.Activity.TryEnter("p1", ProjectActivityKind.Run);

        for (var n = 1; n <= 5; n++)
        {
            current = Snap(n);
            await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        }

        var box = await rig.BoxAsync();
        Assert.Equal(1, box.ParkedCount);
        Assert.Empty(rig.Log.Observations());
        Assert.Contains(rig.Store.State.Pending, p => p.Observation is not null);

        lease!.Dispose();
        await rig.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));

        var recorded = Assert.Single(rig.Log.Observations());
        Assert.Equal(Snap(5).Fingerprint, recorded.Fingerprint);
        Assert.Equal(0, box.ParkedCount);
    }

    [Fact]
    public async Task WhatWasParkedSurvivesARestartAndIsAskedAgainFromThePlatform()
    {
        await using var first = new PublicationRig();
        first.Provider.Observe = Answer(() => Snap(1));
        await Follow(first);
        using (first.Activity.TryEnter("p1", ProjectActivityKind.Run))
        {
            await first.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
            Assert.Equal(1, (await first.BoxAsync()).ParkedCount);
        }

        var saved = first.Store.State;
        Assert.Single(saved.Pending);

        await using var second = new PublicationRig(saved);
        second.Provider.Observe = Answer(() => Snap(1));
        await second.IdleAsync();
        await second.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, second.Trace.Count("gh:observe"));
        Assert.Single(second.Log.Observations());
    }

    [Fact]
    public async Task ARateLimitStopsEverythingUntilItsHourAndIsKeptAndRestored()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Observe = Answer(() => Snap(1));
        await Follow(rig);
        var until = rig.Time.GetUtcNow().AddMinutes(30);
        rig.Provider.RateLimitedUntil = until;

        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        Assert.Equal(0, rig.Trace.Count("gh:observe"));
        Assert.Equal(until, rig.Sync.Health("p1").RateLimitedUntil);
        Assert.Equal(until, rig.Store.State.RateLimitedUntil);

        var state = rig.Store.State with { Repository = HostingP0Data.Repository };
        await using var restarted = new PublicationRig(state);
        await restarted.BoxAsync();
        Assert.Equal(until, restarted.Provider.Restored);
    }

    [Fact]
    public async Task AnAbsurdWaitFromTheFileIsCutToTwoHours()
    {
        var state = RemoteSyncState.Empty with { Repository = HostingP0Data.Repository, RateLimitedUntil = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero) };
        await using var rig = new PublicationRig(state);

        await rig.BoxAsync();

        Assert.Equal(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), rig.Provider.Restored);
    }

    [Fact]
    public async Task WithoutAConnectionTheWaitGrowsOneTwoFiveAndFifteenMinutes()
    {
        await using var rig = new PublicationRig();
        rig.Policy.Interval = TimeSpan.FromSeconds(30);
        rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.Unreachable);
        await Follow(rig);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        Assert.Equal(1, rig.Trace.Count("gh:observe"));

        await rig.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, rig.Trace.Count("gh:observe"));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, rig.Trace.Count("gh:observe"));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(90));
        Assert.Equal(2, rig.Trace.Count("gh:observe"));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, rig.Trace.Count("gh:observe"));
        Assert.Empty(rig.Log.Observations());
        Assert.Equal(HostingOutcome.Unreachable, rig.Sync.Health("p1").LastOutcome);
        Assert.NotNull(rig.Sync.Health("p1").LastOutcomeAt);
    }

    [Fact]
    public async Task ACaughtSessionStopsThePollUntilThePersonRetries()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Observe = (_, _) => new HostingResult<PullRequestObservation>(HostingOutcome.SignInNeeded);
        await Follow(rig);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        await rig.AdvanceAsync(TimeSpan.FromMinutes(5));
        await rig.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, rig.Trace.Count("gh:observe"));

        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);
        Assert.Equal(2, rig.Trace.Count("gh:observe"));
        Assert.Empty(rig.Log.Observations());
    }

    [Fact]
    public async Task WithoutThePermissionThereIsNoNetworkInTheBackgroundEvenAfterADay()
    {
        await using var rig = new PublicationRig();
        rig.Policy.Allowed = false;
        await Follow(rig);

        for (var i = 0; i < 24; i++)
        {
            await rig.AdvanceAsync(TimeSpan.FromHours(1));
        }

        Assert.Equal(0, rig.Trace.Count("gh:"));
    }

    [Fact]
    public async Task WithoutAPolicyInTheCompositionTheNetworkIsNeverUsed()
    {
        await using var rig = new PublicationRig(policy: false);
        await Follow(rig);

        await rig.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(0, rig.Trace.Count("gh:"));
    }

    [Fact]
    public async Task WithThePermissionThePollOnlyReads()
    {
        await using var rig = new PublicationRig();
        rig.Provider.Observe = Answer(() => Snap(1));
        await Follow(rig);

        await rig.AdvanceAsync(TimeSpan.FromMinutes(5));
        await rig.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.True(rig.Trace.Count("gh:observe") >= 2);
        Assert.Equal(rig.Trace.Count("gh:"), rig.Trace.Count("gh:observe"));
    }

    [Fact]
    public async Task ALocalFileThatNamesAPullRequestOfAnotherRepositoryAuthorisesNothingAndBreaksNothing()
    {
        var foreign = new PullRequestRef(RemoteRepository.TryCreateGitHub("evil", "repo")!, 5);
        var legit = HostingP0Data.PullRequest;
        var cursor = new ObservationCursor(new Dictionary<ObservedResource, string> { [ObservedResource.PullRequest] = "\"x\"" }, null);
        var state = new RemoteSyncState(
            foreign.Repository,
            [new FollowedPullRequest(foreign, null, cursor, RemoteDisplayTexts.None, null), new FollowedPullRequest(legit, null, cursor, RemoteDisplayTexts.None, null)],
            [],
            null,
            null,
            null);
        await using var rig = new PublicationRig(state);
        var asked = new List<string>();
        rig.Provider.Observe = (pr, c) =>
        {
            asked.Add(pr.TraceId);
            Assert.Empty(c!.ETags);
            return new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(Snap(1)));
        };

        await rig.AdvanceAsync(TimeSpan.FromMinutes(5));
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([legit.TraceId], asked);
        Assert.Equal(legit, Assert.Single(rig.Log.Observations()).ToPullRequest());
        Assert.Empty(rig.Approvals.Requests);
        Assert.Equal(0, rig.Log.Count(DecisionKind.HostingPolicyGranted));
    }

    [Fact]
    public async Task ClosingTheAppEndsTheMailboxWithoutLeavingTasksBehind()
    {
        var rig = new PublicationRig();
        rig.Provider.Observe = Answer(() => Snap(1));
        await Follow(rig);
        await rig.Sync.SyncNowAsync(PublicationRig.Project, CancellationToken.None);

        await rig.DisposeAsync();

        Assert.Null(rig.Activity.Current("p1"));
        Assert.Single(rig.Log.Observations());
    }
}
