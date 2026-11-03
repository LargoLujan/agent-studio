using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// The synthetic world of the status panel (docs/16 D-15): a run of «monorepo» with four tasks in course (context exact,
/// not exposed, estimated and not observed yet), one blocked by quota, one finished and an approval that waits; its header
/// with the roster and the budget; the usage of its finished attempts; the quota of the account; a debate of another
/// project and an audit of a third. Nothing real behind any of it.
/// </summary>
internal static class PanelSamples
{
    public const string Run = "20261021-c4a1f2";

    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    public static FakeTimeProvider Clock()
    {
        var clock = new FakeTimeProvider(T0);
        clock.SetLocalTimeZone(Zone);
        return clock;
    }

    public static ModelBinding Claude(string? model = null) => new(ProviderId.ClaudeCode, VendorId.Anthropic, model);

    public static ModelBinding Codex { get; } = new(ProviderId.CodexCli, VendorId.OpenAI);

    public static IReadOnlyList<AgentDefinition> Roster { get; } =
    [
        DebateSamples.Agent("orquestador", AgentRole.Orchestrator, Claude("claude-opus-5-5")),
        DebateSamples.Agent("ejecutor", AgentRole.Executor, Claude()),
        DebateSamples.Agent("ejecutor-2", AgentRole.Executor, Claude("claude-sonnet-5-5")),
        DebateSamples.Agent("documentador", AgentRole.Documenter, Claude("claude-sonnet-5-5")),
        DebateSamples.Agent("juez-revision", AgentRole.Judge, Codex),
    ];

    public static RunRecord Header { get; } = new(
        new RunId(Run), "p-a", T0.AddMinutes(-40), "plan-hash", "req-hash", new EvidenceId("ev-peticion"), "abc1234", "main",
        Backends.Profile, Roster, new RunOptions(MaxAgentRuns: 12), null);

    public static FakeRun RunInCourse() => new(Snapshot(
        Run,
        RunPhase.Running,
        5,
        TaskOf("T1", WorkTaskStatus.Running, "ejecutor") with { Title = "Implementar Calc.Multiply" },
        TaskOf("T2", WorkTaskStatus.Running, "juez-revision", kind: WorkTaskKind.Review) with { Title = "Revisión independiente" },
        TaskOf("T3", WorkTaskStatus.Blocked, "documentador", blockReason: "RateLimitedUntil(2026-10-21T12:05:00.0000000+00:00)") with { Title = "Documentar la API" },
        TaskOf("T4", WorkTaskStatus.Completed, "ejecutor") with { Title = "Preparar el esqueleto" },
        TaskOf("T5", WorkTaskStatus.AwaitingApproval, kind: WorkTaskKind.HumanApproval) with { Title = "Aprobar la entrega" },
        TaskOf("T6", WorkTaskStatus.Running, "ejecutor-2") with { Title = "Refactorizar el parser" },
        TaskOf("T7", WorkTaskStatus.Running, "ejecutor-2") with { Title = "Añadir pruebas" }));

    /// <summary>What the agents of the run reported while it went: the context of three tasks and the approval of the delivery.</summary>
    public static OrchestrationEvent[] Reported { get; } =
    [
        Context("T1", MetricValue<long>.Exact(470_000, "claude assistant.usage"), MetricValue<long>.Exact(1_000_000, "claude modelUsage.contextWindow")),
        Context("T2", MetricValue<long>.NotExposed("codex exec no informa del contexto"), MetricValue<long>.NotExposed("codex exec no informa del contexto"), model: null),
        Context("T6", MetricValue<long>.Exact(180_000, "claude assistant.usage"), MetricValue<long>.EstimatedBySystem(200_000, "ventana vista en una ejecución anterior")),
        new ApprovalPendingEvent(T0, new TaskId("T5"), new ApprovalRequest("delivery.ff", "Entregar a main", RiskLevel.Critical, null, [], "p-a", Run)),
    ];

    private static AgentActivityEvent Context(string task, MetricValue<long> used, MetricValue<long> window, string? model = "claude-sonnet-5-5") =>
        new(T0, new TaskId(task), new ContextObserved(T0, model, used, window));

    /// <summary>The usage of the four attempts of the run that finished, as the projection of «Consumo» gives it.</summary>
    public static UsageReport Usage { get; } = new(
        [UsageSamples.ClaudeAttempt with { RunId = Run, Agent = new AgentId("ejecutor") }],
        [new UsageGroup([], UsageSamples.ClaudeTotals with { Attempts = 4 })],
        [],
        new UsageReconciliation(null, 4, []));

    public static FakeQuotaObservations Quota()
    {
        var record = new FakeQuotaObservations();
        record.Latest.Add(new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), null, T0.AddHours(2).AddMinutes(5), T0.AddMinutes(-12), "rate_limit_event"));
        record.Latest.Add(new QuotaObservation(ProviderId.ClaudeCode, "seven_day", MetricValue<double>.Exact(0.18, "claude rate_limit_event"), null, T0.AddDays(3), T0.AddMinutes(-12), "rate_limit_event"));
        return record;
    }

    /// <summary>A monitor that followed the run and applied what its agents reported.</summary>
    public static async Task<(FakeRun Run, RunMonitorViewModel Monitor)> FollowAsync(TimeProvider clock, SynchronizationContext? ui = null)
    {
        var run = RunInCourse();
        var monitor = new RunMonitorViewModel(clock, ui ?? new InlineSynchronizationContext());
        monitor.Attach(run, ProjectA);
        run.Emit(Reported);
        await WaitUntilAsync(
            () => monitor.Tasks.Count(t => t.LastContext is not null) == 3 && monitor.Tasks.Any(t => t.PendingApproval is not null),
            "The monitor did not apply what the agents reported.");
        return (run, monitor);
    }

    /// <summary>The panel over the run, with every source it reads.</summary>
    public static StatusPanelViewModel Panel(BackgroundWorkRegistry work, TimeProvider clock, Action? openApp = null) =>
        new(work, clock, new StatusPanelSources(new FakeUsageReport(() => Usage), new FakeRunRegistry(Header), new QuotaViewModel(Quota(), null, clock)), openApp);
}
