using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Domain.Hosting;

namespace AgentStudio.Tests.Orchestration.Publication;

/// <summary>Changes made while a platform read is in flight must stop the next write, without asking for another approval.</summary>
public sealed class PublicationRevalidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Breaking_the_log_during_the_PR_observation_stops_closing_it()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        rig.Provider.Observe = (_, _) =>
        {
            rig.Log.Verifies = false;
            return new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(HostingP0Data.Open()));
        };

        var ticket = await rig.Service.CloseAsync(PublicationRig.Project, HostingP0Data.PullRequest, Ct);
        await rig.IdleAsync();

        Assert.Equal(1, rig.Trace.Count("gh:observe"));
        Assert.Equal(0, rig.Trace.Count("gh:close"));
        Assert.Equal(RemoteWorkState.Failed, (await rig.BoxAsync()).StatusOf(ticket.Id)!.State);
        Assert.Single(rig.Approvals.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_gate_rechecks_authority_after_the_capability_probe(bool revoke)
    {
        await using var rig = new PublicationRig();
        var grant = new HostingPolicyGrant("probe-grant", HostingOperationClass.PullRequestWrite, HostingP0Data.Repository, HostingP0Data.Main, "person", HostingP0Data.At);
        rig.Log.Add(DecisionKind.HostingPolicyGranted, new Actor(ActorKind.User, "person"), "grant", HostingPolicyGrantedPayload.Granting(grant));
        rig.Provider.OnProbe = () =>
        {
            if (revoke)
            {
                rig.Log.Add(DecisionKind.HostingPolicyRevoked, new Actor(ActorKind.User, "person"), "withdraw", HostingPolicyRevokedPayload.Revoking(grant.GrantId, "person"));
            }
            else
            {
                rig.Delivery.Ready = false;
            }
        };

        var operation = PublicationRig.Operation(HostingOperationKind.OpenPullRequest, baseBranch: HostingP0Data.Main);
        var answer = await rig.Gate.AuthorizeUseAsync(operation, new ApprovalLease(), mayAsk: false, holdsProject: false, Ct);

        Assert.False(answer.Allowed);
        Assert.Equal(revoke ? PublicationCodes.NeedsApproval : PublicationCodes.VerdictNotApproved, answer.Refusal!.Code);
        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public async Task A_revocation_recorded_while_readiness_is_checked_does_not_survive_in_a_gate_snapshot()
    {
        await using var rig = new PublicationRig();
        var grant = new HostingPolicyGrant("readiness-grant", HostingOperationClass.PullRequestWrite, HostingP0Data.Repository, HostingP0Data.Main, "person", HostingP0Data.At);
        rig.Log.Add(DecisionKind.HostingPolicyGranted, new Actor(ActorKind.User, "person"), "grant", HostingPolicyGrantedPayload.Granting(grant));
        rig.Delivery.OnCheck = () =>
        {
            if (rig.Trace.Count("gh:probe") == 1)
            {
                rig.Log.Add(DecisionKind.HostingPolicyRevoked, new Actor(ActorKind.User, "person"), "withdraw", HostingPolicyRevokedPayload.Revoking(grant.GrantId, "person"));
            }
        };

        var operation = PublicationRig.Operation(HostingOperationKind.OpenPullRequest, baseBranch: HostingP0Data.Main);
        var answer = await rig.Gate.AuthorizeUseAsync(operation, new ApprovalLease(), mayAsk: false, holdsProject: false, Ct);

        Assert.False(answer.Allowed);
        Assert.Equal(PublicationCodes.NeedsApproval, answer.Refusal!.Code);
        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public async Task A_gate_rechecks_the_verdict_after_inspecting_git()
    {
        await using var rig = new PublicationRig();
        rig.Git.InspectHold = () =>
        {
            rig.Delivery.Ready = false;
            return Task.CompletedTask;
        };

        using var taken = rig.Activity.TryEnter(PublicationRig.Project.ProjectId, AgentStudio.Application.Orchestration.ProjectActivityKind.RemoteOperation);
        var answer = await rig.Gate.AuthorizeUseAsync(PublicationRig.Operation(HostingOperationKind.PushRunBranch), new ApprovalLease(), mayAsk: false, holdsProject: true, Ct);

        Assert.False(answer.Allowed);
        Assert.Equal(PublicationCodes.VerdictNotApproved, answer.Refusal!.Code);
        Assert.Empty(rig.Approvals.Requests);
    }

    [Fact]
    public async Task Every_disposal_waits_for_the_same_mailbox_drain()
    {
        await using var rig = new PublicationRig();
        var box = await rig.BoxAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = box.ExecAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, Ct);
        Task? first = null;
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            first = box.DisposeAsync().AsTask();
            second = box.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await held.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await Task.WhenAll(first!, second!).WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Losing_the_verdict_or_grant_during_the_PR_lookup_stops_opening_it(bool revoke)
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        var box = await rig.BoxAsync();
        var grant = new HostingPolicyGrant("pr-grant", HostingOperationClass.PullRequestWrite, HostingP0Data.Repository, HostingP0Data.Main, "person", HostingP0Data.At);
        if (revoke)
        {
            rig.Log.Add(DecisionKind.HostingPolicyGranted, new Actor(ActorKind.User, "person"), "grant", HostingPolicyGrantedPayload.Granting(grant));
        }

        Task? withdrawal = null;
        bool? pendingDuringLookup = null;
        rig.Provider.OnFind = () =>
        {
            if (revoke)
            {
                withdrawal = box.RevokeAsync(grant.GrantId);

                // The consumer is still in the lookup and cannot have processed its queued revocation. Noted here and asserted after the flow: an assertion that threw inside the
                // lookup would fail the flow by itself, and the test would pass without the withdrawal counting at once.
                pendingDuringLookup = box.PendingRevocations.Contains(grant.GrantId);
            }
            else
            {
                rig.Delivery.Ready = false;
            }
        };

        var ticket = await rig.Service.PublishAsync(new PublicationRequest(PublicationRig.Project, HostingP0Data.Run, HostingP0Data.Main, new PullRequestText("title", "body"), false), Ct);
        var approvals = rig.Approvals.Requests.Count;
        await rig.AdvanceAsync(TimeSpan.FromSeconds(4));
        if (withdrawal is not null)
        {
            await withdrawal.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        if (revoke)
        {
            Assert.True(pendingDuringLookup, "The withdrawal did not count from the instant it was asked for (P93).");
        }

        Assert.Equal(1, rig.Trace.Count("gh:find"));
        Assert.Equal(0, rig.Trace.Count("gh:open"));
        Assert.Equal(RemoteWorkState.Failed, box.StatusOf(ticket.Id)!.State);
        Assert.Equal(approvals, rig.Approvals.Requests.Count);
    }

    [Fact]
    public async Task Losing_the_verdict_during_the_last_PR_observation_stops_merging_it()
    {
        await using var rig = new PublicationRig();
        await rig.JudgeAsync();
        rig.Observed(HostingP0Data.Open());
        rig.Provider.Observe = (_, _) =>
        {
            // The click observes twice; the flow makes the last observation before writing.
            if (rig.Trace.Count("gh:observe") >= 3)
            {
                rig.Delivery.Ready = false;
            }

            return new HostingResult<PullRequestObservation>(HostingOutcome.Ok, PublicationRig.Observation(HostingP0Data.Open()));
        };

        var ticket = await rig.Service.MergeAsync(PublicationRig.Project, HostingP0Data.PullRequest, MergeMethod.Squash, Ct);
        await rig.IdleAsync();

        Assert.True(rig.Trace.Count("gh:observe") >= 3);
        Assert.Equal(0, rig.Trace.Count("gh:merge"));
        Assert.Equal(RemoteWorkState.Failed, (await rig.BoxAsync()).StatusOf(ticket.Id)!.State);
        Assert.Single(rig.Approvals.Requests);
    }

    [Fact]
    public async Task A_restored_snapshot_and_confirmation_are_discarded_before_the_first_read()
    {
        var cursor = new ObservationCursor(new Dictionary<ObservedResource, string> { [ObservedResource.PullRequest] = "etag" }, HostingP0Data.Merged());
        var followed = new FollowedPullRequest(HostingP0Data.PullRequest, HostingP0Data.Run.Value, cursor, RemoteDisplayTexts.None, HostingP0Data.Open());
        await using var rig = new PublicationRig(RemoteSyncState.Empty with { Repository = HostingP0Data.Repository, Followed = [followed] });
        rig.Provider.Observe = (_, received) =>
        {
            Assert.NotNull(received);
            Assert.Empty(received.ETags);
            Assert.Null(received.LastSnapshot);
            return new HostingResult<PullRequestObservation>(HostingOutcome.Unreachable);
        };

        await rig.Sync.SyncNowAsync(PublicationRig.Project, Ct);
        await rig.IdleAsync();

        Assert.Equal(1, rig.Trace.Count("gh:observe"));
        Assert.Null(Assert.Single(rig.Store.State.Followed).PendingConfirmation);
        Assert.Empty(rig.Log.Observations());
    }

    [Fact]
    public async Task Checking_a_session_updates_its_health_without_claiming_a_sync_or_writing_evidence()
    {
        await using var rig = new PublicationRig();
        await rig.Sync.NoteConnectionAsync(PublicationRig.Project, HostingOutcome.Ok, Ct);
        await rig.IdleAsync();

        var health = rig.Sync.Health(PublicationRig.Project.ProjectId);
        Assert.Equal(HostingOutcome.Ok, health.LastOutcome);
        Assert.Equal(rig.Time.GetUtcNow(), health.LastOutcomeAt);
        Assert.Null(health.LastGoodSync);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task A_withdrawal_queued_behind_work_survives_a_close_before_its_log_entry()
    {
        await using var rig = new PublicationRig();
        var box = await rig.BoxAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = box.ExecAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, Ct);
        Task? withdrawal = null;
        Task? closing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            withdrawal = box.RevokeAsync("g1");
            Assert.Contains("g1", box.PendingRevocations);
            closing = box.DisposeAsync().AsTask();
        }
        finally
        {
            release.TrySetResult();
        }

        await held.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await withdrawal!.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await closing!.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Contains("g1", rig.Store.State.PendingRevocations ?? []);
        Assert.Equal(0, rig.Log.Count(DecisionKind.HostingPolicyRevoked));
    }

    [Fact]
    public async Task A_closed_service_does_not_open_another_mailbox()
    {
        var rig = new PublicationRig();
        await rig.Sync.AttachAsync(PublicationRig.Project, Ct);
        await rig.Sync.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Sync.AttachAsync(PublicationRig.Project, Ct));
        await rig.Sync.DisposeAsync();
    }
}
