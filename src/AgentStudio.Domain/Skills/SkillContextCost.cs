using AgentStudio.Domain.Metrics;

namespace AgentStudio.Domain.Skills;

/// <summary>
/// The context a skill costs (docs/14 D-26, docs/10 section 1): a CLI shows the model the name and the description of
/// every skill it has, in every run and whether or not the skill is used, so each active skill takes about that many
/// characters divided by <see cref="CharactersPerToken"/>. It is always an estimate of Agent Studio
/// (<see cref="MetricOrigin.EstimatedBySystem"/>), never presented as exact. The catalog summary stands for the
/// description; the body of a SKILL.md is only read when the skill is used, and is not counted here.
/// </summary>
public static class SkillContextCost
{
    /// <summary>The average Agent Studio assumes; a part of a token counts as a whole one.</summary>
    public const int CharactersPerToken = 4;

    /// <summary>The <see cref="MetricValue{T}.Source"/> of every estimate made here.</summary>
    public const string Source = "estimate: (name + description) characters / 4";

    /// <summary>The estimated tokens of one skill: its name and description, divided by 4 and rounded up.</summary>
    public static MetricValue<long> Of(SkillEntry skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return MetricValue<long>.EstimatedBySystem(Tokens(skill), Source);
    }

    /// <summary>The estimated tokens of a set of active skills (an agent's): the sum of theirs, still an estimate; 0 for none.</summary>
    public static MetricValue<long> Total(IEnumerable<SkillEntry> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var total = 0L;
        foreach (var skill in skills)
        {
            ArgumentNullException.ThrowIfNull(skill, nameof(skills));
            total += Tokens(skill);
        }

        return MetricValue<long>.EstimatedBySystem(total, Source);
    }

    private static long Tokens(SkillEntry skill)
    {
        long characters = skill.Name.Length + skill.Summary.Length;
        return (characters + CharactersPerToken - 1) / CharactersPerToken;
    }
}
