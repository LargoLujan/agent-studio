using System.Globalization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Domain.Work.Backlog;

/// <summary>
/// Identifier of an item of the backlog, such as <c>bk-20261115-a1b2c3</c>: stable through every version of the item. 1 to
/// 64 characters from <c>[A-Za-z0-9_-]</c>, starting with <see cref="Prefix"/>.
/// </summary>
public readonly record struct BacklogItemId
{
    public const string Prefix = "bk-";

    public const int MaxLength = 64;

    public BacklogItemId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaxLength || value.Length <= Prefix.Length || !value.StartsWith(Prefix, StringComparison.Ordinal) || !value.All(IsAllowed))
        {
            throw new ArgumentException($"A backlog item id starts with '{Prefix}' and has up to {MaxLength} characters from [A-Za-z0-9_-]: '{value}'.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static bool IsAllowed(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}

/// <summary>
/// One version of an item of the backlog (docs/16 D-18, F9-2): a requirement in draft, which is what the person notes for later:
/// its statement, its priority as a requirement (<see cref="RequirementPriority"/>) and, if they want, its acceptance
/// criteria. Version 1 is the item as it was added; each change is a new version with all of its content, and the earlier
/// ones stay. It has no id as a requirement until it is planned into a milestone's frozen set (<see cref="ToRequirement"/>).
/// </summary>
/// <param name="Id">The id of the item, the same in every version.</param>
/// <param name="Version">1 for the item as added, and one more for each change.</param>
/// <param name="Statement">What the requirement says.</param>
/// <param name="Priority">Whether it will be mandatory or desirable once planned.</param>
/// <param name="Criteria">The acceptance criteria, possibly none: a draft does not need them yet, a mandatory requirement does when it is frozen.</param>
/// <param name="CreatedAt">When this version was written.</param>
public sealed record BacklogItem(
    BacklogItemId Id,
    int Version,
    string Statement,
    RequirementPriority Priority,
    IReadOnlyList<AcceptanceCriterion> Criteria,
    DateTimeOffset CreatedAt)
{
    /// <summary>The longest statement an item keeps, in characters.</summary>
    public const int MaxStatementLength = 2000;

    /// <summary>The codes of what is wrong with this version; empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Version < 1)
        {
            problems.Add("backlog.version");
        }

        if (string.IsNullOrWhiteSpace(Statement) || Statement.Length > MaxStatementLength)
        {
            problems.Add("backlog.statement");
        }

        if (!Enum.IsDefined(Priority))
        {
            problems.Add("backlog.priority");
        }

        var criteria = Criteria ?? [];
        if (criteria.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Description) || !Enum.IsDefined(c.Method)))
        {
            problems.Add("backlog.criterion");
        }
        else if (criteria.GroupBy(c => c.Id, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            problems.Add("backlog.criterion-duplicate");
        }

        return problems;
    }

    /// <summary>
    /// The canonical SHA-256 that <c>BacklogItemRecorded</c> announces and the row is checked against, as
    /// <c>MemoryContent.Sha256</c> is for memory: the id, the version, the statement, the priority and the criteria in the
    /// order of their ids, every field with a length prefix, so two different versions never share a hash. The time is not
    /// part of it.
    /// </summary>
    public string Sha256()
    {
        var fields = new List<string?>
        {
            "backlog-item/1",
            Id.Value,
            Version.ToString(CultureInfo.InvariantCulture),
            Statement,
            Priority.ToString(),
        };
        var criteria = (Criteria ?? []).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        fields.Add(criteria.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var criterion in criteria)
        {
            fields.Add(criterion.Id);
            fields.Add(criterion.Method.ToString());
            fields.Add(criterion.Description);
        }

        return ContentHash.Sha256Hex(ContentHash.Canonical([.. fields]));
    }

    /// <summary>The requirement this item becomes when it is planned into a milestone, under the id the person gives it there.</summary>
    public Requirement ToRequirement(RequirementId id) => new(id, Statement, Priority, [.. (Criteria ?? []).OrderBy(c => c.Id, StringComparer.Ordinal)]);
}
