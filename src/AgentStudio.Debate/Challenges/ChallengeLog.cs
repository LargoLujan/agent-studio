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
/// the store. Where a challenge stands is the rule of <see cref="ChallengeRules"/> (in Application), shared with the board:
/// a challenge whose revision a person declined to open counts for nothing, a revision not closed, aborted or without a decision
/// leaves it pending, and a person accepts a revision that turns towards an approval only for the verdict the run ended with.
/// </summary>
public static class ChallengeLog
{
    /// <summary>
    /// The action of a person's acceptance of a revision that turned towards an approval (D-40). The Debate module cannot
    /// reference Orchestration (ADR-0002): this is the value of <c>DeliveryService.RevisionAcceptanceAction</c>, and a test
    /// fails if they ever differ.
    /// </summary>
    public const string RevisionAcceptanceAction = ChallengeRules.RevisionAcceptanceAction;

    /// <summary>
    /// The action of the approval with which a debate opens (D-06): the person who gives it is who challenges. The value of
    /// <c>DeliveryService.DebateOpenAction</c>, pinned by a test.
    /// </summary>
    public const string DebateOpenAction = ChallengeRules.DebateOpenAction;

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

    /// <summary>
    /// The revision debate that stands for <paramref name="verdict"/> (the latest challenge of it whose revision a person did not decline to open), or null when it was never
    /// challenged: the one <see cref="ChallengeRules"/> reads.
    /// </summary>
    public static string? LatestRevision(IReadOnlyList<DecisionEntry> log, string verdict)
    {
        ArgumentNullException.ThrowIfNull(log);
        return ChallengeRules.LiveRevision(ChallengeRules.Parse(log), verdict);
    }

    /// <summary>
    /// Where the challenge of <paramref name="challenged"/> stands (D-40), from the verified log: the rule of <see cref="ChallengeRules"/>, the same the board reads, so what a person
    /// may do (challenge again, accept a revision) never contradicts what the board shows or the delivery lets through.
    /// </summary>
    /// <param name="challenged">The challenged verdict as the store holds it; its approval counts only when the log announced it valid too.</param>
    /// <param name="stored">The verdicts of the store by id: those of the revision must be in it, valid.</param>
    public static ChallengeStatus StatusOf(IReadOnlyList<DecisionEntry> log, StoredVerdict challenged, IReadOnlyDictionary<string, StoredVerdict> stored)
    {
        ArgumentNullException.ThrowIfNull(log);
        return ChallengeRules.Read(log, challenged, stored).Status;
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
}
