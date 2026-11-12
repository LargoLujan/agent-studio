using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli.Codex;

/// <summary>
/// Runs agents through <c>codex exec --json</c>. Each run starts one process; its JSONL is translated to
/// <see cref="AgentEvent"/>s as it arrives. The prompt travels through standard input.
/// <para>
/// Permission switches Codex CANNOT honour (<c>exec</c> has no per-tool allowlist; the sandbox decides):
/// <c>AllowShell</c> is neither granted nor denied (commands always run inside the sandbox, read-only or workspace-write);
/// <c>AllowedTools</c> and <c>DeniedTools</c> are ignored; <c>AllowNetwork=true</c> only takes effect together with
/// <c>AllowFileWrites=true</c> (<c>sandbox_workspace_write.network_access</c>), the read-only sandbox never has network;
/// <c>AdditionalDirectories</c> are only added when file writes are allowed. <c>AllowFileWrites=false</c> and
/// <c>AllowNetwork=false</c> are enforced by the sandbox.
/// </para>
/// </summary>
public sealed class CodexCliAgentProvider(IExecutableLocator locator, IStreamingProcessRunner runner, TimeProvider timeProvider, RunTempFolderOptions? tempFolders = null) : IAgentProvider
{
    public ProviderId Provider => ProviderId.CodexCli;

    public VendorId Vendor => VendorId.OpenAI;

    /// <summary>
    /// Quota is not reported by <c>exec</c> (only by the experimental app-server), and exec cannot ask for approvals
    /// interactively: the sandbox profile decides.
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        StreamsEvents: true,
        SupportsStructuredOutput: true,
        SupportsResume: true,
        SupportsInteractiveApproval: false,
        ReportsTokenUsage: true,
        ReportsSubscriptionQuota: false,
        MaxConcurrentRuns: 2);

    public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // codex exec runs with --ignore-user-config and never loads the local MCP servers of Agent Studio (docs/14 D-30):
        // a request that counts on them fails closed instead of running without them.
        if (request.McpServers is { Count: > 0 })
        {
            throw new ArgumentException("codex exec does not load the local MCP servers of Agent Studio: only Claude Code launches them, read-only.", nameof(request));
        }

        var located = locator.Find("codex") ?? throw new InvalidOperationException("Codex CLI was not found on the PATH.");
        var executable = CodexExecutableResolver.Resolve(located);

        // The arguments are built, and so a launch that cannot be made is refused, before anything is written: the folder of the
        // run is only planned here (its path) and made once the arguments are valid.
        var baseDirectory = (tempFolders ?? RunTempFolderOptions.ForCurrentUser()).BaseDirectory;
        string? tempDirectory = null;
        string? schemaPath = null;
        if (request.OutputJsonSchema is not null)
        {
            tempDirectory = RunTempFolder.NewPath(baseDirectory);
            schemaPath = Path.Combine(tempDirectory, "schema.json");
        }

        var maxDuration = request.Agent.Budget.MaxDuration;
        var spec = new ProcessSpec(
            executable,
            CodexExecArguments.Build(request, schemaPath, CodexExecArguments.IsRepository(request.WorkingDirectory)),
            WorkingDirectory: request.WorkingDirectory,
            Timeout: maxDuration > TimeSpan.Zero ? maxDuration : null,
            StandardInput: CodexExecArguments.BuildStandardInput(request));

        try
        {
            if (tempDirectory is not null)
            {
                RunTempFolder.Create(baseDirectory, tempDirectory);
                await RunTempFolder.WriteNewFileAsync(schemaPath!, request.OutputJsonSchema!, cancellationToken).ConfigureAwait(false);
            }

            var session = await runner.StartAsync(spec, cancellationToken).ConfigureAwait(false);
            return new Run(session, tempDirectory, maxDuration, request, timeProvider);
        }
        catch
        {
            RunTempFolder.DeleteQuietly(tempDirectory);
            throw;
        }
    }

    private sealed class Run : IAgentRun
    {
        private const int StderrLinesKept = 8;

        private readonly IProcessSession _session;
        private readonly string? _tempDirectory;
        private readonly TimeSpan _maxDuration;
        private readonly TimeProvider _time;
        private readonly DateTimeOffset _startedAt;
        private readonly CodexExecJsonParser _parser;
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource<AgentRunOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<string> _stderrTail = new();
        private readonly Task _pump;
        private int _cancelRequested;
        private int _disposed;

        public Run(IProcessSession session, string? tempDirectory, TimeSpan maxDuration, AgentRunRequest request, TimeProvider time)
        {
            _session = session;
            _tempDirectory = tempDirectory;
            _maxDuration = maxDuration;
            _time = time;
            _startedAt = time.GetUtcNow();
            _parser = new CodexExecJsonParser(request.Agent.Model.Model, expectStructuredOutput: request.OutputJsonSchema is not null);
            _pump = Task.Run(PumpAsync);
        }

        public string RunId { get; } = Guid.NewGuid().ToString("N");

        public IAsyncEnumerable<AgentEvent> Events => _events.Reader.ReadAllAsync();

        public Task<AgentRunOutcome> Completion => _completion.Task;

        public async Task CancelAsync()
        {
            Interlocked.Exchange(ref _cancelRequested, 1);
            await _session.KillAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (!_completion.Task.IsCompleted)
            {
                Interlocked.Exchange(ref _cancelRequested, 1);
            }

            await _session.DisposeAsync().ConfigureAwait(false);
            await _pump.ConfigureAwait(false);
            RunTempFolder.DeleteQuietly(_tempDirectory);
        }

        private async Task PumpAsync()
        {
            AgentRunOutcome outcome;
            var terminal = new List<AgentEvent>();
            try
            {
                await foreach (var line in _session.Output.ConfigureAwait(false))
                {
                    if (line.Stream == ProcessStream.StandardError)
                    {
                        _stderrTail.Enqueue(line.Text);
                        while (_stderrTail.Count > StderrLinesKept)
                        {
                            _stderrTail.Dequeue();
                        }

                        continue;
                    }

                    foreach (var evt in _parser.Parse(line.Text, line.At))
                    {
                        if (IsTerminal(evt))
                        {
                            terminal.Add(evt);
                        }
                        else
                        {
                            _events.Writer.TryWrite(evt);
                        }
                    }
                }

                var exitCode = await _session.Exited.ConfigureAwait(false);
                outcome = BuildOutcome(exitCode, terminal);
            }
            catch (Exception ex)
            {
                outcome = new AgentRunOutcome(AgentRunStatus.Failed, null, _parser.LastAgentMessage, null, _parser.SessionId, "Codex run failed: " + ex.Message);
                terminal.Clear();

                // A failing pump must not leave the CLI running (and editing files) after the slot is freed.
                await TryKillAsync().ConfigureAwait(false);
            }

            // Clean up BEFORE publishing the result: the process has ended (or was killed above).
            RunTempFolder.DeleteQuietly(_tempDirectory);

            var now = _time.GetUtcNow();
            foreach (var evt in terminal.Where(e => e is RateLimitSignaled))
            {
                _events.Writer.TryWrite(evt);
            }

            _events.Writer.TryWrite(new RunFinished(now, outcome));
            _events.Writer.TryComplete();
            _completion.TrySetResult(outcome);
        }

        private async Task TryKillAsync()
        {
            try
            {
                await _session.KillAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: DisposeAsync kills the tree as well.
            }
        }

        private static bool IsTerminal(AgentEvent evt) => evt is RunFinished or RateLimitSignaled;

        private AgentRunOutcome BuildOutcome(int exitCode, List<AgentEvent> terminal)
        {
            if (Volatile.Read(ref _cancelRequested) == 1)
            {
                return new AgentRunOutcome(AgentRunStatus.Cancelled, exitCode, _parser.LastAgentMessage, null, _parser.SessionId, "The run was cancelled.");
            }

            if (terminal.OfType<RunFinished>().FirstOrDefault() is { } finished)
            {
                return finished.Outcome with { ExitCode = exitCode };
            }

            if (_maxDuration > TimeSpan.Zero && _time.GetUtcNow() - _startedAt >= _maxDuration)
            {
                return new AgentRunOutcome(AgentRunStatus.LimitReached, exitCode, _parser.LastAgentMessage, null, _parser.SessionId, $"The time budget of {_maxDuration} was exhausted.");
            }

            var message = $"Codex exited with code {exitCode} without completing the turn.";
            if (_parser.LastError is not null)
            {
                message += " Last error: " + _parser.LastError;
            }

            if (_stderrTail.Count > 0)
            {
                message += " stderr: " + string.Join(" | ", _stderrTail);
            }

            var failure = _parser.FailureEvents(_time.GetUtcNow(), message);
            terminal.AddRange(failure.Where(e => e is RateLimitSignaled));
            return failure.OfType<RunFinished>().First().Outcome with { ExitCode = exitCode };
        }
    }
}
