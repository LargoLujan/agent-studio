using AgentStudio.Domain.Semantic;

namespace AgentStudio.Tests.Domain.Semantic;

/// <summary>
/// The terms of the lexical list of the hybrid search (docs/17 D-05; ADR-0047): function words out, folded like
/// unicode61, each term once and at most 32. The spike showed that with them the fusion loses against the vectors alone.
/// </summary>
public sealed class LexicalTermsTests
{
    [Fact]
    public void Function_words_go_whatever_their_case_accents_or_punctuation()
    {
        Assert.Equal(["permisos", "planificador?"], LexicalTerms.From("¿Qué permisos tiene el planificador?"));
        Assert.Equal(["PRUEBAS", "fallan"], LexicalTerms.From("Las PRUEBAS que fallan"));
        Assert.Equal(["race", "condition", "componentes"], LexicalTerms.From("race condition entre dos componentes"));
        Assert.True(LexicalTerms.IsFunctionWord("Cómo"));
        Assert.True(LexicalTerms.IsFunctionWord("¿Dónde"));
        Assert.True(LexicalTerms.IsFunctionWord("THE"));
        Assert.False(LexicalTerms.IsFunctionWord("bitácora"));
    }

    [Fact]
    public void Terms_keep_their_order_and_come_once_and_terms_without_letters_go()
    {
        Assert.Equal(["Bitácora", "índice", "12"], LexicalTerms.From("Bitácora · índice — bitacora 12 ¿? ... Indice"));
    }

    [Fact]
    public void At_most_32_terms_go_and_a_text_left_with_none_gives_none()
    {
        var many = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"termino{i}"));

        Assert.Equal(32, LexicalTerms.From(many).Count);
        Assert.Equal("termino31", LexicalTerms.From(many)[^1]);
        Assert.Empty(LexicalTerms.From("de la que el"));
        Assert.Empty(LexicalTerms.From("   "));
        Assert.Empty(LexicalTerms.From(null));
    }
}
