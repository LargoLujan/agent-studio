using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Work.Backlog;

/// <summary>A fact of the backlog read back from the log (<c>BacklogItemRecorded</c>, <c>BacklogItemRetired</c>, <c>BacklogOrderChanged</c>).</summary>
public abstract record BacklogEvent(DateTimeOffset At);

/// <summary>An item was added, or a new version of it was written (<c>BacklogItemRecorded</c>, with its row).</summary>
public sealed record ItemRecorded(BacklogItem Item) : BacklogEvent(Item.CreatedAt);

/// <summary>An item was retired after the given version (<c>BacklogItemRetired</c>).</summary>
public sealed record ItemRetired(DateTimeOffset At, BacklogItemId Item, int Version) : BacklogEvent(At);

/// <summary>The person put the items in a new order (<c>BacklogOrderChanged</c>).</summary>
public sealed record OrderChanged(DateTimeOffset At, IReadOnlyList<BacklogItemId> Items) : BacklogEvent(At);

/// <summary>
/// The backlog of a project (docs/16 D-18, F9-2), rebuilt from its log: the requirements in draft the person noted for later.
/// Rows are never edited or deleted: an item is added (version 1), changed (a version more) or retired, and the order is a
/// decision of the person. This is the state those facts leave, and the only way to build it.
/// </summary>
/// <remarks>
/// <para>The rules:</para>
/// <list type="bullet">
/// <item>A version is valid (<see cref="BacklogItem.Validate"/>), is the first or exactly the next of its item, and comes
/// after the last thing that happened to the backlog in time.</item>
/// <item>A retired item takes no new version and is never retired twice; retiring names the last version it has.</item>
/// <item>The order lists items that exist and are not retired, each once. Items it does not list follow, in the order they
/// were first added; the items it lists come first, in its order. A later change of the order replaces the earlier one.</item>
/// </list>
/// </remarks>
public sealed class BacklogState
{
    private readonly Dictionary<BacklogItemId, List<BacklogItem>> _versions = [];
    private readonly List<BacklogItemId> _firstAdded = [];
    private readonly HashSet<BacklogItemId> _retired = [];
    private List<BacklogItemId> _order = [];
    private DateTimeOffset _lastAt = DateTimeOffset.MinValue;

    /// <summary>The items in force (the last version of each, without the retired ones) in the order of the backlog.</summary>
    public IReadOnlyList<BacklogItem> Items
    {
        get
        {
            var listed = _order.Where(id => !_retired.Contains(id)).ToList();
            var rest = _firstAdded.Where(id => !_retired.Contains(id) && !listed.Contains(id));
            return [.. listed.Concat(rest).Select(id => _versions[id][^1])];
        }
    }

    /// <summary>Every version an item has had, oldest first, retired or not; empty for an id the backlog never saw.</summary>
    public IReadOnlyList<BacklogItem> History(BacklogItemId id) => _versions.TryGetValue(id, out var versions) ? versions : [];

    public bool IsRetired(BacklogItemId id) => _retired.Contains(id);

    /// <summary>Adds an item (version 1) or writes its next version.</summary>
    /// <exception cref="DomainRuleViolationException">The version is not valid or not the next, the item is retired, or time went back.</exception>
    public void Record(BacklogItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var problems = item.Validate();
        if (problems.Count > 0)
        {
            throw new DomainRuleViolationException(problems[0], $"The version {item.Version} of '{item.Id}' is not valid: {string.Join(", ", problems)}.");
        }

        RequireForward(item.CreatedAt);
        if (_retired.Contains(item.Id))
        {
            throw new DomainRuleViolationException("backlog.retired", $"'{item.Id}' was retired: it takes no new version.");
        }

        var last = _versions.TryGetValue(item.Id, out var versions) ? versions[^1].Version : 0;
        if (item.Version != last + 1)
        {
            throw new DomainRuleViolationException("backlog.version-order", $"The version of '{item.Id}' is {item.Version}, but the next one is {last + 1}.");
        }

        if (versions is null)
        {
            _versions[item.Id] = versions = [];
            _firstAdded.Add(item.Id);
        }

        versions.Add(item);
        _lastAt = item.CreatedAt;
    }

    /// <summary>Retires an item. Its rows stay, with its history.</summary>
    /// <exception cref="DomainRuleViolationException">The item does not exist, is retired already, <paramref name="version"/> is not its last one, or time went back.</exception>
    public void Retire(BacklogItemId id, int version, DateTimeOffset at)
    {
        RequireForward(at);
        if (!_versions.TryGetValue(id, out var versions))
        {
            throw new DomainRuleViolationException("backlog.unknown-item", $"'{id}' is not in the backlog.");
        }

        if (_retired.Contains(id))
        {
            throw new DomainRuleViolationException("backlog.retired", $"'{id}' is retired already.");
        }

        if (version != versions[^1].Version)
        {
            throw new DomainRuleViolationException("backlog.version-order", $"The retirement names version {version} of '{id}', but its last is {versions[^1].Version}.");
        }

        _retired.Add(id);
        _lastAt = at;
    }

    /// <summary>Puts the items in a new order. Items it does not list follow, in the order they were added.</summary>
    /// <exception cref="DomainRuleViolationException">An item is repeated, unknown or retired, or time went back.</exception>
    public void Reorder(IReadOnlyList<BacklogItemId> items, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(items);
        RequireForward(at);
        var seen = new HashSet<BacklogItemId>();
        foreach (var id in items)
        {
            if (!_versions.ContainsKey(id) || _retired.Contains(id))
            {
                throw new DomainRuleViolationException("backlog.order-unknown", $"'{id}' is not an item in force: the order lists items that exist and are not retired.");
            }

            if (!seen.Add(id))
            {
                throw new DomainRuleViolationException("backlog.order-duplicate", $"'{id}' is listed twice in the order.");
            }
        }

        _order = [.. items];
        _lastAt = at;
    }

    private void RequireForward(DateTimeOffset at)
    {
        if (at < _lastAt)
        {
            throw new DomainRuleViolationException("backlog.time-order", "The events of the backlog only go forward in time.");
        }
    }
}

/// <summary>
/// The backlog rebuilt from its log. A replay with <paramref name="Problems"/> describes a backlog the domain does not allow
/// (a version out of order, an item retired twice…): the backlog is the state just before the first one.
/// </summary>
public sealed record BacklogReplay(BacklogState Backlog, IReadOnlyList<string> Problems)
{
    public bool IsConsistent => Problems.Count == 0;
}

/// <summary>Rebuilds the backlog by applying its events, in order, through the API of <see cref="BacklogState"/> and nothing else.</summary>
public static class BacklogReplayer
{
    public static BacklogReplay Replay(IEnumerable<BacklogEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var backlog = new BacklogState();
        var position = 0;
        foreach (var backlogEvent in events)
        {
            position++;
            string? problem;
            try
            {
                problem = Apply(backlog, backlogEvent);
            }
            catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
            {
                problem = ex is DomainRuleViolationException { Rule.Length: > 0 } rule ? $"{rule.Rule}: {rule.Message}" : ex.Message;
            }

            if (problem is not null)
            {
                return new BacklogReplay(backlog, [$"Event {position}: {problem}"]);
            }
        }

        return new BacklogReplay(backlog, []);
    }

    private static string? Apply(BacklogState backlog, BacklogEvent? backlogEvent)
    {
        switch (backlogEvent)
        {
            case ItemRecorded recorded:
                backlog.Record(recorded.Item);
                return null;
            case ItemRetired retired:
                backlog.Retire(retired.Item, retired.Version, retired.At);
                return null;
            case OrderChanged changed:
                backlog.Reorder(changed.Items, changed.At);
                return null;
            case null:
                return "The event is missing.";
            default:
                return $"Unknown kind of event: {backlogEvent.GetType().Name}.";
        }
    }
}
