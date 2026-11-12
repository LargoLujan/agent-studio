using AgentStudio.Desktop.Help;
using AgentStudio.Tests.Desktop.TextsGuide;

namespace AgentStudio.Tests.Desktop.Help;

/// <summary>
/// P59 (ADR-0056 addendum): the search of the help knows the technical words of the glossary of the guide of texts (ADR-0054 point 9),
/// because the articles say «límite de uso» and not «cuota». The synonyms live in <see cref="HelpSynonyms"/>, and the query of the
/// person stays data.
/// </summary>
public sealed class HelpSynonymsTests
{
    private static readonly HelpCorpus Corpus = HelpCorpus.FromAssembly();

    private static async Task<IReadOnlyList<string>> IdsAsync(HelpSearch search, string query, string language) =>
        [.. (await search.SearchAsync(query, language, 10, TestContext.Current.CancellationToken)).Select(h => h.ArticleId)];

    [Fact]
    public void The_table_covers_every_term_of_the_glossary_of_the_guide_of_texts()
    {
        var words = HelpSynonyms.Words();
        var names = TextGuide.Jargon.Select(t => t.Name)
            .Concat(TextGuide.Glossary.Select(g => g.Name))
            .Append(TextGuide.Commit.Name)
            .Where(n => !HelpSynonyms.NeverShown.Contains(n, StringComparer.Ordinal))
            .ToList();

        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(HelpSynonyms.Fold);
            Assert.True(parts.Any(words.Contains), $"The glossary term «{name}» has no synonym in HelpSynonyms.");
        }

        // The names the guide says do not appear at all have no synonym, and the guide still lists them.
        var listed = TextGuide.Jargon.Select(t => t.Name);
        Assert.All(HelpSynonyms.NeverShown, n => Assert.Contains(n, listed));
        Assert.All(HelpSynonyms.NeverShown, n => Assert.Empty(HelpSynonyms.For(n, english: false)));
    }

    [Fact]
    public async Task A_technical_word_finds_the_articles_that_say_it_in_plain_words_in_both_languages()
    {
        using var search = new HelpSearch(Corpus);

        Assert.Contains("consumption", await IdsAsync(search, "cuota", "es-ES"));
        Assert.Contains("consumption", await IdsAsync(search, "quota", "en"));
        Assert.Contains("consumption", await IdsAsync(search, "cuotas", "es-ES"));
        Assert.Contains("work", await IdsAsync(search, "evidencia", "es-ES"));
        Assert.Contains("work", await IdsAsync(search, "evidence", "en"));
        Assert.Contains("repository", await IdsAsync(search, "commit", "es-ES"));
        Assert.Contains("repository", await IdsAsync(search, "commit", "en"));
        Assert.Contains("monitor", await IdsAsync(search, "ventana", "es-ES"));
        Assert.Contains("settings", await IdsAsync(search, "contexto", "es-ES"));
        Assert.Contains("skills", await IdsAsync(search, "context window", "en"));
    }

    [Fact]
    public void The_query_adds_the_plain_words_of_the_term_in_the_language_of_the_index_and_nothing_else()
    {
        var spanish = HelpSearch.MatchOf("CLI", english: false);
        var english = HelpSearch.MatchOf("CLI", english: true);

        Assert.Equal("\"CLI\"* OR \"programa del agente\"*", spanish);
        Assert.Equal("\"CLI\"* OR \"agent program\"*", english);
        Assert.Equal("\"cuota\"* OR \"límite de uso\"*", HelpSearch.MatchOf("cuota"));
        Assert.Equal("\"quota\"* OR \"usage limit\"*", HelpSearch.MatchOf("quota", english: true));
    }

    [Fact]
    public async Task A_word_without_synonym_searches_as_before()
    {
        using var search = new HelpSearch(Corpus);

        Assert.Equal("\"auditoria\"*", HelpSearch.MatchOf("auditoria"));
        Assert.Equal("\"zzzqqq\"*", HelpSearch.MatchOf("zzzqqq", english: true));
        Assert.Empty(HelpSynonyms.For("auditoria", english: false));
        Assert.Contains("audit", await IdsAsync(search, "auditoria", "es-ES"));
        Assert.Empty(await IdsAsync(search, "zzzqqq", "es-ES"));
    }

    [Fact]
    public async Task The_characters_of_fts5_in_the_query_stay_data_with_the_synonyms_too()
    {
        using var search = new HelpSearch(Corpus);

        var match = HelpSearch.MatchOf("\"cuota\" OR NEAR(evidencia) * title:cuota ^x \u0001")!;
        // Every term is a quoted phrase (quotes doubled) with its prefix mark, so the quotes always balance.
        Assert.Equal(0, match.Count(c => c == '"') % 2);
        Assert.Contains("\"límite de uso\"*", match, StringComparison.Ordinal);
        Assert.StartsWith("\"\"\"cuota\"\"\"*", match, StringComparison.Ordinal);
        Assert.Null(HelpSearch.MatchOf("\u0001 * \" "));

        var ct = TestContext.Current.CancellationToken;
        _ = await search.SearchAsync("\"cuota\" OR NEAR(evidencia, 2) * title:cuota ^x", "es-ES", 5, ct);
        _ = await search.SearchAsync("quota\" ) OR (", "en", 5, ct);
        _ = await search.SearchAsync("\uD800cuota", "es-ES", 5, ct);
    }
}
