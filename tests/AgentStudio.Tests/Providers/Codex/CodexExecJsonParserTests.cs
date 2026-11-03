using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Providers.Cli.Codex;

namespace AgentStudio.Tests.Providers.Codex;

public sealed class CodexExecJsonParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static List<AgentEvent> ParseAll(CodexExecJsonParser parser, string fixture) =>
        CodexTestSupport.FixtureLines(fixture).SelectMany(l => parser.Parse(l, Now)).ToList();

    [Fact]
    public void Text_fixture_yields_session_message_usage_and_success()
    {
        var events = ParseAll(new CodexExecJsonParser("gpt-x"), "01-texto.jsonl");

        var started = Assert.IsType<SessionStarted>(events[0]);
        Assert.Equal("01a13f53-8d38-7960-8305-91879673182c", started.SessionId);
        Assert.Equal("gpt-x", started.Model);
        Assert.Equal("OK", Assert.Single(events.OfType<MessageEmitted>()).Text);

        var usage = Assert.Single(events.OfType<UsageReported>()).Usage;
        Assert.Equal(18291, usage.InputTokens.Value);
        Assert.Equal(0, usage.CachedInputTokens.Value);
        Assert.Equal(5, usage.OutputTokens.Value);
        Assert.Equal(MetricProvenance.Exact, usage.InputTokens.Provenance);
        Assert.Equal(MetricProvenance.Unavailable, usage.CostUsd.Provenance);
        Assert.Equal(MetricProvenance.Unavailable, usage.ContextWindow.Provenance);

        var outcome = Assert.IsType<RunFinished>(events[^1]).Outcome;
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal("OK", outcome.FinalText);
        Assert.Null(outcome.StructuredOutputJson);
        Assert.Equal("01a13f53-8d38-7960-8305-91879673182c", outcome.SessionId);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void Configuration_warnings_of_type_error_never_fail_the_run()
    {
        var parser = new CodexExecJsonParser();

        var events = ParseAll(parser, "01-texto.jsonl");

        Assert.Equal(AgentRunStatus.Succeeded, events.OfType<RunFinished>().Single().Outcome.Status);
        Assert.Null(parser.LastError);
    }

    [Fact]
    public void Tool_fixture_yields_shell_invocation_and_completion()
    {
        var events = ParseAll(new CodexExecJsonParser(), "02-herramienta.jsonl");

        var invoked = Assert.Single(events.OfType<ToolInvoked>());
        Assert.Equal("shell", invoked.Tool);
        Assert.Contains("hola.txt", invoked.Summary);
        var completed = Assert.Single(events.OfType<ToolCompleted>());
        Assert.Equal("shell", completed.Tool);
        Assert.True(completed.Succeeded);
        Assert.True(events.IndexOf(invoked) < events.IndexOf(completed));

        Assert.Equal(["Crearé `hola.txt` con el texto `hola`.\n", "HECHO"], events.OfType<MessageEmitted>().Select(m => m.Text));
        var usage = events.OfType<UsageReported>().Single().Usage;
        Assert.Equal(18816, usage.CachedInputTokens.Value);
        Assert.Equal(370, usage.OutputTokens.Value);
        Assert.Equal("HECHO", events.OfType<RunFinished>().Single().Outcome.FinalText);
    }

    [Fact]
    public void Structured_fixture_returns_the_json_of_the_last_message()
    {
        var events = ParseAll(new CodexExecJsonParser(expectStructuredOutput: true), "03-estructurada.jsonl");

        var outcome = events.OfType<RunFinished>().Single().Outcome;
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal("{\"respuesta\":\"OK\",\"numero\":42}", outcome.StructuredOutputJson);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void Tool_fixture_reports_the_cache_writes_and_the_reasoning_of_the_thread()
    {
        var usage = ParseAll(new CodexExecJsonParser(), "02-herramienta.jsonl").OfType<UsageReported>().Single().Usage;

        Assert.Equal((38283L, 18816L, 0L, 370L, 210L), (usage.InputTokens.Value!.Value, usage.CachedInputTokens.Value!.Value, usage.CacheWriteInputTokens.Value!.Value, usage.OutputTokens.Value!.Value, usage.ReasoningOutputTokens.Value!.Value));
        Assert.All(
            [usage.InputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens, usage.OutputTokens, usage.ReasoningOutputTokens],
            figure => Assert.Equal(MetricOrigin.ReportedByProvider, figure.Origin));

        // turn.completed.usage is the total of the thread (codex-rs 0.160.0), not the usage of the turn (docs/14 D-02).
        Assert.Contains("acumulado del hilo", usage.InputTokens.Source, StringComparison.Ordinal);
        Assert.Empty(usage.PerModel);
        Assert.Null(usage.Turns);
        Assert.Equal(MetricOrigin.NotExposed, usage.CostUsd.Origin);
        Assert.Equal(MetricOrigin.NotExposed, usage.ContextWindow.Origin);
    }

    [Fact]
    public void A_usage_that_is_all_zero_is_missing_because_no_usage_notification_arrived()
    {
        // codex-rs 0.160.0 writes Usage::default() when no token-usage notification arrived: zero is not a figure.
        var usage = new CodexExecJsonParser().Parse("""{"type":"turn.completed","usage":{"input_tokens":0,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":0,"reasoning_output_tokens":0}}""", Now)
            .OfType<UsageReported>().Single().Usage;

        Assert.All(
            [usage.InputTokens, usage.CachedInputTokens, usage.CacheWriteInputTokens, usage.OutputTokens, usage.ReasoningOutputTokens],
            figure => Assert.Equal((MetricOrigin.Missing, false), (figure.Origin, figure.HasValue)));
    }

    [Fact]
    public void A_figure_that_did_not_come_is_missing_and_the_others_stay()
    {
        var usage = new CodexExecJsonParser().Parse("""{"type":"turn.completed","usage":{"input_tokens":120,"output_tokens":7}}""", Now)
            .OfType<UsageReported>().Single().Usage;

        Assert.Equal((120L, 7L), (usage.InputTokens.Value!.Value, usage.OutputTokens.Value!.Value));
        Assert.Equal(MetricOrigin.Missing, usage.CachedInputTokens.Origin);
        Assert.Equal(MetricOrigin.Missing, usage.CacheWriteInputTokens.Origin);
        Assert.Equal(MetricOrigin.Missing, usage.ReasoningOutputTokens.Origin);
    }

    [Fact]
    public void Structured_output_requested_but_not_json_leaves_it_null_with_an_error()
    {
        var events = ParseAll(new CodexExecJsonParser(expectStructuredOutput: true), "01-texto.jsonl");

        var outcome = events.OfType<RunFinished>().Single().Outcome;
        Assert.Null(outcome.StructuredOutputJson);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void File_change_and_mcp_items_map_to_tool_events()
    {
        var parser = new CodexExecJsonParser();
        var events = new List<AgentEvent>();
        events.AddRange(parser.Parse("""{"type":"item.completed","item":{"id":"i1","type":"file_change","changes":[{"path":"a.txt","kind":"add"}],"status":"completed"}}""", Now));
        events.AddRange(parser.Parse("""{"type":"item.started","item":{"id":"i2","type":"mcp_tool_call","server":"srv","tool":"t","status":"in_progress"}}""", Now));
        events.AddRange(parser.Parse("""{"type":"item.completed","item":{"id":"i2","type":"mcp_tool_call","server":"srv","tool":"t","status":"failed"}}""", Now));

        Assert.Collection(
            events,
            e => Assert.Equal("a.txt", Assert.IsType<ToolInvoked>(e).Summary),
            e => Assert.True(Assert.IsType<ToolCompleted>(e).Succeeded),
            e => Assert.Equal("srv/t", Assert.IsType<ToolInvoked>(e).Summary),
            e => Assert.False(Assert.IsType<ToolCompleted>(e).Succeeded));
    }

    [Fact]
    public void Failed_command_reports_unsuccessful_completion()
    {
        var parser = new CodexExecJsonParser();
        var events = parser.Parse("""{"type":"item.completed","item":{"id":"i1","type":"command_execution","command":"x","exit_code":1,"status":"failed"}}""", Now);

        Assert.False(events.OfType<ToolCompleted>().Single().Succeeded);
    }

    [Fact]
    public void Turn_failed_fails_the_run()
    {
        var parser = new CodexExecJsonParser();
        var events = parser.Parse("""{"type":"turn.failed","error":{"message":"boom"}}""", Now);

        var outcome = events.OfType<RunFinished>().Single().Outcome;
        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal("boom", outcome.Error);
        Assert.True(parser.IsFinished);
    }

    [Theory]
    [InlineData("You've hit your usage limit. Try again later.")]
    [InlineData("exceeded retry limit, last status: 429 Too Many Requests")]
    public void Usage_limit_messages_become_rate_limited(string message)
    {
        var parser = new CodexExecJsonParser();
        var events = parser.Parse("{\"type\":\"turn.failed\",\"error\":{\"message\":\"" + message + "\"}}", Now);

        Assert.Equal(AgentRunStatus.RateLimited, events.OfType<RunFinished>().Single().Outcome.Status);
        Assert.Single(events.OfType<RateLimitSignaled>());
    }

    [Fact]
    public void Top_level_error_is_remembered_but_does_not_finish_the_run()
    {
        var parser = new CodexExecJsonParser();
        var events = parser.Parse("""{"type":"error","message":"Reconnecting... 1/5"}""", Now);

        Assert.Empty(events);
        Assert.False(parser.IsFinished);
        Assert.Equal("Reconnecting... 1/5", parser.LastError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{broken")]
    [InlineData("""{"type":"turn.started"}""")]
    [InlineData("""{"type":"something.new","x":1}""")]
    [InlineData("""{"type":"item.completed","item":{"id":"r","type":"reasoning","text":"hmm"}}""")]
    [InlineData("[1,2]")]
    public void Unknown_or_malformed_lines_are_ignored(string line)
    {
        Assert.Empty(new CodexExecJsonParser().Parse(line, Now));
    }

    [Fact]
    public void Long_commands_are_summarised()
    {
        var command = "echo " + new string('x', 500) + "\n  second";
        var json = System.Text.Json.JsonSerializer.Serialize(new { type = "item.started", item = new { id = "c", type = "command_execution", command } });

        var invoked = new CodexExecJsonParser().Parse(json, Now).OfType<ToolInvoked>().Single();

        Assert.True(invoked.Summary.Length <= 201);
        Assert.DoesNotContain('\n', invoked.Summary);
    }
}
