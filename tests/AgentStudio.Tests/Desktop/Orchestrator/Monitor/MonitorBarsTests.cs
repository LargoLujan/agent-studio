using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Monitor;

/// <summary>
/// F13-6 (docs/17 D-12 and D-13): the bars of the cards of the monitor: the context by parts, with the breakdown of
/// <c>AgentRunStarted</c> version 3, and the consumption of the finished attempts, each with its origin. What is not known is an
/// empty track with the words that say why: never a bar at zero.
/// </summary>
public sealed class MonitorBarsTests
{
    private static readonly ContextHealthThresholds Thresholds = new();

    private static readonly IReadOnlyList<ContextPartSnapshot> Parts =
    [
        new(ContextPartKind.Protocol, 6000),
        new(ContextPartKind.Requirements, 1800),
        new(ContextPartKind.Memory, 4000),
    ];

    private static readonly MetricValue<long> Window = MetricValue<long>.Exact(200_000, "the CLI");

    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void Before_the_first_step_the_track_is_empty_and_the_words_give_the_parts_as_an_estimate()
    {
        var bar = MonitorBars.PartsBar(Parts, false, "claude-sonnet-5-5", MetricValue<long>.Missing("no step yet"), Window, Thresholds);

        Assert.False(bar.HasValue);
        Assert.Null(bar.Value);

        // 6.000 + 1.800 + 4.000 characters, divided by 4: 1.500 + 450 + 1.000 tokens.
        Assert.Equal("~2.950 tokens antes de empezar", bar.ValueText);
        Assert.True(bar.IsEstimate);
        Assert.Equal(MetricOrigin.EstimatedBySystem, bar.OriginKind);
        Assert.Contains("estimado por Agent Studio", bar.Origin, StringComparison.Ordinal);
        Assert.Equal(["Instrucciones del protocolo ~1.500 tokens", "Requisitos ~450 tokens", "Memoria ~1.000 tokens"], bar.Segments.Select(s => s.ToString()));
        Assert.All(bar.Segments, s => Assert.True(s.IsEstimate));
        Assert.Contains("Por partes:", bar.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Memoria ~1.000 tokens", bar.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_does_not_expose_the_occupancy_so_only_the_parts_before_the_launch_are_known_and_the_words_say_so()
    {
        var bar = MonitorBars.PartsBar(Parts, false, null, MetricValue<long>.NotExposed("codex exec does not report it"), MetricValue<long>.Missing("unknown"), Thresholds);

        Assert.False(bar.HasValue);
        Assert.Contains("la ocupación en curso no está expuesta", bar.Detail, StringComparison.Ordinal);
        Assert.Equal(3, bar.Segments.Count);
        Assert.Contains("~2.950 tokens", bar.ValueText, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_occupancy_of_the_step_it_is_the_bar_of_the_context_with_the_parts_as_segments_and_the_conversation_as_the_rest()
    {
        // The CLI reported 20.000 tokens for the step: the parts are 2.950 and the conversation and the tools, the rest.
        var bar = MonitorBars.PartsBar(Parts, false, "claude-sonnet-5-5", MetricValue<long>.Exact(20_000, "the CLI"), Window, Thresholds);

        Assert.True(bar.HasValue);
        Assert.Equal(10, bar.Value);
        Assert.Equal(100, bar.Maximum);
        Assert.Equal("10 %", bar.ValueText);
        Assert.Equal(["Instrucciones del protocolo", "Requisitos", "Memoria", "Conversación y herramientas"], bar.Segments.Select(s => s.Label));
        Assert.Equal(20_000d / 200_000 * 100, bar.Segments.Sum(s => s.Value), 6);
        Assert.Equal(3, bar.SegmentEnds.Count);
        Assert.Contains("estimado por Agent Studio", bar.Detail, StringComparison.Ordinal);
        Assert.Equal(3, bar.Marks.Count);
    }

    [Fact]
    public void Parts_that_pass_what_the_cli_reported_leave_the_conversation_without_a_figure_not_at_zero()
    {
        var bar = MonitorBars.PartsBar(Parts, false, "claude-sonnet-5-5", MetricValue<long>.Exact(1_000, "the CLI"), Window, Thresholds);

        Assert.DoesNotContain(bar.Segments, s => s.Label == "Conversación y herramientas");
        Assert.Contains("la conversación no tiene un dato fiable", bar.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "sin desglose: este intento empezó antes de que se guardaran las partes")]
    [InlineData(true, "sin leer: el registro no se pudo leer")]
    public void Without_parts_the_bar_is_an_empty_track_that_says_why(bool readFailed, string why)
    {
        var bar = MonitorBars.PartsBar(null, readFailed, null, MetricValue<long>.Missing("none"), Window, Thresholds);

        Assert.False(bar.HasValue);
        Assert.Equal(why, bar.ValueText);
        Assert.Empty(bar.Segments);
    }

    [Fact]
    public void The_words_of_the_bars_come_from_the_resources_in_english_too()
    {
        var bar = MonitorBars.PartsBar(Parts, false, null, MetricValue<long>.NotExposed("codex exec"), MetricValue<long>.Missing("none"), Thresholds, English);

        Assert.Equal("Context by parts", bar.Label);
        Assert.Equal("~2,950 tokens before starting", bar.ValueText);
        Assert.Contains("the occupancy in progress is not exposed", bar.Detail, StringComparison.Ordinal);
        Assert.Equal("Protocol instructions ~1,500 tokens", bar.Segments[0].ToString());
        Assert.Equal("no attempts yet", MonitorBars.ConsumptionBar([], English).ValueText);
    }

    [Fact]
    public void The_consumption_without_finished_attempts_says_so_and_is_not_a_zero()
    {
        var bar = MonitorBars.ConsumptionBar([]);

        Assert.False(bar.HasValue);
        Assert.Equal("Consumo", bar.Label);
        Assert.Equal("sin intentos todavía", bar.ValueText);
        Assert.Equal("ningún intento de este agente ha terminado", bar.Detail);
    }

    [Fact]
    public void The_consumption_adds_the_finished_attempts_with_the_weakest_origin_and_says_what_is_partial()
    {
        var exact = new TokenUsage(Fig(30_000), Fig(2_000), Fig(25_000), Window, MetricValue<decimal>.Unavailable("none"));
        var second = new TokenUsage(Fig(10_000), Fig(500), Fig(0), Window, MetricValue<decimal>.Unavailable("none"));

        var bar = MonitorBars.ConsumptionBar([exact, second]);

        Assert.Equal("42.500 tokens", bar.ValueText);
        Assert.Contains("40.000 de entrada", bar.Detail, StringComparison.Ordinal);
        Assert.Contains("25.000 de caché", bar.Detail, StringComparison.Ordinal);
        Assert.Contains("2.500 de salida", bar.Detail, StringComparison.Ordinal);
        Assert.Contains("intentos terminados: 2", bar.Detail, StringComparison.Ordinal);

        // An attempt whose output the CLI did not report: the total is partial, never completed with a zero.
        var noOutput = new TokenUsage(Fig(5_000), MetricValue<long>.NotExposed("not reported"), Fig(0), Window, MetricValue<decimal>.Unavailable("none"));
        var partial = MonitorBars.ConsumptionBar([exact, noOutput]);

        Assert.Contains("parcial", partial.ValueText + partial.Origin, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("32.000", partial.ValueText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_estimated_attempt_makes_the_consumption_an_estimate()
    {
        var estimated = new TokenUsage(MetricValue<long>.Estimated(1000, "tokenizer"), MetricValue<long>.Estimated(100, "tokenizer"), MetricValue<long>.Missing("none"), Window, MetricValue<decimal>.Unavailable("none"));

        var bar = MonitorBars.ConsumptionBar([estimated]);

        Assert.True(bar.IsEstimate);
        Assert.Equal(MetricOrigin.EstimatedByProvider, bar.OriginKind);
    }

    private static MetricValue<long> Fig(long value) => MetricValue<long>.Exact(value, "the CLI");

    // ---- the cards of the tasks, from a run with its events ----

    [Fact]
    public async Task The_card_of_a_task_reads_the_parts_of_its_attempt_from_the_log_and_shows_them_by_parts()
    {
        var source = new RecordingPartsSource { Parts = Parts };
        var (monitor, run, ui, _) = Open(new ProjectContext("p1", "C:/p"), View("T1", "exec1"));
        monitor.ContextPartsSource = source;

        run.Emit(Move("T1", WorkTaskStatus.Pending, WorkTaskStatus.Running), Step("T1", 20_000));
        Pump(ui, 2);
        await monitor.PartsCheck;
        ui.RunAll();

        var task = monitor.Tasks[0];
        Assert.Equal(("p1", "20260101-abc123", "T1", 1), source.Asked[0]);
        var bar = Assert.IsType<AgentStudio.Desktop.Controls.MetricBarReading>(task.PartsBar);
        Assert.Equal(10, bar.Value);
        Assert.Equal(4, bar.Segments.Count);

        // The consumption counts the attempt only once it ended.
        Assert.Equal("sin intentos todavía", task.ConsumptionBar!.ValueText);
        run.Emit(Usage("T1", 20_000, 1_000), EndOfAttempt("T1"));
        Pump(ui, 2);
        Assert.Contains("21.000", task.ConsumptionBar.ValueText, StringComparison.Ordinal);
        Assert.Contains("tokens", task.ConsumptionBar.ValueText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_log_that_cannot_be_read_is_shown_as_not_read_and_a_missing_start_is_asked_again_at_the_first_step()
    {
        var source = new RecordingPartsSource { Fails = true };
        var (monitor, run, ui, _) = Open(new ProjectContext("p1", "C:/p"), View("T1", "exec1"));
        monitor.ContextPartsSource = source;

        run.Emit(Move("T1", WorkTaskStatus.Pending, WorkTaskStatus.Running));
        Pump(ui, 1);
        await monitor.PartsCheck;
        ui.RunAll();

        Assert.Equal("sin leer: el registro no se pudo leer", monitor.Tasks[0].PartsBar!.ValueText);

        // The start of the attempt may be written a moment after the monitor hears of it: the first step asks once more.
        source.Fails = false;
        source.Parts = Parts;
        run.Emit(Step("T1", 5_000));
        Pump(ui, 1);
        await monitor.PartsCheck;
        ui.RunAll();

        Assert.Equal(2, source.Asked.Count);
        Assert.Equal(3, monitor.Tasks[0].PartsBar!.Segments.Count(s => s.Label != "Conversación y herramientas"));
    }

    [Fact]
    public void A_task_that_runs_no_agent_has_no_bars()
    {
        var (monitor, _, _, _) = Open(View("T1", null, WorkTaskStatus.Running, kind: WorkTaskKind.ToolRun));

        Assert.Null(monitor.Tasks[0].PartsBar);
        Assert.Null(monitor.Tasks[0].ConsumptionBar);
    }

    private sealed class RecordingPartsSource : IContextPartsSource
    {
        public IReadOnlyList<ContextPartSnapshot>? Parts { get; set; }

        public bool Fails { get; set; }

        public List<(string Project, string Run, string Task, int Attempt)> Asked { get; } = [];

        public Task<IReadOnlyList<ContextPartSnapshot>?> ReadAsync(ProjectContext project, RunId run, TaskId task, int attempt, CancellationToken cancellationToken)
        {
            Asked.Add((project.ProjectId, run.Value, task.Value, attempt));
            return Fails ? throw new InvalidOperationException("The log does not verify.") : Task.FromResult(Parts);
        }
    }
}
