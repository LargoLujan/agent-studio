using AgentStudio.Application.Help;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.Setup;
using AgentStudio.Desktop.Skills;
using AgentStudio.Desktop.Tools;

namespace AgentStudio.Desktop.Help;

/// <summary>
/// Where the person is, as the help knows it (docs/17 D-55 and D-57): the code of the screen that has the focus, which <c>F1</c> turns
/// into the article of that screen, and what the screen says about itself for the card that goes to the agent. The codes are the
/// ones the articles name in their <c>screens:</c> header.
/// </summary>
public static class HelpScreens
{
    public const string Preparation = "preparation";
    public const string Canvas = "canvas";
    public const string Monitor = "monitor";
    public const string Debates = "debates";
    public const string Audit = "audit";
    public const string Memory = "memory";
    public const string Skills = "skills";
    public const string Consumption = "consumption";
    public const string Settings = "settings";
    public const string Lock = "lock";

    /// <summary>The screen shown by the content of the dock (a document, or a panel's view); null when it is none of the screens that have help (the welcome tab, a file).</summary>
    public static string? Of(object? content) => content switch
    {
        OrchestratorDocumentViewModel orchestrator => orchestrator.Monitor.IsAttached ? Monitor : orchestrator.IsPreparationExpanded ? Preparation : Canvas,
        SetupDocumentViewModel => Preparation,
        ToolsDocumentViewModel => Canvas,
        DebatesDocumentViewModel => Debates,
        ProjectAuditDocumentViewModel => Audit,
        MemoryViewModel => Memory,
        SkillsDocumentViewModel => Skills,
        UsageDashboardViewModel => Consumption,
        SettingsDocumentViewModel => Settings,
        _ => null,
    };

    /// <summary>The name of a screen as the person knows it, in the language of <paramref name="texts"/>.</summary>
    public static string NameOf(string screen, IUiTextProvider texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts.Get("Help_Screen_" + screen);
    }

    /// <summary>
    /// What the screen says of itself for the card (D-57): its code and name, a fact about its state when it has one, the last notice
    /// the person could see and the name of the project. Nothing else: no file, no code, no memory.
    /// </summary>
    public static HelpScreenFacts FactsOf(string screen, object? content, string? notice, string? projectName, IUiTextProvider texts)
    {
        var state = new List<string>();
        if (content is OrchestratorDocumentViewModel orchestrator && orchestrator.ApprovalText is { Length: > 0 } approval)
        {
            state.Add(approval);
        }

        return new HelpScreenFacts(screen, NameOf(screen, texts), state, notice, projectName);
    }

    /// <summary>Every screen code that has an article, for the tests of the corpus.</summary>
    public static IReadOnlyList<string> All { get; } = [Preparation, Canvas, Monitor, Debates, Audit, Memory, Skills, Consumption, Settings, Lock];
}
