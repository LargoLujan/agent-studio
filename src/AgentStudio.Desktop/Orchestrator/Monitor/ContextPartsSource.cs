using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Observability.Context;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>
/// Where the monitor reads the parts of the prompt of an attempt that already started (docs/17 D-13, F13-6): the events of the
/// run do not carry them, but <c>AgentRunStarted</c> version 3 does, in the verified log. Optional: without it the bar by parts
/// says «sin desglose» instead of showing a figure it does not have.
/// </summary>
public interface IContextPartsSource
{
    /// <summary>
    /// The parts of the attempt <paramref name="attempt"/> of <paramref name="task"/> as its start recorded them, in the order of
    /// <see cref="ContextPartKind"/>; null when the start recorded none (before version 3, or not written yet).
    /// </summary>
    /// <exception cref="InvalidOperationException">The log does not verify: its figures are not shown.</exception>
    Task<IReadOnlyList<ContextPartSnapshot>?> ReadAsync(ProjectContext project, RunId run, TaskId task, int attempt, CancellationToken cancellationToken);
}

/// <summary>
/// The parts of an attempt read from the decision log of the project (<see cref="ContextPartsLog.Find"/>). A log that does not
/// verify fails closed: the parts of a log that may have been altered are not shown.
/// </summary>
public sealed class DecisionLogContextPartsSource(IDecisionRecorder decisions) : IContextPartsSource
{
    public async Task<IReadOnlyList<ContextPartSnapshot>?> ReadAsync(ProjectContext project, RunId run, TaskId task, int attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var log = await decisions.LoadAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new InvalidOperationException($"The decision log of project '{project.ProjectId}' does not verify: the parts of the prompt are not read from it.");
        }

        return ContextPartsLog.Find(log.Entries, run.Value, task, attempt)?.Parts;
    }
}
