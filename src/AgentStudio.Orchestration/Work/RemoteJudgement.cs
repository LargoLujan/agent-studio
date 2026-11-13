using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Integration;
using HostingStanding = AgentStudio.Application.Hosting.VerdictStanding;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// The commit the judge saw, for the rule of «integrado» by pull request (docs/21 D-29): the one the dossier of the verdict that decided was built on. It is the
/// commit <see cref="DeliveryService.ShownCommit"/> gives for the head of the log the verdict names (the same reading the local delivery uses to stop for a verdict that
/// does not match), so the two paths to «integrado» ask the same question of the same data.
/// </summary>
internal static class RemoteJudgement
{
    /// <summary>
    /// The one rule of «the verdict in force of a run» for what is published and shown about its pull request: the commit the judge saw and the <see cref="HostingStanding"/> of the
    /// verdict, for the last valid verdict that judged the run. It is the rule of <see cref="VerdictReach"/> that the board, the chain and the participation share: valid in the log and in
    /// the store with the same decision, never the verdict of a revision (it reaches the work through the verdict it revises), and its standing after a person's challenge
    /// (<see cref="VerdictReach.Standing"/>). The mapping is the one of docs/21 D-21: approved and not challenged is <c>Approved</c>; rejected or with changes requested is <c>Rejected</c>;
    /// challenged, escalated or unresolved is <c>InReview</c>. (None, none) when no valid verdict judged the run. The view of the pull requests and, from <c>Orchestration/Publication</c>,
    /// the verdict notice call this, so that what is shown and what is published cannot say different things.
    /// </summary>
    public static (string? JudgedCommit, HostingStanding? Standing) VerdictInForce(
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload? Payload)> parsed, IReadOnlyList<DecisionEntry> log, string run, IReadOnlyDictionary<string, StoredVerdict> storedVerdicts)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(storedVerdicts);
        VerdictIssuedPayload? deciding = null;
        StoredVerdict? stored = null;
        foreach (var issued in parsed.Select(p => p.Payload).OfType<VerdictIssuedPayload>().Where(v => string.Equals(v.RunId, run, StringComparison.Ordinal)))
        {
            if (issued.Valid && storedVerdicts.TryGetValue(issued.VerdictId, out var found) && found.Valid && found.Verdict.Decision == issued.Decision
                && VerdictReach.Revises(parsed, found, storedVerdicts) is null)
            {
                deciding = issued;
                stored = found;
            }
        }

        if (deciding is null || stored is null)
        {
            return (null, null);
        }

        var state = VerdictReach.Standing(parsed, stored, storedVerdicts);
        HostingStanding standing = state.Challenged
            ? HostingStanding.InReview
            : state.Decision switch
            {
                VerdictDecision.Approved => HostingStanding.Approved,
                VerdictDecision.Rejected or VerdictDecision.ChangesRequested => HostingStanding.Rejected,
                _ => HostingStanding.InReview,
            };
        return (JudgedCommit(log, run, deciding), standing);
    }

    /// <summary>
    /// The commit the judge of <paramref name="run"/> saw, for a reader that has no store of verdicts (the annotation of a commit): the one of the last verdict the log
    /// announced as valid for the run. It is the board's reading except that it does not look at the store nor at the revisions of a challenge, so it is a hint for
    /// <c>HeadWasJudgedCommit</c>, never what decides «integrado».
    /// </summary>
    public static string? LastAnnouncedCommit(IReadOnlyList<DecisionEntry> log, string run)
    {
        ArgumentNullException.ThrowIfNull(log);
        var announced = log
            .Where(e => e.Kind == DecisionKind.VerdictIssued)
            .Select(Engine.RunLog.Payload)
            .OfType<VerdictIssuedPayload>()
            .LastOrDefault(v => v.Valid && string.Equals(v.RunId, run, StringComparison.Ordinal));
        return JudgedCommit(log, run, announced);
    }

    /// <summary>
    /// The commit the judge of <paramref name="run"/> saw when it issued <paramref name="deciding"/>, or null when the log does not say (no verdict, a head that is not
    /// in the log, or a run with no clean integration yet): then a merged pull request cannot be shown to be what was judged.
    /// </summary>
    public static string? JudgedCommit(IReadOnlyList<DecisionEntry> log, string run, VerdictIssuedPayload? deciding)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (deciding is null || string.IsNullOrEmpty(deciding.DecisionLogHeadHash))
        {
            return null;
        }

        try
        {
            return DeliveryService.ShownCommit(log, deciding.DecisionLogHeadHash, new RunId(run));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
