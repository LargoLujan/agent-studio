using System.Globalization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Panels;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Panels;

/// <summary>
/// F13-18 (docs/17 D-45): the panels of the bottom zone, «Problemas», «Salida» and «Pruebas». Their data are the ones that exist: the
/// findings of the analyzer and the validator, the activity and the evidence of the run. They follow the run through the grouped
/// state of the monitor, and none of them invents a line.
/// </summary>
public sealed class PanelsTests
{
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    private static AgentActivityEvent End(string task, AgentRunStatus status = AgentRunStatus.Failed, AttemptEndClass cls = AttemptEndClass.ContextExhausted) =>
        new(T0, Tid(task), new RunFinished(T0, new AgentRunOutcome(status, 1, null, null, "s1", "boom", new AgentRunEnd(cls))));

    private static EvidenceCapturedEvent Verification(string task, string title, EvidenceKind kind, TestRunSummary? summary, EvidenceOrigin origin = EvidenceOrigin.CapturedBySystem) =>
        new(T0, Tid(task), new EvidenceRecord(Eid("E-" + Math.Abs(title.GetHashCode(StringComparison.Ordinal))), kind, origin, title, "test", T0, new string('a', 64), summary));

    // ---- Problemas ----

    [Fact]
    public void A_failed_task_is_an_error_with_the_class_of_its_end_and_its_reason()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running), View("T2", "exec2", WorkTaskStatus.Running));
        var panel = new ProblemsPanelViewModel();
        panel.Attach(monitor);

        run.Emit(End("T1"), Move("T1", WorkTaskStatus.Running, WorkTaskStatus.Failed, "La verificación falló en: test"));
        Pump(ui, 2);
        Settle(time, ui);

        var row = Assert.Single(panel.Rows);
        Assert.Equal(ProblemSeverity.Error, row.Severity);
        Assert.Equal("Error", row.SeverityText);
        Assert.Equal("T1", row.Where);
        Assert.Equal("La tarea «Tarea T1» falló. El intento terminó así: se llenó la memoria de trabajo del modelo.", row.Text);
        Assert.Contains("La verificación falló en: test", row.Detail, StringComparison.Ordinal);
        Assert.Equal("Problemas: 1 (errores: 1, avisos: 0, información: 0)", panel.Summary);
        Assert.False(panel.IsEmpty);
    }

    [Fact]
    public void A_blocked_task_is_a_warning_but_one_that_waits_for_an_answer_is_not_a_problem()
    {
        var (monitor, _, _, _) = Open(
            View("T1", "a", WorkTaskStatus.Blocked, "ProviderUnavailable"),
            View("T2", "b", WorkTaskStatus.Blocked, BlockReasonText.AwaitingAnswerReason));
        var panel = new ProblemsPanelViewModel();

        panel.Attach(monitor);

        var row = Assert.Single(panel.Rows);
        Assert.Equal("T1", row.Where);
        Assert.Equal(ProblemSeverity.Warning, row.Severity);
        Assert.Equal("Bloqueada · el programa del agente no está disponible", row.Detail);
    }

    [Fact]
    public void The_findings_of_the_montage_come_with_their_sentence_and_the_most_serious_goes_first()
    {
        var (monitor, _, _, _) = Open(View("T1", "a", WorkTaskStatus.Failed));
        var panel = new ProblemsPanelViewModel();
        panel.Attach(monitor);

        panel.SetMontage(new MontageAnalysis(1,
        [
            Finding.Of(FindingCodes.ParallelWriteOverlap, FindingSeverity.Warning, ["T1", "T2"], null, "T1", "T2"),
            Finding.Of(FindingCodes.JudgeSameVendor, FindingSeverity.Error, ["judge"], null, "juez", "anthropic"),
            Finding.Of("validator.something-new", FindingSeverity.Suggestion, ["T3"]),
        ]));

        Assert.Equal([ProblemSeverity.Error, ProblemSeverity.Error, ProblemSeverity.Warning, ProblemSeverity.Info], panel.Rows.Select(r => r.Severity));
        Assert.Equal("El juez juez es de anthropic, la misma empresa que quien hace el trabajo: tiene que ser de otra.", panel.Rows.First(r => r.Source == ProblemSource.Montage && r.Severity == ProblemSeverity.Error).Text);
        Assert.Equal("T1, T2", panel.Rows.First(r => r.Severity == ProblemSeverity.Warning).Where);

        // A code whose sentence is not in the resources yet says its code: never an empty line and never a made-up sentence.
        Assert.Equal("Aviso del montaje (validator.something-new)", panel.Rows[^1].Text);
        Assert.Equal("Información", panel.Rows[^1].SeverityText);
    }

    [Fact]
    public void Without_problems_the_panel_says_so_and_clearing_the_montage_clears_its_lines()
    {
        var panel = new ProblemsPanelViewModel();
        panel.SetMontage(new MontageAnalysis(1, [Finding.Of(FindingCodes.JudgeSameVendor, FindingSeverity.Error, ["judge"], null, "juez", "anthropic")]));
        Assert.Single(panel.Rows);

        panel.SetMontage(null);

        Assert.Empty(panel.Rows);
        Assert.True(panel.IsEmpty);
        Assert.Equal("Sin problemas.", panel.Summary);
    }

    [Fact]
    public void The_problems_are_said_in_english_too()
    {
        var (monitor, _, _, _) = Open(View("T1", "a", WorkTaskStatus.Failed));
        var panel = new ProblemsPanelViewModel(English);

        panel.Attach(monitor);

        Assert.Equal("Problems: 1 (errors: 1, warnings: 0, information: 0)", panel.Summary);
        Assert.Equal("Error", panel.Rows[0].SeverityText);
        Assert.Equal("The task \"Tarea T1\" failed.", panel.Rows[0].Text);
    }

    // ---- Salida ----

    [Fact]
    public void The_output_is_the_activity_of_the_run_or_of_the_task_or_the_agent_selected()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running), View("T2", "exec2", WorkTaskStatus.Running));
        var panel = new OutputPanelViewModel();
        panel.Attach(monitor);

        run.Emit(
            new AgentActivityEvent(T0, Tid("T1"), new ToolInvoked(T0, "Edit", "a.cs")),
            new AgentActivityEvent(T0, Tid("T2"), new ToolInvoked(T0, "Read", "b.cs")),
            new AgentActivityEvent(T0, Tid("T1"), new ToolCompleted(T0, "Edit", true)));
        Pump(ui, 3);
        Settle(time, ui);

        Assert.Equal("Salida de la ejecución", panel.Title);
        Assert.Equal(4, panel.Lines.Count);

        panel.SelectTask(Tid("T1"));
        Assert.Equal("Salida de la tarea T1", panel.Title);
        Assert.All(panel.Lines, l => Assert.Equal("T1", l.Task));
        Assert.Equal(2, panel.Lines.Count);

        panel.SelectAgent(new AgentId("exec2"));
        Assert.Equal("Salida del agente exec2", panel.Title);
        Assert.Equal(["T2"], panel.Lines.Select(l => l.Task).Distinct());

        panel.SelectTask(null);
        Assert.Equal("Salida de la ejecución", panel.Title);
        Assert.Equal(4, panel.Lines.Count);
    }

    [Fact]
    public void The_output_adds_only_the_new_lines_and_follows_the_run_through_the_grouped_change()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        var panel = new OutputPanelViewModel();
        panel.Attach(monitor);
        run.Emit(new AgentActivityEvent(T0, Tid("T1"), new ToolInvoked(T0, "Read", "a.cs")));
        Pump(ui, 1);
        Settle(time, ui);
        var first = panel.Lines[^1];
        var changes = 0;
        panel.Lines.CollectionChanged += (_, _) => changes++;

        run.Emit(new AgentActivityEvent(T0, Tid("T1"), new ToolInvoked(T0, "Edit", "b.cs")));
        Pump(ui, 1);

        // Nothing repaints until the group: the line is in the activity of the monitor, not yet in the panel.
        Assert.Equal(0, changes);
        Settle(time, ui);

        Assert.Equal(1, changes);
        Assert.Same(first, panel.Lines[^2]);
        Assert.False(panel.IsEmpty);
    }

    [Fact]
    public void The_text_of_an_agent_loses_its_control_and_direction_characters_and_the_panel_says_it_is_theirs()
    {
        var hostile = "gracias ‮oculto⁩ ‏ fin\u0007";
        var line = AgentActivityText.Describe(new ToolInvoked(T0, "Edit", hostile), TimeZoneInfo.Utc);

        Assert.Equal("Usa Edit: gracias oculto  fin", line);
        Assert.DoesNotContain((char)0x202E, line!);
        Assert.DoesNotContain((char)0x200F, line!);
        Assert.Equal("gracias oculto  fin", AgentActivityText.Clean(hostile));
        Assert.Equal("Lo que sale aquí lo escribieron los agentes: son sus textos, no mensajes del sistema. Se muestran sin caracteres de control.", new OutputPanelViewModel().AgentTextNotice);
    }

    [Fact]
    public void The_monitor_keeps_the_cleaned_line_in_its_activity()
    {
        var (monitor, run, ui, _) = Open(View("T1", "exec1", WorkTaskStatus.Running));

        run.Emit(new AgentActivityEvent(T0, Tid("T1"), new MessageEmitted(T0, "hecho ‮odnum", false)));
        Pump(ui, 1);

        Assert.Contains(monitor.Activity, a => a.Text == "hecho odnum");
    }

    [Fact]
    public void Before_any_activity_the_panel_says_there_is_nothing_to_show()
    {
        var panel = new OutputPanelViewModel();

        Assert.True(panel.IsEmpty);
        Assert.Equal("Aún no hay actividad que enseñar.", panel.EmptyText);
    }

    // ---- Pruebas ----

    [Fact]
    public void The_tests_panel_lists_the_verifications_the_system_captured_with_their_counters_and_marks_the_failed_ones()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        var panel = new TestsPanelViewModel();
        panel.Attach(monitor);

        run.Emit(
            Verification("T1", "Compilar (build) · sin aislamiento", EvidenceKind.BuildLog, null),
            Verification("T1", "Pruebas (test)", EvidenceKind.TestRun, new TestRunSummary(8, 2, 1, 1)),
            Verification("T1", "Declaración del agente", EvidenceKind.AgentStatement, null, EvidenceOrigin.ReportedByAgent));
        Pump(ui, 3);
        Settle(time, ui);

        Assert.Equal(2, panel.Rows.Count);
        Assert.Equal("capturada", panel.Rows[0].StateText);
        Assert.False(panel.Rows[0].Failed);
        var tests = panel.Rows[1];
        Assert.True(tests.Failed);
        Assert.Equal("falló", tests.StateText);
        Assert.Equal("8 correctas · 2 fallidas · 1 omitidas · código de salida 1", tests.Counters);
        Assert.Equal("2 verificaciones, 1 con fallos.", panel.Summary);
        Assert.Contains("Pruebas (test)", tests.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_of_tests_that_executed_none_does_not_count_as_passed()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        var panel = new TestsPanelViewModel();
        panel.Attach(monitor);

        run.Emit(Verification("T1", "Pruebas (test)", EvidenceKind.TestRun, new TestRunSummary(0, 0, 0, 0)));
        Pump(ui, 1);
        Settle(time, ui);

        Assert.True(panel.Rows[0].Failed);
    }

    [Fact]
    public void The_button_of_the_tests_of_head_exists_only_when_the_shell_composed_it_and_runs_through_the_service()
    {
        var panel = new TestsPanelViewModel();
        Assert.False(panel.CanRunHead);
        Assert.False(panel.RunHeadTestsCommand.CanExecute(null));

        var service = new ScriptedHeadTests(new HeadTestsResult([new TestRunRow(null, "Pruebas (test)", "10 correctas · 0 fallidas · 0 omitidas · código de salida 0", false, "bien", null, "abc")]));
        panel.HeadTests = service;
        panel.Refresh();

        Assert.True(panel.CanRunHead);
        Assert.True(panel.RunHeadTestsCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_tests_of_head_show_what_the_service_said_and_a_failure_in_words()
    {
        var panel = new TestsPanelViewModel { HeadTests = new ScriptedHeadTests(new HeadTestsResult([new TestRunRow(null, "Pruebas (test)", "c", true, "falló", "error", "h")])) };

        await panel.RunHeadTestsCommand.ExecuteAsync(null);

        Assert.Equal("Fallan 1 pasos de las pruebas de la última versión.", panel.HeadSummary);
        Assert.Single(panel.HeadRows);
        Assert.False(panel.IsRunning);

        panel.HeadTests = new ScriptedHeadTests(new InvalidOperationException("No hay copia aislada."));
        await panel.RunHeadTestsCommand.ExecuteAsync(null);

        Assert.Equal("No se pudieron ejecutar las pruebas de la última versión: No hay copia aislada.", panel.ErrorMessage);
        Assert.Empty(panel.HeadRows);
    }

    [Fact]
    public async Task The_tests_of_head_can_be_cancelled_and_the_service_gets_the_cancellation()
    {
        var service = new WaitingHeadTests();
        var panel = new TestsPanelViewModel { HeadTests = service };

        Assert.False(panel.CancelHeadTestsCommand.CanExecute(null));
        var running = panel.RunHeadTestsCommand.ExecuteAsync(null);
        await service.Started.Task;

        Assert.True(panel.IsRunning);
        Assert.True(panel.CancelHeadTestsCommand.CanExecute(null));
        panel.CancelHeadTestsCommand.Execute(null);
        await running;

        Assert.True(service.Token.IsCancellationRequested);
        Assert.False(panel.IsRunning);
        Assert.Equal("Cancelaste las pruebas de la última versión.", panel.HeadSummary);
        Assert.Null(panel.ErrorMessage);
    }

    [Fact]
    public void Two_failed_steps_of_one_task_do_not_repeat_the_same_error_line_on_each_row()
    {
        var (monitor, run, ui, time) = Open(View("T1", "exec1", WorkTaskStatus.Running));
        var panel = new TestsPanelViewModel();
        panel.Attach(monitor);

        run.Emit(
            Verification("T1", "Pruebas (test)", EvidenceKind.TestRun, new TestRunSummary(8, 2, 0, 1)),
            Verification("T1", "Pruebas de integración (itest)", EvidenceKind.TestRun, new TestRunSummary(3, 1, 0, 1)));
        Pump(ui, 2);
        Settle(time, ui);

        Assert.Equal(2, panel.Rows.Count(r => r.Failed));
        Assert.All(panel.Rows, r => Assert.Null(r.Error));
        Assert.Empty(panel.Notes);
    }

    private sealed class WaitingHeadTests : IHeadTestsService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        public async Task<HeadTestsResult> RunAsync(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HeadTestsResult([]);
        }
    }

    private sealed class ScriptedHeadTests : IHeadTestsService
    {
        private readonly HeadTestsResult? _result;
        private readonly Exception? _failure;

        public ScriptedHeadTests(HeadTestsResult result) => _result = result;

        public ScriptedHeadTests(Exception failure) => _failure = failure;

        public Task<HeadTestsResult> RunAsync(CancellationToken cancellationToken) =>
            _failure is not null ? Task.FromException<HeadTestsResult>(_failure) : Task.FromResult(_result!);
    }
}
