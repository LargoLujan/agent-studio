namespace AgentStudio.Tests.Desktop.Texts;

/// <summary>The syllable counter and the INFLESZ index of the guide of texts (D-53), checked on words and texts whose count is known.</summary>
public sealed class SpanishReadabilityTests
{
    [Theory]
    [InlineData("sol", 1)]
    [InlineData("casa", 2)]
    [InlineData("cuidado", 3)]
    [InlineData("aire", 2)]
    [InlineData("agua", 2)]
    [InlineData("ciudad", 2)]
    [InlineData("reunión", 2)]
    [InlineData("estudiáis", 3)]
    [InlineData("buey", 1)]
    [InlineData("hay", 1)]
    [InlineData("muy", 1)]
    [InlineData("y", 1)]
    [InlineData("país", 2)]
    [InlineData("día", 2)]
    [InlineData("río", 2)]
    [InlineData("oído", 3)]
    [InlineData("leer", 2)]
    [InlineData("teatro", 3)]
    [InlineData("paraguas", 3)]
    [InlineData("queso", 2)]
    [InlineData("guerra", 2)]
    [InlineData("pingüino", 3)]
    [InlineData("ejecución", 4)]
    [InlineData("aprobar", 3)]
    [InlineData("herramienta", 4)]
    [InlineData("Agent", 2)]
    [InlineData("12", 2)]
    public void A_spanish_word_has_the_syllables_it_is_known_to_have(string word, int syllables) =>
        Assert.Equal(syllables, SpanishReadability.CountSyllables(word));

    [Fact]
    public void A_sentence_ends_at_a_stop_or_a_new_line_and_a_placeholder_is_a_word()
    {
        var sentences = SpanishReadability.Sentences("Abre el plan. ¿Lo quieres? Sí\nHay {0} pruebas");
        Assert.Equal(["Abre el plan.", "¿Lo quieres?", "Sí", "Hay dato pruebas"], sentences);
        Assert.Equal(3, SpanishReadability.Words("Hay {0} pruebas".Replace("{0}", "dato")).Count);
        Assert.Empty(SpanishReadability.Words("· → —"));
    }

    [Fact]
    public void The_index_is_the_formula_of_szigriszt_pazos()
    {
        // 4 words, 7 syllables (la ca-sa es bo-ni-ta), one sentence.
        var index = SpanishReadability.Inflesz("La casa es bonita.")!.Value;
        Assert.Equal(206.835 - (62.3 * 7 / 4) - 4, index, 3);
        Assert.Null(SpanishReadability.Inflesz("· · ·"));
    }

    [Fact]
    public void A_plain_text_scores_normal_and_a_dense_one_scores_low()
    {
        var plain = SpanishReadability.Inflesz("Abre el plan. Mira lo que cambia. Luego elige si lo apruebas.")!.Value;
        var dense = SpanishReadability.Inflesz(
            "La reconstrucción retrospectiva de la configuración experimental determinada anteriormente imposibilita cualquier comprobación automatizada.")!.Value;
        Assert.True(plain >= SpanishReadability.NormalLevel, plain.ToString());
        Assert.True(dense < 40, dense.ToString());
    }

    [Fact]
    public void The_longest_sentence_counts_its_words()
    {
        var (words, sentence) = SpanishReadability.LongestSentence("Uno dos. Uno dos tres cuatro.\nUno");
        Assert.Equal(4, words);
        Assert.Equal("Uno dos tres cuatro.", sentence);
    }
}
