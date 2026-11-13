using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Integration;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// The commit the judge saw, for the rule of «integrado» by pull request (docs/21 D-29): the one the dossier of the verdict that decided was built on. It is the
/// commit <see cref="DeliveryService.ShownCommit"/> gives for the head of the log the verdict names (the same reading the local delivery uses to stop for a verdict that
/// does not match), so the two paths to «integrado» ask the same question of the same data.
/// </summary>
internal static class RemoteJudgement
{
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
