namespace AgentStudio.Domain.Metrics;

// Pure and free of I/O, so it lives in the domain (docs/14 D-11): the engine can use it once the deferred retry policy
// arrives, and ContextHealthTracker (Observability) evaluates each step with it.

public enum ContextHealthLevel
{
    /// <summary>Usage or window size is not known, so no judgement is made (never shown as "healthy").</summary>
    Unknown,
    Healthy,
    Warning,
    CompressionRecommended,
    Critical,
}

/// <summary>
/// Utilization fractions (0..1) at which the context window changes level. Must satisfy
/// 0 &lt; Warning &lt; Compress &lt; Critical &lt;= 1.
/// </summary>
public sealed record ContextHealthThresholds(double Warning = 0.70, double Compress = 0.85, double Critical = 0.95)
{
    public double Warning { get; init; } = Warning;

    public double Compress { get; init; } = Compress;

    public double Critical { get; init; } = Validate(Warning, Compress, Critical);

    internal void EnsureValid() => Validate(Warning, Compress, Critical);

    private static double Validate(double warning, double compress, double critical)
    {
        if (!(warning > 0 && warning < compress && compress < critical && critical <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(warning),
                $"Thresholds must satisfy 0 < Warning < Compress < Critical <= 1 (got {warning}, {compress}, {critical}).");
        }

        return critical;
    }
}

public sealed record ContextHealthReport(MetricValue<double> Utilization, ContextHealthLevel Level);

/// <summary>
/// Turns "tokens used" and "context window" into a utilization and a health level. An estimate is never reported as
/// exact and a missing figure is reported as unknown, never as zero. The utilization takes the weakest origin of its two
/// figures (docs/14 D-03): a window seen in an earlier run makes it an estimate of Agent Studio (D-10), a figure the CLI
/// does not expose makes it not exposed, and one that did not arrive, or arrived unusable, makes it missing.
/// </summary>
public sealed class ContextHealthEvaluator
{
    private readonly ContextHealthThresholds _thresholds;

    public ContextHealthEvaluator(ContextHealthThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        thresholds.EnsureValid(); // a `with` expression could have bypassed the constructor check
        _thresholds = thresholds;
    }

    public ContextHealthReport Evaluate(MetricValue<long> usedTokens, MetricValue<long> contextWindow)
    {
        if (usedTokens.Value is not { } used || contextWindow.Value is not { } window || used < 0 || window <= 0)
        {
            var reason = $"context utilization unavailable (used tokens: {usedTokens.Source}; context window: {contextWindow.Source})";

            // A figure that did not come keeps its own reason; two that came but cannot be used give no figure either.
            var missing = usedTokens.HasValue && contextWindow.HasValue
                ? MetricOrigin.Missing
                : MetricOrigins.Weakest(ReasonOf(usedTokens), ReasonOf(contextWindow));
            return new ContextHealthReport(MetricValue<double>.Of(missing, null, reason), ContextHealthLevel.Unknown);
        }

        var utilization = (double)used / window;
        var source = $"used tokens / context window (used tokens: {usedTokens.Source}; context window: {contextWindow.Source})";
        var metric = MetricValue<double>.Of(MetricOrigins.Weakest(usedTokens.Origin, contextWindow.Origin), utilization, source);

        return new ContextHealthReport(metric, LevelFor(utilization));
    }

    /// <summary>
    /// Why a figure gives no utilization: its own origin when it has no value (a figure without a value that claims to be
    /// reported, measured or estimated counts as missing), and the strongest origin when it has one, so the other decides.
    /// </summary>
    private static MetricOrigin ReasonOf(MetricValue<long> figure) =>
        figure.HasValue ? MetricOrigin.ReportedByProvider
        : figure.Origin.CarriesValue() ? MetricOrigin.Missing
        : figure.Origin;

    private ContextHealthLevel LevelFor(double utilization) =>
        utilization >= _thresholds.Critical ? ContextHealthLevel.Critical
        : utilization >= _thresholds.Compress ? ContextHealthLevel.CompressionRecommended
        : utilization >= _thresholds.Warning ? ContextHealthLevel.Warning
        : ContextHealthLevel.Healthy;
}
