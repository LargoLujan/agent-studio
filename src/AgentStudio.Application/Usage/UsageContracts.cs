using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Usage;

// Phase 4 contracts of the usage panel and its export (docs/14 sections 4.1 and 5.3). The usage is a projection of the
// verified decision log (D-01): AgentRunStarted and AgentRunFinished are the only record of it, there is no usage table,
// and every log is read through IProjectLogReader.

/// <summary>What a usage report groups by (D-04). <see cref="Week"/> is the ISO week; the periods are in the zone of the query.</summary>
public enum UsageDimension
{
    Provider,
    Vendor,

    /// <summary>The model the CLI reported; else the configured one; else «predeterminado del CLI (no expuesto)».</summary>
    Model,

    /// <summary>The CLI session: the pair (provider, session id).</summary>
    Session,

    /// <summary>The orchestration run, or the auxiliary run.</summary>
    Run,
    Task,
    Agent,
    Purpose,
    Project,
    Day,
    Week,
    Month,

    // Phase 9 (docs/16 D-34, ADR-0040), appended. UsageAggregator.KeyOf computes them from UsageAttempt.Milestones and
    // UsageAttempt.Activity; the dimension list of the dashboard (WP5) still has to label them.

    /// <summary>The milestone an attempt is linked to (by <c>MilestoneLinked</c>); an attempt in two milestones is in each, marked shared, and once in the total.</summary>
    Milestone,

    /// <summary>What the attempt was for: the <c>ActivityKind</c> (first axis of the activity).</summary>
    Activity,

    /// <summary>First attempt of its task or retry: the <c>AttemptOrder</c> (second axis).</summary>
    ActivityAttempt,

    /// <summary>What became of the work: the <c>AttemptFate</c> (third axis).</summary>
    ActivityDestination,
}

/// <summary>What a report keeps (D-04). A list that is null or empty lets every value through.</summary>
public sealed record UsageFilter(
    IReadOnlyList<ProviderId>? Providers = null,
    IReadOnlyList<VendorId>? Vendors = null,
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<AgentId>? Agents = null,
    IReadOnlyList<AgentRunPurpose>? Purposes = null,
    IReadOnlyList<string>? Runs = null);

/// <summary>The attempts that finished in [<paramref name="From"/>, <paramref name="To"/>), grouped by <paramref name="GroupBy"/> in that order, in <paramref name="Zone"/>.</summary>
public sealed record UsageQuery(DateTimeOffset From, DateTimeOffset To, TimeZoneInfo Zone, IReadOnlyList<UsageDimension> GroupBy, UsageFilter? Filter = null);

/// <summary>
/// The token and cost figures of an attempt, each with its origin (D-03): its own share, or the total of its session. A
/// figure that could not be told is «sin dato»; a share computed from a total that went down is «estimado por Agent
/// Studio» (D-02).
/// </summary>
public sealed record UsageFigures(
    MetricValue<long> InputTokens,
    MetricValue<long> CacheReadInputTokens,
    MetricValue<long> CacheWriteInputTokens,
    MetricValue<long> OutputTokens,
    MetricValue<long> ReasoningOutputTokens,
    MetricValue<decimal> CostUsd);

/// <summary>The figures of one model of an attempt (Claude <c>modelUsage</c>), with the window of that model and the CLI's cost basis.</summary>
public sealed record ModelUsageFigures(string Model, UsageFigures Figures, MetricValue<long> ContextWindow, string? CostBasis);

/// <summary>The step of an attempt with the largest context (D-12): its model, the tokens used and the window of that model.</summary>
public sealed record ContextPeakFigures(string? Model, MetricValue<long> UsedTokens, MetricValue<long> ContextWindow);

/// <summary>
/// One attempt as the log records it (D-02 and D-04): who, where, when and how it ended, its own usage
/// (<paramref name="Own"/>, deduplicated by session) and, when it shares a session, the session's total.
/// <paramref name="LaunchedAt"/> is when the process started, after any wait in the provider's queue (null before
/// version 2), so the duration is <paramref name="FinishedAt"/> minus it. <see cref="RecordedVersion"/> says which
/// version of <c>AgentRunFinished</c> the figures come from: version 1 has no origins nor breakdown («sin desglose
/// (registro anterior a la Fase 4)»).
/// </summary>
public sealed record UsageAttempt(
    string ProjectId,
    string RunId,
    string? TaskId,
    int Attempt,
    AgentRunPurpose? Purpose,
    AgentId Agent,
    ProviderId Provider,
    VendorId Vendor,
    string? ConfiguredModel,
    string? ReportedModel,
    string? SessionId,
    DateTimeOffset RecordedAt,
    DateTimeOffset? LaunchedAt,
    DateTimeOffset FinishedAt,
    AttemptOutcome Outcome,
    UsageFigures Own,
    UsageFigures? SessionTotal,
    IReadOnlyList<ModelUsageFigures> PerModel,
    ContextPeakFigures? ContextPeak,
    int? Compactions)
{
    /// <summary>The version of the <c>AgentRunFinished</c> entry the figures were read from.</summary>
    public required int RecordedVersion { get; init; }

    /// <summary>
    /// The reasoning effort the agent was set with, as its <c>AgentRunStarted</c> records it (version 3, docs/18 P47, ADR-0050),
    /// read whenever the start carries one, so an attempt at <c>low</c> is told from one at <c>high</c> of the same model; null
    /// when none was set, and for every start written before the field existed.
    /// </summary>
    public ReasoningEffort? Effort { get; init; }

    /// <summary>
    /// Phase 9 (docs/16 D-34, ADR-0040): the ids of the milestones the attempt belongs to, through <c>MilestoneLinked</c> (a run is
    /// linked with its follow-ups). Empty when it is in none; two or more make it shared. The projection fills it.
    /// </summary>
    public IReadOnlyList<string> Milestones { get; init; } = [];

    /// <summary>The value of the attempt on the three axes of the activity (D-34, <c>ActivityClassifier</c>); null for an attempt built by hand.</summary>
    public ActivityClass? Activity { get; init; }

    /// <summary>
    /// How long the task waited for its quota window after this attempt hit the limit, in milliseconds: from the block
    /// <c>RateLimitedUntil</c> to the task leaving it. Measured from the log and never part of the duration of the attempt. Null when
    /// the attempt began no such wait.
    /// </summary>
    public long? QuotaWaitMilliseconds { get; init; }

    /// <summary>The wait for quota this attempt began had not ended when the log ends (the task never left the block), so it has no length yet.</summary>
    public bool OpenQuotaWait { get; init; }
}

/// <summary>
/// The figures of a group of attempts (D-04): counts measured by Agent Studio, and sums that never mix origins silently
/// and say how many values they miss (<see cref="MetricAggregate{T}"/>). The context peak is the largest of the group;
/// compactions that a CLI does not expose are missing, never zero.
/// </summary>
public sealed record UsageTotals(
    int Attempts,
    MetricAggregate<long> DurationMilliseconds,
    MetricAggregate<long> InputTokens,
    MetricAggregate<long> CacheReadInputTokens,
    MetricAggregate<long> CacheWriteInputTokens,
    MetricAggregate<long> OutputTokens,
    MetricAggregate<long> ReasoningOutputTokens,
    MetricAggregate<decimal> CostUsd,
    int RateLimited,
    int LimitReached,
    ContextPeakFigures? ContextPeak,
    MetricAggregate<long> Compactions)
{
    /// <summary>
    /// Phase 9 (D-34): the time the attempts waited in the provider's queue, from <c>AgentRunStarted</c> to <c>LaunchedAt</c>, apart from
    /// their duration. Null in a total built by hand.
    /// </summary>
    public MetricAggregate<long>? QueueWaitMilliseconds { get; init; }

    /// <summary>Phase 9 (D-34): the time the tasks waited for their quota window after these attempts hit a limit, apart from their duration.</summary>
    public MetricAggregate<long>? QuotaWaitMilliseconds { get; init; }
}

/// <summary>The value of one dimension in the key of a group, such as (<see cref="UsageDimension.Model"/>, <c>claude-sonnet-5-5</c>).</summary>
public sealed record UsageGroupKey(UsageDimension Dimension, string Value);

/// <summary>One group of a report: its key, in the order of the query's dimensions (empty for the grand total), and its figures.</summary>
/// <param name="SharedAttempts">
/// Phase 9 (D-34): how many of the attempts of the group also belong to another group of the same dimension (a milestone). The
/// groups of that dimension add up to more than the grand total, which counts each attempt once, but not by the sum of this number:
/// an attempt of n milestones is in n groups and marked shared in each, so it adds n - 1 to the excess and n to the sum of this number.
/// Only when every shared attempt belongs to exactly two milestones is the excess half of that sum.
/// </param>
public sealed record UsageGroup(IReadOnlyList<UsageGroupKey> Key, UsageTotals Totals, int SharedAttempts = 0);

/// <summary>A project left out of a report and why (D-05): its log does not verify, no anchor recognises it, or it cannot be read.</summary>
public sealed record ExcludedProject(string Root, string? ProjectId, string Reason);

/// <summary>
/// The prompts of the journal of the period against the attempts of the log (D-08): a difference is shown, never counted
/// (live tests, an earlier version...). <paramref name="JournalPrompts"/> is null when there was no journal to read.
/// </summary>
public sealed record UsageReconciliation(int? JournalPrompts, int RecordedAttempts, IReadOnlyList<string> Warnings)
{
    /// <summary>How many more prompts the journal has than the log has attempts; null without a journal.</summary>
    public int? Difference => JournalPrompts - RecordedAttempts;
}

/// <summary>A usage report: the attempts, the groups, the projects left out and the reconciliation with the journal.</summary>
public sealed record UsageReport(IReadOnlyList<UsageAttempt> Attempts, IReadOnlyList<UsageGroup> Groups, IReadOnlyList<ExcludedProject> Excluded, UsageReconciliation Journal);

/// <summary>Projects the usage of the given projects from their verified logs (D-01 and D-05), each read through <c>IProjectLogReader</c>.</summary>
public interface IUsageReport
{
    Task<UsageReport> QueryAsync(IReadOnlyList<string> projectRoots, UsageQuery query, CancellationToken ct);
}

/// <summary>
/// Writes a report (D-07): CSV per RFC 4180, UTF-8 with BOM, invariant culture and three columns per figure (value,
/// origin, source); JSON <c>agentstudio-usage/1</c>. Text fields that start like a formula are escaped, and no prompt nor
/// agent output is ever written.
/// </summary>
public interface IUsageExporter
{
    Task WriteCsvAsync(UsageReport report, Stream destination, CancellationToken ct);

    Task WriteJsonAsync(UsageReport report, Stream destination, CancellationToken ct);
}
