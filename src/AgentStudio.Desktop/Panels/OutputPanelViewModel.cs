using System.Collections.ObjectModel;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Common;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Panels;

/// <summary>
/// The panel «Salida» (docs/17 D-45, F13-18): the live activity of the task or the agent selected, or of the whole run when none is.
/// It is the activity the monitor already keeps (what the agents and the run did, newest last), filtered; it follows it through the
/// grouped state, so a chatty agent repaints it four times a second at most. Nothing here reads the events of the run.
/// </summary>
public sealed partial class OutputPanelViewModel : ObservableObject
{
    /// <summary>The most lines the panel keeps on screen.</summary>
    public const int MaxLines = 200;

    private readonly IUiTextProvider _texts;
    private RunMonitorViewModel? _monitor;
    private TaskId? _task;
    private AgentId? _agent;

    public OutputPanelViewModel(IUiTextProvider? texts = null)
    {
        _texts = texts ?? Texts.Current;
        _title = _texts.Get("Panels_Output_Run");
        _emptyText = _texts.Get("Panels_Output_Empty");
    }

    /// <summary>Says that the lines are texts of the agents, not messages of the system: they are data of the agents, shown cleaned of control characters.</summary>
    public string AgentTextNotice => _texts.Get("Panels_Output_AgentText");

    /// <summary>The lines of the activity of the selection, oldest first.</summary>
    public ObservableCollection<ActivityEntry> Lines { get; } = [];

    /// <summary>«Salida de la tarea T3», «Salida del agente exec1» or «Salida de la ejecución».</summary>
    [ObservableProperty]
    private string _title;

    /// <summary>What the panel says while it has no line: before the run starts, or when the selection did nothing yet.</summary>
    [ObservableProperty]
    private string _emptyText;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>Follows the run the monitor follows.</summary>
    public void Attach(RunMonitorViewModel monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (_monitor is not null)
        {
            _monitor.Changed -= OnChanged;
        }

        _monitor = monitor;
        monitor.Changed += OnChanged;
        Refresh();
    }

    /// <summary>Shows the output of one task; null shows the whole run. The canvas gives it when the person selects a node.</summary>
    public void SelectTask(TaskId? task)
    {
        _task = task;
        _agent = null;
        Lines.Clear();
        Title = task is { } id ? _texts.Format("Panels_Output_Task", id.Value) : _texts.Get("Panels_Output_Run");
        Refresh();
    }

    /// <summary>Shows the output of the tasks of one agent; null shows the whole run.</summary>
    public void SelectAgent(AgentId? agent)
    {
        _agent = agent;
        _task = null;
        Lines.Clear();
        Title = agent is { } id ? _texts.Format("Panels_Output_Agent", id.Value) : _texts.Get("Panels_Output_Run");
        Refresh();
    }

    private void OnChanged(object? sender, RunLiveChangedEventArgs e) => Refresh();

    /// <summary>Builds the lines again from the activity of the monitor.</summary>
    public void Refresh()
    {
        IEnumerable<ActivityEntry> source = _monitor?.Activity ?? [];
        if (_task is { } task)
        {
            source = source.Where(a => string.Equals(a.Task, task.Value, StringComparison.Ordinal));
        }
        else if (_agent is { } agent && _monitor is { } monitor)
        {
            var tasks = monitor.Tasks.Where(t => string.Equals(t.Agent, agent.Value, StringComparison.Ordinal)).Select(t => t.Id.Value).ToHashSet(StringComparer.Ordinal);
            source = source.Where(a => a.Task is not null && tasks.Contains(a.Task));
        }

        var wanted = source.TakeLast(MaxLines).ToList();

        // The activity only grows at its end: when the last line on screen is still in it, only the lines after it are added.
        var lastShown = Lines.Count > 0 ? Lines[^1] : null;
        var at = lastShown is null ? -1 : wanted.FindLastIndex(l => ReferenceEquals(l, lastShown));
        if (at >= 0)
        {
            foreach (var line in wanted.Skip(at + 1))
            {
                Lines.Add(line);
            }

            while (Lines.Count > MaxLines)
            {
                Lines.RemoveAt(0);
            }
        }
        else
        {
            Lines.Clear();
            foreach (var line in wanted)
            {
                Lines.Add(line);
            }
        }

        IsEmpty = Lines.Count == 0;
    }
}
