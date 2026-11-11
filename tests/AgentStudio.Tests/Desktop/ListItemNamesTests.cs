using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Skills;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Skills;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Memory;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Tests.Desktop;

/// <summary>
/// A1-bis, seen live with UI Automation: the options of the model of an agent read as «ModelOption { Label = haiku, … }».
/// A list item that is not a control is named by UI Automation with its <c>ToString()</c>; each record or class shown as an
/// item of a list or a selector gives its label. Built off screen: no window is shown.
/// </summary>
public sealed class ListItemNamesTests
{
    private static IEnumerable<(object Item, string Name)> Items =>
    [
        (new ModelOption("haiku", "haiku"), "haiku"),
        (new Choice<int>(1, "Ejecutor"), "Ejecutor"),
        (new Option<int>(7, "Últimos 7 días"), "Últimos 7 días"),
        (new RunChoice(new RunId("20261030-abc123"), "20261030-abc123 · terminada", "hash"), "20261030-abc123 · terminada"),
        (new PaletteItem(WorkTaskKind.AgentWork), NodeKinds.Label(WorkTaskKind.AgentWork)),
        (new StatusLegendItem(WorkTaskStatus.Running), TaskStatusText.Label(WorkTaskStatus.Running)),
        (new PlanProblem(null, "Falta una tarea de integración."), "Falta una tarea de integración."),
        (new QuickOpenItem("src/Calc.cs", @"C:\proyectos\calc\src\Calc.cs"), "src/Calc.cs"),
        (RecentProjectItem.From(@"C:\proyectos\calc"), "calc"),
        (new SkillCheckViewModel("license", "Licencia", SkillHealthStatus.Healthy, "Sana", "Licencia permisiva: MIT."), "Licencia: Sana"),
        (new SkillScopeViewModel { Title = "Proyecto" }, "Proyecto"),
        (new VerificationRow("Compilar", "dotnet build", null), "Compilar"),
        (new ExitWorkLine("calc", "Ejecución 20261030-abc123", "En ejecución", true), "calc · Ejecución 20261030-abc123 · En ejecución"),
        (new ShellInfo("pwsh", "PowerShell 7", @"C:\Program Files\PowerShell\7\pwsh.exe", []), "PowerShell 7"),
        (new RunListItem(new RunSummary(new RunId("20261030-abc123"), DateTimeOffset.UnixEpoch, RunPhase.Succeeded, "hash", "Hecho.", null), "30/10/2026 10:00"),
            $"{RunPhaseText.Label(RunPhase.Succeeded)} · 30/10/2026 10:00 · 20261030-abc123"),
        (new AgentChoice(PlanFixtures.Roster()[0]), new AgentChoice(PlanFixtures.Roster()[0]).Label),
        (new AgentCostViewModel("ejecutor", 2, MetricValue<long>.EstimatedBySystem(37, "Agent Studio")), "ejecutor: 2 skills · ~37 tokens por ejecución (estimado)"),
        (new SkillRecommendationItemViewModel("spark-optimization", "spark-optimization", "11 puntos", new RelayCommand(() => { })), "spark-optimization"),
        (new SkillSectorNodeViewModel("datos", "Ingeniería de datos", []), "Ingeniería de datos (0)"),

        // The bars of tanda 2 (docs/18 P3): the bars of the status bar, the rows of the panel of the tray and the windows of «Consumo».
        (MetricBars.Budget("Presupuesto", 3, 12), "Presupuesto: 3 de 12 · exacto · medido por Agent Studio. ejecuciones de agente usadas frente al máximo de la ejecución"),
        (MetricBars.Quota("Claude 5 h", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), "vista hace 12 min"), "Claude 5 h: 47 % · exacto · informado por el programa del agente · vista hace 12 min"),
        (new ContextBarRow("T1 Implementar Calc.Multiply", 0.47, "47 % · exacto · informado por el programa del agente · sano", Tone.Success), "Contexto de T1 Implementar Calc.Multiply: 47 % · exacto · informado por el programa del agente · sano"),
        (new QuotaRow("Codex CLI", "Desactivado: activa «Leer el límite de uso de Codex» en «Ajustes»."), "Codex CLI · Desactivado: activa «Leer el límite de uso de Codex» en «Ajustes»."),
        (new QuotaRow("Claude Code", "5 horas · 47 %") { Bar = MetricBars.Quota("Claude Code · 5 horas", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), "vista hace 12 min") },
            "Claude Code · 5 horas: 47 % · exacto · informado por el programa del agente · vista hace 12 min"),
        (new QuotaWindowRow("5 horas", MetricText.Percent(MetricValue<double>.Exact(0.47, "claude rate_limit_event")), "vista hace 12 min", null)
            { Bar = MetricBars.Quota("5 horas", MetricValue<double>.Exact(0.47, "claude rate_limit_event"), "vista hace 12 min") },
            "5 horas: 47 % · exacto · informado por el programa del agente · vista hace 12 min"),

        // V34, the walk of tanda 2 (2026-11-03): the 11 types still read by their type name or the text of their record.
        (new ActivityEntry(DateTimeOffset.UnixEpoch, "10:02:03", "T1", "Empezó el intento 1"), "10:02:03 · T1 · Empezó el intento 1"),
        (new EvidenceEntry("T1", "Pruebas (test)", "Ejecución de pruebas", "capturada por el sistema", "abc123def456", null),
            "T1 · Pruebas (test) · Ejecución de pruebas · capturada por el sistema"),
        (new AssessmentRow("✔", "satisfecho", Tone.Success, "R1", "Añadir Calc.Divide", "Pasa su prueba.", "ev-1", null), "R1: satisfecho"),
        (new MonitorTaskViewModel(new TaskId("T1"), "Implementar Calc.Divide", WorkTaskKind.AgentWork, TimeZoneInfo.Utc), "T1 Implementar Calc.Divide · Pendiente"),
        (Roster().Agents[0], Roster().Agents[0].Name),
        (Requirements().Drafts[0], "R1: Add Calc.Multiply"),
        (new ConnectorViewModel(new NodeViewModel(new TaskId("T2"), WorkTaskKind.AgentWork, "Integrar", default), ConnectorFlow.Input, "Entrada"), "Entrada de T2"),
        (Card().Verdicts[0], $"{Card().Verdicts[0].DecisionText} · {Card().Verdicts[0].JudgeText}"),
        (new DimensionOption(AgentStudio.Application.Usage.UsageDimension.Provider, "Proveedor"), "Proveedor"),
        (new UsageGroupRow("Proveedor: claude-code", Count, Count, Count, Count, Count, Count, Count, Count, Count, Count, "sin dato", Count)
            { ContextPeakBar = MetricBars.Empty(UsageDashboardViewModel.PeakLabel, "sin dato") },
            "Proveedor: claude-code"),
        (new AgentStudio.Desktop.Memory.MemoryEntryViewModel(
            new AgentStudio.Application.Knowledge.MemoryEntry("m-1", AgentStudio.Application.Knowledge.MemoryKind.Decision, "Pruebas", "Las pruebas usan xUnit.", AgentStudio.Application.Knowledge.MemoryOrigin.User, "registro", DateTimeOffset.UnixEpoch, "sha", AgentStudio.Application.Knowledge.MemoryTrust.Trusted, null, null),
            TimeZoneInfo.Utc),
            "Decisión · Pruebas · De confianza"),

        // «Memoria» (Phase 12): its results, the days of the journal and the files of the model.
        (new LogFragmentViewModel(MemorySearchSamples.LogHit(57, "registro · veredicto", "Veredicto: R1 cumplido.", 1, null), TimeZoneInfo.Utc), "Registro · veredicto · 18/10/2026 07:30"),
        (new JournalDayViewModel(new DateOnly(2026, 10, 28), 2, false), "miércoles 28/10/2026 · 2 entradas en cuarentena"),
        (new ModelFileLine("sentencepiece.bpe.model", "5,1 MB", "https://huggingface.co/intfloat/multilingual-e5-small"), "sentencepiece.bpe.model · 5,1 MB"),
    ];

    private static Figure Count { get; } = MetricText.Measured(3);

    /// <summary>The agents of the default template, as the preparation lists them.</summary>
    private static RosterEditorViewModel Roster()
    {
        var roster = new RosterEditorViewModel(null, () => Task.CompletedTask);
        roster.Load(null, AgentStudio.Desktop.Orchestrator.Session.DefaultAgentRoster.Create());
        return roster;
    }

    /// <summary>The requirements of the fixture, as the preparation lists them.</summary>
    private static RequirementsEditorViewModel Requirements()
    {
        var editor = new RequirementsEditorViewModel(new AgentStudio.Desktop.Orchestrator.Session.OrchestrationSession(AgentStudio.Desktop.Orchestrator.Session.OrchestrationBackend.None), TimeProvider.System);
        editor.Load(null, PlanFixtures.Requirements());
        return editor;
    }

    /// <summary>The card of a panel of two judges that disagree, as the debates show it.</summary>
    private static VerdictCardViewModel Card() =>
        new(Debate.DebateSamples.PanelThatDisagrees, Debate.DebateSamples.Disagree, new VerdictCardContext("Veredicto del debate"), TimeZoneInfo.Utc);

    [Fact]
    public void Each_item_of_a_list_is_named_by_its_label_in_ui_automation()
    {
        var wrong = WpfHost.Run(() =>
        {
            var problems = new List<string>();
            foreach (var (item, name) in Items)
            {
                // A selector (a list box, as a combo box's list) and a plain list of items.
                var list = new ListBox { ItemsSource = new[] { item } };
                var plain = new ItemsControl { ItemsSource = new[] { item } };
                OffscreenRender.Render(new StackPanel { Children = { list, plain } }, 400, 200);
                foreach (var (kind, names) in new[] { ("ListBox", Names(list)), ("ItemsControl", Names(plain)) })
                {
                    if (names is not [var only] || only != name)
                    {
                        problems.Add($"{item.GetType().Name} in a {kind}: «{string.Join("», «", names)}», not «{name}».");
                    }
                }
            }

            return problems;
        });

        Assert.Empty(wrong);
    }

    private static List<string> Names(ItemsControl control) =>
        [.. (UIElementAutomationPeer.CreatePeerForElement(control)?.GetChildren() ?? []).Select(peer => peer.GetName())];
}
