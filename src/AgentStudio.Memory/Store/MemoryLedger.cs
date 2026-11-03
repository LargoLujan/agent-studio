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
/// announced and with what, how each was reviewed, where the last history accepted as a new origin begins, and which
/// integrity alerts of the memory were raised already. Every state of an entry is derived from here; a row only says what
/// it holds.
/// </summary>
internal sealed class MemoryLedger
{
    private readonly Dictionary<string, MemoryAnnouncement> _announcements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(long Sequence, MemoryReviewedPayload Review)>> _reviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MemoryAnnouncement>> _superseders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _alerts = new(StringComparer.Ordinal);
    private long? _lastOriginAcceptance;

    private MemoryLedger()
    {
    }

    /// <summary>
    /// Reads the memory entries of <paramref name="entries"/>, a log that verified. Only entries outside every run count
    /// (<see cref="DecisionPayload.NoRun"/>); the first announcement of an id is the one that holds.
    /// </summary>
    public static MemoryLedger From(IReadOnlyList<DecisionEntry> entries)
    {
        var ledger = new MemoryLedger();
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
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

        return ledger;
    }

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
    /// retract, never accept.
    /// </summary>
    public MemoryTrust TrustOf(MemoryAnnouncement announcement)
    {
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

    /// <summary>The announcements that declare they supersede <paramref name="entry"/>, in the order of the log.</summary>
    public IReadOnlyList<MemoryAnnouncement> SupersedersOf(string entry) => _superseders.GetValueOrDefault(entry) ?? [];

    private bool Inherited(long sequence) => _lastOriginAcceptance is { } accepted && sequence < accepted;

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
