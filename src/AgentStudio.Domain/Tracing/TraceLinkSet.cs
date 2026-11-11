using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Tracing;

/// <summary>A fact of the trace read back from the log (<c>TraceLinkRecorded</c>, <c>TraceLinkRetracted</c>).</summary>
public abstract record TraceEvent(DateTimeOffset At);

/// <summary>A link was recorded (<c>TraceLinkRecorded</c>).</summary>
public sealed record LinkRecorded(TraceLink Link) : TraceEvent(Link.At);

/// <summary>A link was withdrawn (<c>TraceLinkRetracted</c>): by its id, by whom and why.</summary>
public sealed record LinkRetracted(DateTimeOffset At, string LinkId, string RetractedBy, string Reason) : TraceEvent(At);

/// <summary>A link that was withdrawn, with who withdrew it and why. It stays in the log and in the history: nothing is erased.</summary>
public sealed record RetractedLink(TraceLink Link, DateTimeOffset At, string RetractedBy, string Reason);

/// <summary>
/// The explicit links of a project (docs/16 D-29, F9-3), rebuilt from the log: many to many, append-only, and read in both
/// directions. A link is never edited: it is withdrawn and another is recorded. Recording a link that is in force is a no-op,
/// because two equal observations are one; recording one that was withdrawn puts it back in force, and the withdrawal stays in
/// <see cref="Retracted"/>.
/// </summary>
public sealed class TraceLinkSet
{
    private readonly Dictionary<string, TraceLink> _active = [];
    private readonly List<string> _order = [];
    private readonly List<RetractedLink> _retracted = [];
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    /// <summary>The links in force, in the order they were recorded.</summary>
    public IReadOnlyList<TraceLink> Active => [.. _order.Where(_active.ContainsKey).Select(id => _active[id])];

    /// <summary>Every withdrawal, oldest first.</summary>
    public IReadOnlyList<RetractedLink> Retracted => _retracted;

    /// <summary>The link in force with this id, or null.</summary>
    public TraceLink? Find(string linkId) => linkId is not null && _active.TryGetValue(linkId, out var link) ? link : null;

    /// <summary>The links in force that start at <paramref name="node"/>.</summary>
    public IReadOnlyList<TraceLink> From(TraceRef node) => [.. Active.Where(l => l.From == node)];

    /// <summary>The links in force that end at <paramref name="node"/>.</summary>
    public IReadOnlyList<TraceLink> To(TraceRef node) => [.. Active.Where(l => l.To == node)];

    /// <summary>The links in force that touch <paramref name="node"/> at either end: the relation is read in both directions.</summary>
    public IReadOnlyList<TraceLink> Around(TraceRef node) => [.. Active.Where(l => l.From == node || l.To == node)];

    /// <summary>The nodes linked to <paramref name="node"/> by a link in force, in either direction, each once, in the order of the links.</summary>
    public IReadOnlyList<TraceRef> Neighbours(TraceRef node) =>
        [.. Around(node).Select(l => l.From == node ? l.To : l.From).Distinct()];

    /// <summary>Records a link. It returns false when the same link is in force already and nothing changes.</summary>
    /// <exception cref="DomainRuleViolationException">The link does not validate, or time went back.</exception>
    public bool Record(TraceLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var problems = link.Validate();
        if (problems.Count > 0)
        {
            throw new DomainRuleViolationException(problems[0], $"The link is not valid: {string.Join(", ", problems)}.");
        }

        RequireForward(link.At);
        _lastAt = link.At;
        var id = link.Id;
        if (_active.ContainsKey(id))
        {
            return false;
        }

        _active[id] = link;
        _order.Remove(id);
        _order.Add(id);
        return true;
    }

    /// <summary>Withdraws the link in force with this id.</summary>
    /// <exception cref="DomainRuleViolationException">No link in force has that id, nobody retracts it, or time went back.</exception>
    public void Retract(string linkId, DateTimeOffset at, string retractedBy, string reason)
    {
        RequireForward(at);
        if (!_active.TryGetValue(linkId ?? string.Empty, out var link))
        {
            throw new DomainRuleViolationException("trace.link-not-active", $"No link in force has the id '{linkId}'.");
        }

        if (string.IsNullOrWhiteSpace(retractedBy) || string.IsNullOrWhiteSpace(reason) || reason.Length > TraceLink.MaxReasonLength)
        {
            throw new DomainRuleViolationException("trace.retraction", "A link is withdrawn by someone and with a reason.");
        }

        _active.Remove(linkId!);
        _retracted.Add(new RetractedLink(link, at, retractedBy, reason));
        _lastAt = at;
    }

    private void RequireForward(DateTimeOffset at)
    {
        if (at < _lastAt)
        {
            throw new DomainRuleViolationException("trace.time-order", "The events of the trace only go forward in time.");
        }
    }
}

/// <summary>
/// The links rebuilt from the log. A replay with <paramref name="Problems"/> describes a trace the domain does not allow (a
/// retraction of a link that was never in force…): the set is the state just before the first one.
/// </summary>
public sealed record TraceReplay(TraceLinkSet Links, IReadOnlyList<string> Problems)
{
    public bool IsConsistent => Problems.Count == 0;
}

/// <summary>Rebuilds the links of a project by applying its events, in order, through the API of <see cref="TraceLinkSet"/> and nothing else.</summary>
public static class TraceLinkReplayer
{
    public static TraceReplay Replay(IEnumerable<TraceEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var links = new TraceLinkSet();
        var position = 0;
        foreach (var traceEvent in events)
        {
            position++;
            string? problem;
            try
            {
                problem = Apply(links, traceEvent);
            }
            catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
            {
                problem = ex is DomainRuleViolationException { Rule.Length: > 0 } rule ? $"{rule.Rule}: {rule.Message}" : ex.Message;
            }

            if (problem is not null)
            {
                return new TraceReplay(links, [$"Event {position}: {problem}"]);
            }
        }

        return new TraceReplay(links, []);
    }

    private static string? Apply(TraceLinkSet links, TraceEvent? traceEvent)
    {
        switch (traceEvent)
        {
            case LinkRecorded recorded:
                links.Record(recorded.Link);
                return null;
            case LinkRetracted retracted:
                links.Retract(retracted.LinkId, retracted.At, retracted.RetractedBy, retracted.Reason);
                return null;
            case null:
                return "The event is missing.";
            default:
                return $"Unknown kind of event: {traceEvent.GetType().Name}.";
        }
    }
}
