using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Desktop.Orchestrator.Live;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>
/// Builds the live state of a run (docs/17 D-14) from what the monitor already knows of its tasks: no event is read here and
/// nothing is asked of the run, so it is cheap and pure. The state of an agent is the strongest of what its tasks say, in this
/// order: it asks, it waits for an approval, it works, it waits for quota, it is blocked, it is queued, it finished, it is free.
/// </summary>
internal static class LiveStateFactory
{
    /// <param name="roster">The agents of the template: those without a task are «Libre». Without it, only the agents that have a task show.</param>
    public static RunLiveState Build(
        RunId run,
        RunPhase phase,
        IReadOnlyList<MonitorTaskViewModel> tasks,
        IReadOnlyList<AgentDefinition> roster,
        int agentRunsUsed,
        int? maxAgentRuns,
        bool integrating,
        long generation)
    {
        var taskStates = new Dictionary<TaskId, TaskLiveState>();
        foreach (var task in tasks)
        {
            taskStates[task.Id] = TaskState(task);
        }

        var ids = roster.Select(a => a.Id.Value)
            .Concat(tasks.Select(t => t.Agent).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var agents = new Dictionary<AgentId, AgentLiveState>();
        foreach (var id in ids)
        {
            var agent = new AgentId(id);
            agents[agent] = AgentState(agent, [.. tasks.Where(t => string.Equals(t.Agent, id, StringComparison.Ordinal))]);
        }

        return new RunLiveState(run, phase, agents, taskStates, agentRunsUsed, maxAgentRuns, integrating, generation);
    }

    private static TaskLiveState TaskState(MonitorTaskViewModel task) => new(
        task.Id,
        task.Status,
        task.LiveAgent,
        task.Attempt,
        task.Status == WorkTaskStatus.Blocked ? task.StatusLabel : task.Status == WorkTaskStatus.Failed ? task.ReasonText : null,
        task.ReadLiveContext(),

        // The cable agent → folder is animated while the task of an agent writes: a task of work that is running.
        task.Status == WorkTaskStatus.Running && task.Kind == WorkTaskKind.AgentWork);

    private static AgentLiveState AgentState(AgentId agent, IReadOnlyList<MonitorTaskViewModel> tasks)
    {
        var (status, driver) = StatusOf(tasks);
        var shown = driver ?? tasks.LastOrDefault();
        var context = (tasks.FirstOrDefault(t => t.Status == WorkTaskStatus.Running) ?? tasks.LastOrDefault(t => t.ReadLiveContext() is not null))?.ReadLiveContext();

        var finished = tasks.SelectMany(t => t.FinishedAttempts).ToList();
        const string none = "no attempt of the agent has finished yet";
        var input = finished.Count == 0 ? MetricValue<long>.Missing(none) : MonitorBars.ToValue(MetricAggregate.Sum(finished.Select(a => a.InputTokens)), none);
        var cached = finished.Count == 0 ? MetricValue<long>.Missing(none) : MonitorBars.ToValue(MetricAggregate.Sum(finished.Select(a => a.CachedInputTokens)), none);
        var output = finished.Count == 0 ? MetricValue<long>.Missing(none) : MonitorBars.ToValue(MetricAggregate.Sum(finished.Select(a => a.OutputTokens)), none);

        return new AgentLiveState(
            agent,
            status,
            status is AgentLiveStatus.Idle or AgentLiveStatus.Finished ? null : shown?.Id,
            status == AgentLiveStatus.WaitingForQuota ? BlockReasonText.RateLimitedUntil(driver?.BlockReason) : null,
            status == AgentLiveStatus.Blocked ? BlockedWhy(driver) : null,
            context,
            input,
            cached,
            output,
            tasks.Sum(t => Math.Max(t.Attempt, 0)));
    }

    private static (AgentLiveStatus Status, MonitorTaskViewModel? Driver) StatusOf(IReadOnlyList<MonitorTaskViewModel> tasks)
    {
        if (tasks.FirstOrDefault(t => t.AwaitingAnswer) is { } asking)
        {
            return (AgentLiveStatus.Asking, asking);
        }

        if (tasks.FirstOrDefault(t => t.Status == WorkTaskStatus.AwaitingApproval) is { } approval)
        {
            return (AgentLiveStatus.AwaitingApproval, approval);
        }

        if (tasks.FirstOrDefault(t => t.Status == WorkTaskStatus.Running) is { } running)
        {
            return (AgentLiveStatus.Working, running);
        }

        if (tasks.FirstOrDefault(t => t.Status == WorkTaskStatus.Blocked && BlockReasonText.RateLimitedUntil(t.BlockReason) is not null || IsQuotaWithoutTime(t)) is { } waiting)
        {
            return (AgentLiveStatus.WaitingForQuota, waiting);
        }

        if (tasks.FirstOrDefault(t => t.Status == WorkTaskStatus.Blocked) is { } blocked)
        {
            return (AgentLiveStatus.Blocked, blocked);
        }

        if (tasks.FirstOrDefault(t => t.Status == WorkTaskStatus.Failed) is { } failed)
        {
            return (AgentLiveStatus.Blocked, failed);
        }

        if (tasks.FirstOrDefault(t => t.IsQueued) is { } queued)
        {
            return (AgentLiveStatus.Queued, queued);
        }

        return tasks.Count > 0 && tasks.All(t => t.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Skipped)
            ? (AgentLiveStatus.Finished, null)
            : (AgentLiveStatus.Idle, null);
    }

    private static bool IsQuotaWithoutTime(MonitorTaskViewModel task) =>
        task.Status == WorkTaskStatus.Blocked && task.BlockReason is { } reason && reason.StartsWith("RateLimitedUntil", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why an agent is blocked, as the monitor says it: the label of the block, or the reason of the failure.</summary>
    private static string? BlockedWhy(MonitorTaskViewModel? task) => task is null ? null
        : task.Status == WorkTaskStatus.Failed ? task.ReasonText
        : task.StatusLabel;
}
