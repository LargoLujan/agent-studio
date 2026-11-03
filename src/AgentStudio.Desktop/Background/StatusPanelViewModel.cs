using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// What the panel reads besides the work itself, each piece only when it is composed (docs/16 D-15): the usage projected
/// from the verified logs, the stored headers of the runs (roster and budget), and the quota of the account as «Consumo»
/// shows it. Without one, its figures say «sin dato».
/// </summary>
public sealed record StatusPanelSources(IUsageReport? Usage = null, IRunRegistry? Runs = null, QuotaViewModel? Quota = null)
{
    public static StatusPanelSources None { get; } = new();
}

/// <summary>
/// The context of one task in course: its fraction (null: an empty track, only the words), the text of the monitor and the
/// bar the panel draws (<see cref="Bar"/>, the monitor's own, docs/18 P3).
/// </summary>
public sealed record ContextBarRow(string Task, double? Fraction, string Text, Tone Tone)
{
    public bool HasBar => Fraction is not null;

    /// <summary>The fraction as a percentage for the bar, from 0 to 100.</summary>
    public double Percent => Math.Clamp((Fraction ?? 0) * 100, 0, 100);

    /// <summary>The bar of the context of the task, as its monitor follows it; null when the task has none.</summary>
    public MetricBarReading? Bar { get; init; }

    /// <summary>What UI Automation names the row with, as an item of the list of the panel: the task and its whole line.</summary>
    public override string ToString() => $"Contexto de {Task}: {Text}";
}

/// <summary>A figure with its name: «Entrada · 1.200.000 · exacto · informado por el CLI».</summary>
public sealed record FigureRow(string Name, Figure Figure)
{
    public string Text => Figure.Text;
}

/// <summary>A window of the quota of the account: «Claude Code · 5 horas · 47 % · … · se reinicia hoy a las 14:05 · vista hace 12 min».</summary>
public sealed record QuotaRow(string Provider, string Text)
{
    /// <summary>The bar of the window (docs/18 P3); null for a line that only says why there is none.</summary>
    public MetricBarReading? Bar { get; init; }

    /// <summary>What UI Automation names the row with, as an item of the list of the panel: the bar's whole line, or the words.</summary>
    public override string ToString() => Bar?.AccessibleName ?? $"{Provider} · {Text}";
}

/// <summary>
/// One piece of work as the panel shows it: its state, and for a run its agents, tasks, context, tokens, budget, blocks,
/// errors and approvals; an empty section is not shown.
/// </summary>
public sealed class WorkCardViewModel
{
    public required string Title { get; init; }

    public required string State { get; init; }

    public Tone Tone { get; init; }

    public string? Detail { get; init; }

    public IReadOnlyList<string> Agents { get; init; } = [];

    public string? Tasks { get; init; }

    public IReadOnlyList<ContextBarRow> Context { get; init; } = [];

    public IReadOnlyList<FigureRow> Tokens { get; init; } = [];

    /// <summary>Where the tokens come from, or why there are none.</summary>
    public string? TokensNote { get; init; }

    public string? Budget { get; init; }

    /// <summary>The bar of the budget (docs/18 P3): the agent runs used against the maximum; null when not known.</summary>
    public MetricBarReading? BudgetBar { get; init; }

    /// <summary>The bar of the tokens of a debate against its maximum (docs/18 P3); null for a run.</summary>
    public MetricBarReading? TokensBar { get; init; }

    public IReadOnlyList<string> Blocks { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> Approvals { get; init; } = [];

    public bool HasAgents => Agents.Count > 0;

    public bool HasContext => Context.Count > 0;

    public bool HasTokens => Tokens.Count > 0 || TokensNote is not null;

    public bool HasBlocks => Blocks.Count > 0;

    public bool HasErrors => Errors.Count > 0;

    public bool HasApprovals => Approvals.Count > 0;
}

/// <summary>One project with work going on, and its work.</summary>
public sealed record ProjectStatusViewModel(string Name, string Root, IReadOnlyList<WorkCardViewModel> Work);

/// <summary>
/// The status panel of the tray (docs/16 D-15, F8-8): a projection of what already exists, project by project, with
/// nothing computed apart. For each run, from its monitor: the phase, the tasks by status, a bar of the context of each task
/// in course with its label (exact, estimated or not exposed, docs/14 D-12), the blocks with their reason, the errors and
/// the approvals that wait; from its stored header: the orchestrator, the judge and the agents in course with their
/// provider and model, and the budget (agent runs used against <c>MaxAgentRuns</c>); from the usage of its log: the tokens
/// of its finished attempts with their origin and the model the CLI reported. For a debate, its snapshot; for an audit or
/// a planning, what <c>IProjectActivity</c> says. The quota is the account's, once for all, with its reset and its age.
/// What is not known says «sin dato» or «no expuesto». While the interface is locked (D-13) it hides all of it and only
/// says how much goes on.
/// </summary>
public sealed partial class StatusPanelViewModel : ObservableObject
{
    /// <summary>The start of the tooltip, which the panel does not repeat under its own title.</summary>
    private const string SummaryHead = "Agent Studio · ";

    private readonly BackgroundWorkRegistry _work;
    private readonly TimeProvider _time;
    private readonly StatusPanelSources _sources;
    private readonly Dictionary<string, RunRecord?> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UsageReading> _usage = new(StringComparer.Ordinal);
    private Action? _openApp;

    /// <param name="openApp">«Abrir Agent Studio»; the background sets it to <see cref="ManagedExit.ShowWindow"/> (<see cref="OpenApp"/>).</param>
    public StatusPanelViewModel(BackgroundWorkRegistry work, TimeProvider time, StatusPanelSources? sources = null, Action? openApp = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(time);
        _work = work;
        _time = time;
        _sources = sources ?? StatusPanelSources.None;
        _openApp = openApp;
        OpenAppCommand = new RelayCommand(() => _openApp?.Invoke(), () => _openApp is not null);
        work.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<ProjectStatusViewModel> Projects { get; } = [];

    /// <summary>The quota of the account, one line per window of each provider in use.</summary>
    public ObservableCollection<QuotaRow> Quota { get; } = [];

    /// <summary>«Abrir Agent Studio».</summary>
    public IRelayCommand OpenAppCommand { get; }

    /// <summary>
    /// What «Abrir Agent Studio» does: the background gives <see cref="ManagedExit.ShowWindow"/>, so opening the window while
    /// the app waits to exit cancels that exit. Without it, the button is disabled.
    /// </summary>
    public Action? OpenApp
    {
        get => _openApp;
        set
        {
            _openApp = value;
            OpenAppCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>What goes on, in one line (the tooltip's summary).</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>When the figures read from the logs and the quota were last read.</summary>
    [ObservableProperty]
    private string? _updated;

    /// <summary>What the quota section says when it has no windows, or where its figures come from.</summary>
    [ObservableProperty]
    private string? _quotaNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWork))]
    [NotifyPropertyChangedFor(nameof(ShowsNothing))]
    private bool _hasWork;

    /// <summary>The interface is locked (D-13): the panel hides what goes on and only counts it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWork))]
    [NotifyPropertyChangedFor(nameof(ShowsNothing))]
    [NotifyPropertyChangedFor(nameof(LockedText))]
    private bool _isLocked;

    public bool ShowsWork => HasWork && !IsLocked;

    public bool ShowsNothing => !HasWork && !IsLocked;

    /// <summary>What the locked panel says: that the work goes on, how much, and how much waits for the person.</summary>
    public string LockedText
    {
        get
        {
            var active = _work.Active;
            var waiting = active.Select(WorkStatus.Of).Sum(w => w.AwaitingPerson);
            var going = active.Count == 0 ? "no hay trabajo en marcha" : $"el trabajo sigue: {WorkTexts.Count(active.Count, "trabajo en marcha", "trabajos en marcha")}";
            var person = waiting == 0 ? string.Empty : $"; {WorkTexts.Count(waiting, "espera", "esperan")} por ti";
            return $"Agent Studio está bloqueado: {going}{person}. Desbloquéalo para ver el detalle.";
        }
    }

    /// <summary>Reads again what comes from the logs (the headers and the usage of each run) and the quota, and shows it.</summary>
    public async Task RefreshAsync()
    {
        foreach (var run in _work.Active.OfType<RunWork>())
        {
            if (run.Project is not { } project || run.WorkId is not { } id)
            {
                continue;
            }

            var key = Key(project.ProjectId, id);
            if (!_headers.TryGetValue(key, out var header) && _sources.Runs is { } runs)
            {
                try
                {
                    header = await runs.FindAsync(project.ProjectId, new RunId(id), CancellationToken.None).ConfigureAwait(true);
                }
#pragma warning disable CA1031 // The header is behind a port: a failure is «sin dato» in the panel, never thrown into the UI.
                catch (Exception)
#pragma warning restore CA1031
                {
                    header = null;
                }

                _headers[key] = header;
            }

            _usage[key] = await ReadUsageAsync(run.ProjectRoot, id, header).ConfigureAwait(true);
        }

        if (_sources.Quota is { } quota)
        {
            await quota.RefreshAsync().ConfigureAwait(true);
        }

        Updated = $"Actualizado a las {_time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
        Rebuild();
    }

    /// <summary>Builds the panel again from the work as it is now and the figures last read.</summary>
    public void Rebuild()
    {
        var active = _work.Active;
        HasWork = active.Count > 0;
        var summary = TrayToolTip.Compose([.. active.Select(WorkStatus.Of)], ExitState.None, null, locked: false, _time.LocalTimeZone);
        Summary = summary.StartsWith(SummaryHead, StringComparison.Ordinal) ? summary[SummaryHead.Length..] : summary;
        OnPropertyChanged(nameof(LockedText));
        Projects.Clear();
        foreach (var project in active.GroupBy(w => w.ProjectRoot, StringComparer.OrdinalIgnoreCase))
        {
            Projects.Add(new ProjectStatusViewModel(WorkTexts.ProjectName(project.Key), project.Key, [.. project.Select(Card)]));
        }

        RebuildQuota(active);
    }

    private WorkCardViewModel Card(IBackgroundWork work) => work switch
    {
        RunWork run => RunCard(run),
        DebateWork debate => DebateCard(debate),
        _ => new WorkCardViewModel
        {
            Title = work.Title,
            State = work.StateText,
            Tone = work.Tone,
            Detail = work.Kind switch
            {
                BackgroundWorkKind.Audit => "Su detalle está en «Auditoría».",
                BackgroundWorkKind.Debate => "Su detalle está en «Debates».",
                _ => "Su detalle está en el «Orquestador».",
            },
        },
    };

    private WorkCardViewModel RunCard(RunWork run)
    {
        var monitor = run.Monitor;
        var key = run.Project is { } project && run.WorkId is { } id ? Key(project.ProjectId, id) : null;
        var header = key is not null && _headers.TryGetValue(key, out var stored) ? stored : null;
        var usage = key is not null && _usage.TryGetValue(key, out var read) ? read : null;
        return new WorkCardViewModel
        {
            Title = run.Title,
            State = run.StateText,
            Tone = run.Tone,
            Detail = monitor.PhaseDetail,
            Agents = RunAgents(monitor, header, usage, key is not null),
            Tasks = monitor.Tasks.Count == 0 ? "Tareas: sin dato" : "Tareas: " + TaskCounts(monitor.Tasks),
            Context = [.. monitor.Tasks.Where(t => t.Status == WorkTaskStatus.Running && RunsAnAgent(t.Kind)).Select(Bar)],
            Tokens = usage?.Totals is { } totals ? Figures(totals) : [],
            TokensNote = usage?.Note ?? (_sources.Usage is null ? "Tokens: sin dato (el consumo no está disponible en esta composición)." : "Tokens: sin dato todavía."),
            Budget = header is null
                ? $"Presupuesto: {WorkTexts.Count(monitor.AgentRunsUsed, "ejecución de agente usada", "ejecuciones de agente usadas")} · máximo sin dato (sin la cabecera de la ejecución)"
                : $"Presupuesto: {monitor.AgentRunsUsed.ToString(CultureInfo.InvariantCulture)} de {header.Options.MaxAgentRuns.ToString(CultureInfo.InvariantCulture)} ejecuciones de agente",
            BudgetBar = MetricBars.Budget("Presupuesto", monitor.AgentRunsUsed, header?.Options.MaxAgentRuns ?? monitor.MaxAgentRuns, header is null && monitor.MaxAgentRuns is null ? "sin la cabecera de la ejecución" : null),
            Blocks = [.. monitor.Tasks.Where(t => t.IsBlocked).Select(t => $"{t.Id} · {t.StatusLabel}")],
            Errors = RunErrors(monitor),
            Approvals = [.. monitor.Tasks.Where(t => t.PendingApproval is not null).Select(t => $"{t.Id} espera tu aprobación: {t.PendingApproval}")],
        };
    }

    /// <summary>
    /// The orchestrator, the judge and the agents of the tasks in course, from the roster frozen in the header of the run:
    /// provider and configured model, and the model the CLI reported in the attempts that finished.
    /// </summary>
    private static IReadOnlyList<string> RunAgents(RunMonitorViewModel monitor, RunRecord? header, UsageReading? usage, bool known)
    {
        if (header is null)
        {
            return [known ? "Agentes: sin dato (no se pudo leer la cabecera de la ejecución)" : "Agentes: sin dato (el proyecto no está abierto aquí)"];
        }

        var running = monitor.Tasks.Where(t => t.Status == WorkTaskStatus.Running && t.Agent is not null).Select(t => t.Agent!).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. header.Roster
                .Where(a => a.Role is AgentRole.Orchestrator or AgentRole.Judge || running.Contains(a.Id.Value))
                .OrderBy(a => a.Role is AgentRole.Orchestrator ? 0 : a.Role is AgentRole.Judge ? 2 : 1)
                .Select(a => AgentLine(a, running.Contains(a.Id.Value), usage?.ReportedModels.GetValueOrDefault(a.Id.Value))),
        ];
    }

    private static string AgentLine(AgentDefinition agent, bool working, string? reported)
    {
        var model = string.IsNullOrWhiteSpace(agent.Model.Model) ? "modelo predeterminado del CLI (no expuesto)" : $"modelo {agent.Model.Model}";
        var seen = reported is null ? string.Empty : $" · informado por el CLI: {reported}";
        var state = working ? " · en marcha" : string.Empty;
        return $"{agent.Id} · {NodeEditorViewModel.RoleLabel(agent.Role)} · {DebateTexts.Provider(agent.Model.Provider)} · {model}{seen}{state}";
    }

    private static string TaskCounts(IEnumerable<MonitorTaskViewModel> tasks) => string.Join(
        " · ",
        tasks.GroupBy(t => t.Status).OrderBy(g => Order(g.Key)).Select(g => WorkTexts.Count(g.Count(), One(g.Key), Many(g.Key))));

    private static int Order(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Running => 0,
        WorkTaskStatus.AwaitingApproval => 1,
        WorkTaskStatus.Blocked => 2,
        WorkTaskStatus.Pending => 3,
        WorkTaskStatus.Completed => 4,
        WorkTaskStatus.Failed => 5,
        WorkTaskStatus.Cancelled => 6,
        _ => 7,
    };

    private static string One(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Running => "en curso",
        WorkTaskStatus.AwaitingApproval => "espera aprobación",
        WorkTaskStatus.Blocked => "bloqueada",
        WorkTaskStatus.Pending => "pendiente",
        WorkTaskStatus.Completed => "completada",
        WorkTaskStatus.Failed => "fallida",
        WorkTaskStatus.Cancelled => "cancelada",
        WorkTaskStatus.Skipped => "omitida",
        _ => status.ToString(),
    };

    private static string Many(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Running => "en curso",
        WorkTaskStatus.AwaitingApproval => "esperan aprobación",
        WorkTaskStatus.Blocked => "bloqueadas",
        WorkTaskStatus.Pending => "pendientes",
        WorkTaskStatus.Completed => "completadas",
        WorkTaskStatus.Failed => "fallidas",
        WorkTaskStatus.Cancelled => "canceladas",
        WorkTaskStatus.Skipped => "omitidas",
        _ => status.ToString(),
    };

    private static bool RunsAnAgent(WorkTaskKind kind) => kind is WorkTaskKind.AgentWork or WorkTaskKind.Review or WorkTaskKind.Debate;

    /// <summary>
    /// The bar of a task in course: the monitor's own (docs/18 P3), which follows the attempt step by step with the windows the
    /// project knows, and the monitor's own line as its text; without a percentage (no window known, or the CLI does not expose
    /// it) the track is empty and the words say why, never a bar at zero.
    /// </summary>
    private static ContextBarRow Bar(MonitorTaskViewModel task)
    {
        var label = $"{task.Id} {task.Title}";
        var bar = task.ContextBar;
        var text = task.Context is { } line && line.StartsWith("Contexto: ", StringComparison.Ordinal) ? line["Contexto: ".Length..] : task.Context ?? bar?.Text ?? "sin dato";
        return new ContextBarRow(label, bar?.Fraction, text, task.ContextTone) { Bar = bar };
    }

    private static IReadOnlyList<string> RunErrors(RunMonitorViewModel monitor)
    {
        var errors = new List<string>();
        if (monitor.ErrorMessage is { Length: > 0 } error)
        {
            errors.Add(error);
        }

        if (monitor.ShowsDeadlockAlert)
        {
            errors.Add(monitor.DeadlockText);
        }

        errors.AddRange(monitor.HealthLines);
        if (monitor.Phase == RunPhase.Failed && monitor.ResultSummary is { Length: > 0 } summary)
        {
            errors.Add(summary);
        }

        return errors;
    }

    private static IReadOnlyList<FigureRow> Figures(UsageTotals totals) =>
    [
        new("Entrada", MetricText.Tokens(totals.InputTokens)),
        new("Caché leída", MetricText.Tokens(totals.CacheReadInputTokens)),
        new("Caché escrita", MetricText.Tokens(totals.CacheWriteInputTokens)),
        new("Salida", MetricText.Tokens(totals.OutputTokens)),
        new("Razonamiento", MetricText.Tokens(totals.ReasoningOutputTokens)),
        new("Coste", MetricText.Cost(totals.CostUsd)),
    ];

    private WorkCardViewModel DebateCard(DebateWork debate)
    {
        var snapshot = debate.Snapshot();
        if (snapshot is null)
        {
            return new WorkCardViewModel { Title = debate.Title, State = debate.StateText, Tone = debate.Tone, Detail = "Su detalle está en «Debates»." };
        }

        var awaiting = snapshot.Awaiting;
        return new WorkCardViewModel
        {
            Title = debate.Title,
            State = debate.StateText,
            Tone = debate.Tone,
            Detail = "Su detalle está en «Debates».",
            Agents = [.. snapshot.Participants.Select(p => $"{p.Label} · {p.Agent} · {DebateTexts.Role(p.Role)} · {DebateTexts.Provider(p.Provider)} · {(string.IsNullOrWhiteSpace(p.Model) ? "modelo predeterminado del CLI (no expuesto)" : $"modelo {p.Model}")}")],
            Tokens = [new FigureRow("Tokens", MetricText.Tokens(snapshot.Tokens))],
            TokensNote = "Del debate, por turno (como en la sala).",
            TokensBar = MetricBars.Tokens("Tokens del debate", snapshot.Tokens, snapshot.Limits.MaxTotalTokens),
            Budget = $"Presupuesto: {snapshot.AgentRuns.ToString(CultureInfo.InvariantCulture)} de {snapshot.MaxAgentRuns.ToString(CultureInfo.InvariantCulture)} ejecuciones de agente",
            BudgetBar = MetricBars.Budget("Presupuesto", snapshot.AgentRuns, snapshot.MaxAgentRuns),
            Blocks = awaiting is { Reason: DebateAwaitReason.ProviderLimit } limit
                ? [limit.Until is { } until ? $"Límite de uso del proveedor hasta las {TimeZoneInfo.ConvertTime(until, _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture)}" : DebateTexts.Await(limit.Reason)]
                : [],
            Approvals = awaiting is { Reason: DebateAwaitReason.HumanResolution or DebateAwaitReason.FailedTurn } person ? [DebateTexts.Await(person.Reason)] : [],
        };
    }

    /// <summary>The usage of the finished attempts of one run, from its verified log (the projection of «Consumo»).</summary>
    private async Task<UsageReading> ReadUsageAsync(string root, string run, RunRecord? header)
    {
        if (_sources.Usage is not { } usage)
        {
            return new UsageReading(null, "Tokens: sin dato (el consumo no está disponible en esta composición).", new Dictionary<string, string>(StringComparer.Ordinal));
        }

        try
        {
            var to = _time.GetUtcNow().AddMinutes(1);
            var from = header?.CreatedAt ?? DateTimeOffset.UnixEpoch;
            var report = await usage.QueryAsync([root], new UsageQuery(from, to, _time.LocalTimeZone, [], new UsageFilter(Runs: [run])), CancellationToken.None).ConfigureAwait(true);
            if (report.Excluded.FirstOrDefault() is { } excluded)
            {
                return new UsageReading(null, $"Tokens: sin dato ({excluded.Reason}).", new Dictionary<string, string>(StringComparer.Ordinal));
            }

            var totals = report.Groups.FirstOrDefault(g => g.Key.Count == 0)?.Totals;
            var models = report.Attempts
                .Where(a => a.ReportedModel is not null)
                .GroupBy(a => a.Agent.Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(a => a.FinishedAt).Last().ReportedModel!, StringComparer.Ordinal);
            var note = totals is not { Attempts: > 0 } finished
                ? "Tokens: ningún intento de esta ejecución ha terminado todavía."
                : $"De {WorkTexts.Count(finished.Attempts, "intento terminado", "intentos terminados")} de esta ejecución; el intento en curso cuenta cuando termina.";
            return new UsageReading(totals is { Attempts: > 0 } ? totals : null, note, models);
        }
#pragma warning disable CA1031 // The usage is behind a port: a failure is «sin dato» in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new UsageReading(null, $"Tokens: sin dato (no se pudo leer el consumo: {ex.Message}).", new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    /// <summary>The quota of the account (Claude and Codex, and Gemini only when an agent in course uses it), as «Consumo» shows it.</summary>
    private void RebuildQuota(IReadOnlyList<IBackgroundWork> active)
    {
        Quota.Clear();
        if (_sources.Quota is not { } quota)
        {
            QuotaNote = "Cuota: sin dato (no está disponible en esta composición).";
            return;
        }

        var providers = new List<ProviderQuota> { quota.Claude, quota.Codex };
        if (active.OfType<DebateWork>().Any(d => d.Snapshot()?.Participants.Any(p => p.Provider == ProviderId.GeminiCli) == true)
            || _headers.Values.Any(h => h?.Roster.Any(a => a.Model.Provider == ProviderId.GeminiCli) == true))
        {
            providers.Add(quota.Gemini);
        }

        foreach (var provider in providers)
        {
            if (provider.Windows.Count == 0)
            {
                Quota.Add(new QuotaRow(provider.Name, provider.Status));
                continue;
            }

            foreach (var window in provider.Windows)
            {
                var parts = new List<string> { window.Name, window.Utilization.Text };
                if (window.Resets is { } resets)
                {
                    parts.Add(resets);
                }

                if (window.Reached is { } reached)
                {
                    parts.Add(reached);
                }

                parts.Add(window.Seen);
                Quota.Add(new QuotaRow(provider.Name, string.Join(" · ", parts)) { Bar = window.Bar is { } bar ? bar with { Label = $"{provider.Name} · {window.Name}" } : null });
            }
        }

        QuotaNote = "Cuota de tu cuenta (no del proyecto), tal como la anunció cada proveedor.";
    }

    private static string Key(string projectId, string run) => projectId + "/" + run;

    /// <summary>The figures read from the log of a run: its totals (null without a finished attempt), the note and the models the CLI reported, by agent.</summary>
    private sealed record UsageReading(UsageTotals? Totals, string Note, IReadOnlyDictionary<string, string> ReportedModels);
}
