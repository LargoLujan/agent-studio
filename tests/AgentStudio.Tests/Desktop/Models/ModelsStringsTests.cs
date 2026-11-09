using System.Globalization;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Models;

namespace AgentStudio.Tests.Desktop.Models;

/// <summary>
/// Every text of models and power (docs/17 D-22 to D-25, F13-19) comes from the resources, in Spanish of Spain and in English:
/// the reasons of the recommendation, the notices of a model and its effort, the notes of the shipped catalog, the levels and
/// the efforts. The layers below give codes; the desktop paints them (ADR-0054).
/// </summary>
public sealed class ModelsStringsTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    private static readonly string[] Reasons =
    [
        ModelRecommendationReasons.ManyMandatoryRequirements, ModelRecommendationReasons.LargeProject, ModelRecommendationReasons.RoleDefault,
        ModelRecommendationReasons.LightSecondExecutor, ModelRecommendationReasons.JudgeHighestOtherVendor, ModelRecommendationReasons.History,
    ];

    public static TheoryData<string> Codes()
    {
        var codes = new TheoryData<string>();
        foreach (var code in Reasons.Concat(ModelNoticeCodes.All).Concat(EstimateAssumptions.All).Concat(BuiltInModelCatalog.Shipped.Catalog.Entries.Select(e => e.Note!).Where(n => n is not null)).Distinct(StringComparer.Ordinal))
        {
            codes.Add(code);
        }

        return codes;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_code_of_models_has_its_text_in_both_languages(string code)
    {
        var key = UserText.KeyOf(code);

        Assert.True(Spanish.Has(key), key);
        Assert.True(English.Has(key), key);
        Assert.NotEqual(Spanish.Get(key), English.Get(key));
    }

    [Fact]
    public void Every_level_and_every_effort_has_its_name_in_both_languages()
    {
        foreach (var key in Enum.GetNames<ModelTier>().Select(t => "Models_Tier_" + t).Append("Models_Tier_Unclassified")
            .Concat(Enum.GetNames<ReasoningEffort>().Select(e => "Models_Effort_" + e)).Append("Models_Effort_CliDefault"))
        {
            Assert.True(Spanish.Has(key), key);
            Assert.False(string.IsNullOrWhiteSpace(English.Get(key)), key);
        }

        Assert.Equal("equilibrado", Spanish.Get("Models_Tier_Balanced"));
        Assert.Equal("balanced", English.Get("Models_Tier_Balanced"));
    }

    /// <summary>D-24: «tus 9 tareas con sonnet se aprobaron a la primera en 7», with its arguments in their places.</summary>
    [Fact]
    public void A_reason_and_a_notice_are_painted_with_their_arguments()
    {
        var history = ModelRecommendation.Of(ProviderId.ClaudeCode, "claude-sonnet-5-5", ModelTier.Balanced, ReasoningEffort.Medium, ModelRecommendationReasons.History, "sonnet", 9, 7);

        Assert.Equal(
            "Tus 9 tareas con sonnet se aprobaron a la primera en 7 (estimado por Agent Studio).",
            Spanish.Render(new UserText(history.ReasonCode, history.ReasonArguments)));
        Assert.Equal(
            "Of your 9 tasks with sonnet, 7 were approved at the first attempt (estimated by Agent Studio).",
            English.Render(new UserText(history.ReasonCode, history.ReasonArguments)));

        var notice = ModelNotices.Of(BuiltInModelCatalog.Shipped.Catalog, ProviderId.CodexCli, "gpt-6-luna", ReasoningEffort.Max).Single();
        Assert.Equal("El programa del agente no admite el esfuerzo max. Usará el suyo por defecto.", Spanish.Render(new UserText(notice.Code, notice.Arguments)));
    }
}
