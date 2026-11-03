using AgentStudio.Domain.Semantic;

namespace AgentStudio.Tests.Domain.Semantic;

/// <summary>
/// The fusion of the hybrid search (docs/17 D-05, F12-4): Reciprocal Rank Fusion with k = 60, and an order that never
/// depends on chance: ties go to the newest source and then by ordinal id.
/// </summary>
public sealed class HybridRankingTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static RankedCandidate C(string id, int day = 0) => new(id, Monday.AddDays(day));

    [Fact]
    public void Each_list_adds_one_over_k_plus_the_rank()
    {
        var fused = HybridRanking.Fuse([C("a"), C("b")], [C("b"), C("c")]);

        Assert.Equal(["b", "a", "c"], fused.Select(f => f.Id));
        Assert.Equal((1.0 / 62) + (1.0 / 61), fused[0].Score);
        Assert.Equal((2, 1), (fused[0].LexicalRank, fused[0].SemanticRank));
        Assert.Equal(1.0 / 61, fused[1].Score);
        Assert.Equal((1, (int?)null), (fused[1].LexicalRank, fused[1].SemanticRank));
        Assert.Equal(1.0 / 62, fused[2].Score);
        Assert.Equal(((int?)null, 2), (fused[2].LexicalRank, fused[2].SemanticRank));
        Assert.Equal(60, HybridRanking.K);
    }

    [Fact]
    public void Ties_go_to_the_newest_source_and_then_by_ordinal_id()
    {
        // Same ranks in opposite lists: the same score exactly.
        var fused = HybridRanking.Fuse([C("old", 0), C("new", 2)], [C("new", 2), C("old", 0)]);
        Assert.Equal(fused[0].Score, fused[1].Score);
        Assert.Equal(["new", "old"], fused.Select(f => f.Id));

        var sameDay = HybridRanking.Fuse([C("b"), C("a")], [C("a"), C("b")]);
        Assert.Equal(["a", "b"], sameDay.Select(f => f.Id));

        var upper = HybridRanking.Fuse([C("b")], [C("B")]);
        Assert.Equal(["B", "b"], upper.Select(f => f.Id));
    }

    [Fact]
    public void Without_a_semantic_list_the_order_is_the_lexical_one_and_the_other_way_round()
    {
        RankedCandidate[] words = [C("x", 0), C("y", 5), C("z", 1)];

        Assert.Equal(["x", "y", "z"], HybridRanking.Fuse(words, []).Select(f => f.Id));
        Assert.Equal(["x", "y", "z"], HybridRanking.Fuse([], words).Select(f => f.Id));
        Assert.Empty(HybridRanking.Fuse([], []));
    }

    [Fact]
    public void A_repeated_id_counts_at_its_first_place()
    {
        var fused = HybridRanking.Fuse([C("a"), C("b"), C("a")], []);

        Assert.Equal(["a", "b"], fused.Select(f => f.Id));
        Assert.Equal(1, fused[0].LexicalRank);
    }

    [Fact]
    public void An_id_with_two_times_or_a_blank_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HybridRanking.Fuse([C("a", 0)], [C("a", 1)]));
        Assert.Throws<ArgumentException>(() => HybridRanking.Fuse([C(" ")], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => HybridRanking.Fuse([C("a")], [], k: 0));
    }

    [Fact]
    public void Over_two_hundred_random_lists_the_fusion_is_deterministic_and_ordered()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var random = new Random(seed);
            var ids = Enumerable.Range(0, random.Next(1, 40)).Select(i => $"f{i:D2}").ToList();
            var days = ids.ToDictionary(id => id, _ => random.Next(0, 4));
            var lexical = ids.Where(_ => random.Next(3) > 0).OrderBy(_ => random.Next()).Select(id => C(id, days[id])).ToList();
            var semantic = ids.Where(_ => random.Next(3) > 0).OrderBy(_ => random.Next()).Select(id => C(id, days[id])).ToList();

            var fused = HybridRanking.Fuse(lexical, semantic);
            var again = HybridRanking.Fuse([.. lexical], [.. semantic]);

            Assert.Equal(fused, again);
            Assert.Equal(lexical.Concat(semantic).Select(c => c.Id).Distinct().Order(StringComparer.Ordinal), fused.Select(f => f.Id).Order(StringComparer.Ordinal));
            for (var i = 1; i < fused.Count; i++)
            {
                var (before, after) = (fused[i - 1], fused[i]);
                Assert.True(
                    before.Score > after.Score
                    || (before.Score == after.Score && (before.At > after.At || (before.At == after.At && string.CompareOrdinal(before.Id, after.Id) < 0))),
                    $"seed {seed}: {before} before {after}");
            }
        }
    }
}
