using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Models;

namespace AgentStudio.Observability.Estimates;

/// <summary>
/// The estimate of the system (docs/16 D-20, F9-7, brought forward to Phase 13; docs/17 D-23, D-32 and F13-11; ADR-0050): pure
/// and deterministic, over the attempts of the verified log of the project (<see cref="UsageProjector"/>, its own usage
/// deduplicated by session). It never makes a number up: with fewer than three references a figure is «desconocida».
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>References of an attempt</b> (<see cref="Estimate"/>): attempts of the same CLI and purpose, with the same model
/// (an alias and its full name, with or without the date the CLI appends, are one model: <see cref="ModelKeys.Of"/>;
/// the default of the CLI matches attempts configured without a model), that ended with a result. First those of the same agent
/// («medido en el proyecto»: <see cref="EstimateAssumptions.SameAgentAndModel"/>); with fewer than three, those of any agent
/// («aprendido del modelo»: <see cref="EstimateAssumptions.SameModel"/>). With fewer than three there too, unknown: there is
/// no base table of consumption, and none is made up.</item>
/// <item><b>Size</b> (D-32): the references are split in thirds by their tokens (input and output), and only the third of the
/// size asked counts (<see cref="EstimateAssumptions.SameSize"/>).</item>
/// <item><b>Total tokens</b> (D-32, what the window of a question shows): the percentiles of the totals of the references,
/// computed as such (<see cref="UsageEstimate.TotalTokens"/>, <see cref="UsageEstimate.ToSnapshot"/>).</item>
/// <item><b>Interval:</b> the 25th and 75th percentiles by nearest rank, so each end is a figure some attempt really had. Each
/// figure stands alone: one that fewer than three references have is unknown; the cost of a CLI that does not expose it is
/// «no expuesto» (<see cref="EstimateAssumptions.CostNotExposed"/>), never zero; a known cost is the CLI's list price
/// (<see cref="EstimateAssumptions.ListPrice"/>).</item>
/// <item><b>A plan</b> (<see cref="EstimatePlan"/>): each task of agent work (executor) or review (judge) is estimated as one
/// attempt, and its agent runs are the 25th and 75th percentiles of the runs similar tasks took (retries included). The
/// plan's runs and tokens add up the low ends and the high ends; its duration follows the graph (<see cref="EstimateAssumptions.CriticalPath"/>)
/// with <see cref="RunOptions.MaxParallelTasks"/> and the concurrency of each CLI, and every branch of a condition counts. Any
/// task without three references leaves the figure unknown, and says which.</item>
/// </list>
/// </remarks>
public static class UsageEstimation
{
    private const double Low = 0.25;
    private const double High = 0.75;

    /// <summary>What one attempt would spend (<see cref="IUsageEstimator.EstimateAsync"/>), from <paramref name="attempts"/>.</summary>
    public static UsageEstimate Estimate(IReadOnlyList<UsageAttempt> attempts, ModelCatalog catalog, UsageEstimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);

        var (references, assumptions, scope) = References(attempts, catalog, request.Agent, request.Provider, request.Model, request.Purpose, request.Size);
        if (references.Count < UsageEstimate.MinimumReferences)
        {
            return UsageEstimate.Unknown(references.Count, assumptions);
        }

        var costs = references.Select(a => a.Own.CostUsd).ToList();
        var costInterval = Interval(costs, scope, "cost at the CLI's list price");
        var all = assumptions.ToList();
        if (costInterval.IsKnown)
        {
            all.Add(EstimateAssumptions.ListPrice);
        }
        else if (costInterval.Low.Origin == MetricOrigin.NotExposed)
        {
            all.Add(EstimateAssumptions.CostNotExposed);
        }

        return new UsageEstimate(
            references.Count,
            Interval(references.Select(a => a.Own.InputTokens).ToList(), scope, "input tokens"),
            Interval(references.Select(a => a.Own.OutputTokens).ToList(), scope, "output tokens"),
            Interval(references.Select(Duration).ToList(), scope, "duration"),
            costInterval,
            all,
            Interval(references.Select(Total).ToList(), scope, "total tokens"));
    }

    /// <summary>What a plan would spend (<see cref="IUsageEstimator.EstimatePlanAsync"/>), from <paramref name="attempts"/>.</summary>
    public static PlanEstimate EstimatePlan(IReadOnlyList<UsageAttempt> attempts, ModelCatalog catalog, PlanEstimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);

        var roster = (request.Roster ?? []).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var tasks = new Dictionary<TaskId, UsageEstimate>();
        var steps = new List<Step>();
        var unknown = new List<string>();
        var agentsOnly = false;
        foreach (var task in request.Plan.Tasks)
        {
            var purpose = task.Kind switch
            {
                WorkTaskKind.AgentWork => AgentRunPurpose.Executor,
                WorkTaskKind.Review => AgentRunPurpose.Judge,
                _ => (AgentRunPurpose?)null,
            };
            if (purpose is null)
            {
                agentsOnly = true;
                steps.Add(new Step(task, null, TimeSpan.Zero, TimeSpan.Zero));
                continue;
            }

            if (task.Agent is not { } agentId || !roster.TryGetValue(agentId, out var agent))
            {
                // A task of an agent the roster does not have (the validator refuses it): nothing to estimate it from.
                unknown.Add(task.Id.Value);
                steps.Add(new Step(task, null, null, null));
                continue;
            }

            var estimate = Estimate(attempts, catalog, new UsageEstimateRequest(request.Project, agent.Id, agent.Model.Provider, agent.Model.Model, purpose.Value));
            tasks[task.Id] = estimate;
            var runs = RunsPerTask(attempts, catalog, agent, purpose.Value);
            if (!estimate.IsKnown || runs is null)
            {
                unknown.Add(task.Id.Value);
            }

            steps.Add(new Step(
                task,
                agent.Model.Provider,
                Times(estimate.Duration.Low, runs?.Low),
                Times(estimate.Duration.High, runs?.High),
                runs,
                estimate));
        }

        var assumptions = new List<string> { EstimateAssumptions.CriticalPath };
        if (agentsOnly)
        {
            assumptions.Add(EstimateAssumptions.AgentsOnly);
        }

        if (unknown.Count > 0)
        {
            var reason = string.Create(CultureInfo.InvariantCulture, $"unknown: fewer than {UsageEstimate.MinimumReferences} references for {string.Join(", ", unknown)}");
            return new PlanEstimate(tasks, EstimateInterval<long>.Unknown(reason), EstimateInterval<long>.Unknown(reason), EstimateInterval<long>.Unknown(reason), EstimateInterval<TimeSpan>.Unknown(reason), assumptions);
        }

        var estimated = steps.Where(s => s.Estimate is not null).ToList();
        var agentRuns = Sum(estimated.Select(s => (long?)s.Runs!.Value.Low), estimated.Select(s => (long?)s.Runs!.Value.High), "agent runs of similar tasks");
        var input = Sum(
            estimated.Select(s => s.Estimate!.InputTokens.Low.Value is { } v ? v * s.Runs!.Value.Low : (long?)null),
            estimated.Select(s => s.Estimate!.InputTokens.High.Value is { } v ? v * s.Runs!.Value.High : (long?)null),
            "input tokens of each task by its runs");
        var output = Sum(
            estimated.Select(s => s.Estimate!.OutputTokens.Low.Value is { } v ? v * s.Runs!.Value.Low : (long?)null),
            estimated.Select(s => s.Estimate!.OutputTokens.High.Value is { } v ? v * s.Runs!.Value.High : (long?)null),
            "output tokens of each task by its runs");
        var limits = new Limits(Math.Max(1, request.Options.MaxParallelTasks), request.ProviderConcurrency);
        var duration = estimated.Any(s => s.LowDuration is null || s.HighDuration is null)
            ? EstimateInterval<TimeSpan>.Unknown("unknown: the duration of a task is not known")
            : new EstimateInterval<TimeSpan>(
                Path(steps, s => s.LowDuration!.Value, limits, "low"),
                Path(steps, s => s.HighDuration!.Value, limits, "high"));
        return new PlanEstimate(tasks, agentRuns, input, output, duration, assumptions);
    }

    /// <summary>The references of an attempt, the assumptions they rest on and how a source names them.</summary>
    private static (List<UsageAttempt> References, IReadOnlyList<string> Assumptions, string Scope) References(
        IReadOnlyList<UsageAttempt> attempts,
        ModelCatalog catalog,
        AgentId agent,
        ProviderId provider,
        string? model,
        AgentRunPurpose purpose,
        WorkSize? size)
    {
        var similar = Similar(attempts, catalog, provider, model, purpose);
        var sizeNote = size is { } s ? ", " + s.ToString().ToLowerInvariant() + " size" : string.Empty;
        var sameAgent = Sized(similar.Where(a => a.Agent == agent).ToList(), size);
        var agentAssumptions = With(size, EstimateAssumptions.SameAgentAndModel, EstimateAssumptions.SamePurpose);
        if (sameAgent.Count >= UsageEstimate.MinimumReferences)
        {
            return (sameAgent, agentAssumptions, "attempts of the same agent, model and purpose" + sizeNote);
        }

        var sameModel = Sized(similar, size);
        if (sameModel.Count >= UsageEstimate.MinimumReferences)
        {
            return (sameModel, With(size, EstimateAssumptions.SameModel, EstimateAssumptions.SamePurpose), "attempts of the same model and purpose by any agent" + sizeNote);
        }

        return (sameAgent, agentAssumptions, string.Empty);
    }

    private static IReadOnlyList<string> With(WorkSize? size, params string[] assumptions) =>
        size is null ? assumptions : [.. assumptions, EstimateAssumptions.SameSize];

    /// <summary>The attempts of the same CLI, purpose and model that ended with a result, in the order the log closed them.</summary>
    private static List<UsageAttempt> Similar(IReadOnlyList<UsageAttempt> attempts, ModelCatalog catalog, ProviderId provider, string? model, AgentRunPurpose purpose)
    {
        var key = ModelKeys.Of(catalog, provider, model);
        return
        [
            .. attempts.Where(a =>
                a.Provider == provider
                && PurposeOf(a) == purpose
                && a.Outcome is not (AttemptOutcome.Cancelled or AttemptOutcome.Interrupted)
                && (a.Own.InputTokens.HasValue || a.Own.OutputTokens.HasValue)
                && (key is null
                    ? string.IsNullOrWhiteSpace(a.ConfiguredModel)
                    : ModelKeys.Of(catalog, a.Provider, string.IsNullOrWhiteSpace(a.ReportedModel) ? a.ConfiguredModel : a.ReportedModel) == key)),
        ];
    }

    /// <summary>The purpose of an attempt; before version 2 of the start, an attempt with a task was an executor's, and any other is not known.</summary>
    private static AgentRunPurpose? PurposeOf(UsageAttempt attempt) =>
        attempt.Purpose ?? (attempt.TaskId is null ? null : AgentRunPurpose.Executor);

    /// <summary>The third of <paramref name="attempts"/> of <paramref name="size"/> by their tokens (input and output); all of them without a size.</summary>
    private static List<UsageAttempt> Sized(List<UsageAttempt> attempts, WorkSize? size)
    {
        if (size is not { } wanted)
        {
            return attempts;
        }

        var sized = attempts
            .Where(a => a.Own.InputTokens.HasValue && a.Own.OutputTokens.HasValue)
            .OrderBy(a => a.Own.InputTokens.Value!.Value + a.Own.OutputTokens.Value!.Value)
            .ThenBy(a => a.FinishedAt)
            .ToList();
        var n = sized.Count;
        return
        [
            .. sized.Where((_, i) => wanted switch
            {
                WorkSize.Small => i * 3 < n,
                WorkSize.Medium => i * 3 >= n && i * 3 < 2 * n,
                _ => i * 3 >= 2 * n,
            }),
        ];
    }

    /// <summary>
    /// The tokens of an attempt in all, its input and its output: the total its percentiles are taken of, as such (D-32), never
    /// a sum of the percentiles of each part. Missing when either part is.
    /// </summary>
    private static MetricValue<long> Total(UsageAttempt attempt) =>
        attempt.Own.InputTokens.Value is { } input && attempt.Own.OutputTokens.Value is { } output
            ? MetricValue<long>.EstimatedBySystem(input + output, "input and output tokens of the attempt")
            : MetricValue<long>.Missing("a part of the tokens of the attempt is missing");

    /// <summary>How long an attempt ran, from its launch (after any wait in the queue) to its end; missing before version 2.</summary>
    private static MetricValue<TimeSpan> Duration(UsageAttempt attempt) =>
        attempt.LaunchedAt is { } launched && attempt.FinishedAt >= launched
            ? MetricValue<TimeSpan>.Measured(attempt.FinishedAt - launched, "launch to end of the attempt")
            : MetricValue<TimeSpan>.Missing("the attempt does not record its launch");

    /// <summary>The 25th and 75th percentiles (nearest rank) of the figures that have a value; unknown or «no expuesto» otherwise.</summary>
    private static EstimateInterval<T> Interval<T>(IReadOnlyList<MetricValue<T>> figures, string scope, string what)
        where T : struct, IComparable<T>
    {
        var values = figures.Where(f => f.HasValue).Select(f => f.Value!.Value).Order().ToList();
        if (values.Count >= UsageEstimate.MinimumReferences)
        {
            return new EstimateInterval<T>(
                MetricValue<T>.EstimatedBySystem(Percentile(values, Low), Source("p25", what, values.Count, scope)),
                MetricValue<T>.EstimatedBySystem(Percentile(values, High), Source("p75", what, values.Count, scope)));
        }

        if (figures.Count > 0 && figures.All(f => f.Origin == MetricOrigin.NotExposed))
        {
            var reason = figures[0].Source;
            return new EstimateInterval<T>(MetricValue<T>.NotExposed(reason), MetricValue<T>.NotExposed(reason));
        }

        return EstimateInterval<T>.Unknown(
            string.Create(CultureInfo.InvariantCulture, $"unknown: {values.Count} of {UsageEstimate.MinimumReferences} references with {what}"));
    }

    private static string Source(string percentile, string what, int count, string scope) =>
        string.Create(CultureInfo.InvariantCulture, $"{percentile} of the {what} of {count} {scope}");

    /// <summary>The value at the nearest rank of <paramref name="share"/> in <paramref name="sorted"/>: always one of them.</summary>
    private static T Percentile<T>(IReadOnlyList<T> sorted, double share)
    {
        var rank = (int)Math.Ceiling(share * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>The runs of the tasks like this one (the 25th and 75th percentiles), from three tasks on; null otherwise.</summary>
    private static (int Low, int High)? RunsPerTask(IReadOnlyList<UsageAttempt> attempts, ModelCatalog catalog, AgentDefinition agent, AgentRunPurpose purpose)
    {
        var similar = Similar(attempts, catalog, agent.Model.Provider, agent.Model.Model, purpose);
        foreach (var references in new[] { similar.Where(a => a.Agent == agent.Id).ToList(), similar })
        {
            var runs = references
                .GroupBy(a => (a.RunId, Task: a.TaskId ?? string.Empty))
                .Select(g => g.Select(a => a.Attempt).Distinct().Count())
                .Order()
                .ToList();
            if (runs.Count >= UsageEstimate.MinimumReferences)
            {
                return (Percentile(runs, Low), Percentile(runs, High));
            }
        }

        return null;
    }

    private static TimeSpan? Times(MetricValue<TimeSpan> duration, int? runs) =>
        duration.Value is { } value && runs is { } count ? TimeSpan.FromTicks(value.Ticks * count) : null;

    private static EstimateInterval<long> Sum(IEnumerable<long?> lows, IEnumerable<long?> highs, string what)
    {
        var low = lows.ToList();
        var high = highs.ToList();
        if (low.Any(v => v is null) || high.Any(v => v is null))
        {
            return EstimateInterval<long>.Unknown("unknown: a task has no " + what);
        }

        return new EstimateInterval<long>(
            MetricValue<long>.EstimatedBySystem(low.Sum(v => v!.Value), "sum of the low ends: " + what),
            MetricValue<long>.EstimatedBySystem(high.Sum(v => v!.Value), "sum of the high ends: " + what));
    }

    /// <summary>
    /// How long the plan takes with these durations: the tasks start as soon as what they wait for is done (every branch of a
    /// condition counts), in the order of their ids, within the tasks at once and the runs at once of each CLI.
    /// </summary>
    private static MetricValue<TimeSpan> Path(IReadOnlyList<Step> steps, Func<Step, TimeSpan> durationOf, Limits limits, string end)
    {
        var pending = steps.OrderBy(s => s.Task.Id.Value, StringComparer.Ordinal).ToList();
        var known = steps.Select(s => s.Task.Id).ToHashSet();
        var done = new HashSet<TaskId>();
        var running = new List<(Step Step, TimeSpan End)>();
        var now = TimeSpan.Zero;
        while (pending.Count > 0 || running.Count > 0)
        {
            var started = false;
            foreach (var step in pending.ToList())
            {
                var ready = step.Task.DependsOn.All(d => done.Contains(d.Prerequisite) || !known.Contains(d.Prerequisite));
                if (!ready || running.Count >= limits.Tasks || (step.Provider is { } provider && running.Count(r => r.Step.Provider == provider) >= limits.Of(provider)))
                {
                    continue;
                }

                pending.Remove(step);
                running.Add((step, now + durationOf(step)));
                started = true;
            }

            if (running.Count == 0)
            {
                if (!started)
                {
                    // What is left waits for itself: a cycle the validator would have refused. Its time is not known.
                    return MetricValue<TimeSpan>.Missing("unknown: the plan has a cycle");
                }

                continue;
            }

            var next = running.Min(r => r.End);
            now = next;
            foreach (var finished in running.Where(r => r.End == next).ToList())
            {
                running.Remove(finished);
                done.Add(finished.Step.Task.Id);
            }
        }

        return MetricValue<TimeSpan>.EstimatedBySystem(now, $"critical path of the plan with the {end} ends, the tasks at once and the runs at once of each CLI");
    }

    private sealed record Step(TaskSpec Task, ProviderId? Provider, TimeSpan? LowDuration, TimeSpan? HighDuration, (int Low, int High)? Runs = null, UsageEstimate? Estimate = null);

    private sealed record Limits(int Tasks, IReadOnlyDictionary<ProviderId, int>? Providers)
    {
        public int Of(ProviderId provider) =>
            Providers is not null && Providers.TryGetValue(provider, out var limit) && limit > 0 ? limit : MontageRunFacts.DefaultProviderConcurrency;
    }
}
