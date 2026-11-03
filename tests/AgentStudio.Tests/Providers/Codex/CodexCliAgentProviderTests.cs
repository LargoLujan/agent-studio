using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli.Codex;

namespace AgentStudio.Tests.Providers.Codex;

public sealed class CodexCliAgentProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeLocator(string? path) : IExecutableLocator
    {
        public string? Find(string command) => path;
    }

    private sealed class FakeSession(IEnumerable<ProcessOutputLine> lines, int exit, bool blockUntilKilled = false) : IProcessSession
    {
        private readonly Channel<ProcessOutputLine> _channel = Channel.CreateUnbounded<ProcessOutputLine>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;

        public int Kills { get; private set; }

        public bool Disposed { get; private set; }

        public int ProcessId => 1;

        public IAsyncEnumerable<ProcessOutputLine> Output => Pump();

        public Task<int> Exited => _exited.Task;

        private async IAsyncEnumerable<ProcessOutputLine> Pump([EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!_started)
            {
                _started = true;
                foreach (var line in lines)
                {
                    _channel.Writer.TryWrite(line);
                }

                if (!blockUntilKilled)
                {
                    _channel.Writer.TryComplete();
                    _exited.TrySetResult(exit);
                }
            }

            await foreach (var line in _channel.Reader.ReadAllAsync(ct))
            {
                yield return line;
            }
        }

        public Task KillAsync()
        {
            Kills++;
            _channel.Writer.TryComplete();
            _exited.TrySetResult(-1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return KillAsync().ContinueWith(_ => { }, TaskScheduler.Default).AsValueTask();
        }
    }

    private sealed class FakeRunner(FakeSession session) : IStreamingProcessRunner
    {
        public ProcessSpec? Spec { get; private set; }

        /// <summary>
        /// The schema file as the CLI would see it at launch. Read here because the provider deletes it as soon as the
        /// run completes, which with a fake session can happen before the test gets to look.
        /// </summary>
        public string? SchemaAtLaunch { get; private set; }

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Spec = spec;
            var arguments = spec.Arguments.ToList();
            var schemaIndex = arguments.IndexOf("--output-schema");
            if (schemaIndex >= 0 && schemaIndex + 1 < arguments.Count)
            {
                SchemaAtLaunch = File.ReadAllText(arguments[schemaIndex + 1]);
            }

            return Task.FromResult<IProcessSession>(session);
        }
    }

    private static IEnumerable<ProcessOutputLine> Stdout(params string[] lines) =>
        lines.Select(l => new ProcessOutputLine(ProcessStream.StandardOutput, l, Now));

    private static ProcessOutputLine Stderr(string text) => new(ProcessStream.StandardError, text, Now);

    private static CodexCliAgentProvider Provider(FakeRunner runner, string? path = "C:/bin/codex.exe") =>
        new(new FakeLocator(path), runner, TimeProvider.System);

    private static async Task<List<AgentEvent>> DrainAsync(IAgentRun run)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in run.Events.WithCancellation(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public void Capabilities_and_identity()
    {
        var provider = Provider(new FakeRunner(new FakeSession([], 0)));

        Assert.Equal(ProviderId.CodexCli, provider.Provider);
        Assert.Equal(VendorId.OpenAI, provider.Vendor);
        var c = provider.Capabilities;
        Assert.True(c.StreamsEvents && c.SupportsStructuredOutput && c.SupportsResume && c.ReportsTokenUsage);
        Assert.False(c.ReportsSubscriptionQuota);
        Assert.False(c.SupportsInteractiveApproval);
        Assert.False(c.ReportsContextOccupancy);
        Assert.Equal(2, c.MaxConcurrentRuns);
    }

    [Fact]
    public async Task Mcp_servers_are_refused_because_codex_exec_never_loads_those_of_agent_studio()
    {
        var runner = new FakeRunner(new FakeSession([], 0));
        var request = CodexTestSupport.Request(CodexTestSupport.Permissions()) with
        {
            McpServers = [new McpServerSpec("agentstudio", @"C:\tools\AgentStudio.SkillsMcp.exe", [], ["mcp__agentstudio__search_skills"])],
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(runner).StartAsync(request, TestContext.Current.CancellationToken));

        Assert.Null(runner.Spec);
    }

    [Fact]
    public async Task A_turn_that_must_not_be_saved_and_resumes_is_refused_before_anything_is_started()
    {
        var runner = new FakeRunner(new FakeSession([], 0));
        var request = CodexTestSupport.Request(schema: "{}", resume: "01a13f53-e4b3-7071-8396-4afc4d6c0323") with { PersistSession = false };

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(runner).StartAsync(request, TestContext.Current.CancellationToken));

        Assert.Null(runner.Spec);
    }

    [Fact]
    public async Task A_turn_that_must_not_be_saved_is_launched_ephemeral_with_its_schema_file()
    {
        var runner = new FakeRunner(new FakeSession(Stdout(CodexTestSupport.FixtureLines("03-estructurada.jsonl")), 0));
        var request = CodexTestSupport.Request(schema: """{"type":"object"}""") with { PersistSession = false };

        await using (var run = await Provider(runner).StartAsync(request, TestContext.Current.CancellationToken))
        {
            await DrainAsync(run);
            await run.Completion;
        }

        var arguments = runner.Spec!.Arguments.ToList();
        Assert.Equal(["--ephemeral", "-c", "history.persistence=none", "-"], arguments.TakeLast(4));
        Assert.DoesNotContain("resume", arguments);
        Assert.Equal("""{"type":"object"}""", runner.SchemaAtLaunch);
    }

    [Fact]
    public async Task Success_replays_fixture_and_sends_prompt_through_stdin()
    {
        var session = new FakeSession(Stdout(CodexTestSupport.FixtureLines("02-herramienta.jsonl")), 0);
        var runner = new FakeRunner(session);
        var ct = TestContext.Current.CancellationToken;

        await using var run = await Provider(runner).StartAsync(
            CodexTestSupport.Request(CodexTestSupport.Permissions(writes: true), "C:/work", prompt: "Crea hola.txt", instructions: "Eres ejecutor", maxDuration: TimeSpan.FromMinutes(3)),
            ct);
        var events = await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("HECHO", outcome.FinalText);
        Assert.Equal("01a13f53-e4b3-7071-8396-4afc4d6c0323", outcome.SessionId);
        Assert.IsType<SessionStarted>(events[0]);
        Assert.Contains(events, e => e is ToolInvoked);
        Assert.Contains(events, e => e is UsageReported);
        Assert.Equal(outcome, Assert.IsType<RunFinished>(events[^1]).Outcome);
        Assert.Single(events.OfType<RunFinished>());

        Assert.NotNull(runner.Spec);
        Assert.Equal("C:/bin/codex.exe", runner.Spec.FileName);
        Assert.Equal("-", runner.Spec.Arguments[^1]);
        Assert.Contains("<task>\nCrea hola.txt\n</task>", runner.Spec.StandardInput);
        Assert.Contains("Eres ejecutor", runner.Spec.StandardInput);
        Assert.DoesNotContain(runner.Spec.Arguments, a => a.Contains("Crea hola.txt"));
        Assert.Equal(TimeSpan.FromMinutes(3), runner.Spec.Timeout);
        Assert.Equal("C:/work", runner.Spec.WorkingDirectory);
    }

    [Fact]
    public async Task The_launched_codex_ignores_user_and_project_configuration_and_keeps_an_explicit_windows_sandbox()
    {
        var runner = new FakeRunner(new FakeSession(Stdout(CodexTestSupport.FixtureLines("01-texto.jsonl")), 0));

        await using var run = await Provider(runner).StartAsync(CodexTestSupport.Request(), TestContext.Current.CancellationToken);
        await DrainAsync(run);

        var arguments = runner.Spec!.Arguments.ToList();
        Assert.Contains("--ignore-user-config", arguments);
        Assert.Contains("--ignore-rules", arguments);
        Assert.Equal("hooks", arguments[arguments.IndexOf("--disable") + 1]);
        Assert.Contains("windows.sandbox=unelevated", arguments);
        Assert.Equal("read-only", arguments[arguments.IndexOf("-s") + 1]);
    }

    [Fact]
    public async Task Process_ending_without_turn_completed_fails_with_stderr_and_exit_code()
    {
        var lines = Stdout("""{"type":"thread.started","thread_id":"t-1"}""").Append(Stderr("error: algo fallo")).Append(Stderr("segunda linea"));
        var runner = new FakeRunner(new FakeSession(lines, 3));
        await using var run = await Provider(runner).StartAsync(CodexTestSupport.Request(), TestContext.Current.CancellationToken);

        var events = await DrainAsync(run);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal("t-1", outcome.SessionId);
        Assert.Contains("algo fallo", outcome.Error);
        Assert.Contains("segunda linea", outcome.Error);
        Assert.Contains("3", outcome.Error);
        Assert.Equal(outcome, Assert.IsType<RunFinished>(events[^1]).Outcome);
    }

    [Fact]
    public async Task Usage_limit_without_result_is_rate_limited()
    {
        var lines = Stdout("""{"type":"error","message":"You've hit your usage limit."}""");
        await using var run = await Provider(new FakeRunner(new FakeSession(lines, 1))).StartAsync(CodexTestSupport.Request(), TestContext.Current.CancellationToken);

        var events = await DrainAsync(run);

        Assert.Equal(AgentRunStatus.RateLimited, (await run.Completion).Status);
        Assert.Single(events.OfType<RateLimitSignaled>());
    }

    [Fact]
    public async Task Cancel_kills_the_session_and_reports_cancelled()
    {
        var session = new FakeSession(Stdout("""{"type":"thread.started","thread_id":"t-2"}"""), 0, blockUntilKilled: true);
        await using var run = await Provider(new FakeRunner(session)).StartAsync(CodexTestSupport.Request(), TestContext.Current.CancellationToken);

        await run.CancelAsync();
        var events = await DrainAsync(run);
        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Cancelled, outcome.Status);
        Assert.Equal(1, session.Kills);
        Assert.IsType<RunFinished>(events[^1]);
    }

    [Fact]
    public async Task Schema_goes_to_a_temporary_file_that_is_removed_on_dispose()
    {
        var session = new FakeSession(Stdout(CodexTestSupport.FixtureLines("03-estructurada.jsonl")), 0);
        var runner = new FakeRunner(session);
        var run = await Provider(runner).StartAsync(CodexTestSupport.Request(schema: """{"type":"object"}"""), TestContext.Current.CancellationToken);

        var schemaPath = runner.Spec!.Arguments[runner.Spec.Arguments.ToList().IndexOf("--output-schema") + 1];
        Assert.Equal("""{"type":"object"}""", runner.SchemaAtLaunch);

        await DrainAsync(run);
        var outcome = await run.Completion;
        await run.DisposeAsync();

        Assert.Equal("{\"respuesta\":\"OK\",\"numero\":42}", outcome.StructuredOutputJson);
        Assert.False(File.Exists(schemaPath));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Missing_codex_throws()
    {
        var provider = Provider(new FakeRunner(new FakeSession([], 0)), path: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartAsync(CodexTestSupport.Request(), TestContext.Current.CancellationToken));
    }
}

internal static class TaskExtensions
{
    public static async ValueTask AsValueTask(this Task task) => await task.ConfigureAwait(false);
}
