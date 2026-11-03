using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Providers.Claude;
using AgentStudio.Tests.Providers.Codex;

namespace AgentStudio.Tests.Integration.Phase8;

/// <summary>
/// One launch of a fake CLI: the request the engine gave the adapter, the process the real adapter asked for (its command
/// line, its working directory and its standard input, where the prompt travels) and the attempt the engine tagged it with.
/// </summary>
internal sealed record CliLaunch(AgentRunRequest Request, ProcessSpec Process, int Attempt)
{
    public string Agent => Request.Agent.Id.Value;

    public string? Task => Request.TaskId;

    /// <summary>What the CLI read from its standard input: the prompt, which never goes as an argument.</summary>
    public string StandardInput => Process.StandardInput ?? string.Empty;

    /// <summary>The argument that follows <paramref name="flag"/> in the command line, or null.</summary>
    public string? ArgumentAfter(string flag)
    {
        var index = Process.Arguments.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < Process.Arguments.Count ? Process.Arguments[index + 1] : null;
    }

    public bool Has(string argument) => Process.Arguments.Contains(argument, StringComparer.Ordinal);

    public override string ToString() => $"{Agent}/{Task ?? "-"}#{Attempt}";
}

/// <summary>
/// What one process of a fake CLI does once started: it writes <see cref="Files"/> in its working directory and prints
/// <see cref="Lines"/> on its standard output, then exits with <see cref="ExitCode"/>. With <see cref="Gate"/> it prints its
/// first line, waits for the gate and then prints the rest; with <see cref="Hang"/> it prints its first line and never ends
/// by itself. A kill (a cancellation, or the app closing) ends it at once with -1, as the real process runner reports it.
/// </summary>
internal sealed record CliTurn(IReadOnlyList<string> Lines, int ExitCode = 0)
{
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    public TaskCompletionSource? Gate { get; init; }

    public bool Hang { get; init; }
}

/// <summary>
/// The fake CLIs of the Phase 8 integration tests: the real adapter of Claude Code (<see cref="ClaudeCodeAgentProvider"/>) or
/// of <c>codex exec</c> (<see cref="CodexCliAgentProvider"/>), with its real command line, its prompt on standard input and its
/// real parser, over a process runner that starts nothing and plays what <see cref="Turn"/> says for each launch: a synthetic
/// capture of WP2 (<c>Providers/Fixtures/*/limits/</c>), a turn that does the work, or one that hangs. No CLI is ever run, and
/// no quota is spent. It goes into the app through the real pipeline (<see cref="Phase4ScriptedProviders"/>: journal, quota
/// record and concurrency), as the scripted CLIs of Phases 3 to 5 do.
/// </summary>
internal sealed class FakeCli : IAgentProvider, IExecutableLocator, IStreamingProcessRunner
{
    /// <summary>The request whose process the adapter is asking for, in the flow of its own launch.</summary>
    private static readonly AsyncLocal<AgentRunRequest?> Launching = new();

    private readonly IAgentProvider _adapter;
    private readonly string _command;
    private readonly string _executable;
    private readonly Lock _gate = new();
    private readonly List<CliLaunch> _launches = [];
    private readonly List<FakeCliSession> _sessions = [];

    private FakeCli(ProviderId provider, TimeProvider clock)
    {
        _command = provider == ProviderId.ClaudeCode ? "claude" : "codex";

        // Never run: the runner below starts no process. A native name, as the adapters require for an inline schema.
        _executable = Path.Combine(Path.GetTempPath(), "agentstudio-fake-cli", _command + ".exe");
        _adapter = provider == ProviderId.ClaudeCode
            ? new ClaudeCodeAgentProvider(this, this, clock)
            : new CodexCliAgentProvider(this, this, clock);
        Turn = launch => CliTurns.Works(launch);
    }

    public ProviderId Provider => _adapter.Provider;

    public VendorId Vendor => _adapter.Vendor;

    public ProviderCapabilities Capabilities => _adapter.Capabilities;

    /// <summary>What the CLI does for a launch; by default it does the work (<see cref="CliTurns.Works"/>).</summary>
    public Func<CliLaunch, CliTurn> Turn { get; set; }

    /// <summary>Every launch that reached the CLI, in order.</summary>
    public IReadOnlyList<CliLaunch> Launches
    {
        get
        {
            lock (_gate)
            {
                return [.. _launches];
            }
        }
    }

    /// <summary>How many of its processes are still running (neither ended nor killed).</summary>
    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count(s => !s.Exited.IsCompleted);
            }
        }
    }

    public static FakeCli Claude(TimeProvider clock) => new(ProviderId.ClaudeCode, clock);

    public static FakeCli Codex(TimeProvider clock) => new(ProviderId.CodexCli, clock);

    public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Launching.Value = request;
        return await _adapter.StartAsync(request, cancellationToken).ConfigureAwait(false);
    }

    string? IExecutableLocator.Find(string command) => string.Equals(command, _command, StringComparison.OrdinalIgnoreCase) ? _executable : null;

    Task<IProcessSession> IStreamingProcessRunner.StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        var request = Launching.Value ?? throw new InvalidOperationException($"The fake {_command} was started outside a launch of its adapter.");
        var launch = new CliLaunch(request, spec, ScriptedAgentProvider.AttemptOf(request) ?? 1);
        var turn = Turn(launch);
        foreach (var (path, content) in turn.Files)
        {
            var full = Path.Combine(spec.WorkingDirectory ?? throw new InvalidOperationException("The launch has no working directory."), path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        var session = new FakeCliSession(turn);
        lock (_gate)
        {
            _launches.Add(launch);
            _sessions.Add(session);
        }

        return Task.FromResult<IProcessSession>(session);
    }

    /// <summary>A process of a fake CLI: its output is played in the background, and a kill ends it with -1.</summary>
    private sealed class FakeCliSession : IProcessSession
    {
        private readonly Channel<ProcessOutputLine> _lines = Channel.CreateUnbounded<ProcessOutputLine>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _killed = new();

        public FakeCliSession(CliTurn turn) => _ = System.Threading.Tasks.Task.Run(() => PlayAsync(turn));

        public int ProcessId => 4242;

        public IAsyncEnumerable<ProcessOutputLine> Output => _lines.Reader.ReadAllAsync();

        public Task<int> Exited => _exited.Task;

        public Task KillAsync()
        {
            Kill();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Kill();
            return ValueTask.CompletedTask;
        }

        private async Task PlayAsync(CliTurn turn)
        {
            try
            {
                var waits = turn.Hang || turn.Gate is not null;
                var first = waits ? Math.Min(1, turn.Lines.Count) : turn.Lines.Count;
                foreach (var line in turn.Lines.Take(first))
                {
                    Print(line);
                }

                if (turn.Hang)
                {
                    await System.Threading.Tasks.Task.Delay(Timeout.Infinite, _killed.Token).ConfigureAwait(false);
                }

                if (turn.Gate is { } gate)
                {
                    await gate.Task.WaitAsync(_killed.Token).ConfigureAwait(false);
                }

                foreach (var line in turn.Lines.Skip(first))
                {
                    Print(line);
                }

                End(turn.ExitCode);
            }
            catch (OperationCanceledException)
            {
                // Killed: Kill already ended the process with -1.
            }
        }

        private void Print(string line) => _lines.Writer.TryWrite(new ProcessOutputLine(ProcessStream.StandardOutput, line, DateTimeOffset.UtcNow));

        private void Kill()
        {
            End(-1);
            try
            {
                _killed.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Never disposed; kept for safety.
            }
        }

        private void End(int exitCode)
        {
            _lines.Writer.TryComplete();
            _exited.TrySetResult(exitCode);
        }
    }
}

/// <summary>The turns a fake CLI plays: the synthetic captures of WP2, a turn that does the work, and one that hangs.</summary>
internal static class CliTurns
{
    private const string ClaudeModel = "claude-haiku-4-5-20251001";

    /// <summary>
    /// A synthetic capture of WP2 of the CLI of <paramref name="cli"/>, as it is (<c>Providers/Fixtures/&lt;cli&gt;/&lt;version&gt;/limits/</c>,
    /// marked as synthetic in its README). None of them has its exit code measured: a turn that did not complete exits with 1.
    /// </summary>
    public static CliTurn Capture(ProviderId cli, string name) =>
        new(cli == ProviderId.ClaudeCode ? ClaudeTestSupport.LimitsLines(name) : CodexTestSupport.LimitsLines(name), ExitCode: 1);

    /// <summary>
    /// A turn that does the work: it writes <paramref name="files"/> in the working directory and ends with a completed report
    /// (the executor's schema) in the session it was given, as the CLI of the launch writes it.
    /// </summary>
    public static CliTurn Works(CliLaunch launch, params (string Path, string Content)[] files) =>
        Report(launch, AgentScript.Report(summary: "Trabajo terminado.", filesChanged: [.. files.Select(f => f.Path)]), files);

    /// <summary>A turn that ends with <paramref name="report"/> as its structured output, after writing <paramref name="files"/>.</summary>
    public static CliTurn Report(CliLaunch launch, string report, params (string Path, string Content)[] files) =>
        new(Ends(launch, report))
        {
            Files = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal),
        };

    /// <summary>A turn that starts its session and gives no other sign of life until it is killed.</summary>
    public static CliTurn Hangs(CliLaunch launch) => new([Ends(launch, AgentScript.Report())[0]]) { Hang = true };

    /// <summary>A turn that does the work only once <paramref name="gate"/> opens: until then it is a session that started and is silent.</summary>
    public static CliTurn Held(CliLaunch launch, TaskCompletionSource gate, params (string Path, string Content)[] files) =>
        Works(launch, files) with { Gate = gate };

    /// <summary>The session of a launch: the one it resumes, or the one Agent Studio gave it.</summary>
    public static string SessionOf(CliLaunch launch) =>
        launch.ArgumentAfter("--resume") ?? launch.ArgumentAfter("resume") ?? launch.ArgumentAfter("--session-id") ?? Guid.NewGuid().ToString();

    private static string[] Ends(CliLaunch launch, string report)
    {
        var session = SessionOf(launch);
        var structured = JsonNode.Parse(report)!;
        if (launch.Request.Agent.Model.Provider == ProviderId.CodexCli)
        {
            return
            [
                new JsonObject { ["type"] = "thread.started", ["thread_id"] = session }.ToJsonString(),
                new JsonObject { ["type"] = "turn.started" }.ToJsonString(),
                new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["id"] = "item_0", ["type"] = "agent_message", ["text"] = report } }.ToJsonString(),
                new JsonObject { ["type"] = "turn.completed", ["usage"] = new JsonObject { ["input_tokens"] = 1200, ["cached_input_tokens"] = 0, ["output_tokens"] = 80, ["reasoning_output_tokens"] = 0 } }.ToJsonString(),
            ];
        }

        return
        [
            new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "init",
                ["cwd"] = launch.Process.WorkingDirectory,
                ["session_id"] = session,
                ["tools"] = new JsonArray("Read", "Edit", "Write"),
                ["mcp_servers"] = new JsonArray(),
                ["model"] = ClaudeModel,
                ["permissionMode"] = "dontAsk",
                ["apiKeySource"] = "none",
                ["claude_code_version"] = ClaudeTestSupport.FixtureVersion,
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject
                {
                    ["model"] = ClaudeModel,
                    ["id"] = "msg_fase8_" + launch.Attempt,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Hecho." }),
                    ["stop_reason"] = null,
                },
                ["parent_tool_use_id"] = null,
                ["session_id"] = session,
            }.ToJsonString(),
            new JsonObject
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = false,
                ["api_error_status"] = null,
                ["num_turns"] = 1,
                ["result"] = report,
                ["structured_output"] = structured,
                ["session_id"] = session,
                ["terminal_reason"] = "completed",
            }.ToJsonString(),
        ];
    }
}
