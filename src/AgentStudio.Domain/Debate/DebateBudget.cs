using AgentStudio.Domain.Metrics;

namespace AgentStudio.Domain.Debate;

/// <summary>
/// The usage of one attempt of one turn of a participant (docs/15 D-19), as <c>AgentRunFinished</c> version 2 recorded it:
/// the input tokens (the whole prompt, with the cache read and written) and the output tokens, each with its origin, and
/// <paramref name="Estimate"/>, what Agent Studio estimates for the attempt from the characters of its prompt and its answer
/// (<see cref="DebateBudget.EstimateOf"/>). <paramref name="Turn"/> is the task of the turn (<c>r2-arquitecto</c>).
/// </summary>
public sealed record TurnUsage(string Turn, int Attempt, MetricValue<long> InputTokens, MetricValue<long> OutputTokens, long Estimate);

/// <summary>
/// The tokens a debate used (docs/15 D-19 and D-20), always from the log, never from a counter in memory. Each turn is a
/// new session, so the usage an attempt reports is its own. The tokens of the judge do not count: the limit sends the
/// debate to judgment, and the judgment cannot run out of budget.
/// </summary>
public static class DebateBudget
{
    /// <summary>The source of every figure Agent Studio estimates: the characters divided by four.</summary>
    public const string EstimateSource = "estimate: 4 chars/token";

    /// <summary>The estimate of a text: its characters divided by four, rounded up, so it never counts less.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public static long EstimateOf(long characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characters);
        return (characters / 4) + (characters % 4 == 0 ? 0 : 1);
    }

    /// <summary>
    /// What one attempt counts: input plus output as the CLI reported them, with the weaker of their origins. When a figure
    /// did not come (no result, a CLI that does not expose it, a usage all at zero, which is «sin dato») the attempt counts
    /// the larger of what came and its estimate, «estimado por Agent Studio»: a missing figure never lets the limit pass.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The estimate is negative.</exception>
    public static MetricValue<long> TokensOf(TurnUsage turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentOutOfRangeException.ThrowIfNegative(turn.Estimate);

        var came = Came(turn);
        if (IsComplete(turn))
        {
            var origin = MetricOrigins.Weakest(turn.InputTokens.Origin, turn.OutputTokens.Origin);
            var source = string.Equals(turn.InputTokens.Source, turn.OutputTokens.Source, StringComparison.Ordinal)
                ? turn.InputTokens.Source
                : $"{turn.InputTokens.Source} + {turn.OutputTokens.Source}";
            return MetricValue<long>.Of(origin, came, source);
        }

        return MetricValue<long>.EstimatedBySystem(Math.Max(came, turn.Estimate), EstimateSource);
    }

    /// <summary>
    /// The tokens of the attempts so far: every attempt counts (<see cref="TokensOf"/>), so the sum has no gap, and its
    /// origin is the weakest of them: «estimado por Agent Studio» as soon as one is an estimate. How many were reported by
    /// the CLI, for «parcial (n de m informados)», is <see cref="ReportedTurns"/>. With no attempt there is no sum yet.
    /// </summary>
    public static MetricAggregate<long> Used(IReadOnlyList<TurnUsage> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return MetricAggregate.Sum(turns.Select(TokensOf));
    }

    /// <summary>How many attempts came with both figures, so they count what was reported and not an estimate.</summary>
    public static int ReportedTurns(IReadOnlyList<TurnUsage> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return turns.Count(IsComplete);
    }

    /// <summary>
    /// What the next round is expected to use (D-20): the mean of an attempt of the last round times the participants,
    /// rounded up. Zero when the last round had no attempt.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The number of participants is negative.</exception>
    public static long NextRoundEstimate(IReadOnlyList<TurnUsage> previousRound, int participants)
    {
        ArgumentNullException.ThrowIfNull(previousRound);
        ArgumentOutOfRangeException.ThrowIfNegative(participants);
        if (previousRound.Count == 0)
        {
            return 0;
        }

        var total = previousRound.Sum(t => TokensOf(t).Value!.Value);
        var scaled = checked(total * participants);
        return (scaled / previousRound.Count) + (scaled % previousRound.Count == 0 ? 0 : 1);
    }

    /// <summary>Whether launching what is estimated would pass the token limit (strictly more than it); never without a limit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The estimate is negative.</exception>
    public static bool WouldExceed(DebateLimits limits, MetricAggregate<long> used, long nextRoundEstimate)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(used);
        ArgumentOutOfRangeException.ThrowIfNegative(nextRoundEstimate);
        return limits.MaxTotalTokens is { } max && checked((used.Sum ?? 0) + nextRoundEstimate) > max;
    }

    /// <summary>Both figures came (none negative) and they are not all zero, which is «sin dato» (D-19).</summary>
    private static bool IsComplete(TurnUsage turn) =>
        turn.InputTokens.Value is >= 0 && turn.OutputTokens.Value is >= 0 && Came(turn) > 0;

    /// <summary>What the figures that came add up to; a negative one is not a figure.</summary>
    private static long Came(TurnUsage turn) =>
        checked((turn.InputTokens.Value is { } input and >= 0 ? input : 0) + (turn.OutputTokens.Value is { } output and >= 0 ? output : 0));
}
