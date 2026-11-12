using System.Globalization;
using AgentStudio.Domain.Metrics;
using static AgentStudio.Tests.Desktop.Localization.AreaStringsChecks;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The texts of the figures, the context bars, the quota of the plan and their settings (docs/17 D-47, D-51 and D-54; F14-1,
/// F14-3, F14-5 and F14-6; ADR-0054): every key the screens name exists in Spanish of Spain and in English, the English is
/// English, the plurals have both forms, each ⓘ of these screens has its three texts in short sentences, and the texts say «límite
/// de uso» and «programa del agente», never «cuota» or «CLI», as the glossary of the guide asks.
/// </summary>
public sealed class ObservabilityStringsTests
{
    private static readonly string[] Prefixes = ["Observability_", "Info_"];

    private static readonly string[] InfoSections = ["Info_Quota", "Info_ContextSettings"];

    private static IReadOnlyList<string> ScreenFiles() =>
        Sources(["Observability"], ["Observability"]).Where(f => !f.EndsWith("UsageDashboardViewModel.cs", StringComparison.Ordinal) && !f.EndsWith("UsageDashboardView.xaml", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Every_key_the_observability_screens_name_exists_in_both_languages()
    {
        var sources = ScreenFiles();
        Assert.True(sources.Count >= 6, "The screens of observability were not found.");
        var missing = MissingKeys(sources, Prefixes);
        Assert.True(missing.Count == 0, "Keys that are not in both languages:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Every_origin_of_a_figure_has_its_text_in_both_languages()
    {
        foreach (var name in Enum.GetNames<MetricOrigin>())
        {
            foreach (var provider in new[] { Spanish, English })
            {
                Assert.True(provider.Has("Observability_Origin_" + name), "Observability_Origin_" + name);
                Assert.False(provider.Get("Observability_Origin_" + name).StartsWith('['), name);
            }
        }

        Assert.Equal("exacto · informado por el programa del agente", Spanish.Get("Observability_Origin_ReportedByProvider"));
        Assert.Equal("exact · reported by the agent program", English.Get("Observability_Origin_ReportedByProvider"));
    }

    [Fact]
    public void A_plural_has_its_one_and_its_many_in_both_languages_with_the_number_in_place()
    {
        foreach (var provider in new[] { Spanish, English })
        {
            Assert.Contains("1", provider.Format("Observability_Bar_RunsUsed_One", "1"), StringComparison.Ordinal);
            Assert.Contains("3", provider.Format("Observability_Bar_RunsUsed_Other", "3"), StringComparison.Ordinal);
        }

        Assert.NotEqual(Spanish.Format("Observability_Bar_RunsUsed_One", 1), Spanish.Format("Observability_Bar_RunsUsed_Other", 1));
        Assert.Equal("3 ejecuciones de agente usadas", Spanish.Format("Observability_Bar_RunsUsed_Other", 3));
        Assert.Equal("1 agent run used", English.Format("Observability_Bar_RunsUsed_One", 1));
    }

    /// <summary>The date of a reset is written the way each language writes it: day first in Spanish, month first in English.</summary>
    [Fact]
    public void The_date_of_a_reset_follows_the_language()
    {
        var day = new DateTime(2026, 10, 30, 19, 20, 0, DateTimeKind.Unspecified);
        Assert.Equal("30/10", day.ToString(Spanish.Get("Observability_Clock_DateFormat"), CultureInfo.InvariantCulture));
        Assert.Equal("10/30", day.ToString(English.Get("Observability_Clock_DateFormat"), CultureInfo.InvariantCulture));
        Assert.Equal("el 30/10 a las 19:20", Spanish.Format("Observability_Clock_OnDate", "30/10", "19:20"));
        Assert.Equal("on 10/30 at 19:20", English.Format("Observability_Clock_OnDate", "10/30", "19:20"));
    }

    [Fact]
    public void The_english_texts_of_observability_have_no_spanish_letters_or_signs()
    {
        var untranslated = SpanishInEnglish("ObservabilityStrings");
        Assert.True(untranslated.Count == 0, "English texts with Spanish letters or signs:\n" + string.Join('\n', untranslated));
    }

    [Fact]
    public void Each_information_button_of_these_screens_has_its_three_texts_in_short_sentences()
    {
        var problems = InfoProblems(InfoSections);
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void The_screens_use_their_information_button_and_each_one_has_its_texts()
    {
        Assert.Equal(["Info_ContextSettings"], InfoKeysOf("Observability/ObservabilitySettingsView.xaml"));
        Assert.Equal(["Info_Quota"], InfoKeysOf("Observability/QuotaView.xaml"));
    }

    /// <summary>The guide asks for 20 words a sentence at most (D-52): none of the texts of this area has a longer one.</summary>
    [Fact]
    public void No_text_of_observability_has_a_sentence_of_more_than_20_words()
    {
        var tooLong = KeysWithLongSentences("ObservabilityStrings", new HashSet<string>());
        Assert.True(tooLong.Count == 0, "Sentences with more than 20 words:\n" + string.Join('\n', tooLong));
    }

    /// <summary>The glossary of the guide (D-52): the quota is «límite de uso», and the CLI is «programa del agente».</summary>
    [Fact]
    public void The_texts_use_the_glossary_of_the_guide()
    {
        var offenders = new List<string>();
        foreach (var (key, text) in Read(ResourceFiles()["ObservabilityStrings"].Neutral))
        {
            if (text.Contains("cuota", StringComparison.OrdinalIgnoreCase) || text.Contains("CLI", StringComparison.Ordinal) && !text.Contains("Codex CLI", StringComparison.Ordinal) && !text.Contains("Gemini CLI", StringComparison.Ordinal))
            {
                offenders.Add($"{key}: {text}");
            }
        }

        Assert.True(offenders.Count == 0, "Texts that say «cuota» or «CLI»:\n" + string.Join('\n', offenders));
    }
}
