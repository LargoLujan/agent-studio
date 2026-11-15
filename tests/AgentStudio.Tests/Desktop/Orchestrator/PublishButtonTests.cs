using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Repository;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.RepositoryScreen;
using AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// «Publicar y abrir PR» of the monitor (docs/21 §7.2, docs/18 P77 and P81): offered only where the section of pull requests exists, once the run finished and while it has a run; asking raises
/// <see cref="RunMonitorViewModel.PublishRequested"/> with the run; and the shell wires it on the monitors it makes for runs resumed on their own (no document) as on the ones of a document, so the card
/// of publication of that run opens in the section of pull requests. Over doubles: nothing is published and no CLI is launched.
/// </summary>
public sealed class PublishButtonTests
{
    private static readonly RunId Run = new("20260101-abc123");

    private static readonly ProjectContext Project = new("p-1", @"C:\proyectos\calc");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RunResult Succeeded() => new(Run, RunPhase.Succeeded, FakeRun.Healthy, "c0ffee0000000000000000000000000000000000", "veredicto-1", "Hecho");

    [Fact]
    public async Task The_button_appears_when_the_section_exists_the_run_finished_and_has_a_run_and_goes_when_any_of_them_goes()
    {
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
        Assert.False(monitor.PublishCommand.CanExecute(null));

        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run, Project);
        monitor.IsPublishAvailable = true;
        Assert.False(monitor.PublishCommand.CanExecute(null), "the run is still going");

        run.Finish(Succeeded());
        await monitor.Listening;
        Assert.True(monitor.IsFinished);
        Assert.True(monitor.PublishCommand.CanExecute(null));

        monitor.IsPublishAvailable = false;
        Assert.False(monitor.PublishCommand.CanExecute(null), "without the section of pull requests there is no button");
        monitor.IsPublishAvailable = true;
        Assert.True(monitor.PublishCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_monitor_without_the_section_never_offers_it_even_when_the_run_finished()
    {
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run, Project);
        run.Finish(Succeeded());
        await monitor.Listening;

        Assert.False(monitor.IsPublishAvailable);
        Assert.False(monitor.PublishCommand.CanExecute(null));
    }

    [Fact]
    public async Task Asking_to_publish_raises_the_request_with_the_run_of_the_monitor_and_publishes_nothing()
    {
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext()) { IsPublishAvailable = true };
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1")));
        monitor.Attach(run, Project);
        run.Finish(Succeeded());
        await monitor.Listening;
        var asked = new List<RunId>();
        monitor.PublishRequested += (_, id) => asked.Add(id);

        monitor.PublishCommand.Execute(null);

        Assert.Equal([Run], asked);
    }

    [Fact]
    public async Task A_run_resumed_on_its_own_is_offered_for_publication_and_the_card_of_its_run_opens_in_the_pull_requests()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        var project = new ProjectContext("p-1", root);
        var other = new RunId("20260101-def456");

        var (offered, requested, calls) = await WpfHost.RunAsync(async () =>
        {
            var world = new PrWorld();
            var publication = new RunRecordingPublication(world.Publication);
            var pullRequests = new PullRequestsViewModel(world.Providers, world.Projector, world.Sync, publication, world.Gate, new FakeSyncPolicy(true), PrData.Clock(), world.Opener, world.Permission);
            var repository = new RepositoryViewModel(new RootWorkspace(), new FakeHistory(), null, new FakeActivity(), null, PrData.Clock(), pullRequests);
            var resumes = new ScriptedResumes();
            var main = Shell(Factory(), new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan), resumes, repository);
            await main.OpenProjectAsync(root);
            await main.OpenOrchestratorCommand.ExecuteAsync(null);
            var tab = Assert.IsType<OrchestratorDocumentViewModel>(main.ActiveContent);

            // The tab follows one run and the next one resumed on its own gets a monitor of the shell's own, with no document.
            Assert.True(tab.FollowResumed(new FakeRun(new RunSnapshot(Run, RunPhase.Running, [], FakeRun.Healthy, 0)), project));
            var second = new FakeRun(new RunSnapshot(other, RunPhase.Running, [FakeRun.View("T1")], FakeRun.Healthy, 0));
            await Task.Run(() => resumes.Raise(project, second), Ct);
            WpfHost.FlushBindings();
            var monitor = Assert.Single(main.BackgroundMonitors);
            Assert.True(monitor.IsPublishAvailable);
            Assert.False(monitor.PublishCommand.CanExecute(null), "the run is still going");

            main.OpenBackgroundRunsCommand.Execute(null);
            var backgroundTab = Assert.IsType<BackgroundRunsDocumentViewModel>(main.ActiveContent);
            Assert.Same(monitor, Assert.Single(backgroundTab.Runs).Monitor);
            Assert.Same(monitor, backgroundTab.SelectedMonitor);
            Assert.True(await main.CloseDocumentAsync(backgroundTab));
            Assert.True(monitor.IsActive, "closing a view must not stop the shell's run");
            Assert.Same(monitor, Assert.Single(main.BackgroundMonitors));

            second.Finish(new RunResult(other, RunPhase.Succeeded, FakeRun.Healthy, "c0ffee0000000000000000000000000000000000", "veredicto-1", "Hecho"));
            await monitor.Listening;
            WpfHost.FlushBindings();
            var offeredNow = monitor.PublishCommand.CanExecute(null);

            main.OpenBackgroundRunsCommand.Execute(null);
            Assert.Same(backgroundTab, main.ActiveContent);
            Assert.Same(monitor, Assert.Single(backgroundTab.Runs).Monitor);
            Assert.Same(monitor, backgroundTab.SelectedMonitor);

            var cardOpened = new TaskCompletionSource();
            pullRequests.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PullRequestsViewModel.Card) && pullRequests.Card is not null)
                {
                    cardOpened.TrySetResult();
                }
            };
            monitor.PublishCommand.Execute(null);
            await cardOpened.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            return (offeredNow, publication.Previewed.ToList(), world.Publication.Calls.ToList());
        });

        Assert.True(offered);
        Assert.Equal([other], requested);
        Assert.Contains("preview", calls);
    }

    private static MainViewModel Shell(IOrchestratorDocumentFactory factory, BackgroundWorkRegistry work, IConsentedResumes resumes, RepositoryViewModel repository) => new(
        new InMemorySettingsStore(),
        new FakeFolderPicker(),
        new FakeUnsavedChangesPrompt(),
        new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
        new ExplorerViewModel(),
        new JournalViewModel(TimeProvider.System),
        new QuickOpenViewModel(),
        orchestrators: factory,
        work: work,
        resumes: resumes,
        repository: repository);

    private static OrchestratorDocumentFactory Factory() =>
        new(new OrchestrationSession(Backends.Complete(new FakeOrchestrator(), new FakePlanStore()) with { Runs = new InMemoryRunRegistry() }), new NoCanvasLayout(), new FakeTimeProvider(T0));

    /// <summary>The publication of the doubles of the section, noting the run each preview was asked for.</summary>
    private sealed class RunRecordingPublication(FakePublication inner) : IPublicationService
    {
        public List<RunId> Previewed { get; } = [];

        public Task<RemoteGitInspection?> InspectAsync(ProjectContext project, CancellationToken ct) => inner.InspectAsync(project, ct);

        public Task<PublicationPreview> PreviewAsync(ProjectContext project, RunId run, RemoteBranch? baseBranch, CancellationToken ct)
        {
            Previewed.Add(run);
            return inner.PreviewAsync(project, run, baseBranch, ct);
        }

        public Task<RemoteWorkTicket> PublishAsync(PublicationRequest request, CancellationToken ct) => inner.PublishAsync(request, ct);

        public Task<RemoteWorkTicket> UpdateVerdictAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct) => inner.UpdateVerdictAsync(project, pullRequest, ct);

        public Task<RemoteWorkTicket> RequestReviewersAsync(ProjectContext project, PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct) => inner.RequestReviewersAsync(project, pullRequest, reviewers, ct);

        public Task<RemoteWorkTicket> MergeAsync(ProjectContext project, PullRequestRef pullRequest, MergeMethod method, CancellationToken ct) => inner.MergeAsync(project, pullRequest, method, ct);

        public Task<RemoteWorkTicket> CloseAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct) => inner.CloseAsync(project, pullRequest, ct);

        public Task<RemoteWorkTicket> DeleteRunBranchAsync(ProjectContext project, RunId run, CancellationToken ct) => inner.DeleteRunBranchAsync(project, run, ct);
    }

    /// <summary>Opens any folder as the project of the same id.</summary>
    private sealed class RootWorkspace : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new ProjectContext("p-1", projectRoot));
    }

    private sealed class ScriptedResumes : IConsentedResumes
    {
        public event EventHandler<ScheduledResumeEventArgs>? Resumed;

        public event EventHandler? Available
        {
            add { }
            remove { }
        }

        public Task<ConsentedPass> ResumeAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(new ConsentedPass([], Complete: true));

        public void Raise(ProjectContext project, IOrchestrationRun run) =>
            Resumed?.Invoke(this, new ScheduledResumeEventArgs(project, run, RunResumeCause.ConsentAfterUnrequestedClose));
    }

    private sealed class NoCanvasLayout : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }
}
