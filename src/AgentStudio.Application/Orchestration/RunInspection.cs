using AgentStudio.Application.Projects;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Orchestration;

/// <summary>
/// Opens a run of a project that does not run in this process (a finished run, or one that was interrupted) to look at it:
/// what the monitor shows of it, rebuilt from the verified decision log and the stores, so a run approved by the judge can
/// still be reviewed and delivered after the app was closed. Nothing is launched and nothing is written.
/// </summary>
public interface IRunInspector
{
    /// <summary>
    /// The run <paramref name="run"/> of <paramref name="project"/>, read back as an <see cref="IOrchestrationRun"/> that does
    /// not run:
    /// <list type="bullet">
    /// <item><see cref="IOrchestrationRun.Snapshot"/> is the state its log leaves: its phase, each task with its status, attempt,
    /// retries, agent, block reason and completion evidence, the health of its graph and the agent runs it used;</item>
    /// <item><see cref="IOrchestrationRun.Events"/> is its history in log order, and ends: the transitions of its tasks with
    /// their reasons, the evidence it captured with its task, its pauses and resumes, its deliveries
    /// (<see cref="RunDeliveredEvent"/>) and, when its log cannot be replayed to its end, a last phase event that says why (the
    /// state is then the one before the problem);</item>
    /// <item><see cref="IOrchestrationRun.Completion"/> is what its <c>RunFinished</c> recorded or, for a run that did not
    /// finish, <see cref="RunPhase.Interrupted"/>: then the tasks that were running are shown interrupted (blocked, with the
    /// reason <c>Interrupted</c>), as a resume would record them, after the history of its log;</item>
    /// <item>every command throws <see cref="InvalidOperationException"/>, and disposing it does nothing.</item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidOperationException">The run runs in this process (its monitor follows it), is not registered, never started, or its plan is not stored.</exception>
    Task<IOrchestrationRun> InspectAsync(ProjectContext project, RunId run, CancellationToken cancellationToken);
}
