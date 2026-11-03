using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

/// <summary>
/// Decorator that limits how many runs of one provider execute at the same time. The permit is taken before the
/// inner provider starts and released when the run's <see cref="IAgentRun.Completion"/> ends (success, failure or
/// cancellation) or when the run is disposed, not when <see cref="StartAsync"/> returns. Waiting honours cancellation.
/// </summary>
public sealed class ConcurrencyLimitedAgentProvider : IAgentProvider
{
    private readonly SemaphoreSlim _permits;

    /// <param name="inner">Provider to limit.</param>
    /// <param name="maxConcurrent">Maximum simultaneous runs; defaults to <c>Capabilities.MaxConcurrentRuns</c>; never below 1.</param>
    public ConcurrencyLimitedAgentProvider(IAgentProvider inner, int? maxConcurrent = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
        Limit = Math.Max(1, maxConcurrent ?? inner.Capabilities.MaxConcurrentRuns);
        _permits = new SemaphoreSlim(Limit, Limit);
    }

    public IAgentProvider Inner { get; }

    public int Limit { get; }

    public ProviderId Provider => Inner.Provider;

    public VendorId Vendor => Inner.Vendor;

    public ProviderCapabilities Capabilities => Inner.Capabilities;

    public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        await _permits.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await Inner.StartAsync(request, cancellationToken).ConfigureAwait(false);
            return new LimitedRun(run, _permits);
        }
        catch
        {
            _permits.Release();
            throw;
        }
    }

    private sealed class LimitedRun : IAgentRun
    {
        private readonly IAgentRun _run;
        private readonly SemaphoreSlim _permits;
        private int _released;

        public LimitedRun(IAgentRun run, SemaphoreSlim permits)
        {
            _run = run;
            _permits = permits;
            Completion = ObserveAsync();
        }

        public string RunId => _run.RunId;

        public IAsyncEnumerable<AgentEvent> Events => _run.Events;

        public Task<AgentRunOutcome> Completion { get; }

        public Task CancelAsync() => _run.CancelAsync();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _run.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Release();
            }
        }

        private async Task<AgentRunOutcome> ObserveAsync()
        {
            try
            {
                return await _run.Completion.ConfigureAwait(false);
            }
            finally
            {
                Release();
            }
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _permits.Release();
            }
        }
    }
}
