using System.Windows.Media;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Persistence;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Views.Memory;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Agents;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// The memory screen and the «Memoria del proyecto» switches of the preparation, drawn without ever being shown
/// (<see cref="OffscreenRender"/>); with <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set, the pictures are saved for docs/evidencias.
/// </summary>
public sealed class MemoryRenderTests
{
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Raised = Color.FromRgb(0x16, 0x22, 0x3D);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);

    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(2));

    private static MemoryEntry Entry(string id, MemoryKind kind, string topic, string content, MemoryOrigin origin, MemoryTrust trust, string source) =>
        new(id, kind, topic, content, origin, source, At, new string('b', 64), trust, null, null);

    [Fact]
    public async Task The_memory_document_renders_offscreen_with_every_trust_and_the_daily_summary()
    {
        var memory = await WpfHost.RunAsync(async () =>
        {
            var store = new FakeProjectMemory();
            store.Entries.AddRange(
            [
                Entry("m-41", MemoryKind.Decision, "plan", "Plan aprobado: añadir Calc.Multiply con pruebas (3 tareas).", MemoryOrigin.System, MemoryTrust.Trusted, "run 20261018-c4a1f2 · entrada 12"),
                Entry("m-42", MemoryKind.PendingTask, "Calc", "Falta documentar Calc.Multiply en el README (openIssues del informe).", MemoryOrigin.Agent, MemoryTrust.Proposed, "evidencia ev-9 · sha256 3f2a…"),
                Entry("m-17", MemoryKind.KnownError, "pruebas", "CalcTests.Divide_by_zero falla en la verificación aislada.", MemoryOrigin.System, MemoryTrust.Quarantined, "run 20261001-a1b2c3 · entrada 30"),
                Entry("m-08", MemoryKind.Convention, "estilo", "Los mensajes de excepción van en inglés.", MemoryOrigin.User, MemoryTrust.Retracted, "nota del usuario"),
            ]);
            var summaries = new FakeDailySummaryService(day => new DailySummaryResult(
                day,
                true,
                new string('a', 64),
                "## Prompts\n- 12 de user (claude-code/sonnet: 9, codex-cli: 3)\n\n## Resultados\n- 10 correctos, 2 fallidos · 41 min\n\n## Hecho\n- «Hecho: panel de consumo con su procedencia»",
                12,
                2,
                null));
            // A run started while the export was writing: the exporter stopped after the first file, and the run goes on.
            var exporter = new FakeMemoryExporter(new MemoryExportResult(false, ["memory/cronologico-2026-10.md"], 0, "Hay una ejecución, una planificación, un debate o una auditoría en curso en el proyecto: la memoria se exporta cuando termine."));
            var activity = new FakeRunActivity();
            var model = new MemoryViewModel(new FakeProjectWorkspace(), store, exporter, activity, new DailySummaryViewModel(summaries, new FakeClock(At)), new FakeClock(At));
            await model.SetProjectAsync(@"C:\proyectos\calc");
            await model.ExportCommand.ExecuteAsync(null);
            activity.Busy = true;
            model.RefreshRunActivity();
            await model.Summary.SummarizeCommand.ExecuteAsync(null);
            return model;
        });

        var (drawn, cards, warning, success) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new MemoryView { DataContext = memory };

            var bitmap = OffscreenRender.Render(view, 1500, 900);
            OffscreenRender.SaveIfRequested(bitmap, "fase4-memoria.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Raised), OffscreenRender.Count(bitmap, Warning), OffscreenRender.Count(bitmap, Success));
        });

        Assert.Equal(4, memory.Entries.Count);
        Assert.NotNull(memory.ExportBlocked);
        Assert.StartsWith("Exportación incompleta:", memory.ExportWarning, StringComparison.Ordinal);
        Assert.True(drawn > 50_000, $"Only {drawn} pixels differ from the panel: the memory was not drawn.");
        Assert.True(cards > 20_000, $"Only {cards} pixels have the card colour: the entries were not drawn.");
        Assert.True(warning > 100, $"Only {warning} pixels have the warning colour (the entries waiting for you and the blocked export).");
        Assert.True(success > 20, $"Only {success} pixels have the success colour (the trusted entry).");
    }

    [Fact]
    public async Task The_search_shows_its_label_and_hint_and_the_empty_summary_says_what_to_do()
    {
        var memory = await WpfHost.RunAsync(async () =>
        {
            var model = new MemoryViewModel(
                new FakeProjectWorkspace(),
                new FakeProjectMemory(),
                new FakeMemoryExporter(),
                new FakeRunActivity(),
                new DailySummaryViewModel(new FakeDailySummaryService(), new FakeClock(At)),
                new FakeClock(At));
            await model.SetProjectAsync(@"C:\proyectos\calc");
            return model;
        });

        var (label, hint, searchName, summaryHint) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new MemoryView { DataContext = memory };

            var bitmap = OffscreenRender.Render(view, 1500, 640);
            OffscreenRender.SaveIfRequested(bitmap, "memoria-busqueda-y-resumen-vacio.png");
            var texts = Descendants<System.Windows.Controls.TextBlock>(view).ToList();
            var search = Descendants<System.Windows.Controls.TextBox>(view).Single(t => System.Windows.Automation.AutomationProperties.GetName(t).StartsWith("Buscar", StringComparison.Ordinal));
            return (
                texts.Single(t => t.Text == "Buscar por palabras").ActualWidth,
                texts.Single(t => t.Text == "contenido, tema o fuente…").ActualWidth,
                System.Windows.Automation.AutomationProperties.GetName(search),
                texts.Single(t => t.Text == memory.Summary.EmptyHint).ActualHeight);
        });

        Assert.True(label > 0, "The label of the search was not laid out.");
        Assert.True(hint > 0, "The hint inside the empty search box was not laid out.");
        Assert.Equal("Buscar en la memoria por palabras", searchName);
        Assert.StartsWith("Pulsa «Resumir el día»", memory.Summary.EmptyHint, StringComparison.Ordinal);
        Assert.True(summaryHint > 0, "The empty box of the summary says nothing.");
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

    /// <summary>The template of the project, saved in place by id as the SQLite roster does.</summary>
    private sealed class TemplateRoster : IAgentRoster
    {
        private readonly List<AgentDefinition> _agents = [.. DefaultAgentRoster.Create().Select(a => a with { Memory = a.Role == AgentRole.Executor ? MemoryPolicy.ReadSelected : MemoryPolicy.None })];

        public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentDefinition>>([.. _agents]);

        public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoLayout : ICanvasLayoutStore
    {
        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => CanvasLayout.Empty;

        public void Save(string projectRoot, CanvasLayout layout)
        {
        }
    }

    [Fact]
    public async Task The_preparation_renders_offscreen_with_the_memory_switch_of_each_agent()
    {
        var document = await WpfHost.RunAsync(async () =>
        {
            var model = new OrchestratorDocumentViewModel(
                @"C:\proyectos\calc",
                new OrchestrationSession(Backends.Complete(new FakeOrchestrator()) with { Roster = new TemplateRoster() }),
                new NoLayout(),
                new FakeTimeProvider(T0),
                new InlineSynchronizationContext());
            await model.OpenAsync();
            model.IsPreparationExpanded = true;
            return model;
        });

        var drawn = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new PreparationView { DataContext = document };

            var bitmap = OffscreenRender.Render(view, 1500, 470);
            OffscreenRender.SaveIfRequested(bitmap, "fase4-preparacion-memoria.png");
            return OffscreenRender.CountUnlike(bitmap, Surface);
        });

        Assert.Equal([true, true, false, true, true, false, false], document.Roster.Agents.Select(a => a.CanReceiveMemory));
        Assert.Equal([false, true, false, false, false, false, false], document.Roster.Agents.Select(a => a.ReceivesMemory));
        Assert.True(drawn > 20_000, $"Only {drawn} pixels differ from the panel: the preparation was not drawn.");
    }
}
