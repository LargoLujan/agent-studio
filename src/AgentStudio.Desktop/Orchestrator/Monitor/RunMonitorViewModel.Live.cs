using System.Collections.ObjectModel;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

// Phase 13 (docs/17 D-14, D-16 and D-12; F13-6 and F13-7, WP8): the monitor is the only reader of the events of its run
// (ADR-0036) and the one that implements the live projection. The events reach it through the queue with a single consumer it
// already had (the context of the UI, in order); each marks the nodes it changed, and a timer groups them: the state is built
// once, at most every 250 ms, however many events came. The UI thread does only what a burst costs once.

public sealed partial class RunMonitorViewModel : IRunLiveProjection
{
    /// <summary>A burst that starts after a quiet moment waits this long, so what comes together is one repaint and not two.</summary>
    internal static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(50);

    private readonly System.Threading.Lock _liveGate = new();
    private readonly HashSet<string> _liveDirty = new(StringComparer.Ordinal);
    private readonly List<(TaskId From, TaskId To)> _livePulses = [];
    private IReadOnlyList<AgentDefinition> _roster = [];
    private IReadOnlyDictionary<TaskId, IReadOnlyList<TaskId>> _dependencies = new Dictionary<TaskId, IReadOnlyList<TaskId>>();
    private ITimer? _liveTimer;
    private DateTimeOffset _lastLiveFlush = DateTimeOffset.MinValue;
    private long _liveGeneration;
    private bool _liveRunDirty;
    private RunLiveState? _live;
    private QuotaViewModel? _quota;

    /// <summary>A grouped change of the live state, on the UI thread, at most once every 250 ms per node.</summary>
    public event EventHandler<RunLiveChangedEventArgs>? Changed;

    /// <summary>The state of the run followed now, as the last grouped change left it; null while no run is followed.</summary>
    public RunLiveState? Current => IsAttached ? _live : null;

    /// <summary>The cards of the agents, rebuilt from the grouped state: the list of the monitor (D-14).</summary>
    public ObservableCollection<AgentCardViewModel> AgentCards { get; } = [];

    /// <summary>
    /// The agents of the template of the run, for the agents that have no task yet to show as «Libre» and for the name, the
    /// program and the model of each card. The orchestrator document gives it when it attaches the run; without it only the agents
    /// that have a task show.
    /// </summary>
    public IReadOnlyList<AgentDefinition> Roster
    {
        get => _roster;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _roster = value;
            AgentCards.Clear();
            MarkLiveRun();
        }
    }

    /// <summary>
    /// The dependencies of the plan, task by the tasks it waits for: with them the projection says which met dependency to pulse
    /// when the next task starts (D-14). The monitor does not read the plan, so the document gives it; without it there are no pulses.
    /// </summary>
    public IReadOnlyDictionary<TaskId, IReadOnlyList<TaskId>> PlanDependencies
    {
        get => _dependencies;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _dependencies = value;
        }
    }

    /// <summary>
    /// The quota of the account, one bar per window of each program, each with its origin (D-12): the monitor reads it again
    /// when an agent announces it. «no expuesto» (an empty track) when the program does not give it.
    /// </summary>
    public IReadOnlyList<MetricBarReading> QuotaBars { get; private set; } = [];

    /// <summary>The quota of the account the bars read; the shell gives it once.</summary>
    public QuotaViewModel? Quota
    {
        get => _quota;
        set
        {
            if (ReferenceEquals(_quota, value))
            {
                return;
            }

            if (_quota is not null)
            {
                _quota.PropertyChanged -= OnQuotaChanged;
            }

            _quota = value;
            if (_quota is not null)
            {
                _quota.PropertyChanged += OnQuotaChanged;
            }

            RefreshQuotaBars();
        }
    }

    private void OnQuotaChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshQuotaBars();

    /// <summary>Reads the quota bars again: after the quota changed or an agent of the run announced it.</summary>
    internal void RefreshQuotaBars()
    {
        var bars = new List<MetricBarReading>();
        if (_quota is { } quota)
        {
            foreach (var provider in new[] { quota.Claude, quota.Codex })
            {
                // The quota is read once it is first asked: before it, a provider has no windows to say.
                if (provider is null)
                {
                    continue;
                }

                var windows = provider.Windows.Select(w => w.Bar).OfType<MetricBarReading>().ToList();
                if (windows.Count > 0)
                {
                    bars.AddRange(windows);
                }
                else
                {
                    bars.Add(MetricBars.Empty(Texts.Format("Monitor_Bar_Quota", provider.Name), provider.Status, MetricOrigin.NotExposed));
                }
            }
        }

        QuotaBars = bars;
        OnPropertyChanged(nameof(QuotaBars));
    }

    // ---- grouping ----

    /// <summary>Marks the task and its agent as changed: the next grouped change says so.</summary>
    private void MarkLive(MonitorTaskViewModel task)
    {
        _liveDirty.Add(task.Id.Value);
        if (!string.IsNullOrWhiteSpace(task.Agent))
        {
            _liveDirty.Add(task.Agent);
        }

        ScheduleLive();
    }

    /// <summary>Marks something of the run itself as changed (its phase, its budget, an integration): a grouped change without nodes.</summary>
    private void MarkLiveRun()
    {
        _liveRunDirty = true;
        ScheduleLive();
    }

    private void ScheduleLive()
    {
        if (!IsAttached)
        {
            return;
        }

        // Without a context of the UI there is no UI thread to protect and nobody to group for: tests that drive the monitor
        // directly see every change at once.
        if (_ui is not { } ui)
        {
            FlushLive();
            return;
        }

        if (_liveTimer is not null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var since = now - _lastLiveFlush;
        var due = since >= LiveProjection.MinimumInterval ? CoalesceWindow : LiveProjection.MinimumInterval - since;
        var generation = _generation;
        _liveTimer = _time.CreateTimer(_ => ui.Post(__ => FlushWhenCurrent(generation), null), null, due, Timeout.InfiniteTimeSpan);
    }

    private void FlushWhenCurrent(int generation)
    {
        if (generation == _generation)
        {
            FlushLive();
        }
    }

    /// <summary>Publishes the grouped change: the state is built once for everything marked since the last one.</summary>
    internal void FlushLive()
    {
        RunLiveChangedEventArgs? change;
        lock (_liveGate)
        {
            change = BuildChange();
        }

        if (change is not null)
        {
            Changed?.Invoke(this, change);
        }
    }

    private RunLiveChangedEventArgs? BuildChange()
    {
        _liveTimer?.Dispose();
        _liveTimer = null;
        if (RunId is not { } run || Phase is not { } phase)
        {
            _liveDirty.Clear();
            _livePulses.Clear();
            _liveRunDirty = false;
            return null;
        }

        if (_liveDirty.Count == 0 && !_liveRunDirty)
        {
            return null;
        }

        var changed = _liveDirty.ToArray();
        var pulses = _livePulses.ToArray();
        _liveDirty.Clear();
        _livePulses.Clear();
        _liveRunDirty = false;
        var integrating = IsDelivering || Tasks.Any(t => t.Kind == WorkTaskKind.Integration && t.Status == WorkTaskStatus.Running);
        var state = LiveStateFactory.Build(run, phase, [.. Tasks], _roster, AgentRunsUsed, MaxAgentRuns, integrating, ++_liveGeneration);
        _live = state;
        _lastLiveFlush = _time.GetUtcNow();
        UpdateAgentCards(state);
        return new RunLiveChangedEventArgs(state, changed, pulses);
    }

    private void UpdateAgentCards(RunLiveState state)
    {
        var tasks = (IReadOnlyList<MonitorTaskViewModel>)[.. Tasks];
        foreach (var (agent, agentState) in state.Agents)
        {
            var card = AgentCards.FirstOrDefault(c => c.Id == agent);
            if (card is null)
            {
                card = new AgentCardViewModel(agent, _roster.FirstOrDefault(a => a.Id == agent), _time.LocalTimeZone);
                AgentCards.Add(card);
            }

            card.Update(agentState, tasks, ContextThresholds);
        }
    }

    /// <summary>The events of a task that start or end the live state of its node.</summary>
    private void MarkLive(MonitorTaskViewModel task, WorkTaskStatus from, WorkTaskStatus to)
    {
        // A pulse runs along each met dependency of a task that just started (D-14); the state is read as it is now.
        if (to == WorkTaskStatus.Running && from != WorkTaskStatus.Running && _dependencies.TryGetValue(task.Id, out var waitedFor))
        {
            foreach (var before in waitedFor)
            {
                if (Tasks.FirstOrDefault(t => t.Id == before) is { Status: WorkTaskStatus.Completed })
                {
                    _livePulses.Add((before, task.Id));
                }
            }
        }

        MarkLive(task);
    }

    private void ResetLive()
    {
        _liveTimer?.Dispose();
        _liveTimer = null;
        _liveDirty.Clear();
        _livePulses.Clear();
        _liveRunDirty = false;
        _live = null;
        AgentCards.Clear();
    }
}
