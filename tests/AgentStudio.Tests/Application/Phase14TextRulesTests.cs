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
    [InlineData(@"Abre «C:\Users\ana\repo\src\App.cs» y mira", @"C:\Users\ana\repo\src\App.cs")]
    [InlineData("Está en D:/trabajo/tienda.", "D:/trabajo/tienda")]
    [InlineData(@"Ruta de red \\servidor\compartida\datos, ahí.", @"\\servidor\compartida\datos")]
    [InlineData("Mira ~/repos/tienda, la copia", "~/repos/tienda")]
    [InlineData(@"Mira ~\repos\tienda", @"~\repos\tienda")]
    [InlineData(@"Abre C:\Personal Projects\Agent Studio\src\App.cs", @"C:\Personal Projects\Agent Studio\src\App.cs")]
    [InlineData(@"Es C:\Users\Ana Garcia\Documentos\plan final.docx. Luego", @"C:\Users\Ana Garcia\Documentos\plan final.docx")]
    [InlineData(@"En \\servidor\mi recurso\datos\x.txt, sí", @"\\servidor\mi recurso\datos\x.txt")]
    [InlineData("Ver file:///C:/Users/ana/mi proyecto/x.md.", "file:///C:/Users/ana/mi proyecto/x.md")]
    [InlineData("Ver file:///home/ana/x y más", "file:///home/ana/x y más")]
    [InlineData(@"Está en C:\Program Files (x86)\App\a.exe", @"C:\Program Files (x86)\App\a.exe")]
    [InlineData(@"(ver C:\Users\ana\x)", @"C:\Users\ana\x")]
    [InlineData("Dice \"C:\\Mi Carpeta\\a b.txt\" y más", @"C:\Mi Carpeta\a b.txt")]
    [InlineData(@"Es C:\Users\O'Brien\x, no", @"C:\Users\O'Brien\x")]
    [InlineData("Primera C:\\a b\nsegunda línea", @"C:\a b")]
    public void A_path_of_the_machine_is_found_whole_spaces_included_up_to_a_clear_delimiter(string text, string path)
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
    [InlineData("Una ruta de Unix /home/ana/x no se detecta")]
    public void A_relative_path_a_url_and_an_id_are_not_paths_of_the_machine(string text)
    {
        Assert.Empty(LocalPaths.Find(text));
        Assert.False(LocalPaths.Contains(text));
    }

    [Fact]
    public void Nothing_is_found_in_a_missing_text_and_every_path_is_found_in_order_each_ending_where_the_next_begins()
    {
        Assert.Empty(LocalPaths.Find(null));
        Assert.Equal(
            [@"C:\repo", @"\\srv\share", "~/x"],
            LocalPaths.Find(@"Primero «C:\repo», luego «\\srv\share» y por último ~/x."));
        Assert.Equal([@"C:\repo uno", "~/x"], LocalPaths.Find(@"Primero C:\repo uno ~/x."));
    }

    [Theory]
    [InlineData(@"Abre C:\Personal Projects\Agent Studio\src\App.cs", "Abre [ruta]")]
    [InlineData(@"Es C:\Users\Ana Garcia\Documentos\x.docx, y ya", "Es [ruta], y ya")]
    [InlineData(@"En \\servidor\mi recurso\datos\x.txt.", "En [ruta].")]
    [InlineData("Ver file:///C:/Users/ana/mi proyecto/x.md.", "Ver [ruta].")]
    [InlineData(@"Dice «C:\Mi Carpeta\a b.txt» y más", "Dice «[ruta]» y más")]
    [InlineData(@"Es C:\Program Files (x86)\App\a.exe", "Es [ruta]")]
    public void A_path_with_spaces_is_removed_whole_never_half(string text, string expected)
    {
        var (clean, removed) = LocalPaths.Remove(text, "[ruta]");

        Assert.Equal(expected, clean);
        Assert.Equal(1, removed);
        Assert.False(LocalPaths.Contains(clean));
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
