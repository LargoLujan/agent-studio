using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Tests.Domain;

/// <summary>
/// docs/15 D-19 and D-20: the tokens of a debate are those of the turns of its participants, input (with the cache) plus
/// output as the CLI reported them; what is missing is estimated and counts all the same, so the limit is never passed by
/// a figure that did not come.
/// </summary>
public sealed class DebateBudgetTests
{
    private static MetricValue<long> Reported(long value) => MetricValue<long>.Exact(value, "claude result.usage");

    private static TurnUsage Turn(string turn, MetricValue<long> input, MetricValue<long> output, long estimate = 1_000, int attempt = 1) =>
        new(turn, attempt, input, output, estimate);

    [Fact]
    public void The_tokens_of_reported_turns_are_their_input_and_output_added_up_exactly()
    {
        var used = DebateBudget.Used([Turn("r1-arquitecto", Reported(40_000), Reported(1_200)), Turn("r1-critico", MetricValue<long>.Exact(20_000, "codex turn.completed"), MetricValue<long>.Exact(800, "codex turn.completed"))]);

        Assert.Equal(62_000, used.Sum);
        Assert.Equal((MetricOrigin.ReportedByProvider, 2, 0), (used.Origin, used.Reported, used.Missing));
        Assert.Equal(["claude result.usage", "codex turn.completed"], used.Sources);
    }

    [Fact]
    public void A_turn_without_figures_counts_as_its_estimate_and_the_total_says_so()
    {
        var used = DebateBudget.Used([Turn("r1-arquitecto", Reported(40_000), Reported(1_200)), Turn("r1-critico", MetricValue<long>.Missing("sin resultado"), MetricValue<long>.Missing("sin resultado"), estimate: 5_000)]);

        Assert.Equal(46_200, used.Sum);
        Assert.Equal(MetricOrigin.EstimatedBySystem, used.Origin);
        Assert.Contains(DebateBudget.EstimateSource, used.Sources);
        Assert.Equal(1, DebateBudget.ReportedTurns([Turn("a", Reported(1), Reported(1)), Turn("b", MetricValue<long>.NotExposed("n/a"), Reported(1))]));
    }

    [Fact]
    public void A_turn_with_part_of_its_figures_counts_the_larger_of_what_came_and_its_estimate()
    {
        var bigInput = DebateBudget.TokensOf(Turn("r2-critico", Reported(39_000), MetricValue<long>.NotExposed("no output"), estimate: 2_000));
        var smallInput = DebateBudget.TokensOf(Turn("r2-critico", Reported(500), MetricValue<long>.NotExposed("no output"), estimate: 2_000));

        Assert.Equal(MetricValue<long>.EstimatedBySystem(39_000, DebateBudget.EstimateSource), bigInput);
        Assert.Equal(MetricValue<long>.EstimatedBySystem(2_000, DebateBudget.EstimateSource), smallInput);
    }

    /// <summary>D-19: an all-zero usage is «sin dato» (Codex reports zeros when it has nothing): it is estimated, never zero.</summary>
    [Fact]
    public void A_turn_that_reported_zero_tokens_counts_as_its_estimate()
    {
        var zero = DebateBudget.TokensOf(Turn("r1-critico", MetricValue<long>.Exact(0, "codex turn.completed"), MetricValue<long>.Exact(0, "codex turn.completed"), estimate: 3_000));

        Assert.Equal(MetricValue<long>.EstimatedBySystem(3_000, DebateBudget.EstimateSource), zero);
    }

    [Fact]
    public void A_debate_with_no_turn_has_used_nothing_yet_and_no_sum()
    {
        var used = DebateBudget.Used([]);

        Assert.Null(used.Sum);
        Assert.Equal((0, 0), (used.Reported, used.Missing));
        Assert.False(DebateBudget.WouldExceed(new DebateLimits(4, TimeSpan.FromMinutes(30), 1_000), used, 1_000));
        Assert.True(DebateBudget.WouldExceed(new DebateLimits(4, TimeSpan.FromMinutes(30), 1_000), used, 1_001));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(7, 2)]
    [InlineData(10, 3)]
    public void The_estimate_of_a_text_is_its_characters_divided_by_four_rounded_up(long characters, long tokens)
    {
        Assert.Equal(tokens, DebateBudget.EstimateOf(characters));
    }

    [Fact]
    public void The_next_round_is_estimated_as_the_mean_turn_of_the_last_one_times_the_participants()
    {
        IReadOnlyList<TurnUsage> lastRound = [Turn("r2-a", Reported(100_000), Reported(1)), Turn("r2-b", Reported(50_000), Reported(0))];

        Assert.Equal(225_002, DebateBudget.NextRoundEstimate(lastRound, 3));
        Assert.Equal(0, DebateBudget.NextRoundEstimate([], 3));
    }

    [Fact]
    public void A_round_is_not_launched_when_what_it_would_spend_passes_the_limit_and_without_a_limit_it_always_is()
    {
        var used = DebateBudget.Used([Turn("r1-a", Reported(300_000), Reported(0))]);
        var limits = DebateLimits.Default;

        Assert.False(DebateBudget.WouldExceed(limits, used, 100_000));
        Assert.True(DebateBudget.WouldExceed(limits, used, 100_001));
        Assert.False(DebateBudget.WouldExceed(limits with { MaxTotalTokens = null }, used, long.MaxValue / 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => DebateBudget.WouldExceed(limits, used, -1));
    }

    [Fact]
    public void Estimates_cannot_be_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DebateBudget.TokensOf(Turn("r1-a", Reported(1), Reported(1), estimate: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DebateBudget.EstimateOf(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DebateBudget.NextRoundEstimate([], -1));
    }

    /// <summary>
    /// docs/15 §8: the budget never counts less. Over random turns by seed, each one counts at least what its CLI reported
    /// and, when a figure is missing, at least its estimate; the total is their exact sum and is estimated whenever one is.
    /// </summary>
    [Fact]
    public void The_budget_never_counts_less_than_what_came_nor_than_the_estimate_of_what_did_not()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            MetricValue<long> Figure() => random.Next(5) switch
            {
                0 => MetricValue<long>.Missing("sin dato"),
                1 => MetricValue<long>.NotExposed("no expuesto"),
                _ => Reported(random.Next(0, 160_000)),
            };

            var turns = Enumerable.Range(0, random.Next(0, 9)).Select(i => Turn($"r{i / 2 + 1}-p{i % 2}", Figure(), Figure(), random.Next(0, 50_000))).ToList();

            var used = DebateBudget.Used(turns);

            long expected = 0;
            var estimated = false;
            foreach (var turn in turns)
            {
                var counted = DebateBudget.TokensOf(turn).Value!.Value;
                var came = (turn.InputTokens.Value ?? 0) + (turn.OutputTokens.Value ?? 0);
                var complete = turn.InputTokens.HasValue && turn.OutputTokens.HasValue && came > 0;
                Assert.True(counted >= came, $"seed {seed}: {turn.Turn} counts {counted} but {came} came.");
                Assert.True(complete || counted >= turn.Estimate, $"seed {seed}: {turn.Turn} counts {counted}, less than its estimate {turn.Estimate}.");
                expected += counted;
                estimated |= !complete;
            }

            Assert.Equal(turns.Count == 0 ? null : expected, used.Sum);
            Assert.Equal(turns.Count, used.Reported);
            Assert.Equal(0, used.Missing);
            if (turns.Count > 0)
            {
                Assert.Equal(estimated, used.Origin == MetricOrigin.EstimatedBySystem);
            }
        }
    }
}
