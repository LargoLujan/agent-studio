using AgentStudio.Application.Help;
using AgentStudio.Application.Localization;

namespace AgentStudio.Desktop.Help;

/// <summary>
/// The closed list of actions the agent of the help may offer (docs/17 D-56, ADR-0056), written here, in the code of the desktop:
/// neither the agent nor a file can add one. Each opens a place of the app; none changes anything. The agent only names an id, the
/// panel paints the button with its label (<c>Text_help_action_…</c> in the resources) and nothing happens until the person presses it.
/// </summary>
public sealed class HelpActionCatalog : IHelpActionCatalog
{
    public const string OpenOrchestrator = "open.orchestrator";
    public const string OpenAgents = "open.agents";
    public const string OpenTools = "open.tools";
    public const string OpenSkills = "open.skills";
    public const string OpenUsage = "open.usage";
    public const string OpenMemory = "open.memory";
    public const string OpenDebates = "open.debates";
    public const string OpenAudit = "open.audit";
    public const string OpenSettings = "open.settings";

    public IReadOnlyList<HelpAction> Actions { get; } =
    [
        new(OpenOrchestrator, UserText.Of("help.action-open-orchestrator")),
        new(OpenAgents, UserText.Of("help.action-open-agents")),
        new(OpenTools, UserText.Of("help.action-open-tools")),
        new(OpenSkills, UserText.Of("help.action-open-skills")),
        new(OpenUsage, UserText.Of("help.action-open-usage")),
        new(OpenMemory, UserText.Of("help.action-open-memory")),
        new(OpenDebates, UserText.Of("help.action-open-debates")),
        new(OpenAudit, UserText.Of("help.action-open-audit")),
        new(OpenSettings, UserText.Of("help.action-open-settings")),
    ];

    /// <summary>The action with this id, or null: an id that is not in the list is never run.</summary>
    public HelpAction? Find(string id) => Actions.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
}
