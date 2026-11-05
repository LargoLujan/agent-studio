using AgentStudio.Domain.Decisions;

namespace AgentStudio.Application.Persistence;

/// <summary>
/// A decision log store that can read only what was appended after the entries a writer already verified, so the writer
/// verifies a log as it grows instead of reading and verifying all of it on every load (<c>DecisionRecorder</c>; the
/// recording of memory was quadratic without it). In one consistent read it gives the stamp of the store, whether the
/// entries up to the mark are still the ones marked, and the entries after it. A store without it is read whole.
/// </summary>
public interface IDecisionLogTailReader
{
    /// <param name="after">What the writer verified (how many entries, the hash of the last one, and the stamp it read them with); null reads every entry.</param>
    Task<DecisionLogTail> ReadTailAsync(string projectId, DecisionLogMark? after, CancellationToken cancellationToken);
}

/// <summary>What a writer verified of a log: its first <paramref name="EntryCount"/> entries, whose last has <paramref name="HeadHash"/>, read with the store at <paramref name="Stamp"/>.</summary>
public sealed record DecisionLogMark(long EntryCount, string HeadHash, string Stamp);

/// <summary>What a store holds after a mark, read at once.</summary>
/// <param name="Stamp">
/// What identifies the state of the store as a whole beyond its entries: when it differs from the mark's, the entries up to
/// the mark may have changed without the append-only checks of the store seeing it (its schema changed, so a trigger that
/// keeps the log append-only was dropped and perhaps restored; the database is another file; the project was opened again),
/// and only reading and verifying the whole log can tell. The same stamp is no proof of the contrary: a schema version set
/// back, a file replaced with its creation time kept, bytes written in place, or a row replaced through its rowid by a
/// connection without recursive triggers leave it as it was, so the writer verifies the whole log from time to time and
/// at the critical moments.
/// </param>
/// <param name="Continues">
/// Whether the store still holds the entries up to the mark as marked: as many of them, the last with the marked hash, and
/// the same stamp. Always true without a mark.
/// </param>
/// <param name="Entries">The entries after the mark (every entry without one), in order; none when it does not continue.</param>
public sealed record DecisionLogTail(string Stamp, bool Continues, IReadOnlyList<DecisionEntry> Entries);
