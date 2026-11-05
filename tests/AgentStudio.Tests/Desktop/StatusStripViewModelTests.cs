using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Context;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The bars of the status bar of the main window (docs/18 P3): the context of the tasks in course (or the fullest of several,
/// with how many), the quota of the account (Claude's 5 hours and 7 days; Codex only with the opt-in), the budget of the
/// runs, the detail in the tooltip, built at most every 250 ms whatever the burst, and nothing at all while locked.
/// </summary>
public sealed partial class StatusStripViewModelTests
{
    private static readonly DateTimeOffset Now = T0;

    private static FakeQuotaObservations Observations() => new()
    {
        Latest =
        {
            new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), null, Now.AddHours(2), Now.AddMinutes(-12), "rate_limit_event"),
            new QuotaObservation(ProviderId.ClaudeCode, "seven_day", MetricValue<double>.Exact(0.18, "claude rate_limit_event"), null, Now.AddDays(3), Now.AddMinutes(-12), "rate_limit_event"),
        },
    };

    private static ContextObserved Step(long tokens, string model = "claude-sonnet-5-5") =>
        new(Now, model, MetricValue<long>.Exact(tokens, "claude assistant.usage"), MetricValue<long>.Missing("llega con el resultado"));

    /// <summary>A monitor following a run whose given tasks are running, with what their agents reported applied.</summary>
    private static async Task<RunMonitorViewModel> RunningAsync(FakeTimeProvider clock, int used, int? max, params (string Task, long? Tokens)[] tasks)
    {
        var monitor = new RunMonitorViewModel(clock, new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, [.. tasks.Select(t => FakeRun.View(t.Task, status: WorkTaskStatus.Running))]) with { AgentRunsUsed = used, MaxAgentRuns = max });
        monitor.Attach(run);
        run.Emit([.. tasks.Where(t => t.Tokens is not null).Select(t => (OrchestrationEvent)new AgentActivityEvent(Now, new(t.Task), Step(t.Tokens!.Value)))]);
        for (var i = 0; i < 400 && monitor.Tasks.Count(t => t.LastContext is not null) < tasks.Count(t => t.Tokens is not null); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return monitor;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The monitor did not apply the events in time.");
    }

    [Fact]
    public async Task Without_work_the_context_says_there_are_no_tasks_and_the_quota_stays_in_view()
    {
        var clock = new FakeTimeProvider(Now);
        var quota = new QuotaViewModel(Observations(), null, clock);
        await quota.RefreshAsync();

        var strip = new StatusStripViewModel(quota, clock, null);

        Assert.Equal("sin tareas en curso", strip.Context.ValueText);
        Assert.False(strip.Context.HasValue);
        Assert.Equal(["Claude 5 h", "Claude 7 días"], strip.QuotaBars.Select(b => b.Label));
        Assert.Equal(["47 %", "18 %"], strip.QuotaBars.Select(b => b.ValueText));
        Assert.Null(strip.Budget);
        Assert.Equal(["Contexto", "Claude 5 h", "Claude 7 días"], strip.Bars.Select(b => b.Label));
        Assert.Contains("Contexto: sin tareas en curso.", strip.ToolTip, StringComparison.Ordinal);
        Assert.Contains("Claude 5 h: 47 % · exacto · informado por el CLI · vista hace 12 min", strip.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_shows_only_with_the_opt_in()
    {
        var clock = new FakeTimeProvider(Now);
        var read = new QuotaReadResult(QuotaReadStatus.Read, [new QuotaObservation(ProviderId.CodexCli, "codex/primary", MetricValue<double>.Exact(0.41, "codex account/rateLimits/read"), TimeSpan.FromHours(5), null, Now, "app-server") { Label = "Codex" }]);
        var quota = new QuotaViewModel(Observations(), new FakeQuotaReader(() => read), clock);
        await quota.RefreshAsync();
        var strip = new StatusStripViewModel(quota, clock, null);

        Assert.DoesNotContain(strip.QuotaBars, b => b.Label.StartsWith("Codex", StringComparison.Ordinal));

        quota.CodexOptedIn = true;
        Assert.Equal("sin leer", Assert.Single(strip.QuotaBars, b => b.Label == "Codex").ValueText);

        await quota.ReadCodexCommand.ExecuteAsync(null);
        var codex = Assert.Single(strip.QuotaBars, b => b.Label.StartsWith("Codex", StringComparison.Ordinal));
        Assert.Equal("Codex 5 h", codex.Label);
        Assert.Equal("41 %", codex.ValueText);
    }

    [Fact]
    public async Task One_task_in_course_shows_its_live_context_and_the_budget_of_its_run()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, null);
        var monitor = await RunningAsync(clock, used: 3, max: 12, ("T1", 470_000));

        strip.Track(monitor);

        Assert.Equal("Contexto · T1", strip.Context.Label);
        Assert.Equal("~47 %", strip.Context.ValueText);
        Assert.True(strip.Context.IsEstimate);
        Assert.Equal("3 de 12", strip.Budget!.ValueText);
        Assert.Equal(["Contexto · T1", "Cuota", "Presupuesto"], strip.Bars.Select(b => b.Label));
        Assert.Contains("Presupuesto de la ejecución", strip.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_several_tasks_the_fullest_is_shown_with_how_many_are_in_course()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, null);
        var monitor = await RunningAsync(clock, used: 2, max: 12, ("T1", 100_000), ("T2", 870_000), ("T3", null));

        strip.Track(monitor);

        Assert.Equal("Contexto · T2, la más llena de 3", strip.Context.Label);
        Assert.Equal("~87 %", strip.Context.ValueText);
        Assert.Equal("compresión recomendada", strip.Context.LevelText);
        Assert.Contains("Contexto de T3", strip.ToolTip, StringComparison.Ordinal);
        Assert.Contains("sin observaciones todavía", strip.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_burst_of_steps_is_built_once_every_quarter_of_a_second_and_nothing_waits_for_it()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, new InlineSynchronizationContext());
        var monitor = new RunMonitorViewModel(clock, new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running)) with { AgentRunsUsed = 1, MaxAgentRuns = 12 });
        monitor.Attach(run);
        run.Emit(new AgentActivityEvent(Now, new("T1"), Step(100_000)));
        await WaitAsync(() => monitor.Tasks[0].LastContext?.PromptTokens.Value == 100_000);
        strip.Track(monitor);
        strip.Refresh();
        var builds = 0;
        strip.PropertyChanged += (_, e) => builds += e.PropertyName == nameof(StatusStripViewModel.Context) ? 1 : 0;

        // 100 steps inside the same quarter of a second: no build until it ends, and then one with the last step.
        run.Emit([.. Enumerable.Range(1, 100).Select(i => (OrchestrationEvent)new AgentActivityEvent(Now, new("T1"), Step(100_000 + (i * 1_000))))]);
        await WaitAsync(() => monitor.Tasks[0].LastContext?.PromptTokens.Value == 200_000);

        Assert.Equal(0, builds);
        Assert.Equal("~10 %", strip.Context.ValueText);

        clock.Advance(StatusStripViewModel.RefreshInterval);

        Assert.Equal(1, builds);
        Assert.Equal("~20 %", strip.Context.ValueText);
    }

    [Fact]
    public async Task Locked_the_strip_shows_and_names_nothing_and_unlocked_it_comes_back()
    {
        var clock = new FakeTimeProvider(Now);
        var quota = new QuotaViewModel(Observations(), null, clock);
        await quota.RefreshAsync();
        var strip = new StatusStripViewModel(quota, clock, null);
        strip.Track(await RunningAsync(clock, used: 3, max: 12, ("T1", 470_000)));

        strip.IsLocked = true;

        Assert.False(strip.ShowsBars);
        Assert.Empty(strip.Bars);
        Assert.Empty(strip.QuotaBars);
        Assert.Null(strip.Budget);
        Assert.Null(strip.ToolTip);
        Assert.DoesNotContain("T1", strip.Context.AccessibleName, StringComparison.Ordinal);

        strip.IsLocked = false;

        Assert.True(strip.ShowsBars);
        Assert.Equal("Contexto · T1", strip.Context.Label);
        Assert.Equal(4, strip.Bars.Count);
    }

    [Fact]
    public async Task A_quota_announced_in_a_run_is_read_again()
    {
        var clock = new FakeTimeProvider(Now);
        var observations = new FakeQuotaObservations();
        var quota = new QuotaViewModel(observations, null, clock);
        var strip = new StatusStripViewModel(quota, clock, null);
        var monitor = new RunMonitorViewModel(clock, new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running)));
        monitor.Attach(run);
        strip.Track(monitor);
        Assert.Equal("sin dato", strip.QuotaBars[0].ValueText);

        observations.Latest.Add(new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.52, "claude rate_limit_event"), null, null, Now, "rate_limit_event"));
        Assert.Equal("sin dato", strip.QuotaBars[0].ValueText);
        run.Emit(new AgentActivityEvent(Now, new("T1"), new QuotaReported(Now, [new QuotaWindow("five_hour", MetricValue<double>.Exact(0.52, "claude rate_limit_event"), null)])));
        for (var i = 0; i < 400 && strip.QuotaBars[0].ValueText != "52 %"; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal("52 %", strip.QuotaBars[0].ValueText);
    }

    [Fact]
    public void The_shell_makes_one_and_follows_the_monitor_of_each_orchestrator()
    {
        var main = ShellFactory.CreateMain();

        Assert.NotNull(main.StatusStrip);
        Assert.Equal("sin tareas en curso", main.StatusStrip.Context.ValueText);
        Assert.Equal(0, main.StatusStrip.TrackedMonitors);
    }

    /// <summary>
    /// V33, seen live: while the orchestrator planned, its own bar was live and the status bar said «Contexto: sin tareas en
    /// curso». The context of what runs is always in view (P3): the planning counts as one more in course, and once it ended
    /// the bar goes back to «sin tareas en curso».
    /// </summary>
    [Fact]
    public void While_the_orchestrator_plans_the_status_bar_shows_its_context()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, null);
        var planner = new FakePlanner();
        strip.TrackPlanning(planner);

        Assert.Equal("sin tareas en curso", strip.Context.ValueText);

        planner.Plan(MetricBars.NoObservation("Contexto del orquestador"));
        Assert.Equal("Contexto · orquestador", strip.Context.Label);
        Assert.Equal("sin observaciones todavía", strip.Context.ValueText);

        planner.PlanningContext = Context(240_000);
        Assert.Equal("Contexto · orquestador", strip.Context.Label);
        Assert.Equal("~24 %", strip.Context.ValueText);
        Assert.Contains("Contexto del orquestador, que planifica: ~24 %", strip.ToolTip, StringComparison.Ordinal);

        // Locked, nothing of the planning is named.
        strip.IsLocked = true;
        Assert.DoesNotContain("orquestador", strip.Context.AccessibleName, StringComparison.Ordinal);
        Assert.Null(strip.ToolTip);
        strip.IsLocked = false;

        // The planning ended: its bar stays in its tab, and the status bar has nothing in course.
        planner.IsPlanningInProgress = false;
        Assert.Equal("Contexto", strip.Context.Label);
        Assert.Equal("sin tareas en curso", strip.Context.ValueText);
    }

    [Fact]
    public async Task With_a_run_and_a_planning_at_once_the_fullest_context_is_shown()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, null);
        strip.Track(await RunningAsync(clock, used: 1, max: 12, ("T1", 100_000)));
        var planner = new FakePlanner();
        strip.TrackPlanning(planner);

        planner.Plan(Context(470_000));
        Assert.Equal("Contexto · orquestador, la más llena de 2", strip.Context.Label);
        Assert.Equal("~47 %", strip.Context.ValueText);

        planner.PlanningContext = Context(50_000);
        Assert.Equal("Contexto · T1, la más llena de 2", strip.Context.Label);
        Assert.Equal("~10 %", strip.Context.ValueText);
    }

    [Fact]
    public void The_bars_of_a_planning_are_built_once_every_quarter_of_a_second()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, new InlineSynchronizationContext());
        var planner = new FakePlanner();
        strip.TrackPlanning(planner);
        planner.Plan(Context(100_000));
        strip.Refresh();
        var builds = 0;
        strip.PropertyChanged += (_, e) => builds += e.PropertyName == nameof(StatusStripViewModel.Context) ? 1 : 0;

        for (var i = 1; i <= 50; i++)
        {
            planner.PlanningContext = Context(100_000 + (i * 2_000));
        }

        Assert.Equal(0, builds);
        Assert.Equal("~10 %", strip.Context.ValueText);

        clock.Advance(StatusStripViewModel.RefreshInterval);

        Assert.Equal(1, builds);
        Assert.Equal("~20 %", strip.Context.ValueText);
    }

    /// <summary>The real «Orquestador» document drives the status bar while its orchestrator agent plans (a fake engine).</summary>
    [Fact]
    public async Task The_planning_of_an_orchestrator_document_reaches_the_status_bar()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, null);
        var seen = new List<string>();
        var engine = new FakeOrchestrator
        {
            Plan = (_, progress, _) =>
            {
                seen.Add($"{strip.Context.Label}: {strip.Context.ValueText}");
                progress?.Report(new ContextObserved(Now, "claude-opus-5-5", MetricValue<long>.Exact(120_000, "claude assistant.usage"), MetricValue<long>.Missing("llega con el resultado")));
                seen.Add($"{strip.Context.Label}: {strip.Context.ValueText}");
                return Task.FromResult(new PlanProposal(new RunId("20261103-abc123"), AgentStudio.Tests.Domain.Orchestration.PlanFixtures.Plan(), [], Corrected: false, AgentRunStatus.Succeeded, null, null));
            },
        };
        var document = new AgentStudio.Desktop.Orchestrator.OrchestratorDocumentViewModel(
            @"C:\proyectos\calc", new AgentStudio.Desktop.Orchestrator.Session.OrchestrationSession(Backends.Complete(engine)), new NoLayout(), clock, new InlineSynchronizationContext());
        await document.OpenAsync();
        strip.TrackPlanning(document);
        document.RequestText = "Añadir Calc.Divide";

        await document.RequestPlanCommand.ExecuteAsync(null);

        Assert.Equal(["Contexto · orquestador: sin observaciones todavía", "Contexto · orquestador: ~12 %"], seen);
        Assert.False(document.IsPlanningInProgress);
        Assert.Equal("sin tareas en curso", strip.Context.ValueText);
    }

    /// <summary>
    /// The review of V33: the strip never let go of the plannings and the monitors of the tabs that closed. It lets go of them
    /// at once when nothing runs in them, or once what runs ends (a planning or a run goes on when its tab closes, and its
    /// context stays in view meanwhile); a tab opened again is followed again.
    /// </summary>
    [Fact]
    public async Task The_planning_and_the_run_of_a_closed_tab_are_let_go_once_nothing_runs_in_them()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, null);
        var idle = new FakePlanner();
        var planning = new FakePlanner();
        strip.TrackPlanning(idle);
        strip.TrackPlanning(planning);
        planning.Plan(Context(240_000));

        strip.ReleasePlanning(idle);
        strip.ReleasePlanning(planning);
        Assert.Equal(1, strip.TrackedPlanners);
        Assert.Equal("Contexto · orquestador", strip.Context.Label);

        planning.IsPlanningInProgress = false;
        Assert.Equal(0, strip.TrackedPlanners);
        Assert.Equal("sin tareas en curso", strip.Context.ValueText);

        strip.TrackPlanning(planning);
        Assert.Equal(1, strip.TrackedPlanners);

        // A run: kept while it is active after its tab closed, let go once it ends.
        var monitor = new RunMonitorViewModel(clock, new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running)));
        monitor.Attach(run);
        strip.Track(monitor);
        strip.Release(monitor);
        Assert.Equal(1, strip.TrackedMonitors);

        run.Finish(new RunResult(new("20261103-abc123"), RunPhase.Succeeded, FakeRun.Healthy, null, null, "Terminó bien."));
        await monitor.Listening;
        Assert.Equal(0, strip.TrackedMonitors);

        strip.Track(monitor);
        strip.Release(monitor);
        Assert.Equal(0, strip.TrackedMonitors);
    }

    /// <summary>In the shell: closing the «Orquestador» tab lets go of its planning and its idle monitor; opening it again follows them again.</summary>
    [Fact]
    public async Task Closing_the_orchestrator_tab_lets_go_of_it_in_the_status_bar_and_opening_it_follows_it_again()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(Now);
        var main = new MainViewModel(
            new InMemorySettingsStore(),
            new FakeFolderPicker(),
            new FakeUnsavedChangesPrompt(),
            new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), clock),
            new ExplorerViewModel(),
            new JournalViewModel(clock),
            new QuickOpenViewModel(),
            orchestrators: new Orchestrators(clock));
        await main.OpenProjectAsync(temp.CreateDirectory("calc"));
        await main.OpenOrchestratorCommand.ExecuteAsync(null);
        var orchestrator = Assert.Single(main.Documents.OfType<OrchestratorDocumentViewModel>());
        Assert.Equal((1, 1), (main.StatusStrip.TrackedMonitors, main.StatusStrip.TrackedPlanners));

        Assert.True(await main.CloseDocumentAsync(orchestrator));
        Assert.Equal((0, 0), (main.StatusStrip.TrackedMonitors, main.StatusStrip.TrackedPlanners));

        await main.OpenOrchestratorCommand.ExecuteAsync(null);
        Assert.Same(orchestrator, Assert.Single(main.Documents.OfType<OrchestratorDocumentViewModel>()));
        Assert.Equal((1, 1), (main.StatusStrip.TrackedMonitors, main.StatusStrip.TrackedPlanners));
    }

    /// <summary>
    /// The re-review: a run that resumes in the monitor of a closed tab is followed again, and let go once it ends while its
    /// tab stays closed (it was kept forever).
    /// </summary>
    [Fact]
    public async Task A_run_resumed_in_a_closed_tab_is_followed_and_let_go_once_it_ends()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(Now);
        var main = new MainViewModel(
            new InMemorySettingsStore(),
            new FakeFolderPicker(),
            new FakeUnsavedChangesPrompt(),
            new ProviderDiagnosticsViewModel(new AgentStudio.Application.Diagnostics.ProviderDiagnostics([]), clock),
            new ExplorerViewModel(),
            new JournalViewModel(clock),
            new QuickOpenViewModel(),
            orchestrators: new Orchestrators(clock));
        await main.OpenProjectAsync(temp.CreateDirectory("calc"));
        await main.OpenOrchestratorCommand.ExecuteAsync(null);
        var orchestrator = Assert.Single(main.Documents.OfType<OrchestratorDocumentViewModel>());
        Assert.True(await main.CloseDocumentAsync(orchestrator));
        Assert.Equal(0, main.StatusStrip.TrackedMonitors);

        // A run resumes in its monitor (by its consent), with the tab closed.
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1", status: WorkTaskStatus.Running)));
        orchestrator.Monitor.Attach(run);
        Assert.True(orchestrator.Monitor.IsActive);
        Assert.Equal(1, main.StatusStrip.TrackedMonitors);

        run.Finish(new RunResult(new("20261103-abc123"), RunPhase.Succeeded, FakeRun.Healthy, null, null, "Terminó bien."));
        await orchestrator.Monitor.Listening;
        Assert.Equal(0, main.StatusStrip.TrackedMonitors);
    }

    /// <summary>
    /// The re-review: a monitor that starts again (it attaches, inspects or lets go of a run) says Reset to its tasks, and the
    /// strip stopped watching the tasks of the other monitors: their bars froze.
    /// </summary>
    [Fact]
    public async Task A_monitor_that_starts_again_keeps_the_tasks_of_the_others_watched()
    {
        var clock = new FakeTimeProvider(Now);
        var strip = new StatusStripViewModel(null, clock, null);
        var idle = new RunMonitorViewModel(clock, new InlineSynchronizationContext());
        var other = new RunMonitorViewModel(clock, new InlineSynchronizationContext());
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T9", status: WorkTaskStatus.Running)) with { AgentRunsUsed = 1, MaxAgentRuns = 12 });
        other.Attach(run);
        run.Emit(new AgentActivityEvent(Now, new("T9"), Step(100_000)));
        await WaitAsync(() => other.Tasks[0].LastContext?.PromptTokens.Value == 100_000);
        strip.Track(idle);
        strip.Track(other);
        Assert.Equal("~10 %", strip.Context.ValueText);

        // The first monitor takes a run: its tasks start again (Reset).
        idle.Attach(new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, FakeRun.View("T1"))));

        run.Emit(new AgentActivityEvent(Now, new("T9"), Step(870_000)));

        // Its bar follows by itself (it froze at ~10 % before): the strip still watches the task of the other monitor.
        await WaitAsync(() => strip.Context.ValueText == "~87 %");
    }

    private sealed class Orchestrators(TimeProvider time) : AgentStudio.Desktop.Orchestrator.IOrchestratorDocumentFactory
    {
        public OrchestratorDocumentViewModel Create(string projectRoot) =>
            new(projectRoot, new AgentStudio.Desktop.Orchestrator.Session.OrchestrationSession(AgentStudio.Desktop.Orchestrator.Session.OrchestrationBackend.None), new NoLayout(), time, new InlineSynchronizationContext());
    }

    private static MetricBarReading Context(long tokens) =>
        MetricBars.Context("Contexto del orquestador", "claude-opus-5-5", MetricValue<long>.Exact(tokens, "claude assistant.usage"), ModelWindowTable.Current.WindowFor("claude-opus-5-5"), new ContextHealthThresholds());

    /// <summary>An orchestrator that plans when the test says so, with the reading the test gives.</summary>
    private sealed partial class FakePlanner : ObservableObject, IPlanningContext
    {
        [ObservableProperty]
        private MetricBarReading? _planningContext;

        [ObservableProperty]
        private bool _isPlanningInProgress;

        public void Plan(MetricBarReading reading)
        {
            PlanningContext = reading;
            IsPlanningInProgress = true;
        }
    }

    private sealed class NoLayout : AgentStudio.Desktop.Orchestrator.Canvas.ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public AgentStudio.Desktop.Orchestrator.Canvas.CanvasLayout Load(string projectRoot) => AgentStudio.Desktop.Orchestrator.Canvas.CanvasLayout.Empty;

        public void Save(string projectRoot, AgentStudio.Desktop.Orchestrator.Canvas.CanvasLayout layout)
        {
        }
    }
}
