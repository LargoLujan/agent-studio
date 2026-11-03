using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// The consented resume in the shell (F8-4; docs/16 D-39, ADR-0034 and ADR-0036): one pass for each project opened, and every
/// run it resumed followed by the monitor of the «Orquestador» document of its project (created if the tab was never opened),
/// so the run has its place in the tray, its pause and its choice on exit. A project without its storage is never opened for a
/// pass (the suspension of the passes while the app leaves is in <see cref="ExitSuspendsResumesTests"/>). Over doubles and the
/// engine's harness: nothing is shown and no CLI is launched.
/// </summary>
public sealed class ResumedRunTests
{
    private static readonly RunId Run = new("20260101-abc123");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_the_scheduler_resumed_is_followed_by_its_orchestrator_by_the_tray_and_by_the_exit()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        var project = new ProjectContext("p-1", root);
        var plan = PlanFixtures.Plan();
        var runs = new InMemoryRunRegistry();
        await runs.CreateAsync(Header(project, new RunOptions(AutoResumeConsent: true), plan.ContentHash), Ct);
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var recorder = new FakeRunExitRecorder();

        var seen = await WpfHost.RunAsync(async () =>
        {
            var work = new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan);
            var resumes = new ScriptedResumes();
            var main = Shell(Factory(plans, runs), work, resumes);
            var resumed = new FakeRun(new RunSnapshot(Run, RunPhase.Running, [FakeRun.View("T1", status: WorkTaskStatus.Running)], FakeRun.Healthy, 1));

            // The scheduler resumes on a thread of its own, before the tab of the project was ever opened.
            await Task.Run(() => resumes.Raise(project, resumed), Ct);
            WpfHost.FlushBindings();
            var tracked = (RunWork)Assert.Single(work.Active);

            // Opening the tab shows the document that follows it, with its plan and its consent.
            await main.OpenProjectAsync(root);
            await main.OpenOrchestratorCommand.ExecuteAsync(null);
            var tab = Assert.IsType<OrchestratorDocumentViewModel>(main.ActiveContent);
            await tab.ResumedCheck;

            // Leaving now writes the choice in its log, as for any run the shell follows.
            var exit = new ManagedExit(work, new FakeExitPrompt(ExitChoice.ExitNow), recorder, new FakeShellWindow(), new FakeAppShutdown());
            await exit.ExitAsync();
            return (tracked.WorkId, tracked.Project, SameMonitor: ReferenceEquals(tracked.Monitor, tab.Monitor), tab.Monitor.ResumeConsent, Nodes: tab.Canvas.Nodes.Count, tab.Canvas.IsReadOnly, resumes.Asked.ToList());
        });

        Assert.Equal(Run.Value, seen.WorkId);
        Assert.Equal(project, seen.Project);
        Assert.True(seen.SameMonitor, "The run is followed by the monitor of the «Orquestador» of its project.");
        Assert.True(seen.ResumeConsent);
        Assert.Equal(plan.Tasks.Count, seen.Nodes);
        Assert.True(seen.IsReadOnly);
        Assert.Equal([root], seen.Item7);
        Assert.Equal([("p-1", Run.Value, RunExitChoice.ExitNow)], recorder.Recorded.ToList());
    }

    [Fact]
    public async Task Each_project_opened_gets_one_pass_in_the_session()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");

        var asked = await WpfHost.RunAsync(async () =>
        {
            var resumes = new ScriptedResumes();
            var main = Shell(OrchestratorDocumentFactory.WithoutEngine(), new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan), resumes);
            await main.OpenProjectAsync(a);
            await main.ConsentedResumeCheck;
            await main.OpenProjectAsync(b);
            await main.OpenProjectAsync(a);
            await main.ConsentedResumeCheck;
            return resumes.Asked.ToList();
        });

        Assert.Equal([a, b], asked);
    }

    [Fact]
    public async Task A_run_resumed_while_its_orchestrator_follows_another_gets_a_monitor_of_its_own_that_the_tray_follows()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        var project = new ProjectContext("p-1", root);
        var other = new RunId("20260101-def456");

        var (titles, message) = await WpfHost.RunAsync(async () =>
        {
            var work = new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan);
            var resumes = new ScriptedResumes();
            var main = Shell(Factory(new FakePlanStore(), new InMemoryRunRegistry()), work, resumes);
            await main.OpenProjectAsync(root);
            await main.OpenOrchestratorCommand.ExecuteAsync(null);
            var tab = Assert.IsType<OrchestratorDocumentViewModel>(main.ActiveContent);
            Assert.True(tab.FollowResumed(new FakeRun(new RunSnapshot(Run, RunPhase.Running, [], FakeRun.Healthy, 0)), project));

            await Task.Run(() => resumes.Raise(project, new FakeRun(new RunSnapshot(other, RunPhase.Running, [], FakeRun.Healthy, 0))), Ct);
            WpfHost.FlushBindings();
            return (work.Active.Select(w => w.WorkId).ToList(), main.StatusMessage);
        });

        Assert.Equal([Run.Value, other.Value], titles);
        Assert.Contains("la sigue la bandeja", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pass_held_back_is_asked_for_again_once_the_passes_may_run_and_not_after_it_went_over_the_project()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");

        var (whileHeld, afterLetGo, afterAgain) = await WpfHost.RunAsync(async () =>
        {
            // The managed exit holds the resumes (or the tray has not started): the pass of the project is held back.
            var resumes = new ScriptedResumes { HeldBack = true };
            var main = Shell(OrchestratorDocumentFactory.WithoutEngine(), new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan), resumes);
            await main.OpenProjectAsync(a);
            await main.ConsentedResumeCheck;
            var held = resumes.Asked.Count;

            // The person stayed: the passes may run, and the one held back is asked for again; a second release asks nothing.
            resumes.LetGo();
            WpfHost.FlushBindings();
            await main.ConsentedResumeCheck;
            var letGo = resumes.Asked.Count;
            resumes.LetGo();
            WpfHost.FlushBindings();
            await main.ConsentedResumeCheck;
            return (held, letGo, resumes.Asked.Count);
        });

        Assert.Equal(1, whileHeld);
        Assert.Equal(2, afterLetGo);
        Assert.Equal(2, afterAgain);
    }

    [Fact]
    public async Task A_run_no_monitor_could_take_keeps_its_place_in_the_registry_and_the_shell_says_so_in_spanish()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        var project = new ProjectContext("p-1", root);

        var (tracked, message) = await WpfHost.RunAsync(async () =>
        {
            var work = new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan);
            var resumes = new ScriptedResumes();
            var main = Shell(new BrokenFactory(), work, resumes);
            await Task.Run(() => resumes.Raise(project, new FakeRun(new RunSnapshot(Run, RunPhase.Running, [], FakeRun.Healthy, 0))), Ct);
            WpfHost.FlushBindings();
            return (work.Active.Select(w => (w.WorkId, w.Kind)).ToList(), main.StatusMessage);
        });

        // The announcement of the run stays: the tray shows it, and the exit asks about it, pauses it and writes its choice.
        Assert.Equal([(Run.Value, BackgroundWorkKind.Run)], tracked);
        Assert.Equal($"La ejecución {Run} se reanudó sola, pero no se pudo seguir en su pestaña. Sigue en marcha: la bandeja la muestra, y al salir se te pregunta por ella.", message);
    }

    [Fact]
    public async Task No_pass_resumes_anything_until_the_managed_exit_exists()
    {
        using var harness = new EngineHarness();
        CreateStorage(harness.Project.RootPath);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(OrchestrationFixtures.Plan(OrchestrationFixtures.Work("T1")), new RunOptions(AutoResumeConsent: true));
        await harness.WaitForStatusAsync(first, "T1", WorkTaskStatus.Running);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);
        var workspace = new CountingWorkspace(harness.Project);
        var resumes = new ConsentedResumes(new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs), workspace);
        var available = 0;
        resumes.Available += (_, _) => available++;

        // Before the tray started: nothing is looked at, and the pass is held back.
        var before = await resumes.ResumeAsync(harness.Project.RootPath, Ct);
        var opensBefore = workspace.Opens;
        resumes.Enable();
        resumes.Enable();
        var after = await resumes.ResumeAsync(harness.Project.RootPath, Ct);
        await using var resumed = Assert.Single(after.Outcome).Resumed!;
        await resumed.CompleteAsync(harness);

        Assert.Equal(ConsentedPass.Skipped, before);
        Assert.Equal(0, opensBefore);
        Assert.Equal(1, available);
        Assert.True(after.Complete);
        Assert.False(resumes.IsHeld);
    }

    [Fact]
    public async Task A_project_without_its_storage_is_never_opened_for_a_pass()
    {
        using var harness = new EngineHarness();
        var workspace = new CountingWorkspace(harness.Project);
        var resumes = new ConsentedResumes(new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs), workspace);
        resumes.Enable();
        var root = harness.Project.RootPath;

        Assert.Empty((await resumes.ResumeAsync(root, Ct)).Outcome);
        Assert.Equal(0, workspace.Opens);

        CreateStorage(root);
        Assert.Empty((await resumes.ResumeAsync(root, Ct)).Outcome);
        Assert.Equal(1, workspace.Opens);
    }

    [Fact]
    public async Task A_pass_resumes_the_run_its_consent_allows_and_announces_it_with_its_project()
    {
        using var harness = new EngineHarness();
        CreateStorage(harness.Project.RootPath);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });

        // A run with the consent, cut while T1 runs by a close nobody asked for: nothing is recorded of the exit.
        var first = await harness.StartAsync(OrchestrationFixtures.Plan(OrchestrationFixtures.Work("T1")), new RunOptions(AutoResumeConsent: true));
        await harness.WaitForStatusAsync(first, "T1", WorkTaskStatus.Running);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);
        var resumes = new ConsentedResumes(new ResumeScheduler(harness.CreateOrchestrator(), harness.Decisions, harness.Runs), new CountingWorkspace(harness.Project));
        resumes.Enable();
        var announced = new List<ScheduledResumeEventArgs>();
        resumes.Resumed += (_, e) => announced.Add(e);

        var outcome = Assert.Single((await resumes.ResumeAsync(harness.Project.RootPath, Ct)).Outcome);
        await using var resumed = outcome.Resumed!;
        await resumed.CompleteAsync(harness);

        Assert.Equal((first.Id, RunResumeCause.ConsentAfterUnrequestedClose), (outcome.Run, outcome.Cause));
        var told = Assert.Single(announced);
        Assert.Same(resumed, told.Run);
        Assert.Equal(harness.Project, told.Project);
    }

    private static MainViewModel Shell(IOrchestratorDocumentFactory factory, BackgroundWorkRegistry work, IConsentedResumes resumes) => new(
        new InMemorySettingsStore(),
        new FakeFolderPicker(),
        new FakeUnsavedChangesPrompt(),
        new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
        new ExplorerViewModel(),
        new JournalViewModel(TimeProvider.System),
        new QuickOpenViewModel(),
        orchestrators: factory,
        work: work,
        resumes: resumes);

    private static OrchestratorDocumentFactory Factory(FakePlanStore plans, IRunRegistry runs) =>
        new(new OrchestrationSession(Backends.Complete(new FakeOrchestrator(), plans) with { Runs = runs }), new NoCanvasLayout(), new FakeTimeProvider(T0));

    private static RunRecord Header(ProjectContext project, RunOptions options, string planHash) => new(
        Run,
        project.ProjectId,
        T0,
        planHash,
        PlanFixtures.Requirements().ContentHash,
        new EvidenceId("ev-1"),
        new string('a', 40),
        "main",
        Backends.Profile,
        PlanFixtures.Roster(),
        options,
        null);

    /// <summary>The database of the project, as the workspace leaves it once opened: what a pass needs to find before opening it.</summary>
    private static void CreateStorage(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, ".agentstudio"));
        File.WriteAllBytes(Path.Combine(root, ".agentstudio", "agentstudio.db"), []);
    }

    /// <summary>
    /// The consented resume, scripted: the projects a pass was asked for, the runs the test says it resumed, and passes held
    /// back (<see cref="HeldBack"/>) until the test lets them go (<see cref="LetGo"/>).
    /// </summary>
    private sealed class ScriptedResumes : IConsentedResumes
    {
        public List<string> Asked { get; } = [];

        /// <summary>Whether the passes asked for now are held back (not complete).</summary>
        public bool HeldBack { get; set; }

        public event EventHandler<ScheduledResumeEventArgs>? Resumed;

        public event EventHandler? Available;

        public Task<ConsentedPass> ResumeAsync(string projectRoot, CancellationToken cancellationToken)
        {
            Asked.Add(projectRoot);
            return Task.FromResult(HeldBack ? ConsentedPass.Skipped : new ConsentedPass([], Complete: true));
        }

        public void Raise(ProjectContext project, IOrchestrationRun run) =>
            Resumed?.Invoke(this, new ScheduledResumeEventArgs(project, run, RunResumeCause.ConsentAfterUnrequestedClose));

        /// <summary>The passes may run again: <see cref="IConsentedResumes.Available"/>.</summary>
        public void LetGo()
        {
            HeldBack = false;
            Available?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A factory whose documents cannot be created: no monitor can take the run.</summary>
    private sealed class BrokenFactory : IOrchestratorDocumentFactory
    {
        public OrchestratorDocumentViewModel Create(string projectRoot) => throw new InvalidOperationException("The document could not be created.");
    }

    private sealed class CountingWorkspace(ProjectContext project) : IProjectWorkspace
    {
        public int Opens { get; private set; }

        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken)
        {
            Opens++;
            return Task.FromResult(project);
        }
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
