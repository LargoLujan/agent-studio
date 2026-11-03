using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// «Ejecuciones anteriores» opened in the monitor (B8): «Ver» reads a run back from its log and shows it in read-only, its
/// plan on the canvas with the status of each task; a run approved and not delivered is delivered as from a live monitor;
/// a run in progress in the monitor is never put away for it; «Cerrar» brings back the draft. While the monitor shows a run,
/// the review says which plan it follows (V24).
/// </summary>
public sealed class PreviousRunsTests
{
    private const string Root = @"C:\proyectos\calc";
    private static readonly RunId Done = new("20260101-abc123");
    private static readonly RunId Cut = new("20260102-def456");
    private static readonly ProjectContext Project = new("p-1", Root);

    private sealed class NoLayout : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    /// <summary>Gives back, for each run it knows, a run read from its log; it records what it was asked.</summary>
    internal sealed class FakeInspector : IRunInspector
    {
        public Dictionary<RunId, Func<IOrchestrationRun>> Runs { get; } = [];

        public List<RunId> Inspected { get; } = [];

        public Task<IOrchestrationRun> InspectAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
        {
            Inspected.Add(run);
            return Runs.TryGetValue(run, out var read)
                ? Task.FromResult(read())
                : Task.FromException<IOrchestrationRun>(new InvalidOperationException($"Run {run} is not registered."));
        }
    }

    /// <summary>A run that finished, as the inspector reads it: every task completed, in its history, and its result.</summary>
    internal static FakeRun Finished(RunId id, PlanDefinition plan, RunPhase phase = RunPhase.Succeeded)
    {
        var run = new FakeRun(new RunSnapshot(
            id,
            phase,
            [.. plan.Tasks.Select(t => FakeRun.View(t.Id.Value, t.Kind, WorkTaskStatus.Completed))],
            FakeRun.Healthy,
            AgentRunsUsed: 3));
        run.Emit([.. plan.Tasks.SelectMany(t => new OrchestrationEvent[]
        {
            new TaskStatusChangedEvent(T0, t.Id, WorkTaskStatus.Pending, WorkTaskStatus.Running, "Despachada."),
            new TaskStatusChangedEvent(T0, t.Id, WorkTaskStatus.Running, WorkTaskStatus.Completed, "Completada."),
        })]);
        run.Finish(new RunResult(id, phase, FakeRun.Healthy, "c0ffee0000000000000000000000000000000000", "veredicto-1", "Hecho."));
        return run;
    }

    private sealed record Setup(OrchestratorDocumentViewModel Document, FakeOrchestrator Engine, FakeInspector Inspector, FakeDeliveryService Delivery, PlanDefinition Plan);

    private static async Task<Setup> OpenAsync(bool interrupted = false)
    {
        var plan = PlanFixtures.Plan();
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var engine = new FakeOrchestrator();
        engine.Runs.Add(new RunSummary(Done, T0, RunPhase.Succeeded, plan.ContentHash, "Hecho.", null));
        if (interrupted)
        {
            engine.Runs.Add(new RunSummary(Cut, T0.AddHours(1), RunPhase.Interrupted, plan.ContentHash, "Interrumpida.", null));
        }

        var inspector = new FakeInspector();
        inspector.Runs[Done] = () => Finished(Done, plan);
        var delivery = new FakeDeliveryService();
        var backend = Backends.Complete(engine, plans) with { Inspector = inspector, Delivery = delivery };
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(backend), new NoLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();
        return new Setup(document, engine, inspector, delivery, plan);
    }

    private static RunListItem Item(OrchestratorDocumentViewModel document, RunId run) => document.PreviousRuns.Single(r => r.Summary.Id == run);

    [Fact]
    public async Task Ver_opens_a_finished_run_in_read_only_with_its_plan_and_Cerrar_brings_the_draft_back()
    {
        var (document, _, inspector, _, plan) = await OpenAsync();
        document.RequestText = "Mi borrador";
        document.Canvas.LoadTasks([PlanFixtures.Spec("D1")], CanvasLayout.Empty);
        var item = Item(document, Done);
        Assert.True(item.CanView);
        Assert.False(item.CanResume);
        Assert.True(document.ViewRunCommand.CanExecute(item));

        await document.ViewRunCommand.ExecuteAsync(item);
        await document.Monitor.Listening;

        Assert.Equal([Done], inspector.Inspected);
        Assert.True(document.Monitor.IsInspecting);
        Assert.False(document.Monitor.IsActive);
        Assert.Equal("Completada", document.Monitor.PhaseLabel);
        Assert.True(document.Canvas.IsReadOnly);
        Assert.Equal(plan.Tasks.Select(t => t.Id), document.Canvas.Nodes.Select(n => n.Id));
        Assert.All(document.Canvas.Nodes, n => Assert.Equal(WorkTaskStatus.Completed, n.Status));
        Assert.True(item.IsViewed);
        Assert.Equal($"Viendo la ejecución {Done}: siguió el plan {plan.ContentHash[..12]}, aprobado al iniciarla.", document.ApprovalText);
        Assert.Contains("Cierra la ejecución que estás viendo", document.ExecuteBlockers, StringComparison.Ordinal);
        Assert.False(document.EditPlanCommand.CanExecute(null));
        Assert.False(document.ViewRunCommand.CanExecute(item));
        Assert.Equal("Mi borrador", document.RequestText);

        // «Cerrar» in the monitor asks the document, which puts the run away and brings the draft back, editable.
        document.Monitor.CloseInspectionCommand.Execute(null);
        await document.CloseRunViewCommand.ExecutionTask!;

        Assert.False(document.Monitor.IsAttached);
        Assert.False(document.Canvas.IsReadOnly);
        Assert.Equal(["D1"], document.Canvas.Nodes.Select(n => n.Id.Value));
        Assert.All(document.Canvas.Nodes, n => Assert.Equal(WorkTaskStatus.Pending, n.Status));
        Assert.False(item.IsViewed);
        Assert.Equal("Plan sin aprobar.", document.ApprovalText);
        Assert.Equal("Mi borrador", document.RequestText);
    }

    [Fact]
    public async Task A_run_approved_and_not_delivered_is_delivered_from_the_view_through_the_same_delivery_service()
    {
        var (document, _, _, delivery, _) = await OpenAsync();

        await document.ViewRunCommand.ExecuteAsync(Item(document, Done));
        await document.Monitor.Listening;
        await document.Monitor.DeliveryCheck;

        Assert.Equal([(Project, Done)], delivery.Checks);
        Assert.True(document.Monitor.DeliverCommand.CanExecute(null));
        await document.Monitor.DeliverCommand.ExecuteAsync(null);
        Assert.Equal([(Project, Done, DeliveryMode.FastForwardOnly)], delivery.Deliveries);
        Assert.True(document.Monitor.IsDelivered);
    }

    [Fact]
    public async Task A_run_in_progress_in_the_monitor_is_never_put_away_to_open_another()
    {
        var (document, engine, inspector, _, plan) = await OpenAsync();
        var live = new RunId("20260103-aaa111");
        engine.Start = _ => new FakeRun(new RunSnapshot(live, RunPhase.Running, [FakeRun.View("T1", status: WorkTaskStatus.Running)], FakeRun.Healthy, 1));
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(plan.Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.True(document.Monitor.IsActive);
        var item = Item(document, Done);

        Assert.False(document.ViewRunCommand.CanExecute(item));
        await document.ViewRunCommand.ExecuteAsync(item);

        Assert.Equal(live, document.Monitor.RunId);
        Assert.True(document.Monitor.IsActive);
        Assert.False(document.Monitor.IsInspecting);
        Assert.Contains("ocupado con otra ejecución", document.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal([Done], inspector.Inspected);
    }

    [Fact]
    public async Task Reanudar_puts_away_the_run_looked_at_and_follows_the_run_it_resumes()
    {
        var (document, engine, _, _, _) = await OpenAsync(interrupted: true);
        engine.Resume = id => new FakeRun(new RunSnapshot(id, RunPhase.Running, [FakeRun.View("T1", status: WorkTaskStatus.Running)], FakeRun.Healthy, 1));
        await document.ViewRunCommand.ExecuteAsync(Item(document, Done));
        var cut = Item(document, Cut);
        Assert.True(cut.CanView);
        Assert.True(document.ResumeRunCommand.CanExecute(cut));

        await document.ResumeRunCommand.ExecuteAsync(cut);

        Assert.Equal([Cut], engine.Resumed);
        Assert.Equal(Cut, document.Monitor.RunId);
        Assert.False(document.Monitor.IsInspecting);
        Assert.True(document.Monitor.IsActive);
        Assert.False(Item(document, Done).IsViewed);
    }

    [Fact]
    public async Task While_the_monitor_follows_a_run_the_review_says_the_plan_of_that_run_and_not_the_draft()
    {
        var (document, engine, _, _, plan) = await OpenAsync();
        FakeRun? started = null;
        var live = new RunId("20260103-aaa111");
        engine.Start = _ => started = new FakeRun(new RunSnapshot(live, RunPhase.Running, [FakeRun.View("T1", status: WorkTaskStatus.Running)], FakeRun.Healthy, 1));
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(plan.Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);
        var hash = document.CurrentHash[..12];

        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal($"La ejecución {live} sigue el plan {hash}, aprobado al iniciarla.", document.ApprovalText);
        started!.Finish(new RunResult(live, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Hecho."));
        await document.Monitor.Listening;
        Assert.Equal($"La ejecución {live} terminó con el plan {hash}, aprobado al iniciarla.", document.ApprovalText);

        await document.EditPlanCommand.ExecuteAsync(null);
        Assert.Equal("Plan sin aprobar.", document.ApprovalText);
    }

    /// <summary>
    /// An approval is given to the plan on screen when it is given (and confirmed with Windows Hello then): a plan that comes
    /// back with «Cerrar» is approved again, as after «Editar el plan».
    /// </summary>
    [Fact]
    public async Task An_approved_plan_comes_back_from_Ver_and_Cerrar_unapproved()
    {
        var (document, engine, _, _, plan) = await OpenAsync();
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(plan.Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);
        Assert.True(document.IsApproved);
        var approved = document.CurrentHash;

        await document.ViewRunCommand.ExecuteAsync(Item(document, Done));
        Assert.Null(document.PlanDecision);
        await document.CloseRunViewCommand.ExecuteAsync(null);

        Assert.Equal(approved, document.CurrentHash);
        Assert.Null(document.PlanDecision);
        Assert.False(document.IsApproved);
        Assert.Equal("Plan sin aprobar.", document.ApprovalText);
        Assert.False(document.ExecuteCommand.CanExecute(null));
        Assert.Equal("Aprueba el plan", document.ExecuteBlockers);
        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.Empty(engine.RunRequests);
    }

    [Fact]
    public async Task A_plan_that_ran_never_runs_again_on_its_old_approval_after_Ver_and_Cerrar()
    {
        var (document, engine, _, _, plan) = await OpenAsync();
        var live = new RunId("20260103-aaa111");
        FakeRun? started = null;
        engine.Start = _ => started = new FakeRun(new RunSnapshot(live, RunPhase.Running, [], FakeRun.Healthy, 0));
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(plan.Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);
        started!.Finish(new RunResult(live, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Hecho."));
        await document.Monitor.Listening;

        await document.ViewRunCommand.ExecuteAsync(Item(document, Done));
        await document.CloseRunViewCommand.ExecuteAsync(null);

        Assert.Null(document.PlanDecision);
        Assert.False(document.IsApproved);
        Assert.False(document.ExecuteCommand.CanExecute(null));
        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.Single(engine.RunRequests);
    }

    [Fact]
    public async Task Ver_is_not_offered_while_the_tab_is_busy_asking_for_a_plan_and_its_button_is_told()
    {
        var (document, engine, _, _, _) = await OpenAsync();
        var pending = new TaskCompletionSource<PlanProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Plan = (_, _, cancellation) =>
        {
            cancellation.Register(() => pending.TrySetCanceled(cancellation));
            return pending.Task;
        };
        document.RequestText = "Añadir Calc.Multiply";
        var item = Item(document, Done);
        Assert.True(document.ViewRunCommand.CanExecute(item));
        var told = 0;
        document.ViewRunCommand.CanExecuteChanged += (_, _) => told++;

        var planning = document.RequestPlanCommand.ExecuteAsync(null);

        Assert.True(document.IsBusy);
        Assert.False(document.ViewRunCommand.CanExecute(item));
        Assert.True(told > 0, "The button of «Ver» was not told that it cannot be used now.");
        document.CancelPlanningCommand.Execute(null);
        await planning;
        Assert.True(document.ViewRunCommand.CanExecute(item));
    }

    [Fact]
    public async Task Without_an_inspector_the_previous_runs_are_only_listed()
    {
        var plan = PlanFixtures.Plan();
        var engine = new FakeOrchestrator();
        engine.Runs.Add(new RunSummary(Done, T0, RunPhase.Succeeded, plan.ContentHash, "Hecho.", null));
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(Backends.Complete(engine)), new NoLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();

        Assert.False(document.ViewRunCommand.CanExecute(Item(document, Done)));
    }
}
