using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Orchestration.Fakes;

/// <summary>
/// What one scripted agent run does. Everything is optional: the default is a run that succeeds with a completed
/// report. A run can emit events and usage, write files in its working directory, return structured JSON, fail, hang,
/// hit a rate limit, throw at launch, or wait on a gate so a test decides when it ends.
/// </summary>
internal sealed record AgentScript
{
    public AgentRunStatus Status { get; init; } = AgentRunStatus.Succeeded;

    /// <summary>The structured output (the executor report or the plan). Null returns none.</summary>
    public string? StructuredOutput { get; init; } = Report();

    public string? FinalText { get; init; } = "Hecho.";

    public string? Error { get; init; }

    /// <summary>Session id announced in <see cref="SessionStarted"/>; null makes one up from the agent, task and attempt.</summary>
    public string? SessionId { get; init; }

    /// <summary>Events emitted after <see cref="SessionStarted"/>.</summary>
    public IReadOnlyList<AgentEvent> Events { get; init; } = [];

    /// <summary>Files written in the working directory (relative path, '/' separated, to content) before the run ends.</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    /// <summary>The run never ends by itself: only <see cref="IAgentRun.CancelAsync"/> or disposal ends it (as cancelled).</summary>
    public bool Hang { get; init; }

    /// <summary>Events a test writes while the run is alive (heartbeats); the run goes on once the writer completes.</summary>
    public ChannelReader<AgentEvent>? LiveEvents { get; init; }

    /// <summary>The run waits for this before it ends (cancellation still ends it).</summary>
    public TaskCompletionSource? Gate { get; init; }

    /// <summary>Completed as soon as the run starts (after the provider's queue).</summary>
    public TaskCompletionSource? Started { get; init; }

    /// <summary>Thrown by <c>StartAsync</c>: the CLI could not be launched.</summary>
    public Exception? ThrowOnStart { get; init; }

    /// <summary>Called when the run starts, with its request (for example, to change the user's repository behind the engine's back).</summary>
    public Action<AgentRunRequest>? OnStart { get; init; }

    /// <summary>When set, the run reports a rate limit that resets at this time and ends as <see cref="AgentRunStatus.RateLimited"/>.</summary>
    public DateTimeOffset? RateLimitResetsAt { get; init; }

    public static string Report(
        string status = "completed",
        string summary = "Trabajo terminado.",
        IReadOnlyList<string>? filesChanged = null,
        IReadOnlyList<(string Key, string Value)>? outputs = null,
        IReadOnlyList<string>? openIssues = null) =>
        JsonSerializer.Serialize(new
        {
            status,
            summary,
            filesChanged = filesChanged ?? [],
            claims = new[] { "Hecho según el objetivo." },
            openIssues = openIssues ?? [],
            outputs = (outputs ?? []).Select(o => new { key = o.Key, value = o.Value }).ToArray(),
        });

    /// <summary>
    /// Phase 13 (docs/17 D-28): a report blocked with a question, in the schema of the report. Each option is its id, label,
    /// detail, effort (<c>small</c>, <c>medium</c> or <c>large</c>) and whether the agent recommends it.
    /// </summary>
    public static string Asking(
        string text = "¿Guardo las sesiones en SQLite o en ficheros JSON?",
        string why = "Las dos cumplen RQ-01; cambia cómo se migran los datos.",
        IReadOnlyList<(string Id, string Label, string Detail, string Effort, bool Recommended)>? options = null,
        bool allowOther = true,
        string status = "blocked") =>
        JsonSerializer.Serialize(new
        {
            status,
            summary = "Necesito una decisión.",
            filesChanged = Array.Empty<string>(),
            claims = Array.Empty<string>(),
            openIssues = Array.Empty<string>(),
            outputs = Array.Empty<object>(),
            question = new
            {
                text,
                why,
                options = (options ?? [("a", "SQLite", "Un fichero y migraciones con esquema.", "small", true), ("b", "Ficheros JSON", "Sin esquema; migrar es reescribir.", "medium", false)])
                    .Select(o => new { id = o.Id, label = o.Label, detail = o.Detail, effort = o.Effort, recommended = o.Recommended })
                    .ToArray(),
                allowOther,
            },
        });

    public static AgentScript Completed(params (string Path, string Content)[] files) => new()
    {
        Files = files.ToDictionary(f => f.Path, f => f.Content),
        StructuredOutput = Report(filesChanged: [.. files.Select(f => f.Path)]),
    };

    public static AgentScript Failing(string error = "El agente falló.") => new()
    {
        Status = AgentRunStatus.Failed,
        StructuredOutput = null,
        Error = error,
    };

    public static UsageReported Usage(DateTimeOffset at, long input, long output) =>
        new(at, new TokenUsage(
            MetricValue<long>.Exact(input, "scripted"),
            MetricValue<long>.Exact(output, "scripted"),
            MetricValue<long>.Unavailable("n/a"),
            MetricValue<long>.Unavailable("n/a"),
            MetricValue<decimal>.Exact(0.01m, "scripted")));
}

/// <summary>
/// Test-only adapter (it generalises the fakes of the provider tests). Scripts are keyed by agent, task and attempt;
/// the attempt comes from the <c>#intento-N</c> tag the engine puts on every run, or from the number of runs seen
/// for that agent and task when there is no tag (the planner). Compose it through the real
/// <c>AgentProviderPipeline.Compose</c>: the concurrency it measures is the one that reaches the CLI.
/// </summary>
internal sealed class ScriptedAgentProvider : IAgentProvider
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Agent, string? Task, int Attempt), AgentScript> _scripts = [];
    private readonly Dictionary<(string Agent, string? Task), int> _counts = [];
    private readonly List<(AgentRunRequest Request, int Attempt)> _starts = [];
    private readonly TimeProvider _time;
    private int _active;
    private int _maxActive;

    public ScriptedAgentProvider(ProviderId provider, VendorId vendor, TimeProvider time, int maxConcurrentRuns = 2)
    {
        Provider = provider;
        Vendor = vendor;
        _time = time;
        Capabilities = new ProviderCapabilities(true, true, true, false, true, false, maxConcurrentRuns);
    }

    public ProviderId Provider { get; }

    public VendorId Vendor { get; }

    public ProviderCapabilities Capabilities { get; }

    /// <summary>The script for runs nobody scripted; by default a completed report.</summary>
    public Func<AgentRunRequest, int, AgentScript>? Fallback { get; set; }

    public int MaxObservedConcurrency => Volatile.Read(ref _maxActive);

    public int Active => Volatile.Read(ref _active);

    public IReadOnlyList<(AgentRunRequest Request, int Attempt)> Starts
    {
        get
        {
            lock (_gate)
            {
                return [.. _starts];
            }
        }
    }

    public static int? AttemptOf(AgentRunRequest request)
    {
        foreach (var tag in request.Tags ?? [])
        {
            if (tag.StartsWith("#intento-", StringComparison.Ordinal)
                && int.TryParse(tag["#intento-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var attempt))
            {
                return attempt;
            }
        }

        return null;
    }

    public void Script(string agent, string? task, int attempt, AgentScript script)
    {
        lock (_gate)
        {
            _scripts[(agent, task, attempt)] = script;
        }
    }

    public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AgentScript script;
        int attempt;
        lock (_gate)
        {
            var key = (request.Agent.Id.Value, request.TaskId);
            var seen = _counts.GetValueOrDefault(key) + 1;
            _counts[key] = seen;
            attempt = AttemptOf(request) ?? seen;
            script = _scripts.GetValueOrDefault((request.Agent.Id.Value, request.TaskId, attempt))
                ?? Fallback?.Invoke(request, attempt)
                ?? AgentScript.Completed();
        }

        if (script.ThrowOnStart is { } failure)
        {
            throw failure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _starts.Add((request, attempt));
            _active++;
            _maxActive = Math.Max(_maxActive, _active);
        }

        return Task.FromResult<IAgentRun>(new ScriptedRun(this, script, request, attempt, _time));
    }

    private void Ended()
    {
        lock (_gate)
        {
            _active--;
        }
    }

    private sealed class ScriptedRun : IAgentRun
    {
        private readonly ScriptedAgentProvider _owner;
        private readonly AgentScript _script;
        private readonly AgentRunRequest _request;
        private readonly int _attempt;
        private readonly TimeProvider _time;
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource<AgentRunOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _body;

        public ScriptedRun(ScriptedAgentProvider owner, AgentScript script, AgentRunRequest request, int attempt, TimeProvider time)
        {
            _owner = owner;
            _script = script;
            _request = request;
            _attempt = attempt;
            _time = time;
            _body = Task.Run(BodyAsync);
        }

        public string RunId { get; } = Guid.NewGuid().ToString("N");

        public IAsyncEnumerable<AgentEvent> Events => _events.Reader.ReadAllAsync();

        public Task<AgentRunOutcome> Completion => _completion.Task;

        public Task CancelAsync() => _cancel.CancelAsync();

        public async ValueTask DisposeAsync()
        {
            await _cancel.CancelAsync();
            try
            {
                await _body;
            }
            catch (Exception)
            {
                // The body never faults; disposal must not throw anyway.
            }
        }

        private async Task BodyAsync()
        {
            var sessionId = _script.SessionId ?? $"session-{_request.Agent.Id.Value}-{_request.TaskId ?? "plan"}-{_attempt}";
            AgentRunOutcome outcome;
            try
            {
                _script.Started?.TrySetResult();
                _script.OnStart?.Invoke(_request);
                _events.Writer.TryWrite(new SessionStarted(_time.GetUtcNow(), sessionId, _request.Agent.Model.Model));
                foreach (var e in _script.Events)
                {
                    _events.Writer.TryWrite(e);
                }

                if (_script.LiveEvents is { } live)
                {
                    await foreach (var e in live.ReadAllAsync(_cancel.Token))
                    {
                        _events.Writer.TryWrite(e);
                    }
                }

                foreach (var (path, content) in _script.Files)
                {
                    var full = Path.Combine(_request.WorkingDirectory, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllTextAsync(full, content, _cancel.Token);
                }

                if (_script.Gate is { } gate)
                {
                    await gate.Task.WaitAsync(_cancel.Token);
                }

                if (_script.Hang)
                {
                    await Task.Delay(Timeout.Infinite, _cancel.Token);
                }

                var status = _script.Status;
                if (_script.RateLimitResetsAt is { } resetsAt)
                {
                    _events.Writer.TryWrite(new RateLimitSignaled(_time.GetUtcNow(), "rejected", resetsAt));
                    status = AgentRunStatus.RateLimited;
                }

                _cancel.Token.ThrowIfCancellationRequested();
                var structured = status == AgentRunStatus.Succeeded ? _script.StructuredOutput : null;
                outcome = new AgentRunOutcome(status, 0, _script.FinalText, structured, sessionId, _script.Error);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                outcome = new AgentRunOutcome(AgentRunStatus.Cancelled, -1, null, null, sessionId, "Ejecución cancelada.");
            }
            catch (Exception ex)
            {
                outcome = new AgentRunOutcome(AgentRunStatus.Failed, 1, null, null, sessionId, ex.Message);
            }

            _owner.Ended();
            _events.Writer.TryWrite(new RunFinished(_time.GetUtcNow(), outcome));
            _events.Writer.TryComplete();
            _completion.TrySetResult(outcome);
        }
    }
}
