using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Domain.Debate;

/// <summary>
/// The immutable header of a debate as the domain needs it to open the session again (docs/15 D-01): the row of
/// <c>debate_sessions</c> that <c>DebateOpened</c> anchors, read back. <see cref="Question"/> is the text of the question,
/// read from its evidence against the hash the log anchored.
/// </summary>
public sealed record DebateHeader(
    DebateId Id,
    DebateMode Mode,
    DebateSubjectKind Subject,
    string Question,
    FrozenRequirementSet Requirements,
    IReadOnlyList<DebateParticipant> Participants,
    JudgingArrangement Judging,
    DebateLimits Limits,
    DateTimeOffset StartedAt);

/// <summary>A fact of a debate that changes its state, read back from the log (<c>DebateContributionRecorded</c>, <c>DebateRoundClosed</c>, <c>DebateClosed</c>).</summary>
public abstract record DebateEvent(DateTimeOffset At);

/// <summary>A contribution was published when its round closed (<c>DebateContributionRecorded</c>).</summary>
public sealed record ContributionPublished(DateTimeOffset At, DebateContribution Contribution) : DebateEvent(At);

/// <summary>
/// A round closed (<c>DebateRoundClosed</c>) with the tokens used so far. Without <paramref name="Judgment"/> the debate went
/// on to the next round; with it, it went to judgment for that reason and, for <see cref="DebateJudgmentReason.Limit"/>, the
/// <paramref name="Limit"/> reached. <paramref name="At"/> is the instant the coordinator closed the round (never a later
/// one), so a limit the live debate did not reach is not reached by the replay either.
/// </summary>
public sealed record RoundClosed(DateTimeOffset At, int Round, long TokensUsed, DebateJudgmentReason? Judgment, DebateLimitReached Limit = DebateLimitReached.None) : DebateEvent(At);

/// <summary>The debate was closed (<c>DebateClosed</c>) with this outcome and these valid verdicts, read from the verdict store.</summary>
public sealed record DebateEnded(DateTimeOffset At, PanelOutcome Outcome, IReadOnlyList<Verdict> Verdicts) : DebateEvent(At);

/// <summary>The debate was aborted (<c>DebateClosed</c> with the outcome <see cref="DebateOutcome.Aborted"/>).</summary>
public sealed record DebateAborted(DateTimeOffset At, string Reason) : DebateEvent(At);

/// <summary>
/// A debate rebuilt from its log. One whose replay reports any <paramref name="Problems"/> is not resumed: the log does not
/// describe a debate the domain allows, so a person must look at it. The session is the state just before the first
/// problem, or null when the header itself does not open.
/// </summary>
public sealed record DebateReplay(DebateSession? Session, IReadOnlyList<string> Problems)
{
    public bool IsResumable => Session is not null && Problems.Count == 0;
}

/// <summary>
/// Rebuilds the state of a debate (docs/15 D-01 and D-04) by applying its events, in order, through the public API of
/// <see cref="DebateSession"/> and nothing else, like <see cref="Orchestration.RunStateReplayer"/> for a run.
/// </summary>
/// <remarks>
/// <para>Besides the rules of the session, the replay checks that:</para>
/// <list type="bullet">
/// <item>a round that closes is the current one;</item>
/// <item>a round the log closed without judgment is one the domain lets go on: if <see cref="DebateSession.AdvanceRound"/>
/// sends it to judgment, the log hid a limit;</item>
/// <item>a round the log closed into judgment does so for a reason the domain accepts (<see cref="DebateSession.RequestJudgment(DebateJudgmentReason, DebateLimitReached)"/>),
/// which leaves the same state as the domain reaching that limit or that synthesis by itself.</item>
/// </list>
/// </remarks>
public static class DebateStateReplayer
{
    public static DebateReplay Replay(DebateHeader header, IEnumerable<DebateEvent> events)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(events);

        DebateSession session;
        try
        {
            session = DebateSession.Open(header.Id, header.Mode, header.Subject, header.Question, header.Requirements, header.Participants, header.Judging, header.Limits, header.StartedAt);
        }
        catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
        {
            return new DebateReplay(null, [$"The header of the debate cannot be opened: {Describe(ex)}"]);
        }

        var position = 0;
        foreach (var debateEvent in events)
        {
            position++;
            string? problem;
            try
            {
                problem = Apply(session, debateEvent);
            }
            catch (Exception ex) when (ex is DomainRuleViolationException or ArgumentException)
            {
                // The log is data read back from storage: a rule it breaks (or a blank field) is a finding, not a crash.
                problem = Describe(ex);
            }

            if (problem is not null)
            {
                return new DebateReplay(session, [$"Event {position}: {problem}"]);
            }
        }

        return new DebateReplay(session, []);
    }

    private static string? Apply(DebateSession session, DebateEvent? debateEvent)
    {
        switch (debateEvent)
        {
            case ContributionPublished published:
                session.Submit(published.Contribution);
                return null;
            case RoundClosed closed:
                return CloseRound(session, closed);
            case DebateEnded ended:
                session.Close(ended.Outcome, ended.Verdicts);
                return null;
            case DebateAborted aborted:
                session.Abort(aborted.Reason);
                return null;
            case null:
                return "The event is missing.";
            default:
                return $"Unknown kind of event: {debateEvent.GetType().Name}.";
        }
    }

    private static string? CloseRound(DebateSession session, RoundClosed closed)
    {
        if (closed.Round != session.CurrentRound)
        {
            return $"The log closes round {closed.Round}, but the debate is in round {session.CurrentRound}.";
        }

        if (closed.Judgment is { } reason)
        {
            session.RequestJudgment(reason, closed.Limit);
            return null;
        }

        if (closed.Limit != DebateLimitReached.None)
        {
            return $"The log closes round {closed.Round} for the limit {closed.Limit} without sending the debate to judgment.";
        }

        session.AdvanceRound(closed.At, closed.TokensUsed);
        return session.Phase == DebatePhase.AwaitingJudgment
            ? $"The log closes round {closed.Round} without sending the debate to judgment, but the domain sends it to judgment ({session.JudgmentReason}, {session.LimitReached})."
            : null;
    }

    private static string Describe(Exception ex) => ex is DomainRuleViolationException { Rule.Length: > 0 } violation ? $"{violation.Rule}: {ex.Message}" : ex.Message;
}
