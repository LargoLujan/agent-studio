using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Memory.Store;

namespace AgentStudio.Memory.Derivation;

/// <summary>
/// One launch of an agent as a result line of the journal names it (docs/17 D-02, addendum of 2026-11-03): the unit of the
/// log, the task and the attempt of its <c>AgentRunStarted</c>, and its entry with its day. The planner and the judge of a run
/// share run, task (none) and attempt; their entries tell them apart.
/// </summary>
internal readonly record struct JournalLaunch(string Run, string? Task, int Attempt, JournalEntryId Entry);

/// <summary>
/// An entry of the journal as the memory keeps it: its text, its topic, who wrote it (<see cref="MemoryOrigin.System"/> only
/// for what Agent Studio wrote and its receipt vouches for) and the earlier version of it the memory holds, which it
/// supersedes. <see cref="Known"/> when the memory already holds this very text with this origin, or as Agent Studio's.
/// </summary>
internal sealed record JournalFact(JournalEntryText Entry, string Source, string Topic, MemoryOrigin Origin, string? Supersedes, bool Known);

/// <summary>
/// Which entries of the journal the memory takes, and with what origin (docs/17 D-02; ADR-0047 and its addendum of
/// 2026-11-03). An entry is <see cref="MemoryOrigin.System"/>, and so trusted, only when all of this holds:
/// <list type="bullet">
/// <item>it is an entry of the product (<see cref="JournalWriter.Product"/>): its line and exactly one result line, which names
/// it (<c>· entrada `&lt;its day&gt;/P-NNNN`</c>) at the end of its anchor;</item>
/// <item>a run of its own of the log (<see cref="DerivationLog.OwnRuns"/>, never one inherited from a history accepted as a new
/// origin) has the <c>AgentRunFinished</c> of that run, task and attempt with the receipt of that entry: its day, its number
/// and the SHA-256 of its exact text (<see cref="JournalReceipt"/>). So copying the end of a line onto another entry, or
/// changing an entry, gives another hash; and a start of before Phase 12, which has no receipt, is never claimed;</item>
/// <item>no other entry names the same launch: neither one the memory holds already nor another one of the files now. If
/// two do, neither is <see cref="MemoryOrigin.System"/>.</item>
/// </list>
/// Anything else is <see cref="MemoryOrigin.Agent"/>, «text Agent Studio did not write»: the entries of the hook, the notes
/// under an entry of the product, and any entry that does not match. Its trust is the one of the journal (by day or for
/// the whole project) or a review of its own (<see cref="MemoryLedger.JournalTrustOf"/>).
/// <para>
/// What the memory holds is known by its origin and its source: an entry with this very text is not taken again with the
/// same origin, nor at all once it is Agent Studio's. An entry anchored as <see cref="MemoryOrigin.Agent"/> because its
/// receipt was not in the log yet (the launch ended between the two reads) is taken again as
/// <see cref="MemoryOrigin.System"/> once it is, and supersedes the first. One whose text changed is another entry, which
/// never changes the one anchored. The new version of an entry (a «Hecho» line added under an entry of the hook, a result
/// written after the derivation saw its entry) supersedes the last one the memory holds with its topic, unless that one is
/// <see cref="MemoryOrigin.System"/> and the new one is not.
/// </para>
/// </summary>
internal static class JournalFacts
{
    /// <param name="entries">The entries of the journal as its files hold them now.</param>
    /// <param name="log">The verified log, read for the derivation.</param>
    /// <param name="ledger">What the same log says about the memory.</param>
    /// <param name="rows">Every row of the memory of the project.</param>
    /// <returns>Newest day first, and each day in the order of its lines.</returns>
    public static IReadOnlyList<JournalFact> Of(IReadOnlyList<JournalEntryText> entries, DerivationLog log, MemoryLedger ledger, IReadOnlyList<MemoryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(rows);

        // What the memory holds of the journal: the sources announced with a row (taken, whatever became of them) and the
        // rows that are still what the log announced (the launches they name, and the versions to supersede).
        var journalRows = rows.Where(r => r.Kind == MemoryKind.JournalEntry).ToList();
        var withRow = journalRows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var known = ledger.Announcements
            .Where(a => a.Payload.Kind == MemoryKind.JournalEntry && withRow.Contains(a.Payload.Entry))
            .Select(a => (a.Payload.Origin, a.Payload.Source))
            .ToHashSet();
        var intact = journalRows
            .Where(r => ledger.Check(r) is null)
            .Select(r => (Row: r, ledger.Announcement(r.Id)!.Sequence))
            .OrderBy(r => r.Sequence)
            .ToList();

        var ordered = entries
            .Select(e => (Entry: e, e.Source))
            .DistinctBy(e => e.Source, StringComparer.Ordinal)
            .OrderByDescending(e => e.Entry.Day)
            .ThenBy(e => e.Entry.Line)
            .ToList();

        // Every launch named by an entry, and by which sources: the memory's and the files' new ones.
        var named = new Dictionary<JournalLaunch, HashSet<string>>();
        foreach (var (row, _) in intact)
        {
            Name(named, Launches(JournalEntries.Lines(row.Content)), row.Source);
        }

        foreach (var (entry, source) in ordered.Where(e => !known.Contains((MemoryOrigin.System, e.Source))))
        {
            Name(named, Launches(entry.Lines), source);
        }

        var receipts = Receipts(log);
        var facts = new List<JournalFact>(ordered.Count);
        foreach (var (entry, source) in ordered)
        {
            var topic = DerivationTexts.JournalTopic(entry.Day, entry.Key);
            var origin = known.Contains((MemoryOrigin.System, source)) || Matches(entry, receipts, named) ? MemoryOrigin.System : MemoryOrigin.Agent;
            if (known.Contains((origin, source)))
            {
                facts.Add(new JournalFact(entry, source, topic, origin, null, Known: true));
                continue;
            }

            var previous = intact.LastOrDefault(r => string.Equals(r.Row.Topic, topic, StringComparison.Ordinal)).Row;
            var supersedes = previous is not null && (previous.Origin != MemoryOrigin.System || origin == MemoryOrigin.System) ? previous.Id : null;
            facts.Add(new JournalFact(entry, source, topic, origin, supersedes, Known: false));
        }

        return facts;
    }

    /// <summary>The launches the result lines among <paramref name="lines"/> name, with their entry (lines of before the addendum name none).</summary>
    public static IEnumerable<JournalLaunch> Launches(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (JournalEntries.ResultSequence(line) > 0
                && JournalResultAnchor.TryParse(line, out var anchor, out var entry)
                && entry is { } named)
            {
                yield return new JournalLaunch(anchor.Run, anchor.Task, anchor.Attempt, named);
            }
        }
    }

    /// <summary>Whether <paramref name="entry"/> is what Agent Studio wrote for one launch, and the only entry that names it.</summary>
    private static bool Matches(JournalEntryText entry, ILookup<(string Run, string? Task, int Attempt), JournalReceipt> receipts, Dictionary<JournalLaunch, HashSet<string>> named)
    {
        if (entry is not { Writer: JournalWriter.Product, Results: [var result] }
            || !JournalResultAnchor.TryParse(result, out var anchor, out var owner)
            || owner is not { } own
            || own != new JournalEntryId(entry.Day, entry.Sequence))
        {
            return false;
        }

        var launch = new JournalLaunch(anchor.Run, anchor.Task, anchor.Attempt, own);
        if (!named.TryGetValue(launch, out var sources) || sources.Count != 1)
        {
            return false;
        }

        var sha = entry.Sha256;
        return receipts[(anchor.Run, anchor.Task, anchor.Attempt)]
            .Any(r => r.Day == entry.Day && r.Entry == entry.Sequence && string.Equals(r.Sha256, sha, StringComparison.Ordinal));
    }

    /// <summary>The receipts of the journal the runs of the project's own log keep in their <c>AgentRunFinished</c>, by run, task and attempt.</summary>
    private static ILookup<(string Run, string? Task, int Attempt), JournalReceipt> Receipts(DerivationLog log) =>
        log.OwnRuns
            .SelectMany(r => r.Items.Select(i => (r.Run, i.Payload)))
            .Where(i => i.Payload is AgentRunFinishedPayload { Journal: not null })
            .Select(i => (i.Run, Finished: (AgentRunFinishedPayload)i.Payload))
            .ToLookup(i => (Run: i.Run, Task: i.Finished.Task?.Value, Attempt: i.Finished.Attempt), i => i.Finished.Journal!);

    private static void Name(Dictionary<JournalLaunch, HashSet<string>> named, IEnumerable<JournalLaunch> launches, string source)
    {
        foreach (var launch in launches)
        {
            if (!named.TryGetValue(launch, out var sources))
            {
                named.Add(launch, sources = new HashSet<string>(StringComparer.Ordinal));
            }

            sources.Add(source);
        }
    }
}
