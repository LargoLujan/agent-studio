using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli.Gemini;

/// <summary>
/// Runs agents through the official Gemini CLI, unmodified, in headless mode (<c>-p "" -o stream-json</c>, prompt on
/// stdin). See <see cref="GeminiArguments"/> for the flag decisions and <see cref="GeminiStreamJsonParser"/> for the
/// event mapping (derived from documentation, pending a real authenticated capture).
/// <para>
/// Permission switches Gemini CLI CANNOT honour (headless approval modes are <c>plan</c> and <c>auto_edit</c>; <c>yolo</c>
/// is never used, so any tool that needs confirmation is denied): <c>AllowShell=true</c> and <c>AllowNetwork=true</c>
/// grant nothing (shell and web tools stay unapproved); <c>AllowedTools</c> and <c>DeniedTools</c> are ignored.
/// <c>AllowFileWrites</c> is the only switch that changes behaviour (<c>auto_edit</c> vs <c>plan</c>), and
/// <c>AdditionalDirectories</c> are passed as <c>--include-directories</c>. Derived from the documentation, not verified
/// with an authenticated session.
/// </para>
/// <para>
/// Exit codes: 0 success; 41 no session (verified with 0.64.0, undocumented); 42 invalid input; 53 turn limit; others
/// fail with the last stderr lines.
/// </para>
/// </summary>
public sealed class GeminiCliAgentProvider(IExecutableLocator locator, IStreamingProcessRunner runner) : IAgentProvider
{
    public const string NoSessionMessage = "Gemini CLI no tiene sesión iniciada: ejecuta `gemini` en una terminal para iniciar sesión.";

    private readonly GeminiLaunchResolver _resolver = new(locator);

    public ProviderId Provider => ProviderId.GeminiCli;

    public VendorId Vendor => VendorId.Google;

    // Resume works with the session UUID reported by init (docs/cli/session-management.md). No native output schema in headless.
    public ProviderCapabilities Capabilities { get; } = new(
        StreamsEvents: true,
        SupportsStructuredOutput: false,
        SupportsResume: true,
        SupportsInteractiveApproval: false,
        ReportsTokenUsage: true,
        ReportsSubscriptionQuota: false,
        MaxConcurrentRuns: 1);

    public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var launch = _resolver.Resolve();
        if (launch is null)
        {
            return GeminiRun.Failed("Gemini CLI no está instalado o no se encuentra en el PATH." + InsideProjectNote.Spanish(locator.FindInUntrustedRoot("gemini")));
        }

        var spec = new ProcessSpec(
            launch.FileName,
            [.. launch.LeadingArguments, .. GeminiArguments.Build(request)],
            request.WorkingDirectory,
            // A leftover GEMINI_SYSTEM_MD would silently replace the built-in system prompt.
            new Dictionary<string, string?> { ["GEMINI_SYSTEM_MD"] = null },
            request.Agent.Budget.MaxDuration,
            GeminiArguments.BuildStandardInput(request));

        var session = await runner.StartAsync(spec, cancellationToken).ConfigureAwait(false);
        return new GeminiRun(session, request.Agent.Budget.MaxDuration, !string.IsNullOrWhiteSpace(request.OutputJsonSchema));
    }

    private sealed class GeminiRun : IAgentRun
    {
        private const int StderrLines = 8;

        private readonly IProcessSession? _session;
        private readonly TimeSpan _maxDuration;
        private readonly bool _expectStructuredOutput;
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource<AgentRunOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GeminiStreamJsonParser _parser = new();
        private readonly Queue<string> _stderr = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Task _pump;
        private volatile bool _cancelled;

        public GeminiRun(IProcessSession session, TimeSpan maxDuration, bool expectStructuredOutput)
        {
            _expectStructuredOutput = expectStructuredOutput;
            _session = session;
            _maxDuration = maxDuration;
            RunId = Guid.NewGuid().ToString("N");
            _pump = Task.Run(PumpAsync);
        }

        private GeminiRun(string error)
        {
            _maxDuration = TimeSpan.Zero;
            RunId = Guid.NewGuid().ToString("N");
            _pump = Task.CompletedTask;
            Finish(new AgentRunOutcome(AgentRunStatus.Failed, null, null, null, null, error));
        }

        public string RunId { get; }

        public IAsyncEnumerable<AgentEvent> Events => _events.Reader.ReadAllAsync();

        public Task<AgentRunOutcome> Completion => _completion.Task;

        public static GeminiRun Failed(string error) => new(error);

        public async Task CancelAsync()
        {
            _cancelled = true;
            if (_session is not null)
            {
                await _session.KillAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }

            await _pump.ConfigureAwait(false);
        }

        private async Task PumpAsync()
        {
            AgentRunOutcome outcome;
            try
            {
                await foreach (var line in _session!.Output.ConfigureAwait(false))
                {
                    if (line.Stream == ProcessStream.StandardOutput)
                    {
                        foreach (var evt in _parser.Parse(line.Text, line.At))
                        {
                            _events.Writer.TryWrite(evt);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(line.Text))
                    {
                        foreach (var evt in _parser.ObserveStderr(line.Text, line.At))
                        {
                            _events.Writer.TryWrite(evt);
                        }

                        _stderr.Enqueue(line.Text.Trim());
                        if (_stderr.Count > StderrLines)
                        {
                            _stderr.Dequeue();
                        }
                    }
                }

                outcome = BuildOutcome(await _session.Exited.ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outcome = new AgentRunOutcome(AgentRunStatus.Failed, null, _parser.FinalText, null, _parser.SessionId, $"Fallo al leer la salida de Gemini CLI: {ex.Message}");

                // A failing pump must not leave the CLI running (and editing files) after the slot is freed.
                await TryKillAsync().ConfigureAwait(false);
            }

            Finish(outcome);
        }

        private async Task TryKillAsync()
        {
            try
            {
                await _session!.KillAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: DisposeAsync kills the tree as well.
            }
        }

        // Quota errors usually end with a generic failure exit code: reclassify those as RateLimited.
        private AgentRunOutcome BuildOutcome(int exitCode)
        {
            var outcome = BuildBaseOutcome(exitCode);
            if (outcome.Status == AgentRunStatus.Failed && _parser.RateLimited)
            {
                return outcome with { Status = AgentRunStatus.RateLimited };
            }

            return ApplyStructuredOutputRule(outcome);
        }

        // Shared rule: a requested schema without valid JSON back is a failure (FinalText is kept). Gemini headless has
        // no native schema option, so this only succeeds when the model happened to answer with valid JSON.
        private AgentRunOutcome ApplyStructuredOutputRule(AgentRunOutcome outcome)
        {
            if (!_expectStructuredOutput || outcome.Status != AgentRunStatus.Succeeded)
            {
                return outcome;
            }

            var text = outcome.FinalText;
            if (!string.IsNullOrWhiteSpace(text) && IsValidJson(text))
            {
                return outcome with { StructuredOutputJson = text };
            }

            return outcome with
            {
                Status = AgentRunStatus.Failed,
                Error = "Se pidió una salida estructurada pero Gemini CLI no devolvió un JSON válido (el modo headless no admite esquema nativo).",
            };
        }

        private static bool IsValidJson(string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private AgentRunOutcome BuildBaseOutcome(int exitCode)
        {
            var text = _parser.FinalText;
            var session = _parser.SessionId;
            var tail = string.Join(Environment.NewLine, _stderr);

            AgentRunOutcome Make(AgentRunStatus status, string? error) =>
                new(status, exitCode, text, null, session, error);

            if (_cancelled)
            {
                return Make(AgentRunStatus.Cancelled, "Ejecución cancelada.");
            }

            return exitCode switch
            {
                0 when _parser.ResultSeen && !_parser.ResultSucceeded =>
                    Make(AgentRunStatus.Failed, _parser.Error ?? "Gemini CLI terminó con error."),
                0 => Make(AgentRunStatus.Succeeded, null),
                41 => Make(AgentRunStatus.Failed, NoSessionMessage),
                42 => Make(AgentRunStatus.Failed, $"Gemini CLI rechazó la entrada (código 42). {tail}".Trim()),
                53 => Make(AgentRunStatus.LimitReached, "Gemini CLI alcanzó el límite de turnos."),
                -1 when _maxDuration > TimeSpan.Zero && _clock.Elapsed >= _maxDuration =>
                    Make(AgentRunStatus.LimitReached, "Se agotó el tiempo máximo de la ejecución."),
                _ => Make(AgentRunStatus.Failed, $"Gemini CLI terminó con el código {exitCode}. {_parser.Error} {tail}".Trim()),
            };
        }

        private void Finish(AgentRunOutcome outcome)
        {
            _events.Writer.TryWrite(new RunFinished(DateTimeOffset.UtcNow, outcome));
            _events.Writer.TryComplete();
            _completion.TrySetResult(outcome);
        }
    }
}
