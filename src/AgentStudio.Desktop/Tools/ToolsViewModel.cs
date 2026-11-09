using System.Collections.ObjectModel;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Tools;

/// <summary>How much a tool may do (docs/17 D-46): it says whether giving it asks Windows Hello, never in colour alone.</summary>
public enum ToolRisk
{
    /// <summary>It only reads.</summary>
    Low,

    /// <summary>It writes in the isolated copy of the work, or runs a step of the verification profile.</summary>
    Medium,

    /// <summary>It runs commands or reaches the network: giving it asks Windows Hello (D-37).</summary>
    High,
}

/// <summary>One tool of the view: what it is, what it allows, how risky it is, whether giving it asks Windows Hello and which agent has it.</summary>
/// <param name="Name">The name as the launch says it: <c>Read</c>, <c>search_skills</c>, <c>test</c>.</param>
/// <param name="Description">What it allows, in the language of the interface.</param>
/// <param name="RiskText">«Solo lee», «Escribe en la copia aislada», «Ejecuta órdenes».</param>
/// <param name="HelloText">«Darla pide Windows Hello» or «Darla no pide Windows Hello».</param>
/// <param name="AgentsText">«La tienen: Ejecutor 1, Ejecutor 2» or «Ningún agente la tiene».</param>
public sealed record ToolRow(string Name, string Description, ToolRisk Risk, string RiskText, bool AsksHello, string HelloText, string AgentsText)
{
    /// <summary>The name of the button that leads to the inspector (never to another door).</summary>
    public string GiveName { get; init; } = string.Empty;

    /// <summary>Whether it can be given to an agent from the inspector: a step of the profile is not given, a task of tool uses it.</summary>
    public bool CanBeGiven { get; init; }

    /// <summary>What a screen reader says of the tool: its name, what it allows, its risk, whether Hello is asked and who has it.</summary>
    public string AccessibleName => $"{Name}. {Description} {RiskText}. {HelloText}. {AgentsText}";

    public override string ToString() => AccessibleName;
}

/// <summary>A group of tools: those of one program, the steps of the profile or the tools of the local server.</summary>
public sealed record ToolGroup(string Title, string Note, IReadOnlyList<ToolRow> Rows)
{
    /// <summary>What a screen reader says of a group: its title.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// «Herramientas» (docs/17 D-46, F13-18): what each agent may use and with what risk. It shows the built-in tools of each program and
/// what they allow, the steps of the verification profile (which can be used as tasks of tool), the tools of the local server of
/// Agent Studio and where each is allowed, and which agent has each. What an agent has is read from the arguments of its launch
/// (<see cref="EffectiveTools"/>): the same code that launches it. «Dar a un agente…» leads to the inspector of the agent
/// (<see cref="GiveToAgentRequested"/>), never to another door: the change goes through the usual gates, with Windows Hello when it grants
/// a risky capability.
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    /// <summary>The built-in tools of Claude Code that Agent Studio ever allows, in the order the view lists them.</summary>
    public static IReadOnlyList<string> ClaudeBuiltIns { get; } = ["Read", "Grep", "Glob", "Edit", "Write", "Bash", "WebFetch", "WebSearch"];

    private readonly IUiTextProvider _texts;

    public ToolsViewModel(IUiTextProvider? texts = null) => _texts = texts ?? Texts.Current;

    public ObservableCollection<ToolGroup> Groups { get; } = [];

    /// <summary>What each agent of the template may use, in a task that writes and one that only reads, as its launch says it.</summary>
    public IReadOnlyList<AgentToolSet> AgentSets { get; private set; } = [];

    [ObservableProperty]
    private bool _hasAgents;

    /// <summary>The person asked to give a tool to an agent: the shell opens the inspector of the agents in the layer Team. The argument is the tool.</summary>
    public event EventHandler<string>? GiveToAgentRequested;

    /// <summary>«Dar a un agente…»: leads to the inspector, where the change is made and approved as always.</summary>
    [RelayCommand]
    private void GiveToAgent(ToolRow? row)
    {
        if (row is { CanBeGiven: true })
        {
            GiveToAgentRequested?.Invoke(this, row.Name);
        }
    }

    /// <summary>Builds the view for the template of the agents and the verification profile; each is optional.</summary>
    public void Load(IReadOnlyList<AgentDefinition> roster, VerificationProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var sets = new List<AgentToolSet>();
        foreach (var agent in roster)
        {
            sets.Add(EffectiveTools.For(agent, writes: true));
            sets.Add(EffectiveTools.For(agent, writes: false));
        }

        AgentSets = sets;
        HasAgents = roster.Count > 0;
        Groups.Clear();
        Groups.Add(ClaudeGroup(roster, sets));
        Groups.Add(CodexGroup(roster, sets));
        if (profile is not null)
        {
            Groups.Add(StepsGroup(profile));
        }

        Groups.Add(McpGroup());
    }

    private ToolGroup ClaudeGroup(IReadOnlyList<AgentDefinition> roster, List<AgentToolSet> sets)
    {
        var rows = new List<ToolRow>();
        foreach (var tool in ClaudeBuiltIns)
        {
            var own = sets.Where(s => s.Provider == ProviderId.ClaudeCode && s.Available.Contains(tool, StringComparer.Ordinal)).ToList();
            var all = own.Select(s => s.Agent).Distinct().ToList();
            var readsToo = own.Where(s => !s.WritesFiles).Select(s => s.Agent).ToHashSet();
            var who = all.Select(id => roster.First(a => a.Id == id)).Select(a => readsToo.Contains(a.Id) ? a.Name : _texts.Format("Tools_OnlyWriting", a.Name)).ToList();
            rows.Add(Row(tool, _texts.Get("Tools_Tool_" + tool), RiskOf(tool), who, canBeGiven: true));
        }

        return new ToolGroup(_texts.Get("Tools_Group_Claude"), _texts.Get("Tools_Group_ClaudeNote"), rows);
    }

    private ToolGroup CodexGroup(IReadOnlyList<AgentDefinition> roster, List<AgentToolSet> sets)
    {
        string[] Who(Func<AgentToolSet, bool> match) =>
            [.. sets.Where(s => s.Provider == ProviderId.CodexCli && match(s)).Select(s => s.Agent).Distinct().Select(id => roster.First(a => a.Id == id).Name)];

        var rows = new List<ToolRow>
        {
            Row("read-only", _texts.Get("Tools_Codex_ReadOnly"), ToolRisk.Low, Who(s => s.Sandbox == "read-only"), canBeGiven: false),
            Row("workspace-write", _texts.Get("Tools_Codex_WorkspaceWrite"), ToolRisk.Medium, Who(s => s.Sandbox == "workspace-write"), canBeGiven: false),
            Row("network", _texts.Get("Tools_Codex_Network"), ToolRisk.High, Who(s => s.Network == true), canBeGiven: false),
        };
        return new ToolGroup(_texts.Get("Tools_Group_Codex"), _texts.Get("Tools_Group_CodexNote"), rows);
    }

    private ToolGroup StepsGroup(VerificationProfile profile)
    {
        var rows = profile.Steps.Select(step => new ToolRow(
            step.Id,
            _texts.Format("Tools_Step_Description", step.Title, string.Join(' ', new[] { step.Executable }.Concat(step.Arguments)), _texts.Get("Tools_Step_Isolation_" + step.Isolation)),
            ToolRisk.Medium,
            _texts.Get("Tools_Risk_Medium"),
            AsksHello: true,
            _texts.Get("Tools_Hello_Profile"),
            _texts.Get("Tools_Step_Use")) { GiveName = _texts.Format("Tools_GiveName", step.Id) }).ToList();
        return new ToolGroup(_texts.Format("Tools_Group_Steps", profile.Name), _texts.Get("Tools_Group_StepsNote"), rows);
    }

    private ToolGroup McpGroup()
    {
        var rows = new List<ToolRow>
        {
            Row(SkillsMcpNames.SearchTool, _texts.Get("Tools_Mcp_SearchSkills"), ToolRisk.Low, [_texts.Get("Tools_Mcp_SkillsWhere")], canBeGiven: false),
            Row(SkillsMcpNames.RecommendTool, _texts.Get("Tools_Mcp_RecommendSkills"), ToolRisk.Low, [_texts.Get("Tools_Mcp_SkillsWhere")], canBeGiven: false),
            Row(ProjectHistoryNames.Tool, _texts.Get("Tools_Mcp_SearchHistory"), ToolRisk.Low, [_texts.Get("Tools_Mcp_HistoryWhere")], canBeGiven: false),
        };
        return new ToolGroup(_texts.Get("Tools_Group_Mcp"), _texts.Get("Tools_Group_McpNote"), rows);
    }

    private ToolRow Row(string name, string description, ToolRisk risk, IReadOnlyList<string> agents, bool canBeGiven)
    {
        // A tool of the list that asks Windows Hello is the one the analyzer calls high risk, by the barrier of the domain.
        var asksHello = risk == ToolRisk.High && PresencePolicy.AsksPresence(PresenceAction.GrantHighRiskCapability, importantApprovalsSetting: false);
        return new ToolRow(
            name,
            description,
            risk,
            _texts.Get("Tools_Risk_" + risk),
            asksHello,
            _texts.Get(asksHello ? "Tools_Hello_Yes" : "Tools_Hello_No"),
            agents.Count == 0 ? _texts.Get("Tools_NoAgents") : _texts.Format("Tools_Agents", string.Join(", ", agents)))
        {
            GiveName = _texts.Format("Tools_GiveName", name),
            CanBeGiven = canBeGiven,
        };
    }

    /// <summary>The risk of a built-in tool: the ones the analyzer calls high risk, those that write, and those that only read.</summary>
    private static ToolRisk RiskOf(string tool) =>
        HighRiskCapabilityRule.HighRiskTools.Contains(tool, StringComparer.Ordinal) ? ToolRisk.High
        : tool is "Edit" or "Write" ? ToolRisk.Medium
        : ToolRisk.Low;
}
