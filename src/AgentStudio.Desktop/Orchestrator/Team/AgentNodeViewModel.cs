using System.Windows;
using System.Windows.Automation;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>Which way a connector of the Team layer takes a cable.</summary>
public enum TeamConnectorFlow
{
    Input,
    Output,
}

/// <summary>
/// A connector of a node of the Team layer: the output of an agent (it «reparte», «revisa» and «trabaja en») or an input (of an
/// executor or a judge, or of a main folder of the repository, or of the whole project when <see cref="Folder"/> is null).
/// </summary>
public sealed partial class TeamConnectorViewModel : ObservableObject
{
    public TeamConnectorViewModel(object owner, TeamConnectorFlow flow, string title, string? folder = null, bool isWholeProject = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Owner = owner;
        Flow = flow;
        Title = title;
        Folder = folder;
        IsWholeProject = isWholeProject;
    }

    /// <summary>The agent node or the repository node it belongs to.</summary>
    public object Owner { get; }

    public TeamConnectorFlow Flow { get; }

    /// <summary>What the connector is called for a screen reader and next to the node: the folder, «todo el proyecto» or the kind of cable.</summary>
    public string Title { get; }

    /// <summary>The main folder of the repository this connector stands for, relative with <c>/</c>; null for any other connector.</summary>
    public string? Folder { get; }

    /// <summary>The connector of the repository that stands for all of it: an agent without folders works there, as today.</summary>
    public bool IsWholeProject { get; }

    [ObservableProperty]
    private Point _anchor;

    [ObservableProperty]
    private bool _isConnected;

    internal int Count { get; set; }

    internal void Add()
    {
        Count++;
        IsConnected = true;
    }

    internal void Remove()
    {
        Count = Math.Max(0, Count - 1);
        IsConnected = Count > 0;
    }

    public override string ToString() => Title;
}

/// <summary>
/// A node of the Team layer, which is also the card of the strip of the team (docs/17 D-07, D-12, D-14 and D-17): one agent of
/// the template with its program, model and effort, its live state and its bars. The same object is the node and the card, so
/// the two always say the same. The node is the template: changing it is changing the template (<see cref="Definition"/>).
/// </summary>
public sealed partial class AgentNodeViewModel : ObservableObject
{
    private readonly CanvasText _text;
    private readonly ContextHealthThresholds _thresholds;
    private AgentLiveState? _live;
    private int? _maxAgentRuns;

    public AgentNodeViewModel(AgentDefinition definition, Point location, CanvasText text, ContextHealthThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(text);
        _definition = definition;
        _text = text;
        _thresholds = thresholds ?? new ContextHealthThresholds();
        _location = location;
        Input = new TeamConnectorViewModel(this, TeamConnectorFlow.Input, text.Get("Canvas_Connector_In"));
        Output = new TeamConnectorViewModel(this, TeamConnectorFlow.Output, text.Get("Canvas_Connector_Out"));
        Refresh();
    }

    public TeamConnectorViewModel Input { get; }

    public TeamConnectorViewModel Output { get; }

    /// <summary>The agent of the template this node shows: it is replaced when the template is edited.</summary>
    [ObservableProperty]
    private AgentDefinition _definition;

    public AgentId Id => Definition.Id;

    public string Name => Definition.Name;

    public string RoleText => _text.Role(Definition.Role);

    /// <summary>The program of the agent and the company of its model: «Claude Code (Anthropic)».</summary>
    public string ProgramText => $"{Definition.Model.Provider} ({Definition.Model.Vendor})";

    public string ModelText => string.IsNullOrWhiteSpace(Definition.Model.Model) ? _text.Get("Canvas_Model_ProgramDefault") : Definition.Model.Model;

    public string EffortText => _text.Format("Canvas_Effort_Format", _text.Effort(Definition.Effort));

    /// <summary>Top-left corner in graph coordinates; Nodify writes it back when the node is dragged.</summary>
    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private Size _size;

    /// <summary>The agent of the task selected in the Plan layer: it shines here (selection linked, D-07).</summary>
    [ObservableProperty]
    private bool _isHighlighted;

    [ObservableProperty]
    private AgentLiveStatus _status = AgentLiveStatus.Idle;

    /// <summary>The state in words: «Libre», «Trabajando en T3», «Esperando cuota hasta las 14:05», «Bloqueado: …».</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private Tone _tone;

    /// <summary>An icon that says the state without colour: the card never relies on the colour alone (D-17).</summary>
    [ObservableProperty]
    private string _statusIcon = string.Empty;

    [ObservableProperty]
    private MetricBarReading _contextBar = null!;

    [ObservableProperty]
    private MetricBarReading _consumptionBar = null!;

    [ObservableProperty]
    private MetricBarReading _budgetBar = null!;

    /// <summary>The cable «trabaja en» of this agent is animated: its task is writing (docs/17 D-14).</summary>
    [ObservableProperty]
    private bool _isWriting;

    /// <summary>How a screen reader announces a change of state of the card: «Polite» in what is normal, «Assertive» in a question or a block (D-17).</summary>
    [ObservableProperty]
    private AutomationLiveSetting _liveSetting = AutomationLiveSetting.Polite;

    /// <summary>The whole name of the node and the card: «Ejecutor 2 · Claude Code · sonnet · esfuerzo medio · Trabajando en T3 · Contexto: 47 %, exacto».</summary>
    [ObservableProperty]
    private string _automationName = string.Empty;

    /// <summary>The problems of the analysis on this node, as sentences (the band says them all).</summary>
    [ObservableProperty]
    private string? _problemText;

    /// <summary>The live state it shows now; null when no run is followed.</summary>
    public AgentLiveState? Live => _live;

    /// <summary>The program the agent runs under is one that reports the context of its steps (Claude Code does, Codex <c>exec</c> does not).</summary>
    public bool ReportsContext => MetricBars.ReportsContext(Definition.Model.Provider);

    partial void OnDefinitionChanged(AgentDefinition value)
    {
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(ProgramText));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(EffortText));
        Refresh();
    }

    partial void OnProblemTextChanged(string? value) => Refresh();

    /// <summary>
    /// Takes what the live projection says of this agent (or nothing: no run is followed). Only the nodes the grouped change
    /// names are given this call (docs/17 D-16): the UI thread only paints.
    /// </summary>
    public void ApplyLive(AgentLiveState? state, int? maxAgentRuns)
    {
        _live = state;
        _maxAgentRuns = maxAgentRuns;
        Refresh();
    }

    private void Refresh()
    {
        var state = _live;
        Status = state?.Status ?? AgentLiveStatus.Idle;
        IsWriting = state is { Status: AgentLiveStatus.Working, Task: not null } && state.Task is not null && IsWriter;
        StatusText = StatusOf(state);
        StatusIcon = _text.Get("Canvas_StatusIcon_" + Status);
        Tone = ToneOf(Status);
        LiveSetting = Status is AgentLiveStatus.Asking or AgentLiveStatus.Blocked ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;
        ContextBar = ContextOf(state);
        ConsumptionBar = ConsumptionOf(state);
        BudgetBar = BudgetOf(state);
        AutomationName = string.Join(
            " · ",
            new[]
            {
                Name,
                ProgramText,
                ModelText,
                EffortText,
                StatusText,
                ContextBar.AccessibleName,
                ProblemText,
            }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private bool IsWriter => TeamRolesOf(Definition.Role);

    private static bool TeamRolesOf(AgentRole role) => Domain.Team.TeamRoles.Writes(role);

    private string StatusOf(AgentLiveState? state) => Status switch
    {
        AgentLiveStatus.Working => _text.Format("Canvas_Status_Working", state?.Task?.Value ?? "?"),
        AgentLiveStatus.WaitingForQuota when state?.WaitingUntil is { } until => _text.Format("Canvas_Status_WaitingUntil", until.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture)),
        AgentLiveStatus.Blocked when !string.IsNullOrWhiteSpace(state?.BlockReason) => _text.Format("Canvas_Status_BlockedWhy", state.BlockReason),
        var other => _text.Get("Canvas_Status_" + other),
    };

    private static Tone ToneOf(AgentLiveStatus status) => status switch
    {
        AgentLiveStatus.Working => Tone.Active,
        AgentLiveStatus.Finished => Tone.Success,
        AgentLiveStatus.Queued or AgentLiveStatus.WaitingForQuota or AgentLiveStatus.AwaitingApproval or AgentLiveStatus.Asking => Tone.Warning,
        AgentLiveStatus.Blocked => Tone.Error,
        _ => Tone.Neutral,
    };

    private MetricBarReading ContextOf(AgentLiveState? state)
    {
        var label = _text.Get("Canvas_Bar_Context");
        if (state?.Context is { } context)
        {
            return MetricBars.Context(label, context.Model, context.UsedTokens, context.Window, _thresholds);
        }

        // «No expuesto» with an empty track, never a bar at zero: Codex does not say it; Claude has not said it yet.
        return ReportsContext
            ? MetricBars.Empty(label, _text.Get("Canvas_Bar_NoObservationYet"))
            : MetricBars.Empty(label, _text.Get("Canvas_Bar_NotExposed"), MetricOrigin.NotExposed);
    }

    private MetricBarReading ConsumptionOf(AgentLiveState? state)
    {
        var label = _text.Get("Canvas_Bar_Consumption");
        if (state is null || state.AgentRuns == 0)
        {
            return MetricBars.Empty(label, _text.Get("Canvas_Bar_NoAttemptsYet"));
        }

        var parts = new[] { state.InputTokens, state.CachedInputTokens, state.OutputTokens };
        var known = parts.Where(p => p.Value is not null).ToList();
        if (known.Count == 0)
        {
            return MetricBars.Empty(label, _text.Get("Canvas_Bar_NotExposed"), MetricOrigin.NotExposed);
        }

        var origin = MetricOrigins.Weakest(known.Select(p => p.Origin).ToArray());
        var sum = known.Sum(p => p.Value!.Value);
        var source = string.Join("; ", known.Select(p => p.Source).Distinct(StringComparer.Ordinal));
        return MetricBars.Tokens(label, MetricValue<long>.Of(origin, sum, source), null);
    }

    private MetricBarReading BudgetOf(AgentLiveState? state) =>
        MetricBars.Budget(_text.Get("Canvas_Bar_Budget"), state?.AgentRuns ?? 0, _maxAgentRuns);

    public override string ToString() => AutomationName;
}
