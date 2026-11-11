using AgentStudio.Application.Localization;
using AgentStudio.Application.Security;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/17 §5.4 and §5.5 (P0 of Phase 14): the three small rules the help and the documents for clients share. What counts as a
/// path of the person's machine (D-57 and D-60), which characters a text for the screen or an agent must not carry, and the shape of
/// the name of a language (D-49 and D-59).
/// </summary>
public sealed class Phase14TextRulesTests
{
    // ---- LocalPaths (D-57 and D-60) ----

    [Theory]
    [InlineData(@"Abre C:\Users\ana\repo\src\App.cs y mira", @"C:\Users\ana\repo\src\App.cs")]
    [InlineData("Está en D:/trabajo/tienda.", "D:/trabajo/tienda")]
    [InlineData(@"Ruta de red \\servidor\compartida\datos, ahí.", @"\\servidor\compartida\datos")]
    [InlineData("Mira ~/repos/tienda (la copia)", "~/repos/tienda")]
    [InlineData(@"Mira ~\repos\tienda", @"~\repos\tienda")]
    public void A_path_of_the_machine_is_found_without_the_punctuation_that_closes_the_sentence(string text, string path)
    {
        Assert.Equal([path], LocalPaths.Find(text));
        Assert.True(LocalPaths.Contains(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sin ninguna ruta")]
    [InlineData("src/AgentStudio.Desktop/App.cs es relativa")]
    [InlineData("Mira https://example.com/a/b y http://localhost:5000/x")]
    [InlineData("El intento T3:2 y la clave a:b no son rutas")]
    [InlineData("Un id como ev-1:/x tampoco")]
    [InlineData("100:/50 es una proporción")]
    public void A_relative_path_a_url_and_an_id_are_not_paths_of_the_machine(string text)
    {
        Assert.Empty(LocalPaths.Find(text));
        Assert.False(LocalPaths.Contains(text));
    }

    [Fact]
    public void Nothing_is_found_in_a_missing_text_and_every_path_is_found_in_order()
    {
        Assert.Empty(LocalPaths.Find(null));
        Assert.Equal(
            [@"C:\repo", @"\\srv\share", "~/x"],
            LocalPaths.Find(@"Primero C:\repo, luego \\srv\share y por último ~/x."));
    }

    [Fact]
    public void Removing_the_paths_replaces_each_occurrence_keeps_the_punctuation_and_counts_them()
    {
        var (text, removed) = LocalPaths.Remove(@"Ver C:\Users\ana\repo. Y otra vez C:\Users\ana\repo, más ~/x)", "[ruta]");

        Assert.Equal("Ver [ruta]. Y otra vez [ruta], más [ruta])", text);
        Assert.Equal(3, removed);
        Assert.Equal(("sin rutas", 0), LocalPaths.Remove("sin rutas", "[ruta]"));
        Assert.Equal((string.Empty, 0), LocalPaths.Remove(null, "[ruta]"));
        Assert.Throws<ArgumentNullException>(() => LocalPaths.Remove("x", null!));
    }

    // ---- PlainText ----

    [Fact]
    public void Control_and_bidi_characters_are_hidden_and_a_line_break_is_allowed_only_when_asked()
    {
        Assert.False(PlainText.HasHiddenCharacter(null, allowLineBreaks: false));
        Assert.False(PlainText.HasHiddenCharacter("Texto normal, con ñ y tildes: áéíóú.", allowLineBreaks: false));
        Assert.True(PlainText.HasHiddenCharacter("a\u0007b", allowLineBreaks: true));
        Assert.True(PlainText.HasHiddenCharacter("a\u202Eb", allowLineBreaks: true));
        Assert.True(PlainText.HasHiddenCharacter("a\u2028b", allowLineBreaks: true));
        Assert.True(PlainText.HasHiddenCharacter("a\nb", allowLineBreaks: false));
        Assert.False(PlainText.HasHiddenCharacter("a\nb\r\n\tc", allowLineBreaks: true));
    }

    [Fact]
    public void Stripping_removes_what_is_hidden_and_turns_a_line_break_into_a_space_unless_it_is_kept()
    {
        Assert.Equal(string.Empty, PlainText.Strip(null, keepLineBreaks: false));
        Assert.Equal("ab", PlainText.Strip("a\u202E\u0007b", keepLineBreaks: false));
        Assert.Equal("una dos", PlainText.Strip("una\ndos", keepLineBreaks: false));
        Assert.Equal("una\ndos", PlainText.Strip("una\r\ndos", keepLineBreaks: true));
        Assert.Equal("a b", PlainText.Strip("a\tb", keepLineBreaks: true));
    }

    // ---- LanguageTags ----

    [Theory]
    [InlineData("es-ES", true)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("zh-Hans-CN", true)]
    [InlineData("fil", true)]
    [InlineData("", false)]
    [InlineData("e", false)]
    [InlineData("english", false)]
    [InlineData("es_ES", false)]
    [InlineData("es-", false)]
    [InlineData("C:\\idioma", false)]
    [InlineData("es-ES-un-subtag-demasiado-largo", false)]
    public void A_language_is_the_name_of_a_culture(string tag, bool valid)
    {
        Assert.Equal(valid, LanguageTags.IsValid(tag));
    }

    [Fact]
    public void A_missing_language_is_not_valid()
    {
        Assert.False(LanguageTags.IsValid(null));
    }
}
