using System.IO;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Tracing;
using AgentStudio.Orchestration.Publication;
using AgentStudio.Tests.Infrastructure.Git;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests.Integration.Phase10;

/// <summary>
/// F10-7 and F10-6 with the app composed for real (docs/21 §9, «F10-2 y F10-7, sin rodeos»): a real run of the engine is published through the real service, gate and remote executor to a bare folder; a platform with
/// no script (<see cref="LocalRemoteHostingProvider"/>) opens the pull request and merges it with real git (squash, rebase and merge commit), and a revert follows; the mailbox observes it by asking that platform, which reads
/// the folder; and the view of the pull request, the log and the chain hold the commits git made. Nothing here writes a SHA.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class RemoteJourneyLocalToolsTests
{
    private static readonly RemoteBranch Main = RemoteBranch.TryParse("main")!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(MergeMethod.Squash)]
    [InlineData(MergeMethod.Rebase)]
    [InlineData(MergeMethod.MergeCommit)]
    public async Task A_run_is_published_merged_on_the_platform_and_reverted_and_the_log_the_view_and_the_chain_hold_the_commits_git_made(MergeMethod method)
    {
        await using var world = await Phase10World.OpenAsync();
        var project = world.Project;
        var run = await world.SucceededRunAsync();
        var integration = run.IntegrationCommit!;

        // Publish: the real gate (the person approves the push and the pull request), the real executor (it pushes to the folder) and the platform that opens the pull request.
        var preview = await world.Publication.PreviewAsync(project, run.Id, Main, Ct);
        Assert.True(preview.Ready, preview.NotReadyReason?.Code);
        world.Clock.Advance(TimeSpan.FromSeconds(2)); // the courtesy after the preview let go of the project
        var status = await PublishAsync(world, new PublicationRequest(project, run.Id, Main, preview.ProposedText, false));
        Assert.True(status is { State: RemoteWorkState.Done }, Why(world, status));
        Assert.Equal(integration, world.BareRef(RemoteBranch.ForRun(run.Id).Name));
        var found = await world.Platform.FindPullRequestAsync(Phase10World.Repository, RemoteBranch.ForRun(run.Id), Ct);
        var pull = Assert.IsType<PullRequestRef>(found.Value);

        // Somebody else moves the base branch, and then the platform merges with the method of the case: real git, in a clone of the folder.
        var baseBefore = world.Platform.AdvanceBase();
        var merged = await world.Platform.MergeAsync(pull, method, integration, Ct);
        Assert.True(merged.Value!.Merged);
        var mergeCommit = world.BareRef("main")!;
        Assert.Equal(mergeCommit, merged.Value.MergeCommit);
        var made = TempGitRepo.Git(world.Bare, "rev-list", "--reverse", $"{baseBefore}..{mergeCommit}").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // The mailbox observes by asking the platform, which reads the folder; the log gets the observation, written with the project taken.
        await world.Sync.FollowAsync(project, pull, run.Id, Ct);
        await world.Sync.SyncNowAsync(project, Ct);
        await world.SettleAsync();
        var view = await ViewAsync(world, pull, v => v.Snapshot?.State == PullRequestState.Merged);
        var snapshot = view.Snapshot!;
        Assert.Equal(mergeCommit, snapshot.MergeCommit);
        Assert.Equal(made, snapshot.ResultCommits);
        Assert.Equal(integration, snapshot.HeadCommit);
        Assert.Equal(integration, snapshot.OriginalCommits[^1]);
        Assert.True(view.HeadIsJudgedCommit, "the head that was merged is the commit the judge saw");
        // The mode is deduced from the commits git left, as the adapter does (D-27): a merge commit has two parents, a rebase leaves several commits, and one commit for one original says «squash o rebase».
        var expectedMode = method == MergeMethod.MergeCommit ? RemoteMergeMode.MergeCommit
            : made.Length > 1 ? RemoteMergeMode.Rebase
            : snapshot.OriginalCommits.Count == 1 ? RemoteMergeMode.SquashOrRebase
            : RemoteMergeMode.Squash;
        Assert.Equal(expectedMode, snapshot.MergeMode);
        Assert.All(snapshot.ResultCommits.Append(snapshot.MergeCommit!), c => TempGitRepo.Git(world.Bare, "cat-file", "-e", c + "^{commit}"));

        // The chain from a commit of the run reaches the pull request and the way it was merged, and the log still verifies and keeps the entries of the run.
        var chain = await world.App.Get<ITraceChainReader>().ChainAsync(project, TraceRef.Commit(integration), Ct);
        var steps = string.Join("\n", chain.Steps.Select(s => $"{s.Stage} {s.Node} {s.Title}"));
        Assert.True(chain.Steps.Any(s => s.Stage == TraceChainStage.Publication), steps);
        Assert.True(chain.Steps.Any(s => s.Stage == TraceChainStage.RemoteMerge && s.Title == RemoteTraceTitles.MergeMode(expectedMode)), steps);
        var log = await world.LogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
        Assert.Contains(log.Entries, e => e.Kind == DecisionKind.HostingOperationPerformed);
        Assert.Contains(log.Entries, e => e.Kind == DecisionKind.RemoteStateObserved);

        // The revert, made on the platform: the snapshot names the commits of git whose message carries the standard line, and the chain says «revertido por…».
        var reverts = world.Platform.Revert(pull);
        await world.Sync.SyncNowAsync(project, Ct);
        await world.SettleAsync();
        view = await ViewAsync(world, pull, v => v.Snapshot is { RevertCommits.Count: > 0 });
        Assert.Equal(reverts.Order(StringComparer.Ordinal), view.Snapshot!.RevertCommits.Order(StringComparer.Ordinal));
        Assert.All(reverts, r => TempGitRepo.Git(world.Bare, "cat-file", "-e", r + "^{commit}"));
        Assert.True((await world.LogAsync()).Verification.IsValid);
    }

    [Fact]
    public async Task With_the_project_taken_by_a_run_nothing_of_git_is_launched_and_nothing_is_written_and_when_it_is_free_the_same_request_goes_through()
    {
        await using var world = await Phase10World.OpenAsync();
        var project = world.Project;
        var run = await world.SucceededRunAsync();
        var request = new PublicationRequest(project, run.Id, Main, new PullRequestText("Title", "Body"), false);
        var logBefore = (await world.LogAsync()).Entries.Count;
        var launched = world.Git.Specs.Count;

        using (var taken = world.Activity.TryEnter(project.ProjectId, ProjectActivityKind.Run))
        {
            Assert.NotNull(taken);

            var preview = await world.Publication.PreviewAsync(project, run.Id, Main, Ct);
            Assert.False(preview.Ready);
            var refused = await PublishAsync(world, request);
            Assert.Equal(RemoteWorkState.Failed, refused?.State);
            _ = await world.Publication.InspectAsync(project, Ct);

            // The positive control is the other half of this test: nothing was launched while taken, and the same calls launch git once it is free.
            Assert.Equal(launched, world.Git.Specs.Count);
            Assert.Equal(logBefore, (await world.LogAsync()).Entries.Count);
            Assert.Null(world.BareRef(RemoteBranch.ForRun(run.Id).Name));
        }

        world.Clock.Advance(TimeSpan.FromSeconds(2));
        var done = await PublishAsync(world, request);
        Assert.True(done is { State: RemoteWorkState.Done }, Why(world, done));
        Assert.True(world.Git.Specs.Count > launched);
        Assert.Equal(run.IntegrationCommit, world.BareRef(RemoteBranch.ForRun(run.Id).Name));
        Assert.True((await world.LogAsync()).Entries.Count > logBefore);
    }

    [Fact]
    public async Task A_run_that_starts_in_the_middle_of_a_push_waits_and_enters_when_the_mailbox_lets_go_of_the_project()
    {
        var hold = new HoldingGit();
        await using var world = await Phase10World.OpenAsync(services => hold.Wrap(services));
        var project = world.Project;
        var run = await world.SucceededRunAsync();
        var request = new PublicationRequest(project, run.Id, Main, new PullRequestText("Title", "Body"), false);

        // The publication is inside its push, with the project taken for the remote operation; a run that asks for the project now waits (D-25), it is not told «ocupado».
        var publishing = world.Publication.PublishAsync(request, Ct);
        // The publication identifies the repository (a take of the project) and then runs its flow (another one): between them the courtesy of the mailbox parks it, and the clock of the mailbox is the test's, so it is moved
        // until the flow is inside its push (D-25: the mailbox holds the project for the remote operation).
        await Task.WhenAny(publishing, hold.InPush.Task).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var box = await world.App.Get<RemoteSyncService>().OfAsync(project, Ct);
        for (var i = 0; i < 200 && !hold.InPush.Task.IsCompleted; i++)
        {
            await Task.WhenAny(hold.InPush.Task, box.WhenIdleAsync());
            if (!hold.InPush.Task.IsCompleted)
            {
                world.Clock.Advance(TimeSpan.FromSeconds(1.5));
            }
        }

        Assert.True(hold.InPush.Task.IsCompleted, "The publication never reached its push.");
        var time = new FakeTimeProvider();
        var entering = ProjectActivityEntry.EnterAsync(world.Activity, project.ProjectId, ProjectActivityKind.Run, time, Ct);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(ProjectActivityEntry.RemoteWaitPoll);
            await Task.Yield();
        }

        Assert.False(entering.IsCompleted, "the run waits while the mailbox holds the project");
        Assert.Equal(ProjectActivityKind.RemoteOperation, world.Activity.Current(project.ProjectId));

        hold.Release.SetResult();
        var ticket = await publishing;
        await world.SettleAsync();
        Assert.Equal(RemoteWorkState.Done, box.StatusOf(ticket.Id)?.State);
        for (var i = 0; i < 50 && !entering.IsCompleted; i++)
        {
            time.Advance(ProjectActivityEntry.RemoteWaitPoll);
            await Task.Yield();
        }

        using var lease = await entering.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.NotNull(lease);
        Assert.Equal(ProjectActivityKind.Run, world.Activity.Current(project.ProjectId));
        Assert.Equal(run.IntegrationCommit, world.BareRef(RemoteBranch.ForRun(run.Id).Name));
    }

    /// <summary>What the mailbox says it did with the requests (for the message of an assertion).</summary>
    private static string Why(Phase10World world, RemoteWorkStatus? status) =>
        $"{status?.State} {status?.Reason} {status?.Message?.Code} / activity: {world.Activity.Current(world.Project.ProjectId)} / platform: {string.Join(",", world.Platform.Calls)}";

    /// <summary>The publication is work of the mailbox: the ticket comes back when it is queued. This waits until the mailbox has nothing queued or running (its own test door, no wall clock) and gives back what became of the ticket.</summary>
    private static async Task<RemoteWorkStatus?> PublishAsync(Phase10World world, PublicationRequest request)
    {
        var ticket = await world.Publication.PublishAsync(request, Ct);
        await world.SettleAsync();
        return (await world.App.Get<RemoteSyncService>().OfAsync(world.Project, Ct)).StatusOf(ticket.Id);
    }

    /// <summary>The view of the pull request once the mailbox has settled (it was observed by the click of «Sincronizar ahora» and written with the project taken); with a message that says what the mailbox and the log hold when it is not what the test expects.</summary>
    private static async Task<PullRequestView> ViewAsync(Phase10World world, PullRequestRef pull, Func<PullRequestView, bool> ready)
    {
        await world.SettleAsync();
        var views = await world.App.Get<IPullRequestProjector>().ListAsync(world.Project, Ct);
        var log = await world.LogAsync();
        var seen = log.Entries.GroupBy(e => e.Kind).Select(g => $"{g.Key}x{g.Count()}");
        var view = views.FirstOrDefault(v => v.PullRequest == pull);
        Assert.True(view is not null && ready(view), $"views: {views.Count}; state: {view?.Snapshot?.State}; log: {string.Join(",", seen)}; health: {world.Sync.Health(world.Project.ProjectId)}; platform: {string.Join(",", world.Platform.Calls)}");
        return view!;
    }

    /// <summary>The remote executor of the app, held inside its push until the test lets it go.</summary>
    private sealed class HoldingGit
    {
        public TaskCompletionSource InPush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Wrap(IServiceCollection services)
        {
            var inner = services.Last(d => d.ServiceType == typeof(IRemoteGitRunner));
            services.Replace(ServiceDescriptor.Singleton<IRemoteGitRunner>(provider =>
                new Holder(this, (IRemoteGitRunner)inner.ImplementationFactory!(provider))));
        }

        private sealed class Holder(HoldingGit owner, IRemoteGitRunner inner) : IRemoteGitRunner
        {
            public Task<RemoteGitInspection> InspectAsync(AgentStudio.Application.Projects.ProjectContext project, RemoteRepository destination, CancellationToken ct) => inner.InspectAsync(project, destination, ct);

            public Task<RemoteBranchRead> ReadBranchAsync(AgentStudio.Application.Projects.ProjectContext project, RemoteRepository destination, RemoteBranch branch, CancellationToken ct) => inner.ReadBranchAsync(project, destination, branch, ct);

            public async Task<RemotePushResult> PushRunBranchAsync(RemotePush push, CancellationToken ct)
            {
                owner.InPush.TrySetResult();
                await owner.Release.Task.WaitAsync(ct);
                return await inner.PushRunBranchAsync(push, ct);
            }
        }
    }
}
