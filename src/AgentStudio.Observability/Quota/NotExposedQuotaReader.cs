using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Observability.Quota;

/// <summary>
/// The quota of a provider that does not expose it (docs/14 D-16): every read answers «no expuesto» with its reason, and
/// nothing is launched. Gemini is parked (ADR-0009) and its CLI has no interface for the plan's quota.
/// </summary>
public sealed class NotExposedQuotaReader : IProviderQuotaReader
{
    private readonly QuotaReadResult _result;

    public NotExposedQuotaReader(ProviderId provider, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Provider = provider;
        _result = new QuotaReadResult(QuotaReadStatus.NotExposed, [], reason);
    }

    /// <summary>Gemini: «no expuesto».</summary>
    public static NotExposedQuotaReader Gemini { get; } = new(ProviderId.GeminiCli, "Gemini CLI no expone la cuota del plan (aparcado, ADR-0009).");

    public ProviderId Provider { get; }

    public Task<QuotaReadResult> ReadAsync(CancellationToken ct) => Task.FromResult(_result);
}
