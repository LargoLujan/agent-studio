using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// The bars of the status bar of the main window (docs/18 P3): the context of the tasks in course (or the fullest of several,
/// with how many), the quota of the account (Claude's 5 hours and 7 days; Codex only with the opt-in), the budget of the
/// runs, the detail in the tooltip, built at most every 250 ms whatever the burst, and nothing at all while locked.
/// </summary>
public sealed class StatusStripViewModelTests
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
}
