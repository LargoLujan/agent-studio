using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// The consent of a run to resume by itself (F8-4; docs/16 D-39, decision 4 (a); ADR-0034) in the «Orquestador» document: a
/// box next to «Ejecutar», off by default and again after each start, that goes into the options of the run, and so into its
/// header and the hash <c>RunStarted</c> anchors; the monitor says whether the run it follows has it, whoever started or
/// resumed it. The engine is a fake.
/// </summary>
public sealed class ResumeConsentTests
{
    private const string Root = @"C:\proyectos\calc";
    private static readonly RunId Run = new("20260101-abc123");
    private static readonly ProjectContext Project = new("p-1", Root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_consent_is_off_by_default_and_a_run_started_without_it_has_none()
    {
        var (document, engine, _) = await ApprovedAsync();

        Assert.False(document.AutoResumeConsent);
        Assert.True(document.CanChooseResumeConsent);
        await document.ExecuteCommand.ExecuteAsync(null);

        var request = Assert.Single(engine.RunRequests);
        Assert.False(request.Options.AutoResumeConsent);
        Assert.Equal(new RunOptions(), request.Options);
        Assert.False(document.Monitor.ResumeConsent);
        Assert.StartsWith("Sin consentimiento para reanudarse sola", document.Monitor.ResumeConsentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marked_the_consent_goes_into_the_options_and_the_hashed_header_of_that_run_only()
    {
        var (document, engine, started) = await ApprovedAsync();

        document.AutoResumeConsent = true;
        await document.ExecuteCommand.ExecuteAsync(null);

        // In the options of the request, which the engine freezes in the header of the run (RunRecord.Options).
        var request = Assert.Single(engine.RunRequests);
        Assert.Equal(new RunOptions(AutoResumeConsent: true), request.Options);

        // The waits are the engine's defaults, which StartAsync accepts (the document exposes none of them).
        request.Options.EnsureValid();
        var withConsent = RunHeaderHash.Of(Header(request.Options, request.Plan.ContentHash));
        var without = RunHeaderHash.Of(Header(new RunOptions(), request.Plan.ContentHash));
        Assert.NotEqual(without, withConsent);

        // The monitor says it; the box is off again for the next run, and cannot change while this one is on screen.
        Assert.True(document.Monitor.ResumeConsent);
        Assert.StartsWith("Con tu consentimiento para reanudarse sola", document.Monitor.ResumeConsentText, StringComparison.Ordinal);
        Assert.False(document.AutoResumeConsent);
        Assert.False(document.CanChooseResumeConsent);

        started.Run!.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, "c0ffee", null, "Hecho"));
        await document.Monitor.Listening;
        await document.EditPlanCommand.ExecuteAsync(null);

        Assert.True(document.CanChooseResumeConsent);
        Assert.Null(document.Monitor.ResumeConsent);
        Assert.Null(document.Monitor.ResumeConsentText);
    }

    [Fact]
    public async Task A_run_the_person_resumes_shows_the_consent_its_header_has()
    {
        var plan = PlanFixtures.Plan();
        var runs = new InMemoryRunRegistry();
        await runs.CreateAsync(Header(new RunOptions(AutoResumeConsent: true), plan.ContentHash), Ct);
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var engine = new FakeOrchestrator();
        engine.Runs.Add(new RunSummary(Run, T0, RunPhase.Interrupted, plan.ContentHash, "Interrumpida.", null));
        engine.Resume = _ => new FakeRun(new RunSnapshot(Run, RunPhase.Running, [FakeRun.View("T1", status: WorkTaskStatus.Running)], FakeRun.Healthy, 1));
        var document = await OpenAsync(Backends.Complete(engine, plans) with { Runs = runs });

        await document.ResumeRunCommand.ExecuteAsync(Assert.Single(document.PreviousRuns));

        Assert.Equal([Run], engine.Resumed);
        Assert.True(document.Monitor.ResumeConsent);
    }

    [Fact]
    public async Task A_run_the_scheduler_resumed_is_followed_at_once_and_then_shows_its_plan_and_its_consent()
    {
        var plan = PlanFixtures.Plan();
        var runs = new InMemoryRunRegistry();
        await runs.CreateAsync(Header(new RunOptions(AutoResumeConsent: true), plan.ContentHash), Ct);
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;

        // The tab was never opened: the document knows the project only from the scheduler.
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(Backends.Complete(new FakeOrchestrator(), plans) with { Runs = runs }), new MemoryCanvasLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
        var resumed = new FakeRun(new RunSnapshot(Run, RunPhase.Running, [FakeRun.View("T1", status: WorkTaskStatus.Running)], FakeRun.Healthy, 1));

        Assert.True(document.FollowResumed(resumed, Project));
        Assert.Equal(Run, document.Monitor.RunId);
        Assert.Same(Project, document.Monitor.Project);
        await document.ResumedCheck;

        Assert.True(document.Monitor.ResumeConsent);
        Assert.Equal(plan.Tasks.Count, document.Canvas.Nodes.Count);
        Assert.True(document.Canvas.IsReadOnly);
        Assert.Equal(WorkTaskStatus.Running, document.Canvas.Nodes.Single(n => n.Id.Value == "T1").Status);
        Assert.False(document.CanChooseResumeConsent);

        // The monitor follows one run at a time: another resumed run is the caller's to follow.
        Assert.False(document.FollowResumed(new FakeRun(FakeRun.SnapshotOf(RunPhase.Running)), Project));
        Assert.Equal(Run, document.Monitor.RunId);
    }

    /// <summary>A plan proposed, valid and approved: «Ejecutar» starts it on the fake engine, and the run it starts lands in the holder.</summary>
    private static async Task<(OrchestratorDocumentViewModel Document, FakeOrchestrator Engine, StartedRun Started)> ApprovedAsync()
    {
        var engine = new FakeOrchestrator();
        var plan = PlanFixtures.Plan();
        var started = new StartedRun();
        engine.Plan = (_, _, _) => Task.FromResult(new PlanProposal(Run, plan, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null));
        engine.Start = request => started.Run = new FakeRun(new RunSnapshot(Run, RunPhase.Running, [.. request.Plan.Tasks.Select(t => FakeRun.View(t.Id.Value, t.Kind))], FakeRun.Healthy, 0));
        var document = await OpenAsync(Backends.Complete(engine));
        document.RequestText = "Añadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        document.ApproveCommand.Execute(null);
        Assert.True(document.ExecuteCommand.CanExecute(null), document.ExecuteBlockers);
        return (document, engine, started);
    }

    private sealed class StartedRun
    {
        public FakeRun? Run { get; set; }
    }

    private static async Task<OrchestratorDocumentViewModel> OpenAsync(OrchestrationBackend backend)
    {
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(backend), new MemoryCanvasLayout(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();
        return document;
    }

    private static RunRecord Header(RunOptions options, string planHash) => new(
        Run,
        Project.ProjectId,
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

    private sealed class MemoryCanvasLayout : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }
}
