using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Desktop.Setup;

/// <summary>
/// The words of the configurator for what the layers below give as codes and enums (ADR-0054): every text comes from
/// <c>SetupStrings</c> (and the level and the effort of a model from <c>ModelsStrings</c>), in the language the provider says.
/// </summary>
internal static class SetupTexts
{
    public static string Step(IUiTextProvider texts, SetupStep step) => texts.Get("Setup_Step_" + step);

    public static string Size(IUiTextProvider texts, SetupSize size) => texts.Get("Setup_Size_" + size);

    public static string Role(IUiTextProvider texts, AgentRole role) => texts.Get("Setup_Role_" + role);

    public static string Outcome(IUiTextProvider texts, SetupPartOutcome outcome) => texts.Get("Setup_Outcome_" + outcome);

    /// <summary>The stack in words; the name of a product is not translated.</summary>
    public static string Stack(IUiTextProvider texts, string? stack) => stack switch
    {
        SetupStacks.DotNet => ".NET",
        SetupStacks.Node => "Node",
        SetupStacks.Python => "Python",
        _ => texts.Get("Setup_Stack_None"),
    };

    /// <summary>The program of an agent: the name of the product, never translated.</summary>
    public static string Provider(ProviderId provider) =>
        provider == ProviderId.ClaudeCode ? "Claude Code"
        : provider == ProviderId.CodexCli ? "Codex"
        : provider.Value;

    /// <summary>The model in words: its name, or «modelo predeterminado del programa» when the agent names none.</summary>
    public static string Model(IUiTextProvider texts, string? model) =>
        string.IsNullOrWhiteSpace(model) ? texts.Get("Setup_Model_Default") : model;

    public static string Effort(IUiTextProvider texts, ReasoningEffort? effort) =>
        effort is { } value ? texts.Get("Models_Effort_" + value) : texts.Get("Models_Effort_CliDefault");

    public static string Tier(IUiTextProvider texts, ModelTier tier) => texts.Get("Models_Tier_" + tier);

    /// <summary>The model and the effort an agent has, in a line.</summary>
    public static string Power(IUiTextProvider texts, string? model, ReasoningEffort? effort) =>
        texts.Format("Setup_Power_Line", Model(texts, model), Effort(texts, effort));

    /// <summary>The agent in a line: its role and its program.</summary>
    public static string AgentLine(IUiTextProvider texts, AgentDefinition agent) =>
        texts.Format("Setup_Agent_Line", Role(texts, agent.Role), Provider(agent.Model.Provider));
}
