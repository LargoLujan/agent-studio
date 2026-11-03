using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;

namespace AgentStudio.Providers.Composition;

/// <summary>
/// The only way to turn a concrete adapter into the provider the rest of the app uses. The chain is always
/// <c>Concurrency(Journaling(QuotaObservation(adapter)))</c>, the quota observation only when there is a record to keep it:
/// <list type="bullet">
/// <item>Journaling is the inner decorator, so no prompt reaches the adapter without being recorded (fail closed).</item>
/// <item>The quota observation sits inside the journal, whose pump reads every event of every run: the quota a run
/// announces is kept even when nobody reads its events (docs/14 D-14).</item>
/// <item>Concurrency is the outer one, so a run waiting in the queue has not started yet: its journal entry (and its
/// start time and duration) is written when the run really begins, after the queue, not while it waits.</item>
/// </list>
/// </summary>
public static class AgentProviderPipeline
{
    /// <param name="quota">The user's record of quota observations; null composes without it.</param>
    public static IAgentProvider Compose(IAgentProvider adapter, IPromptJournal journal, TimeProvider time, int? maxConcurrent = null, IQuotaObservations? quota = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(time);
        var observed = quota is null ? adapter : new QuotaObservingAgentProvider(adapter, quota);
        var journaled = new JournalingAgentProvider(observed, journal, time);
        return new ConcurrencyLimitedAgentProvider(journaled, maxConcurrent);
    }

    /// <summary>True when the decorator chain of <paramref name="provider"/> contains the prompt-journal decorator (criterion F2-9).</summary>
    public static bool IsJournaled(IAgentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider switch
        {
            JournalingAgentProvider => true,
            ConcurrencyLimitedAgentProvider limited => IsJournaled(limited.Inner),
            _ => false,
        };
    }

    /// <summary>True when the decorator chain of <paramref name="provider"/> keeps the quota its runs announce (docs/14 D-14).</summary>
    public static bool IsQuotaObserved(IAgentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider switch
        {
            QuotaObservingAgentProvider => true,
            JournalingAgentProvider journaled => IsQuotaObserved(journaled.Inner),
            ConcurrencyLimitedAgentProvider limited => IsQuotaObserved(limited.Inner),
            _ => false,
        };
    }
}
