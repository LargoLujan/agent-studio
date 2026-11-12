using System.Globalization;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>
/// ADR-0054 on «Trabajo»: every text comes from <c>WorkStrings</c> in both languages, every key the screens name exists, each ⓘ explains its
/// section in short sentences, and every code or value of the layers below that the screens say has a text of its own (no value falls back to
/// its English name or to <c>[Key]</c>).
/// </summary>
public sealed class WorkStringsTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    private static readonly string[] Prefixes = ["Work_", "Info_Work"];

    [Fact]
    public void Every_key_the_screens_name_exists_in_both_languages()
    {
        var sources = AreaStringsChecks.Sources(["Work"], ["Work"]);

        Assert.NotEmpty(sources);
        Assert.Empty(AreaStringsChecks.MissingKeys(sources, Prefixes));
    }

    [Fact]
    public void The_english_has_no_letter_or_sign_only_spanish_uses()
    {
        Assert.Empty(AreaStringsChecks.SpanishInEnglish("WorkStrings"));
    }

    [Fact]
    public void Every_section_has_its_information_button_in_both_languages_with_short_sentences()
    {
        var document = new WorkDocumentViewModel(WorkBackend.None, TimeProvider.System);
        string[] sections = ["Info_Work", .. document.Sections.Select(s => s.InfoKey)];

        Assert.Equal(8, sections.Length);
        Assert.Empty(AreaStringsChecks.InfoProblems(sections));
        Assert.All(document.Sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Title) || s.Title.StartsWith('['), s.InfoKey));
    }

    [Fact]
    public void No_long_sentence_in_the_area_in_either_language()
    {
        Assert.Empty(AreaStringsChecks.KeysWithLongSentences("WorkStrings", new HashSet<string>()));
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

        Check<BoardColumn>("Work_Column_");
        Check<BoardCardKind>("Work_CardKind_");
        Check<MilestoneStatus>("Work_Status_");
        Check<RequirementPriority>("Work_Priority_");
        Check<VerificationMethod>("Work_Method_");
        Check<ParticipationRole>("Work_Role_");
        Check<TraceChainStage>("Work_Stage_");
        Check<TraceNodeKind>("Work_Node_");
        Check<BudgetPeriod>("Work_Period_");
        Check<TraceAssertedBy>("Work_Asserted_");
        Check<TraceSuggestionSource>("Work_Suggestion_");

        Assert.True(missing.Count == 0, "Values with no text: " + string.Join(", ", missing));
    }

    [Fact]
    public void A_participation_role_without_a_source_is_said_no_consta_in_spanish_and_in_english()
    {
        Assert.Equal("No consta", Spanish.Get("Work_Participation_NotRecorded"));
        Assert.Equal("No consta", English.Get("Work_Participation_NotRecorded"));
    }
}
