using System.Collections.Concurrent;
using AgentStudio.Application.Skills;

namespace AgentStudio.Application.Providers;

/// <summary>
/// The OKs to spend quota already used (docs/14 D-29, docs/17 D-20, D-35 and D-44): one register for every auxiliary run that
/// asks the person, so an OK given for one use (the assisted skill recommendation, «Revisar el orden», a message of the chat of
/// preparation, the analysis of consumption with an agent) is never spent twice, by the same service or by another one.
/// </summary>
/// <remarks>
/// It lives in memory, one per app (the composition registers it once): after a restart only the expiry of each OK protects,
/// ten minutes from when it was given (<c>SkillRecommendationOptions.ConsentValidity</c>, <c>OrderReviewService.ConsentValidity</c>,
/// <c>PreparationChatService.ConsentValidity</c>). Each service checks that expiry before it spends the OK here.
/// </remarks>
public interface IQuotaConsentLedger
{
    /// <summary>Spends <paramref name="consent"/>, atomically: true the first time, false when it was spent already.</summary>
    bool TrySpend(QuotaConsent consent);
}

/// <summary>The register of the OKs spent, in memory and thread-safe (<see cref="IQuotaConsentLedger"/>).</summary>
public sealed class QuotaConsentLedger : IQuotaConsentLedger
{
    private readonly ConcurrentDictionary<QuotaConsent, byte> _spent = new();

    public bool TrySpend(QuotaConsent consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        return _spent.TryAdd(consent, 0);
    }
}
