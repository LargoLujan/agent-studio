using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Backlog;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Application.Decisions;

// The payloads of Phase 9 (docs/16 D-17, D-18, D-29 and D-49, section 4.6). Like every payload they are written once, in the
// one format of DecisionPayloadSerializer, and the log hashes their text: to change one, add a field and raise V. None carries
// text of an agent. The milestone entries carry the id of the milestone (mil-yyyyMMdd-xxxxxx) as their run id, like a debate:
// RunLog.EntriesOf selects them as it selects those of a run. The backlog entries and the trace links span runs, so they
// belong to no run (DecisionPayload.NoRun): replay and the dossier of the judge never see them. The people's own words in
// them (the name of a milestone, a reason) are the person's text; what they cite as evidence is cited by id and SHA-256.

/// <summary>
/// A milestone opens (D-17). It anchors the canonical hash of the immutable header stored in <c>milestones</c>
/// (<paramref name="HeaderHash"/>, <c>MilestoneHeaderHash.Of</c>) and repeats what a reader of the log needs: the name, the first
/// set of requirements and the budget. The objective is the person's text and lives in the header, which the hash covers.
/// </summary>
public sealed record MilestoneOpenedPayload(
    string HeaderHash,
    string Name,
    string RequirementSetHash,
    WorkBudget? Budget,
    string OpenedBy) : DecisionPayload
{
    /// <summary>The opening of milestone <paramref name="milestone"/>, whose entries carry its id as their run id.</summary>
    /// <exception cref="ArgumentException">A hash that is not a SHA-256, a blank name or nobody who opened it.</exception>
    public static MilestoneOpenedPayload Opening(MilestoneId milestone, string headerHash, string name, string requirementSetHash, WorkBudget? budget, string openedBy)
    {
        WorkPayloadRules.RequireSha256(headerHash, nameof(headerHash));
        WorkPayloadRules.RequireSha256(requirementSetHash, nameof(requirementSetHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(openedBy);
        budget?.EnsureValid();
        return new MilestoneOpenedPayload(headerHash, name, requirementSetHash, budget, openedBy) { RunId = milestone.Value };
    }
}

/// <summary>The requirements of a milestone are revised (D-17): the hash of the frozen set it now works against and of the one it replaces, who and why.</summary>
public sealed record MilestoneRequirementsRevisedPayload(string RequirementSetHash, string PreviousRequirementSetHash, string RevisedBy, string? Reason) : DecisionPayload
{
    public static MilestoneRequirementsRevisedPayload Revising(MilestoneId milestone, string requirementSetHash, string previousRequirementSetHash, string revisedBy, string? reason)
    {
        WorkPayloadRules.RequireSha256(requirementSetHash, nameof(requirementSetHash));
        WorkPayloadRules.RequireSha256(previousRequirementSetHash, nameof(previousRequirementSetHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(revisedBy);
        if (string.Equals(requirementSetHash, previousRequirementSetHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("A revision is a set different from the one it replaces.", nameof(requirementSetHash));
        }

        return new MilestoneRequirementsRevisedPayload(requirementSetHash, previousRequirementSetHash, revisedBy, reason) { RunId = milestone.Value };
    }
}

/// <summary>
/// A run or a debate is linked to a milestone (D-17 and D-29): <paramref name="LinkedBy"/> is <c>agent-studio</c> when the system
/// links a run it launched from the milestone and the person's name when they link one. A run is linked with its follow-ups: they
/// are found from the starts of the runs (<c>RunStarted.Follows</c>), never written here.
/// </summary>
public sealed record MilestoneLinkedPayload(MilestoneTargetKind Kind, string Target, string LinkedBy) : DecisionPayload
{
    public static MilestoneLinkedPayload Linking(MilestoneId milestone, MilestoneTargetKind kind, string target, string linkedBy)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of target of a milestone.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkedBy);
        return new MilestoneLinkedPayload(kind, target, linkedBy) { RunId = milestone.Value };
    }
}

/// <summary>
/// A milestone is closed by auditing it (D-49 and D-50): which report of the milestone it is (<paramref name="Revision"/>, from 1),
/// the audit (<c>aud-…</c>, its own unit of the log), the evidence of the deterministic report with its SHA-256, the commit audited
/// (the person's branch after the delivery, or with Phase 10 the merge commit of the pull request) and who closed it.
/// </summary>
public sealed record MilestoneClosedPayload(int Revision, string Audit, EvidenceId Report, string ReportSha256, string? IntegratedCommit, string ClosedBy) : DecisionPayload
{
    public static MilestoneClosedPayload Closing(MilestoneId milestone, int revision, string audit, EvidenceId report, string reportSha256, string? integratedCommit, string closedBy)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit);
        ArgumentException.ThrowIfNullOrWhiteSpace(closedBy);
        WorkPayloadRules.RequireSha256(reportSha256, nameof(reportSha256));
        return new MilestoneClosedPayload(revision, audit, report, reportSha256, integratedCommit, closedBy) { RunId = milestone.Value };
    }
}

/// <summary>
/// A closed milestone is reopened (D-50): why, the evidence of the reason with its SHA-256 (the revert, the check that fails, the
/// challenge; a person's own decision may have none) and who. The report it had stays as evidence; the next close is the next revision.
/// </summary>
public sealed record MilestoneReopenedPayload(MilestoneReopenReason Reason, EvidenceId? Evidence, string? EvidenceSha256, string ReopenedBy, string? Note) : DecisionPayload
{
    public static MilestoneReopenedPayload Reopening(MilestoneId milestone, MilestoneReopenReason reason, EvidenceId? evidence, string? evidenceSha256, string reopenedBy, string? note)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown reason to reopen a milestone.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reopenedBy);
        if ((evidence is null) != (evidenceSha256 is null))
        {
            throw new ArgumentException("The evidence of the reason is cited by its id and its SHA-256, both or neither.", nameof(evidenceSha256));
        }

        if (evidenceSha256 is not null)
        {
            WorkPayloadRules.RequireSha256(evidenceSha256, nameof(evidenceSha256));
        }

        if (reason != MilestoneReopenReason.ByPerson && evidence is null)
        {
            throw new ArgumentException("A revert, a check that fails and a challenge reopen a milestone with their evidence.", nameof(evidence));
        }

        return new MilestoneReopenedPayload(reason, evidence, evidenceSha256, reopenedBy, note) { RunId = milestone.Value };
    }
}

/// <summary>
/// An item of the backlog is announced before its row is stored (D-18), as <c>MemoryRecorded</c> announces a memory entry: its id,
/// its version (1 for the item added, one more for each change), its priority and the canonical SHA-256 of its content
/// (<see cref="BacklogItem.Sha256"/>: statement, priority and criteria). It carries no text of the item: the row holds it, and
/// reading the backlog only returns rows announced here with the same hash. It belongs to no run.
/// </summary>
public sealed record BacklogItemRecordedPayload(string Item, int Version, RequirementPriority Priority, string Sha256) : DecisionPayload
{
    /// <summary>The announcement of a version of an item, outside every run.</summary>
    public static BacklogItemRecordedPayload Announcing(BacklogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var problems = item.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"The item is not valid: {string.Join(", ", problems)}.", nameof(item));
        }

        return new BacklogItemRecordedPayload(item.Id.Value, item.Version, item.Priority, item.Sha256()) { RunId = NoRun };
    }
}

/// <summary>A person retired an item of the backlog (D-18): the item and the last version it had. No row is deleted. It belongs to no run.</summary>
public sealed record BacklogItemRetiredPayload(string Item, int Version, string DecidedBy, string? Reason) : DecisionPayload
{
    public static BacklogItemRetiredPayload Retiring(BacklogItemId item, int version, string decidedBy, string? reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        return new BacklogItemRetiredPayload(item.Value, version, decidedBy, reason) { RunId = NoRun };
    }
}

/// <summary>A person changed the order of the backlog (D-18): the ids of the items, in their new order. Items it does not list follow in the order they were added. It belongs to no run.</summary>
public sealed record BacklogOrderChangedPayload(IReadOnlyList<string> Items, string DecidedBy) : DecisionPayload
{
    public static BacklogOrderChangedPayload Ordering(IReadOnlyList<BacklogItemId> items, string decidedBy)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        var ids = items.Select(i => i.Value).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new ArgumentException("The order lists each item once.", nameof(items));
        }

        return new BacklogOrderChangedPayload(ids, decidedBy) { RunId = NoRun };
    }
}

/// <summary>
/// An explicit link of the trace is recorded (D-29): its id (<see cref="TraceLinks.IdOf"/>), both ends by kind and id, who asserts it
/// and why. <paramref name="Suggestion"/> is where it was inferred from when it is a suggestion a person confirmed (D-30), null for
/// a link recorded as such. It belongs to no run: a link joins things across runs.
/// </summary>
public sealed record TraceLinkRecordedPayload(
    string Link,
    TraceNodeKind FromKind,
    string From,
    TraceNodeKind ToKind,
    string To,
    TraceAssertedBy AssertedBy,
    string Reason,
    TraceSuggestionSource? Suggestion) : DecisionPayload
{
    /// <summary>The record of a link, outside every run.</summary>
    /// <exception cref="ArgumentException">The link does not validate, or a link that is a confirmed suggestion is not asserted by a person.</exception>
    public static TraceLinkRecordedPayload Recording(TraceLink link, TraceSuggestionSource? suggestion = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        var problems = link.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"The link is not valid: {string.Join(", ", problems)}.", nameof(link));
        }

        if (suggestion is { } source && (!Enum.IsDefined(source) || link.AssertedBy != TraceAssertedBy.User))
        {
            throw new ArgumentException("A suggestion only becomes a link when a person confirms it.", nameof(suggestion));
        }

        return new TraceLinkRecordedPayload(link.Id, link.From.Kind, link.From.Id, link.To.Kind, link.To.Id, link.AssertedBy, link.Reason, suggestion) { RunId = NoRun };
    }

    /// <summary>
    /// The link this entry recorded, at <paramref name="at"/>. The entry is read as it was written: an id that is not the one its ends
    /// and its author give, or an end that is not a valid node, is refused, so a payload edited by hand is never read as a link.
    /// </summary>
    /// <exception cref="ArgumentException">The ends are not valid nodes, or <see cref="Link"/> is not the id of this link.</exception>
    public TraceLink ToLink(DateTimeOffset at)
    {
        var link = new TraceLink(new TraceRef(FromKind, From), new TraceRef(ToKind, To), AssertedBy, Reason, at);
        if (!string.Equals(link.Id, Link, StringComparison.Ordinal))
        {
            throw new ArgumentException("The id of the link is not the one its ends and its author give.", nameof(at));
        }

        return link;
    }
}

/// <summary>A link of the trace is withdrawn (D-29): its id, who and why. The link stays in the log. It belongs to no run.</summary>
public sealed record TraceLinkRetractedPayload(string Link, string RetractedBy, string Reason) : DecisionPayload
{
    public static TraceLinkRetractedPayload Retracting(string link, string retractedBy, string reason)
    {
        WorkPayloadRules.RequireSha256(link, nameof(link));
        ArgumentException.ThrowIfNullOrWhiteSpace(retractedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > TraceLink.MaxReasonLength)
        {
            throw new ArgumentException($"The reason of a retraction is up to {TraceLink.MaxReasonLength} characters.", nameof(reason));
        }

        return new TraceLinkRetractedPayload(link, retractedBy, reason) { RunId = NoRun };
    }
}

internal static class WorkPayloadRules
{
    public static void RequireSha256(string value, string name)
    {
        if (!ContentHash.IsSha256Hex(value))
        {
            throw new ArgumentException($"'{value}' is not a SHA-256 in lowercase hexadecimal.", name);
        }
    }
}
