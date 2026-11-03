using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Requirements;

public enum RequirementPriority
{
    Mandatory,
    Desirable,
}

/// <summary>How an acceptance criterion is checked. Only the first two produce objective evidence.</summary>
public enum VerificationMethod
{
    AutomatedTest,
    CommandCheck,
    HumanReview,
    JudgeAssessment,
}

public sealed record AcceptanceCriterion(string Id, string Description, VerificationMethod Method);

public sealed record Requirement(
    RequirementId Id,
    string Statement,
    RequirementPriority Priority,
    IReadOnlyList<AcceptanceCriterion> Criteria);

/// <summary>
/// Immutable copy of the requirements taken at a given moment ("congelar los requisitos"). Debates and judges work
/// against a frozen set and quote its <see cref="ContentHash"/>, so nobody can quietly change what is being judged.
/// </summary>
public sealed class FrozenRequirementSet
{
    private FrozenRequirementSet(IReadOnlyList<Requirement> requirements, DateTimeOffset frozenAt, string contentHash)
    {
        Requirements = requirements;
        FrozenAt = frozenAt;
        ContentHash = contentHash;
    }

    public IReadOnlyList<Requirement> Requirements { get; }

    public DateTimeOffset FrozenAt { get; }

    /// <summary>SHA-256 of the canonical content. Independent of the freezing time and of the input order.</summary>
    public string ContentHash { get; }

    public IEnumerable<Requirement> Mandatory => Requirements.Where(r => r.Priority == RequirementPriority.Mandatory);

    public static FrozenRequirementSet Freeze(IEnumerable<Requirement> requirements, DateTimeOffset frozenAt)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var ordered = requirements.OrderBy(r => r.Id.Value, StringComparer.Ordinal).ToList();

        if (ordered.Count == 0)
        {
            throw new DomainRuleViolationException("requirements.empty", "A requirement set cannot be empty.");
        }

        var duplicate = ordered.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new DomainRuleViolationException("requirements.duplicate-id", $"Requirement '{duplicate.Key}' appears more than once.");
        }

        foreach (var requirement in ordered)
        {
            if (string.IsNullOrWhiteSpace(requirement.Statement))
            {
                throw new DomainRuleViolationException("requirements.statement-required", $"Requirement '{requirement.Id}' has no statement.");
            }

            if (requirement.Priority == RequirementPriority.Mandatory && requirement.Criteria.Count == 0)
            {
                throw new DomainRuleViolationException(
                    "requirements.mandatory-needs-criteria",
                    $"Mandatory requirement '{requirement.Id}' needs at least one acceptance criterion.");
            }
        }

        var copy = ordered
            .Select(r => r with { Criteria = r.Criteria.OrderBy(c => c.Id, StringComparer.Ordinal).ToList() })
            .ToList();

        return new FrozenRequirementSet(copy, frozenAt, ComputeHash(copy));
    }

    public Requirement? Find(RequirementId id) => Requirements.FirstOrDefault(r => r.Id == id);

    private static string ComputeHash(IEnumerable<Requirement> requirements)
    {
        var fields = new List<string?>();
        foreach (var requirement in requirements)
        {
            fields.Add(requirement.Id.Value);
            fields.Add(requirement.Priority.ToString());
            fields.Add(requirement.Statement);
            fields.Add(requirement.Criteria.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var criterion in requirement.Criteria)
            {
                fields.Add(criterion.Id);
                fields.Add(criterion.Method.ToString());
                fields.Add(criterion.Description);
            }
        }

        return Common.ContentHash.Sha256Hex(Common.ContentHash.Canonical([.. fields]));
    }
}
