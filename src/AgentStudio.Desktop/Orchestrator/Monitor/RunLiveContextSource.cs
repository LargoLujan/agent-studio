using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>What a monitor needs to paint the free agents and the pulses of a run: its template and the dependencies of its plan.</summary>
/// <param name="Roster">The agents of the template the run was created with.</param>
/// <param name="Dependencies">For each task of the plan, the tasks it waits for.</param>
public sealed record RunLiveContext(IReadOnlyList<AgentDefinition> Roster, IReadOnlyDictionary<TaskId, IReadOnlyList<TaskId>> Dependencies);

/// <summary>
/// Where a monitor that no document attached reads the template and the plan of its run (P52): not from a screen, which a run followed
/// in the background does not have, but from what the run itself stored.
/// </summary>
public interface IRunLiveContextSource
{
    /// <summary>The context of the run, or null when its header cannot be read.</summary>
    Task<RunLiveContext?> ReadAsync(ProjectContext project, RunId run, CancellationToken cancellationToken);
}

/// <summary>Reads the header of the run from the registry (its template and the hash of its plan) and the plan from the store of plans.</summary>
public sealed class RegistryRunLiveContextSource(IRunRegistry runs, IPlanStore plans) : IRunLiveContextSource
{
    public async Task<RunLiveContext?> ReadAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            if (await runs.FindAsync(project.ProjectId, run, cancellationToken).ConfigureAwait(false) is not { } record)
            {
                return null;
            }

            var plan = await plans.FindAsync(project.ProjectId, record.PlanHash, cancellationToken).ConfigureAwait(false);
            var dependencies = plan is null
                ? new Dictionary<TaskId, IReadOnlyList<TaskId>>()
                : plan.Tasks.ToDictionary(t => t.Id, t => (IReadOnlyList<TaskId>)[.. t.DependsOn.Select(d => d.Prerequisite)]);
            return new RunLiveContext(record.Roster, dependencies);
        }
#pragma warning disable CA1031 // The stores are behind ports: a run whose header or plan cannot be read keeps its monitor without the extras.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
