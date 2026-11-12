using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// A run the scheduler resumes while the person's «Ejecutar», «Reanudar» or «Pedir plan» waits for the engine (docs/16 D-39,
/// ADR-0036): the scheduler's run is posted to the UI thread, and runs inside those waits. The document is busy then, so it
/// does not take it (the shell follows it with a monitor of its own, which the tray follows); the person's run is followed by
/// the monitor of the document as always, and the proposal lands on the canvas. Nothing is left without a monitor, and no
/// engine text in English reaches the screen.
/// </summary>
public sealed class ResumeRaceTests
{
    private const string Root = @"C:\proyectos\calc";
    private static readonly ProjectContext Project = new("p-1", Root);
    private static readonly RunId Scheduled = new("20260101-sched1");

    [Fact]
    public async Task A_run_resumed_while_ejecutar_waits_for_the_engine_is_left_to_the_shell_and_the_persons_run_is_followed()
    {
        var (document, inner, engine, started) = await ApprovedAsync();
        document.AutoResumeConsent = true;
        engine.Start = new TaskCompletionSource();

        var executing = document.ExecuteCommand.ExecuteAsync(null);
        Assert.False(executing.IsCompleted);
        var taken = document.FollowResumed(ScheduledRun(), Project);
        engine.Start.SetResult();
        await executing;

        Assert.False(taken);
        Assert.Single(inner.RunRequests);
        var run = Assert.Single(started);
        Assert.Equal(run.Id, document.Monitor.RunId);
        Assert.Null(document.ErrorMessage);
        Assert.False(document.AutoResumeConsent);
        Assert.True(document.Monitor.ResumeConsent);
        Assert.False(run.Disposed);
    }

    [Fact]
    public async Task A_run_resumed_while_reanudar_waits_for_the_engine_is_left_to_the_shell_and_the_persons_run_is_followed()
    {
        var plan = PlanFixtures.Plan();
        var inner = new FakeOrchestrator();
        var mine = new RunId("20260101-mine01");
        inner.Runs.Add(new RunSummary(mine, T0, RunPhase.Interrupted, plan.ContentHash, "Interrumpida.", null));
        var resumed = new List<FakeRun>();
        inner.Resume = id =>
        {
            var run = new FakeRun(new RunSnapshot(id, RunPhase.Running, [], FakeRun.Healthy, 0));
            resumed.Add(run);
            return run;
        };
        var engine = new GatedEngine(inner);
        var document = Document(inner, engine);
        await document.OpenAsync();
        var item = Assert.Single(document.PreviousRuns);
        engine.Resume = new TaskCompletionSource();

        var resuming = document.ResumeRunCommand.ExecuteAsync(item);
        Assert.False(resuming.IsCompleted);
        var taken = document.FollowResumed(ScheduledRun(), Project);
        engine.Resume.SetResult();
        await resuming;

        Assert.False(taken);
        Assert.Equal([mine], inner.Resumed);
        Assert.Single(resumed);
        Assert.Equal(mine, document.Monitor.RunId);
        Assert.Null(document.ErrorMessage);
    }

    [Fact]
    public async Task A_run_resumed_while_pedir_plan_waits_for_the_orchestrator_does_not_take_the_canvas_of_the_proposal()
    {
        var plan = PlanFixtures.Plan();
        var inner = new FakeOrchestrator();
        inner.Plan = (_, _, _) => Task.FromResult(new PlanProposal(new RunId("20260101-plan01"), plan, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null));
        var engine = new GatedEngine(inner) { Plan = new TaskCompletionSource() };
        var document = Document(inner, engine);
        await document.OpenAsync();
        document.RequestText = "Calc.Multiply";

        var planning = document.RequestPlanCommand.ExecuteAsync(null);
        Assert.False(planning.IsCompleted);
        var taken = document.FollowResumed(ScheduledRun(), Project);
        engine.Plan.SetResult();
        await planning;

        Assert.False(taken);
        Assert.False(document.Monitor.IsAttached);
        Assert.NotNull(document.Proposal);
        Assert.False(document.Canvas.IsReadOnly);
        Assert.Null(document.ErrorMessage);
    }

    [Fact]
    public async Task A_run_the_person_started_once_the_monitor_followed_another_is_handed_to_the_shell_with_its_consent()
    {
        var (document, inner, engine, started) = await ApprovedAsync();
        var handed = new List<HandedOffRun>();
        document.RunHandedOff += (_, run) => handed.Add(run);
        document.AutoResumeConsent = true;
        engine.Start = new TaskCompletionSource();

        var executing = document.ExecuteCommand.ExecuteAsync(null);

        // Whatever took the monitor while the engine was starting the run, the run is not taken from it, nor left alone.
        document.Monitor.Attach(ScheduledRun(), Project);
        engine.Start.SetResult();
        await executing;

        var run = Assert.Single(started);
        var given = Assert.Single(handed);
        Assert.Same(run, given.Run);
        Assert.Equal(Root, given.Project.RootPath);
        Assert.True(given.ResumeConsent);
        Assert.Equal(Scheduled, document.Monitor.RunId);
        Assert.Single(inner.RunRequests);
        Assert.False(document.AutoResumeConsent);
        Assert.StartsWith($"La ejecución {run.Id} empezó mientras esta pestaña seguía otra ejecución", document.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("already", document.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_the_person_resumed_once_the_monitor_followed_another_is_handed_to_the_shell()
    {
        var plan = PlanFixtures.Plan();
        var inner = new FakeOrchestrator();
        var mine = new RunId("20260101-mine01");
        inner.Runs.Add(new RunSummary(mine, T0, RunPhase.Interrupted, plan.ContentHash, "Interrumpida.", null));
        inner.Resume = id => new FakeRun(new RunSnapshot(id, RunPhase.Running, [], FakeRun.Healthy, 0));
        var engine = new GatedEngine(inner);
        var document = Document(inner, engine);
        var handed = new List<HandedOffRun>();
        document.RunHandedOff += (_, run) => handed.Add(run);
        await document.OpenAsync();
        var item = Assert.Single(document.PreviousRuns);
        engine.Resume = new TaskCompletionSource();

        var resuming = document.ResumeRunCommand.ExecuteAsync(item);
        document.Monitor.Attach(ScheduledRun(), Project);
        engine.Resume.SetResult();
        await resuming;

        Assert.Equal(mine, Assert.Single(handed).Run.Id);
        Assert.Equal(Scheduled, document.Monitor.RunId);
        Assert.StartsWith($"La ejecución {mine} empezó mientras esta pestaña seguía otra ejecución", document.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shell_follows_a_handed_off_run_with_a_monitor_the_tray_follows()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        var plan = PlanFixtures.Plan();
        var inner = new FakeOrchestrator();
        var startedId = new RunId("20260101-start1");
        inner.Plan = (_, _, _) => Task.FromResult(new PlanProposal(startedId, plan, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null));
        inner.Start = request => new FakeRun(new RunSnapshot(startedId, RunPhase.Running, [.. request.Plan.Tasks.Select(t => FakeRun.View(t.Id.Value, t.Kind))], FakeRun.Healthy, 0));
        var engine = new GatedEngine(inner);
        var factory = new OrchestratorDocumentFactory(new OrchestrationSession(Backends.Complete(inner) with { Orchestrator = engine }), new MemoryCanvasLayout(), new FakeTimeProvider(T0));

        var (tracked, message) = await WpfHost.RunAsync(async () =>
        {
            var work = new BackgroundWorkRegistry(new FakeTimeProvider(T0), pollInterval: Timeout.InfiniteTimeSpan);
            var main = new MainViewModel(
                new InMemorySettingsStore(),
                new FakeFolderPicker(),
                new FakeUnsavedChangesPrompt(),
                new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
                new ExplorerViewModel(),
                new JournalViewModel(TimeProvider.System),
                new QuickOpenViewModel(),
                orchestrators: factory,
                work: work);
            await main.OpenProjectAsync(root);
            await main.OpenOrchestratorCommand.ExecuteAsync(null);
            var tab = Assert.IsType<OrchestratorDocumentViewModel>(main.ActiveContent);
            tab.RequestText = "Calc.Multiply";
            await tab.RequestPlanCommand.ExecuteAsync(null);
            tab.ApproveCommand.Execute(null);
            engine.Start = new TaskCompletionSource();

            var executing = tab.ExecuteCommand.ExecuteAsync(null);
            tab.Monitor.Attach(ScheduledRun(), Project);
            engine.Start.SetResult();
            await executing;
            return (work.Active.Select(w => w.WorkId).ToList(), main.StatusMessage);
        });

        Assert.Contains(startedId.Value, tracked);
        Assert.Contains(Scheduled.Value, tracked);
        Assert.Equal($"La ejecución {startedId} empezó, pero su pestaña seguía otra ejecución. La sigue la bandeja del sistema.", message);
    }

    [Fact]
    public async Task A_start_that_failed_keeps_the_consent_box_as_the_person_left_it()
    {
        var (document, inner, _, started) = await ApprovedAsync();
        inner.Start = _ => throw new InvalidOperationException("The project is held by a debate.");
        document.AutoResumeConsent = true;

        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.Empty(started);
        Assert.False(document.Monitor.IsAttached);
        Assert.True(document.AutoResumeConsent, "No run started: the box stays as the person marked it.");
        Assert.StartsWith("No se pudo iniciar la ejecución", document.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>A document with an approved plan, «Ejecutar» enabled, over a gated engine that records the runs it starts.</summary>
    private static async Task<(OrchestratorDocumentViewModel Document, FakeOrchestrator Inner, GatedEngine Engine, List<FakeRun> Started)> ApprovedAsync()
    {
        var plan = PlanFixtures.Plan();
        var inner = new FakeOrchestrator();
        var startedId = new RunId("20260101-start1");
        inner.Plan = (_, _, _) => Task.FromResult(new PlanProposal(startedId, plan, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null));
        var started = new List<FakeRun>();
        inner.Start = request =>
        {
            var run = new FakeRun(new RunSnapshot(startedId, RunPhase.Running, [.. request.Plan.Tasks.Select(t => FakeRun.View(t.Id.Value, t.Kind))], FakeRun.Healthy, 0));
            started.Add(run);
            return run;
        };
        var engine = new GatedEngine(inner);
        var document = Document(inner, engine);
        await document.OpenAsync();
        document.RequestText = "Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        document.ApproveCommand.Execute(null);
        Assert.True(document.ExecuteCommand.CanExecute(null), document.ExecuteBlockers);
        return (document, inner, engine, started);
    }

    private static OrchestratorDocumentViewModel Document(FakeOrchestrator inner, GatedEngine engine) =>
        new(Root, new OrchestrationSession(Backends.Complete(inner) with { Orchestrator = engine }), new MemoryCanvasLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());

    private static FakeRun ScheduledRun() => new(new RunSnapshot(Scheduled, RunPhase.Running, [], FakeRun.Healthy, 0));

    /// <summary>The fake engine, with a gate in front of planning, starting and resuming (open unless a test closes it).</summary>
    private sealed class GatedEngine(FakeOrchestrator inner) : IOrchestrator
    {
        public TaskCompletionSource? Plan { get; set; }

        public TaskCompletionSource? Start { get; set; }

        public TaskCompletionSource? Resume { get; set; }

        public async Task<PlanProposal> PlanAsync(PlanningRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken)
        {
            await (Plan?.Task ?? Task.CompletedTask);
            return await inner.PlanAsync(request, progress, cancellationToken);
        }

        public async Task<IOrchestrationRun> StartAsync(RunRequest request, CancellationToken cancellationToken)
        {
            await (Start?.Task ?? Task.CompletedTask);
            return await inner.StartAsync(request, cancellationToken);
        }

        public async Task<IOrchestrationRun> ResumeAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
        {
            await (Resume?.Task ?? Task.CompletedTask);
            return await inner.ResumeAsync(project, run, cancellationToken);
        }

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(ProjectContext project, CancellationToken cancellationToken) =>
            inner.ListRunsAsync(project, cancellationToken);
    }

    private sealed class MemoryCanvasLayout : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }
}
