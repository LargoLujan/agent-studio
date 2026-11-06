using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Usage;

// Phase 13 contracts of the estimator (docs/17 D-23, D-32 and D-18 cost.over-budget), brought forward from F9-7 of Phase 9
// (docs/16 D-20). The estimate is computed by the system from the verified log of this project: similar attempts (same
// agent, same model reported, same purpose), the 25th and 75th percentiles, the number of references and the assumptions.
// With fewer than three references it is «desconocida»: never a number made up. There is no table of prices (docs/14 D-06):
// the cost is the CLI's list price as the log recorded it, and Codex says «no expuesto». WP3 implements it
// (Observability/Estimates).

/// <summary>The size of the work an estimate is for: the effort an agent declares for an option of a question (D-32), or a task by its kind.</summary>
public enum WorkSize
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// An interval from the 25th to the 75th percentile, each end with its origin: «estimado por Agent Studio» when known, or a
/// figure without a value that says why («desconocida: 2 referencias»). A figure that is not available is never zero.
/// </summary>
public sealed record EstimateInterval<T>(MetricValue<T> Low, MetricValue<T> High)
    where T : struct
{
    /// <summary>Whether both ends have a value.</summary>
    public bool IsKnown => Low.HasValue && High.HasValue;

    /// <summary>An interval that is not known, and why.</summary>
    public static EstimateInterval<T> Unknown(string reason) => new(MetricValue<T>.Missing(reason), MetricValue<T>.Missing(reason));
}

/// <summary>Stable codes of the assumptions of an estimate, which the desktop says with its resources.</summary>
public static class EstimateAssumptions
{
    /// <summary>The references are attempts of the same agent and the same model reported.</summary>
    public const string SameAgentAndModel = "estimate.same-agent-and-model";

    /// <summary>The references are attempts of the same purpose (an executor, the judge…).</summary>
    public const string SamePurpose = "estimate.same-purpose";

    /// <summary>The references are attempts of the same size of work, as the agent declared it (D-32).</summary>
    public const string SameSize = "estimate.same-size";

    /// <summary>The duration of a plan follows its critical path, with the tasks at once and the concurrency of each provider.</summary>
    public const string CriticalPath = "estimate.critical-path";

    /// <summary>The cost is the list price the CLI reported; the vendor bills by subscription.</summary>
    public const string ListPrice = "estimate.list-price";

    /// <summary>The CLI does not expose the cost (Codex): it is not estimated.</summary>
    public const string CostNotExposed = "estimate.cost-not-exposed";
}

/// <summary>
/// What one attempt is estimated to spend (docs/16 D-20): input and output tokens, duration and cost, each as an interval
/// with its origin, the number of attempts it rests on and its assumptions. Known only with at least
/// <see cref="MinimumReferences"/> references.
/// </summary>
public sealed record UsageEstimate(
    int References,
    EstimateInterval<long> InputTokens,
    EstimateInterval<long> OutputTokens,
    EstimateInterval<TimeSpan> Duration,
    EstimateInterval<decimal> CostUsd,
    IReadOnlyList<string> Assumptions)
{
    /// <summary>The fewest references that make an estimate known (docs/16 D-20).</summary>
    public const int MinimumReferences = 3;

    /// <summary>Whether there are enough references to say a number.</summary>
    public bool IsKnown => References >= MinimumReferences;

    /// <summary>
    /// An estimate that is not known: with fewer than three references, every figure is <see cref="MetricOrigin.Missing"/>
    /// and its source says why, in technical words; the desktop says «desconocida» with its resources.
    /// </summary>
    public static UsageEstimate Unknown(int references, IReadOnlyList<string>? assumptions = null)
    {
        var reason = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"unknown: {references} of {MinimumReferences} references");
        return new UsageEstimate(
            references,
            EstimateInterval<long>.Unknown(reason),
            EstimateInterval<long>.Unknown(reason),
            EstimateInterval<TimeSpan>.Unknown(reason),
            EstimateInterval<decimal>.Unknown(reason),
            assumptions ?? []);
    }
}

/// <summary>What one attempt of an agent would spend: of what agent, on what CLI and model, for what purpose and, when known, of what size.</summary>
public sealed record UsageEstimateRequest(
    ProjectContext Project,
    AgentId Agent,
    ProviderId Provider,
    string? Model,
    AgentRunPurpose Purpose,
    WorkSize? Size = null);

/// <summary>What a plan would spend (docs/16 D-20): the estimate of each task, the agent runs and its duration by the critical path.</summary>
public sealed record PlanEstimateRequest(
    ProjectContext Project,
    PlanDefinition Plan,
    IReadOnlyList<AgentDefinition> Roster,
    RunOptions Options,
    IReadOnlyDictionary<ProviderId, int>? ProviderConcurrency = null);

/// <summary>
/// The estimate of a plan: of each task, the agent runs it is expected to take (with the retries of similar tasks, which
/// <c>cost.over-budget</c> compares with <see cref="RunOptions.MaxAgentRuns"/>), the tokens and the duration by its critical
/// path. A wait for a window of quota is said apart («puede esperar hasta las HH:MM»): tokens are never turned into a share of
/// quota, because that equivalence does not exist.
/// </summary>
public sealed record PlanEstimate(
    IReadOnlyDictionary<TaskId, UsageEstimate> Tasks,
    EstimateInterval<long> AgentRuns,
    EstimateInterval<long> InputTokens,
    EstimateInterval<long> OutputTokens,
    EstimateInterval<TimeSpan> Duration,
    IReadOnlyList<string> Assumptions);

/// <summary>
/// The estimator of the system (docs/16 D-20, brought forward to Phase 13). It reads only the verified log, launches nothing
/// and spends no quota; a log that does not verify fails closed, as the memory does.
/// </summary>
public interface IUsageEstimator
{
    /// <summary>What one attempt would spend.</summary>
    Task<UsageEstimate> EstimateAsync(UsageEstimateRequest request, CancellationToken ct);

    /// <summary>What a plan would spend.</summary>
    Task<PlanEstimate> EstimatePlanAsync(PlanEstimateRequest request, CancellationToken ct);
}
