using System.Globalization;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Models;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Observability.Context;
using AgentStudio.Observability.Usage;

namespace AgentStudio.Observability.Models;

/// <summary>
/// What the verified log of a project teaches of each model (docs/17 D-22 and D-23, ADR-0050): a projection, never stored, so
/// it is rebuilt the same from the same log. A log that does not verify, or that no anchor recognises, teaches nothing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The model of an attempt</b> is the one its CLI reported (<c>ReportedModel</c>), or else the one the agent was
/// configured with; an attempt with neither (the default of the CLI, never reported) teaches nothing. A model the catalog
/// classifies is learned under the name of its entry, so an alias and its full name, with or without the date the CLI
/// appends, are one model; any other under the id the CLI reported, without its date.</item>
/// <item><b>Its tasks</b> are those of executors (<c>AgentRunPurpose.Executor</c>; before version 2 of the start, an attempt
/// with a task whose agent is not the orchestrator, a judge or an auditor of the roster of its run) that ended: their last
/// <c>TaskStatusChanged</c> is <c>Completed</c> or <c>Failed</c> (a cancelled or skipped task says nothing of the model). A
/// task belongs to the model of its first attempt.</item>
/// <item><b>First-pass approval:</b> completed with one attempt in a run whose last valid verdict is <c>Approved</c>.
/// <b>Retries:</b> the attempts of each task beyond the first. <b>Rejections:</b> its tasks in a run whose last valid verdict
/// is <c>Rejected</c> or <c>ChangesRequested</c>. All of it is estimated by Agent Studio, and with fewer than three tasks it is
/// «desconocido» (<see cref="ModelPerformance.IsKnown"/>).</item>
/// <item><b>The window</b> is the last one its CLI reported (<see cref="ModelWindowCatalog"/>), as an estimate of Agent
/// Studio for what comes next. <b>The cost of a task</b> is the sum of what its attempts cost at the CLI's list price
/// (<see cref="UsageProjector"/>, deduplicated by session); the model's is the median over its tasks with a cost, from three
/// on. Codex does not expose it: «no expuesto», never zero.</item>
/// </list>
/// </remarks>
public static class LearnedModelsProjection
{
    private const string Project = "project";

    /// <summary>What the log of the open project teaches; nothing when it does not verify.</summary>
    public static LearnedModels FromLog(DecisionLogState log, ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(catalog);
        return log.Verification.IsValid ? Learn(Project, log.Entries, ModelWindowCatalog.FromLog(log), catalog) : LearnedModels.Empty;
    }

    /// <summary>What the log of a project teaches; nothing unless it is trusted (it verifies against a recognised anchor).</summary>
    public static LearnedModels FromLog(ProjectLogSnapshot snapshot, ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);
        return snapshot.IsTrusted
            ? Learn(string.IsNullOrWhiteSpace(snapshot.ProjectId) ? Project : snapshot.ProjectId, snapshot.Entries, ModelWindowCatalog.FromLog(snapshot), catalog)
            : LearnedModels.Empty;
    }

    /// <summary>
    /// The key a model is learned under: the name of its entry in <paramref name="catalog"/> (looked up as it is, then without
    /// the date the CLI appends and without <c>[1m]</c>), or else the id without its date; null for none.
    /// </summary>
    public static string? KeyOf(ModelCatalog catalog, ProviderId provider, string? model)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var trimmed = model.Trim();
        var normalized = ModelWindowTable.Normalize(trimmed) ?? trimmed;
        var withoutWindow = normalized.EndsWith("[1m]", StringComparison.Ordinal) ? normalized[..^4] : normalized;
        var entry = catalog.Find(provider, trimmed) ?? catalog.Find(provider, normalized) ?? catalog.Find(provider, withoutWindow);
        return entry?.Name ?? normalized;
    }

    private static LearnedModels Learn(string projectId, IReadOnlyList<DecisionEntry> entries, ModelWindowCatalog windows, ModelCatalog catalog)
    {
        var attempts = UsageProjector.Project(projectId, entries).Attempts;
        var facts = Facts.Read(entries);

        var byModel = new Dictionary<(ProviderId Provider, string Key), Learning>();
        foreach (var attempt in attempts)
        {
            var name = string.IsNullOrWhiteSpace(attempt.ReportedModel) ? attempt.ConfiguredModel : attempt.ReportedModel;
            if (KeyOf(catalog, attempt.Provider, name) is not { } key)
            {
                continue;
            }

            if (!byModel.TryGetValue((attempt.Provider, key), out var learning))
            {
                byModel[(attempt.Provider, key)] = learning = new Learning(attempt.Provider, key);
            }

            learning.Attempts++;
            if (!learning.ReportedAs.Contains(name!.Trim(), StringComparer.Ordinal))
            {
                learning.ReportedAs.Add(name.Trim());
            }
        }

        // The tasks of executors, each with the model of its first attempt.
        var tasks = attempts
            .Where(a => a.TaskId is not null && IsExecutor(a, facts))
            .GroupBy(a => (a.RunId, Task: a.TaskId!))
            .ToList();
        foreach (var task in tasks)
        {
            var first = task.OrderBy(a => a.Attempt).ThenBy(a => a.RecordedAt).First();
            var name = string.IsNullOrWhiteSpace(first.ReportedModel) ? first.ConfiguredModel : first.ReportedModel;
            if (KeyOf(catalog, first.Provider, name) is not { } key
                || !byModel.TryGetValue((first.Provider, key), out var learning)
                || !facts.LastStatus.TryGetValue((task.Key.RunId, task.Key.Task), out var status)
                || status is not (WorkTaskStatus.Completed or WorkTaskStatus.Failed))
            {
                continue;
            }

            var tries = task.Select(a => a.Attempt).Distinct().Count();
            var verdict = facts.LastVerdict.TryGetValue(task.Key.RunId, out var decision) ? decision : (VerdictDecision?)null;
            learning.Tasks++;
            learning.Retries += tries - 1;
            if (status == WorkTaskStatus.Completed && tries == 1 && verdict == VerdictDecision.Approved)
            {
                learning.FirstPassApprovals++;
            }

            if (verdict is VerdictDecision.Rejected or VerdictDecision.ChangesRequested)
            {
                learning.Rejections++;
            }

            learning.TaskCosts.Add(CostOf(task));
        }

        var models = byModel.Values
            .OrderBy(l => l.Provider.Value, StringComparer.Ordinal)
            .ThenBy(l => l.Key, StringComparer.Ordinal)
            .Select(l => new LearnedModel(
                l.Provider,
                l.Key,
                [.. l.ReportedAs],
                new ModelPerformance(l.Provider, l.Key, l.Tasks, l.FirstPassApprovals, l.Retries, l.Rejections),
                WindowOf(l, windows, catalog),
                CostPerTask(l),
                l.Attempts))
            .ToList();
        return new LearnedModels(models);
    }

    private static bool IsExecutor(UsageAttempt attempt, Facts facts)
    {
        if (attempt.Purpose is { } purpose)
        {
            return purpose == AgentRunPurpose.Executor;
        }

        // Before version 2 of the start: an attempt with a task, unless the roster of its run says its agent does not execute.
        return !facts.Roles.TryGetValue((attempt.RunId, attempt.Agent.Value), out var role)
            || role is not (AgentRole.Orchestrator or AgentRole.Judge or AgentRole.Auditor);
    }

    /// <summary>What a task cost: the sum of its attempts when each has a cost; «no expuesto» when its CLI does not give one.</summary>
    private static MetricValue<decimal> CostOf(IEnumerable<UsageAttempt> task)
    {
        var costs = task.Select(a => a.Own.CostUsd).ToList();
        if (costs.Any(c => c.Origin == MetricOrigin.NotExposed))
        {
            return MetricValue<decimal>.NotExposed("the CLI does not expose the cost");
        }

        return costs.All(c => c.HasValue)
            ? MetricValue<decimal>.EstimatedBySystem(costs.Sum(c => c.Value!.Value), "sum of the attempts at the CLI's list price")
            : MetricValue<decimal>.Missing("an attempt of the task has no cost");
    }

    private static MetricValue<decimal> CostPerTask(Learning learning)
    {
        var known = learning.TaskCosts.Where(c => c.HasValue).Select(c => c.Value!.Value).Order().ToList();
        if (known.Count >= ModelPerformance.MinimumReferences)
        {
            var middle = known.Count / 2;
            var median = known.Count % 2 == 1 ? known[middle] : (known[middle - 1] + known[middle]) / 2m;
            return MetricValue<decimal>.EstimatedBySystem(
                median,
                string.Create(CultureInfo.InvariantCulture, $"median of {known.Count} tasks at the CLI's list price"));
        }

        if (learning.TaskCosts.Count > 0 && learning.TaskCosts.All(c => c.Origin == MetricOrigin.NotExposed))
        {
            return MetricValue<decimal>.NotExposed("the CLI does not expose the cost");
        }

        return MetricValue<decimal>.Missing(
            string.Create(CultureInfo.InvariantCulture, $"unknown: {known.Count} of {ModelPerformance.MinimumReferences} references"));
    }

    private static MetricValue<long> WindowOf(Learning learning, ModelWindowCatalog windows, ModelCatalog catalog)
    {
        var seen = windows.Windows
            .Where(w => KeyOf(catalog, learning.Provider, w.Model) == learning.Key)
            .OrderByDescending(w => w.SeenAt)
            .ThenBy(w => w.Model, StringComparer.Ordinal)
            .FirstOrDefault();
        return seen is null
            ? MetricValue<long>.Missing("the CLI has not reported a window for this model")
            : MetricValue<long>.EstimatedBySystem(
                seen.Tokens,
                string.Create(CultureInfo.InvariantCulture, $"window the CLI reported in an earlier run ({seen.Model}, {seen.SeenAt.UtcDateTime:yyyy-MM-dd})"));
    }

    private sealed class Learning(ProviderId provider, string key)
    {
        public ProviderId Provider { get; } = provider;

        public string Key { get; } = key;

        public List<string> ReportedAs { get; } = [];

        public List<MetricValue<decimal>> TaskCosts { get; } = [];

        public int Attempts { get; set; }

        public int Tasks { get; set; }

        public int FirstPassApprovals { get; set; }

        public int Retries { get; set; }

        public int Rejections { get; set; }
    }

    /// <summary>What the projection needs besides the attempts: the last status of each task, the last valid verdict of each run and the roles of each roster.</summary>
    private sealed class Facts
    {
        public Dictionary<(string Run, string Task), WorkTaskStatus> LastStatus { get; } = [];

        public Dictionary<string, VerdictDecision> LastVerdict { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Run, string Agent), AgentRole> Roles { get; } = [];

        public static Facts Read(IReadOnlyList<DecisionEntry> entries)
        {
            var facts = new Facts();
            foreach (var entry in entries.OrderBy(e => e.Sequence))
            {
                try
                {
                    switch (entry.Kind)
                    {
                        case DecisionKind.TaskStatusChanged:
                            var status = DecisionPayloadSerializer.Deserialize<TaskStatusChangedPayload>(entry.PayloadJson);
                            facts.LastStatus[(status.RunId, status.Task.Value)] = status.To;
                            break;

                        case DecisionKind.VerdictIssued:
                            var verdict = DecisionPayloadSerializer.Deserialize<VerdictIssuedPayload>(entry.PayloadJson);
                            if (verdict.Valid)
                            {
                                facts.LastVerdict[verdict.RunId] = verdict.Decision;
                            }

                            break;

                        case DecisionKind.RunStarted:
                            var started = DecisionPayloadSerializer.Deserialize<RunStartedPayload>(entry.PayloadJson);
                            foreach (var member in started.Roster ?? [])
                            {
                                facts.Roles[(started.RunId, member.Agent.Value)] = member.Role;
                            }

                            break;
                    }
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
                {
                    // A payload that cannot be read teaches nothing; the usage projection already warns of it.
                }
            }

            return facts;
        }
    }
}
