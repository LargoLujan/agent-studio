using System.Globalization;
using System.Text.Json;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Usage;

namespace AgentStudio.Observability.Analysis;

/// <summary>
/// The thresholds of the deterministic analysis of consumption (docs/17 D-34, ADR-0052). Each one is a starting point, the
/// same for every project; none is a setting of the person yet.
/// </summary>
public sealed record ConsumptionThresholds
{
    /// <summary>The levels of the context; <c>ctx.high-peak</c> uses its threshold of compression (85 % by default).</summary>
    public ContextHealthThresholds Context { get; init; } = new();

    /// <summary><c>ctx.heavy-preamble</c>: the skills and the memory of an attempt, together, from this many estimated tokens.</summary>
    public long HeavyPreambleTokens { get; init; } = 4_000;

    /// <summary><c>ctx.heavy-preamble</c>: an attempt that succeeded in this long at most «terminó pronto».</summary>
    public TimeSpan ShortAttempt { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary><c>model.oversized</c>: a small change touches this many files at most…</summary>
    public int SmallChangeFiles { get; init; } = 3;

    /// <summary>…and this many lines at most (added and removed).</summary>
    public int SmallChangeLines { get; init; } = 50;

    /// <summary><c>cache.low-reuse</c>: an attempt that resumed its session and read from the cache less than this share of its input.</summary>
    public double LowCacheShare { get; init; } = 0.30;

    /// <summary><c>cache.low-reuse</c>: only for an input of this many tokens at least, where it matters.</summary>
    public long LowCacheMinimumInput { get; init; } = 10_000;

    /// <summary><c>wait.queue</c>: a wait in the queue of the provider from this long.</summary>
    public TimeSpan LongQueue { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary><c>retry.same-class</c> and <c>retry.same-failing-tests</c>: this many attempts in a row at least.</summary>
    public int RepeatedAttempts { get; init; } = 2;
}

/// <summary>The size of the change an attempt left, as the system recorded it: its files (the attempt record) and its lines (the diff; null when unknown).</summary>
public sealed record AttemptChange(int Files, int? Lines);

/// <summary>An attempt of a run: its task (null for the planner and the auxiliary agents) and its number.</summary>
public readonly record struct AttemptRef(TaskId? Task, int Attempt)
{
    /// <summary>The id of a target of kind attempt (<c>T3#2</c>; <c>plan#1</c> without a task).</summary>
    public string Id => string.Create(CultureInfo.InvariantCulture, $"{Task?.Value ?? "plan"}#{Attempt}");
}

/// <summary>What the deterministic analysis reads: the verified log of the project, the run, the moment, the thresholds, the catalog of models and the size of the changes the system read from the evidence.</summary>
public sealed record ConsumptionInput(string ProjectId, RunId Run, IReadOnlyList<DecisionEntry> Entries, DateTimeOffset At)
{
    public ConsumptionThresholds Thresholds { get; init; } = new();

    /// <summary>The catalog of models (docs/17 D-23); without one, <c>model.oversized</c> says nothing.</summary>
    public ModelCatalog? Catalog { get; init; }

    /// <summary>The size of the change of the attempts <see cref="ConsumptionRules.OversizedCandidates"/> named, read from their anchored evidence.</summary>
    public IReadOnlyDictionary<AttemptRef, AttemptChange> Changes { get; init; } = new Dictionary<AttemptRef, AttemptChange>();
}

/// <summary>
/// The deterministic analysis of consumption of one run (docs/17 D-34, F13-15, ADR-0052): pure, free and instant, over the
/// verified log only (the service checks it and reads the evidence). Every finding says what it cost with its origin, the
/// tokens of the attempts it is about, never a saving made up; the order is that of <see cref="ConsumptionFindingCodes.All"/>,
/// then by what it is about. The arguments of each code, in order, are those of <see cref="ConsumptionTexts"/>.
/// </summary>
public static class ConsumptionRules
{
    /// <summary>Why a wait has no figure of tokens: it spends time, which its arguments say.</summary>
    public const string WaitSpendsNoTokens = "a wait spends no tokens: what it cost is its time, in the arguments";

    /// <summary>
    /// The executor attempts that succeeded on a model the catalog classes as «máximo»: the ones whose change the service reads
    /// (attempt record and diff) for <c>model.oversized</c>. Empty without a catalog.
    /// </summary>
    public static IReadOnlyList<AttemptRef> OversizedCandidates(IReadOnlyList<DecisionEntry> entries, RunId run, ModelCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (catalog is null)
        {
            return [];
        }

        return [.. RunFacts.Read(UsageLogProjectId, entries, run).Attempts
            .Where(a => IsMaximum(a, catalog) && a.Start.Purpose == AgentRunPurpose.Executor && a.Finish.Status == AttemptOutcome.Succeeded)
            .Select(a => a.Ref)
            .Distinct()];
    }

    /// <summary>Analyses the run.</summary>
    public static ConsumptionReport Analyze(ConsumptionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Entries);
        var facts = RunFacts.Read(input.ProjectId, input.Entries, input.Run);
        var limits = input.Thresholds;
        var findings = new List<ConsumptionFinding>();
        PackOmitted(facts, findings);
        HighPeak(facts, limits, findings);
        Compacted(facts, findings);
        HeavyPreamble(facts, limits, findings);
        RetrySameClass(facts, limits, findings);
        RetrySameFailingTests(facts, limits, findings);
        PlanCorrected(facts, findings);
        VerdictCorrected(facts, findings);
        ModelOversized(facts, input, findings);
        CacheLowReuse(facts, limits, findings);
        WorkDiscarded(facts, findings);
        WaitQueue(facts, limits, findings);

        var head = input.Entries.Count == 0 ? string.Empty : input.Entries.MaxBy(e => e.Sequence)!.Hash;
        var ordered = findings
            .OrderBy(f => IndexOf(f.Code))
            .ThenBy(f => f.Target.Kind)
            .ThenBy(f => f.Target.Id, StringComparer.Ordinal)
            .ToList();
        return new ConsumptionReport(input.Run, head, input.At, ordered);
    }

    // ---- ctx.* ----

    /// <summary>The pack of context left entries out (thin context): one finding per agent, with the entries and attempts.</summary>
    private static void PackOmitted(RunFacts facts, List<ConsumptionFinding> findings)
    {
        foreach (var group in facts.Attempts.Where(a => a.Start.ContextPack is { Omitted.Count: > 0 }).GroupBy(a => a.Start.Agent))
        {
            var attempts = group.ToList();
            findings.Add(ConsumptionFinding.Of(
                ConsumptionFindingCodes.PackOmitted,
                new ConsumptionTarget(ConsumptionTargetKind.Agent, group.Key.Value),
                Sum(attempts.Select(a => a.Usage?.Own.InputTokens)),
                attempts.Count,
                group.Key.Value,
                attempts.Sum(a => a.Start.ContextPack!.Omitted.Count),
                attempts.Count));
        }
    }

    /// <summary>A peak over the threshold of compression: one finding per attempt, with its share of the window.</summary>
    private static void HighPeak(RunFacts facts, ConsumptionThresholds limits, List<ConsumptionFinding> findings)
    {
        foreach (var attempt in facts.Attempts)
        {
            if (attempt.Usage?.ContextPeak is { UsedTokens.Value: { } used, ContextWindow.Value: { } window } peak && window > 0 && (double)used / window >= limits.Context.Compress)
            {
                findings.Add(ConsumptionFinding.Of(
                    ConsumptionFindingCodes.HighPeak,
                    new ConsumptionTarget(ConsumptionTargetKind.Attempt, attempt.Ref.Id),
                    peak.UsedTokens,
                    1,
                    attempt.Ref.Id,
                    (int)Math.Floor(100.0 * used / window),
                    peak.Model ?? attempt.Model ?? string.Empty));
            }
        }
    }

    /// <summary>The CLI compacted the context: one finding per attempt, with its compactions.</summary>
    private static void Compacted(RunFacts facts, List<ConsumptionFinding> findings)
    {
        foreach (var attempt in facts.Attempts.Where(a => a.Finish.Compactions is > 0))
        {
            findings.Add(ConsumptionFinding.Of(
                ConsumptionFindingCodes.Compacted,
                new ConsumptionTarget(ConsumptionTargetKind.Attempt, attempt.Ref.Id),
                Sum([attempt.Usage?.Own.InputTokens]),
                1,
                attempt.Ref.Id,
                attempt.Finish.Compactions!.Value));
        }
    }

    /// <summary>
    /// Large skills and memory before starting in attempts that succeeded soon: one finding per agent, with the estimated tokens
    /// of each (characters divided by 4). Only starts of version 3 have parts.
    /// </summary>
    private static void HeavyPreamble(RunFacts facts, ConsumptionThresholds limits, List<ConsumptionFinding> findings)
    {
        var heavy = facts.Attempts
            .Where(a => a.Start.RecordsContextParts() && a.Start.ContextParts is { Count: > 0 })
            .Select(a => (Attempt: a, Skills: Tokens(a, ContextPartKind.Skills), Memory: Tokens(a, ContextPartKind.Memory)))
            .Where(x => x.Skills + x.Memory >= limits.HeavyPreambleTokens
                && x.Attempt.Finish.Status == AttemptOutcome.Succeeded
                && x.Attempt.Duration is { } duration && duration <= limits.ShortAttempt);
        foreach (var group in heavy.GroupBy(x => x.Attempt.Start.Agent))
        {
            var items = group.ToList();
            var skills = items.Sum(x => x.Skills);
            var memory = items.Sum(x => x.Memory);
            findings.Add(ConsumptionFinding.Of(
                ConsumptionFindingCodes.HeavyPreamble,
                new ConsumptionTarget(ConsumptionTargetKind.Agent, group.Key.Value),
                MetricValue<long>.EstimatedBySystem(skills + memory, "estimate: 4 chars/token (parts of the prompt measured by Agent Studio)"),
                items.Count,
                group.Key.Value,
                skills,
                memory,
                items.Count));
        }

        static long Tokens(RunAttempt attempt, ContextPartKind kind) =>
            (attempt.Start.ContextParts!.Where(p => p.Kind == kind).Sum(p => (long)p.Characters) + 3) / 4;
    }

    // ---- retry.* ----

    /// <summary>Attempts in a row of a task that ended with the same class of end, other than completed (F8-1).</summary>
    private static void RetrySameClass(RunFacts facts, ConsumptionThresholds limits, List<ConsumptionFinding> findings)
    {
        foreach (var task in facts.Attempts.Where(a => a.Ref.Task is not null).GroupBy(a => a.Ref.Task!.Value))
        {
            foreach (var streak in Streaks(task.OrderBy(a => a.Ref.Attempt), a => a.Finish.ResolvedEnd().Class is var c && c != AttemptEndClass.Completed ? c.ToString() : null))
            {
                if (streak.Count >= limits.RepeatedAttempts)
                {
                    findings.Add(ConsumptionFinding.Of(
                        ConsumptionFindingCodes.RetrySameClass,
                        new ConsumptionTarget(ConsumptionTargetKind.Task, task.Key.Value),
                        Sum(streak.Select(a => a.Usage?.Own.InputTokens)),
                        streak.Count,
                        task.Key.Value,
                        streak.Count,
                        streak[0].Finish.ResolvedEnd().Class.ToString()));
                }
            }
        }
    }

    /// <summary>
    /// Attempts in a row of a task whose verification left the same tests failing. The TRX is not kept with the names of the
    /// tests: the same step with the same anchored counters (failed above zero, and passed) counts as the same tests in red.
    /// </summary>
    private static void RetrySameFailingTests(RunFacts facts, ConsumptionThresholds limits, List<ConsumptionFinding> findings)
    {
        foreach (var task in facts.Attempts.Where(a => a.Ref.Task is not null).GroupBy(a => a.Ref.Task!.Value))
        {
            var byAttempt = task.OrderBy(a => a.Ref.Attempt).ToList();
            foreach (var streak in Streaks(byAttempt, a => facts.FailingTests(a.Ref) is { } tests ? tests.Key : null))
            {
                if (streak.Count >= limits.RepeatedAttempts && facts.FailingTests(streak[0].Ref) is { } failing)
                {
                    findings.Add(ConsumptionFinding.Of(
                        ConsumptionFindingCodes.RetrySameFailingTests,
                        new ConsumptionTarget(ConsumptionTargetKind.Task, task.Key.Value),
                        Sum(streak.Select(a => a.Usage?.Own.InputTokens)),
                        streak.Count,
                        task.Key.Value,
                        streak.Count,
                        failing.Failed,
                        failing.Step));
                }
            }
        }
    }

    // ---- plan.corrected, verdict.corrected ----

    /// <summary>The plan had to be corrected: the corrections, and the tokens of the planner's attempts beyond the first.</summary>
    private static void PlanCorrected(RunFacts facts, List<ConsumptionFinding> findings)
    {
        if (facts.PlanCorrections > 0)
        {
            var corrections = facts.PlannerAttempts.OrderBy(a => a.FinishedAt).Skip(1).ToList();
            findings.Add(ConsumptionFinding.Of(
                ConsumptionFindingCodes.PlanCorrected,
                new ConsumptionTarget(ConsumptionTargetKind.Run, facts.Run.Value),
                corrections.Count == 0 ? MetricValue<long>.Missing("the correction of the plan has no attempt of the planner in the log") : Sum(corrections.Select(a => a.Usage?.Own.InputTokens)),
                corrections.Count,
                facts.PlanCorrections));
        }
    }

    /// <summary>The verdict had to be corrected: the verdicts that supersede another, and the tokens of the judge's attempts beyond the first.</summary>
    private static void VerdictCorrected(RunFacts facts, List<ConsumptionFinding> findings)
    {
        if (facts.VerdictCorrections > 0)
        {
            var corrections = facts.Attempts.Where(a => a.Start.Purpose == AgentRunPurpose.Judge).OrderBy(a => a.FinishedAt).Skip(1).ToList();
            findings.Add(ConsumptionFinding.Of(
                ConsumptionFindingCodes.VerdictCorrected,
                new ConsumptionTarget(ConsumptionTargetKind.Run, facts.Run.Value),
                corrections.Count == 0 ? MetricValue<long>.Missing("the correction of the verdict has no attempt of the judge in the log") : Sum(corrections.Select(a => a.Usage?.Own.InputTokens)),
                corrections.Count,
                facts.VerdictCorrections));
        }
    }

    // ---- model.oversized ----

    /// <summary>Level «máximo» for small changes: one finding per agent and model, from the changes the service read.</summary>
    private static void ModelOversized(RunFacts facts, ConsumptionInput input, List<ConsumptionFinding> findings)
    {
        if (input.Catalog is not { } catalog)
        {
            return;
        }

        var limits = input.Thresholds;
        var small = facts.Attempts
            .Where(a => a.Start.Purpose == AgentRunPurpose.Executor && a.Finish.Status == AttemptOutcome.Succeeded && IsMaximum(a, catalog))
            .Where(a => input.Changes.TryGetValue(a.Ref, out var change) && change.Files <= limits.SmallChangeFiles && change.Lines is { } lines && lines <= limits.SmallChangeLines);
        foreach (var group in small.GroupBy(a => (a.Start.Agent, Model: a.Model ?? string.Empty)))
        {
            var attempts = group.ToList();
            findings.Add(ConsumptionFinding.Of(
                ConsumptionFindingCodes.ModelOversized,
                new ConsumptionTarget(ConsumptionTargetKind.Agent, group.Key.Agent.Value),
                Sum(attempts.Select(a => a.Usage?.Own.InputTokens)),
                attempts.Count,
                group.Key.Agent.Value,
                group.Key.Model,
                attempts.Count));
        }
    }

    private static bool IsMaximum(RunAttempt attempt, ModelCatalog catalog) =>
        (catalog.Find(attempt.Start.Provider, attempt.Start.Model) ?? catalog.Find(attempt.Start.Provider, attempt.Finish.ReportedModel))?.Tier == ModelTier.Maximum;

    // ---- cache.low-reuse ----

    /// <summary>An attempt that resumed its session and read little from the cache: the tokens of its input that were not read from it.</summary>
    private static void CacheLowReuse(RunFacts facts, ConsumptionThresholds limits, List<ConsumptionFinding> findings)
    {
        foreach (var attempt in facts.Attempts.Where(a => a.Finish.ResumedSession is not null))
        {
            if (attempt.Usage?.Own is { InputTokens.Value: { } input, CacheReadInputTokens.Value: { } read } own
                && input >= limits.LowCacheMinimumInput
                && (double)read / input < limits.LowCacheShare)
            {
                var origin = MetricOrigins.Weakest(own.InputTokens.Origin, own.CacheReadInputTokens.Origin);
                findings.Add(ConsumptionFinding.Of(
                    ConsumptionFindingCodes.CacheLowReuse,
                    new ConsumptionTarget(ConsumptionTargetKind.Attempt, attempt.Ref.Id),
                    MetricValue<long>.Of(origin, input - read, "input of the attempt minus what it read from the cache"),
                    1,
                    attempt.Ref.Id,
                    (int)Math.Floor(100.0 * read / input)));
            }
        }
    }

    // ---- work.discarded ----

    /// <summary>
    /// Attempts of work that reached no delivery (the axis «destino» of docs/16 D-34): those of tasks that ended failed or
    /// cancelled and, when the run itself ended failed or cancelled, every attempt of its executors. A run that succeeded and
    /// waits for its delivery discards nothing yet.
    /// </summary>
    private static void WorkDiscarded(RunFacts facts, List<ConsumptionFinding> findings)
    {
        var executors = facts.Attempts.Where(a => a.Start.Purpose == AgentRunPurpose.Executor && a.Ref.Task is not null).ToList();
        var discarded = facts.RunEnded is RunPhase.Failed or RunPhase.Cancelled
            ? executors
            : [.. executors.Where(a => facts.FinalStatus(a.Ref.Task!.Value) is WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)];
        if (discarded.Count > 0)
        {
            findings.Add(ConsumptionFinding.Of(
                ConsumptionFindingCodes.WorkDiscarded,
                new ConsumptionTarget(ConsumptionTargetKind.Run, facts.Run.Value),
                Sum(discarded.Select(a => a.Usage?.Own.InputTokens)),
                discarded.Count,
                discarded.Count,
                discarded.Select(a => a.Ref.Task!.Value).Distinct().Count()));
        }
    }

    // ---- wait.queue ----

    /// <summary>Long waits in the queue of a provider (from the start recorded to the launch): one finding per provider, with the total and the longest.</summary>
    private static void WaitQueue(RunFacts facts, ConsumptionThresholds limits, List<ConsumptionFinding> findings)
    {
        var waits = facts.Attempts
            .Where(a => a.Finish.LaunchedAt is not null)
            .Select(a => (Attempt: a, Wait: a.Finish.LaunchedAt!.Value - a.StartedAt))
            .Where(x => x.Wait >= limits.LongQueue);
        foreach (var group in waits.GroupBy(x => x.Attempt.Start.Provider))
        {
            var items = group.ToList();
            findings.Add(ConsumptionFinding.Of(
                ConsumptionFindingCodes.WaitQueue,
                new ConsumptionTarget(ConsumptionTargetKind.Setting, "provider-concurrency:" + group.Key.Value),
                MetricValue<long>.Missing(WaitSpendsNoTokens),
                items.Count,
                group.Key.Value,
                items.Count,
                items.Aggregate(TimeSpan.Zero, (total, x) => total + x.Wait),
                items.Max(x => x.Wait)));
        }
    }

    // ---- helpers ----

    /// <summary>
    /// The sum of the tokens of some attempts, with the weakest origin of its figures. Without any figure it says why (the CLI
    /// does not expose them, or they did not come), never zero; when some are missing it is an estimate of Agent Studio, and
    /// its source says how many.
    /// </summary>
    internal static MetricValue<long> Sum(IEnumerable<MetricValue<long>?> values)
    {
        var all = values.ToList();
        var known = all.Where(v => v is { HasValue: true }).Select(v => v!.Value).ToList();
        if (known.Count == 0)
        {
            return all.Count > 0 && all.All(v => v is { Origin: MetricOrigin.NotExposed })
                ? MetricValue<long>.NotExposed(all[0]!.Value.Source)
                : MetricValue<long>.Missing(string.Create(CultureInfo.InvariantCulture, $"no figure in {all.Count} attempts"));
        }

        var total = known.Sum(v => v.Value!.Value);
        if (known.Count == all.Count)
        {
            var origin = MetricOrigins.Weakest([.. known.Select(v => v.Origin)]);
            return MetricValue<long>.Of(origin, total, known.Count == 1 ? known[0].Source : string.Create(CultureInfo.InvariantCulture, $"sum of {known.Count} attempts ({known[0].Source})"));
        }

        return MetricValue<long>.EstimatedBySystem(total, string.Create(CultureInfo.InvariantCulture, $"sum of {known.Count} of {all.Count} attempts: {all.Count - known.Count} have no figure"));
    }

    /// <summary>The runs of consecutive items with the same key (null breaks a run).</summary>
    private static IEnumerable<List<RunAttempt>> Streaks(IEnumerable<RunAttempt> attempts, Func<RunAttempt, string?> key)
    {
        var current = new List<RunAttempt>();
        string? last = null;
        foreach (var attempt in attempts)
        {
            var k = key(attempt);
            if (k is not null && k == last)
            {
                current.Add(attempt);
                continue;
            }

            if (current.Count > 0)
            {
                yield return current;
            }

            current = k is null ? [] : [attempt];
            last = k;
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private static int IndexOf(string code)
    {
        for (var i = 0; i < ConsumptionFindingCodes.All.Count; i++)
        {
            if (ConsumptionFindingCodes.All[i] == code)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>The project id the candidates read the log with: the usage projection only uses it to label the attempts.</summary>
    private const string UsageLogProjectId = "consumption-analysis";
}

/// <summary>One attempt of the run as its start and its end recorded it, with its own usage (the projection of docs/14 D-02).</summary>
internal sealed record RunAttempt(AttemptRef Ref, AgentRunStartedPayload Start, DateTimeOffset StartedAt, AgentRunFinishedPayload Finish, DateTimeOffset FinishedAt, UsageAttempt? Usage)
{
    /// <summary>The model the CLI reported, else the one configured.</summary>
    public string? Model => Finish.ReportedModel ?? Start.Model;

    /// <summary>From the launch (after the queue) to the end; null when it never launched.</summary>
    public TimeSpan? Duration => Finish.LaunchedAt is { } launched ? FinishedAt - launched : null;
}

/// <summary>The failing tests of an attempt: the step and its anchored counters.</summary>
internal sealed record FailingTests(string Step, int Failed, int Passed)
{
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Step}|{Failed}|{Passed}");
}

/// <summary>What the log says of one run, read once.</summary>
internal sealed class RunFacts
{
    private readonly Dictionary<TaskId, WorkTaskStatus> _finalStatus = [];
    private readonly Dictionary<AttemptRef, FailingTests> _failing = [];

    private RunFacts(RunId run)
    {
        Run = run;
    }

    public RunId Run { get; }

    public List<RunAttempt> Attempts { get; } = [];

    /// <summary>The attempts of the planner for this run's plan: the planning may be a unit of its own in the log.</summary>
    public List<RunAttempt> PlannerAttempts { get; } = [];

    public int PlanCorrections { get; private set; }

    public int VerdictCorrections { get; private set; }

    /// <summary>The phase <c>RunFinished</c> recorded; null while the run goes on.</summary>
    public RunPhase? RunEnded { get; private set; }

    public WorkTaskStatus? FinalStatus(TaskId task) => _finalStatus.TryGetValue(task, out var status) ? status : null;

    public FailingTests? FailingTests(AttemptRef attempt) => _failing.TryGetValue(attempt, out var tests) ? tests : null;

    public static RunFacts Read(string projectId, IReadOnlyList<DecisionEntry> entries, RunId run)
    {
        var facts = new RunFacts(run);
        var ordered = entries.OrderBy(e => e.Sequence).ToList();
        var usage = UsageProjector.Project(projectId, ordered).Attempts;
        string? planHash = null;
        var planningRuns = new HashSet<string>(StringComparer.Ordinal) { run.Value };

        // The plan of the run, and the units of the log that planned it (a planning has a run id of its own).
        foreach (var entry in ordered)
        {
            if (entry.Kind == DecisionKind.RunStarted && Read<RunStartedPayload>(entry) is { } started && started.RunId == run.Value)
            {
                planHash = started.PlanHash;
            }
        }

        foreach (var entry in ordered)
        {
            if (entry.Kind == DecisionKind.PlanRevised && Read<PlanRevisedPayload>(entry) is { } revised
                && (revised.RunId == run.Value || (planHash is not null && revised.PlanHash == planHash)))
            {
                facts.PlanCorrections++;
                planningRuns.Add(revised.RunId);
            }
        }

        var open = new Dictionary<(string Run, AttemptRef Attempt), Stack<(DecisionEntry Entry, AgentRunStartedPayload Payload)>>();
        foreach (var entry in ordered)
        {
            switch (entry.Kind)
            {
                case DecisionKind.AgentRunStarted when Read<AgentRunStartedPayload>(entry) is { } start && planningRuns.Contains(start.RunId):
                    var key = (start.RunId, new AttemptRef(start.Task, start.Attempt));
                    if (!open.TryGetValue(key, out var stack))
                    {
                        open[key] = stack = new();
                    }

                    stack.Push((entry, start));
                    break;

                case DecisionKind.AgentRunFinished when Read<AgentRunFinishedPayload>(entry) is { } finish && planningRuns.Contains(finish.RunId):
                    if (!open.TryGetValue((finish.RunId, new AttemptRef(finish.Task, finish.Attempt)), out var starts) || starts.Count == 0)
                    {
                        break;
                    }

                    var (startEntry, startPayload) = starts.Pop();
                    var reference = new AttemptRef(finish.Task, finish.Attempt);
                    var own = usage.FirstOrDefault(u => u.RunId == finish.RunId && u.TaskId == finish.Task?.Value && u.Attempt == finish.Attempt && u.FinishedAt == entry.RecordedAt);
                    var attempt = new RunAttempt(reference, startPayload, startEntry.RecordedAt, finish, entry.RecordedAt, own);
                    if (finish.RunId == run.Value)
                    {
                        facts.Attempts.Add(attempt);
                    }

                    if (startPayload.Purpose == AgentRunPurpose.Planner)
                    {
                        facts.PlannerAttempts.Add(attempt);
                    }

                    break;

                case DecisionKind.TaskStatusChanged when Read<TaskStatusChangedPayload>(entry) is { } change && change.RunId == run.Value:
                    facts._finalStatus[change.Task] = change.To;
                    break;

                case DecisionKind.EvidenceRegistered when Read<EvidenceRegisteredPayload>(entry) is { } evidence && evidence.RunId == run.Value:
                    if (evidence is { Kind: EvidenceKind.TestRun, TestRun: { Failed: > 0 } tests, Task: { } task, Attempt: { } number })
                    {
                        var at = new AttemptRef(task, number);
                        if (!facts._failing.ContainsKey(at))
                        {
                            facts._failing[at] = new FailingTests(evidence.Title ?? "tests", tests.Failed, tests.Passed);
                        }
                    }

                    break;

                case DecisionKind.VerdictIssued when Read<VerdictIssuedPayload>(entry) is { } verdict && verdict.RunId == run.Value && verdict.Supersedes is not null:
                    facts.VerdictCorrections++;
                    break;

                case DecisionKind.RunFinished when Read<RunFinishedPayload>(entry) is { } finished && finished.RunId == run.Value:
                    facts.RunEnded = finished.Phase;
                    break;
            }
        }

        return facts;
    }

    private static T? Read<T>(DecisionEntry entry)
        where T : DecisionPayload
    {
        try
        {
            return DecisionPayloadSerializer.Deserialize<T>(entry.PayloadJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException)
        {
            return null;
        }
    }
}
