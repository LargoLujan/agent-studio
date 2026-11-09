using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Memory;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Localization;

/// <summary>
/// The foundations of the languages (docs/17 D-47, D-49 and D-51; F13-19; ADR-0054): every key of the resources exists in each
/// language with the same placeholders and no key more; a key lives in one file; the texts the interface reads come from the
/// resources in Spanish of Spain and in English; and the language is the one of Windows when Agent Studio has it, English when
/// it does not.
/// </summary>
public sealed partial class LocalizationResourcesTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static IReadOnlyList<string> PlaceholdersOf(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal)];

    // ---- the files (D-51: coverage of keys) ----

    [Fact]
    public void Every_file_of_texts_has_every_language_of_the_list_with_the_same_keys_and_the_same_placeholders()
    {
        var files = ResourceFiles();
        Assert.Contains("Strings", files.Keys);
        var languages = UiLanguages.All.Where(l => l.CultureName != UiLanguages.SourceCulture).Select(l => l.CultureName).ToList();

        foreach (var (baseName, (neutralFile, satellites)) in files)
        {
            var neutral = Read(neutralFile);
            Assert.NotEmpty(neutral);
            Assert.All(neutral, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"{baseName}: '{pair.Key}' is empty."));
            foreach (var language in languages)
            {
                Assert.True(satellites.ContainsKey(language), $"{baseName} has no file for '{language}'.");
                var translated = Read(satellites[language]);
                Assert.Equal(neutral.Keys.Order(StringComparer.Ordinal), translated.Keys.Order(StringComparer.Ordinal));
                foreach (var (key, text) in neutral)
                {
                    Assert.False(string.IsNullOrWhiteSpace(translated[key]), $"{baseName}.{language}: '{key}' is empty.");
                    Assert.Equal(PlaceholdersOf(text), PlaceholdersOf(translated[key]));
                }
            }

            Assert.All(satellites.Keys, culture => Assert.Contains(culture, languages, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void A_key_lives_in_one_file_of_texts_and_the_provider_reads_every_file()
    {
        var files = ResourceFiles();
        var keys = files.Values.SelectMany(f => Read(f.Neutral).Keys).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            files.Keys.Select(b => ResourceTextProvider.ResourcePrefix + b).Order(StringComparer.Ordinal),
            ResourceTextProvider.BaseNames.Order(StringComparer.Ordinal));
        Assert.Equal(ResourceTextProvider.ResourcePrefix + "Strings", ResourceTextProvider.BaseNames[0]);
    }

    // ---- the texts at run time (F13-19) ----

    [Fact]
    public void The_texts_come_from_the_resources_in_spanish_of_spain_and_in_english()
    {
        Assert.Equal("Qué es esto", Spanish.Get("Info_WhatIsThis"));
        Assert.Equal("What this is", English.Get("Info_WhatIsThis"));
        Assert.Equal("Información sobre Idioma", Spanish.Format("Info_Button_Name", Spanish.Get("Info_Language_Title")));
        Assert.Equal("Information about Language", English.Format("Info_Button_Name", English.Get("Info_Language_Title")));
        Assert.Equal("Qué es esto", AgentStudio.Desktop.Resources.Strings.ResourceManager.GetString("Info_WhatIsThis", CultureInfo.GetCultureInfo("es-ES")));

        foreach (var key in Read(ResourceFiles()["Strings"].Neutral).Keys)
        {
            Assert.True(Spanish.Has(key), key);
            Assert.False(Spanish.Get(key).StartsWith('['), key);
            Assert.False(English.Get(key).StartsWith('['), key);
        }
    }

    /// <summary>A text missing in a language falls back to the source language; a key that does not exist is never an empty text.</summary>
    [Fact]
    public void A_language_without_its_own_resources_reads_the_source_and_a_missing_key_is_seen()
    {
        var mexican = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-MX"));

        Assert.Equal("Qué es esto", mexican.Get("Info_WhatIsThis"));
        Assert.Equal("[No_Such_Key]", English.Get("No_Such_Key"));
        Assert.False(English.Has("No_Such_Key"));
    }

    [Fact]
    public void A_coded_text_of_the_layers_below_is_painted_in_the_language()
    {
        Assert.Equal("Espera tu respuesta", Spanish.Render(UserText.Of(UserTextCodes.QuestionAwaitingAnswer)));
        Assert.Equal("Too many questions: review the requirements.", English.Render(UserText.Of(UserTextCodes.QuestionTooMany)));
    }

    [Fact]
    public void Every_part_of_the_context_and_the_project_brief_have_their_name_in_both_languages()
    {
        foreach (var kind in Enum.GetValues<ContextPartKind>())
        {
            Assert.True(Spanish.Has(ContextPartTexts.KeyOf(kind)), kind.ToString());
            Assert.NotEqual(string.Empty, ContextPartTexts.Label(kind, English));
        }

        Assert.Equal("Requisitos", ContextPartTexts.Label(ContextPartKind.Requirements, Spanish));
        Assert.Equal("Requirements", ContextPartTexts.Label(ContextPartKind.Requirements, English));
        Assert.True(Spanish.Has(ContextPartTexts.ConversationKey));
        Assert.True(Spanish.Has("Memory_Kind_ProjectBrief"));
        Assert.Equal(Texts.Get("Memory_Kind_ProjectBrief"), MemoryTexts.Kind(MemoryKind.ProjectBrief));
    }

    /// <summary>Each section with its ⓘ has its title, what it is and its actions (D-54).</summary>
    [Fact]
    public void Every_section_with_an_information_button_has_its_three_texts()
    {
        var keys = ResourceFiles().Values.SelectMany(f => Read(f.Neutral).Keys).ToHashSet(StringComparer.Ordinal);
        var sections = keys.Where(k => k.StartsWith("Info_", StringComparison.Ordinal) && k.EndsWith("_What", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(sections);
        foreach (var what in sections)
        {
            var section = what[..^"_What".Length];
            Assert.Contains(section + "_Title", keys);
            Assert.Contains(section + "_Actions", keys);
        }
    }

    // ---- the language (D-49) ----

    [Theory]
    [InlineData(null, "es-ES", "es-ES")]
    [InlineData(null, "es-MX", "es-ES")]
    [InlineData(null, "en-GB", "en")]
    [InlineData(null, "fr-FR", "en")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("en", "es-ES", "en")]
    [InlineData("es-ES", "en-US", "es-ES")]
    [InlineData("klingon", "es-ES", "es-ES")]
    [InlineData("fr-FR", "de-DE", "en")]

    // ADR-0054, addendum of 2026-11-08: the languages of Spain without resources read Spanish of Spain, not English.
    [InlineData(null, "ca-ES", "es-ES")]
    [InlineData(null, "eu-ES", "es-ES")]
    [InlineData(null, "gl-ES", "es-ES")]
    [InlineData(null, "ca-ES-valencia", "es-ES")]
    [InlineData("en", "ca-ES", "en")]
    [InlineData(null, "pt-PT", "en")]
    public void The_language_is_the_one_chosen_else_the_one_of_windows_else_english(string? chosen, string windows, string expected)
    {
        Assert.Equal(expected, UiLanguages.Resolve(chosen, CultureInfo.GetCultureInfo(windows)).CultureName);
    }

    [Fact]
    public void The_list_names_each_language_in_its_own_language_with_who_reviewed_it()
    {
        Assert.Equal(["es-ES", "en"], UiLanguages.All.Select(l => l.CultureName));
        Assert.Equal(["Español (España)", "English"], UiLanguages.All.Select(l => l.ToString()));
        Assert.All(UiLanguages.All, l => Assert.Equal(TranslationReview.ReviewedByPerson, l.Review));
        Assert.All(UiLanguages.All, l => Assert.True(Spanish.Has(l.ReviewKey)));
        Assert.Equal("es-ES", UiLanguages.SourceCulture);
        Assert.Null(UiLanguages.Find(" "));
    }
}
