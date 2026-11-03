using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Debate.Challenges;

/// <summary>What a verdict judged, as the verified log says: the kind of its unit (the run, debate or audit of its <c>RunId</c>).</summary>
public enum ChallengedUnitKind
{
    /// <summary>The log has no start of the unit: nothing can be inherited from it.</summary>
    Unknown,
    Run,
    Debate,
    Audit,
}

/// <summary>
/// The object of a challenged verdict (docs/15 D-38 and D-39), read from the verified log: its unit and kind, the vendors no
/// judge of the revision may run on (inherited, never with the vendor of the judge that issued the verdict), whether the
/// verdict came from a jury, and the judges that judged it.
/// </summary>
/// <param name="JudgedBy">The debate whose judgment gave the verdict (the debate itself, or a revision debate that closed with it), or null when a run's review or an audit gave it.</param>
/// <param name="Choice">Whether the object is judged like a debate that chooses (D-39: «se juzga como el objeto del veredicto que impugna»).</param>
public sealed record ChallengedObject(
    string Unit,
    ChallengedUnitKind Kind,
    IReadOnlySet<VendorId> ExcludedVendors,
    string? JudgedBy,
    DebateJudgingKind? Judging,
    IReadOnlyList<AgentId> Judges,
    bool Choice)
{
    /// <summary>Whether the verdict is one of a cross-vendor panel: its revision is judged by a panel too (D-39).</summary>
    public bool FromJury => Judging == DebateJudgingKind.CrossVendorPanel;
}

/// <summary>
/// Reads challenges from the verified log (docs/15 D-38 to D-40). Pure: the service gives it the entries and the verdicts of
/// the store.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A verdict is challenged by <c>VerdictChallenged</c> in its unit, naming its revision debate, or by a debate that
/// opened naming it (<c>DebateOpened.Challenges</c>): the challenge records the first after the second, so the second alone
/// still counts. The latest of them is the one that stands.</item>
/// <item>A revision not closed, aborted, or closed without a decision (a panel that disagrees, a verdict that is not valid)
/// leaves the challenge <see cref="ChallengeStatus.Pending"/>: it fails closed. One closed by a person («Decidir yo») is
/// <see cref="ChallengeStatus.DecidedByUser"/>.</item>
/// <item>One closed by its judge or by an agreeing panel is compared with the challenged verdict: an approval confirmed is
/// <see cref="ChallengeStatus.Confirmed"/>, an approval withdrawn <see cref="ChallengeStatus.Revoked"/>, a turn towards an
/// approval <see cref="ChallengeStatus.AwaitingAcceptance"/> until a person accepts it (<see cref="RevisionAcceptanceAction"/>, asked after the
/// close, in the unit of the verdict or in the revision debate: <see cref="ChallengeStatus.Accepted"/>), and anything else
/// that is not an approval <see cref="ChallengeStatus.Confirmed"/>. The verdicts of the revision must be announced valid with
/// the decision that closed it; otherwise the challenge stays pending.</item>
/// </list>
/// </remarks>
public static class ChallengeLog
{
    /// <summary>
    /// The action of a person's acceptance of a revision that turned towards an approval (D-40). The Debate module cannot
    /// reference Orchestration (ADR-0002): this is the value of <c>DeliveryService.RevisionAcceptanceAction</c>, and a test
    /// fails if they ever differ.
    /// </summary>
    public const string RevisionAcceptanceAction = "verdict.revision.accept";

    /// <summary>
    /// The action of the approval with which a debate opens (D-06): the person who gives it is who challenges. The value of
    /// <c>DeliveryService.DebateOpenAction</c>, pinned by a test.
    /// </summary>
    public const string DebateOpenAction = "debate.open";

    /// <summary>The kind of the unit <paramref name="unit"/>, by the start the log has of it.</summary>
    public static ChallengedUnitKind KindOf(IReadOnlyList<DecisionEntry> log, string unit)
    {
        ArgumentNullException.ThrowIfNull(log);
        foreach (var item in Items(log))
        {
            if (!string.Equals(item.Payload?.RunId, unit, StringComparison.Ordinal))
            {
                continue;
            }

            switch (item.Payload)
            {
                case RunStartedPayload:
                    return ChallengedUnitKind.Run;
                case DebateOpenedPayload:
                    return ChallengedUnitKind.Debate;
                case ProjectAuditStartedPayload:
                    return ChallengedUnitKind.Audit;
            }
        }

        return ChallengedUnitKind.Unknown;
    }

    /// <summary>
    /// The object of <paramref name="verdict"/>, stored in <paramref name="unit"/>, as the verified <paramref name="log"/> says
    /// (docs/15 D-39): a run gives the vendors of everyone who produced its work; a debate its producers and the vendors of its
    /// participants that were not waived; an audit the producers of the project and the declared authors. The vendor of the
    /// judge that issued the verdict is never added: with two vendors nobody could judge a run of Claude again.
    /// </summary>
    public static ChallengedObject ObjectOf(IReadOnlyList<DecisionEntry> log, string unit, string verdict)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(verdict);
        var items = Items(log);
        var kind = KindOf(log, unit);
        var excluded = new HashSet<VendorId>();
        var choice = false;
        switch (kind)
        {
            case ChallengedUnitKind.Run:
                excluded.UnionWith(RunLog.ProducerVendors(RunLog.EntriesOf(log, unit)));
                break;
            case ChallengedUnitKind.Debate when Opened(items, unit) is { } opened:
                excluded.UnionWith(Excluded(opened));
                choice = DebateJudgingRules.IsChoice(opened.Mode, opened.Subject);
                break;
            case ChallengedUnitKind.Audit when items.Select(i => i.Payload).OfType<ProjectAuditStartedPayload>().FirstOrDefault(p => p.RunId == unit) is { } started:
                excluded.UnionWith(started.ProducerVendors);
                excluded.UnionWith(started.DeclaredAuthors);
                break;
        }

        // The debate whose judgment gave the verdict: the unit itself, or a debate that closed with it (a revision kept in the unit of what it revised).
        var judgedBy = kind == ChallengedUnitKind.Debate
            ? unit
            : items.Select(i => i.Payload).OfType<DebateClosedPayload>().LastOrDefault(c => c.Verdicts.Contains(verdict, StringComparer.Ordinal))?.RunId;
        var judgedByOpened = judgedBy is null ? null : Opened(items, judgedBy);
        return new ChallengedObject(
            unit,
            kind,
            excluded,
            judgedBy,
            judgedByOpened?.Judging,
            [.. judgedByOpened?.Judges.Select(j => j.Agent) ?? []],
            choice);
    }

    /// <summary>The vendors no judge of a debate could run on: its producers, and the vendors of its participants that were not waived.</summary>
    public static IReadOnlySet<VendorId> Excluded(DebateOpenedPayload opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        var excluded = new HashSet<VendorId>(opened.ProducerVendors);
        excluded.UnionWith(opened.Participants.Select(p => p.Vendor).Where(v => !opened.WaivedParticipantVendors.Contains(v)));
        return excluded;
    }

    /// <summary>The revision debate that stands for <paramref name="verdict"/> (the latest challenge of it), or null when it was never challenged.</summary>
    public static string? LatestRevision(IReadOnlyList<DecisionEntry> log, string verdict)
    {
        ArgumentNullException.ThrowIfNull(log);
        string? latest = null;
        foreach (var item in Items(log))
        {
            switch (item.Payload)
            {
                case VerdictChallengedPayload challenged when string.Equals(challenged.Verdict, verdict, StringComparison.Ordinal):
                    latest = challenged.RevisionDebate;
                    break;
                case DebateOpenedPayload opened when string.Equals(opened.Challenges, verdict, StringComparison.Ordinal):
                    latest = opened.RunId;
                    break;
            }
        }

        return latest;
    }

    /// <summary>Where the challenge of <paramref name="challenged"/> stands (D-40), from the verified log.</summary>
    /// <param name="challenged">The challenged verdict as the store holds it; its approval counts only when the log announced it valid too.</param>
    public static ChallengeStatus StatusOf(IReadOnlyList<DecisionEntry> log, StoredVerdict challenged)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(challenged);
        var items = Items(log);
        if (LatestRevision(log, challenged.Id) is not { } debate)
        {
            return ChallengeStatus.None;
        }

        var opened = Opened(items, debate);
        var closing = items.LastOrDefault(i => i.Payload is DebateClosedPayload && string.Equals(i.Payload.RunId, debate, StringComparison.Ordinal));
        if (opened is null || !string.Equals(opened.Challenges, challenged.Id, StringComparison.Ordinal) || closing?.Payload is not DebateClosedPayload closed)
        {
            return ChallengeStatus.Pending;
        }

        if (string.Equals(closed.Rule, JudgingPanel.User, StringComparison.Ordinal))
        {
            return ChallengeStatus.DecidedByUser;
        }

        if (closed.Outcome != DebateOutcome.Resolved || Decision(items, closed) is not { } revision)
        {
            return ChallengeStatus.Pending; // aborted, a panel that disagrees, a verdict not valid, or verdicts the log does not show
        }

        var announced = items.Select(i => i.Payload).OfType<VerdictIssuedPayload>().LastOrDefault(v => string.Equals(v.VerdictId, challenged.Id, StringComparison.Ordinal));
        var approved = challenged.Valid && challenged.Verdict.Decision == VerdictDecision.Approved
            && announced is { Valid: true, Decision: VerdictDecision.Approved };
        var approves = revision == VerdictDecision.Approved;
        return (approved, approves) switch
        {
            (true, true) => ChallengeStatus.Confirmed,
            (true, false) => ChallengeStatus.Revoked,
            (false, true) => Accepted(items, challenged.RunId, debate, closing.Entry.Sequence) ? ChallengeStatus.Accepted : ChallengeStatus.AwaitingAcceptance,
            (false, false) => ChallengeStatus.Confirmed,
        };
    }

    /// <summary>The revision debate that closed with <paramref name="verdict"/> among its verdicts, and the verdict it challenged; null when there is none.</summary>
    public static (string Debate, string Challenged)? RevisionOf(IReadOnlyList<DecisionEntry> log, string verdict)
    {
        ArgumentNullException.ThrowIfNull(log);
        var items = Items(log);
        var closed = items.Select(i => i.Payload).OfType<DebateClosedPayload>().LastOrDefault(c => c.Verdicts.Contains(verdict, StringComparer.Ordinal));
        return closed is not null && Opened(items, closed.RunId) is { Challenges: { } challenged }
            ? (closed.RunId, challenged)
            : null;
    }

    /// <summary>The person who approved the opening of <paramref name="debate"/> (<see cref="DebateOpenAction"/>, in the debate), or null when the log shows none.</summary>
    public static string? OpenedBy(IReadOnlyList<DecisionEntry> log, string debate)
    {
        ArgumentNullException.ThrowIfNull(log);
        return PersonApprovals.Granted(Items(log), DebateOpenAction, unit => string.Equals(unit, debate, StringComparison.Ordinal)).LastOrDefault()?.DecidedBy;
    }

    private static List<RunItem> Items(IReadOnlyList<DecisionEntry> log) => [.. log.OrderBy(e => e.Sequence).Select(AuditedRun.Parse)];

    private static DebateOpenedPayload? Opened(List<RunItem> items, string debate) =>
        items.Select(i => i.Payload).OfType<DebateOpenedPayload>().FirstOrDefault(o => string.Equals(o.RunId, debate, StringComparison.Ordinal));

    /// <summary>The decision of a revision that closed by its judge or its agreeing panel: each verdict it names announced valid, all with the same decision.</summary>
    private static VerdictDecision? Decision(List<RunItem> items, DebateClosedPayload closed)
    {
        if (closed.Verdicts.Count == 0)
        {
            return null;
        }

        var decisions = new HashSet<VerdictDecision>();
        foreach (var id in closed.Verdicts)
        {
            var issued = items.Select(i => i.Payload).OfType<VerdictIssuedPayload>().LastOrDefault(v => string.Equals(v.VerdictId, id, StringComparison.Ordinal));
            if (issued is not { Valid: true })
            {
                return null;
            }

            decisions.Add(issued.Decision);
        }

        return decisions.Count == 1 ? decisions.Single() : null;
    }

    /// <summary>A person's acceptance asked after the revision closed (its request comes after <c>DebateClosed</c>), in the unit of the verdict or in the revision debate.</summary>
    private static bool Accepted(List<RunItem> items, string? unit, string debate, long closedAt) =>
        PersonApprovals.Granted(items, RevisionAcceptanceAction, u => string.Equals(u, debate, StringComparison.Ordinal) || (unit is not null && string.Equals(u, unit, StringComparison.Ordinal)))
            .Any(yes => yes.Requested > closedAt);
}
