using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Models;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>
/// The texts of the visual builder (docs/17 D-07 to D-17, ADR-0048) in the language of a provider: all of them come from
/// <c>Resources/CanvasStrings.resx</c>, none is written here. A screen that paints in a given language (a test, a render per
/// language) passes its own <see cref="IUiTextProvider"/>; the others use the one of the interface.
/// </summary>
public sealed class CanvasText(IUiTextProvider? texts = null)
{
    private readonly IUiTextProvider? _texts = texts;

    /// <summary>The provider in use.</summary>
    public IUiTextProvider Provider => _texts ?? Texts.Current;

    public string Get(string key) => Provider.Get(key);

    public string Format(string key, params object?[] arguments) => Provider.Format(key, arguments);

    /// <summary>A sentence of the system as a code and its arguments, painted in the language in use.</summary>
    public string Render(string code, IReadOnlyList<object> arguments) => Provider.Render(new UserText(code, arguments));

    /// <summary>The name of a role in the language in use: «Orquestador», «Ejecutor», «Juez»…</summary>
    public string Role(AgentRole role) => Get("Canvas_Role_" + role);

    /// <summary>The level of effort in words, or that it is not set: the program's own.</summary>
    public string Effort(ReasoningEffort? effort) => Get("Models_Effort_" + (effort is { } set ? set.ToString() : "CliDefault"));

    /// <summary>The level of a model in words.</summary>
    public string Tier(ModelTier? tier) => Get("Models_Tier_" + (tier is { } known ? known.ToString() : "Unclassified"));
}
