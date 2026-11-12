using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// The timeline of «Actividad» in «Repositorio y cambios» (docs/16 D-44): the integrations, the deliveries and the cleaning of worktrees the verified log
/// records, each with its real result. It reads only, oldest first (the screen decides how to show it), and a log that does not verify throws
/// <see cref="DecisionLogIntegrityException"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Result.</b> An integration or a delivery succeeded when it left a commit and no conflict; otherwise <see cref="RepositoryActivity.ErrorCode"/> is
/// <see cref="ConflictCode"/> (it conflicted) or <see cref="FailedCode"/> (it left no commit). The cleaning of a worktree is always a success; its task is
/// not in the entry, so a filter by task leaves it out.</item>
/// <item><b>Filters</b> are dimensions the log already has: the run and the task of the entry; the milestone (its runs and their follow-ups); the agent, provider
/// and model, by the attempts of the task (or, for what has no task such as a delivery, of any task of the run); and the time of the entry.</item>
/// </list>
/// </remarks>
public sealed class RepositoryActivityReader(AgentStudio.Application.Decisions.IDecisionRecorder recorder) : IRepositoryActivityReader
{
    /// <summary>An integration or a delivery that conflicted.</summary>
    public const string ConflictCode = "integration.conflict";

    /// <summary>An integration or a delivery that left no commit.</summary>
    public const string FailedCode = "integration.failed";

    public async Task<IReadOnlyList<RepositoryActivity>> ListAsync(ProjectContext project, RepositoryActivityFilter? filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        var milestoneRuns = filter?.Milestone is { } milestone ? WorkLog.RunsOfMilestone(entries, milestone.Value).ToHashSet(StringComparer.Ordinal) : null;
        var launches = entries
            .Select(RunLog.Payload)
            .OfType<AgentRunStartedPayload>()
            .Where(p => !string.IsNullOrEmpty(p.RunId))
            .ToList();

        var activities = new List<RepositoryActivity>();
        foreach (var entry in entries)
        {
            if (RunLog.Payload(entry) is not { } payload || string.IsNullOrEmpty(payload.RunId))
            {
                continue;
            }

            var activity = payload switch
            {
                IntegrationPerformedPayload integration => Integration(entry, integration),
                WorktreeRemovedPayload => new RepositoryActivity(entry.RecordedAt, RepositoryActivityKind.WorktreeRemoved, payload.RunId, null, null, true, null, entry.Sequence),
                _ => null,
            };
            if (activity is not null && Matches(activity, filter, milestoneRuns, launches))
            {
                activities.Add(activity);
            }
        }

        return activities;
    }

    private static RepositoryActivity Integration(DecisionEntry entry, IntegrationPerformedPayload integration)
    {
        var succeeded = integration.ResultCommit is not null && integration.ConflictPaths.Count == 0;
        var code = succeeded ? null : integration.ConflictPaths.Count > 0 ? ConflictCode : FailedCode;
        return new RepositoryActivity(
            entry.RecordedAt,
            integration.IsDelivery ? RepositoryActivityKind.Delivery : RepositoryActivityKind.Integration,
            integration.RunId,
            integration.Task?.Value,
            integration.ResultCommit,
            succeeded,
            code,
            entry.Sequence);
    }

    private static bool Matches(RepositoryActivity activity, RepositoryActivityFilter? filter, HashSet<string>? milestoneRuns, List<AgentRunStartedPayload> launches)
    {
        if (filter is null)
        {
            return true;
        }

        if ((filter.Run is not null && !string.Equals(activity.Run, filter.Run, StringComparison.Ordinal))
            || (filter.Task is not null && !string.Equals(activity.Task, filter.Task, StringComparison.Ordinal))
            || (milestoneRuns is not null && (activity.Run is null || !milestoneRuns.Contains(activity.Run)))
            || (filter.From is { } from && activity.At < from)
            || (filter.To is { } to && activity.At > to))
        {
            return false;
        }

        if (filter.Agent is null && filter.Provider is null && filter.Model is null)
        {
            return true;
        }

        // The attempts of its task, or of any task of its run when the activity has none (a delivery is the run's).
        return launches.Any(launch =>
            string.Equals(launch.RunId, activity.Run, StringComparison.Ordinal)
            && (activity.Task is null || string.Equals(launch.Task?.Value, activity.Task, StringComparison.Ordinal))
            && (filter.Agent is not { } agent || launch.Agent == agent)
            && (filter.Provider is not { } provider || launch.Provider == provider)
            && (filter.Model is null || string.Equals(launch.Model, filter.Model, StringComparison.Ordinal)));
    }
}
