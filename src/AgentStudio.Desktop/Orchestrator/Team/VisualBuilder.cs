using System.Collections.ObjectModel;
using AgentStudio.Application.Models;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Orchestrator.Team;

/// <summary>
/// The visual builder of a document (docs/17 §4, ADR-0048): what the canvas, the strip of the team, the inspector, the analysis
/// band, «Revisar el orden» and the chat of preparation share. The document creates it with its parts and the services of
/// <see cref="BuilderServices"/>; this class keeps the life that goes beyond one part: the grouped live state of the run, which
/// it gives to the nodes it names and no others, the repository read once without network, what the log taught of each model, and
/// the quota of the account for the header of the strip. It spends no quota: what costs asks the person first, in each part.
/// </summary>
public sealed partial class VisualBuilder : ObservableObject, IDisposable
{
    private readonly CanvasViewModel _canvas;
    private readonly BuilderServices _services;
    private readonly CanvasText _text;
    private readonly Func<bool> _reduceMotion;
    private readonly Func<ContextHealthThresholds> _thresholds;
    private readonly TimeProvider _time;
    private readonly HashSet<ConnectionViewModel> _pulsing = [];
    private bool _disposed;

    public VisualBuilder(
        CanvasViewModel canvas,
        BuilderServices services,
        CanvasText text,
        TimeProvider time,
        Func<bool> reduceMotion,
        Func<ContextHealthThresholds> thresholds,
        TeamEditorViewModel editor,
        AnalysisBandViewModel band,
        OrderReviewViewModel orderReview,
        PreparationChatViewModel chat,
        ProjectBriefViewModel brief)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(services);
        _canvas = canvas;
        _services = services;
        _text = text;
        _time = time;
        _reduceMotion = reduceMotion;
        _thresholds = thresholds;
        Editor = editor;
        Band = band;
        OrderReview = orderReview;
        Chat = chat;
        Brief = brief;
        _canvas.Team.Editor = editor;
        _canvas.Team.Builder = this;
        if (services.Live is { } live)
        {
            live.Changed += OnLiveChanged;
        }
    }

    public TeamEditorViewModel Editor { get; }

    public AnalysisBandViewModel Band { get; }

    public OrderReviewViewModel OrderReview { get; }

    public PreparationChatViewModel Chat { get; }

    /// <summary>The brief of the project (docs/17 D-10): a text of the person that reaches the agents that work, never the judge.</summary>
    public ProjectBriefViewModel Brief { get; }

    public BuilderServices Services => _services;

    /// <summary>The bars of quota of the account, one per program and window, in the header of the strip (D-12).</summary>
    public ObservableCollection<MetricBarReading> QuotaBars => _canvas.Team.QuotaBars;

    /// <summary>What the log taught of each model of this project; null until it was read.</summary>
    public LearnedModels? Learned { get; private set; }

    /// <summary>The windows of quota as last seen, for the analyzer (<c>quota.window-near</c>).</summary>
    internal IReadOnlyList<QuotaObservation> QuotaObservations { get; private set; } = [];

    /// <summary>The budget of the run in agent runs (<c>RunOptions.MaxAgentRuns</c>): a fix can raise it, and the next run starts with it.</summary>
    [ObservableProperty]
    private int _maxAgentRuns = 12;

    partial void OnMaxAgentRunsChanged(int value) => Band.Schedule();

    // ---------- The live state (D-14 and D-16) ----------

    private void OnLiveChanged(object? sender, RunLiveChangedEventArgs e) => ApplyLive(e);

    /// <summary>
    /// Applies a grouped change of the live state: the agents and the tasks the change names are given theirs, and no other node is
    /// touched (the monitor groups at most four changes per second and node, so a burst of events is one repaint). The animation
    /// of the cables and the pulses are gated here, by «Reducir movimiento» and the option of Windows.
    /// </summary>
    public void ApplyLive(RunLiveChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var animated = MotionPolicy.AnimationsEnabled(_reduceMotion());
        _canvas.Team.AnimationsEnabled = animated;
        _canvas.Team.ApplyLive(change.State, change.ChangedNodes);

        var thresholds = _thresholds();
        foreach (var node in _canvas.Nodes)
        {
            if (!change.ChangedNodes.Contains(node.Id.Value))
            {
                continue;
            }

            if (change.State.Tasks.TryGetValue(node.Id, out var live))
            {
                node.LiveText = TaskText(live);
                node.ContextBar = live.Context is { } context
                    ? MetricBars.Context(_text.Get("Canvas_Bar_Context"), context.Model, context.UsedTokens, context.Window, thresholds)
                    : null;
            }
        }

        foreach (var connection in _pulsing)
        {
            connection.IsPulsing = false;
        }

        _pulsing.Clear();
        if (!animated)
        {
            return;
        }

        foreach (var (from, to) in change.Pulses)
        {
            foreach (var connection in _canvas.Connections.Where(c => c.Source.Node.Id == from && c.Target.Node.Id == to))
            {
                connection.IsPulsing = true;
                _pulsing.Add(connection);
            }
        }
    }

    private string? TaskText(TaskLiveState live)
    {
        var parts = new List<string>();
        if (live.Attempt > 0)
        {
            parts.Add(_text.Format("Canvas_Live_Attempt", live.Attempt));
        }

        if (!string.IsNullOrWhiteSpace(live.BlockReason))
        {
            parts.Add(_text.Format("Canvas_Live_BlockedWhy", live.BlockReason));
        }
        else if (live.Writing)
        {
            parts.Add(_text.Get("Canvas_Live_Writing"));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Forgets the live state: no run is followed, and every card and node goes back to what it says without one.</summary>
    public void ClearLive()
    {
        _canvas.Team.ApplyLive(null);
        foreach (var node in _canvas.Nodes)
        {
            node.LiveText = null;
            node.ContextBar = null;
        }

        foreach (var connection in _pulsing)
        {
            connection.IsPulsing = false;
        }

        _pulsing.Clear();
    }

    // ---------- What is read once ----------

    /// <summary>Reads the repository of the project without network and shows it on the Team layer (D-10).</summary>
    public async Task LoadRepositoryAsync(string projectRoot, Func<Task<RepositoryReading>>? read = null)
    {
        var reading = read is not null
            ? await read().ConfigureAwait(true)
            : await new RepositoryReader(_services.Repository).ReadAsync(projectRoot, CancellationToken.None).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        _canvas.Team.Load(_canvas.Team.Roster, reading, _canvas.CaptureLayout());
    }

    /// <summary>Reads what the log taught of each model of this project (D-23); without the service the inspector says there is no data.</summary>
    public async Task LoadLearnedAsync(string projectRoot)
    {
        if (_services.Learning is not { } learning)
        {
            return;
        }

        try
        {
            Learned = await learning.LearnAsync(projectRoot, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // What the log taught is a help: a failure leaves the inspector with no data, never an error.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Learned = null;
        }
    }

    /// <summary>Reads the record of the quota and shows one bar per program and window, each with its origin; a program that says nothing is «no expuesto» with an empty track.</summary>
    public async Task RefreshQuotaAsync()
    {
        if (_services.Quota is not { } quota)
        {
            return;
        }

        IReadOnlyList<QuotaObservation> latest;
        try
        {
            latest = await quota.LatestAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The record is a help: a failure leaves the strip without quota bars, never an error.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return;
        }

        QuotaObservations = latest;
        QuotaBars.Clear();
        var now = _time.GetUtcNow();
        foreach (var observation in latest.OrderBy(o => o.Provider.Value, StringComparer.Ordinal).ThenBy(o => o.Window, StringComparer.Ordinal))
        {
            var seen = Ago(now - observation.ObservedAt);
            var resets = observation.ResetsAt?.ToLocalTime().ToString("HH:mm", _text.Provider.Culture);
            QuotaBars.Add(MetricBars.Quota(_text.Format("Canvas_Bar_Quota", observation.Provider, observation.Window), observation.Utilization, _text.Format("Canvas_Quota_Seen", seen), resets));
        }

        foreach (var provider in new[] { ProviderId.ClaudeCode, ProviderId.CodexCli }.Where(p => _canvas.Team.Roster.Any(a => a.Model.Provider == p) && latest.All(o => o.Provider != p)))
        {
            QuotaBars.Add(MetricBars.Empty(_text.Format("Canvas_Bar_Quota", provider, string.Empty).Trim(), _text.Get("Canvas_Quota_NotExposed"), MetricOrigin.NotExposed));
        }
    }

    private string Ago(TimeSpan age) => age < TimeSpan.FromMinutes(1)
        ? "<1 min"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min" : $"{(int)age.TotalHours} h";

    public void Dispose()
    {
        _disposed = true;
        if (_services.Live is { } live)
        {
            live.Changed -= OnLiveChanged;
        }

        Band.Dispose();
    }
}
