using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Work.Milestones;

namespace AgentStudio.Application.Work;

/// <summary>A run or a debate linked to a milestone (<c>MilestoneLinked</c>): the milestone, the kind of target and its id.</summary>
public readonly record struct MilestoneLink(string Milestone, MilestoneTargetKind Kind, string Target);

/// <summary>
/// Which milestones a run or a debate belongs to (docs/16 D-17 and D-34), worked out from the log alone: the links of
/// <c>MilestoneLinked</c> and, for a run that follows another (<c>RunStarted.Follows</c>), the milestones of the run it follows, at any depth.
/// The consumption by milestone (<c>UsageProjector</c>) and the budget of a milestone (the engine) read it the same way, so both
/// count the same attempts. Pure: no clock, no store.
/// </summary>
public static class MilestoneMembership
{
    /// <summary>
    /// The milestones of each run or debate that is in at least one, in ordinal order, by the id of the run or debate. A follow-up chain that
    /// loops (a log written by hand) is read once and never loops.
    /// </summary>
    /// <param name="links">The links, in any order.</param>
    /// <param name="follows">Each run that follows another, by its id: <c>(run, the run it follows)</c>.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Resolve(IEnumerable<MilestoneLink> links, IEnumerable<(string Run, string Follows)> follows)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(follows);

        var direct = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (!direct.TryGetValue(link.Target, out var set))
            {
                direct[link.Target] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.Add(link.Milestone);
        }

        var predecessor = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (run, followed) in follows)
        {
            predecessor[run] = followed;
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var run in direct.Keys.Concat(predecessor.Keys).Distinct(StringComparer.Ordinal))
        {
            var milestones = new SortedSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = run; current is not null && seen.Add(current); current = predecessor.GetValueOrDefault(current))
            {
                if (direct.TryGetValue(current, out var own))
                {
                    milestones.UnionWith(own);
                }
            }

            if (milestones.Count > 0)
            {
                result[run] = [.. milestones];
            }
        }

        return result;
    }

    /// <summary>
    /// The same from the entries of a verified log: <c>MilestoneLinked</c> (the milestone is the run id of the entry) and
    /// <c>RunStarted.Follows</c>. An entry that cannot be read is left out: the caller verified the log, and a payload it cannot read
    /// links nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Of(IEnumerable<DecisionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var links = new List<MilestoneLink>();
        var follows = new List<(string, string)>();
        foreach (var entry in entries)
        {
            try
            {
                switch (entry.Kind)
                {
                    case DecisionKind.MilestoneLinked:
                        var linked = DecisionPayloadSerializer.Deserialize<MilestoneLinkedPayload>(entry.PayloadJson);
                        links.Add(new MilestoneLink(linked.RunId, linked.Kind, linked.Target));
                        break;
                    case DecisionKind.RunStarted:
                        var started = DecisionPayloadSerializer.Deserialize<RunStartedPayload>(entry.PayloadJson);
                        if (!string.IsNullOrEmpty(started.Follows))
                        {
                            follows.Add((started.RunId, started.Follows));
                        }

                        break;
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidCastException)
            {
                // unreadable: it links nothing
            }
        }

        return Resolve(links, follows);
    }
}
