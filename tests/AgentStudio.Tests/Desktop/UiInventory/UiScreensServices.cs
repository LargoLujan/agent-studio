using AgentStudio.Application.Coordination;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Views.Background;
using AgentStudio.Desktop.Views.Lock;
using AgentStudio.Desktop.Views.Memory;
using AgentStudio.Desktop.Views.Observability;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Lock;
using AgentStudio.Tests.Desktop.Memory;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;
using static AgentStudio.Tests.Desktop.Background.PanelSamples;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>The screens of memory, consumption, the lock and the background work (Phases 4 and 8), over synthetic data.</summary>
internal static class UiScreensServices
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(2));

    private static readonly ApprovalRequest Delivery = new(
        "deliver-integration", "Entregar la ejecución 20261021-abc123 en main.", RiskLevel.Critical, null, [@"src\Calc.cs"], "p-1", "20261021-abc123");


    private static MemoryEntry Entry(string id, MemoryKind kind, string topic, string content, MemoryOrigin origin, MemoryTrust trust, string source) =>
        new(id, kind, topic, content, origin, source, At, new string('b', 64), trust, null, null);

    private static async Task<StatusPanelViewModel> PanelAsync()
    {
        var clock = Clock();
        var (_, monitor) = await FollowAsync(clock, SynchronizationContext.Current);
        var activity = new FakeProjectActivity { Holding = { ["p-b"] = ProjectActivityKind.Audit } };
        var work = new BackgroundWorkRegistry(clock, activity, SynchronizationContext.Current, Timeout.InfiniteTimeSpan);
        work.TrackRun(RootA, monitor, () => ProjectA);
        work.TrackDebate(RootC, ProjectC, new FakeDebateRun(DebateSamples.Snapshot(turnsInFlight: 2, awaiting: new DebateAwaiting(DebateAwaitReason.ProviderLimit, "Límite de uso.", Until: T0.AddMinutes(25)))));
        work.TrackProject(RootB, () => ProjectB);
        var panel = Panel(work, clock, openApp: () => { });
        await panel.RefreshAsync();
        return panel;
    }

    private static ExitPromptViewModel ExitPrompt() => new(new ExitQuestion(
        [
            new ExitWorkLine("monorepo", "Ejecución 20261021-c4a1f2", "En ejecución · 4 intentos en curso", CanPause: true),
            new ExitWorkLine("docs-internas", "Debate deb-20261019-a1b2c3", "En deliberación · ronda 2 · 2 turnos en curso", CanPause: false),
            new ExitWorkLine("tienda", "Auditoría del proyecto", "En curso (aquí no hay más detalle)", CanPause: false),
        ],
        OffersKeepInTray: true));

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        UiScreens.ScreenAsync("Memory", async () =>
        {
            var store = new FakeProjectMemory();
            store.Entries.AddRange(
            [
                Entry("m-41", MemoryKind.Decision, "plan", "Plan aprobado: añadir Calc.Multiply con pruebas (3 tareas).", MemoryOrigin.System, MemoryTrust.Trusted, "run 20261018-c4a1f2 · entrada 12"),
                Entry("m-42", MemoryKind.PendingTask, "Calc", "Falta documentar Calc.Multiply en el README (openIssues del informe).", MemoryOrigin.Agent, MemoryTrust.Proposed, "evidencia ev-9 · sha256 3f2a…"),
                Entry("m-17", MemoryKind.KnownError, "pruebas", "CalcTests.Divide_by_zero falla en la verificación aislada.", MemoryOrigin.System, MemoryTrust.Quarantined, "run 20261001-a1b2c3 · entrada 30"),
                Entry("m-08", MemoryKind.Convention, "estilo", "Los mensajes de excepción van en inglés.", MemoryOrigin.User, MemoryTrust.Retracted, "nota del usuario"),
            ]);
            var summaries = new FakeDailySummaryService(day => new DailySummaryResult(day, true, new string('a', 64), "## Prompts\n- 12 de user\n\n## Resultados\n- 10 correctos, 2 fallidos\n\n## Hecho\n- «Hecho: panel de consumo»", 12, 2, null));
            var exporter = new FakeMemoryExporter(new MemoryExportResult(false, ["memory/cronologico-2026-10.md"], 0, "Hay una ejecución en curso en el proyecto: la memoria se exporta cuando termine."));
            var activity = new AgentStudio.Tests.Desktop.Memory.FakeRunActivity();
            var model = new MemoryViewModel(new FakeProjectWorkspace(), store, exporter, activity, new DailySummaryViewModel(summaries, new FakeClock(At)), new FakeClock(At));
            await model.SetProjectAsync(@"C:\proyectos\calc");
            await model.ExportCommand.ExecuteAsync(null);
            activity.Busy = true;
            model.RefreshRunActivity();
            await model.Summary.SummarizeCommand.ExecuteAsync(null);
            return UiScreens.Of(new MemoryView(), model);
        }, 1500, 900),
        UiScreens.ScreenAsync("Usage dashboard", async () =>
        {
            var clock = new FakeClock(At);
            var store = new RecordingSettingsStore(AppSettings.Default with { CodexQuotaReadEnabled = true });
            var observations = new FakeQuotaObservations
            {
                Latest =
                {
                    new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), null, At.AddHours(5), At.AddMinutes(-12), "claude rate_limit_event"),
                    new QuotaObservation(ProviderId.ClaudeCode, "seven_day", MetricValue<double>.Exact(0.12, "claude rate_limit_event"), null, At.AddDays(3), At.AddMinutes(-12), "claude rate_limit_event"),
                },
            };
            var codex = new FakeQuotaReader(() => new QuotaReadResult(
                QuotaReadStatus.Read,
                [new QuotaObservation(ProviderId.CodexCli, "codex/primary", MetricValue<double>.Exact(0.41, "codex account/rateLimits/read"), TimeSpan.FromHours(5), At.AddHours(2), At, "codex account/rateLimits/read") { Label = "Codex" }])
            {
                PlanType = "plus",
            });
            var model = new UsageDashboardViewModel(
                new FakeUsageReport(() => UsageSamples.Day1),
                new FakeUsageExporter(),
                new FakeSaveFilePicker(null),
                store,
                clock,
                new QuotaViewModel(observations, codex, clock),
                new ObservabilitySettingsViewModel(store));
            await model.SetProjectAsync(@"C:\proyectos\calc");
            await model.Quota.ReadCodexCommand.ExecuteAsync(null);
            return UiScreens.Of(new UsageDashboardView(), model);
        }, 1500, 1400),
        UiScreens.ScreenAsync("Lock screen", async () =>
        {
            var clock = new FakeTimeProvider(AgentStudio.Tests.Domain.TestData.T0);
            var hello = new FakeUserPresenceVerifier();
            var interfaceLock = new InterfaceLock(hello, new FakeWindowsSession(), clock);
            await interfaceLock.LockAsync(LockReason.Idle, CancellationToken.None);
            _ = interfaceLock.WaitUntilUnlockedAsync(Delivery, CancellationToken.None);
            hello.Availability = _ => Task.FromResult(UserPresenceAvailability.NotConfiguredForUser);
            await interfaceLock.UnlockWithHelloAsync(CancellationToken.None);
            var screen = new LockScreenViewModel(interfaceLock, clock, new InlineSynchronizationContext(), new Work());
            return UiScreens.Of(new LockScreenView(), screen);
        }, 1200, 760),
        UiScreens.Screen("Windows Hello notice", () => UiScreens.Of(new HelloNoticeView(), new ApprovalDialogViewModel(Delivery, ApprovalVerification.WindowsHelloUnavailable, UserPresenceAvailability.NotConfiguredForUser)), 560, 120),
        UiScreens.ScreenAsync("Status panel", async () =>
        {
            var panel = await PanelAsync();
            return UiScreens.Of(new StatusPanelView { ContentMaxHeight = 4000 }, panel);
        }, 540, 1950),
        UiScreens.ScreenAsync("Status panel window", async () => UiScreens.OfWindow(new StatusPanelWindow(), await PanelAsync()), 540, 1950),
        UiScreens.Screen("Exit question window", () => UiScreens.OfWindow(new ExitPromptWindow(), ExitPrompt()), 620, 450),
    ];

    private sealed class Work : IWorkInProgress
    {
        public IReadOnlyList<ProjectActivityKind> Current() => [ProjectActivityKind.Run, ProjectActivityKind.Debate];
    }
}
