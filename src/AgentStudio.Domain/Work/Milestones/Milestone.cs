using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Work.Milestones;

/// <summary>
/// Identifier of a milestone, such as <c>mil-20261115-a1b2c3</c>. It is the run id of the entries of the log that belong to
/// it (a milestone is a unit of the log like a debate, docs/16 D-17), so it has the form of one: 1 to 64 characters from
/// <c>[A-Za-z0-9_-]</c>, and it starts with <see cref="Prefix"/>, which no run id has.
/// </summary>
public readonly record struct MilestoneId
{
    public const string Prefix = "mil-";

    public const int MaxLength = 64;

    public MilestoneId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaxLength || value.Length <= Prefix.Length || !value.StartsWith(Prefix, StringComparison.Ordinal) || !value.All(IsAllowed))
        {
            throw new ArgumentException($"A milestone id starts with '{Prefix}' and has up to {MaxLength} characters from [A-Za-z0-9_-]: '{value}'.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static bool IsAllowed(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}

/// <summary>What can be linked to a milestone (docs/16 D-17): an orchestration run (its follow-ups come with it) or a debate.</summary>
public enum MilestoneTargetKind
{
    Run,
    Debate,
}

/// <summary>A run or a debate linked to a milestone, by its id (<c>20261015-a1b2c3</c>, <c>deb-20261015-a1b2c3</c>).</summary>
public sealed record MilestoneTarget(MilestoneTargetKind Kind, string Id);

/// <summary>Whether a milestone takes changes (<see cref="Open"/>) or was closed by an audit and awaits a reopening.</summary>
public enum MilestoneStatus
{
    Open,
    Closed,
}

/// <summary>Why a closed milestone is reopened (docs/16 D-50): a revert, a check that fails, a new challenge of a verdict, or a person's decision.</summary>
public enum MilestoneReopenReason
{
    Revert,
    CheckFailed,
    Challenge,
    ByPerson,
}

/// <summary>
/// The immutable header of a milestone (D-17), as <c>milestones</c> stores it and <c>MilestoneOpened</c> anchors its hash: its
/// name and objective (the person's text), the first set of requirements it works against (by the hash of a frozen set) and,
/// optionally, its budget (D-05).
/// </summary>
public sealed record MilestoneHeader(
    MilestoneId Id,
    string Name,
    string Objective,
    string RequirementSetHash,
    WorkBudget? Budget,
    DateTimeOffset CreatedAt);

/// <summary>
/// The close of a milestone (D-49): the audit that closed it, the SHA-256 of the report generated from the log (its
/// evidence is in the entry) and the commit audited. <see cref="Revision"/> counts the reports of the milestone, from 1: a
/// reopening and a new close make revision 2 and the first report stays (D-50).
/// </summary>
public sealed record MilestoneClosure(DateTimeOffset At, int Revision, string Audit, string ReportSha256, string? IntegratedCommit);

/// <summary>A reopening of a closed milestone (D-50): why, and the SHA-256 of its evidence when it has one.</summary>
public sealed record MilestoneReopening(DateTimeOffset At, MilestoneReopenReason Reason, string? EvidenceSha256);

/// <summary>
/// A milestone (docs/16 D-17, F9-1): a name, an objective and the sequence of frozen sets of requirements it has worked
/// against, with the runs and debates linked to it. Only its header is stored; its state is rebuilt by replaying the log
/// (<see cref="MilestoneStateReplayer"/>), through this API and nothing else, as a run and a debate are.
/// </summary>
/// <remarks>
/// <para>The rules:</para>
/// <list type="bullet">
/// <item>An open milestone takes revisions of its requirements (a set different from the current one: the earlier stays in
/// its history), links to runs and debates (each once) and a close.</item>
/// <item>A milestone closes only with something linked to it that was a run: closing is auditing the work of its runs
/// (D-49), and there is nothing to audit in a milestone with none.</item>
/// <item>A closed milestone takes nothing but a reopening, which keeps every report it had.</item>
/// <item>The times only go forward.</item>
/// </list>
/// </remarks>
public sealed class Milestone
{
    public const int MaxNameLength = 120;

    public const int MaxObjectiveLength = 2000;

    private readonly List<string> _requirementSets;
    private readonly List<MilestoneTarget> _targets = [];
    private readonly List<MilestoneClosure> _closures = [];
    private readonly List<MilestoneReopening> _reopenings = [];
    private DateTimeOffset _lastAt;

    private Milestone(MilestoneHeader header)
    {
        Header = header;
        _requirementSets = [header.RequirementSetHash];
        _lastAt = header.CreatedAt;
    }

    public MilestoneHeader Header { get; }

    public MilestoneId Id => Header.Id;

    public MilestoneStatus Status { get; private set; } = MilestoneStatus.Open;

    /// <summary>The hash of the frozen set the milestone works against now.</summary>
    public string CurrentRequirementSetHash => _requirementSets[^1];

    /// <summary>The hashes of every set it has had, in order: the first is the header's.</summary>
    public IReadOnlyList<string> RequirementSetHistory => _requirementSets;

    /// <summary>The runs and debates linked, in the order they were linked.</summary>
    public IReadOnlyList<MilestoneTarget> Targets => _targets;

    /// <summary>The reports of its closes, oldest first: reopening keeps them.</summary>
    public IReadOnlyList<MilestoneClosure> Closures => _closures;

    /// <summary>The reopenings, oldest first.</summary>
    public IReadOnlyList<MilestoneReopening> Reopenings => _reopenings;

    /// <summary>Opens a milestone from its header.</summary>
    /// <exception cref="DomainRuleViolationException">The name or the objective is blank or too long, the hash is not a SHA-256 or the budget is not valid.</exception>
    public static Milestone Open(MilestoneHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (string.IsNullOrWhiteSpace(header.Name) || header.Name.Length > MaxNameLength || header.Name.Any(char.IsControl))
        {
            throw new DomainRuleViolationException("milestone.name", $"A milestone has a name of 1 to {MaxNameLength} characters on one line.");
        }

        if (string.IsNullOrWhiteSpace(header.Objective) || header.Objective.Length > MaxObjectiveLength)
        {
            throw new DomainRuleViolationException("milestone.objective", $"A milestone has an objective of 1 to {MaxObjectiveLength} characters.");
        }

        RequireSha256(header.RequirementSetHash, "milestone.requirements-hash");
        if (header.Budget is { } budget)
        {
            budget.EnsureValid();
            if (budget.Period != BudgetPeriod.Total)
            {
                throw new DomainRuleViolationException("milestone.budget-period", "The budget of a milestone is for the whole milestone, not for a period.");
            }
        }

        return new Milestone(header);
    }

    /// <summary>Revises the requirements: the milestone works against <paramref name="requirementSetHash"/> from now on, and the set it had stays in its history.</summary>
    /// <exception cref="DomainRuleViolationException">The milestone is closed, the hash is not a SHA-256, it is the set the milestone has now, or time went back.</exception>
    public void ReviseRequirements(string requirementSetHash, DateTimeOffset at)
    {
        RequireOpen();
        RequireSha256(requirementSetHash, "milestone.requirements-hash");
        RequireForward(at);
        if (string.Equals(requirementSetHash, CurrentRequirementSetHash, StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException("milestone.requirements-unchanged", "The revision names the set the milestone already works against: a revision is a different set.");
        }

        _requirementSets.Add(requirementSetHash);
        _lastAt = at;
    }

    /// <summary>Links a run or a debate. Each is linked once.</summary>
    /// <exception cref="DomainRuleViolationException">The milestone is closed, the target has no id or is already linked, or time went back.</exception>
    public void Link(MilestoneTarget target, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireOpen();
        RequireForward(at);
        if (!Enum.IsDefined(target.Kind) || string.IsNullOrWhiteSpace(target.Id))
        {
            throw new DomainRuleViolationException("milestone.target", "A run or a debate is linked by its kind and its id.");
        }

        if (_targets.Contains(target))
        {
            throw new DomainRuleViolationException("milestone.already-linked", $"{target.Kind} '{target.Id}' is already linked to the milestone.");
        }

        _targets.Add(target);
        _lastAt = at;
    }

    /// <summary>Closes the milestone with the report of its audit.</summary>
    /// <exception cref="DomainRuleViolationException">The milestone is closed already, no run is linked, the revision is not the next, the audit or the report is missing, or time went back.</exception>
    public void Close(MilestoneClosure closure)
    {
        ArgumentNullException.ThrowIfNull(closure);
        RequireOpen();
        RequireForward(closure.At);
        if (!_targets.Any(t => t.Kind == MilestoneTargetKind.Run))
        {
            throw new DomainRuleViolationException("milestone.nothing-to-close", "A milestone with no run linked has nothing to audit: it cannot be closed.");
        }

        if (closure.Revision != _closures.Count + 1)
        {
            throw new DomainRuleViolationException("milestone.revision", $"The report is revision {closure.Revision}, but the next one is {_closures.Count + 1}.");
        }

        if (string.IsNullOrWhiteSpace(closure.Audit))
        {
            throw new DomainRuleViolationException("milestone.audit", "Closing a milestone is auditing it: the close names its audit.");
        }

        RequireSha256(closure.ReportSha256, "milestone.report-hash");
        _closures.Add(closure);
        Status = MilestoneStatus.Closed;
        _lastAt = closure.At;
    }

    /// <summary>Reopens a closed milestone. The reports of its earlier closes stay.</summary>
    /// <exception cref="DomainRuleViolationException">The milestone is open, the reason is unknown, a reason that is not a person's has no evidence, or time went back.</exception>
    public void Reopen(MilestoneReopening reopening)
    {
        ArgumentNullException.ThrowIfNull(reopening);
        if (Status != MilestoneStatus.Closed)
        {
            throw new DomainRuleViolationException("milestone.not-closed", "Only a closed milestone is reopened.");
        }

        RequireForward(reopening.At);
        if (!Enum.IsDefined(reopening.Reason))
        {
            throw new DomainRuleViolationException("milestone.reopen-reason", "Unknown reason to reopen a milestone.");
        }

        if (reopening.Reason != MilestoneReopenReason.ByPerson && reopening.EvidenceSha256 is null)
        {
            throw new DomainRuleViolationException("milestone.reopen-evidence", "A revert, a check that fails and a challenge reopen a milestone with their evidence.");
        }

        if (reopening.EvidenceSha256 is not null)
        {
            RequireSha256(reopening.EvidenceSha256, "milestone.evidence-hash");
        }

        _reopenings.Add(reopening);
        Status = MilestoneStatus.Open;
        _lastAt = reopening.At;
    }

    private void RequireOpen()
    {
        if (Status != MilestoneStatus.Open)
        {
            throw new DomainRuleViolationException("milestone.not-open", "A closed milestone takes nothing until it is reopened.");
        }
    }

    private void RequireForward(DateTimeOffset at)
    {
        if (at < _lastAt)
        {
            throw new DomainRuleViolationException("milestone.time-order", "The events of a milestone only go forward in time.");
        }
    }

    private static void RequireSha256(string? hash, string rule)
    {
        if (!ContentHash.IsSha256Hex(hash))
        {
            throw new DomainRuleViolationException(rule, $"'{hash}' is not a SHA-256 in lowercase hexadecimal.");
        }
    }
}
