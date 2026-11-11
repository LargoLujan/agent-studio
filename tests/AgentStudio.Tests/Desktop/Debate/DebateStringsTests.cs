using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// The texts of the debates, the card of a verdict and the challenge (docs/17 D-47, D-51 and D-54; F14-1, F14-3 and F14-6;
/// ADR-0054): every name an enumeration gives to the screen exists in Spanish of Spain and in English (a key made of a prefix
/// and an enumeration value is never checked by the test of keys, so this one does it), the plurals have both forms, the
/// English texts are English, and each ⓘ of these screens has its three texts in both languages.
/// </summary>
public sealed partial class DebateStringsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    private static readonly string[] InfoSections = ["Info_Debates", "Info_Verdict", "Info_Challenge", "Info_Audit"];

    [GeneratedRegex("InfoKey=.(Info_[A-Za-z]+).")]
    private static partial Regex InfoKey();

    /// <summary>A key of these screens written as a literal: <c>"Debate_Mode_"</c>, <c>"Verdict_Outcome_Valid"</c>…</summary>
    [GeneratedRegex(@"""((?:Debate|Verdict|Audit|Info)_[A-Za-z_]*)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"\{l:T ([A-Za-z_]+)\}")]
    private static partial Regex XamlKey();

    /// <summary>Every prefix with the enumeration whose values complete it, as <c>DebateTexts</c> and <c>VerdictTexts</c> read them.</summary>
    private static IEnumerable<(string Prefix, Type Enumeration, string[] Except)> NamedValues() =>
    [
        ("Debate_Mode_", typeof(DebateMode), []),
        ("Debate_ModeHelp_", typeof(DebateMode), []),
        ("Debate_Role_", typeof(DebateRole), []),
        ("Debate_Phase_", typeof(DebatePhase), []),
        ("Debate_Outcome_", typeof(DebateOutcome), []),
        ("Debate_Kind_", typeof(ContributionKind), []),
        ("Debate_Limit_", typeof(DebateLimitReached), [nameof(DebateLimitReached.None)]),
        ("Debate_Judgment_", typeof(DebateJudgmentReason), [nameof(DebateJudgmentReason.Limit)]),
        ("Debate_Judging_", typeof(DebateJudgingKind), []),
        ("Debate_JudgingHelp_", typeof(DebateJudgingKind), []),
        ("Debate_Await_", typeof(DebateAwaitReason), []),
        ("Debate_Turn_", typeof(AttemptOutcome), []),
        ("Verdict_Decision_", typeof(VerdictDecision), []),
        ("Verdict_Confidence_", typeof(ConfidenceLevel), []),
        ("Verdict_Status_", typeof(AssessmentStatus), []),
        ("Verdict_Category_", typeof(FindingCategory), []),
        ("Verdict_Severity_", typeof(Severity), []),
        ("Verdict_Violation_", typeof(VerdictViolationCode), []),
        ("Verdict_Challenge_", typeof(ChallengeStatus), []),
        ("Verdict_Disposition_", typeof(ReviewDisposition), []),
    ];

    [Fact]
    public void Every_name_an_enumeration_gives_to_a_screen_exists_in_both_languages()
    {
        var missing = new List<string>();
        foreach (var (prefix, enumeration, except) in NamedValues())
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
    }

    /// <summary>
    /// The test of keys of phase 13 only sees <c>Get("Key")</c>; these screens also choose a key with a condition
    /// (<c>Get(ok ? "A" : "B")</c>), build it from a prefix or a plural, or name it in XAML. Every key they name exists in both
    /// languages; a prefix needs one key that starts with it, a plural both forms and an information button its three texts.
    /// </summary>
    [Fact]
    public void Every_key_the_debate_and_audit_screens_name_exists_in_both_languages()
    {
        var files = ResourceFiles();
        var spanish = files.Values.SelectMany(f => Read(f.Neutral).Keys).ToHashSet(StringComparer.Ordinal);
        var english = files.Values.SelectMany(f => Read(f.Satellites["en"]).Keys).ToHashSet(StringComparer.Ordinal);
        var sources = new[] { "Debate", "Audit" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(DesktopRoot, folder), "*.cs"))
            .Concat(new[] { "Debate", "Audit" }.SelectMany(folder => Directory.GetFiles(Path.Combine(DesktopRoot, "Views", folder), "*.xaml")))
            .ToList();
        Assert.True(sources.Count >= 7, "The screens of the debates and the audit were not found.");

        var missing = new List<string>();
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            var named = KeyLiteral().Matches(text).Select(m => m.Groups[1].Value)
                .Concat(XamlKey().Matches(text).Select(m => m.Groups[1].Value))
                .Distinct(StringComparer.Ordinal);
            foreach (var key in named)
            {
                var found = (spanish.Contains(key) && english.Contains(key))
                    || (key.EndsWith('_') && spanish.Any(k => k.StartsWith(key, StringComparison.Ordinal)) && english.Any(k => k.StartsWith(key, StringComparison.Ordinal)))
                    || (spanish.Contains(key + "_One") && spanish.Contains(key + "_Other") && english.Contains(key + "_One") && english.Contains(key + "_Other"))
                    || (spanish.Contains(key + "_Title") && spanish.Contains(key + "_What") && spanish.Contains(key + "_Actions")
                        && english.Contains(key + "_Title") && english.Contains(key + "_What") && english.Contains(key + "_Actions"));
                if (!found)
                {
                    missing.Add($"{Path.GetFileName(source)}: {key}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Keys that are not in both languages:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void The_names_a_screen_shares_read_from_the_resources_and_a_value_the_resources_do_not_know_is_shown_by_its_name()
    {
        Assert.Equal("Revisión de arquitectura", DebateTexts.Mode(DebateMode.ArchitectureReview));
        Assert.Equal("En deliberación", DebateTexts.Phase(DebatePhase.Deliberation));
        Assert.Equal("Aprobado", VerdictTexts.Decision(VerdictDecision.Approved));
        Assert.Equal("Media", VerdictTexts.Severity(Severity.Medium));
        Assert.Equal("Claude Code", DebateTexts.Provider(new ProviderId("claude-code")));
        Assert.Contains("Anthropic y OpenAI", DebateTexts.Waiver([VendorId.OpenAI, VendorId.Anthropic]), StringComparison.Ordinal);
        Assert.Null(DebateTexts.Waiver([]));
        Assert.Null(DebateTexts.Limit(DebateLimitReached.None));
        Assert.Equal("límite de rondas", DebateTexts.Limit(DebateLimitReached.Rounds));
        Assert.Equal("se alcanzó el límite de tokens", DebateTexts.Judgment(DebateJudgmentReason.Limit, DebateLimitReached.Tokens));
        Assert.Equal("se alcanzó un límite", DebateTexts.Judgment(DebateJudgmentReason.Limit));
        Assert.Equal("Versión guardada (commit) 777777777777", DebateTexts.Subject(DebateSubjectKind.Commit, new string('7', 40)));
        Assert.Equal("Ejecución 20261018-c4a1f2", DebateTexts.Subject(DebateSubjectKind.Run, "20261018-c4a1f2"));
        Assert.Equal("sin regla", DebateTexts.Rule(null));
        Assert.Equal("una-regla-nueva", DebateTexts.Rule("una-regla-nueva"));
        Assert.Equal("77", VerdictTexts.Severity((Severity)77));
    }

    [Fact]
    public void A_plural_has_its_one_and_its_many_in_both_languages_with_the_number_in_place()
    {
        var plurals = new[]
        {
            "Debate_Count_AgentRun", "Debate_Count_Requirement", "Debate_Count_TurnInProgress", "Debate_Count_Redaction",
            "Verdict_Validation", "Verdict_Count_Satisfied", "Verdict_Count_Unmet", "Verdict_Count_Unverifiable", "Audit_Why_Issues",
        };

        foreach (var key in plurals)
        {
            foreach (var provider in new[] { Spanish, English })
            {
                Assert.Contains("1", provider.Format(key + "_One", 1), StringComparison.Ordinal);
                Assert.Contains("3", provider.Format(key + "_Other", 3), StringComparison.Ordinal);
            }

            // Spanish inflects its nouns; English may write the same words for one and for many («1 met», «3 met»).
            Assert.NotEqual(Spanish.Format(key + "_One", 1), Spanish.Format(key + "_Other", 1));
        }

        Assert.Equal("1 turno en curso", DebateTexts.Plural(1, "Debate_Count_TurnInProgress"));
        Assert.Equal("3 turnos en curso", DebateTexts.Plural(3, "Debate_Count_TurnInProgress"));
    }

    /// <summary>The English files are English: an accent or a Spanish sign in one of them is a text nobody translated.</summary>
    [Fact]
    public void The_english_texts_of_the_debates_have_no_spanish_letters_or_signs()
    {
        var files = ResourceFiles();
        var untranslated = new List<string>();
        foreach (var baseName in new[] { "DebateStrings", "AuditStrings" })
        {
            foreach (var (key, text) in Read(files[baseName].Satellites["en"]))
            {
                if (text.Any(c => "áéíóúüñÁÉÍÓÚÑ¿¡«»".Contains(c, StringComparison.Ordinal)))
                {
                    untranslated.Add($"{baseName}.{key}: {text}");
                }
            }
        }

        Assert.True(untranslated.Count == 0, "English texts with Spanish letters or signs:\n" + string.Join('\n', untranslated));
    }

    [Fact]
    public void Each_information_button_of_these_screens_has_its_title_what_it_is_and_its_actions_in_both_languages()
    {
        foreach (var section in InfoSections)
        {
            foreach (var part in new[] { "_Title", "_What", "_Actions" })
            {
                Assert.True(Spanish.Has(section + part), section + part + " (es)");
                Assert.True(English.Has(section + part), section + part + " (en)");
            }

            // A title may be the same word in both languages («Debates»); the explanation never is.
            Assert.NotEqual(Spanish.Get(section + "_What"), English.Get(section + "_What"));
            Assert.NotEqual(Spanish.Get(section + "_Actions"), English.Get(section + "_Actions"));

            Assert.True(Spanish.Get(section + "_Actions").Split('\n').Length >= 3, section + " lists its actions, one per line");
        }
    }

    /// <summary>Each screen puts its ⓘ where D-54 says (the debates, the card of a verdict, the audit, the challenge), and no other.</summary>
    [Fact]
    public void The_screens_use_their_information_button_and_each_one_has_its_texts()
    {
        var views = new[] { "Debate/DebatesView.xaml", "Debate/VerdictCardView.xaml", "Debate/ChallengeFormView.xaml", "Audit/ProjectAuditView.xaml" };
        var found = views
            .SelectMany(view => InfoKey().Matches(File.ReadAllText(Path.Combine(DesktopRoot, "Views", view))).Select(m => m.Groups[1].Value))
            .Order()
            .ToList();

        Assert.Equal(new[] { "Info_Audit", "Info_Challenge", "Info_Debates", "Info_Verdict" }, found);
    }

    /// <summary>The explanations of the information buttons are short: no sentence has more than 20 words (D-52).</summary>
    [Fact]
    public void The_explanations_of_the_information_buttons_have_short_sentences()
    {
        var tooLong = new List<string>();
        foreach (var section in InfoSections)
        {
            foreach (var part in new[] { "_What", "_Actions" })
            {
                foreach (var provider in new[] { Spanish, English })
                {
                    foreach (var sentence in Regex.Split(provider.Get(section + part), @"(?<=[.!?:])\s+|\n"))
                    {
                        if (sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 20)
                        {
                            tooLong.Add($"{section}{part}: {sentence}");
                        }
                    }
                }
            }
        }

        Assert.True(tooLong.Count == 0, "Sentences with more than 20 words:\n" + string.Join('\n', tooLong));
    }
}
