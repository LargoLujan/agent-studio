using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Orchestration.WorkTracking.RemoteTestSupport;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/21 D-28 to D-30 and F10-7/F10-8: the participation, the annotation of the commits, the timeline and the view of the pull requests read the remote entries of the
/// verified log as data, and never fail for one that does not validate.
/// </summary>
public sealed class RemoteViewsTests
{
    private const string SystemCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Squashed = "ffffffffffffffffffffffffffffffffffffffff";
    private const string Unrelated = "1212121212121212121212121212121212121212";

    private const string Run = WorkWorld.Run1;

    private static CancellationToken Ct => WorkWorld.Ct;

    private static ParticipationProjector Participation(WorkWorld world) => new(world.Instance.Recorder, world.Instance.Verdicts, world.Instance.Plans);

    private static PullRequestProjector Views(WorkWorld world, IRemoteSyncStore? cache = null, IRemoteSync? sync = null) =>
        new(world.Instance.Recorder, world.Instance.Verdicts, cache, sync);

    private static async Task<WorkWorld> JudgedAsync(VerdictDecision decision = VerdictDecision.Approved)
    {
        var world = await WorkWorld.OpenAsync();
        await world.StartRunAsync();
        await world.RunAndCompleteAsync(Run, "T1");
        await world.FinishAsync(Run, AgentStudio.Application.Orchestration.RunPhase.Succeeded);
        await world.JudgeAsync(decision);
        return world;
    }

    private static ParticipationEntry Of(ParticipationView view, ParticipationRole role, string? source) => view.Entries.First(e => e.Role == role && e.Source == source);

    // ---- participation (D-30) ----

    [Fact]
    public async Task The_account_that_published_the_reviewers_and_who_merged_come_from_the_remote_entries_and_each_has_its_source()
    {
        using var world = await JudgedAsync();
        await world.OperationAsync(HostingOperationKind.PushRunBranch, null, account: "manuel-gh");
        await world.OperationAsync(HostingOperationKind.OpenPullRequest, 12, account: "manuel-gh");
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash));

        var view = await Participation(world).ProjectAsync(world.Project, TraceRef.Run(Run), Ct);

        var account = Of(view, ParticipationRole.PublishingAccount, ParticipationSources.RemotePublication);
        Assert.Equal("manuel-gh", account.Who);
        Assert.Contains("github.com/LargoLujan/agent-studio", account.Detail, StringComparison.Ordinal);
        Assert.Equal("revisora", Of(view, ParticipationRole.Reviewer, ParticipationSources.RemoteReview).Who);
        var merger = Of(view, ParticipationRole.Integrator, ParticipationSources.RemoteMerge);
        Assert.Equal("manuel", merger.Who);
        Assert.Contains(Result, merger.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_with_no_remote_source_stays_no_consta_and_a_refused_operation_publishes_nothing()
    {
        using var world = await JudgedAsync();
        await world.OperationAsync(HostingOperationKind.PushRunBranch, null, outcome: HostingOutcome.Refused, account: "manuel-gh");
        await world.OperationAsync(HostingOperationKind.OpenPullRequest, 12, account: null);

        var view = await Participation(world).ProjectAsync(world.Project, TraceRef.Run(Run), Ct);

        Assert.Equal(ParticipationEntry.NotRecorded(ParticipationRole.PublishingAccount), view.Entries.Single(e => e.Role == ParticipationRole.PublishingAccount));
        Assert.Equal(ParticipationEntry.NotRecorded(ParticipationRole.Integrator), view.Entries.Single(e => e.Role == ParticipationRole.Integrator));
    }

    [Fact]
    public async Task A_pull_request_with_no_run_and_one_that_does_not_validate_attribute_nothing_and_the_local_delivery_still_counts()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash), run: null);
        await world.ObserveWithAWrongFingerprintAsync(Merged(RemoteMergeMode.Squash) with { MergedBy = RemoteLogin.TryParse("intruso") });
        await world.DeliverAsync(Run);

        var view = await Participation(world).ProjectAsync(world.Project, TraceRef.Run(Run), Ct);

        Assert.DoesNotContain(view.Entries, e => e.Source == ParticipationSources.RemoteMerge || e.Source == ParticipationSources.RemoteReview);
        Assert.Contains(view.Entries, e => e.Source == ParticipationSources.LocalDelivery);
    }

    [Fact]
    public async Task The_participation_of_a_task_counts_the_pull_request_of_its_run_and_repeated_observations_add_nobody()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash));
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash));
        await world.ObserveAsync(Open());

        var view = await Participation(world).ProjectAsync(world.Project, TraceRef.Task(Run, "T1"), Ct);

        Assert.Single(view.Entries, e => e.Source == ParticipationSources.RemoteMerge);
        Assert.Single(view.Entries, e => e.Source == ParticipationSources.RemoteReview);
    }

    // ---- the annotation of the commits (D-28) ----

    private sealed class NoHistory : IRepositoryHistoryReader
    {
        public Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BranchSummary>>([]);

        public Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CommitNode>>([]);
    }

    private static RepositoryAnnotator Annotator(WorkWorld world) =>
        new(world.Instance.Recorder, new TraceSuggester(world.Instance.Recorder, new NoHistory()));

    private static CommitNode Node(string id) => new(id, [], T0, "un commit", "Alguien", [], null);

    [Fact]
    public async Task The_commit_a_squash_left_and_the_ones_it_came_from_are_annotated_with_the_pull_request_and_its_mode()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash) with { ResultCommits = [Squashed], MergeCommit = Squashed });

        var annotated = await Annotator(world).AnnotateAsync(world.Project, [Node(Original1), Node(Squashed), Node(Unrelated)], Ct);

        foreach (var commit in new[] { Original1, Squashed })
        {
            var annotation = annotated.Single(a => a.Commit.Id == commit).Annotation!;
            Assert.Equal(Run, annotation.Run);
            var successor = annotation.Successor!;
            Assert.Equal((PullRequest, RemoteMergeMode.Squash, true, true), (successor.PullRequest, successor.Mode, successor.ModeIsDeduced, successor.HeadWasJudgedCommit));
            Assert.Equal([Squashed], successor.ResultCommits);
        }

        Assert.Null(annotated.Single(a => a.Commit.Id == Unrelated).Annotation);
    }

    [Fact]
    public async Task A_revert_is_listed_in_the_successor_and_a_pull_request_not_merged_annotates_nothing()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open());
        Assert.All(await Annotator(world).AnnotateAsync(world.Project, [Node(Original1)], Ct), a => Assert.Null(a.Annotation));

        await world.ObserveAsync(Merged(RemoteMergeMode.Squash) with { RevertCommits = [Revert] });

        var successor = (await Annotator(world).AnnotateAsync(world.Project, [Node(Original1)], Ct)).Single().Annotation!.Successor!;
        Assert.Equal([Revert], successor.RevertedBy);
    }

    // ---- the timeline (D-28) ----

    [Fact]
    public async Task The_operations_and_the_observations_of_the_remote_are_in_the_timeline_with_their_real_result_and_the_filter_by_run_applies()
    {
        using var world = await JudgedAsync();
        await world.OperationAsync(HostingOperationKind.PushRunBranch, null, account: "manuel-gh");
        await world.OperationAsync(HostingOperationKind.OpenPullRequest, 12, outcome: HostingOutcome.RateLimited);
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash));
        await world.ObserveWithAWrongFingerprintAsync(Merged(RemoteMergeMode.Squash));
        var reader = new RepositoryActivityReader(world.Instance.Recorder);

        var all = await reader.ListAsync(world.Project, null, Ct);
        var other = await reader.ListAsync(world.Project, new RepositoryActivityFilter(Run: "20990101-000000"), Ct);

        var remote = all.Where(a => a.Kind is RepositoryActivityKind.RemoteOperation or RepositoryActivityKind.RemoteObservation).ToList();
        Assert.Equal(
            [
                (RepositoryActivityKind.RemoteOperation, true, (string?)null),
                (RepositoryActivityKind.RemoteOperation, false, "hosting.outcome.rate-limited"),
                (RepositoryActivityKind.RemoteObservation, true, null),
            ],
            remote.Select(a => (a.Kind, a.Succeeded, a.ErrorCode)));
        Assert.Equal(Result, remote[2].Commit);
        Assert.All(remote, a => Assert.Equal(Run, a.Run));
        Assert.DoesNotContain(other, a => a.Kind is RepositoryActivityKind.RemoteOperation or RepositoryActivityKind.RemoteObservation);
    }

    // ---- the view of the pull requests ----

    [Fact]
    public async Task A_pull_request_view_has_the_final_snapshot_the_commit_the_judge_saw_and_the_standing_of_the_verdict()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open());
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash));
        await world.ObserveAsync(Open());

        var views = await Views(world).ListAsync(world.Project, Ct);

        var view = Assert.Single(views);
        Assert.Equal((PullRequest, PullRequestState.Merged, Head, true, AgentStudio.Application.Hosting.VerdictStanding.Approved), (view.PullRequest, view.Snapshot!.State, view.JudgedCommit, view.HeadIsJudgedCommit, view.Verdict));
        Assert.Equal(new RunId(Run), view.Run);
        Assert.Equal(RemoteDisplayTexts.None, view.Display);
        Assert.Empty(view.Pending);
    }

    [Fact]
    public async Task The_mode_of_a_merge_is_a_fact_only_when_the_app_merged_with_that_method_and_a_merge_made_outside_is_deduced()
    {
        using var outside = await JudgedAsync();
        await outside.ObserveAsync(Merged(RemoteMergeMode.Squash));
        Assert.True(Assert.Single(await Views(outside).ListAsync(outside.Project, Ct)).ModeIsDeduced);

        using var byTheApp = await JudgedAsync();
        await byTheApp.OperationAsync(HostingOperationKind.MergePullRequest, 12, MergeMethod.Squash);
        await byTheApp.ObserveAsync(Merged(RemoteMergeMode.Squash));
        Assert.False(Assert.Single(await Views(byTheApp).ListAsync(byTheApp.Project, Ct)).ModeIsDeduced);

        // The app asked for a squash and the platform left a rebase: the mode is not the one it asked for, and «squash o rebase» is never a fact.
        using var other = await JudgedAsync();
        await other.OperationAsync(HostingOperationKind.MergePullRequest, 12, MergeMethod.Squash);
        await other.ObserveAsync(Merged(RemoteMergeMode.SquashOrRebase));
        Assert.True(Assert.Single(await Views(other).ListAsync(other.Project, Ct)).ModeIsDeduced);
    }

    [Fact]
    public async Task The_verdict_last_published_on_a_pull_request_is_the_last_operation_that_ended_well_and_none_when_there_is_none()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open());
        Assert.Null(Assert.Single(await Views(world).ListAsync(world.Project, Ct)).PublishedVerdict);

        await world.OperationAsync(HostingOperationKind.PublishVerdictStatus, 12, verdict: "InReview");
        await world.OperationAsync(HostingOperationKind.PublishVerdictComment, 12, verdict: "Approved");
        await world.OperationAsync(HostingOperationKind.PublishVerdictComment, 12, outcome: HostingOutcome.Unreachable, verdict: "Rejected");
        await world.OperationAsync(HostingOperationKind.PublishVerdictStatus, 13, verdict: "Rejected");

        var published = Assert.Single(await Views(world).ListAsync(world.Project, Ct)).PublishedVerdict!;
        Assert.Equal(AgentStudio.Application.Hosting.VerdictStanding.Approved, published.Standing);
        Assert.Equal(Head, published.Commit);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("42")]
    [InlineData("Nonsense")]
    public async Task A_verdict_name_that_is_not_a_standing_is_not_a_published_verdict_even_when_it_parses_as_a_number(string name)
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open());
        await world.OperationAsync(HostingOperationKind.PublishVerdictStatus, 12, verdict: name);

        Assert.Null(Assert.Single(await Views(world).ListAsync(world.Project, Ct)).PublishedVerdict);
    }

    [Fact]
    public async Task The_milestone_of_the_run_comes_from_its_links_and_a_run_in_none_has_no_milestone()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open());
        Assert.Null(Assert.Single(await Views(world).ListAsync(world.Project, Ct)).Milestone);

        var milestone = await world.OpenMilestoneAsync();
        await world.Milestones.LinkAsync(world.Project, milestone.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, Run), "manuel", Ct);

        Assert.Equal(milestone.Header.Id.Value, Assert.Single(await Views(world).ListAsync(world.Project, Ct)).Milestone);
    }

    [Fact]
    public async Task The_activity_of_the_remote_names_the_operation_the_outcome_the_changes_and_the_pull_request()
    {
        using var world = await JudgedAsync();
        await world.OperationAsync(HostingOperationKind.MergePullRequest, 12, MergeMethod.Squash, HostingOutcome.RateLimited);
        await world.ObserveAsync(Open(), Run, RemoteChangeKind.Opened);
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash), Run, RemoteChangeKind.Merged, RemoteChangeKind.BaseChanged);

        var events = await new RepositoryActivityReader(world.Instance.Recorder).ListAsync(world.Project, null, Ct);

        var operation = events.Single(e => e.Kind == RepositoryActivityKind.RemoteOperation);
        Assert.Equal((HostingOperationKind.MergePullRequest, HostingOutcome.RateLimited, 12, false), (operation.Operation, operation.Outcome, operation.PullRequest, operation.Succeeded));
        var observations = events.Where(e => e.Kind == RepositoryActivityKind.RemoteObservation).ToList();
        Assert.Contains(RemoteChangeKind.Opened, observations[0].Changes!);
        Assert.Equal([RemoteChangeKind.Merged, RemoteChangeKind.BaseChanged], observations[1].Changes!);
        Assert.All(observations, o => Assert.Equal(12, o.PullRequest));
    }

    [Theory]
    [InlineData(VerdictDecision.Rejected, AgentStudio.Application.Hosting.VerdictStanding.Rejected)]
    [InlineData(VerdictDecision.ChangesRequested, AgentStudio.Application.Hosting.VerdictStanding.Rejected)]
    [InlineData(VerdictDecision.EscalateToHuman, AgentStudio.Application.Hosting.VerdictStanding.InReview)]
    [InlineData(VerdictDecision.Unresolved, AgentStudio.Application.Hosting.VerdictStanding.InReview)]
    public async Task The_standing_of_a_verdict_that_is_not_an_approval_is_not_approved_or_in_review(VerdictDecision decision, AgentStudio.Application.Hosting.VerdictStanding expected)
    {
        using var world = await JudgedAsync(decision);
        await world.ObserveAsync(Open());

        Assert.Equal(expected, Assert.Single(await Views(world).ListAsync(world.Project, Ct)).Verdict);
    }

    [Fact]
    public async Task A_head_that_is_not_the_judged_commit_is_said_and_a_pull_request_with_no_run_has_no_verdict()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open("8888888888888888888888888888888888888888"));
        await world.ObserveAsync(Open() with { PullRequest = new PullRequestRef(Repository, 13) }, run: null);

        var views = await Views(world).ListAsync(world.Project, Ct);

        Assert.Equal(2, views.Count);
        var withRun = views.Single(v => v.PullRequest.Number == 12);
        Assert.False(withRun.HeadIsJudgedCommit);
        Assert.Equal(Head, withRun.JudgedCommit);
        var without = views.Single(v => v.PullRequest.Number == 13);
        Assert.Equal((null, null, false), (without.Run, without.Verdict, without.HeadIsJudgedCommit));
    }

    [Fact]
    public async Task A_pull_request_the_app_opened_and_has_not_observed_is_listed_without_a_snapshot_and_nothing_remote_gives_an_empty_list()
    {
        using var empty = await WorkWorld.OpenAsync();
        Assert.Empty(await Views(empty).ListAsync(empty.Project, Ct));

        using var world = await JudgedAsync();
        await world.OperationAsync(HostingOperationKind.OpenPullRequest, 12, account: "manuel-gh");

        var view = Assert.Single(await Views(world).ListAsync(world.Project, Ct));

        Assert.Equal((12, null, null, false), (view.PullRequest.Number, view.Snapshot, view.ObservedAt, view.HeadIsJudgedCommit));
        Assert.Equal(new RunId(Run), view.Run);
    }

    [Fact]
    public async Task An_attempt_to_open_a_pull_request_that_did_not_work_is_not_a_pull_request_to_show()
    {
        using var world = await JudgedAsync();
        await world.OperationAsync(HostingOperationKind.OpenPullRequest, 12, outcome: HostingOutcome.RateLimited);
        await world.OperationAsync(HostingOperationKind.OpenPullRequest, 13, outcome: HostingOutcome.Forbidden);

        Assert.Empty(await Views(world).ListAsync(world.Project, Ct));
    }

    [Fact]
    public async Task The_same_repository_written_in_another_case_is_another_pull_request_because_the_identity_is_exact()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open());
        await world.ObserveAsync(Open() with { PullRequest = new PullRequestRef(RemoteRepository.TryCreateGitHub("largolujan", "AGENT-STUDIO")!, 12) });

        var views = await Views(world).ListAsync(world.Project, Ct);

        Assert.Equal(2, views.Count);
        Assert.Equal(2, views.Select(v => v.PullRequest.TraceId).Distinct().Count());
    }

    [Fact]
    public async Task The_verdict_is_worked_out_once_for_each_run_and_two_pull_requests_of_it_get_the_same()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open());
        await world.ObserveAsync(Open() with { PullRequest = new PullRequestRef(Repository, 13) });

        var views = await Views(world).ListAsync(world.Project, Ct);

        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.Equal((Head, AgentStudio.Application.Hosting.VerdictStanding.Approved), (v.JudgedCommit, v.Verdict)));
    }

    private sealed class FixedCache(RemoteSyncState state) : IRemoteSyncStore
    {
        public Task<RemoteSyncState> LoadAsync(string projectId, CancellationToken ct) => Task.FromResult(state);

        public Task SaveAsync(string projectId, RemoteSyncState state, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FixedMailbox(IReadOnlyList<RemoteWorkStatus> pending) : IRemoteSync
    {
        public event EventHandler<string>? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<RemoteWorkStatus> Pending(string projectId) => pending;

        public RemoteSyncHealth Health(string projectId) => new(null, null, null);

        public Task SyncNowAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException();

        public Task FollowAsync(ProjectContext project, PullRequestRef pullRequest, RunId? run, CancellationToken ct) => throw new NotSupportedException();

        public Task UnfollowAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct) => throw new NotSupportedException();

        public Task CancelAsync(string projectId, string ticketId, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_texts_of_the_cache_and_what_is_pending_come_to_the_view_of_their_pull_request_only()
    {
        using var world = await JudgedAsync();
        await world.ObserveAsync(Open());
        var display = new RemoteDisplayTexts("Multiplicar", ["build"], null);
        var followed = new FollowedPullRequest(PullRequest, Run, new ObservationCursor(new Dictionary<ObservedResource, string>(), null), display, null);
        var cache = new FixedCache(new RemoteSyncState(null, [followed], [], null, null, null));
        var mine = new RemoteWorkStatus(new RemoteWorkTicket("t1", HostingOperationKind.MergePullRequest, PullRequest), RemoteWorkState.PendingSync, PendingReason.Offline, null, null);
        var theirs = new RemoteWorkStatus(new RemoteWorkTicket("t2", HostingOperationKind.MergePullRequest, new PullRequestRef(Repository, 99)), RemoteWorkState.Queued, null, null, null);

        var view = Assert.Single(await Views(world, cache, new FixedMailbox([mine, theirs])).ListAsync(world.Project, Ct));

        Assert.Equal(display, view.Display);
        Assert.Equal([mine], view.Pending);
    }

    [Fact]
    public async Task The_registration_gives_the_projector_and_asks_for_nothing_the_other_projections_do_not_have()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var returned = HostingProjectionsServiceCollectionExtensions.AddAgentStudioHostingProjections(services);

        Assert.Same(services, returned);
        Assert.Contains(services, d => d.ServiceType == typeof(IPullRequestProjector) && d.ImplementationType == typeof(PullRequestProjector));
        await Task.CompletedTask;
    }
}
