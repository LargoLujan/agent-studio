using System.Globalization;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Docs;
using AgentStudio.Desktop.Localization;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.Docs;

/// <summary>
/// ADR-0054 on «Diagramas» and «Discrepancias» (WP-D): every text comes from <c>DocsStrings</c> in both languages, every key the screens name exists, each ⓘ explains its
/// section in short sentences, every code and every state of the layers below has a text of its own, and the line of Auto Doctor has its texts in each state it takes.
/// </summary>
public sealed class DocsStringsTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    private static readonly string[] Prefixes = ["Docs_", "Info_Docs"];

    [Fact]
    public void Every_key_the_screens_name_exists_in_both_languages()
    {
        var sources = AreaStringsChecks.Sources(["Docs"], ["Docs"]);

        Assert.NotEmpty(sources);
        Assert.Empty(AreaStringsChecks.MissingKeys(sources, Prefixes));
    }

    [Fact]
    public void The_english_has_no_letter_or_sign_only_spanish_uses()
    {
        Assert.Empty(AreaStringsChecks.SpanishInEnglish("DocsStrings"));
    }

    [Fact]
    public void Every_section_has_its_information_button_in_both_languages_with_short_sentences()
    {
        var diagrams = new DocsDiagramsSection(DocsBackend.None);
        var discrepancies = new DocsDiscrepanciesSection(DocsBackend.None);

        Assert.Equal("Info_Docs_Diagrams", diagrams.InfoKey);
        Assert.Equal("Info_Docs_Discrepancies", discrepancies.InfoKey);
        Assert.Empty(AreaStringsChecks.InfoProblems([diagrams.InfoKey, discrepancies.InfoKey]));
        Assert.All([diagrams.Title, discrepancies.Title], title => Assert.False(string.IsNullOrWhiteSpace(title) || title.StartsWith('[')));
    }

    [Fact]
    public void No_long_sentence_in_the_area_in_either_language()
    {
        Assert.Empty(AreaStringsChecks.KeysWithLongSentences("DocsStrings", new HashSet<string>()));
    }

    [Fact]
    public void Every_code_state_and_kind_the_layers_below_give_has_a_text_of_its_own_in_both_languages()
    {
        var missing = new List<string>();
        void Need(string key)
        {
            foreach (var (name, provider) in new[] { ("es", Spanish), ("en", English) })
            {
                if (!provider.Has(key))
                {
                    missing.Add($"{name}: {key}");
                }
            }
        }

        foreach (var kind in Enum.GetValues<DiagramKind>())
        {
            Need("Docs_Kind_" + kind);
        }

        foreach (var state in Enum.GetValues<DiagramState>())
        {
            Need("Docs_State_" + state);
        }

        foreach (var state in Enum.GetValues<DiagramExportState>())
        {
            Need("Docs_Export_" + state);
        }

        foreach (var code in ConsistencyCodes.All.Append(ConsistencyCodes.CheckFailed))
        {
            Need(UserText.KeyOf(code));
        }

        foreach (var reason in new[] { "missing", "outside", "case" })
        {
            Need(UserText.KeyOf(ConsistencyCodes.LinkBroken) + "_" + reason);
        }

        foreach (var code in new[] { ConsistencyCodes.AdrCitesMissing, ConsistencyCodes.DiagramStale, ConsistencyCodes.RequirementUncovered, ConsistencyCodes.CheckFailed })
        {
            Need(UserText.KeyOf(code) + "_Line");
        }

        // The line of Auto Doctor: its title, its three states and the sentence of its fix.
        foreach (var key in new[] { "Doctor_Check_project_discrepancies_Title", "Doctor_Check_project_discrepancies_Ok", "Doctor_Check_project_discrepancies_Warning", "Doctor_Check_project_discrepancies_Unknown", "Doctor_Fix_open_graph" })
        {
            Need(key);
        }

        // The words for why something failed: by kind of failure, never the message of an exception.
        foreach (var kind in new[] { "Busy", "Log", "Index", "Io", "Access", "Model" })
        {
            Need("Docs_Failure_" + kind);
        }

        Assert.True(missing.Count == 0, "Values with no text: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_sentences_of_the_findings_keep_their_placeholders_in_both_languages()
    {
        foreach (var provider in new IUiTextProvider[] { Spanish, English })
        {
            Assert.Contains("{0}", provider.Get(UserText.KeyOf(ConsistencyCodes.LinkBroken) + "_missing"), StringComparison.Ordinal);
            Assert.Contains("{1}", provider.Get(UserText.KeyOf(ConsistencyCodes.LinkBroken) + "_case"), StringComparison.Ordinal);
            Assert.Contains("{0}", provider.Get(UserText.KeyOf(ConsistencyCodes.AdrCitesMissing) + "_Line"), StringComparison.Ordinal);
            Assert.Contains("{2}", provider.Get(UserText.KeyOf(ConsistencyCodes.RequirementUncovered) + "_Line"), StringComparison.Ordinal);
            Assert.Contains("{0}", provider.Get("Doctor_Check_project_discrepancies_Warning"), StringComparison.Ordinal);
        }
    }
}
