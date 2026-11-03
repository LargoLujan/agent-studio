using AgentStudio.Application.Semantic;
using AgentStudio.SkillsMcp.History;
using static AgentStudio.Tests.SkillsMcp.ProjectHistorySamples;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>
/// The ranking of <c>search_project_history</c> (docs/17 D-06; ADR-0047): by words as the index ranks them (the terms of
/// <c>LexicalTerms</c> as phrases, <c>bm25</c> over tokens folded as <c>unicode61</c> folds them), widened by the vectors
/// of the best word hits when the snapshot carries them, and fused by <c>HybridRanking</c>; never by embedding the query.
/// </summary>
public sealed class HistoryRankingTests
{
    private static IEnumerable<string> Ids(HistorySearch search) => search.Matches.Select(m => m.Fragment.Id);

    [Fact]
    public void Tokens_are_folded_as_unicode61_folds_them()
    {
        Assert.Equal(
            ["que", "paso", "con", "calc", "multiply", "arbol", "nandu", "r2", "3"],
            HistoryRanking.Tokens("¿Qué pasó con Calc.Multiply? ÁRBOL ñandú, R2: 3"));
    }

    [Fact]
    public void Text_that_is_not_well_formed_utf16_is_folded_without_throwing()
    {
        Assert.Equal(["calc", "multiply"], HistoryRanking.Tokens("calc\uD800multiply"));
    }

    [Fact]
    public void Without_vectors_only_the_fragments_that_share_a_word_come_out()
    {
        var ranking = new HistoryRanking(WithoutVectors());

        var search = ranking.Search("¿Por qué falla la división entre cero?", 10);

        Assert.False(ranking.HasVectors);
        Assert.Equal([DivideByZero.Id], Ids(search));
        Assert.Equal(1, search.WordMatches);
        Assert.False(search.Widened);
        Assert.Equal((1, (int?)null), (search.Matches[0].Rank.LexicalRank!.Value, search.Matches[0].Rank.SemanticRank));
    }

    [Fact]
    public void A_term_found_more_often_ranks_its_fragment_first()
    {
        var search = new HistoryRanking(WithoutVectors()).Search("pruebas cumplido", 10);

        // «cumplido» twice in the verdict, «pruebas» once in the plan: the same weight per term, so the verdict goes first.
        Assert.Equal([Verdict.Id, Plan.Id], Ids(search));
    }

    [Fact]
    public void A_term_with_punctuation_is_a_phrase_of_its_tokens()
    {
        var ranking = new HistoryRanking(WithoutVectors());

        Assert.Equal([Plan.Id], Ids(ranking.Search("Calc.Multiply", 10)));
        Assert.Empty(ranking.Search("Multiply.Calc", 10).Matches);
    }

    [Fact]
    public void Function_words_alone_find_nothing()
    {
        var search = new HistoryRanking(WithVectors()).Search("de la el en y", 10);

        Assert.Empty(search.Matches);
        Assert.Equal(0, search.WordMatches);
    }

    [Fact]
    public void The_vectors_of_the_best_word_hits_widen_the_search_to_what_means_the_same()
    {
        var search = new HistoryRanking(WithVectors()).Search("división entre cero", 3);

        // Only the known error shares words with the query; the plan and the verdict, about tests too, come by meaning.
        Assert.Equal([DivideByZero.Id, Plan.Id, Verdict.Id], Ids(search));
        Assert.True(search.Widened);
        Assert.Equal(1, search.WordMatches);
        Assert.Equal((1, 1), (search.Matches[0].Rank.LexicalRank, search.Matches[0].Rank.SemanticRank));
        Assert.Equal(((int?)null, 2), (search.Matches[1].Rank.LexicalRank, search.Matches[1].Rank.SemanticRank));
    }

    [Fact]
    public void Nothing_comes_by_meaning_when_nothing_comes_by_words()
    {
        var search = new HistoryRanking(WithVectors()).Search("kubernetes helm", 10);

        Assert.Empty(search.Matches);
        Assert.False(search.Widened);
    }

    [Fact]
    public void Word_hits_without_a_vector_do_not_widen()
    {
        var fragments = Fragments.Select(f => f.Id == DivideByZero.Id ? f with { Vector = null } : f).ToList();
        var search = new HistoryRanking(new ProjectHistorySnapshot(Day1, Fingerprint, 4, fragments)).Search("división entre cero", 10);

        Assert.Equal([DivideByZero.Id], Ids(search));
        Assert.False(search.Widened);
    }

    [Fact]
    public void A_vector_that_is_not_finite_is_ignored()
    {
        var broken = new float[] { float.NaN, 0, 0, 0 };
        var fragments = Fragments.Select(f => f.Id == DivideByZero.Id ? f with { Vector = broken } : f).ToList();

        var search = new HistoryRanking(new ProjectHistorySnapshot(Day1, Fingerprint, 4, fragments)).Search("división entre cero", 10);

        Assert.Equal([DivideByZero.Id], Ids(search));
        Assert.False(search.Widened);
    }

    [Fact]
    public void Ties_go_to_the_newest_fragment_and_then_to_the_ordinal_id()
    {
        const string Text = "La caché de la compilación se vacía al cambiar de rama.";
        var snapshot = new ProjectHistorySnapshot(Day1, null, 0,
        [
            new HistoryFragment("mem:b#0", "decisión", Day1, Text),
            new HistoryFragment("mem:a#0", "decisión", Day1, Text),
            new HistoryFragment("mem:c#0", "decisión", Day1.AddDays(1), Text),
        ]);

        var search = new HistoryRanking(snapshot).Search("caché compilación", 10);

        Assert.Equal(["mem:c#0", "mem:a#0", "mem:b#0"], Ids(search));
    }

    [Fact]
    public void The_order_does_not_depend_on_the_order_of_the_snapshot_nor_on_the_run()
    {
        var shuffled = new ProjectHistorySnapshot(Day1, Fingerprint, 4, [.. Fragments.OrderBy(f => f.Id, StringComparer.Ordinal)]);
        var first = new HistoryRanking(WithVectors()).Search("pruebas cero cumplido", 5);

        var again = new HistoryRanking(WithVectors()).Search("pruebas cero cumplido", 5);
        var other = new HistoryRanking(shuffled).Search("pruebas cero cumplido", 5);

        Assert.Equal(Ids(first), Ids(again));
        Assert.Equal(Ids(first), Ids(other));
        Assert.Equal(first.Matches.Select(m => m.Rank.Score), other.Matches.Select(m => m.Rank.Score));
    }

    [Fact]
    public void Top_cuts_the_list_but_the_word_matches_count_every_one()
    {
        var search = new HistoryRanking(WithoutVectors()).Search("pruebas cumplido", 1);

        Assert.Equal([Verdict.Id], Ids(search));
        Assert.Equal(2, search.WordMatches);
    }
}
