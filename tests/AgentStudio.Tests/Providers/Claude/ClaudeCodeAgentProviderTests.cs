using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Claude;

public sealed class ClaudeCodeAgentProviderTests
{
    private sealed class FakeLocator(string? path) : IExecutableLocator
    {
        public string? Find(string command) => path;
    }

    private sealed class FakeSession : IProcessSession
    {
        private readonly Channel<ProcessOutputLine> _lines = Channel.CreateUnbounded<ProcessOutputLine>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 1;

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        public IAsyncEnumerable<ProcessOutputLine> Output => _lines.Reader.ReadAllAsync();

        public Task<int> Exited => _exited.Task;

        public void Emit(ProcessStream stream, string text) =>
            _lines.Writer.TryWrite(new ProcessOutputLine(stream, text, DateTimeOffset.UtcNow));

        public void End(int exitCode)
        {
            _lines.Writer.TryComplete();
            _exited.TrySetResult(exitCode);
        }

        public Task KillAsync()
        {
            Killed = true;
            End(-1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            End(-1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRunner(FakeSession session) : IStreamingProcessRunner
    {
        public ProcessSpec? Spec { get; private set; }

        public string? InstructionsContent { get; private set; }

        /// <summary>The <c>--settings</c> file as the CLI sees it at launch (the provider deletes it when the run ends).</summary>
        public string? SettingsContent { get; private set; }

        /// <summary>The <c>--mcp-config</c> file as the CLI sees it at launch, and where it was.</summary>
        public (string Path, string Content)? McpConfig { get; private set; }

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Spec = spec;
            var arguments = spec.Arguments.ToList();
            var mcp = arguments.IndexOf("--mcp-config");
            if (mcp >= 0)
            {
                McpConfig = (spec.Arguments[mcp + 1], File.ReadAllText(spec.Arguments[mcp + 1]));
            }

            var index = arguments.IndexOf("--append-system-prompt-file");
            if (index >= 0)
            {
                InstructionsContent = File.ReadAllText(spec.Arguments[index + 1]);
            }

            var settings = arguments.IndexOf("--settings");
            if (settings >= 0)
            {
                SettingsContent = File.ReadAllText(spec.Arguments[settings + 1]);
            }

            return Task.FromResult<IProcessSession>(session);
        }
    }

    private static async Task<List<AgentEvent>> DrainAsync(IAgentRun run, CancellationToken token)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in run.Events.WithCancellation(token))
        {
            events.Add(evt);
        }

        return events;
    }

    [Fact]
    public void Declares_provider_vendor_and_capabilities()
    {
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("claude"), new FakeRunner(new FakeSession()), TimeProvider.System, TestLocalData.RunFolders);

        Assert.Equal(ProviderId.ClaudeCode, sut.Provider);
        Assert.Equal(VendorId.Anthropic, sut.Vendor);
        Assert.True(sut.Capabilities.StreamsEvents);
        Assert.True(sut.Capabilities.SupportsStructuredOutput);
        Assert.True(sut.Capabilities.SupportsResume);
        Assert.True(sut.Capabilities.ReportsTokenUsage);
        Assert.True(sut.Capabilities.ReportsSubscriptionQuota);
        Assert.False(sut.Capabilities.SupportsInteractiveApproval);
        Assert.Equal(2, sut.Capabilities.MaxConcurrentRuns);
        Assert.True(sut.Capabilities.ReportsContextOccupancy);
    }

    [Fact]
    public async Task Mcp_servers_go_in_an_app_controlled_file_of_the_run_that_is_removed_when_it_ends()
    {
        var session = new FakeSession();
        var runner = new FakeRunner(session);
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("claude.exe"), runner, TimeProvider.System, TestLocalData.RunFolders);
        var server = new McpServerSpec("agentstudio", @"C:\Program Files\AgentStudio\AgentStudio.SkillsMcp.exe", ["--snapshot", @"C:\runs\aux\snapshot.json"], ["mcp__agentstudio__search_skills"]);

        await using (var run = await sut.StartAsync(Request() with { McpServers = [server] }, TestContext.Current.CancellationToken))
        {
            session.End(0);
            await run.Completion;
        }

        var (path, content) = runner.McpConfig!.Value;
        Assert.StartsWith(TestLocalData.RunFolders.BaseDirectory, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("mcp.json", Path.GetFileName(path));
        Assert.Equal(ClaudeCodeArguments.McpConfigJson([server]), content);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Mcp_servers_on_a_writing_launch_are_refused_before_anything_is_written_or_started()
    {
        var runner = new FakeRunner(new FakeSession());
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("claude.exe"), runner, TimeProvider.System, TestLocalData.RunFolders);
        var server = new McpServerSpec("agentstudio", @"C:\tools\AgentStudio.SkillsMcp.exe", [], ["mcp__agentstudio__search_skills"]);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.StartAsync(Request(Permissions(writes: true)) with { McpServers = [server] }, TestContext.Current.CancellationToken));

        Assert.Null(runner.Spec);
    }

    [Fact]
    public async Task A_turn_that_must_not_be_saved_and_resumes_is_refused_before_anything_is_started()
    {
        var runner = new FakeRunner(new FakeSession());
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("claude.exe"), runner, TimeProvider.System, TestLocalData.RunFolders);
        var request = Request(resume: "11111111-2222-3333-4444-555555555555") with { PersistSession = false };

        await Assert.ThrowsAsync<ArgumentException>(() => sut.StartAsync(request, TestContext.Current.CancellationToken));

        Assert.Null(runner.Spec);
    }

    [Fact]
    public async Task A_turn_that_must_not_be_saved_is_launched_as_a_new_session_that_claude_does_not_keep()
    {
        var session = new FakeSession();
        var runner = new FakeRunner(session);
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("claude.exe"), runner, TimeProvider.System, TestLocalData.RunFolders);

        await using (var run = await sut.StartAsync(Request(schema: """{"type":"object"}""") with { PersistSession = false }, TestContext.Current.CancellationToken))
        {
            session.End(0);
            await run.Completion;
        }

        var arguments = runner.Spec!.Arguments.ToList();
        Assert.Single(arguments, a => a == "--no-session-persistence");
        Assert.Contains("--session-id", arguments);
        Assert.DoesNotContain("--resume", arguments);
    }

    [Fact]
    public async Task Start_without_claude_installed_throws_a_clear_spanish_message()
    {
        var sut = new ClaudeCodeAgentProvider(new FakeLocator(null), new FakeRunner(new FakeSession()), TimeProvider.System, TestLocalData.RunFolders);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains("Claude Code no está instalado", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_run_streams_events_and_completes_with_the_result_outcome()
    {
        var token = TestContext.Current.CancellationToken;
        var session = new FakeSession();
        var runner = new FakeRunner(session);
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("C:/bin/claude.exe"), runner, TimeProvider.System, TestLocalData.RunFolders);

        await using var run = await sut.StartAsync(Request(instructions: "Sé breve.", workingDirectory: "C:/work"), token);
        foreach (var line in FixtureLines("01-texto.jsonl"))
        {
            session.Emit(ProcessStream.StandardOutput, line);
        }

        session.Emit(ProcessStream.StandardOutput, "linea basura");
        session.End(0);

        var events = await DrainAsync(run, token);
        var outcome = await run.Completion;

        Assert.Equal("C:/bin/claude.exe", runner.Spec!.FileName);
        Assert.Equal("C:/work", runner.Spec.WorkingDirectory);
        Assert.Equal("Responde exactamente OK", runner.Spec.StandardInput);
        Assert.Equal(TimeSpan.FromMinutes(5), runner.Spec.Timeout);
        Assert.Equal("Sé breve.", runner.InstructionsContent);
        Assert.DoesNotContain(runner.Spec.Arguments, a => a.Contains("Responde exactamente", StringComparison.Ordinal));

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("OK", outcome.FinalText);
        Assert.IsType<QuotaReported>(events[0]);
        Assert.IsType<SessionStarted>(events[1]);
        Assert.IsType<RunFinished>(events[^1]);
        Assert.Single(events.OfType<RunFinished>());
        Assert.Equal(outcome, events.OfType<RunFinished>().Single().Outcome);
    }

    [Fact]
    public async Task Process_without_result_fails_with_exit_code_and_stderr_tail()
    {
        var token = TestContext.Current.CancellationToken;
        var session = new FakeSession();
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("claude"), new FakeRunner(session), TimeProvider.System, TestLocalData.RunFolders);

        await using var run = await sut.StartAsync(Request(), token);
        session.Emit(ProcessStream.StandardError, "Error: not logged in");
        session.End(1);

        var events = await DrainAsync(run, token);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("not logged in", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("código de salida 1", outcome.Error, StringComparison.Ordinal);
        Assert.IsType<RunFinished>(Assert.Single(events));
    }

    [Fact]
    public async Task Cancel_kills_the_session_and_the_outcome_is_cancelled()
    {
        var token = TestContext.Current.CancellationToken;
        var session = new FakeSession();
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("claude"), new FakeRunner(session), TimeProvider.System, TestLocalData.RunFolders);

        await using var run = await sut.StartAsync(Request(), token);
        await run.CancelAsync();
        var events = await DrainAsync(run, token);
        var outcome = await run.Completion;

        Assert.True(session.Killed);
        Assert.Equal(AgentRunStatus.Cancelled, outcome.Status);
        Assert.IsType<RunFinished>(events[^1]);
    }

    [Fact]
    public async Task Every_run_gets_an_app_controlled_settings_file_that_turns_hooks_off_and_is_removed_at_the_end()
    {
        var token = TestContext.Current.CancellationToken;
        var session = new FakeSession();
        var runner = new FakeRunner(session);
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("C:/bin/claude.exe"), runner, TimeProvider.System, TestLocalData.RunFolders);

        // No system instructions: the per-run folder is still needed for the settings file.
        var run = await sut.StartAsync(Request(), token);
        var args = runner.Spec!.Arguments.ToList();
        var file = args[args.IndexOf("--settings") + 1];

        Assert.StartsWith(TestLocalData.RunFolders.BaseDirectory, file, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(runner.SettingsContent);
        using (var settings = JsonDocument.Parse(runner.SettingsContent))
        {
            Assert.True(settings.RootElement.GetProperty("disableAllHooks").GetBoolean());
        }

        Assert.Equal(string.Empty, args[args.IndexOf("--setting-sources") + 1]);
        Assert.Contains("--strict-mcp-config", args);

        session.End(0);
        await DrainAsync(run, token);
        await run.DisposeAsync();

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task Dispose_removes_the_instructions_file_and_disposes_the_session()
    {
        var token = TestContext.Current.CancellationToken;
        var session = new FakeSession();
        var runner = new FakeRunner(session);
        var sut = new ClaudeCodeAgentProvider(new FakeLocator("claude"), runner, TimeProvider.System, TestLocalData.RunFolders);

        var run = await sut.StartAsync(Request(instructions: "x"), token);
        var args = runner.Spec!.Arguments.ToList();
        var file = args[args.IndexOf("--append-system-prompt-file") + 1];
        Assert.True(File.Exists(file));

        await run.DisposeAsync();

        Assert.False(File.Exists(file));
        Assert.True(session.Disposed);
    }
}
