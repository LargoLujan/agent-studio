using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>How a scripted turn answers: its status, its structured output, its usage, its error and when its usage limit resets.</summary>
internal sealed record ScriptedDebateReply(AgentRunStatus Status, string? Json, TokenUsage? Usage, string? Error = null, DateTimeOffset? ResetsAt = null);

/// <summary>
/// A CLI whose turns are scripted, safe to launch from parallel turns. Every request is kept, so a test can check what a turn
/// was asked; with <see cref="Hold"/> every run waits until <see cref="Release"/> (or until it is cancelled), so a test can
/// see that the turns of a round all started before any of them ended.
/// </summary>
internal sealed class ScriptedDebateProvider(ProviderId provider, VendorId vendor, int maxConcurrentRuns = 2) : IAgentProvider
{
    private readonly Lock _gate = new();
    private readonly List<AgentRunRequest> _requests = [];
    private readonly List<ScriptedDebateRun> _runs = [];

    public ProviderId Provider => provider;

    public VendorId Vendor => vendor;

    public ProviderCapabilities Capabilities { get; } = new(
        StreamsEvents: true,
        SupportsStructuredOutput: true,
        SupportsResume: true,
        SupportsInteractiveApproval: false,
        ReportsTokenUsage: true,
        ReportsSubscriptionQuota: false,
        MaxConcurrentRuns: maxConcurrentRuns)
    {
        ReportsContextOccupancy = provider == ProviderId.ClaudeCode,
    };

    /// <summary>What each launch answers (the request and its 1-based number among this provider's launches); by default a valid contribution.</summary>
    public Func<AgentRunRequest, int, ScriptedDebateReply> Reply { get; set; } = (request, _) => Valid(request);

    /// <summary>When set, every run waits until it is released or cancelled.</summary>
    public bool Hold { get; set; }

    public IReadOnlyList<AgentRunRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<ScriptedDebateRun> Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>Lets every held run end (and the ones launched later, until <see cref="Hold"/> is set again).</summary>
    public void Release()
    {
        lock (_gate)
        {
            Hold = false;
            foreach (var run in _runs)
            {
                run.Release();
            }
        }
    }

    public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ScriptedDebateRun run;
        lock (_gate)
        {
            _requests.Add(request);
            run = new ScriptedDebateRun(request, Reply(request, _requests.Count), Hold);
            _runs.Add(run);
        }

        return Task.FromResult<IAgentRun>(run);
    }

    /// <summary>The usage of a turn as a CLI reports it: the whole prompt and the output, exact.</summary>
    public static TokenUsage Usage(long input, long output) => new(
        MetricValue<long>.Exact(input, "scripted input"),
        MetricValue<long>.Exact(output, "scripted output"),
        MetricValue<long>.Exact(0, "scripted cache"),
        MetricValue<long>.NotExposed("scripted window"),
        MetricValue<decimal>.NotExposed("scripted cost"));

    /// <summary>
    /// A valid contribution for the turn: the first kind its schema offers, replying to nothing, with no claims, citations or
    /// concessions; <paramref name="customize"/> changes it before it is written.
    /// </summary>
    public static ScriptedDebateReply Valid(AgentRunRequest request, bool finished = false, Action<JsonObject>? customize = null, TokenUsage? usage = null)
    {
        var answer = Answer(request, finished);
        customize?.Invoke(answer);
        return new ScriptedDebateReply(AgentRunStatus.Succeeded, answer.ToJsonString(), usage ?? Usage(1_000, 100));
    }

    /// <summary>The answer <see cref="Valid"/> writes, before any change.</summary>
    public static JsonObject Answer(AgentRunRequest request, bool finished = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var schema = JsonDocument.Parse(request.OutputJsonSchema ?? "{}");
        var kind = schema.RootElement.GetProperty("properties").GetProperty("kind").GetProperty("enum")[0].GetString();
        return new JsonObject
        {
            ["kind"] = kind,
            ["repliesTo"] = "ninguna",
            ["summary"] = $"Contribución de {request.Agent.Id} en {request.TaskId}.",
            ["argument"] = $"Argumento de {request.Agent.Id} en {request.TaskId}.",
            ["claims"] = new JsonArray(),
            ["citations"] = new JsonArray(),
            ["concedes"] = new JsonArray(),
            ["finished"] = finished,
        };
    }
}

/// <summary>A scripted run: its session, the usage and the rate limit it announces, and its outcome once it is released.</summary>
internal sealed class ScriptedDebateRun : IAgentRun
{
    private readonly ScriptedDebateReply _reply;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScriptedDebateRun(AgentRunRequest request, ScriptedDebateReply reply, bool hold)
    {
        Request = request;
        _reply = reply;
        if (!hold)
        {
            _released.TrySetResult();
        }

        Completion = EndAsync();
    }

    public AgentRunRequest Request { get; }

    /// <summary>What this run was scripted to answer.</summary>
    public ScriptedDebateReply Reply => _reply;

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public string SessionId { get; } = Guid.NewGuid().ToString();

    public bool CancelRequested { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Whether the run is still held (it has not ended).</summary>
    public bool Running => !Completion.IsCompleted;

    public IAsyncEnumerable<AgentEvent> Events => EmitAsync();

    public Task<AgentRunOutcome> Completion { get; }

    public void Release() => _released.TrySetResult();

    public Task CancelAsync()
    {
        CancelRequested = true;
        _cancelled.TrySetResult();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _cancelled.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private async Task<AgentRunOutcome> EndAsync()
    {
        var first = await Task.WhenAny(_released.Task, _cancelled.Task).ConfigureAwait(false);
        if (first == _cancelled.Task)
        {
            return new AgentRunOutcome(AgentRunStatus.Cancelled, null, null, null, SessionId, "The run was cancelled.");
        }

        return new AgentRunOutcome(_reply.Status, _reply.Status == AgentRunStatus.Succeeded ? 0 : 1, _reply.Json, _reply.Status == AgentRunStatus.Succeeded ? _reply.Json : null, SessionId, _reply.Error);
    }

    private async IAsyncEnumerable<AgentEvent> EmitAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var at = DateTimeOffset.UnixEpoch;
        yield return new SessionStarted(at, SessionId, Request.Agent.Model.Model);
        await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_reply.ResetsAt is { } resets)
        {
            yield return new RateLimitSignaled(at, "rejected", resets);
        }

        if (_reply.Usage is { } usage && Completion.Result.Status != AgentRunStatus.Cancelled)
        {
            yield return new UsageReported(at, usage);
        }
    }
}
