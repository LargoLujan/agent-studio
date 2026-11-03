using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli.Claude;

/// <summary>Runs agents through the unmodified official <c>claude</c> CLI in headless stream-json mode.</summary>
public sealed class ClaudeCodeAgentProvider(IExecutableLocator locator, IStreamingProcessRunner runner, TimeProvider clock) : IAgentProvider
{
    public ProviderId Provider => ProviderId.ClaudeCode;

    public VendorId Vendor => VendorId.Anthropic;

    /// <summary>
    /// Claude Code reports its quota windows at the start of every run (<c>rate_limit_event</c>) and the context of every
    /// step (<c>assistant.message.usage</c>, docs/14 D-09), so it reports context occupancy and compactions.
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        StreamsEvents: true,
        SupportsStructuredOutput: true,
        SupportsResume: true,
        SupportsInteractiveApproval: false,
        ReportsTokenUsage: true,
        ReportsSubscriptionQuota: true,
        MaxConcurrentRuns: 2)
    {
        ReportsContextOccupancy = true,
    };

    public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executable = locator.Find("claude")
            ?? throw new InvalidOperationException("Claude Code no está instalado o no está en el PATH. Instálalo desde https://claude.com/claude-code e inicia sesión con «claude».");

        // The schema travels inline in argv (claude --help documents no file form). A .cmd/.bat shim would re-parse
        // it with cmd.exe, so fail clearly instead of relying on the generic metacharacter rejection.
        if (!string.IsNullOrWhiteSpace(request.OutputJsonSchema) && IsBatchShim(executable))
        {
            throw new InvalidOperationException(
                $"Structured output needs the native claude executable: '{executable}' is a .cmd/.bat shim and the inline --json-schema cannot be passed through cmd.exe safely.");
        }

        string? tempDirectory = null;
        string? instructionsFile = null;
        try
        {
            // Every run gets its own folder: the settings file that turns hooks off always goes there.
            tempDirectory = RunTempFolder.Create();
            var settingsFile = Path.Combine(tempDirectory, "settings.json");
            await RunTempFolder.WriteNewFileAsync(settingsFile, ClaudeCodeArguments.IsolationSettingsJson, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.Agent.SystemInstructions))
            {
                instructionsFile = Path.Combine(tempDirectory, "instructions.md");
                await RunTempFolder.WriteNewFileAsync(instructionsFile, request.Agent.SystemInstructions, cancellationToken).ConfigureAwait(false);
            }

            // Local MCP servers (docs/14 D-30) go in an app-controlled file of the run; Build refuses them for a launch
            // that writes, has a shell or network before the file exists.
            var mcpServers = request.McpServers is { Count: > 0 } servers ? servers : null;
            var mcpConfigFile = mcpServers is null ? null : Path.Combine(tempDirectory, "mcp.json");
            var arguments = ClaudeCodeArguments.Build(request, Guid.NewGuid().ToString(), instructionsFile, settingsFile, mcpConfigFile);
            if (mcpServers is not null)
            {
                await RunTempFolder.WriteNewFileAsync(mcpConfigFile!, ClaudeCodeArguments.McpConfigJson(mcpServers), cancellationToken).ConfigureAwait(false);
            }
            var spec = new ProcessSpec(
                executable,
                arguments,
                request.WorkingDirectory,
                Timeout: request.Agent.Budget.MaxDuration,
                StandardInput: request.Prompt);
            var session = await runner.StartAsync(spec, cancellationToken).ConfigureAwait(false);
            return new ClaudeRun(
                session,
                new ClaudeStreamJsonParser(clock),
                clock,
                tempDirectory,
                request.Agent.Budget.MaxDuration,
                expectStructuredOutput: !string.IsNullOrWhiteSpace(request.OutputJsonSchema));
        }
        catch
        {
            RunTempFolder.DeleteQuietly(tempDirectory);
            throw;
        }
    }

    // Windows drops trailing dots and spaces, so "x.cmd." still runs x.cmd through cmd.exe.
    private static bool IsBatchShim(string fileName)
    {
        var name = fileName.TrimEnd('.', ' ');
        return name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ClaudeRun : IAgentRun
    {
        private const int StderrTail = 20;
        private static readonly TimeSpan PumpWait = TimeSpan.FromSeconds(5);

        private readonly IProcessSession _session;
        private readonly ClaudeStreamJsonParser _parser;
        private readonly TimeProvider _clock;
        private readonly string? _tempDirectory;
        private readonly TimeSpan _maxDuration;
        private readonly bool _expectStructuredOutput;
        private readonly DateTimeOffset _startedAt;
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource<AgentRunOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _pump;
        private int _cancelled;
        private int _disposed;

        public ClaudeRun(IProcessSession session, ClaudeStreamJsonParser parser, TimeProvider clock, string? tempDirectory, TimeSpan maxDuration, bool expectStructuredOutput)
        {
            _maxDuration = maxDuration;
            _expectStructuredOutput = expectStructuredOutput;
            _startedAt = clock.GetUtcNow();
            _session = session;
            _parser = parser;
            _clock = clock;
            _tempDirectory = tempDirectory;
            _pump = Task.Run(PumpAsync);
        }

        public string RunId { get; } = Guid.NewGuid().ToString("N");

        public IAsyncEnumerable<AgentEvent> Events => _events.Reader.ReadAllAsync();

        public Task<AgentRunOutcome> Completion => _completion.Task;

        public async Task CancelAsync()
        {
            Interlocked.Exchange(ref _cancelled, 1);
            await _session.KillAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _session.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAny(_pump, Task.Delay(PumpWait)).ConfigureAwait(false);
            RunTempFolder.DeleteQuietly(_tempDirectory);
        }

        private async Task PumpAsync()
        {
            AgentRunOutcome? result = null;
            string? sessionId = null;
            var stderr = new Queue<string>();
            AgentRunOutcome final;
            try
            {
                await foreach (var line in _session.Output.ConfigureAwait(false))
                {
                    if (line.Stream == ProcessStream.StandardError)
                    {
                        stderr.Enqueue(line.Text);
                        if (stderr.Count > StderrTail)
                        {
                            stderr.Dequeue();
                        }

                        continue;
                    }

                    foreach (var evt in _parser.Parse(line.Text))
                    {
                        switch (evt)
                        {
                            case RunFinished finished:
                                // Held back: the final event carries the exit code, known only when the process ends.
                                result = finished.Outcome;
                                continue;
                            case SessionStarted started:
                                sessionId = started.SessionId;
                                break;
                        }

                        _events.Writer.TryWrite(evt);
                    }
                }

                var exit = await _session.Exited.ConfigureAwait(false);
                final = Resolve(result, sessionId, exit, stderr);
            }
            catch (Exception ex)
            {
                final = new AgentRunOutcome(AgentRunStatus.Failed, null, null, null, sessionId, $"Error al leer la salida de Claude Code: {ex.Message}");
                await TryKillAsync().ConfigureAwait(false);
            }

            // Clean up BEFORE publishing the result: the process has ended (or was killed above).
            RunTempFolder.DeleteQuietly(_tempDirectory);

            _events.Writer.TryWrite(new RunFinished(_clock.GetUtcNow(), final));
            _events.Writer.TryComplete();
            _completion.TrySetResult(final);
        }

        // A failing pump must not leave the CLI running (and editing files) after the concurrency slot is freed.
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

        private AgentRunOutcome Resolve(AgentRunOutcome? result, string? sessionId, int exit, Queue<string> stderr)
        {
            if (result is not null)
            {
                return ApplyStructuredOutputRule(result with { ExitCode = exit });
            }

            if (Volatile.Read(ref _cancelled) != 0)
            {
                return new AgentRunOutcome(AgentRunStatus.Cancelled, exit, null, null, sessionId, "Ejecución cancelada.");
            }

            // The runner reports -1 when it killed the process; with the time budget elapsed that is the timeout.
            if (exit == -1 && _maxDuration > TimeSpan.Zero && _clock.GetUtcNow() - _startedAt >= _maxDuration)
            {
                return new AgentRunOutcome(AgentRunStatus.LimitReached, exit, null, null, sessionId, $"Se agotó el tiempo máximo de la ejecución ({_maxDuration}).");
            }

            var tail = stderr.Count == 0 ? "sin salida de error" : string.Join(" | ", stderr);
            return new AgentRunOutcome(
                AgentRunStatus.Failed,
                exit,
                null,
                null,
                sessionId,
                $"Claude Code terminó sin devolver un resultado (código de salida {exit}). Últimas líneas de error: {tail}");
        }

        // Rule shared by all providers: a requested schema without valid JSON back is a failure (FinalText is kept).
        private AgentRunOutcome ApplyStructuredOutputRule(AgentRunOutcome outcome)
        {
            if (_expectStructuredOutput
                && outcome.Status == AgentRunStatus.Succeeded
                && string.IsNullOrWhiteSpace(outcome.StructuredOutputJson))
            {
                return outcome with
                {
                    Status = AgentRunStatus.Failed,
                    Error = "Se pidió una salida estructurada pero Claude Code no devolvió un JSON válido.",
                };
            }

            return outcome;
        }
    }
}
