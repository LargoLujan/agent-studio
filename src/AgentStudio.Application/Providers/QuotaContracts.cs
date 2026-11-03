using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

// Phase 4 contracts of the subscription quota (docs/14 section 4.3, D-14 to D-16). The quota belongs to the account, not
// to a project, so it is kept in the user's profile and never in a project's log.

/// <summary>
/// One usage window of a subscription as its provider reported it at one moment: Claude's <c>rate_limit_event</c> windows
/// (<c>five_hour</c>, <c>seven_day</c>) and Codex's <c>account/rateLimits/read</c> buckets (<c>primary</c>,
/// <c>secondary</c> of each limit). The utilization (0..1) is the provider's own figure, never one Agent Studio computed
/// or guessed. <paramref name="Length"/> and <paramref name="ResetsAt"/> are null when the provider does not say them;
/// <paramref name="Source"/> names the message the observation came from.
/// </summary>
public sealed record QuotaObservation(
    ProviderId Provider,
    string Window,
    MetricValue<double> Utilization,
    TimeSpan? Length,
    DateTimeOffset? ResetsAt,
    DateTimeOffset ObservedAt,
    string Source)
{
    /// <summary>The name the provider shows for the window or its limit (Codex <c>limitName</c>), when it gives one.</summary>
    public string? Label { get; init; }

    /// <summary>The limit the provider says was reached (Claude <c>status</c>, Codex <c>rateLimitReachedType</c>), when it says so.</summary>
    public string? Reached { get; init; }
}

/// <summary>
/// The user's record of quota observations (D-14), <c>%LOCALAPPDATA%\AgentStudio\usage\quota.jsonl</c>: append-only and
/// rotated by size. Every observation of any run goes there, auxiliary runs included; the panel shows the latest of each
/// window with its age. It is only shown: nothing decides on it.
/// </summary>
public interface IQuotaObservations
{
    Task AppendAsync(IReadOnlyList<QuotaObservation> observations, CancellationToken ct);

    /// <summary>The latest observation of each window of each provider.</summary>
    Task<IReadOnlyList<QuotaObservation>> LatestAsync(CancellationToken ct);
}

/// <summary>How an active read of the quota went (D-15 and D-16).</summary>
public enum QuotaReadStatus
{
    /// <summary>The provider answered: the observations hold its windows.</summary>
    Read,

    /// <summary>The user did not opt in, so nothing was launched.</summary>
    NotOptedIn,

    /// <summary>The provider does not expose the quota here: «no expuesto» (Codex with an API key, Gemini), with the reason.</summary>
    NotExposed,

    /// <summary>The read failed or timed out: «sin dato», with the reason.</summary>
    Missing,
}

/// <summary>
/// The answer of an active quota read, from a whitelist of the provider's response (D-15): the account id, upsells,
/// credit balances and reset-credit details are never kept. <see cref="Warnings"/> carries, in Spanish, what the user
/// should know, such as «versión de Codex no probada».
/// </summary>
public sealed record QuotaReadResult(QuotaReadStatus Status, IReadOnlyList<QuotaObservation> Observations, string? Reason = null)
{
    /// <summary>The plan of the account (Codex <c>planType</c>), when the provider says it.</summary>
    public string? PlanType { get; init; }

    /// <summary>Whether ordinary usage is still allowed (Codex <c>ordinaryUsageAllowed</c>), when the provider says it.</summary>
    public bool? OrdinaryUsageAllowed { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Reads the quota of one provider on demand (D-15): today only Codex, through <c>account/rateLimits/read</c> of its
/// app-server, and only when the user opted in. A read never starts a turn of the model and never spends a stored reset.
/// </summary>
public interface IProviderQuotaReader
{
    ProviderId Provider { get; }

    Task<QuotaReadResult> ReadAsync(CancellationToken ct);
}
