using AgentStudio.Application.Projects;
using AgentStudio.Domain.Work;

namespace AgentStudio.Application.Work;

/// <summary>
/// Where the budget of a project is kept (docs/16 D-05, F9-8). Like the options of the project it is the person's, per project and
/// per computer; nothing of it goes into the repository. The budget of a milestone is part of its header. The decision of whether an
/// attempt may launch is <see cref="WorkBudget.Check"/>; the engine applies it (WP2).
/// </summary>
public interface IProjectBudgetStore
{
    /// <summary>The budget chosen for the project; null when there is none.</summary>
    Task<WorkBudget?> LoadAsync(ProjectContext project, CancellationToken cancellationToken);

    /// <summary>Saves the budget; null removes it. A budget that does not validate is refused.</summary>
    Task SaveAsync(ProjectContext project, WorkBudget? budget, CancellationToken cancellationToken);
}
