using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Orchestrator.Live;

// Phase 13 contract (docs/17 D-14 and D-16): the live state of a run, grouped. The monitor (RunMonitorViewModel) stays the only
// reader of the events of its run (ADR-0036) and implements it (WP8): monitor → a queue with a single consumer → this grouped
// projection → the interface, at most every 250 ms and per node. The canvas and the strip of the team (WP6) and the counter of
// the top bar (the integration) read it; none of them reads the events, and the UI thread only paints.

/// <summary>The state of an agent as the strip, its node and the top bar say it (docs/17 D-14); each has its icon, its text and the colour of its tone.</summary>
public enum AgentLiveStatus
{
    /// <summary>«Libre».</summary>
    Idle,

    /// <summary>«En cola»: its task is ready and its program has no free slot.</summary>
    Queued,

    /// <summary>«Trabajando en T3».</summary>
    Working,

    /// <summary>«Esperando cuota hasta las 14:05».</summary>
    WaitingForQuota,

    /// <summary>«Te pregunta algo»: it waits for the person's answer (D-29). Announced with <c>Assertive</c>.</summary>
    Asking,

    /// <summary>«Espera tu aprobación».</summary>
    AwaitingApproval,

    /// <summary>«Bloqueado», with why. Announced with <c>Assertive</c>.</summary>
    Blocked,

    /// <summary>«Terminado».</summary>
    Finished,
}

/// <summary>
/// The context of the step in progress of an agent or a task (docs/17 D-12 and D-13): the model, what the step uses and the
/// window of the model, each with its origin (in Codex <c>exec</c> the use is «no expuesto»), and the parts of the prompt before
/// the agent started, which Agent Studio knows in characters. What the CLI reports beyond their sum is the conversation and the
/// tools.
/// </summary>
public sealed record LiveContext(string? Model, MetricValue<long> UsedTokens, MetricValue<long> Window, IReadOnlyList<ContextPartSnapshot> Parts)
{
    /// <summary>The model and the use, for logs: the screens build their bars from the figures.</summary>
    public override string ToString() => $"{Model ?? "?"}: {UsedTokens.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}";
}

/// <summary>
/// What one agent is doing now (docs/17 D-14), for its card of the strip, its node of the Team layer and the counter of the top
/// bar: its state, its task, until when it waits for quota, why it is blocked, the context of its step, the tokens of its
/// finished attempts (input, cache and output, from <c>AgentRunFinished</c>) and the agent runs it used.
/// </summary>
public sealed record AgentLiveState(
    AgentId Agent,
    AgentLiveStatus Status,
    TaskId? Task,
    DateTimeOffset? WaitingUntil,
    string? BlockReason,
    LiveContext? Context,
    MetricValue<long> InputTokens,
    MetricValue<long> CachedInputTokens,
    MetricValue<long> OutputTokens,
    int AgentRuns)
{
    public override string ToString() => $"{Agent} · {Status}";
}

/// <summary>
/// What one task is doing now (docs/17 D-14): its state, its agent, its attempt, why it is blocked as the monitor says it (no
/// longer only «Bloqueada»), the context of its step, and whether it is writing now (the cable agent → folder is animated
/// while it does).
/// </summary>
public sealed record TaskLiveState(TaskId Task, WorkTaskStatus Status, AgentId? Agent, int Attempt, string? BlockReason, LiveContext? Context, bool Writing)
{
    public override string ToString() => $"{Task} · {Status}";
}

/// <summary>
/// The live state of a run at one moment (docs/17 D-14): its phase, each agent and each task, the agent runs used against the
/// budget, and whether it is integrating or delivering (the cable Integración → repositorio is animated while it is).
/// <see cref="Generation"/> grows with each change, so a reader that kept one can tell it is old.
/// </summary>
public sealed record RunLiveState(
    RunId Run,
    RunPhase Phase,
    IReadOnlyDictionary<AgentId, AgentLiveState> Agents,
    IReadOnlyDictionary<TaskId, TaskLiveState> Tasks,
    int AgentRunsUsed,
    int? MaxAgentRuns,
    bool Integrating,
    long Generation)
{
    /// <summary>How many agents are in each state: the counter of the top bar (D-43) says «2 trabajando · 1 te pregunta · 1 bloqueado».</summary>
    public int CountOf(AgentLiveStatus status) => Agents.Values.Count(a => a.Status == status);
}

/// <summary>
/// One grouped change of the live state (docs/17 D-16): the state after it, the nodes it changed (agent ids and task ids, as they
/// are), and the dependencies met in it whose next task started (a short pulse on that cable of the Plan layer).
/// </summary>
public sealed class RunLiveChangedEventArgs(RunLiveState state, IReadOnlyCollection<string> changedNodes, IReadOnlyList<(TaskId From, TaskId To)> pulses) : EventArgs
{
    public RunLiveState State { get; } = state;

    public IReadOnlyCollection<string> ChangedNodes { get; } = changedNodes;

    public IReadOnlyList<(TaskId From, TaskId To)> Pulses { get; } = pulses;
}

/// <summary>
/// The live state of the run in progress, grouped (docs/17 D-14 and D-16). <see cref="Changed"/> is raised on the UI thread, at
/// most once every <see cref="LiveProjection.MinimumInterval"/> per node: a burst of 100 events is one repaint. Nothing here
/// spends quota or reads the log: it is what the monitor already knows.
/// </summary>
public interface IRunLiveProjection
{
    /// <summary>The state now; null when no run is followed.</summary>
    RunLiveState? Current { get; }

    /// <summary>A grouped change, on the UI thread.</summary>
    event EventHandler<RunLiveChangedEventArgs>? Changed;
}

/// <summary>The rules of the grouping of the live state (docs/17 D-16 and F13-7).</summary>
public static class LiveProjection
{
    /// <summary>The shortest time between two changes of one node: four per second at most.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The most cables animated at once (D-15).</summary>
    public const int MaxAnimations = 12;

    /// <summary>The frames per second of an animation (D-15, <c>Timeline.DesiredFrameRate</c>).</summary>
    public const int AnimationFrameRate = 30;
}
