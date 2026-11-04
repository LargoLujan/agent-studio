using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Memory.Store;

/// <summary>A <c>MemoryRecorded</c> of the log: where it is (its sequence and time) and what it announced.</summary>
internal sealed record MemoryAnnouncement(long Sequence, DateTimeOffset At, MemoryRecordedPayload Payload);

/// <summary>A row the log does not vouch for, with the alert it raises: its code and a message that names it.</summary>
internal sealed record MemoryIntegrityProblem(string Code, string Entry, string Message);

/// <summary>
/// What a verified decision log says about the memory of a project (docs/14 D-19 and D-20; ADR-0022): which entries were
/// announced and with what, how each was reviewed, where the last history accepted as a new origin begins, which
/// integrity alerts of the memory were raised already, and how far the journal of the project is trusted (docs/17 D-02,
/// ADR-0047: <c>JournalTrustChanged</c>, by project and by day). Every state of an entry is derived from here; a row only
/// says what it holds. A ledger never changes once built: <see cref="Extend"/> gives a new one with what the log grew, so
/// the memory reads its log once and then only what is appended to it (<see cref="ProjectMemory"/>).
/// </summary>
internal sealed class MemoryLedger
{
    private readonly Dictionary<string, MemoryAnnouncement> _announcements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(long Sequence, MemoryReviewedPayload Review)>> _reviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MemoryAnnouncement>> _superseders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _alerts = new(StringComparer.Ordinal);
    private readonly List<(long Sequence, JournalTrustChangedPayload Decision)> _journalDecisions = [];
    private readonly Dictionary<DateOnly, bool> _journalDays = [];
    private long? _lastOriginAcceptance;

    private MemoryLedger()
    {
    }

    /// <summary>A copy of <paramref name="other"/> that the copy can grow without changing it.</summary>
    private MemoryLedger(MemoryLedger other)
    {
        _announcements = new Dictionary<string, MemoryAnnouncement>(other._announcements, StringComparer.Ordinal);
        _reviews = other._reviews.ToDictionary(r => r.Key, r => new List<(long Sequence, MemoryReviewedPayload Review)>(r.Value), StringComparer.Ordinal);
        _superseders = other._superseders.ToDictionary(s => s.Key, s => new List<MemoryAnnouncement>(s.Value), StringComparer.Ordinal);
        _alerts = new HashSet<string>(other._alerts, StringComparer.Ordinal);
        _journalDecisions = [.. other._journalDecisions];
        _lastOriginAcceptance = other._lastOriginAcceptance;
    }

    /// <summary>
    /// Whether the whole journal of the project is trusted now («Confiar en la bitácora de este proyecto»): what the last
    /// decision about it says, of those made after the last history accepted as a new origin. A decision that came with an
    /// inherited history never counts (an <c>agentstudio.db</c> planted with the trust of its journal granted), as an
    /// inherited review never accepts.
    /// </summary>
    public bool JournalTrusted { get; private set; }

    /// <summary>The days of the journal trusted by a decision of their own (not by the whole project), in order.</summary>
    public IReadOnlyList<DateOnly> TrustedJournalDays => [.. _journalDays.Where(d => d.Value).Select(d => d.Key).Order()];

    /// <summary>
    /// Reads the memory entries of <paramref name="entries"/>, a log that verified. Only entries outside every run count
    /// (<see cref="DecisionPayload.NoRun"/>); the first announcement of an id is the one that holds.
    /// </summary>
    public static MemoryLedger From(IReadOnlyList<DecisionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ledger = new MemoryLedger();
        ledger.Read(entries, 0);
        return ledger;
    }

    /// <summary>
    /// A new ledger: this one with the entries of <paramref name="entries"/> from index <paramref name="from"/> on, which
    /// must be the log this ledger was read from followed by what was appended to it (the caller checks it by the hash of
    /// the last entry it read: the chain makes the rest the same). This ledger does not change.
    /// </summary>
    public MemoryLedger Extend(IReadOnlyList<DecisionEntry> entries, int from)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        var ledger = new MemoryLedger(this);
        ledger.Read(entries, from);
        return ledger;
    }

    /// <summary>Takes in the memory entries of <paramref name="entries"/> from <paramref name="from"/> on, then settles the trust of the journal.</summary>
    private void Read(IReadOnlyList<DecisionEntry> entries, int from)
    {
        var ledger = this;
        for (var i = from; i < entries.Count; i++)
        {
            var entry = entries[i];
            switch (entry.Kind)
            {
                case DecisionKind.JournalTrustChanged when Read<JournalTrustChangedPayload>(entry) is { RunId: DecisionPayload.NoRun } decision:
                    ledger._journalDecisions.Add((entry.Sequence, decision));
                    break;
                case DecisionKind.MemoryRecorded when Read<MemoryRecordedPayload>(entry) is { RunId: DecisionPayload.NoRun } announced
                    && !ledger._announcements.ContainsKey(announced.Entry):
                    var announcement = new MemoryAnnouncement(entry.Sequence, entry.RecordedAt, announced);
                    ledger._announcements.Add(announced.Entry, announcement);
                    if (announced.Supersedes is { } superseded)
                    {
                        ListOf(ledger._superseders, superseded).Add(announcement);
                    }

                    break;
                case DecisionKind.MemoryReviewed when Read<MemoryReviewedPayload>(entry) is { RunId: DecisionPayload.NoRun } review:
                    ListOf(ledger._reviews, review.Entry).Add((entry.Sequence, review));
                    break;
                case DecisionKind.AuditAlertRaised when Read<AuditAlertPayload>(entry) is { } alert:
                    if (string.Equals(alert.Code, DecisionRecorder.OriginAcceptedAlert, StringComparison.Ordinal))
                    {
                        ledger._lastOriginAcceptance = entry.Sequence;
                    }
                    else if (alert.Code is ProjectMemory.NotAnchoredAlert or ProjectMemory.ModifiedAlert)
                    {
                        ledger._alerts.Add(alert.Message);
                    }

                    break;
            }
        }

        ledger.SettleJournalTrust();
    }

    /// <summary>
    /// The day of the journal a source of <see cref="MemoryKind.JournalEntry"/> cites
    /// (<c>bitacora:&lt;yyyy-MM-dd&gt;#sha256:&lt;hash&gt;</c>, <see cref="MemorySources.Journal"/>), or null when it is not one.
    /// </summary>
    public static DateOnly? JournalDayOf(string? source) => MemorySources.TryParseJournal(source, out var day, out _) ? day : null;

    /// <summary>What the last decision about <paramref name="day"/> of the journal itself says (the project aside), or null when there is none.</summary>
    public bool? JournalDayDecision(DateOnly day) => _journalDays.TryGetValue(day, out var trusted) ? trusted : null;

    /// <summary>
    /// Whether a journal entry Agent Studio did not write, of <paramref name="day"/>, is covered by a decision about the
    /// journal: the decision about its day when there is one, otherwise the one about the whole project (day before project).
    /// </summary>
    public bool JournalCovers(DateOnly? day) => (day is { } d ? JournalDayDecision(d) : null) ?? JournalTrusted;

    public MemoryAnnouncement? Announcement(string entry) => _announcements.GetValueOrDefault(entry);

    /// <summary>Every entry the log announced, whatever became of it, in the order of the log.</summary>
    public IReadOnlyList<MemoryAnnouncement> Announcements => [.. _announcements.Values.OrderBy(a => a.Sequence)];

    /// <summary>Whether an alert with exactly this message was raised already.</summary>
    public bool HasAlert(string message) => _alerts.Contains(message);

    /// <summary>
    /// Why <paramref name="row"/> is not what the log announced, or null when it is: it was never announced, or its
    /// category, origin, source, superseded entry, time or content (by the canonical SHA-256 it carries and the one its
    /// text gives) differ from the announcement.
    /// </summary>
    public MemoryIntegrityProblem? Check(MemoryRow row)
    {
        var holds = MemoryContent.Sha256(row.Kind, row.Topic, row.Content);
        if (!_announcements.TryGetValue(row.Id, out var announcement))
        {
            return new MemoryIntegrityProblem(
                ProjectMemory.NotAnchoredAlert,
                row.Id,
                $"Memory row '{row.Id}' (content SHA-256 {holds}) was never announced in the decision log: it is left out of recall, prompts and the export.");
        }

        var announced = announcement.Payload;
        var differs = new List<string>();
        if (row.Kind != announced.Kind)
        {
            differs.Add("category");
        }

        if (row.Origin != announced.Origin)
        {
            differs.Add("origin");
        }

        if (!string.Equals(row.Source, announced.Source, StringComparison.Ordinal))
        {
            differs.Add("source");
        }

        if (!string.Equals(row.Supersedes, announced.Supersedes, StringComparison.Ordinal))
        {
            differs.Add("superseded entry");
        }

        if (row.CreatedAt != announcement.At)
        {
            differs.Add("time");
        }

        if (!string.Equals(row.ContentSha256, announced.Sha256, StringComparison.Ordinal) || !string.Equals(holds, announced.Sha256, StringComparison.Ordinal))
        {
            differs.Add("content");
        }

        return differs.Count == 0
            ? null
            : new MemoryIntegrityProblem(
                ProjectMemory.ModifiedAlert,
                row.Id,
                $"The row of memory entry '{row.Id}' (content SHA-256 {holds}) does not match its announcement in the decision log ({string.Join(", ", differs)}): it is left out of recall, prompts and the export.");
    }

    /// <summary>
    /// The trust of an announced entry (D-20): by its origin (the system and the user are trusted, an agent proposes),
    /// in quarantine when it was announced before the last history accepted as a new origin, and then what people decided,
    /// the last review winning. A review made before that acceptance came with the inherited history: it may reject or
    /// retract, never accept. A journal entry has its own rule (<see cref="JournalTrustOf"/>).
    /// </summary>
    public MemoryTrust TrustOf(MemoryAnnouncement announcement)
    {
        if (announcement.Payload.Kind == MemoryKind.JournalEntry)
        {
            return JournalTrustOf(announcement);
        }

        var trust = Inherited(announcement.Sequence) ? MemoryTrust.Quarantined
            : announcement.Payload.Origin == MemoryOrigin.Agent ? MemoryTrust.Proposed
            : MemoryTrust.Trusted;
        foreach (var (sequence, review) in _reviews.GetValueOrDefault(announcement.Payload.Entry) ?? [])
        {
            if (Inherited(sequence) && review.Decision == MemoryReview.Accepted)
            {
                continue;
            }

            trust = review.Decision switch
            {
                MemoryReview.Accepted => MemoryTrust.Trusted,
                MemoryReview.Rejected => MemoryTrust.Rejected,
                _ => MemoryTrust.Retracted,
            };
        }

        return trust;
    }

    /// <summary>
    /// The trust of a journal entry (<see cref="MemoryKind.JournalEntry"/>, docs/17 D-02 and the addendum of 2026-11-03 to
    /// ADR-0047). Between levels the most concrete one decides, the entry before its day and its day before the project;
    /// within a level, the most recent decision, as in the rest of the memory:
    /// <list type="number">
    /// <item>the last review of the entry itself, if any (an acceptance that came with an inherited history does not count):
    /// accepted is <see cref="MemoryTrust.Trusted"/>, rejected <see cref="MemoryTrust.Rejected"/> and retracted
    /// <see cref="MemoryTrust.Retracted"/>, so an acceptance after a retraction brings the entry back;</item>
    /// <item>an entry announced before the last history accepted as a new origin is <see cref="MemoryTrust.Quarantined"/>:
    /// it is reviewed one by one, never covered by a decision about the journal (ADR-0022);</item>
    /// <item>what Agent Studio wrote and matched its receipt (<see cref="MemoryOrigin.System"/>) is trusted;</item>
    /// <item>anything else (<see cref="MemoryOrigin.Agent"/>): the last decision about its day, when there is one, and
    /// otherwise the last one about the whole project (<see cref="JournalCovers"/>); without either it is
    /// <see cref="MemoryTrust.Quarantined"/>, never a proposal.</item>
    /// </list>
    /// </summary>
    public MemoryTrust JournalTrustOf(MemoryAnnouncement announcement)
    {
        var announced = announcement.Payload;
        var reviews = _reviews.GetValueOrDefault(announced.Entry) ?? [];
        if (reviews.LastOrDefault(r => !(Inherited(r.Sequence) && r.Review.Decision == MemoryReview.Accepted)).Review is { } review)
        {
            return review.Decision switch
            {
                MemoryReview.Accepted => MemoryTrust.Trusted,
                MemoryReview.Rejected => MemoryTrust.Rejected,
                _ => MemoryTrust.Retracted,
            };
        }

        return Inherited(announcement.Sequence) ? MemoryTrust.Quarantined
            : announced.Origin != MemoryOrigin.Agent ? MemoryTrust.Trusted
            : JournalCovers(JournalDayOf(announced.Source)) ? MemoryTrust.Trusted
            : MemoryTrust.Quarantined;
    }

    /// <summary>The announcements that declare they supersede <paramref name="entry"/>, in the order of the log.</summary>
    public IReadOnlyList<MemoryAnnouncement> SupersedersOf(string entry) => _superseders.GetValueOrDefault(entry) ?? [];

    private bool Inherited(long sequence) => _lastOriginAcceptance is { } accepted && sequence < accepted;

    /// <summary>
    /// The trust of the journal once the whole log is read (the last acceptance of a history as a new origin may come after
    /// a decision): for the project and for each day, the last decision made after that acceptance. A decision recorded
    /// before it came with the inherited history and does not count.
    /// </summary>
    private void SettleJournalTrust()
    {
        JournalTrusted = false;
        _journalDays.Clear();
        foreach (var (_, decision) in _journalDecisions.Where(d => !Inherited(d.Sequence)))
        {
            if (decision.Scope == JournalTrustScope.Project)
            {
                JournalTrusted = decision.Trusted;
            }
            else if (decision is { Scope: JournalTrustScope.Day, Day: { } day })
            {
                _journalDays[day] = decision.Trusted;
            }
        }
    }

    private static List<T> ListOf<T>(Dictionary<string, List<T>> lists, string key)
    {
        if (!lists.TryGetValue(key, out var list))
        {
            lists.Add(key, list = []);
        }

        return list;
    }

    /// <summary>The payload of <paramref name="entry"/>, or null when it is not one (an entry Agent Studio did not write).</summary>
    private static T? Read<T>(DecisionEntry entry)
        where T : DecisionPayload
    {
        try
        {
            return DecisionPayloadSerializer.Deserialize<T>(entry.PayloadJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }
}
