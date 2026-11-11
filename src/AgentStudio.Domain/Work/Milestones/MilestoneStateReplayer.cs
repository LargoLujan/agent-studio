using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Work.Milestones;

/// <summary>A fact of a milestone that changes its state, read back from the log (<c>MilestoneRequirementsRevised</c>, <c>MilestoneLinked</c>, <c>MilestoneClosed</c>, <c>MilestoneReopened</c>).</summary>
public abstract record MilestoneEvent(DateTimeOffset At);

/// <summary>The milestone works against another frozen set of requirements (<c>MilestoneRequirementsRevised</c>).</summary>
public sealed record RequirementsRevised(DateTimeOffset At, string RequirementSetHash) : MilestoneEvent(At);

/// <summary>A run or a debate was linked (<c>MilestoneLinked</c>).</summary>
public sealed record TargetLinked(DateTimeOffset At, MilestoneTarget Target) : MilestoneEvent(At);

/// <summary>The milestone was closed by an audit (<c>MilestoneClosed</c>).</summary>
public sealed record ClosureRecorded(MilestoneClosure Closure) : MilestoneEvent(Closure.At);

/// <summary>The milestone was reopened (<c>MilestoneReopened</c>).</summary>
public sealed record ReopeningRecorded(MilestoneReopening Reopening) : MilestoneEvent(Reopening.At);

/// <summary>
/// A milestone rebuilt from its log. A replay that reports any <paramref name="Problems"/> describes a milestone the domain
/// does not allow, so a person must look at it. The milestone is the state just before the first problem, or null when the
/// header itself does not open.
/// </summary>
public sealed record MilestoneReplay(Milestone? Milestone, IReadOnlyList<string> Problems)
{
    /// <summary>Whether the log describes a milestone the domain allows, from its header to its last event.</summary>
    public bool IsConsistent => Milestone is not null && Problems.Count == 0;
}

/// <summary>
/// Rebuilds the state of a milestone (docs/16 D-17, F9-1) by applying its events, in order, through the public API of
/// <see cref="Milestone"/> and nothing else, like <see cref="Debate.DebateStateReplayer"/> for a debate. The rules of the
/// milestone are the only ones: a log that breaks one is a finding, never a crash.
/// </summary>
public static class MilestoneStateReplayer
{
    public static MilestoneReplay Replay(MilestoneHeader header, IEnumerable<MilestoneEvent> events)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(events);

        Milestone milestone;
        try
        {
            milestone = Milestone.Open(header);
        }
        catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
        {
            return new MilestoneReplay(null, [$"The header of the milestone cannot be opened: {Describe(ex)}"]);
        }

        var position = 0;
        foreach (var milestoneEvent in events)
        {
            position++;
            string? problem;
            try
            {
                problem = Apply(milestone, milestoneEvent);
            }
            catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
            {
                problem = Describe(ex);
            }

            if (problem is not null)
            {
                return new MilestoneReplay(milestone, [$"Event {position}: {problem}"]);
            }
        }

        return new MilestoneReplay(milestone, []);
    }

    private static string? Apply(Milestone milestone, MilestoneEvent? milestoneEvent)
    {
        switch (milestoneEvent)
        {
            case RequirementsRevised revised:
                milestone.ReviseRequirements(revised.RequirementSetHash, revised.At);
                return null;
            case TargetLinked linked:
                milestone.Link(linked.Target, linked.At);
                return null;
            case ClosureRecorded closed:
                milestone.Close(closed.Closure);
                return null;
            case ReopeningRecorded reopened:
                milestone.Reopen(reopened.Reopening);
                return null;
            case null:
                return "The event is missing.";
            default:
                return $"Unknown kind of event: {milestoneEvent.GetType().Name}.";
        }
    }

    private static string Describe(Exception ex) => ex is DomainRuleViolationException { Rule.Length: > 0 } rule ? $"{rule.Rule}: {rule.Message}" : ex.Message;
}
