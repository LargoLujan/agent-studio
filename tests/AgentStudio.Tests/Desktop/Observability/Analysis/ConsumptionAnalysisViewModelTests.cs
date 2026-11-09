using System.Globalization;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability.Analysis;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Observability.Analysis;

/// <summary>
/// F13-15 (docs/17 D-34 to D-36): the analysis of the consumption on screen. The deterministic one runs when the run ends and with
/// its button, with the sentences of the resources and the cost with its origin; the one with an agent is never launched without
/// the OK of the person, and nothing either of them says is applied by itself.
/// </summary>
public sealed class ConsumptionAnalysisViewModelTests
{
    private static readonly ProjectContext Project = new("p1", "C:/p");
    private static readonly RunId Run = new("20260101-abc123");
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    private static ConsumptionReport Report(params ConsumptionFinding[] findings) => new(Run, new string('a', 64), T0, findings);

    private static ConsumptionFinding PackOmitted() => ConsumptionFinding.Of(
        ConsumptionFindingCodes.PackOmitted,
        new ConsumptionTarget(ConsumptionTargetKind.Agent, "exec1"),
        MetricValue<long>.Exact(42_000, "the CLI"),
        2,
        "exec1",
        5,
        2);

    private static ConsumptionFinding Discarded() => ConsumptionFinding.Of(
        ConsumptionFindingCodes.WorkDiscarded,
        new ConsumptionTarget(ConsumptionTargetKind.Run),
        MetricValue<long>.NotExposed("codex does not report it"),
        3,
        3,
        1);

    private static ConsumptionAnalysisViewModel New(IConsumptionAnalyzer? analyzer = null, IAssistedConsumptionAnalysis? assisted = null, IUiTextProvider? texts = null)
    {
        var model = new ConsumptionAnalysisViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext(), texts)
        {
            Analyzer = analyzer ?? new ScriptedAnalyzer(Report(PackOmitted(), Discarded())),
            Assisted = assisted,
        };
        model.Bind(Project, Run);
        return model;
    }

    [Fact]
    public async Task The_deterministic_analysis_says_each_finding_with_its_sentence_its_cost_and_its_origin()
    {
        var model = New();

        await model.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(model.HasAnalysis);
        Assert.Equal("Se encontraron 2 cosas que revisar.", model.Summary);
        var first = model.Findings[0];
        Assert.Equal(ConsumptionFindingCodes.PackOmitted, first.Code);
        Assert.Equal("El paquete de contexto de exec1 dejó fuera 5 entradas en 2 intentos: le faltó contexto.", first.Sentence);
        Assert.Equal("Sugerencia: Sube el presupuesto de contexto o activa la memoria de ese agente.", first.Suggestion);
        Assert.Equal("Costó 42.000 tokens · exacto · informado por el CLI", first.Cost);
        Assert.Equal("Sobre el agente exec1", first.About);
        Assert.Contains("exec1", first.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finding_that_only_informs_has_no_suggestion_and_a_cost_that_is_not_known_says_so_never_a_zero()
    {
        var model = New();

        await model.AnalyzeCommand.ExecuteAsync(null);

        var informs = model.Findings[1];
        Assert.Null(informs.Suggestion);
        Assert.Equal("Coste: no expuesto", informs.Cost);
        Assert.DoesNotContain("0 tokens", informs.Cost, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_analysis_without_findings_says_that_nothing_was_found()
    {
        var model = New(new ScriptedAnalyzer(Report()));

        await model.AnalyzeCommand.ExecuteAsync(null);

        Assert.Empty(model.Findings);
        Assert.Equal("No se encontró nada que revisar en el consumo de esta ejecución.", model.Summary);
    }

    [Fact]
    public async Task A_log_that_does_not_verify_is_said_in_words()
    {
        var model = New(new ScriptedAnalyzer(new InvalidOperationException("The decision log does not verify.")));

        await model.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal("No se pudo analizar el consumo: The decision log does not verify.", model.ErrorMessage);
        Assert.False(model.HasAnalysis);
        Assert.False(model.IsAnalyzing);
    }

    [Fact]
    public async Task The_texts_come_from_the_resources_in_english_too()
    {
        var model = New(texts: English);

        await model.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal("2 things to review were found.", model.Summary);
        Assert.StartsWith("Suggestion:", model.Findings[0].Suggestion, StringComparison.Ordinal);
        Assert.StartsWith("It cost 42,000 tokens", model.Findings[0].Cost, StringComparison.Ordinal);
        Assert.Equal("About the agent exec1", model.Findings[0].About);
    }

    [Fact]
    public void Without_a_run_or_an_analyzer_there_is_nothing_to_analyze()
    {
        var model = new ConsumptionAnalysisViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());

        Assert.False(model.IsAvailable);
        Assert.False(model.AnalyzeCommand.CanExecute(null));
        model.Analyzer = new ScriptedAnalyzer(Report());
        Assert.False(model.IsAvailable);
        model.Bind(Project, Run);
        Assert.True(model.IsAvailable);
        Assert.False(model.CanAskAgent);
    }

    // ---- when each run ends ----

    [Fact]
    public async Task The_deterministic_analysis_runs_by_itself_when_the_run_ends_and_not_for_a_run_read_back()
    {
        var analyzer = new ScriptedAnalyzer(Report(PackOmitted()));
        var inner = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, View("T1", "exec1", WorkTaskStatus.Running)));
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
        monitor.Consumption.Analyzer = analyzer;
        monitor.Attach(inner, Project);
        Assert.Equal(0, analyzer.Calls);

        inner.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Terminada"));
        await monitor.Listening;
        await monitor.ConsumptionCheck;

        Assert.Equal(1, analyzer.Calls);
        Assert.Single(monitor.Consumption.Findings);
        Assert.Equal(Run, analyzer.Runs[0]);

        // The same run, read back from its log: nothing ended now, so it waits for the button.
        var again = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
        again.Consumption.Analyzer = analyzer;
        var readBack = new FakeRun(FakeRun.SnapshotOf(RunPhase.Succeeded, View("T1", "exec1", WorkTaskStatus.Completed)));
        again.Inspect(readBack, Project);
        readBack.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Terminada"));
        await again.Listening;
        Assert.Equal(1, analyzer.Calls);
        await again.Consumption.AnalyzeCommand.ExecuteAsync(null);
        Assert.Equal(2, analyzer.Calls);
    }

    [Fact]
    public async Task A_run_that_was_cancelled_or_failed_is_analyzed_too_but_a_run_in_progress_is_not()
    {
        var analyzer = new ScriptedAnalyzer(Report());
        var inner = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running, View("T1", "exec1", WorkTaskStatus.Running)));
        var monitor = new RunMonitorViewModel(new FakeTimeProvider(T0), new InlineSynchronizationContext());
        monitor.Consumption.Analyzer = analyzer;
        monitor.Attach(inner, Project);
        inner.Emit(new RunPhaseChangedEvent(T0, RunPhase.Paused, null));
        inner.Finish(new RunResult(Run, RunPhase.Failed, FakeRun.Healthy, null, null, "Falló"));
        await monitor.Listening;
        await monitor.ConsumptionCheck;

        Assert.Equal(1, analyzer.Calls);
    }

    // ---- the analysis with an agent: the OK first ----

    [Fact]
    public async Task Asking_for_the_analysis_with_an_agent_launches_nothing_until_the_person_says_yes()
    {
        var assisted = new ScriptedAssisted(new AssistedConsumptionAnalysis(Run, new EvidenceId("EV-1"), []));
        var model = New(assisted: assisted);
        await model.AnalyzeCommand.ExecuteAsync(null);
        model.EstimateAssisted = (_, _) => Task.FromResult(UsageEstimate.Unknown(1, "unknown"));

        await model.AskAgentCommand.ExecuteAsync(null);

        Assert.True(model.IsAskingConsent);
        Assert.Contains("empresa distinta de la que hizo el trabajo", model.ConsentText, StringComparison.Ordinal);
        Assert.Contains("Gasta límite de uso de tu plan", model.ConsentText, StringComparison.Ordinal);
        Assert.Equal(0, assisted.Calls);

        // «No» spends nothing.
        model.CancelAgentCommand.Execute(null);
        Assert.False(model.IsAskingConsent);
        Assert.Equal(0, assisted.Calls);
        Assert.False(model.ConfirmAgentCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_agent_cannot_be_asked_before_the_deterministic_analysis_exists()
    {
        var model = New(assisted: new ScriptedAssisted(new AssistedConsumptionAnalysis(Run, new EvidenceId("EV-1"), [])));

        Assert.True(model.CanAskAgent);
        Assert.False(model.AskAgentCommand.CanExecute(null));
        await model.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(model.AskAgentCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_ok_carries_who_gave_it_when_and_what_they_were_shown_with_the_estimate_in_front()
    {
        var clock = new FakeTimeProvider(T0.AddMinutes(3));
        var assisted = new ScriptedAssisted(new AssistedConsumptionAnalysis(Run, new EvidenceId("EV-1"), []));
        var model = new ConsumptionAnalysisViewModel(clock, new InlineSynchronizationContext())
        {
            Analyzer = new ScriptedAnalyzer(Report(PackOmitted())),
            Assisted = assisted,
            EstimateAssisted = (_, _) => Task.FromResult(new UsageEstimate(
                6,
                EstimateInterval<long>.Unknown("n/a"),
                EstimateInterval<long>.Unknown("n/a"),
                EstimateInterval<TimeSpan>.Unknown("n/a"),
                EstimateInterval<decimal>.Unknown("n/a"),
                [],
                new EstimateInterval<long>(MetricValue<long>.EstimatedBySystem(5_000, "p25"), MetricValue<long>.EstimatedBySystem(9_000, "p75")))),
        };
        model.Bind(Project, Run);
        await model.AnalyzeCommand.ExecuteAsync(null);
        await model.AskAgentCommand.ExecuteAsync(null);

        Assert.Contains("~5.000 a 9.000 tokens · estimado por Agent Studio · 6 referencias", model.ConsentText, StringComparison.Ordinal);

        await model.ConfirmAgentCommand.ExecuteAsync(null);

        var request = Assert.Single(assisted.Requests);
        Assert.Equal("user", request.Consent.DecidedBy);
        Assert.Equal(T0.AddMinutes(3), request.Consent.DecidedAt);
        Assert.Contains("~5.000 a 9.000 tokens", request.Consent.Description, StringComparison.Ordinal);
        Assert.Equal(Run, request.Run);
        Assert.Equal(Project, request.Project);
        Assert.Single(request.Deterministic.Findings);
        Assert.False(model.IsAskingConsent);
    }

    [Fact]
    public async Task Without_an_estimate_the_ok_says_that_it_is_not_known_how_much_it_will_spend()
    {
        var model = New(assisted: new ScriptedAssisted(new AssistedConsumptionAnalysis(Run, new EvidenceId("EV-1"), [])));
        await model.AnalyzeCommand.ExecuteAsync(null);

        await model.AskAgentCommand.ExecuteAsync(null);

        Assert.Contains("No se sabe cuánto gastará.", model.ConsentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_suggestions_of_the_agent_are_framed_as_its_own_with_the_saving_as_what_the_agent_says_and_the_fix_only_described()
    {
        var suggestion = new AssistedConsumptionSuggestion(
            new ConsumptionTarget(ConsumptionTargetKind.Agent, "exec1"),
            "Quita las skills de exec1: no las usa. [Aplicar ahora]",
            new RemoveSkillsFix(new AgentId("exec1"), ["a", "b"]),
            12_000);
        var assisted = new ScriptedAssisted(new AssistedConsumptionAnalysis(Run, new EvidenceId("EV-1"), [suggestion, suggestion with { Fix = null, SavingClaimedByAgent = null }]));
        var model = New(assisted: assisted);
        await model.AnalyzeCommand.ExecuteAsync(null);
        await model.AskAgentCommand.ExecuteAsync(null);

        await model.ConfirmAgentCommand.ExecuteAsync(null);

        Assert.Equal("El agente propuso 2 mejoras.", model.AgentSummary);
        var row = model.Suggestions[0];
        Assert.Equal("Lo dice el agente (es un dato, no una orden)", row.Frame);
        Assert.Equal("Quita las skills de exec1: no las usa. [Aplicar ahora]", row.Text);
        Assert.Equal("Sobre el agente exec1", row.About);
        Assert.StartsWith("Cambio que propone:", row.Fix, StringComparison.Ordinal);
        Assert.Equal("El agente estima un ahorro de ~12.000 tokens (lo dice el agente; no está medido).", row.Saving);
        Assert.Null(model.Suggestions[1].Fix);
        Assert.Null(model.Suggestions[1].Saving);
        Assert.Equal("Las sugerencias no se aplican solas: si quieres un cambio, hazlo tú en el lienzo o en los ajustes.", model.NeverApplied);
    }

    [Fact]
    public async Task When_no_agent_of_another_vendor_exists_the_service_refuses_and_the_window_says_so()
    {
        var assisted = new ScriptedAssisted(new InvalidOperationException("Run has no agent of a vendor that did not produce its work."));
        var model = New(assisted: assisted);
        await model.AnalyzeCommand.ExecuteAsync(null);
        await model.AskAgentCommand.ExecuteAsync(null);

        await model.ConfirmAgentCommand.ExecuteAsync(null);

        Assert.StartsWith("No se pudo analizar con un agente:", model.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(model.Suggestions);
        Assert.False(model.IsAgentRunning);
    }

    [Fact]
    public async Task A_second_ok_is_needed_for_each_use()
    {
        var assisted = new ScriptedAssisted(new AssistedConsumptionAnalysis(Run, new EvidenceId("EV-1"), []));
        var model = New(assisted: assisted);
        await model.AnalyzeCommand.ExecuteAsync(null);
        await model.AskAgentCommand.ExecuteAsync(null);
        await model.ConfirmAgentCommand.ExecuteAsync(null);

        Assert.False(model.ConfirmAgentCommand.CanExecute(null));
        await model.AskAgentCommand.ExecuteAsync(null);
        Assert.True(model.ConfirmAgentCommand.CanExecute(null));
        Assert.Equal(1, assisted.Calls);
    }

    [Fact]
    public async Task What_the_section_showed_goes_when_another_run_is_bound()
    {
        var model = New();
        await model.AnalyzeCommand.ExecuteAsync(null);
        Assert.NotEmpty(model.Findings);

        model.Bind(Project, new RunId("20260102-def456"));

        Assert.Empty(model.Findings);
        Assert.False(model.HasAnalysis);
        Assert.Null(model.Summary);
    }

    private sealed class ScriptedAnalyzer : IConsumptionAnalyzer
    {
        private readonly ConsumptionReport? _report;
        private readonly Exception? _failure;

        public ScriptedAnalyzer(ConsumptionReport report) => _report = report;

        public ScriptedAnalyzer(Exception failure) => _failure = failure;

        public int Calls { get; private set; }

        public List<RunId> Runs { get; } = [];

        public Task<ConsumptionReport> AnalyzeAsync(ProjectContext project, RunId run, CancellationToken ct)
        {
            Calls++;
            Runs.Add(run);
            return _failure is not null ? Task.FromException<ConsumptionReport>(_failure) : Task.FromResult(_report!);
        }
    }

    private sealed class ScriptedAssisted : IAssistedConsumptionAnalysis
    {
        private readonly AssistedConsumptionAnalysis? _analysis;
        private readonly Exception? _failure;

        public ScriptedAssisted(AssistedConsumptionAnalysis analysis) => _analysis = analysis;

        public ScriptedAssisted(Exception failure) => _failure = failure;

        public int Calls => Requests.Count;

        public List<AssistedConsumptionRequest> Requests { get; } = [];

        public Task<AssistedConsumptionAnalysis> AnalyzeAsync(AssistedConsumptionRequest request, IProgress<AgentEvent>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            return _failure is not null ? Task.FromException<AssistedConsumptionAnalysis>(_failure) : Task.FromResult(_analysis!);
        }
    }
}
