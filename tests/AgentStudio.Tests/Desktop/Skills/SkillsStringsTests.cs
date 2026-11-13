using AgentStudio.Application.Localization;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using static AgentStudio.Tests.Desktop.Localization.AreaStringsChecks;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Skills;

/// <summary>
/// The texts of the catalog of skills, their health, activation, cost and recommendations (docs/17 D-47, D-51 and D-54; F14-1,
/// F14-3, F14-5 and F14-6; ADR-0054): every key the screen names exists in Spanish of Spain and in English, the English is
/// English, the plurals have both forms, the ⓘ has its three texts in short sentences, and the texts say «límite de uso» and
/// «tecnologías», not «cuota» or «stack», as the glossary of the guide asks. What goes to the log or to the agent is fixed text
/// and has its own test (<c>SkillsDocumentViewModelTests</c>).
/// </summary>
public sealed class SkillsStringsTests
{
    private static readonly string[] Prefixes = ["Skills_", "Info_"];

    [Fact]
    public void Every_key_the_skills_screen_names_exists_in_both_languages()
    {
        var sources = Sources(["Skills"], ["Skills"]);
        Assert.True(sources.Count >= 6, "The screen of the skills was not found.");
        var missing = MissingKeys(sources, Prefixes);
        Assert.True(missing.Count == 0, "Keys that are not in both languages:\n" + string.Join('\n', missing));
    }

    /// <summary>A key made of a prefix and an enumeration value is never seen by the test of keys, so each value is asked for.</summary>
    [Fact]
    public void Every_health_has_its_name_in_both_languages()
    {
        foreach (var name in Enum.GetNames<SkillHealthStatus>())
        {
            var key = "Skills_Health_" + name;
            Assert.True(Spanish.Has(key) && English.Has(key), key);
        }

        Assert.Equal("Sana", Spanish.Get("Skills_Health_Healthy"));
        Assert.Equal("Healthy", English.Get("Skills_Health_Healthy"));
    }

    [Fact]
    public void A_plural_has_its_one_and_its_many_in_both_languages_with_the_number_in_place()
    {
        var plurals = new[]
        {
            "Skills_Cost_Agent", "Skills_Summary_Sectors", "Skills_Linked", "Skills_UnusableLinked", "Skills_UnusableNoCopy",
            "Skills_Warnings_Index", "Skills_Warnings_More", "Skills_Badge_Agents", "Skills_Points", "Skills_Assisted_Proposed",
        };

        foreach (var key in plurals)
        {
            foreach (var provider in new[] { Spanish, English })
            {
                Assert.True(provider.Has(key + "_One") && provider.Has(key + "_Other"), key);
            }
        }

        Assert.Equal("3 agentes", Spanish.Format("Skills_Badge_Agents_Other", 3));
        Assert.Equal("1 agent", English.Get("Skills_Badge_Agents_One"));
        Assert.Equal("2 puntos", Spanish.Format("Skills_Points_Other", 2));
    }

    [Fact]
    public void The_english_texts_of_the_skills_have_no_spanish_letters_or_signs()
    {
        var untranslated = SpanishInEnglish("SkillsStrings");
        Assert.True(untranslated.Count == 0, "English texts with Spanish letters or signs:\n" + string.Join('\n', untranslated));
    }

    /// <summary>
    /// The checks of the doctor and of the reader are codes (ADR-0054): each has its text in both languages, in English no Spanish comes out, and the arguments
    /// (a count, a size, an instant) go in the culture of the language.
    /// </summary>
    [Fact]
    public void Every_coded_text_of_a_health_check_is_painted_in_the_language_of_the_person()
    {
        var reset = new DateTimeOffset(2026, 11, 13, 14, 30, 0, TimeSpan.Zero);
        foreach (var code in SkillsUserTexts.All)
        {
            object[] arguments = code switch
            {
                SkillsUserTexts.DoctorRateLimited => [reset],
                SkillsUserTexts.ReaderScriptsReviewed or SkillsUserTexts.ReaderScriptsUnreviewed or SkillsUserTexts.ReaderScriptsChanged => [3],
                SkillsUserTexts.ReaderTooLargeFrontmatter or SkillsUserTexts.ReaderTooLargeSize => [2048L],
                _ => [],
            };
            var text = new UserText(code, arguments);
            Assert.True(Spanish.Has(text.ResourceKey) && English.Has(text.ResourceKey), text.ResourceKey);

            var english = English.Render(text);
            var spanish = Spanish.Render(text);
            Assert.NotEqual(spanish, english);
            Assert.DoesNotContain("Text_", english, StringComparison.Ordinal);
            Assert.False(english.Any(c => "áéíóúñ¿¡«»".Contains(c, StringComparison.Ordinal)), $"{code}: {english}");
            Assert.DoesNotContain("Sin comprobar", english, StringComparison.Ordinal);
        }

        Assert.Contains("14:30 (UTC)", English.Render(new UserText(SkillsUserTexts.DoctorRateLimited, [reset])), StringComparison.Ordinal);
        Assert.Contains("3 files", English.Render(new UserText(SkillsUserTexts.ReaderScriptsUnreviewed, [3])), StringComparison.Ordinal);
    }

    [Fact]
    public void The_information_button_has_its_three_texts_in_short_sentences_and_the_screen_uses_it()
    {
        var problems = InfoProblems(["Info_Skills", "Info_SkillsDoctor"]);
        Assert.True(problems.Count == 0, string.Join('\n', problems));
        Assert.Equal(["Info_Skills", "Info_SkillsDoctor"], InfoKeysOf("Skills/SkillsView.xaml"));
    }

    /// <summary>The guide asks for 20 words a sentence at most (D-52): none of the texts of this area has a longer one.</summary>
    [Fact]
    public void No_text_of_the_skills_has_a_sentence_of_more_than_20_words()
    {
        var tooLong = KeysWithLongSentences("SkillsStrings", new HashSet<string>());
        Assert.True(tooLong.Count == 0, "Sentences with more than 20 words:\n" + string.Join('\n', tooLong));
    }

    /// <summary>The glossary of the guide (D-52): the quota is «límite de uso», and the words of the code do not reach the first level.</summary>
    [Fact]
    public void The_texts_use_the_glossary_of_the_guide()
    {
        var offenders = new List<string>();
        foreach (var (key, text) in Read(ResourceFiles()["SkillsStrings"].Neutral))
        {
            if (new[] { "cuota", "stack", "vendoriz", "MCP", "composición" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add($"{key}: {text}");
            }
        }

        Assert.True(offenders.Count == 0, "Texts with a word the glossary replaces:\n" + string.Join('\n', offenders));
    }
}
