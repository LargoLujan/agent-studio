using AgentStudio.Application.Projects;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Skills;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Desktop.Orchestrator.Preparation;

/// <summary>
/// What the CLI would present of the skills an agent gets (docs/17 D-13, P49), before any run: the skills that are on for it as the
/// provisioner decides them (<see cref="SkillActivationResolver"/>: the agent over the project over the global one, off unless
/// someone turned it on, and only those with a local copy), measured as the engine measures the materialized ones, on the
/// <c>SKILL.md</c> of each. Nothing is materialized, copied or written: it reads the catalog, the activations and the start of
/// each file, so the builder asks it once for each agent and keeps the answer.
/// </summary>
public sealed class SkillPresentationSource(ISkillCatalog catalog, ISkillActivationStore activations)
{
    /// <summary>The characters of the name and the description of every skill that is on for <paramref name="agent"/>.</summary>
    public async Task<int> CharactersAsync(ProjectContext project, AgentDefinition agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(agent);
        var skills = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        var decisions = await activations.ListAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        var folders = skills
            .DistinctBy(s => s.Entry.Id)
            .Where(s => s.LocalPath is not null && SkillActivationResolver.IsEnabled(s.Entry.Id, project.ProjectId, agent.Id.Value, decisions))
            .OrderBy(s => s.Entry.Id.Value, StringComparer.Ordinal)
            .Select(s => s.LocalPath!)
            .ToList();

        // Reading the files is blocking work: off the thread that asked.
        return await Task.Run(() => ExecutorContextPreview.SkillCharacters(folders), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The service for the builder when both ports are composed; null otherwise (then the skills are not counted).</summary>
    public static Func<ProjectContext, AgentDefinition, CancellationToken, Task<int>>? For(ISkillCatalog? catalog, ISkillActivationStore? activations) =>
        catalog is null || activations is null ? null : new SkillPresentationSource(catalog, activations).CharactersAsync;
}
