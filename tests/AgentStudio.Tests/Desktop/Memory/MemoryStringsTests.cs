using AgentStudio.Application.Knowledge;
using static AgentStudio.Tests.Desktop.Localization.AreaStringsChecks;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Memory;

/// <summary>
/// The texts of the memory, its search, the trust of the journal, the daily summary and their settings (docs/17 D-47, D-51 and
/// D-54; F14-1, F14-3, F14-5 and F14-6; ADR-0054): every key the screens name exists in Spanish of Spain and in English, the
/// English is English, the plurals have both forms, each ⓘ has its three texts in short sentences, and the texts say «límite de
/// uso» and «instrucciones», not «cuota» or «prompts», as the glossary of the guide asks.
/// </summary>
public sealed class MemoryStringsTests
{
    private static readonly string[] Prefixes = ["Memory_", "Info_"];

    private static readonly string[] InfoSections = ["Info_Memory", "Info_DailySummary"];

    [Fact]
    public void Every_key_the_memory_screens_name_exists_in_both_languages()
    {
        var sources = Sources(["Memory"], ["Memory"]);
        Assert.True(sources.Count >= 15, "The screens of the memory were not found.");
        var missing = MissingKeys(sources, Prefixes);
        Assert.True(missing.Count == 0, "Keys that are not in both languages:\n" + string.Join('\n', missing));
    }

    /// <summary>A key made of a prefix and an enumeration value is never seen by the test of keys, so each value is asked for.</summary>
    [Fact]
    public void Every_category_origin_and_trust_has_its_name_in_both_languages()
    {
        var missing = new List<string>();
        foreach (var (prefix, enumeration, except) in new (string, Type, string[])[]
        {
            ("Memory_Kind_", typeof(MemoryKind), [nameof(MemoryKind.ProjectBrief)]),
            ("Memory_Origin_", typeof(MemoryOrigin), []),
            ("Memory_Trust_", typeof(MemoryTrust), []),
        })
        {
            foreach (var name in Enum.GetNames(enumeration).Where(n => !except.Contains(n)))
            {
                foreach (var (language, provider) in new[] { ("es", Spanish), ("en", English) })
                {
                    if (!provider.Has(prefix + name) || provider.Get(prefix + name).StartsWith('['))
                    {
                        missing.Add($"{language}: {prefix}{name}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "Names without a text:\n" + string.Join('\n', missing));
        Assert.Equal("Error conocido", Spanish.Get("Memory_Kind_KnownError"));
        Assert.Equal("Known error", English.Get("Memory_Kind_KnownError"));
    }

    [Fact]
    public void A_plural_has_its_one_and_its_many_in_both_languages_with_the_number_in_place()
    {
        var plurals = new[]
        {
            "Memory_Journal_DayQuarantine", "Memory_Journal_TrustedDays", "Memory_Daily_Requests", "Memory_Daily_Unrecognized",
            "Memory_Upkeep_New", "Memory_Upkeep_Problems", "Memory_Export_Done", "Memory_Export_Rewritten",
        };

        foreach (var key in plurals)
        {
            foreach (var provider in new[] { Spanish, English })
            {
                Assert.True(provider.Has(key + "_One") && provider.Has(key + "_Other"), key);
            }

            Assert.NotEqual(Spanish.Get(key + "_One"), Spanish.Get(key + "_Other"));
        }

        Assert.Contains("3", Spanish.Format("Memory_Daily_Requests_Other", 3), StringComparison.Ordinal);
        Assert.Equal("3 peticiones", Spanish.Format("Memory_Daily_Requests_Other", 3));
        Assert.Equal("1 request", English.Format("Memory_Daily_Requests_One", 1));
    }

    /// <summary>A day is written the way each language writes it: day first in Spanish, month first in English.</summary>
    [Fact]
    public void A_day_follows_the_language()
    {
        var day = new DateOnly(2026, 10, 16);
        Assert.Equal("viernes 16/10/2026", day.ToString(Spanish.Get("Memory_Day_Format"), Spanish.Culture));
        Assert.Equal("Friday 10/16/2026", day.ToString(English.Get("Memory_Day_Format"), English.Culture));
    }

    [Fact]
    public void The_english_texts_of_the_memory_have_no_spanish_letters_or_signs()
    {
        var untranslated = SpanishInEnglish("MemoryStrings");
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
        Assert.Equal(["Info_Memory"], InfoKeysOf("Memory/MemoryView.xaml"));
        Assert.Equal(["Info_DailySummary"], InfoKeysOf("Memory/DailySummaryView.xaml"));
    }

    /// <summary>The guide asks for 20 words a sentence at most (D-52): none of the texts of this area has a longer one.</summary>
    [Fact]
    public void No_text_of_the_memory_has_a_sentence_of_more_than_20_words()
    {
        var tooLong = KeysWithLongSentences("MemoryStrings", new HashSet<string>());
        Assert.True(tooLong.Count == 0, "Sentences with more than 20 words:\n" + string.Join('\n', tooLong));
    }

    /// <summary>The glossary of the guide (D-52): the quota is «límite de uso», a prompt is an instruction, and a hash is a «huella».</summary>
    [Fact]
    public void The_texts_use_the_glossary_of_the_guide()
    {
        var offenders = new List<string>();
        foreach (var (key, text) in Read(ResourceFiles()["MemoryStrings"].Neutral))
        {
            if (new[] { "cuota", "prompt", "SHA-256", "composición", "app-server" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add($"{key}: {text}");
            }
        }

        Assert.True(offenders.Count == 0, "Texts with a word the glossary replaces:\n" + string.Join('\n', offenders));
    }
}
