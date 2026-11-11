using AgentStudio.Domain.Models;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Observability.Estimates;

/// <summary>
/// Real against estimated for a plan or a milestone (docs/16 D-20, F9-7: «real frente a estimado al cerrar»): per figure, whether what was
/// spent is inside, below or above the interval the system estimated, or <see cref="EstimateDeviation.Unknown"/>.
/// </summary>
/// <param name="InputTokens">Input tokens against the interval of the estimate.</param>
/// <param name="OutputTokens">Output tokens against the interval of the estimate.</param>
/// <param name="AgentRuns">Launches of an agent against the interval of the estimate.</param>
public sealed record EstimateDeviations(EstimateDeviation InputTokens, EstimateDeviation OutputTokens, EstimateDeviation AgentRuns);

/// <summary>
/// What the estimator does not do by itself, built on top of it (<see cref="UsageEstimation"/>, ADR-0050) and never beside it: the
/// estimate of an attempt from the history of one activity or one milestone, and the comparison of what was spent with what a plan was
/// estimated at. Pure: it reads attempts and estimates it is handed, no store, no clock, no quota.
/// </summary>
public static class ScopedEstimation
{
    /// <summary>
    /// What one attempt would spend, from the attempts of the history that were of <paramref name="kind"/> (docs/16 D-34), with the rules of
    /// <see cref="UsageEstimation.Estimate"/>: fewer than three references is «desconocida». An attempt with no activity is of none.
    /// </summary>
    public static UsageEstimate EstimateForActivity(IReadOnlyList<UsageAttempt> attempts, ModelCatalog catalog, UsageEstimateRequest request, ActivityKind kind)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        return UsageEstimation.Estimate([.. attempts.Where(a => a.Activity?.Kind == kind)], catalog, request);
    }

    /// <summary>What one attempt would spend, from the attempts of one milestone as the log links them (shared ones count in each).</summary>
    public static UsageEstimate EstimateForMilestone(IReadOnlyList<UsageAttempt> attempts, ModelCatalog catalog, UsageEstimateRequest request, string milestone)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentException.ThrowIfNullOrWhiteSpace(milestone);
        return UsageEstimation.Estimate([.. attempts.Where(a => a.Milestones.Contains(milestone, StringComparer.Ordinal))], catalog, request);
    }

    /// <summary>
    /// What was spent against what the plan was estimated at. A figure of the total that has gaps (<see cref="MetricAggregate{T}.Missing"/>
    /// above zero) is compared with nothing: a partial sum under the interval would say «por debajo» of something that may not be.
    /// An interval that is not known is <see cref="EstimateDeviation.Unknown"/>, never compared with a number made up. The duration is not
    /// compared: the plan's follows its critical path and the total adds the attempts that ran at once.
    /// </summary>
    public static EstimateDeviations CompareWithPlan(PlanEstimate estimate, UsageTotals actual)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(actual);
        return new EstimateDeviations(
            Compare(estimate.InputTokens, actual.InputTokens.Missing == 0 ? actual.InputTokens.Sum : null),
            Compare(estimate.OutputTokens, actual.OutputTokens.Missing == 0 ? actual.OutputTokens.Sum : null),
            Compare(estimate.AgentRuns, actual.Attempts));
    }

    /// <summary>One attempt, or the sum of a group, against an estimated interval of tokens or launches.</summary>
    public static EstimateDeviation Compare(EstimateInterval<long> interval, long? actual)
    {
        ArgumentNullException.ThrowIfNull(interval);
        return interval.IsKnown ? EstimateComparison.Compare(interval.Low.Value, interval.High.Value, actual) : EstimateDeviation.Unknown;
    }
}
