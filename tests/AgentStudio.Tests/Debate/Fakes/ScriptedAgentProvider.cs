using System.Runtime.CompilerServices;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>
/// A provider whose runs are scripted per launch (1-based): what events they emit and how they end. It keeps every
/// request it received, so a test can check what the judge really asked for.
/// </summary>
internal sealed class ScriptedAgentProvider(ProviderId provider, VendorId vendor, Func<AgentRunRequest, int, ScriptedRun> script) : IAgentProvider
{
    public List<AgentRunRequest> Requests { get; } = [];

    public List<ScriptedRun> Runs { get; } = [];

    public ProviderId Provider => provider;

    public VendorId Vendor => vendor;

    public ProviderCapabilities Capabilities { get; init; } = new(
        StreamsEvents: true,
        SupportsStructuredOutput: true,
        SupportsResume: true,
        SupportsInteractiveApproval: false,
        ReportsTokenUsage: true,
        ReportsSubscriptionQuota: false,
        MaxConcurrentRuns: 1);

    /// <summary>When set, the next launch throws this instead of starting.</summary>
    public Exception? FailToStart { get; set; }

    /// <summary>Called with the number of requests seen so far, at the moment a launch is asked for.</summary>
    public Action<int>? OnStart { get; set; }

    public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        OnStart?.Invoke(Requests.Count);
        Requests.Add(request);
        if (FailToStart is { } failure)
        {
            FailToStart = null;
            throw failure;
        }

        var run = script(request, Requests.Count);
        Runs.Add(run);
        return Task.FromResult<IAgentRun>(run);
    }
}

/// <summary>A run that emits its events and ends with its outcome; <see cref="Hanging"/> waits until it is cancelled.</summary>
internal sealed class ScriptedRun : IAgentRun
{
    private readonly IReadOnlyList<AgentEvent> _events;
    private readonly bool _hang;
    private readonly TaskCompletionSource<AgentRunOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancelled = new();

    public ScriptedRun(AgentRunOutcome outcome, params AgentEvent[] events)
        : this(outcome, hang: false, events)
    {
    }

    private ScriptedRun(AgentRunOutcome outcome, bool hang, IReadOnlyList<AgentEvent> events)
    {
        _events = events;
        _hang = hang;
        if (!hang)
        {
            _completion.SetResult(outcome);
        }
    }

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public bool CancelRequested { get; private set; }

    public bool Disposed { get; private set; }

    public IAsyncEnumerable<AgentEvent> Events => EmitAsync();

    public Task<AgentRunOutcome> Completion => _completion.Task;

    public static ScriptedRun Hanging(params AgentEvent[] events) =>
        new(new AgentRunOutcome(AgentRunStatus.Cancelled, null, null, null, null, "cancelled"), hang: true, events);

    public Task CancelAsync()
    {
        CancelRequested = true;
        _cancelled.Cancel();
        _completion.TrySetResult(new AgentRunOutcome(AgentRunStatus.Cancelled, null, null, null, null, "The run was cancelled."));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _cancelled.Dispose();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<AgentEvent> EmitAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var evt in _events)
        {
            await Task.Yield();
            yield return evt;
        }

        if (_hang)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancelled.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
    }
}

/// <summary>The providers of a project, as the composition root hands them out; it remembers which roots were asked for.</summary>
internal sealed class FakeProjectAgentProviders(params IAgentProvider[] providers) : IProjectAgentProviders
{
    public List<string> Roots { get; } = [];

    public IReadOnlyList<IAgentProvider> For(string projectRoot)
    {
        Roots.Add(projectRoot);
        return providers;
    }
}

/// <summary>Collects reported values on the calling thread (Progress&lt;T&gt; would post them elsewhere).</summary>
internal sealed class ListProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = [];

    public void Report(T value) => Items.Add(value);
}
