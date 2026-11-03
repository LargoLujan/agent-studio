using System.Collections.Specialized;
using System.ComponentModel;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// The bars of the status bar of the main window (docs/18 P3, docs/03 §2), always in view and in real time:
/// <list type="bullet">
/// <item>the context of the task in course (its monitor follows it step by step, P36), or with several the fullest and how
/// many there are; while the orchestrator plans, its context too, as one more (V33); without any, «sin tareas en curso»;</item>
/// <item>the quota of the account: Claude's 5 hours and 7 days, as its runs announce them, and Codex's only with the opt-in
/// of «Ajustes»;</item>
/// <item>the budget of the runs in course: the agent runs used against their maximum.</item>
/// </list>
/// The tooltip says the detail of each. The monitors say what changed on the UI thread, and the bars are built again at most
/// every <see cref="RefreshInterval"/>: a burst of steps is one repaint, and the interface never waits for them (docs/17
/// D-16). The quota is read again when a CLI announces it and every <see cref="QuotaInterval"/>, for its age. Locked (docs/16
/// D-13, ADR-0037), nothing of the work nor of the account is shown or named.
/// </summary>
public sealed partial class StatusStripViewModel : ObservableObject, IDisposable
{
    /// <summary>The shortest time between two builds of the bars (docs/17 D-16).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the quota is read again by itself, so its age («vista hace 12 min») stays true.</summary>
    public static readonly TimeSpan QuotaInterval = TimeSpan.FromMinutes(1);

    /// <summary>The shortest time between two reads of the quota that runs ask for.</summary>
    public static readonly TimeSpan QuotaReadSpacing = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> TaskProperties = new(StringComparer.Ordinal)
    {
        nameof(MonitorTaskViewModel.Status),
        nameof(MonitorTaskViewModel.Context),
        nameof(MonitorTaskViewModel.ContextBar),
    };

    private readonly QuotaViewModel? _quota;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private readonly List<RunMonitorViewModel> _monitors = [];
    private readonly List<IPlanningContext> _planners = [];

    /// <summary>The monitors and plannings of closed tabs, let go once what runs in them ends.</summary>
    private readonly HashSet<object> _releasing = [];
    private readonly HashSet<MonitorTaskViewModel> _tasks = [];
    private readonly ITimer? _quotaTimer;
    private ITimer? _pending;
    private DateTimeOffset? _lastBuilt;
    private DateTimeOffset? _lastQuotaRead;
    private bool _disposed;

    /// <param name="quota">The quota of the account, as «Consumo» shows it; without it the quota says «sin dato».</param>
    /// <param name="time">The clock of the spacing of the builds and of the reads of the quota.</param>
    /// <param name="uiContext">Where the bars are built; defaults to the creating thread's context.</param>
    public StatusStripViewModel(QuotaViewModel? quota, TimeProvider time, SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _quota = quota;
        _time = time;
        _ui = uiContext ?? SynchronizationContext.Current;
        if (quota is not null)
        {
            quota.PropertyChanged += OnQuotaChanged;

            // The quota is read on the UI thread, where «Consumo» reads it too: without one (a shell built by hand) only the runs
            // and Refresh ask for it.
            if (_ui is not null)
            {
                _quotaTimer = time.CreateTimer(_ => Post(() => _ = ReadQuotaAsync(force: true)), null, TimeSpan.Zero, QuotaInterval);
            }
        }

        Build();
    }

    /// <summary>The context of the task in course, the fullest of several, or «sin tareas en curso».</summary>
    [ObservableProperty]
    private MetricBarReading _context = MetricBars.Empty("Contexto", "sin tareas en curso");

    /// <summary>The windows of the quota of the account: Claude's 5 hours and 7 days, and Codex's with the opt-in.</summary>
    [ObservableProperty]
    private IReadOnlyList<MetricBarReading> _quotaBars = [];

    /// <summary>The budget of the run in course (the fullest of several); null without a run in course.</summary>
    [ObservableProperty]
    private MetricBarReading? _budget;

    /// <summary>The detail of every bar, one line each, for the tooltip; null while locked.</summary>
    [ObservableProperty]
    private string? _toolTip;

    /// <summary>Every bar of the strip in its order: the context, the windows of the quota and the budget; none while locked.</summary>
    [ObservableProperty]
    private IReadOnlyList<MetricBarReading> _bars = [];

    /// <summary>The interface is locked (docs/16 D-13): the strip shows and names nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBars))]
    private bool _isLocked;

    /// <summary>The bars are in view: the interface is not locked.</summary>
    public bool ShowsBars => !IsLocked;

    /// <summary>How many monitors the strip follows.</summary>
    public int TrackedMonitors => _monitors.Count;

    /// <summary>How many plannings («Orquestador» documents) the strip follows.</summary>
    public int TrackedPlanners => _planners.Count;

    /// <summary>
    /// Follows a monitor of a run (the one of an «Orquestador» document, or a run the tray follows); once for each. One being
    /// let go (<see cref="Release(RunMonitorViewModel)"/>) is kept.
    /// </summary>
    public void Track(RunMonitorViewModel monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _releasing.Remove(monitor);
        if (_disposed || _monitors.Contains(monitor))
        {
            return;
        }

        _monitors.Add(monitor);
        monitor.PropertyChanged += OnMonitorChanged;
        monitor.Tasks.CollectionChanged += OnTasksChanged;
        monitor.QuotaAnnounced += OnQuotaAnnounced;
        foreach (var task in monitor.Tasks)
        {
            Watch(task);
        }

        Request();
    }

    /// <summary>
    /// Follows the planning of an «Orquestador» document (V33): while it plans, its context counts as one more in course, so
    /// the bar never says «sin tareas en curso» while the orchestrator agent runs. Once for each; one being let go is kept.
    /// </summary>
    public void TrackPlanning(IPlanningContext planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _releasing.Remove(planner);
        if (_disposed || _planners.Contains(planner))
        {
            return;
        }

        _planners.Add(planner);
        planner.PropertyChanged += OnPlannerChanged;
        Request();
    }

    /// <summary>
    /// Lets go of the monitor of a tab that closed: now when its run is not active, or once it ends (a run goes on when its
    /// tab closes, and its context stays in view meanwhile). <see cref="Track"/> keeps it again.
    /// </summary>
    public void Release(RunMonitorViewModel monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (!_monitors.Contains(monitor))
        {
            return;
        }

        if (monitor.IsActive)
        {
            _releasing.Add(monitor);
        }
        else
        {
            Untrack(monitor);
        }
    }

    /// <summary>
    /// Lets go of the planning of a tab that closed: now when it is not planning, or once it ends (a planning goes on when its
    /// tab closes). <see cref="TrackPlanning"/> keeps it again.
    /// </summary>
    public void ReleasePlanning(IPlanningContext planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        if (!_planners.Contains(planner))
        {
            return;
        }

        if (planner.IsPlanningInProgress)
        {
            _releasing.Add(planner);
        }
        else
        {
            UntrackPlanning(planner);
        }
    }

    /// <summary>Builds the bars now, from what the monitors and the quota say.</summary>
    public void Refresh()
    {
        _pending?.Dispose();
        _pending = null;
        Build();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _quotaTimer?.Dispose();
        _pending?.Dispose();
        if (_quota is not null)
        {
            _quota.PropertyChanged -= OnQuotaChanged;
        }

        foreach (var monitor in _monitors)
        {
            monitor.PropertyChanged -= OnMonitorChanged;
            monitor.Tasks.CollectionChanged -= OnTasksChanged;
            monitor.QuotaAnnounced -= OnQuotaAnnounced;
        }

        foreach (var task in _tasks)
        {
            task.PropertyChanged -= OnTaskChanged;
        }

        foreach (var planner in _planners)
        {
            planner.PropertyChanged -= OnPlannerChanged;
        }

        _monitors.Clear();
        _planners.Clear();
        _releasing.Clear();
        _tasks.Clear();
    }

    partial void OnIsLockedChanged(bool value) => Build();

    private void Untrack(RunMonitorViewModel monitor)
    {
        _releasing.Remove(monitor);
        _monitors.Remove(monitor);
        monitor.PropertyChanged -= OnMonitorChanged;
        monitor.Tasks.CollectionChanged -= OnTasksChanged;
        monitor.QuotaAnnounced -= OnQuotaAnnounced;
        foreach (var task in monitor.Tasks)
        {
            task.PropertyChanged -= OnTaskChanged;
            _tasks.Remove(task);
        }

        Request();
    }

    private void UntrackPlanning(IPlanningContext planner)
    {
        _releasing.Remove(planner);
        _planners.Remove(planner);
        planner.PropertyChanged -= OnPlannerChanged;
        Request();
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RunMonitorViewModel.IsActive) && sender is RunMonitorViewModel { IsActive: true })
        {
            // A run started: Claude announces the quota at the start of each attempt.
            _ = ReadQuotaAsync(force: false);
        }

        if (sender is RunMonitorViewModel { IsActive: false } ended && _releasing.Contains(ended))
        {
            // The run of a tab that closed ended: the strip lets go of it.
            Untrack(ended);
            return;
        }

        Request();
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && sender is IEnumerable<MonitorTaskViewModel> all)
        {
            foreach (var gone in _tasks.Where(t => !all.Contains(t)).ToList())
            {
                gone.PropertyChanged -= OnTaskChanged;
                _tasks.Remove(gone);
            }

            foreach (var task in all)
            {
                Watch(task);
            }
        }
        else
        {
            foreach (var gone in e.OldItems?.OfType<MonitorTaskViewModel>() ?? [])
            {
                gone.PropertyChanged -= OnTaskChanged;
                _tasks.Remove(gone);
            }

            foreach (var task in e.NewItems?.OfType<MonitorTaskViewModel>() ?? [])
            {
                Watch(task);
            }
        }

        Request();
    }

    private void Watch(MonitorTaskViewModel task)
    {
        if (_tasks.Add(task))
        {
            task.PropertyChanged += OnTaskChanged;
        }
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || TaskProperties.Contains(e.PropertyName))
        {
            Request();
        }
    }

    private void OnPlannerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is IPlanningContext { IsPlanningInProgress: false } ended && _releasing.Contains(ended))
        {
            // The planning of a tab that closed ended: the strip lets go of it.
            UntrackPlanning(ended);
            return;
        }

        if (e.PropertyName is null or nameof(IPlanningContext.PlanningContext) or nameof(IPlanningContext.IsPlanningInProgress))
        {
            Request();
        }
    }

    private void OnQuotaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(QuotaViewModel.Claude) or nameof(QuotaViewModel.Codex) or nameof(QuotaViewModel.CodexOptedIn))
        {
            Request();
        }
    }

    /// <summary>A CLI announced its quota in a run: it is in the user's record now, and is read again.</summary>
    private void OnQuotaAnnounced(object? sender, EventArgs e) => _ = ReadQuotaAsync(force: false);

    /// <summary>
    /// Builds the bars again: now when the last build is older than <see cref="RefreshInterval"/>, else once that interval
    /// ends, whatever comes meanwhile.
    /// </summary>
    private void Request()
    {
        if (_disposed || _pending is not null)
        {
            return;
        }

        // Without a UI thread to come back to (a shell built by hand), the bars are built at once: a timer would build them on
        // another thread while the monitors change.
        var wait = _lastBuilt is { } last ? last + RefreshInterval - _time.GetUtcNow() : TimeSpan.Zero;
        if (wait <= TimeSpan.Zero || _ui is null)
        {
            Build();
            return;
        }

        _pending = _time.CreateTimer(_ => Post(Refresh), null, wait, Timeout.InfiniteTimeSpan);
    }

    private async Task ReadQuotaAsync(bool force)
    {
        if (_disposed || _quota is not { } quota)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (!force && _lastQuotaRead is { } last && now - last < QuotaReadSpacing)
        {
            return;
        }

        _lastQuotaRead = now;
        await quota.RefreshAsync().ConfigureAwait(true);
    }

    private void Build()
    {
        _lastBuilt = _time.GetUtcNow();
        if (IsLocked)
        {
            Context = MetricBars.Empty("Contexto", "bloqueado");
            QuotaBars = [];
            Budget = null;
            ToolTip = null;
            Bars = [];
            return;
        }

        var lines = new List<string>();
        var context = ContextBar(lines);
        var quota = QuotaReadings(lines);
        var budget = BudgetBar(lines);
        Context = context;
        QuotaBars = quota;
        Budget = budget;
        ToolTip = string.Join(Environment.NewLine, lines);
        List<MetricBarReading> bars = [context, .. quota];
        if (budget is not null)
        {
            bars.Add(budget);
        }

        if (!bars.SequenceEqual(Bars))
        {
            Bars = bars;
        }
    }

    /// <summary>
    /// The context of what runs: the tasks in course and the orchestrator while it plans (V33). One, its bar; several, the
    /// fullest and how many; none, «sin tareas en curso».
    /// </summary>
    private MetricBarReading ContextBar(List<string> lines)
    {
        var running = _monitors
            .Where(m => m.IsActive)
            .SelectMany(m => m.Tasks)
            .Where(t => t.Status == WorkTaskStatus.Running && t.ContextBar is not null)
            .Select(t => (Who: t.Id.ToString(), Line: $"Contexto de {t.Id} {t.Title}", Bar: t.ContextBar!))
            .Concat(_planners
                .Where(p => p.IsPlanningInProgress && p.PlanningContext is not null)
                .Select(p => (Who: PlannerName, Line: "Contexto del orquestador, que planifica", Bar: p.PlanningContext!)))
            .ToList();
        if (running.Count == 0)
        {
            lines.Add("Contexto: sin tareas en curso.");
            return MetricBars.Empty("Contexto", "sin tareas en curso");
        }

        foreach (var (_, line, bar) in running)
        {
            lines.Add($"{line}: {bar.Text}" + (bar.Detail is { Length: > 0 } detail ? $" ({detail})" : string.Empty));
        }

        var fullest = running.OrderByDescending(r => r.Bar.Fraction ?? -1).First();
        var label = running.Count == 1 ? $"Contexto · {fullest.Who}" : $"Contexto · {fullest.Who}, la más llena de {running.Count}";
        return fullest.Bar with { Label = label };
    }

    /// <summary>How the bar names the orchestrator while it plans: «Contexto · orquestador».</summary>
    private const string PlannerName = "orquestador";

    /// <summary>Claude's 5 hours and 7 days, and Codex's windows only with the opt-in; «sin dato» for what was not seen.</summary>
    private List<MetricBarReading> QuotaReadings(List<string> lines)
    {
        if (_quota is not { } quota)
        {
            lines.Add("Cuota: sin dato (no está disponible en esta composición).");
            return [MetricBars.Empty("Cuota", "sin dato")];
        }

        var bars = new List<MetricBarReading>();
        foreach (var (window, label) in new[] { ("five_hour", "Claude 5 h"), ("seven_day", "Claude 7 días") })
        {
            if (quota.Claude.Windows.FirstOrDefault(w => w.Window == window) is { Bar: { } bar })
            {
                bars.Add(bar with { Label = label });
                lines.Add($"{label}: {bar.Text}" + (bar.Detail is { Length: > 0 } detail ? $" · {detail}" : string.Empty));
            }
            else
            {
                bars.Add(MetricBars.Empty(label, "sin dato"));
                lines.Add($"{label}: {quota.Claude.Status}");
            }
        }

        if (quota.CodexOptedIn)
        {
            var windows = quota.Codex.Windows.Where(w => w.Bar is not null).ToList();
            foreach (var row in windows)
            {
                var label = "Codex " + row.ShortName;
                bars.Add(row.Bar! with { Label = label });
                lines.Add($"{label}: {row.Bar!.Text}" + (row.Bar!.Detail is { Length: > 0 } detail ? $" · {detail}" : string.Empty));
            }

            if (windows.Count == 0)
            {
                bars.Add(MetricBars.Empty("Codex", "sin leer"));
                lines.Add("Codex: " + quota.Codex.Status);
            }
        }

        return bars;
    }

    /// <summary>The budget of the runs in course: one, its bar; several, the fullest and how many; none, no bar.</summary>
    private MetricBarReading? BudgetBar(List<string> lines)
    {
        var budgets = _monitors.Where(m => m.IsActive && m.BudgetBar is not null).Select(m => (Monitor: m, Bar: m.BudgetBar!)).ToList();
        if (budgets.Count == 0)
        {
            return null;
        }

        foreach (var (monitor, bar) in budgets)
        {
            lines.Add($"Presupuesto de la ejecución {monitor.RunId}: {bar.Text}");
        }

        var fullest = budgets.OrderByDescending(b => b.Bar.Fraction ?? -1).First();
        return budgets.Count == 1 ? fullest.Bar : fullest.Bar with { Label = $"Presupuesto, la más gastada de {budgets.Count}" };
    }

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
