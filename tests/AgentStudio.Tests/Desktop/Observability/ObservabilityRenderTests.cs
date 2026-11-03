using System.Windows.Media;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Views.Observability;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The Phase 4 observability views drawn without ever being shown (<see cref="OffscreenRender"/>): measured, arranged and
/// rendered into a bitmap on the WPF test thread; no window and no HWND. They prove that the templates, the theme and the
/// bindings produce a picture; with <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the pictures are saved for docs/evidencias.
/// </summary>
public sealed class ObservabilityRenderTests
{
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Raised = Color.FromRgb(0x16, 0x22, 0x3D);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task The_usage_panel_renders_offscreen_with_its_figures_and_the_quota_and_its_settings_are_in_ajustes()
    {
        var dashboard = await WpfHost.RunAsync(async () =>
        {
            var clock = new FakeClock(Now);
            var store = new RecordingSettingsStore(AppSettings.Default with { CodexQuotaReadEnabled = true });
            var observations = new FakeQuotaObservations
            {
                Latest =
                {
                    new QuotaObservation(ProviderId.ClaudeCode, "five_hour", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), null, Now.AddHours(5), Now.AddMinutes(-12), "claude rate_limit_event"),
                    new QuotaObservation(ProviderId.ClaudeCode, "seven_day", MetricValue<double>.Exact(0.12, "claude rate_limit_event"), null, Now.AddDays(3), Now.AddMinutes(-12), "claude rate_limit_event"),
                },
            };
            var codex = new FakeQuotaReader(() => new QuotaReadResult(
                QuotaReadStatus.Read,
                [new QuotaObservation(ProviderId.CodexCli, "codex/primary", MetricValue<double>.Exact(0.41, "codex account/rateLimits/read"), TimeSpan.FromHours(5), Now.AddHours(2), Now, "codex account/rateLimits/read") { Label = "Codex" }])
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
            return model;
        });

        var (drawn, cards, warning, fields) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new UsageDashboardView { DataContext = dashboard };

            var bitmap = OffscreenRender.Render(view, 1500, 1180);
            OffscreenRender.SaveIfRequested(bitmap, "fase4-consumo.png");
            // The text box inside the template of a combo box (its editable part, hidden) is not a field of the panel.
            var fieldsOfThePanel = Descendants<System.Windows.Controls.TextBox>(view).Count(t => t.TemplatedParent is not System.Windows.Controls.ComboBox);
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Raised), OffscreenRender.Count(bitmap, Warning), fieldsOfThePanel);
        });

        // The thresholds and the Codex opt-in are not edited here any more: «Ajustes» has them, and the panel says so.
        Assert.Equal(0, fields);
        Assert.Contains("«Ajustes»", UsageDashboardViewModel.SettingsNote, StringComparison.Ordinal);
        Assert.Equal(2, dashboard.Groups.Count);
        Assert.Equal("Leída del app-server de Codex (account/rateLimits/read), sin turno del modelo.", dashboard.Quota.Codex.Status);
        Assert.True(drawn > 60_000, $"Only {drawn} pixels differ from the panel: the report was not drawn.");
        Assert.True(cards > 20_000, $"Only {cards} pixels have the card colour: the groups and the quota were not drawn.");
        Assert.True(warning > 50, $"Only {warning} pixels have the warning colour (the project left out).");
    }

    [Fact]
    public void The_monitor_renders_offscreen_with_the_context_of_each_task()
    {
        var (drawn, success, warning, monitor) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var run = new FakeRun(new RunSnapshot(
                new RunId("20261018-c4a1f2"),
                RunPhase.Running,
                [
                    new TaskView(new TaskId("T1"), "Implementar Calc.Multiply", WorkTaskKind.AgentWork, WorkTaskStatus.Running, new AgentId("ejecutor"), 1, 0, null, null, null, []),
                    new TaskView(new TaskId("T2"), "Documentar la API", WorkTaskKind.AgentWork, WorkTaskStatus.Running, new AgentId("ejecutor"), 1, 0, null, null, null, []),
                    new TaskView(new TaskId("T3"), "Revisión independiente", WorkTaskKind.Review, WorkTaskStatus.Running, new AgentId("juez"), 1, 0, null, null, null, []),
                    new TaskView(new TaskId("T4"), "Integrar ramas", WorkTaskKind.Integration, WorkTaskStatus.Pending, null, 1, 0, null, null, null, []),
                ],
                FakeRun.Healthy,
                AgentRunsUsed: 3));
            run.Emit(
                new AgentActivityEvent(T0, new TaskId("T1"), new ContextObserved(T0, "claude-sonnet-5-5", MetricValue<long>.Exact(30_822, "claude assistant.usage"), MetricValue<long>.Exact(1_000_000, "claude modelUsage"))),
                new AgentActivityEvent(T0, new TaskId("T2"), new ContextObserved(T0, "claude-sonnet-5-5", MetricValue<long>.Exact(180_000, "claude assistant.usage"), MetricValue<long>.EstimatedBySystem(200_000, "window seen in an earlier run"))),
                new AgentActivityEvent(T0, new TaskId("T2"), new CompactionObserved(T0, "auto", MetricValue<long>.Exact(185_000, "claude compact_boundary.pre_tokens"))),
                new AgentActivityEvent(T0, new TaskId("T3"), new ContextObserved(T0, null, MetricValue<long>.NotExposed("codex exec"), MetricValue<long>.NotExposed("codex exec"))));
            var model = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
            model.Attach(run); // the events are buffered: read at once, on this thread
            var view = new RunMonitorView { DataContext = model };

            var bitmap = OffscreenRender.Render(view, 1440, 440);
            OffscreenRender.SaveIfRequested(bitmap, "fase4-monitor-contexto.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Success), OffscreenRender.Count(bitmap, Warning), model);
        });

        Assert.Equal("Contexto: 3 % · exacto · informado por el CLI · 30.822 de 1.000.000 tokens · claude-sonnet-5-5 · sano", monitor.Tasks[0].Context);
        Assert.StartsWith("Contexto: ~90 % · estimado por Agent Studio", monitor.Tasks[1].Context, StringComparison.Ordinal);
        Assert.Equal("Contexto: no expuesto", monitor.Tasks[2].Context);
        Assert.Null(monitor.Tasks[3].Context);
        Assert.True(drawn > 30_000, $"Only {drawn} pixels differ from the panel.");
        Assert.True(success > 20, $"Only {success} pixels have the success colour (the healthy context dot).");
        Assert.True(warning > 20, $"Only {warning} pixels have the warning colour (the context dot near its limit).");
    }

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
