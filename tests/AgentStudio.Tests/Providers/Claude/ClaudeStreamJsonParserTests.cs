using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Providers.Cli.Claude;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Claude;

public sealed class ClaudeStreamJsonParserTests
{
    private static (List<AgentEvent> Events, ClaudeStreamJsonParser Parser) ParseFixture(string name)
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);
        var events = FixtureLines(name).SelectMany(parser.Parse).ToList();
        return (events, parser);
    }

    [Fact]
    public void Text_fixture_yields_the_expected_events_in_order()
    {
        var (events, parser) = ParseFixture("01-texto.jsonl");

        Assert.Equal(
            [typeof(QuotaReported), typeof(SessionStarted), typeof(ContextObserved), typeof(MessageEmitted), typeof(UsageReported), typeof(RunFinished)],
            events.Select(e => e.GetType()).ToArray());
        Assert.Equal(0, parser.IgnoredLines);

        var started = events.OfType<SessionStarted>().Single();
        Assert.Equal("19e487d0-b6fb-4f6f-b1ac-c0c8d9e5e9b5", started.SessionId);
        Assert.Equal("claude-haiku-4-5-20251001", started.Model);
        Assert.Equal("OK", events.OfType<MessageEmitted>().Single().Text);
    }

    [Fact]
    public void Text_fixture_reports_quota_windows()
    {
        var (events, _) = ParseFixture("01-texto.jsonl");

        var windows = events.OfType<QuotaReported>().Single().Windows;
        Assert.Equal(["five_hour", "seven_day"], windows.Select(w => w.Name).ToArray());
        Assert.Equal(0.47, windows[0].Utilization.Value);
        Assert.Equal(MetricProvenance.Exact, windows[0].Utilization.Provenance);
        Assert.Equal(0.25, windows[1].Utilization.Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792066800), windows[0].ResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792350000), windows[1].ResetsAt);
        Assert.Empty(events.OfType<RateLimitSignaled>());
    }

    [Fact]
    public void Text_fixture_reports_usage_with_the_window_of_the_model_that_served_the_steps()
    {
        var (events, _) = ParseFixture("01-texto.jsonl");

        // init announces claude-haiku-4-5-20251001 (200 000), but the main loop ran on claude-sonnet-5-5 (docs/14 D-13).
        var usage = events.OfType<UsageReported>().Single().Usage;
        Assert.Equal(2 + 15296 + 27242, usage.InputTokens.Value);
        Assert.Equal(27242, usage.CachedInputTokens.Value);
        Assert.Equal(391, usage.OutputTokens.Value);
        Assert.Equal(1_000_000, usage.ContextWindow.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, usage.ContextWindow.Origin);
        Assert.Equal(0.0715174m, Math.Round(usage.CostUsd.Value!.Value, 7));
        Assert.Equal(MetricProvenance.Estimated, usage.CostUsd.Provenance);
        Assert.Equal("estimación del CLI", usage.CostUsd.Source);
    }

    [Fact]
    public void Text_fixture_occupies_four_percent_of_the_context_not_twenty_one()
    {
        var (events, _) = ParseFixture("01-texto.jsonl");

        var step = Assert.Single(events.OfType<ContextObserved>());
        var window = Assert.Single(events.OfType<UsageReported>().Single().Usage.PerModel, m => m.Model == step.Model).ContextWindow;
        var health = new ContextHealthEvaluator(new ContextHealthThresholds()).Evaluate(step.PromptTokens, window);

        Assert.Equal(4, (int)Math.Round(health.Utilization.Value!.Value * 100));
        Assert.Equal(MetricOrigin.ReportedByProvider, health.Utilization.Origin);
        Assert.Equal(ContextHealthLevel.Healthy, health.Level);
    }

    [Fact]
    public void Each_step_is_observed_once_by_message_id_with_the_prompt_of_that_call()
    {
        // 02-herramienta repeats every message (thinking, then tool_use or text) with the same id and usage.
        var (events, _) = ParseFixture("02-herramienta.jsonl");

        var steps = events.OfType<ContextObserved>().ToList();
        Assert.Equal([9L + 30391 + 0, 8L + 423 + 30391], steps.Select(s => s.PromptTokens.Value!.Value));
        Assert.All(steps, s => Assert.Equal("claude-haiku-4-5-20251001", s.Model));
        Assert.All(steps, s => Assert.Equal(MetricOrigin.ReportedByProvider, s.PromptTokens.Origin));
        Assert.All(steps, s => Assert.Equal("claude assistant.message.usage", s.PromptTokens.Source));

        // The window of the model is only known when result arrives: during the run it is missing, never zero.
        Assert.All(steps, s => Assert.Equal(MetricOrigin.Missing, s.ContextWindow.Origin));
        var order = events.Select(e => e.GetType()).ToList();
        Assert.True(order.IndexOf(typeof(ContextObserved)) < order.IndexOf(typeof(ToolInvoked)));
    }

    [Fact]
    public void The_steps_of_the_skill_fixture_are_two_and_the_second_reads_what_the_first_wrote()
    {
        var (events, _) = ParseFixture("04-skill-plugin.jsonl");

        Assert.Equal([10L + 9792 + 19849, 9L + 483 + 29641], events.OfType<ContextObserved>().Select(s => s.PromptTokens.Value!.Value));
    }

    [Fact]
    public void Usage_is_broken_down_per_model_with_cache_writes_reasoning_cost_basis_and_turns()
    {
        var (events, _) = ParseFixture("01-texto.jsonl");

        var usage = events.OfType<UsageReported>().Single().Usage;
        Assert.Equal(15296, usage.CacheWriteInputTokens.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, usage.CacheWriteInputTokens.Origin);
        Assert.Equal(387, usage.ReasoningOutputTokens.Value);
        Assert.Equal(1, usage.Turns);

        Assert.Equal(["claude-haiku-4-5-20251001", "claude-sonnet-5-5"], usage.PerModel.Select(m => m.Model));
        var haiku = usage.PerModel[0];
        Assert.Equal((906L, 13L, 0L, 0L, 200_000L), (haiku.InputTokens.Value!.Value, haiku.OutputTokens.Value!.Value, haiku.CacheReadInputTokens.Value!.Value, haiku.CacheWriteInputTokens.Value!.Value, haiku.ContextWindow.Value!.Value));
        var sonnet = usage.PerModel[1];

        // As in the totals, the input of a model is its whole prompt: fresh input plus the cache it read and wrote.
        Assert.Equal(2 + 27242 + 15296, sonnet.InputTokens.Value);
        Assert.Equal((391L, 27242L, 15296L, 1_000_000L), (sonnet.OutputTokens.Value!.Value, sonnet.CacheReadInputTokens.Value!.Value, sonnet.CacheWriteInputTokens.Value!.Value, sonnet.ContextWindow.Value!.Value));
        Assert.Equal(0.0705464m, Math.Round(sonnet.CostUsd.Value!.Value, 7));
        Assert.Equal(MetricOrigin.EstimatedByProvider, sonnet.CostUsd.Origin);
        Assert.Equal("list", sonnet.CostBasis);
        Assert.All(usage.PerModel, m => Assert.Equal(MetricOrigin.ReportedByProvider, m.InputTokens.Origin));
    }

    [Fact]
    public void Without_steps_and_with_several_models_the_window_is_missing_rather_than_the_init_model()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);
        parser.Parse("""{"type":"system","subtype":"init","session_id":"s","model":"claude-haiku-4-5-20251001"}""");

        var result = parser.Parse("""{"type":"result","subtype":"success","is_error":false,"session_id":"s","usage":{"input_tokens":5,"output_tokens":1},"modelUsage":{"claude-haiku-4-5-20251001":{"inputTokens":5,"outputTokens":1,"contextWindow":200000},"claude-sonnet-5-5":{"inputTokens":7,"outputTokens":2,"contextWindow":1000000}}}""");

        var usage = Assert.Single(result.OfType<UsageReported>()).Usage;
        Assert.Equal(MetricOrigin.Missing, usage.ContextWindow.Origin);
        Assert.Equal(2, usage.PerModel.Count);
    }

    [Fact]
    public void A_compaction_boundary_is_observed_with_its_trigger_and_the_tokens_it_had_before()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);

        // The documented shape (SDKCompactBoundaryMessage); no recorded run compacted (docs/14 section 13).
        var events = parser.Parse("""{"type":"system","subtype":"compact_boundary","session_id":"s","uuid":"u","compact_metadata":{"trigger":"auto","pre_tokens":154000}}""");

        var compaction = Assert.IsType<CompactionObserved>(Assert.Single(events));
        Assert.Equal("auto", compaction.Trigger);
        Assert.Equal(154_000, compaction.TokensBefore.Value);
        Assert.Equal(MetricOrigin.ReportedByProvider, compaction.TokensBefore.Origin);
    }

    [Fact]
    public void A_compaction_boundary_without_metadata_still_counts_with_the_tokens_missing()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);

        var compaction = Assert.IsType<CompactionObserved>(Assert.Single(parser.Parse("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"rara vez <x>"}}""")));

        Assert.Equal("unknown", compaction.Trigger);
        Assert.Equal(MetricOrigin.Missing, compaction.TokensBefore.Origin);
    }

    [Fact]
    public void The_steps_of_a_subagent_are_not_the_context_of_the_session()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);

        var events = parser.Parse("""{"type":"assistant","parent_tool_use_id":"toolu_1","message":{"id":"msg_sub","model":"claude-haiku-4-5","usage":{"input_tokens":3,"cache_read_input_tokens":100,"output_tokens":1},"content":[{"type":"text","text":"sub"}]}}""");

        Assert.Empty(events.OfType<ContextObserved>());
        Assert.Single(events.OfType<MessageEmitted>());
    }

    [Fact]
    public void Usage_figures_that_did_not_arrive_are_missing_not_unexposed()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);

        var usage = Assert.Single(parser.Parse("""{"type":"result","subtype":"success","is_error":false,"session_id":"s","usage":{}}""").OfType<UsageReported>()).Usage;

        Assert.Equal(MetricOrigin.Missing, usage.InputTokens.Origin);
        Assert.Equal(MetricOrigin.Missing, usage.OutputTokens.Origin);
        Assert.Equal(MetricOrigin.Missing, usage.CachedInputTokens.Origin);
        Assert.Equal(MetricOrigin.Missing, usage.CacheWriteInputTokens.Origin);
        Assert.Equal(MetricOrigin.Missing, usage.CostUsd.Origin);
        Assert.Equal(MetricOrigin.Missing, usage.ContextWindow.Origin);
        Assert.Null(usage.Turns);
    }

    [Fact]
    public void Text_fixture_outcome_succeeds()
    {
        var (events, _) = ParseFixture("01-texto.jsonl");

        var outcome = events.OfType<RunFinished>().Single().Outcome;
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal("OK", outcome.FinalText);
        Assert.Equal("19e487d0-b6fb-4f6f-b1ac-c0c8d9e5e9b5", outcome.SessionId);
        Assert.Null(outcome.StructuredOutputJson);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void Tool_fixture_pairs_tool_invocation_and_completion_without_dumping_content()
    {
        var (events, _) = ParseFixture("02-herramienta.jsonl");

        var invoked = events.OfType<ToolInvoked>().Single();
        Assert.Equal("Write", invoked.Tool);
        Assert.EndsWith("hola.txt", invoked.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", invoked.Summary, StringComparison.Ordinal);

        var completed = events.OfType<ToolCompleted>().Single();
        Assert.Equal("Write", completed.Tool);
        Assert.True(completed.Succeeded);

        var order = events.Select(e => e.GetType()).ToList();
        Assert.True(order.IndexOf(typeof(ToolInvoked)) < order.IndexOf(typeof(ToolCompleted)));
        Assert.Equal("HECHO.", events.OfType<MessageEmitted>().Last().Text);
        Assert.Equal(0.48, events.OfType<QuotaReported>().Single().Windows[0].Utilization.Value);
        Assert.Equal(AgentRunStatus.Succeeded, events.OfType<RunFinished>().Single().Outcome.Status);
    }

    [Fact]
    public void Structured_fixture_returns_the_structured_output_and_no_window_without_init()
    {
        var (events, _) = ParseFixture("03-estructurada.json");

        var outcome = events.OfType<RunFinished>().Single().Outcome;
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal("""{"respuesta":"OK","numero":42}""", outcome.StructuredOutputJson);
        // Single model in modelUsage: used even without a system/init line.
        var usage = events.OfType<UsageReported>().Single().Usage;
        Assert.Equal(200000, usage.ContextWindow.Value);
        Assert.Equal(38964 + 9, usage.InputTokens.Value);
    }

    [Fact]
    public void Garbage_lines_and_unknown_types_are_ignored_and_counted()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);

        Assert.Empty(parser.Parse("esto no es json"));
        Assert.Empty(parser.Parse("{ roto"));
        Assert.Empty(parser.Parse("[1,2,3]"));
        Assert.Empty(parser.Parse(""));
        Assert.Empty(parser.Parse("""{"type":"tipo_futuro","x":1}"""));
        Assert.Empty(parser.Parse("""{"type":"system","subtype":"hook_started"}"""));
        Assert.Empty(parser.Parse("""{"type":"system","subtype":"thinking_tokens","estimated_tokens":5}"""));
        Assert.Empty(parser.Parse("""{"type":"stream_event","event":{}}"""));
        Assert.Equal(3, parser.IgnoredLines);
    }

    [Fact]
    public void Non_allowed_rate_limit_status_also_signals_the_limit()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);
        var events = parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792066800,"unifiedWindows":{"five_hour":{"utilization":1.0,"resetsAt":1792066800}}}}""");

        Assert.IsType<QuotaReported>(events[0]);
        var signal = Assert.IsType<RateLimitSignaled>(events[1]);
        Assert.Equal("rejected", signal.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792066800), signal.ResetsAt);
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"error_max_turns","is_error":false,"session_id":"s"}""", AgentRunStatus.LimitReached)]
    [InlineData("""{"type":"result","subtype":"error_max_budget_usd","is_error":false,"session_id":"s"}""", AgentRunStatus.LimitReached)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"api_error_status":429,"result":"Too many requests"}""", AgentRunStatus.RateLimited)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached|1792066800"}""", AgentRunStatus.RateLimited)]
    [InlineData("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"boom"}""", AgentRunStatus.Failed)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"api_error_status":500,"result":"server"}""", AgentRunStatus.Failed)]
    public void Result_status_mapping(string line, AgentRunStatus expected)
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);

        var outcome = parser.Parse(line).OfType<RunFinished>().Single().Outcome;

        Assert.Equal(expected, outcome.Status);
        if (expected != AgentRunStatus.Succeeded)
        {
            Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        }
    }

    [Fact]
    public void Failed_tool_result_is_reported_as_not_succeeded_and_unknown_id_is_tolerated()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);
        parser.Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la\nsegunda línea"}}]}}""");

        var failed = parser.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"x"}]}}""");
        var unknown = parser.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"zzz","content":"x"}]}}""");

        var f = Assert.IsType<ToolCompleted>(Assert.Single(failed));
        Assert.Equal("Bash", f.Tool);
        Assert.False(f.Succeeded);
        Assert.Equal("unknown", Assert.IsType<ToolCompleted>(Assert.Single(unknown)).Tool);
    }

    [Fact]
    public void Tool_summary_is_one_short_line()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);
        var long200 = new string('x', 200);

        var events = parser.Parse($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"{{{long200}}}\nmás"}}]}}""");

        var summary = Assert.IsType<ToolInvoked>(Assert.Single(events)).Summary;
        Assert.True(summary.Length <= 121);
        Assert.DoesNotContain('\n', summary);
    }
}
