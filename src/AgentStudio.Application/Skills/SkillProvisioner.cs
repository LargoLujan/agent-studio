using System.Diagnostics;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Application.Skills;

/// <summary>Where the per-run session folders of the skills are created (for the CLIs that load a plugin directory).</summary>
public sealed record SkillProvisioningOptions
{
    public SkillProvisioningOptions(string sessionBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionBaseDirectory);
        SessionBaseDirectory = sessionBaseDirectory;
    }

    public string SessionBaseDirectory { get; }
}

/// <summary>
/// Gives each agent run the skills that are enabled for it: the catalog says what exists and where, the activation
/// history says what is on (<see cref="SkillActivationResolver"/>: Agent over Project over Global, off unless someone
/// turned it on), and <see cref="ISkillMaterializer"/> puts the files where the agent's CLI looks for them. What cannot
/// be provisioned is reported as a warning (in Spanish, for the user), never silently dropped.
/// </summary>
public sealed class SkillProvisioner(
    ISkillCatalog catalog,
    ISkillActivationStore activations,
    ISkillMaterializer materializer,
    SkillProvisioningOptions options) : ISkillProvisioner
{
    public async Task<ProvisionedSkills> ProvisionAsync(string projectId, AgentDefinition agent, string runKey, string workingDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!IsValidRunKey(runKey))
        {
            throw new ArgumentException($"A run key has only letters, digits, '_' and '-': '{runKey}'.", nameof(runKey));
        }

        ct.ThrowIfCancellationRequested();
        var cli = agent.Model.Provider;
        var skills = await catalog.ListAsync(ct).ConfigureAwait(false);
        var decisions = await activations.ListAsync(projectId, ct).ConfigureAwait(false);

        var warnings = new List<string>();
        var selected = new List<ActivatedSkill>();
        var known = new HashSet<SkillId>();
        foreach (var skill in skills.OrderBy(s => s.Entry.Id.Value, StringComparer.Ordinal))
        {
            if (!known.Add(skill.Entry.Id) || !SkillActivationResolver.IsEnabled(skill.Entry.Id, projectId, agent.Id.Value, decisions))
            {
                continue;
            }

            if (skill.LocalPath is null)
            {
                warnings.Add($"La skill '{skill.Entry.Id}' está enlazada y no hay copia local: se omite.");
                continue;
            }

            selected.Add(new ActivatedSkill(skill.Entry, skill.LocalPath));
        }

        foreach (var id in decisions.Select(d => d.Skill).Distinct().Where(id => !known.Contains(id)).OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            if (SkillActivationResolver.IsEnabled(id, projectId, agent.Id.Value, decisions))
            {
                warnings.Add($"La skill '{id}' está activada pero no figura en el catálogo: se omite.");
            }
        }

        if (selected.Count == 0)
        {
            return new ProvisionedSkills(cli, null, [], warnings);
        }

        var directory = await materializer
            .MaterializeAsync(cli, selected, new SkillSessionContext(runKey, options.SessionBaseDirectory, workingDirectory), ct)
            .ConfigureAwait(false);
        return new ProvisionedSkills(cli, directory, [.. selected.Select(s => s.Skill.Id)], warnings);
    }

    public void Release(ProvisionedSkills skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        if (skills.Directory is null)
        {
            return;
        }

        try
        {
            materializer.Cleanup(skills.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Called from finally blocks: a locked file must not hide the failure that is being cleaned up after.
            Debug.WriteLine($"Skills: could not remove '{skills.Directory}': {ex.Message}");
        }
    }

    private static bool IsValidRunKey(string runKey) =>
        !string.IsNullOrEmpty(runKey) && runKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
