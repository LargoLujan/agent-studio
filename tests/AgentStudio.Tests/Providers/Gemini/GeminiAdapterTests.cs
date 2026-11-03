using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli.Gemini;
using static AgentStudio.Tests.Providers.Gemini.GeminiTestData;

namespace AgentStudio.Tests.Providers.Gemini;

public sealed class GeminiAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentstudio-gemini-" + Guid.NewGuid().ToString("N"));

    public GeminiAdapterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- Launch resolver ----

    [Fact]
    public void Resolver_returns_null_when_gemini_is_not_installed()
    {
        Assert.Null(new GeminiLaunchResolver(new FakeLocator([])).Resolve());
    }

    [Fact]
    public void Resolver_runs_node_on_the_bundle_next_to_the_npm_shim()
    {
        var bundle = Path.Combine(_root, "node_modules", "@google", "gemini-cli", "bundle", "gemini.js");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        File.WriteAllText(bundle, "//");
        var shim = Path.Combine(_root, "gemini.cmd");
        var locator = new FakeLocator(new() { ["gemini"] = shim, ["node"] = "C:/node/node.exe" });

        var launch = new GeminiLaunchResolver(locator).Resolve();

        Assert.NotNull(launch);
        Assert.Equal("C:/node/node.exe", launch.FileName);
        Assert.Equal([bundle], launch.LeadingArguments);
    }

    [Fact]
    public void Resolver_falls_back_to_the_shim_without_node_or_bundle()
    {
        var shim = Path.Combine(_root, "gemini.cmd");

        var withoutBundle = new GeminiLaunchResolver(new FakeLocator(new() { ["gemini"] = shim, ["node"] = "C:/node/node.exe" })).Resolve();
        Assert.Equal(shim, withoutBundle!.FileName);
        Assert.Empty(withoutBundle.LeadingArguments);

        var bundle = Path.Combine(_root, "node_modules", "@google", "gemini-cli", "bundle", "gemini.js");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        File.WriteAllText(bundle, "//");
        var withoutNode = new GeminiLaunchResolver(new FakeLocator(new() { ["gemini"] = shim })).Resolve();
        Assert.Equal(shim, withoutNode!.FileName);
    }

    [Fact]
    public void Resolver_uses_a_native_executable_as_is()
    {
        var launch = new GeminiLaunchResolver(new FakeLocator(new() { ["gemini"] = "C:/bin/gemini.exe", ["node"] = "C:/node/node.exe" })).Resolve();
        Assert.Equal("C:/bin/gemini.exe", launch!.FileName);
        Assert.Empty(launch.LeadingArguments);
    }

    // ---- Arguments ----

    [Fact]
    public void Arguments_never_carry_the_prompt_and_always_skip_trust()
    {
        var args = GeminiArguments.Build(Request(prompt: "SECRETO-PROMPT", instructions: "SECRETO-SISTEMA"));

        Assert.DoesNotContain(args, a => a.Contains("SECRETO", StringComparison.Ordinal));
        Assert.Equal(["-p", "", "-o", "stream-json", "--skip-trust", "--approval-mode", "plan"], args);
        var stdin = GeminiArguments.BuildStandardInput(Request(prompt: "SECRETO-PROMPT", instructions: "SECRETO-SISTEMA"));
        Assert.Contains("SECRETO-PROMPT", stdin, StringComparison.Ordinal);
        Assert.Contains("SECRETO-SISTEMA", stdin, StringComparison.Ordinal);
        Assert.True(stdin.IndexOf("SECRETO-SISTEMA", StringComparison.Ordinal) < stdin.IndexOf("SECRETO-PROMPT", StringComparison.Ordinal));
    }

    [Fact]
    public void Arguments_use_auto_edit_with_writes_and_never_yolo()
    {
        Assert.Equal("auto_edit", ValueAfter(GeminiArguments.Build(Request(allowWrites: true)), "--approval-mode"));
        Assert.Equal("plan", ValueAfter(GeminiArguments.Build(Request(allowWrites: false)), "--approval-mode"));
        Assert.DoesNotContain("yolo", GeminiArguments.Build(Request(allowWrites: true)));
    }

    [Fact]
    public void Arguments_add_model_directories_and_resume_when_present()
    {
        var args = GeminiArguments.Build(Request(model: "gemini-2.5-flash", resume: "11111111-2222-3333-4444-555555555555", extraDirectories: ["C:/a", "C:/b"]));

        Assert.Equal("gemini-2.5-flash", ValueAfter(args, "-m"));
        Assert.Equal("11111111-2222-3333-4444-555555555555", ValueAfter(args, "-r"));
        Assert.Equal(2, args.Count(a => a == "--include-directories"));
        Assert.Contains("C:/a", args);
        Assert.Contains("C:/b", args);
        Assert.DoesNotContain("-m", GeminiArguments.Build(Request()));
        Assert.DoesNotContain("-r", GeminiArguments.Build(Request()));
    }

    [Fact]
    public void Arguments_refuse_to_resume_with_an_unsafe_session_id()
    {
        Assert.Throws<ArgumentException>(() => GeminiArguments.Build(Request(resume: "--approval-mode=yolo")));
        Assert.Throws<ArgumentException>(() => GeminiArguments.Build(Request(resume: "a b")));
        Assert.Throws<ArgumentException>(() => GeminiArguments.Build(Request(model: "--yolo")));
        Assert.Throws<ArgumentException>(() => GeminiArguments.Build(Request(extraDirectories: ["--include-directories"])));
        Assert.Throws<ArgumentException>(() => GeminiArguments.Build(Request(extraDirectories: ["relative"])));
    }

    [Fact]
    public void Standard_input_is_just_the_prompt_without_instructions()
    {
        Assert.Equal("Hola", GeminiArguments.BuildStandardInput(Request(prompt: "Hola", instructions: null)));
    }

    private static string ValueAfter(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    // ---- Parser (synthetic fixtures: derived from the documentation, not captured) ----

    [Fact]
    public void Parser_maps_a_successful_synthetic_session()
    {
        var parser = new GeminiStreamJsonParser();
        var events = ReadSynthetic("01-exito.jsonl")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(l => parser.Parse(l, DateTimeOffset.UnixEpoch))
            .ToList();

        var started = Assert.IsType<SessionStarted>(events[0]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", started.SessionId);
        Assert.Equal("gemini-2.5-pro", started.Model);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero), started.At);

        var messages = events.OfType<MessageEmitted>().ToList();
        Assert.Equal(["Voy a crear ", "el fichero.", "Aviso de Gemini CLI: Loop detection disabled", " HECHO"], messages.Select(m => m.Text));
        Assert.Equal([true, true, false, true], messages.Select(m => m.IsPartial));

        var invoked = Assert.Single(events.OfType<ToolInvoked>());
        Assert.Equal("write_file", invoked.Tool);
        Assert.Contains("hola.txt", invoked.Summary, StringComparison.Ordinal);
        var completed = Assert.Single(events.OfType<ToolCompleted>());
        Assert.Equal("write_file", completed.Tool);
        Assert.True(completed.Succeeded);

        var usage = Assert.Single(events.OfType<UsageReported>()).Usage;
        Assert.Equal(100, usage.InputTokens.Value);
        Assert.Equal(50, usage.OutputTokens.Value);
        Assert.Equal(20, usage.CachedInputTokens.Value);
        Assert.Equal(MetricProvenance.Exact, usage.InputTokens.Provenance);
        Assert.Equal(MetricProvenance.Unavailable, usage.CostUsd.Provenance);

        Assert.True(parser.ResultSeen);
        Assert.True(parser.ResultSucceeded);
        Assert.Equal("Voy a crear el fichero. HECHO", parser.FinalText);
        Assert.DoesNotContain(events, e => e is RunFinished);
    }

    [Fact]
    public void Parser_reports_errors_and_ignores_garbage()
    {
        var parser = new GeminiStreamJsonParser();
        var events = ReadSynthetic("02-error.jsonl")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(l => parser.Parse(l, DateTimeOffset.UnixEpoch))
            .ToList();

        Assert.Contains(events, e => e is MessageEmitted m && m.Text.StartsWith("Error de Gemini CLI", StringComparison.Ordinal));
        Assert.True(parser.ResultSeen);
        Assert.False(parser.ResultSucceeded);
        Assert.Equal("Quota exceeded for model", parser.Error);

        Assert.Empty(parser.Parse("", DateTimeOffset.UnixEpoch));
        Assert.Empty(parser.Parse("not json", DateTimeOffset.UnixEpoch));
        Assert.Empty(parser.Parse("{\"type\":", DateTimeOffset.UnixEpoch));
        Assert.Empty(parser.Parse("[1,2]", DateTimeOffset.UnixEpoch));
    }

    // ---- Provider ----

    private static async Task<List<AgentEvent>> Drain(IAgentRun run)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in run.Events.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public void Provider_declares_verified_identity_and_capabilities()
    {
        var provider = new GeminiCliAgentProvider(new FakeLocator([]), new FakeStreamingRunner(_ => throw new InvalidOperationException()));

        Assert.Equal(ProviderId.GeminiCli, provider.Provider);
        Assert.Equal(VendorId.Google, provider.Vendor);
        Assert.True(provider.Capabilities.StreamsEvents);
        Assert.True(provider.Capabilities.ReportsTokenUsage);
        Assert.True(provider.Capabilities.SupportsResume);
        Assert.False(provider.Capabilities.SupportsStructuredOutput);
        Assert.False(provider.Capabilities.ReportsSubscriptionQuota);
        Assert.False(provider.Capabilities.SupportsInteractiveApproval);
        Assert.Equal(1, provider.Capabilities.MaxConcurrentRuns);
    }

    [Fact]
    public async Task Real_no_session_capture_fails_with_the_spanish_message()
    {
        var stderr = ReadReal("01-sin-sesion.stderr");
        var exit = int.Parse(ReadReal("01-sin-sesion.exit").Trim().Replace("exit=", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(41, exit);
        var runner = new FakeStreamingRunner(_ => new FakeSession(StdoutLines(ReadReal("01-sin-sesion.jsonl")).Concat(StderrLines(stderr)), exit));
        var provider = new GeminiCliAgentProvider(new FakeLocator(new() { ["gemini"] = "C:/bin/gemini.exe" }), runner);

        await using var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken);
        var events = await Drain(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(41, outcome.ExitCode);
        Assert.Equal("Gemini CLI no tiene sesión iniciada: ejecuta `gemini` en una terminal para iniciar sesión.", outcome.Error);
        Assert.Equal(outcome, Assert.IsType<RunFinished>(Assert.Single(events.OfType<RunFinished>())).Outcome);

        var spec = Assert.Single(runner.Specs);
        Assert.Equal("Responde OK", spec.StandardInput![^"Responde OK".Length..]);
        Assert.Equal(TimeSpan.FromMinutes(5), spec.Timeout);
        Assert.Contains("--skip-trust", spec.Arguments);
        Assert.Contains(spec.Environment!, kv => kv.Key == "GEMINI_SYSTEM_MD" && kv.Value is null);
    }

    [Fact]
    public async Task Synthetic_success_streams_events_and_succeeds()
    {
        var runner = new FakeStreamingRunner(_ => new FakeSession(StdoutLines(ReadSynthetic("01-exito.jsonl")), 0));
        var provider = new GeminiCliAgentProvider(new FakeLocator(new() { ["gemini"] = "C:/bin/gemini.exe" }), runner);

        await using var run = await provider.StartAsync(Request(allowWrites: true), TestContext.Current.CancellationToken);
        var events = await Drain(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("Voy a crear el fichero. HECHO", outcome.FinalText);
        Assert.Equal("11111111-2222-3333-4444-555555555555", outcome.SessionId);
        Assert.Null(outcome.Error);
        Assert.IsType<SessionStarted>(events[0]);
        Assert.IsType<RunFinished>(events[^1]);
        Assert.Single(events.OfType<UsageReported>());
    }

    [Fact]
    public async Task Result_error_with_exit_zero_and_quota_text_is_rate_limited()
    {
        var runner = new FakeStreamingRunner(_ => new FakeSession(StdoutLines(ReadSynthetic("02-error.jsonl")), 0));
        var provider = new GeminiCliAgentProvider(new FakeLocator(new() { ["gemini"] = "C:/bin/gemini.exe" }), runner);

        await using var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.RateLimited, outcome.Status);
        Assert.Equal("Quota exceeded for model", outcome.Error);
    }

    [Theory]
    [InlineData(53, AgentRunStatus.LimitReached)]
    [InlineData(42, AgentRunStatus.Failed)]
    [InlineData(1, AgentRunStatus.Failed)]
    public async Task Other_exit_codes_map_to_statuses(int exit, AgentRunStatus expected)
    {
        var runner = new FakeStreamingRunner(_ => new FakeSession(StderrLines("boom en stderr"), exit));
        var provider = new GeminiCliAgentProvider(new FakeLocator(new() { ["gemini"] = "C:/bin/gemini.exe" }), runner);

        await using var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken);
        var outcome = await run.Completion;

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(exit, outcome.ExitCode);
        if (expected == AgentRunStatus.Failed)
        {
            Assert.Contains("boom en stderr", outcome.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cancel_kills_the_process_and_reports_cancelled()
    {
        var runner = new FakeStreamingRunner(_ => new FakeSession(StdoutLines(ReadSynthetic("01-exito.jsonl").Split('\n')[0]), 0, blockUntilKilled: true));
        var provider = new GeminiCliAgentProvider(new FakeLocator(new() { ["gemini"] = "C:/bin/gemini.exe" }), runner);

        await using var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken);
        await run.CancelAsync();
        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var events = await Drain(run);

        Assert.Equal(AgentRunStatus.Cancelled, outcome.Status);
        Assert.IsType<RunFinished>(events[^1]);
    }

    [Fact]
    public async Task Missing_gemini_returns_a_failed_run_without_starting_a_process()
    {
        var runner = new FakeStreamingRunner(_ => throw new InvalidOperationException("must not start"));
        var provider = new GeminiCliAgentProvider(new FakeLocator([]), runner);

        await using var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken);
        var outcome = await run.Completion;
        var events = await Drain(run);

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Empty(runner.Specs);
        Assert.IsType<RunFinished>(Assert.Single(events));
    }
}
