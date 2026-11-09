using System.Globalization;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Metrics;
using AgentStudio.Observability.Analysis;

namespace AgentStudio.Tests.Observability.Analysis;

/// <summary>
/// F13-15 and F13-19 (docs/17 D-34 and D-47): every code of the analysis of consumption has its sentence and, but the one that
/// only informs, what it suggests, in Spanish of Spain and in English, and a finding is painted with its arguments.
/// </summary>
public sealed class ConsumptionTextsTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    [Fact]
    public void Every_code_has_its_sentence_and_its_suggestion_in_both_languages()
    {
        foreach (var code in ConsumptionFindingCodes.All)
        {
            var sentence = UserText.KeyOf(ConsumptionTexts.TextPrefix + code);
            Assert.True(Spanish.Has(sentence), sentence);
            Assert.False(English.Get(sentence).StartsWith('['), sentence);
            if (ConsumptionTexts.SuggestionCode(code) is { } suggestion)
            {
                Assert.True(Spanish.Has(UserText.KeyOf(ConsumptionTexts.TextPrefix + suggestion)), suggestion);
            }
            else
            {
                Assert.Equal(ConsumptionFindingCodes.WorkDiscarded, code);
            }
        }
    }

    [Fact]
    public void A_finding_is_painted_with_its_arguments_in_the_language()
    {
        var finding = ConsumptionFinding.Of(
            ConsumptionFindingCodes.WaitQueue,
            new ConsumptionTarget(ConsumptionTargetKind.Setting, "provider-concurrency:claude-code"),
            MetricValue<long>.Missing(ConsumptionRules.WaitSpendsNoTokens),
            2,
            "claude-code",
            2,
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(5));

        Assert.Equal("2 intentos esperaron en la cola de claude-code 0:08:00 en total; la espera más larga, 0:05:00.", Spanish.Render(ConsumptionTexts.Sentence(finding)));
        Assert.Equal("Use fewer executors of the same program at once.", English.Render(ConsumptionTexts.Suggestion(finding)!));
        Assert.Equal("El paquete de contexto de ejecutor dejó fuera 3 entradas en 2 intentos: le faltó contexto.", Spanish.Render(UserText.Of(ConsumptionTexts.TextPrefix + ConsumptionFindingCodes.PackOmitted, "ejecutor", 3, 2)));
    }

    [Fact]
    public void A_code_the_analyzer_of_the_team_also_has_is_painted_with_the_text_of_the_consumption()
    {
        // model.oversized is a finding of the analyzer of the team too, with other arguments: each keeps its own sentence.
        var finding = ConsumptionFinding.Of(
            ConsumptionFindingCodes.ModelOversized,
            new ConsumptionTarget(ConsumptionTargetKind.Agent, "ejecutor"),
            MetricValue<long>.Missing("n/a"),
            3,
            "ejecutor",
            "claude-opus-5-5",
            3);

        Assert.Equal("ejecutor usó claude-opus-5-5, de nivel «máximo», en 3 intentos con cambios pequeños.", Spanish.Render(ConsumptionTexts.Sentence(finding)));
        Assert.NotEqual(Spanish.Get(UserText.KeyOf(ConsumptionFindingCodes.ModelOversized)), Spanish.Render(ConsumptionTexts.Sentence(finding)));
    }
}
