using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Repository;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Desktop.Views.Repository;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;
using AgentStudio.Tests.Desktop.RepositoryScreen;
using AgentStudio.Tests.Desktop.Work;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens of Phase 10 (docs/21 section 7): «Repositorio y cambios › PR», the cards of publication and merge, and the new parts of «Ramas», «Actividad» and «Trabajo». The P0
/// gives the file with its list empty, already added up in <see cref="UiScreens"/>, so WP5 only fills it and no other package touches <c>UiScreens.cs</c>: every view WP5 creates
/// is added here, and the guard of the inventory fails if one is missing. The data carries no words (identifiers and figures), so the check of the language reads only the interface.
/// </summary>
internal static class UiScreensHosting
{
    private static readonly string Sha1 = FakeHistory.Sha('1');

    private static PullRequestView View(int number, RemoteSnapshot? snapshot, string? run = "20261112-a1b2c3", string? title = "x-101", IReadOnlyList<RemoteWorkStatus>? pending = null) =>
        PrData.View(snapshot, number, run, display: new RemoteDisplayTexts(title, ["unit-1"], "prod-1"), pending: pending);

    /// <summary>A repository with a pull request open, one merged and deployed, one closed and one waiting to sync, over the doubles of the ports.</summary>
    private static async Task<(RepositoryViewModel Model, PrWorld World)> LoadedAsync(bool select = false)
    {
        var world = new PrWorld();
        var pending = new RemoteWorkStatus(new RemoteWorkTicket("t-1", HostingOperationKind.MergePullRequest, PrData.Pr(14)), RemoteWorkState.PendingSync, PendingReason.Offline, PrData.Now.AddMinutes(5), null);
        world.Projector.Views.AddRange(
        [
            View(12, PrData.Snapshot(12)),
            View(13, PrData.Snapshot(13, PullRequestState.Merged, results: [PrData.Sha('f')], deployment: new RemoteDeployment(7, PrData.Sha('f'), DeploymentState.Succeeded)), "20261110-d4e5f6"),
            View(14, PrData.Snapshot(14, PullRequestState.Draft), "20261111-b2c3d4", pending: [pending]),
            View(15, PrData.Snapshot(15, PullRequestState.Closed, headBranchExists: false), "20261109-c3d4e5"),
        ]);
        world.Sync.PendingWork.Add(pending);
        world.Gate.Held.Add(new HostingPolicyGrant("g-1", HostingOperationClass.PublishRunBranch, PrData.Repository, null, "person", PrData.Now.AddDays(-1)));

        var history = new FakeHistory();
        history.Branches.AddRange([new BranchSummary("main", Sha1, true, BranchKind.Local), new BranchSummary("agentstudio/run-20261112-a1b2c3", Sha1, false, BranchKind.Run), new BranchSummary("agentstudio/run-20261113-aa", Sha1, false, BranchKind.Run)]);
        var activity = new FakeActivity();
        activity.Events.AddRange(
        [
            new RepositoryActivity(PrData.Now.AddMinutes(-30), RepositoryActivityKind.RemoteOperation, "20261112-a1b2c3", null, Sha1, true, null, 1),
            new RepositoryActivity(PrData.Now.AddMinutes(-20), RepositoryActivityKind.RemoteOperation, "20261112-a1b2c3", null, null, false, "hosting.outcome.rate-limited", 2),
            new RepositoryActivity(PrData.Now.AddMinutes(-10), RepositoryActivityKind.RemoteObservation, "20261110-d4e5f6", null, null, true, null, 3),
        ]);
        var model = new RepositoryViewModel(new FakeWorkspace(), history, null, activity, null, PrData.Clock(), world.Model);
        model.ChainRequested += (_, _) => { };
        await model.SetProjectAsync(@"C:\work\calc");
        if (select)
        {
            world.Model.Selected = world.Model.Rows.Single(r => r.Number == 12);
        }

        return (model, world);
    }

    private static UiScreen Section(string name, RepositorySection section, Func<RepositoryViewModel, PrWorld, Task>? prepare = null, bool select = false, double height = 1500) =>
        UiScreens.ScreenAsync(name, async () =>
        {
            var (model, world) = await LoadedAsync(select: select);
            if (prepare is not null)
            {
                await prepare(model, world);
            }

            model.SelectedSection = model.Sections.Single(s => s.Value == section);
            return UiScreens.Of(new RepositoryView(), model);
        }, 1100, height);

    private static UiScreen WorkScreen(string name, Func<WorkDocumentViewModel, WorkSection> choose, Func<WorkDocumentViewModel, Task> prepare) =>
        UiScreens.ScreenAsync(name, async () =>
        {
            var board = new FixedBoard(
            [
                new(BoardCardKind.Requirement, "R1", "x-1", BoardColumn.Integrated, "20261112-a1b2c3", null, PrData.Pr(12).TraceId),
                new(BoardCardKind.Requirement, "R2", "x-2", BoardColumn.Done, "20261112-a1b2c3", BoardCardReasons.MergedWithUnjudgedChanges),
                new(BoardCardKind.Requirement, "R3", "x-3", BoardColumn.Integrated, "20261110-d4e5f6", null),
            ]);
            var chain = new FixedChain
            {
                Steps =
                [
                    new(TraceChainStage.Delivery, TraceRef.Commit(Sha1), null, false),
                    new(TraceChainStage.Publication, TraceRef.PullRequest(PrData.Pr(12)), null, false),
                    new(TraceChainStage.RemoteMerge, TraceRef.Commit(PrData.Sha('2')), RemoteTraceTitles.MergeMode(RemoteMergeMode.Squash), true),
                    new(TraceChainStage.Deployment, TraceRef.Commit(PrData.Sha('2')), RemoteTraceTitles.Deployed, false),
                    new(TraceChainStage.RemoteMerge, TraceRef.Commit(PrData.Sha('3')), RemoteTraceTitles.RevertedBy, true),
                ],
            };
            var backend = new WorkBackend(new WorldWorkspace(PrData.Project), Board: board, Chain: chain);
            var document = new WorkDocumentViewModel(backend, PrData.Clock());
            await document.SetProjectAsync(PrData.Project.RootPath);
            await prepare(document);
            document.SelectedSection = choose(document);
            return UiScreens.Document(document);
        }, 1180, 1100);

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        Section("Repository pull requests", RepositorySection.PullRequests, select: true),
        Section("Repository pull requests · merge card", RepositorySection.PullRequests, (_, world) =>
        {
            world.Model.Selected = world.Model.Rows.Single(r => r.Number == 12);
            world.Model.OpenMergeCardCommand.Execute(null);
            return Task.CompletedTask;
        }, height: 1800),
        Section("Repository pull requests · publish card", RepositorySection.PullRequests, async (_, world) =>
        {
            await world.Model.OpenPublishCardCommand.ExecuteAsync(world.Model.PublishableRuns.First());
        }, height: 1800),
        Section("Repository pull requests · permissions", RepositorySection.PullRequests, (_, _) => Task.CompletedTask),
        Section("Repository branches with remote", RepositorySection.Branches, height: 600),
        Section("Repository activity with remote", RepositorySection.Activity, height: 900),
        UiScreens.ScreenAsync("Repository pull requests · no remote", async () =>
        {
            var world = new PrWorld(withRemote: false);
            var repository = new RepositoryViewModel(new FakeWorkspace(), new FakeHistory(), null, new FakeActivity(), null, PrData.Clock(), world.Model);
            await repository.SetProjectAsync(@"C:\work\calc");
            repository.SelectedSection = repository.Sections.Single(s => s.Value == RepositorySection.PullRequests);
            return UiScreens.Of(new RepositoryView(), repository);
        }, 1100, 520),
        UiScreens.ScreenAsync("Repository pull requests · no gh", async () =>
        {
            var world = new PrWorld();
            world.Provider.Availability = new HostingAvailability(false, null, HostingCapabilities.None);
            var repository = new RepositoryViewModel(new FakeWorkspace(), new FakeHistory(), null, new FakeActivity(), null, PrData.Clock(), world.Model);
            await repository.SetProjectAsync(@"C:\work\calc");
            repository.SelectedSection = repository.Sections.Single(s => s.Value == RepositorySection.PullRequests);
            return UiScreens.Of(new RepositoryView(), repository);
        }, 1100, 520),
        UiScreens.ScreenAsync("Repository pull requests · not available", async () =>
        {
            var repository = new RepositoryViewModel(new FakeWorkspace(), new FakeHistory(), null, new FakeActivity(), null, PrData.Clock());
            await repository.SetProjectAsync(@"C:\work\calc");
            repository.SelectedSection = repository.Sections.Single(s => s.Value == RepositorySection.PullRequests);
            return UiScreens.Of(new RepositoryView(), repository);
        }, 1100, 420),
        WorkScreen("Work · board with pull requests", d => d.Board, d =>
        {
            d.Context.SelectMilestone(new MilestoneId("mil-20261112-aaaaaa"));
            return d.Board.ReloadAsync();
        }),
        WorkScreen("Work · chain with pull request", d => d.Chain, d =>
        {
            d.Context.LookAt(TraceRef.Requirement("R1"));
            return d.Chain.ReloadAsync();
        }),
    ];
}
