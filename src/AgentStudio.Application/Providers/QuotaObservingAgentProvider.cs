using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

/// <summary>
/// Decorator that keeps the subscription quota a provider announces in its runs (docs/14 D-14): every
/// <see cref="QuotaReported"/>, of any run, auxiliary ones included, is appended to the user's record of quota observations
/// with the provider's own utilization. Claude Code announces its windows at the start of every run
/// (<c>rate_limit_event</c>); no extra call is ever made.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It passes every event on as it comes and adds none; the record is written as the event goes by.</item>
/// <item>The record only shows (nothing decides on it), so a failure to write it never touches the run.</item>
/// <item>The pipeline puts it inside the journal, whose pump reads the whole stream of every run, so it sees the quota of
/// a run nobody reads (<c>AgentProviderPipeline</c>).</item>
/// <item>What the provider does not say stays unsaid: Claude gives no length nor display name for its windows.</item>
/// </list>
/// </remarks>
public sealed class QuotaObservingAgentProvider(IAgentProvider inner, IQuotaObservations observations) : IAgentProvider
{
    public IAgentProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    public ProviderId Provider => Inner.Provider;

    public VendorId Vendor => Inner.Vendor;

    public ProviderCapabilities Capabilities => Inner.Capabilities;

    public async Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var run = await Inner.StartAsync(request, cancellationToken).ConfigureAwait(false);
        return new ObservedRun(run, this);
    }

    /// <summary>The observations of one <see cref="QuotaReported"/>: one per window, at the time the event came.</summary>
    public static IReadOnlyList<QuotaObservation> ObservationsOf(ProviderId provider, QuotaReported reported)
    {
        ArgumentNullException.ThrowIfNull(reported);
        return [.. reported.Windows.Select(w => new QuotaObservation(provider, w.Name, w.Utilization, null, w.ResetsAt, reported.At, w.Utilization.Source))];
    }

    private async Task KeepAsync(QuotaReported reported)
    {
        try
        {
            var observed = ObservationsOf(Provider, reported);
            if (observed.Count > 0)
            {
                await observations.AppendAsync(observed, CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The record only shows: losing an observation must never fail or slow down the run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Debug.WriteLine($"Quota observations: could not keep the quota of {Provider}: {ex.Message}");
        }
    }

    private sealed class ObservedRun(IAgentRun run, QuotaObservingAgentProvider owner) : IAgentRun
    {
        public string RunId => run.RunId;

        public IAsyncEnumerable<AgentEvent> Events => ObserveAsync();

        public Task<AgentRunOutcome> Completion => run.Completion;

        public Task CancelAsync() => run.CancelAsync();

        public ValueTask DisposeAsync() => run.DisposeAsync();

        private async IAsyncEnumerable<AgentEvent> ObserveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var agentEvent in run.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (agentEvent is QuotaReported reported)
                {
                    await owner.KeepAsync(reported).ConfigureAwait(false);
                }

                yield return agentEvent;
            }
        }
    }
}
