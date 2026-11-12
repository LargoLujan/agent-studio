using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// Reads where the challenge of a verdict stands (docs/15 D-38 to D-40) from the verified log, for the board. The rule is <see cref="ChallengeRules"/> (in Application, so the
/// Debate module, which decides whether a person may challenge again or accept a revision, calls the very same one); the delivery (<c>DeliveryService.Revisions</c>) reads the log
/// with its own code and <c>RevisionAgreementTests</c> runs the three over the same scenarios.
/// </summary>
public static class ChallengeReader
{
    /// <summary>The action of a person's acceptance of a revision that turned towards an approval: the value of <c>DeliveryService.RevisionAcceptanceAction</c>.</summary>
    public const string RevisionAcceptanceAction = ChallengeRules.RevisionAcceptanceAction;

    /// <summary>The action of the question that asks a person to open a revision debate: the value of <c>DeliveryService.DebateOpenAction</c>.</summary>
    public const string DebateOpenAction = ChallengeRules.DebateOpenAction;

    /// <param name="log">The verified log.</param>
    /// <param name="challenged">The verdict as the store holds it.</param>
    /// <param name="stored">The verdicts of the store by id, to check those of the revision.</param>
    public static ChallengeReading Read(IReadOnlyList<DecisionEntry> log, StoredVerdict challenged, IReadOnlyDictionary<string, StoredVerdict> stored) =>
        ChallengeRules.Read(log, challenged, stored);

    /// <summary>The same over the log already deserialized (<see cref="WorkLog.Parse"/>), so a caller that reads many verdicts deserializes it once.</summary>
    public static ChallengeReading Read(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, StoredVerdict challenged, IReadOnlyDictionary<string, StoredVerdict> stored) =>
        ChallengeRules.Read(items, challenged, stored);

    /// <summary>The sequence of the log entry that first challenged the verdict with a revision that was not declined, or null when none did: what the verdicts of a revision must come after.</summary>
    internal static long? ChallengedAt(IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> items, string verdict) =>
        ChallengeRules.ChallengedAt(items, verdict);
}
