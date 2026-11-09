using System.Windows.Automation;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Observability.Context;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>
/// The card of one agent in the monitor (docs/17 D-12, D-14, D-17; F13-6, F13-7, F13-8): its state in words, with the dot of its
/// tone, and its bars with their origin: the context of the step (exact once the CLI reports it, an estimate before), the
/// context by parts, and the consumption of its finished attempts. It is rebuilt from the grouped state
/// (<see cref="IRunLiveProjection"/>) and never from the events, so it repaints at most four times a second.
/// </summary>
/// <remarks>
/// For UI Automation the card has a whole name («Ejecutor 2 · Claude Code · sonnet · Trabajando en T3 · contexto 47 %, exacto»)
/// and announces its changes: <see cref="LiveSetting"/> is <c>Assertive</c> when the agent asks or is blocked and <c>Polite</c>
/// otherwise. A card without its figures says so in words; a bar without a value is an empty track, never a zero.
/// </remarks>
public sealed partial class AgentCardViewModel : ObservableObject
{
    private readonly TimeZoneInfo _zone;
    private readonly IUiTextProvider _texts;

    public AgentCardViewModel(AgentId id, AgentDefinition? definition, TimeZoneInfo zone, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        Id = id;
        _zone = zone;
        _texts = texts ?? Texts.Current;
        Name = definition?.Name ?? id.Value;
        Program = definition is null ? null : DebateTexts.Provider(definition.Model.Provider);
        Model = definition?.Model.Model;
        _contextBar = MetricBars.Empty(_texts.Get("Monitor_Bar_Context"), _texts.Get("Monitor_Bar_NotStarted"));
        _consumptionBar = MonitorBars.ConsumptionBar([], _texts);
        _statusText = _texts.Get("Monitor_AgentStatus_Idle");
        _accessibleName = BuildName();
    }

    public AgentId Id { get; }

    public string Name { get; }

    /// <summary>The program of the agent («Claude Code»), from the template; null when the monitor does not know the template.</summary>
    public string? Program { get; }

    public string? Model { get; }

    [ObservableProperty]
    private AgentLiveStatus _status = AgentLiveStatus.Idle;

    /// <summary>The state in words (D-14).</summary>
    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private Tone _tone = Tone.Neutral;

    /// <summary>How a change of the card is announced (D-17).</summary>
    [ObservableProperty]
    private AutomationLiveSetting _liveSetting = AutomationLiveSetting.Polite;

    [ObservableProperty]
    private MetricBarReading _contextBar;

    /// <summary>The context by parts of the task in course (D-13); null while there is none to show.</summary>
    [ObservableProperty]
    private MetricBarReading? _partsBar;

    [ObservableProperty]
    private MetricBarReading _consumptionBar;

    /// <summary>The agent runs it used, in words.</summary>
    [ObservableProperty]
    private string _runsText = string.Empty;

    /// <summary>What a screen reader says of the whole card (D-17).</summary>
    [ObservableProperty]
    private string _accessibleName;

    /// <summary>Takes a new state of the agent: the grouped state, and the tasks it is about for the bars of parts and consumption.</summary>
    public void Update(AgentLiveState state, IReadOnlyList<MonitorTaskViewModel> tasks, ContextHealthThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(thresholds);
        Status = state.Status;
        StatusText = AgentLiveTexts.Status(state, _zone, _texts);
        Tone = AgentLiveTexts.ToneOf(state.Status);
        LiveSetting = AgentLiveTexts.LiveSetting(state.Status);
        ContextBar = state.Context is { } context
            ? MetricBars.Context(_texts.Get("Monitor_Bar_Context"), context.Model, context.UsedTokens, context.Window, thresholds)
            : MetricBars.Empty(_texts.Get("Monitor_Bar_Context"), _texts.Get("Monitor_Bar_NotStarted"));
        var mine = tasks.Where(t => string.Equals(t.Agent, Id.Value, StringComparison.Ordinal)).ToList();
        PartsBar = (mine.FirstOrDefault(t => t.Status == Domain.Work.WorkTaskStatus.Running) ?? mine.LastOrDefault(t => t.PartsBar is not null))?.PartsBar;
        ConsumptionBar = MonitorBars.ConsumptionBar([.. mine.SelectMany(t => t.FinishedAttempts)], _texts);
        RunsText = _texts.Format("Monitor_AgentCard_Runs", state.AgentRuns);
        AccessibleName = BuildName();
    }

    private string BuildName()
    {
        var parts = new List<string> { Name };
        if (Program is not null)
        {
            parts.Add(Program);
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            parts.Add(Model);
        }

        parts.Add(StatusText);
        parts.Add(_texts.Format("Monitor_AgentCard_Context", ContextBar.Text));
        return string.Join(" · ", parts);
    }

    public override string ToString() => AccessibleName;
}
