namespace AgentStudio.Domain.Skills;

public enum SkillScopeKind
{
    Global,
    Project,
    Agent,
}

/// <summary>
/// An explicit enable/disable decision for a skill at one scope. <c>ScopeId</c> is null for Global. <c>ClosedByRemoval</c> is true only for a decision
/// the store wrote when the agent was taken out of the template (ADR-0048, addendum of 2026-11-11, point 8: nobody chose it one by one), so the screen can say
/// why an agent that came back with the same id starts with a skill off at its own level (P57). Rows written before this existed read as false.
/// </summary>
public sealed record SkillActivation(SkillId Skill, SkillScopeKind Scope, string? ScopeId, bool Enabled, bool ClosedByRemoval = false);

/// <summary>
/// Resolves whether a skill is enabled for a given project and agent. Precedence: Agent over Project over Global.
/// With no applicable activation the skill is disabled: third-party skills never activate themselves.
/// If a scope holds several activations for the same skill, the last one enumerated wins.
/// </summary>
public static class SkillActivationResolver
{
    public static bool IsEnabled(
        SkillId skill,
        string? projectId,
        string? agentId,
        IEnumerable<SkillActivation> activations)
    {
        ArgumentNullException.ThrowIfNull(activations);
        var forSkill = activations.Where(a => a.Skill == skill).ToList();

        if (agentId is not null && Last(forSkill, SkillScopeKind.Agent, agentId) is { } byAgent)
        {
            return byAgent.Enabled;
        }

        if (projectId is not null && Last(forSkill, SkillScopeKind.Project, projectId) is { } byProject)
        {
            return byProject.Enabled;
        }

        return Last(forSkill, SkillScopeKind.Global, null)?.Enabled ?? false;
    }

    private static SkillActivation? Last(List<SkillActivation> list, SkillScopeKind scope, string? scopeId) =>
        list.LastOrDefault(a => a.Scope == scope
            && (scope == SkillScopeKind.Global || string.Equals(a.ScopeId, scopeId, StringComparison.Ordinal)));
}
