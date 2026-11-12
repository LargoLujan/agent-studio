using System.Globalization;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Graph;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.Graph;

/// <summary>
/// ADR-0054 on «Auto Graph» (WP-G): every text comes from <c>GraphStrings</c> in both languages, every key the screens name exists, each ⓘ explains its section in
/// short sentences, and every code or kind of the layers below that the screen says has a text of its own (no gap, node or relation falls back to its English
/// name or to <c>[Key]</c>).
/// </summary>
public sealed class GraphStringsTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    private static readonly string[] Prefixes = ["Graph_", "Info_Graph"];

    [Fact]
    public void Every_key_the_screens_name_exists_in_both_languages()
    {
        var sources = AreaStringsChecks.Sources(["Graph"], ["Graph"]);

        Assert.NotEmpty(sources);
        Assert.Empty(AreaStringsChecks.MissingKeys(sources, Prefixes));
    }

    [Fact]
    public void The_english_has_no_letter_or_sign_only_spanish_uses()
    {
        Assert.Empty(AreaStringsChecks.SpanishInEnglish("GraphStrings"));
    }

    [Fact]
    public void Every_section_and_part_has_its_information_button_in_both_languages_with_short_sentences()
    {
        var section = new GraphMapSection(GraphBackend.None);
        string[] keys = ["Info_Graph", section.InfoKey, "Info_Graph_Coverage", "Info_Graph_Propose"];

        Assert.Equal("Info_Graph_Map", section.InfoKey);
        Assert.Empty(AreaStringsChecks.InfoProblems(keys));
        Assert.False(string.IsNullOrWhiteSpace(section.Title) || section.Title.StartsWith('['));
    }

    [Fact]
    public void No_long_sentence_in_the_area_in_either_language()
    {
        Assert.Empty(AreaStringsChecks.KeysWithLongSentences("GraphStrings", new HashSet<string>()));
    }

    [Fact]
    public void Every_value_the_layers_below_give_has_a_text_of_its_own_in_both_languages()
    {
        var missing = new List<string>();
        void Check<T>(string prefix)
            where T : struct, Enum
        {
            foreach (var value in Enum.GetValues<T>())
            {
                foreach (var (name, provider) in new[] { ("es", Spanish), ("en", English) })
                {
                    if (!provider.Has(prefix + value))
                    {
                        missing.Add($"{name}: {prefix}{value}");
                    }
                }
            }
        }

        Check<GraphNodeKind>("Graph_Kind_");
        Check<GraphEdgeKind>("Graph_Edge_");
        foreach (var code in GraphGapCodes.All)
        {
            foreach (var (name, provider) in new[] { ("es", Spanish), ("en", English) })
            {
                if (!provider.Has(UserText.KeyOf(code)))
                {
                    missing.Add($"{name}: {UserText.KeyOf(code)}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Values with no text: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_text_of_a_gap_is_one_sentence_of_the_screen_and_never_quotes_a_path()
    {
        foreach (var code in GraphGapCodes.All)
        {
            foreach (var provider in new[] { Spanish, English })
            {
                var text = provider.Render(UserText.Of(code));

                Assert.False(string.IsNullOrWhiteSpace(text) || text.StartsWith('['), code);
                Assert.DoesNotContain(".cs", text, StringComparison.Ordinal);
                Assert.DoesNotContain("..", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_label_of_the_relations_of_an_ai_and_its_word_for_the_screen_reader_are_in_both_languages()
    {
        Assert.Equal("IA", Spanish.Get("Graph_Wire_Label_Inferred"));
        Assert.Equal("AI", English.Get("Graph_Wire_Label_Inferred"));
        Assert.Contains("propuesta por", Spanish.Get("Graph_Wire_NameInferred"), StringComparison.Ordinal);
        Assert.Contains("proposed by", English.Get("Graph_Wire_NameInferred"), StringComparison.Ordinal);
    }
}
