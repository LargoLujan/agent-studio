using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Infrastructure.Persistence.Json;

/// <summary>A frozen requirement set as stored in <c>requirement_sets.payload_json</c>.</summary>
internal sealed record RequirementSetJson(int V, DateTimeOffset FrozenAt, IReadOnlyList<RequirementJson> Requirements)
{
    public const int Version = 1;

    public static RequirementSetJson From(FrozenRequirementSet set) =>
        new(Version, set.FrozenAt, [.. set.Requirements.Select(RequirementJson.From)]);

    /// <summary>Freezes the stored requirements again, which recomputes the hash from the content.</summary>
    public FrozenRequirementSet ToDomain() =>
        FrozenRequirementSet.Freeze((Requirements ?? []).Select(r => r.ToDomain()), FrozenAt);
}

internal sealed record RequirementJson(string Id, string Statement, RequirementPriority Priority, IReadOnlyList<CriterionJson> Criteria)
{
    public static RequirementJson From(Requirement requirement) =>
        new(requirement.Id.Value, requirement.Statement, requirement.Priority, [.. requirement.Criteria.Select(CriterionJson.From)]);

    public Requirement ToDomain() =>
        new(new RequirementId(Id), Statement, Priority, [.. (Criteria ?? []).Select(c => c.ToDomain())]);
}

internal sealed record CriterionJson(string Id, string Description, VerificationMethod Method)
{
    public static CriterionJson From(AcceptanceCriterion criterion) => new(criterion.Id, criterion.Description, criterion.Method);

    public AcceptanceCriterion ToDomain() => new(Id, Description, Method);
}
