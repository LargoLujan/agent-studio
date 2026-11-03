using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Providers.Cli;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Providers.Cli.Gemini;
using AgentStudio.Tests.Providers.Gemini;
using ClaudeSupport = AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;
using CodexSupport = AgentStudio.Tests.Providers.Codex.CodexTestSupport;

namespace AgentStudio.Tests.Providers;

/// <summary>Hardening found by the Phase 2 review: argv validation, malformed numbers, kill on failure, status rules.</summary>
public sealed class ProviderHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Test doubles ----

    private sealed class Locator(string? path) : IExecutableLocator
    {
        public string? Find(string command) => path;
    }

    /// <summary>Replays the given lines and then ends with <paramref name="exit"/> (or throws while reading when asked).</summary>
    private sealed class Session : IProcessSession
    {
        private readonly Channel<ProcessOutputLine> _lines = Channel.CreateUnbounded<ProcessOutputLine>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _throwWhileReading;

        public Session(IEnumerable<ProcessOutputLine> lines, int exit, bool throwWhileReading = false, bool holdOpen = false)
        {
            _throwWhileReading = throwWhileReading;
            foreach (var line in lines)
            {
                _lines.Writer.TryWrite(line);
            }

            if (!holdOpen)
            {
                End(exit);
            }
        }

        public int Kills { get; private set; }

        public int ProcessId => 1;

        public IAsyncEnumerable<ProcessOutputLine> Output => Read();

        public Task<int> Exited => _exited.Task;

        public void End(int exit)
        {
            _lines.Writer.TryComplete();
            _exited.TrySetResult(exit);
        }

        private async IAsyncEnumerable<ProcessOutputLine> Read([EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_throwWhileReading)
            {
                throw new InvalidOperationException("boom while reading");
            }

            await foreach (var line in _lines.Reader.ReadAllAsync(ct))
            {
                yield return line;
            }
        }

        public Task KillAsync()
        {
            Kills++;
            End(-1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            End(-1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Runner(Session session) : IStreamingProcessRunner
    {
        public ProcessSpec? Spec { get; private set; }

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Spec = spec;
            return Task.FromResult<IProcessSession>(session);
        }
    }

    private static IEnumerable<ProcessOutputLine> Out(params string[] lines) =>
        lines.Select(l => new ProcessOutputLine(ProcessStream.StandardOutput, l, Now));

    private static ProcessOutputLine Err(string text) => new(ProcessStream.StandardError, text, Now);

    private static async Task<List<AgentEvent>> DrainAsync(IAgentRun run)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in run.Events.WithCancellation(Token))
        {
            events.Add(e);
        }

        return events;
    }

    private static ClaudeCodeAgentProvider ClaudeProvider(Session session, string path = "C:/bin/claude.exe") =>
        new(new Locator(path), new Runner(session), TimeProvider.System);

    private static CodexCliAgentProvider CodexProvider(Session session) =>
        new(new Locator("C:/bin/codex.exe"), new Runner(session), TimeProvider.System);

    private static GeminiCliAgentProvider GeminiProvider(Session session) =>
        new(new Locator("C:/bin/gemini.exe"), new Runner(session));

    // ---- 1. argv validation ----

    [Theory]
    [InlineData("--dangerously-bypass-approvals-and-sandbox")]
    [InlineData("-s")]
    [InlineData("id with space")]
    [InlineData("a;b")]
    public void Codex_rejects_unsafe_resume_ids(string id)
    {
        Assert.Throws<ArgumentException>(() => CodexExecArguments.Build(CodexSupport.Request(resume: id), null, true));
    }

    [Fact]
    public void Codex_accepts_a_uuid_resume_id_and_rejects_bad_models_and_directories()
    {
        var ok = CodexExecArguments.Build(CodexSupport.Request(resume: "01a13f53-e4b3-7071-8396-4afc4d6c0323"), null, true);
        Assert.Contains("resume", ok);

        Assert.Throws<ArgumentException>(() => CodexExecArguments.Build(CodexSupport.Request(model: "--oss"), null, true));
        Assert.Throws<ArgumentException>(() => CodexExecArguments.Build(CodexSupport.Request(workingDirectory: "-C"), null, true));
        Assert.Throws<ArgumentException>(() => CodexExecArguments.Build(CodexSupport.Request(workingDirectory: "relative"), null, true));
        Assert.Throws<ArgumentException>(() =>
            CodexExecArguments.Build(CodexSupport.Request(CodexSupport.Permissions(writes: true, extraDirectories: "--add-dir")), null, true));
    }

    [Theory]
    [InlineData("gpt-5.1-codex", true)]
    [InlineData("gemini-2.5-pro", true)]
    [InlineData("models/gemini-2.5-flash", true)]
    [InlineData("-m", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void Model_guard_follows_the_documented_rule(string model, bool valid)
    {
        if (valid)
        {
            Assert.Equal(model, CliArgumentGuard.Model(model, "p"));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => CliArgumentGuard.Model(model, "p"));
        }
    }

    [Fact]
    public void Session_ids_longer_than_the_limit_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => CliArgumentGuard.SessionId(new string('a', CliArgumentGuard.MaxTokenLength + 1), "p"));
        Assert.Equal("abc", CliArgumentGuard.SessionId("abc", "p"));
    }

    // ---- 2. Claude schema in argv ----

    [Fact]
    public async Task Claude_schema_with_a_cmd_shim_fails_clearly_before_launching()
    {
        var session = new Session([], 0);
        var runner = new Runner(session);
        var provider = new ClaudeCodeAgentProvider(new Locator("C:/npm/claude.cmd"), runner, TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.StartAsync(ClaudeSupport.Request(schema: """{"type":"object"}"""), Token));

        Assert.Contains("shim", error.Message, StringComparison.Ordinal);
        Assert.Null(runner.Spec);
    }

    // ---- 3. malformed numbers never throw ----

    [Theory]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"x","total_cost_usd":1e999,"usage":{"input_tokens":99999999999999999999999,"output_tokens":"many"},"api_error_status":99999999999}""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"x","total_cost_usd":"free","usage":{"input_tokens":9223372036854775807,"cache_read_input_tokens":9223372036854775807},"modelUsage":{"m":{"contextWindow":1e30}}}""")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":-5,"unifiedWindows":{"five_hour":{"utilization":1e999,"resetsAt":9223372036854775807},"w":{"utilization":"x","resetsAt":"soon"}}}}""")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":99999999999999,"unifiedWindows":{"five_hour":{"utilization":0.5,"resetsAt":-9223372036854775808}}}}""")]
    public void Claude_parser_survives_malformed_numeric_fields(string line)
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);

        var events = parser.Parse(line);

        Assert.NotNull(events);
    }

    [Fact]
    public void Claude_out_of_range_reset_is_reported_as_unknown_and_overflowing_input_tokens_as_unavailable()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);

        var limit = parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":-5}}""");
        Assert.Null(Assert.Single(limit.OfType<RateLimitSignaled>()).ResetsAt);

        var result = parser.Parse("""{"type":"result","subtype":"success","usage":{"input_tokens":9223372036854775807,"cache_read_input_tokens":9223372036854775807,"output_tokens":3}}""");
        var usage = Assert.Single(result.OfType<UsageReported>()).Usage;
        Assert.Equal(MetricProvenance.Unavailable, usage.InputTokens.Provenance);
        Assert.Equal(3, usage.OutputTokens.Value);
    }

    [Theory]
    [InlineData("""{"type":"item.completed","item":{"id":"1","type":"command_execution","command":"x","exit_code":99999999999999}}""")]
    [InlineData("""{"type":"item.completed","item":{"id":"2","type":"command_execution","command":"x","exit_code":"zero"}}""")]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":1e999,"output_tokens":"a","cached_input_tokens":99999999999999999999}}""")]
    public void Codex_parser_survives_malformed_numeric_fields(string line)
    {
        var parser = new CodexExecJsonParser();

        var events = parser.Parse(line, Now);

        Assert.NotNull(events);
    }

    [Theory]
    [InlineData("""{"type":"result","status":"success","stats":{"input_tokens":99999999999999999999,"output_tokens":"x","cached":1e999}}""")]
    [InlineData("""{"type":"init","timestamp":"no-fecha","session_id":"s","model":5}""")]
    public void Gemini_parser_survives_malformed_numeric_fields(string line)
    {
        var parser = new GeminiStreamJsonParser();

        var events = parser.Parse(line, Now);

        Assert.NotNull(events);
    }

    // ---- 3b. every pump failure kills the CLI ----

    [Fact]
    public async Task Claude_pump_failure_fails_the_run_and_kills_the_cli()
    {
        var session = new Session([], 0, throwWhileReading: true);
        await using var run = await ClaudeProvider(session).StartAsync(ClaudeSupport.Request(), Token);

        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await DrainAsync(run);

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(1, session.Kills);
    }

    [Fact]
    public async Task Codex_pump_failure_fails_the_run_and_kills_the_cli()
    {
        var session = new Session([], 0, throwWhileReading: true);
        await using var run = await CodexProvider(session).StartAsync(CodexSupport.Request(), Token);

        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await DrainAsync(run);

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(1, session.Kills);
    }

    [Fact]
    public async Task Gemini_pump_failure_fails_the_run_and_kills_the_cli()
    {
        var session = new Session([], 0, throwWhileReading: true);
        await using var run = await GeminiProvider(session).StartAsync(GeminiTestData.Request(), Token);

        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await DrainAsync(run);

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(1, session.Kills);
    }

    // ---- 5. consistent status mapping ----

    [Fact]
    public async Task Claude_timeout_without_result_is_limit_reached()
    {
        var session = new Session([], 0, holdOpen: true);
        var request = ClaudeSupport.Request(budget: new(4, TimeSpan.FromMilliseconds(50)));
        await using var run = await ClaudeProvider(session).StartAsync(request, Token);

        await Task.Delay(150, Token);
        session.End(-1); // what the runner reports when the timeout killed the process
        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(AgentRunStatus.LimitReached, outcome.Status);
        Assert.Equal(-1, outcome.ExitCode);
    }

    [Fact]
    public async Task Claude_requested_schema_without_structured_output_fails_and_keeps_the_text()
    {
        var session = new Session(Out(ClaudeSupport.FixtureLines("01-texto.jsonl")), 0);
        await using var run = await ClaudeProvider(session).StartAsync(ClaudeSupport.Request(schema: """{"type":"object"}"""), Token);

        var events = await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal("OK", outcome.FinalText);
        Assert.NotNull(outcome.Error);
        Assert.Equal(outcome, events.OfType<RunFinished>().Single().Outcome);
    }

    [Fact]
    public async Task Codex_requested_schema_without_valid_json_fails_and_keeps_the_text()
    {
        var session = new Session(Out(CodexSupport.FixtureLines("01-texto.jsonl")), 0);
        await using var run = await CodexProvider(session).StartAsync(CodexSupport.Request(schema: """{"type":"object"}"""), Token);

        await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.False(string.IsNullOrEmpty(outcome.FinalText));
        Assert.Null(outcome.StructuredOutputJson);
        Assert.Contains("not valid JSON", outcome.Error, StringComparison.Ordinal);
    }

    private const string GeminiHeader = """{"type":"init","session_id":"s-1","model":"gemini-2.5-pro"}""";

    private static string GeminiMessage(string text) =>
        "{\"type\":\"message\",\"role\":\"assistant\",\"content\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}";

    private const string GeminiOk = """{"type":"result","status":"success","stats":{"input_tokens":1,"output_tokens":1}}""";

    [Fact]
    public async Task Gemini_requested_schema_without_valid_json_fails_and_keeps_the_text()
    {
        var session = new Session(Out(GeminiHeader, GeminiMessage("hola"), GeminiOk), 0);
        var request = GeminiTestData.Request() with { OutputJsonSchema = """{"type":"object"}""" };
        await using var run = await GeminiProvider(session).StartAsync(request, Token);

        await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal("hola", outcome.FinalText);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task Gemini_requested_schema_with_valid_json_succeeds_with_structured_output()
    {
        var session = new Session(Out(GeminiHeader, GeminiMessage("{\"a\":1}"), GeminiOk), 0);
        var request = GeminiTestData.Request() with { OutputJsonSchema = """{"type":"object"}""" };
        await using var run = await GeminiProvider(session).StartAsync(request, Token);

        await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal("{\"a\":1}", outcome.StructuredOutputJson);
    }

    // ---- 10. Gemini rate limiting ----

    [Fact]
    public async Task Gemini_quota_error_event_maps_to_rate_limited_with_a_signal()
    {
        var error = """{"type":"error","severity":"error","code":"RESOURCE_EXHAUSTED","message":"quota exceeded"}""";
        var session = new Session(Out(GeminiHeader, error), 1);
        await using var run = await GeminiProvider(session).StartAsync(GeminiTestData.Request(), Token);

        var events = await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.RateLimited, outcome.Status);
        Assert.Single(events.OfType<RateLimitSignaled>());
    }

    [Fact]
    public async Task Gemini_quota_text_on_stderr_maps_to_rate_limited_with_a_single_signal()
    {
        var session = new Session([Err("{\"error\":{\"code\":429,\"message\":\"x\"}}"), Err("status: RESOURCE_EXHAUSTED")], 1);
        await using var run = await GeminiProvider(session).StartAsync(GeminiTestData.Request(), Token);

        var events = await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.RateLimited, outcome.Status);
        Assert.Single(events.OfType<RateLimitSignaled>());
    }

    [Fact]
    public async Task Gemini_ordinary_failure_stays_failed()
    {
        var session = new Session([Err("something else broke")], 1);
        await using var run = await GeminiProvider(session).StartAsync(GeminiTestData.Request(), Token);

        await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
    }

    // ---- 6. temporary files ----

    [Fact]
    public async Task Claude_instructions_file_is_removed_when_argument_building_fails()
    {
        var marker = "instrucciones-" + Guid.NewGuid().ToString("N");
        var provider = ClaudeProvider(new Session([], 0));

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.StartAsync(ClaudeSupport.Request(instructions: marker, resume: "--bad"), Token));

        var leaked = Directory.Exists(RunTempFolder.BaseDirectory)
            ? Directory.EnumerateFiles(RunTempFolder.BaseDirectory, "instructions.md", SearchOption.AllDirectories).Where(f => TryRead(f) == marker).ToList()
            : [];
        Assert.Empty(leaked);
    }

    [Fact]
    public async Task Claude_instructions_live_under_the_per_user_folder_and_are_removed_when_the_run_ends()
    {
        var marker = "instrucciones-" + Guid.NewGuid().ToString("N");
        var session = new Session([], 0, holdOpen: true);
        var runner = new Runner(session);
        var provider = new ClaudeCodeAgentProvider(new Locator("C:/bin/claude.exe"), runner, TimeProvider.System);

        await using var run = await provider.StartAsync(ClaudeSupport.Request(instructions: marker), Token);
        var args = runner.Spec!.Arguments.ToList();
        var file = args[args.IndexOf("--append-system-prompt-file") + 1];

        Assert.StartsWith(RunTempFolder.BaseDirectory, file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(marker, File.ReadAllText(file));

        session.End(0);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await Task.Delay(300, Token);

        // Deleted when the run completes, not only on dispose.
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Instruction_files_are_never_overwritten()
    {
        var dir = RunTempFolder.Create();
        try
        {
            var path = Path.Combine(dir, "x.md");
            await RunTempFolder.WriteNewFileAsync(path, "a", Token);
            await Assert.ThrowsAsync<IOException>(() => RunTempFolder.WriteNewFileAsync(path, "b", Token));
        }
        finally
        {
            RunTempFolder.DeleteQuietly(dir);
        }
    }

    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\n\n")]
    public void Trailing_line_feeds_do_not_pass_the_validation_regexes(string value)
    {
        Assert.Throws<ArgumentException>(() => CliArgumentGuard.SessionId(value, "p"));
        Assert.Throws<ArgumentException>(() => CliArgumentGuard.Model(value, "p"));
        Assert.Throws<ArgumentException>(() => GeminiArguments.Build(GeminiTestData.Request(resume: value)));
    }

    [Fact]
    public async Task Codex_schema_directory_is_removed_when_argument_building_fails()
    {
        var provider = CodexProvider(new Session([], 0));
        var marker = "schema-" + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.StartAsync(CodexSupport.Request(schema: "{\"title\":\"" + marker + "\"}", resume: "--bad"), Token));

        var leaked = Directory.Exists(RunTempFolder.BaseDirectory)
            ? Directory.EnumerateFiles(RunTempFolder.BaseDirectory, "schema.json", SearchOption.AllDirectories)
                .Where(f => TryRead(f)?.Contains(marker, StringComparison.Ordinal) == true).ToList()
            : [];
        Assert.Empty(leaked);
    }

    // ---- second pass: tool rules, sweep, Gemini false positives ----

    [Theory]
    [InlineData("Write\0")]
    [InlineData("Wri\nte")]
    [InlineData("Write\r")]
    [InlineData("-Write")]
    [InlineData("Bash(git\nlog)")]
    [InlineData("Read Write")]
    [InlineData("")]
    public void Unsafe_tool_rules_are_rejected_in_allowed_and_denied_lists(string tool)
    {
        if (tool.Length == 0)
        {
            // Empty entries are simply skipped.
            Assert.NotEmpty(ClaudeCodeArguments.Build(ClaudeSupport.Request(ClaudeSupport.Permissions(allowed: [tool])), "11111111-1111-1111-1111-111111111111", null));
            return;
        }

        var session = "11111111-1111-1111-1111-111111111111";
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(ClaudeSupport.Request(ClaudeSupport.Permissions(allowed: [tool])), session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(ClaudeSupport.Request(ClaudeSupport.Permissions(denied: [tool])), session, null));
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Bash(git log:*)")]
    [InlineData("mcp__server__tool")]
    [InlineData("Read(./src/**)")]
    public void Claude_rule_syntax_is_accepted(string tool)
    {
        Assert.Equal(tool, CliArgumentGuard.ToolName(tool, "p"));
    }

    [Fact]
    public void Stale_run_folders_are_swept_and_fresh_ones_kept()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "agentstudio-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var old = Path.Combine(baseDirectory, "old");
            var fresh = Path.Combine(baseDirectory, "fresh");
            Directory.CreateDirectory(old);
            Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(old, "schema.json"), "{}");
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));

            RunTempFolder.SweepStale(baseDirectory, TimeSpan.FromDays(7), DateTimeOffset.UtcNow, stopAt: Path.GetTempPath());

            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(fresh));
        }
        finally
        {
            Directory.Delete(baseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Claude_temp_folder_is_gone_by_the_time_completion_is_published()
    {
        var session = new Session([], 0, holdOpen: true);
        var runner = new Runner(session);
        var provider = new ClaudeCodeAgentProvider(new Locator("C:/bin/claude.exe"), runner, TimeProvider.System);
        await using var run = await provider.StartAsync(ClaudeSupport.Request(instructions: "x"), Token);
        var args = runner.Spec!.Arguments.ToList();
        var file = args[args.IndexOf("--append-system-prompt-file") + 1];

        session.End(0);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Gemini_stderr_that_merely_contains_the_word_quota_in_a_path_stays_failed()
    {
        var session = new Session([Err(@"Error: ENOENT C:\quota\config.json")], 1);
        await using var run = await GeminiProvider(session).StartAsync(GeminiTestData.Request(), Token);

        var events = await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Empty(events.OfType<RateLimitSignaled>());
    }

    [Theory]
    [InlineData(@"Error: ENOENT C:\quota\config.json")]
    [InlineData(@"Error: ENOENT C:\RESOURCE_EXHAUSTED\config.json")]
    [InlineData(@"Error: ENOENT C:\rate-limited\config.json")]
    [InlineData("Not rate limited; invalid input")]
    public void Ambiguous_text_is_not_a_rate_limit(string text)
    {
        Assert.False(GeminiStreamJsonParser.IsRateLimitMessage(text));
        Assert.Empty(new GeminiStreamJsonParser().ObserveStderr(text, Now));
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":429,\"message\":\"x\"}}")]
    [InlineData("status: RESOURCE_EXHAUSTED")]
    [InlineData("You exceeded your current quota, please check your plan")]
    [InlineData("Quota exceeded for quota metric 'Generate Content API requests'")]
    public void Unambiguous_provider_formats_are_a_rate_limit(string text)
    {
        Assert.True(GeminiStreamJsonParser.IsRateLimitMessage(text));
    }

    [Theory]
    [InlineData("""{"type":"error","severity":"error","code":429,"message":"boom"}""", true)]
    [InlineData("""{"type":"error","severity":"error","status":"RESOURCE_EXHAUSTED","message":"boom"}""", true)]
    [InlineData("""{"type":"error","severity":"error","code":500,"message":"quota"}""", false)]
    public void Structured_error_codes_decide_the_signal(string line, bool expected)
    {
        var events = new GeminiStreamJsonParser().Parse(line, Now);

        Assert.Equal(expected, events.OfType<RateLimitSignaled>().Any());
    }

    [Fact]
    public void A_multiline_schema_is_compacted_and_an_invalid_one_is_rejected()
    {
        var session = "11111111-1111-1111-1111-111111111111";
        var pretty = "{\r\n  \"type\": \"object\",\r\n  \"properties\": {\"a\": {\"type\": \"string\"}}\r\n}";

        var args = ClaudeCodeArguments.Build(ClaudeSupport.Request(schema: pretty), session, null);

        var value = args[args.ToList().IndexOf("--json-schema") + 1];
        Assert.Equal("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}", value);
        Assert.DoesNotContain(args, a => a.Contains('\n') || a.Contains('\r'));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(ClaudeSupport.Request(schema: "{not json"), session, null));
    }

    [Fact]
    public void A_base_that_is_a_junction_is_not_swept()
    {
        var parent = Path.Combine(Path.GetTempPath(), "agentstudio-sweepj-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(parent, "real");
        var link = Path.Combine(parent, "link");
        try
        {
            var old = Path.Combine(real, "old");
            Directory.CreateDirectory(old);
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using (var process = System.Diagnostics.Process.Start(info)!)
            {
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, "mklink /J failed");
            }

            RunTempFolder.SweepStale(link, TimeSpan.FromDays(7), DateTimeOffset.UtcNow, stopAt: parent);

            Assert.True(Directory.Exists(old));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(parent, recursive: true);
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
