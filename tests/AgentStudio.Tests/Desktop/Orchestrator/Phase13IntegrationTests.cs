using System.IO;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Setup;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Questions;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Setup;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Orchestrator.Monitor;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The integration of Phase 13, tanda 2 (docs/04 sections 21.8 to 21.10) at the level of the document and of the monitor: the
/// sources every monitor is given, the live state of the builder, the options of the project (step 7 of the configurator) that a
/// run starts with, the questions of the orchestrator before it plans and the reading of the template after the configurator.
/// The engine is a fake: nothing here launches an agent.
/// </summary>
public sealed class Phase13IntegrationTests
{
    private const string Root = @"C:\proyectos\calc";
    private static readonly RunId RunOfPlan = new("20260101-abc123");

    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    private sealed class FakeParts : IContextPartsSource
    {
        public Task<IReadOnlyList<ContextPartSnapshot>?> ReadAsync(ProjectContext project, RunId run, TaskId task, int attempt, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContextPartSnapshot>?>(null);
    }

    private sealed class FakeAnalyzer : IConsumptionAnalyzer
    {
        public Task<ConsumptionReport> AnalyzeAsync(ProjectContext project, RunId run, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAssisted : IAssistedConsumptionAnalysis
    {
        public Task<AssistedConsumptionAnalysis> AnalyzeAsync(AssistedConsumptionRequest request, IProgress<AgentEvent>? progress, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeLive : AgentStudio.Desktop.Orchestrator.Live.IRunLiveProjection
    {
        public AgentStudio.Desktop.Orchestrator.Live.RunLiveState? Current => null;

        public event EventHandler<AgentStudio.Desktop.Orchestrator.Live.RunLiveChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingHost : IQuestionWindowHost
    {
        public bool IsAppInView { get; set; } = true;

        public List<string> Shown { get; } = [];

        public List<string> Closed { get; } = [];

        public List<bool> Activations { get; } = [];

        public int Withdrawals { get; private set; }

        public int Restorations { get; private set; }

        public void Show(QuestionViewModel question, bool activate = true)
        {
            Shown.Add(question.Id);
            Activations.Add(activate);
        }

        public void Close(QuestionViewModel question) => Closed.Add(question.Id);

        public void Withdraw() => Withdrawals++;

        public void Restore() => Restorations++;
    }

    private sealed class FakeLock : IInterfaceLock
    {
        public event EventHandler? Changed;

        public bool IsLocked { get; private set; }

        public void Set(bool locked)
        {
            IsLocked = locked;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<CancellationToken> WaitUntilUnlockedAsync(ApprovalRequest question, CancellationToken cancellationToken) => Task.FromResult(CancellationToken.None);
    }

    private static MonitorServices Services(RecordingHost? host = null) => new(
        new FakeParts(),
        new QuotaViewModel(null, null, new FakeTimeProvider(T0)),
        new FakeAnalyzer(),
        new FakeAssisted(),
        (_, _) => Task.FromResult(UsageEstimate.Unknown(0, "no references")),
        interfaceLock: null,
        hostFactory: host is null ? null : () => host);

    private static async Task<OrchestratorDocumentViewModel> OpenAsync(
        FakeOrchestrator? engine = null,
        BuilderServices? builder = null,
        MonitorServices? monitors = null,
        IProjectOptionsStore? options = null,
        FakeRoster? roster = null)
    {
        var backend = Backends.Complete(engine ?? new FakeOrchestrator()) with { Roster = roster ?? new FakeRoster(PlanFixtures.Roster) };
        var document = new OrchestratorDocumentViewModel(
            Root,
            new OrchestrationSession(backend),
            new MemoryLayoutStore(),
            new FakeTimeProvider(T0),
            new InlineSynchronizationContext(),
            builder: builder,
            monitors: monitors,
            projectOptions: options);
        await document.OpenAsync();
        return document;
    }

    private static PlanProposal ProposalOf(PlanDefinition plan) =>
        new(RunOfPlan, plan, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null);

    private static PlanProposal AskingProposal() =>
        new(RunOfPlan, null, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null) { Questions = [PendingOfOrchestrator()] };

    private static FakeRun StartedRun(RunRequest request) =>
        new(new RunSnapshot(RunOfPlan, RunPhase.Running, [.. request.Plan.Tasks.Select(t => FakeRun.View(t.Id.Value, t.Kind))], FakeRun.Healthy, 0));

    // ---- the sources of every monitor (docs/04 section 21.10) ----

    [Fact]
    public async Task The_monitor_of_the_document_gets_its_sources_and_the_builder_reads_the_live_state_from_it()
    {
        var parts = new FakeParts();
        var analyzer = new FakeAnalyzer();
        var assisted = new FakeAssisted();
        var quota = new QuotaViewModel(null, null, new FakeTimeProvider(T0));
        Func<ProjectContext, CancellationToken, Task<UsageEstimate>> estimate = (_, _) => Task.FromResult(UsageEstimate.Unknown(0, "no references"));
        var monitors = new MonitorServices(parts, quota, analyzer, assisted, estimate);

        var document = await OpenAsync(builder: BuilderServices.None, monitors: monitors);

        Assert.Same(parts, document.Monitor.ContextPartsSource);
        Assert.Same(quota, document.Monitor.Quota);
        Assert.Same(analyzer, document.Monitor.Consumption.Analyzer);
        Assert.Same(assisted, document.Monitor.Consumption.Assisted);
        Assert.Same(estimate, document.Monitor.Consumption.EstimateAssisted);

        // The live state of the builder is the monitor of this same document: the composition cannot give it, the monitor is born with the document.
        Assert.Same(document.Monitor, document.Builder!.Services.Live);
    }

    [Fact]
    public async Task A_document_without_the_services_of_the_monitor_leaves_its_monitor_as_it_was()
    {
        var document = await OpenAsync();

        Assert.Null(document.Monitor.ContextPartsSource);
        Assert.Null(document.Monitor.Quota);
        Assert.Null(document.Monitor.Consumption.Analyzer);
        Assert.Null(document.Builder);
    }

    [Fact]
    public async Task The_live_state_the_composition_gives_is_kept()
    {
        var other = new FakeLive();

        var document = await OpenAsync(builder: new BuilderServices(Live: other), monitors: Services());

        Assert.Same(other, document.Builder!.Services.Live);
    }

    [Fact]
    public void An_equipped_monitor_opens_the_window_of_a_question_in_view_and_asks_the_tray_for_the_notice_out_of_view()
    {
        var host = new RecordingHost();
        var services = Services(host);
        var attention = new List<QuestionAttentionEventArgs>();
        services.AttentionRequested += (_, e) => attention.Add(e);
        var (monitor, run, ui, _) = Open(View("T1", "exec1", AgentStudio.Domain.Work.WorkTaskStatus.Running));
        using var release = services.Equip(monitor);

        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        Assert.Equal(["T1-q1"], host.Shown);
        Assert.Empty(attention);

        host.IsAppInView = false;
        run.Emit(new QuestionPendingEvent(T0, Pending("T2", "exec2")));
        Pump(ui, 1);

        // The notice carries no data of the project: not the task, not the agent.
        var notice = Assert.Single(attention);
        Assert.Equal(Texts.Get("Monitor_Question_Notice"), notice.Notice);
        Assert.DoesNotContain("T2", notice.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("exec2", notice.Notice, StringComparison.Ordinal);
        Assert.Equal(["T1-q1"], host.Shown);
    }

    [Fact]
    public async Task Without_the_release_a_disposed_service_stops_asking_the_tray()
    {
        var host = new RecordingHost { IsAppInView = false };
        var services = Services(host);
        var attention = new List<QuestionAttentionEventArgs>();
        services.AttentionRequested += (_, e) => attention.Add(e);
        var (monitor, run, ui, _) = Open(View("T1", "exec1", AgentStudio.Domain.Work.WorkTaskStatus.Running));
        var release = services.Equip(monitor);
        release.Dispose();

        run.Emit(new QuestionPendingEvent(T0, Pending()));
        Pump(ui, 1);

        Assert.Empty(attention);
        await Task.CompletedTask;
    }

    // ---- the options of the project (step 7 of the configurator) ----

    private static async Task<RunRequest> StartRunAsync(OrchestratorDocumentViewModel document, FakeOrchestrator engine)
    {
        var plan = PlanFixtures.Plan();
        engine.Plan = (_, _, _) => Task.FromResult(ProposalOf(plan));
        engine.Start = StartedRun;
        document.RequestText = "Anadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        await ApproveAsync(document);

        await document.ExecuteCommand.ExecuteAsync(null);

        return Assert.Single(engine.RunRequests);
    }

    [Fact]
    public async Task A_run_starts_with_the_options_the_person_chose_for_the_project()
    {
        using var temp = new TempDirectory();
        var folder = temp.Combine("setup");
        var store = new LocalProjectOptionsStore(folder);
        await store.SaveAsync(new ProjectContext("p-1", Root), new ProjectRunOptions(7, 2, TimeSpan.FromMinutes(10)), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(folder, "p-1.json")));
        var engine = new FakeOrchestrator();
        var document = await OpenAsync(engine, builder: BuilderServices.None, options: store);
        await document.BuilderData;

        var request = await StartRunAsync(document, engine);

        Assert.Equal(7, request.Options.MaxAgentRuns);
        Assert.Equal(2, request.Options.MaxParallelTasks);
        Assert.Equal(TimeSpan.FromMinutes(10), request.Options.DefaultTaskTimeout);
        Assert.False(request.Options.AutoResumeConsent);
        Assert.Equal(7, document.Builder!.MaxAgentRuns);
    }

    [Fact]
    public async Task The_consent_to_resume_chosen_for_this_run_is_kept_beside_the_options_of_the_project()
    {
        using var temp = new TempDirectory();
        var store = new LocalProjectOptionsStore(temp.Combine("setup"));
        await store.SaveAsync(new ProjectContext("p-1", Root), new ProjectRunOptions(30, 5, null), CancellationToken.None);
        var engine = new FakeOrchestrator();
        var document = await OpenAsync(engine, builder: BuilderServices.None, options: store);
        await document.BuilderData;
        document.AutoResumeConsent = true;

        var request = await StartRunAsync(document, engine);

        Assert.True(request.Options.AutoResumeConsent);
        Assert.Equal(30, request.Options.MaxAgentRuns);
        Assert.Equal(5, request.Options.MaxParallelTasks);
        Assert.Null(request.Options.DefaultTaskTimeout);
    }

    [Fact]
    public async Task A_raise_of_the_budget_made_on_the_canvas_is_the_one_the_run_starts_with()
    {
        using var temp = new TempDirectory();
        var store = new LocalProjectOptionsStore(temp.Combine("setup"));
        await store.SaveAsync(new ProjectContext("p-1", Root), new ProjectRunOptions(7, 2, null), CancellationToken.None);
        var engine = new FakeOrchestrator();
        var document = await OpenAsync(engine, builder: BuilderServices.None, options: store);
        await document.BuilderData;
        document.Builder!.MaxAgentRuns = 20;

        var request = await StartRunAsync(document, engine);

        Assert.Equal(20, request.Options.MaxAgentRuns);
        Assert.Equal(2, request.Options.MaxParallelTasks);
    }

    [Fact]
    public async Task Without_saved_options_or_without_a_store_a_run_starts_with_the_values_of_always()
    {
        using var temp = new TempDirectory();
        var always = new RunOptions();
        var withEmptyStore = new FakeOrchestrator();
        var first = await OpenAsync(withEmptyStore, options: new LocalProjectOptionsStore(temp.Combine("setup")));
        var withoutStore = new FakeOrchestrator();
        var second = await OpenAsync(withoutStore);

        var fromEmptyStore = await StartRunAsync(first, withEmptyStore);
        var fromNothing = await StartRunAsync(second, withoutStore);

        Assert.Equal((always.MaxAgentRuns, always.MaxParallelTasks, always.DefaultTaskTimeout), (fromEmptyStore.Options.MaxAgentRuns, fromEmptyStore.Options.MaxParallelTasks, fromEmptyStore.Options.DefaultTaskTimeout));
        Assert.Equal((always.MaxAgentRuns, always.MaxParallelTasks, always.DefaultTaskTimeout), (fromNothing.Options.MaxAgentRuns, fromNothing.Options.MaxParallelTasks, fromNothing.Options.DefaultTaskTimeout));
        Assert.Equal(ProjectRunOptions.Defaults.ToRunOptions(), fromNothing.Options);
    }

    [Fact]
    public async Task A_file_of_options_that_does_not_validate_is_no_options()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("setup");
        File.WriteAllText(Path.Combine(folder, "p-1.json"), """{"version":1,"maxAgentRuns":9999,"maxParallelTasks":2,"timePerAttemptSeconds":null}""");
        var engine = new FakeOrchestrator();
        var document = await OpenAsync(engine, options: new LocalProjectOptionsStore(folder));

        var request = await StartRunAsync(document, engine);

        Assert.Equal(ProjectRunOptions.Defaults.ToRunOptions(), request.Options);
    }

    // ---- what the document gives the monitor when it follows a run (docs/04 section 21.10) ----

    [Fact]
    public async Task The_document_gives_the_monitor_the_template_and_the_dependencies_of_the_plan_when_it_follows_a_run()
    {
        var engine = new FakeOrchestrator();
        var document = await OpenAsync(engine, builder: BuilderServices.None, monitors: Services());

        var request = await StartRunAsync(document, engine);

        Assert.Equal(PlanFixtures.Roster().Select(a => a.Id.Value).Order(), document.Monitor.Roster.Select(a => a.Id.Value).Order());
        Assert.Equal(request.Plan.Tasks.Select(t => t.Id.Value).Order(), document.Monitor.PlanDependencies.Keys.Select(k => k.Value).Order());
        Assert.All(document.Monitor.PlanDependencies, pair => Assert.Equal(request.Plan.Tasks.Single(t => t.Id == pair.Key).DependsOn.Select(d => d.Prerequisite), pair.Value));
    }

    // ---- the questions of the orchestrator before it plans (docs/17 D-28) ----

    [Fact]
    public async Task When_the_orchestrator_asks_the_questions_open_and_with_every_answer_it_plans_again_once_with_them()
    {
        var host = new RecordingHost();
        var engine = new FakeOrchestrator();
        var plan = PlanFixtures.Plan();
        engine.Plan = (request, _, _) => Task.FromResult(request.Answers is { Count: > 0 } ? ProposalOf(plan) : AskingProposal());
        var document = await OpenAsync(engine, builder: BuilderServices.None, monitors: Services(host));
        document.RequestText = "Anadir Calc.Multiply";

        await document.RequestPlanCommand.ExecuteAsync(null);

        // The windows opened by themselves, without taking the focus, and the status says that the orchestrator asks.
        var set = Assert.IsType<PlannerQuestionSet>(document.PlannerQuestions);
        Assert.Equal(["plan-q1"], host.Shown);
        Assert.Equal([false], host.Activations);
        Assert.Equal(Texts.Format("Shell_Planner_Questions", 1), document.ProposalStatus);
        Assert.True(document.HasPlannerQuestions);
        Assert.Null(document.Proposal!.Plan);
        Assert.Single(engine.PlanRequests);
        Assert.Null(engine.PlanRequests[0].Answers);

        // «Responder» opens them again, now taking the focus.
        document.OpenPlannerQuestionsCommand.Execute(null);
        Assert.Equal(["plan-q1", "plan-q1"], host.Shown);
        Assert.Equal([false, true], host.Activations);

        var question = set.Questions[0];
        question.Options[0].IsSelected = true;
        await question.AnswerCommand.ExecuteAsync(null);
        await WaitForAsync(() => engine.PlanRequests.Count == 2);

        var again = engine.PlanRequests[1];
        var answered = Assert.Single(again.Answers!);
        Assert.Equal("plan-q1", answered.Question.Id);
        Assert.Equal(RunOfPlan, answered.Run);
        await WaitForAsync(() => document.Proposal?.Plan is not null);
        Assert.Null(document.PlannerQuestions);
        Assert.Equal(2, engine.PlanRequests.Count);
    }

    [Fact]
    public async Task Questions_of_the_orchestrator_out_of_view_ask_the_tray_and_open_no_window()
    {
        var host = new RecordingHost { IsAppInView = false };
        var services = Services(host);
        var attention = new List<QuestionAttentionEventArgs>();
        services.AttentionRequested += (_, e) => attention.Add(e);
        var engine = new FakeOrchestrator { Plan = (_, _, _) => Task.FromResult(AskingProposal()) };
        var document = await OpenAsync(engine, builder: BuilderServices.None, monitors: services);
        document.RequestText = "Anadir Calc.Multiply";

        await document.RequestPlanCommand.ExecuteAsync(null);

        Assert.Empty(host.Shown);
        Assert.Equal(Texts.Get("Monitor_Question_Notice"), Assert.Single(attention).Notice);
        Assert.NotNull(document.PlannerQuestions);
    }

    [Fact]
    public async Task The_windows_of_the_questions_of_the_orchestrator_go_with_the_lock_and_none_opens_while_it_is_on()
    {
        var host = new RecordingHost();
        var interfaceLock = new FakeLock();
        var services = new MonitorServices(interfaceLock: interfaceLock, hostFactory: () => host);
        var attention = new List<QuestionAttentionEventArgs>();
        services.AttentionRequested += (_, e) => attention.Add(e);
        var engine = new FakeOrchestrator { Plan = (_, _, _) => Task.FromResult(AskingProposal()) };
        var document = await OpenAsync(engine, builder: BuilderServices.None, monitors: services);
        document.RequestText = "Anadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        Assert.Equal(["plan-q1"], host.Shown);

        interfaceLock.Set(true);
        Assert.Equal(1, host.Withdrawals);

        // Locked, «Responder» opens nothing over the lock screen: the tray is told instead, with nothing of the project.
        document.OpenPlannerQuestionsCommand.Execute(null);
        Assert.Equal(["plan-q1"], host.Shown);
        Assert.Equal(Texts.Get("Monitor_Question_Notice"), Assert.Single(attention).Notice);

        interfaceLock.Set(false);
        Assert.Equal(1, host.Restorations);
    }

    [Fact]
    public async Task Rejecting_or_asking_for_another_plan_stops_waiting_for_the_answers()
    {
        var host = new RecordingHost();
        var engine = new FakeOrchestrator { Plan = (_, _, _) => Task.FromResult(AskingProposal()) };
        var document = await OpenAsync(engine, builder: BuilderServices.None, monitors: Services(host));
        document.RequestText = "Anadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        var first = document.PlannerQuestions!;

        document.RejectCommand.Execute(null);

        Assert.Null(document.PlannerQuestions);
        Assert.Contains("plan-q1", host.Closed);

        // The answers of a set that nobody waits for any more plan nothing.
        first.Questions[0].Options[0].IsSelected = true;
        await first.Questions[0].AnswerCommand.ExecuteAsync(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(engine.PlanRequests);

        await document.RequestPlanCommand.ExecuteAsync(null);
        var second = document.PlannerQuestions;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(2, engine.PlanRequests.Count);
    }

    // ---- «Configurar proyecto» (docs/04 section 21.9) ----

    [Fact]
    public async Task Set_up_project_asks_the_shell_for_the_configurator()
    {
        var document = await OpenAsync();
        var asked = 0;
        document.ConfigureProjectRequested += (_, _) => asked++;

        document.ConfigureProjectCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task When_the_configurator_changes_the_template_the_document_reads_it_again_and_the_approval_is_withdrawn()
    {
        var agents = PlanFixtures.Roster().ToList();
        var engine = new FakeOrchestrator { Plan = (_, _, _) => Task.FromResult(ProposalOf(PlanFixtures.Plan())) };
        var document = await OpenAsync(engine, builder: BuilderServices.None, roster: new FakeRoster(() => [.. agents]));
        document.RequestText = "Anadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        await ApproveAsync(document);
        Assert.True(document.PlanDecision is { Approved: true });
        agents[0] = agents[0] with { Name = "Otro nombre" };

        await document.OnSetupAppliedAsync(new SetupApplyResult([], "a", "b", RosterChanged: true, Recorded: true, null));

        Assert.Null(document.PlanDecision);
        Assert.Equal("Otro nombre", document.Context!.Roster[0].Name);
    }

    [Fact]
    public async Task A_configurator_that_changed_nothing_in_the_template_keeps_the_approval()
    {
        var engine = new FakeOrchestrator { Plan = (_, _, _) => Task.FromResult(ProposalOf(PlanFixtures.Plan())) };
        var document = await OpenAsync(engine);
        document.RequestText = "Anadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        await ApproveAsync(document);

        await document.OnSetupAppliedAsync(new SetupApplyResult([], "a", "a", RosterChanged: false, Recorded: false, null));

        Assert.True(document.PlanDecision is { Approved: true });
    }

    /// <summary>«Aprobar plan»: with the builder on, once its analysis ended (approving fails closed while it is pending).</summary>
    private static async Task ApproveAsync(OrchestratorDocumentViewModel document)
    {
        if (document.Builder is { } builder)
        {
            await builder.Band.RefreshAsync();
        }

        await document.ApproveCommand.ExecuteAsync(null);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The condition was not met in time.");
    }
}
