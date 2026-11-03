using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

// Phase 2 contract, implemented by the CLI adapters (Providers) and extended in Phase 4 (docs/14 section 5.2): usage per
// model and per step, compactions, context occupancy and local MCP servers; and in Phase 8 (docs/16 D-41): the class of the
// end of an attempt and the window a limit is about. See docs/04-contratos.md.

/// <summary>What an adapter can really do; the orchestrator plans with this instead of assuming.</summary>
public sealed record ProviderCapabilities(
    bool StreamsEvents,
    bool SupportsStructuredOutput,
    bool SupportsResume,
    bool SupportsInteractiveApproval,
    bool ReportsTokenUsage,
    bool ReportsSubscriptionQuota,
    int MaxConcurrentRuns)
{
    /// <summary>
    /// Whether the adapter reports the context occupancy of each step (<see cref="ContextObserved"/>) and its compactions
    /// (<see cref="CompactionObserved"/>), docs/14 D-09. Claude Code does; <c>codex exec</c> and Gemini do not, so there
    /// it is «no expuesto», never zero.
    /// </summary>
    public bool ReportsContextOccupancy { get; init; }
}

/// <summary>
/// Permissions translated by each adapter to its own flags (permission mode, sandbox, allowed tools...).
/// <para>
/// Semantics: a switch set to false is always enforced (the capability is denied or the run is read-only). A switch
/// set to true is honoured as far as the CLI allows; each provider documents what it cannot do:
/// Claude Code maps <c>AllowShell</c>/<c>AllowNetwork</c> to pre-approved tools (<c>Bash</c>, <c>WebFetch</c>,
/// <c>WebSearch</c>); Codex has no per-tool allowlist (the sandbox decides) and Gemini has no sandbox switch.
/// </para>
/// </summary>
public sealed record PermissionProfile(
    bool AllowFileWrites,
    bool AllowShell,
    bool AllowNetwork,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DeniedTools,
    IReadOnlyList<string> AdditionalDirectories);

/// <param name="Purpose">What this run is for, in one line; used as the prompt-journal summary when present.</param>
/// <param name="TaskId">Task the run belongs to, if any; recorded in the prompt journal.</param>
/// <param name="Tags">Extra journal tags (normalised by the journal).</param>
/// <param name="SkillsDirectory">
/// Session directory prepared by an <c>ISkillMaterializer</c> with only the skills enabled for this run, in the layout
/// the target CLI expects; null when no skills are enabled.
/// </param>
/// <param name="McpServers">
/// Local MCP servers the session may use (docs/14 D-30); null or empty for none, which is every run but the assisted
/// skill recommendation. An adapter refuses them for a profile that writes files, has a shell or has network, and the
/// session never loads any other server (<c>--strict-mcp-config</c>).
/// </param>
/// <param name="PersistSession">
/// Whether the CLI keeps the session on disk (docs/15 D-16). False starts a new session that is not saved and cannot be
/// resumed: Claude Code <c>--no-session-persistence</c>, Codex <c>--ephemeral</c> (with <c>-c history.persistence=none</c>).
/// Every turn of a debate is one; the judge keeps its session for its correction. A request that does not persist and also
/// resumes (<see cref="ResumeSessionId"/>) is refused by the adapters: the two exclude each other.
/// </param>
/// <param name="DebateId">
/// The debate (<c>deb-…</c>) or the whole-project audit (<c>aud-…</c>) the run belongs to, if any (D-45): the prompt journal
/// records it and tags the entry <c>#debate</c> or <c>#auditoria</c>.
/// </param>
public sealed record AgentRunRequest(
    AgentDefinition Agent,
    string Prompt,
    string WorkingDirectory,
    PermissionProfile Permissions,
    string? OutputJsonSchema = null,
    string? ResumeSessionId = null,
    string? Purpose = null,
    string? TaskId = null,
    IReadOnlyList<string>? Tags = null,
    string? SkillsDirectory = null,
    IReadOnlyList<McpServerSpec>? McpServers = null,
    bool PersistSession = true,
    string? DebateId = null)
{
    /// <summary>
    /// The <c>AgentRunStarted</c> this launch was announced with (docs/17 D-02), set by whoever recorded it just before the
    /// launch: the prompt journal writes it at the end of the result line (<c>JournalResultAnchor</c>), so the journal
    /// entry can be matched with the anchored start. Null for a launch nobody announced (a probe, a test): its entry then
    /// never matches, and stays in quarantine as memory.
    /// </summary>
    public AgentRunAnchor? Anchor { get; init; }
}

/// <summary>
/// What identifies the <c>AgentRunStarted</c> of one launch in the decision log (docs/17 D-02): the unit of the log it
/// belongs to (<paramref name="Run"/>: the orchestration run, the debate <c>deb-…</c>, the audit <c>aud-…</c> or the
/// auxiliary run <c>aux-…</c>), its task (<paramref name="Task"/>: null for the planner, the judge of a run and the
/// auxiliary runs) and its attempt, exactly as that entry records them.
/// </summary>
public sealed record AgentRunAnchor
{
    /// <exception cref="ArgumentException">The run is blank, the task is blank but not null, or the attempt is not positive.</exception>
    public AgentRunAnchor(string run, string? task, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(run);
        if (task is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(task);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        Run = run;
        Task = task;
        Attempt = attempt;
    }

    public string Run { get; }

    public string? Task { get; }

    public int Attempt { get; }
}

/// <summary>
/// A local MCP server for one session (docs/14 D-30): its name in the session (the <c>mcp__&lt;name&gt;__</c> prefix of
/// its tools), the executable and arguments the CLI launches over stdio, and the tools the session may call without
/// asking (the allow rules; in <c>dontAsk</c> anything else is denied). Text never goes in the arguments.
/// </summary>
public sealed record McpServerSpec(string Name, string Command, IReadOnlyList<string> Arguments, IReadOnlyList<string> AllowedTools);

/// <summary>
/// The usage of one model in a run (Claude's <c>result.modelUsage</c>, docs/14 section 3.1): the main loop, the auxiliary
/// models and the subagents each have one. <paramref name="CostBasis"/> is what the CLI says the cost is based on
/// (<c>list</c>, <c>managed</c> or <c>unknown</c>), null when it says nothing.
/// </summary>
public sealed record ModelUsage(
    string Model,
    MetricValue<long> InputTokens,
    MetricValue<long> OutputTokens,
    MetricValue<long> CacheReadInputTokens,
    MetricValue<long> CacheWriteInputTokens,
    MetricValue<decimal> CostUsd,
    MetricValue<long> ContextWindow,
    string? CostBasis);

/// <summary>
/// The usage of a run as the provider reported it, each figure with its origin. <see cref="InputTokens"/> is the whole
/// prompt: fresh input plus the cache reads (<see cref="CachedInputTokens"/>) and the cache writes
/// (<see cref="CacheWriteInputTokens"/>). <see cref="OutputTokens"/> includes the reasoning
/// (<see cref="ReasoningOutputTokens"/>). The CLIs report totals, of the run or of the whole resumed session (docs/14
/// D-02): the attempt's own usage is a projection.
/// </summary>
public sealed record TokenUsage(
    MetricValue<long> InputTokens,
    MetricValue<long> OutputTokens,
    MetricValue<long> CachedInputTokens,
    MetricValue<long> ContextWindow,
    MetricValue<decimal> CostUsd)
{
    /// <summary>Tokens written to the prompt cache (Claude <c>cache_creation_input_tokens</c>, Codex <c>cache_write_input_tokens</c>).</summary>
    public MetricValue<long> CacheWriteInputTokens { get; init; } = MetricValue<long>.NotExposed("the adapter does not report cache writes");

    /// <summary>Reasoning tokens (Codex <c>reasoning_output_tokens</c>).</summary>
    public MetricValue<long> ReasoningOutputTokens { get; init; } = MetricValue<long>.NotExposed("the adapter does not report reasoning tokens");

    /// <summary>The usage of each model (Claude <c>modelUsage</c>); empty when the CLI gives no breakdown (Codex, Gemini).</summary>
    public IReadOnlyList<ModelUsage> PerModel { get; init; } = [];

    /// <summary>Turns of the agent loop (Claude <c>num_turns</c>); null when the CLI does not say.</summary>
    public int? Turns { get; init; }
}

public enum AgentRunStatus
{
    Succeeded,
    Failed,
    Cancelled,
    LimitReached,
    RateLimited,
}

/// <summary>
/// Why an attempt ended (docs/16 D-41, F8-1). Each class has its policy in the engine. The class refines the status of the
/// outcome and never replaces it: <see cref="AgentRunStatus"/> does not change, and a <see cref="AgentRunStatus.RateLimited"/>
/// run whose class is <see cref="Other"/> is still a limit, of a kind the adapter could not tell. What an adapter does not
/// recognise in a captured message of its CLI is <see cref="Other"/>, never a guess.
/// </summary>
public enum AttemptEndClass
{
    /// <summary>The turn completed: the run succeeded.</summary>
    Completed,

    /// <summary>
    /// A usage window of the subscription ran out (Claude's five-hour or weekly window, Codex's usage limit): the task waits
    /// for that window's reset plus the margin, and the access is checked before it goes on (D-36 to D-38).
    /// </summary>
    WindowQuota,

    /// <summary>The provider throttled the requests or is overloaded (a limit per minute, an overloaded API): a short, progressive wait in the same session.</summary>
    RateOrOverload,

    /// <summary>The context window of the model is full: the next attempt goes in a new session, with the handoff the system builds (D-42).</summary>
    ContextExhausted,

    /// <summary>The network failed on the way to the provider: a bounded retry with a progressive wait, in the same session.</summary>
    Network,

    /// <summary>The CLI has no session, or it expired: blocked with the command that fixes it (<c>claude auth login</c>, <c>codex login</c>), with no retries.</summary>
    SignedOut,

    /// <summary>A budget of the attempt ended it (<see cref="AgentRunEnd.Budget"/>: turns, cost or time), as <see cref="AgentRunStatus.LimitReached"/> says today.</summary>
    AttemptBudget,

    /// <summary>The app stopped with the attempt going. Only recovery records it, never an adapter: the next attempt goes in a new session, with the handoff (D-43).</summary>
    Interrupted,

    /// <summary>The person or the engine cancelled the attempt: a cancelled run, an abandoned task.</summary>
    Cancelled,

    /// <summary>Anything else, and whatever the adapter did not recognise.</summary>
    Other,
}

/// <summary>Which budget of the attempt ended it (docs/16 D-41).</summary>
public enum AttemptBudgetKind
{
    /// <summary>The turns of the agent loop (Claude <c>error_max_turns</c>).</summary>
    Turns,

    /// <summary>The cost (Claude <c>error_max_budget_usd</c>).</summary>
    Cost,

    /// <summary>The time: the maximum duration of the run, or the timeout of the task.</summary>
    Time,
}

/// <summary>
/// How an attempt ended, classified (docs/16 D-41): its <paramref name="Class"/> and, for a window quota, the window that ran
/// out as the CLI names it (<paramref name="LimitType"/>, Claude's <c>rateLimitType</c>) and when it resets
/// (<paramref name="ResetsAt"/>), which is the reset the task waits for (D-36). Both are null when the CLI does not say
/// (<c>codex exec</c>): then the wait is the progressive one, «estimada». For a budget, which one (<paramref name="Budget"/>),
/// null when the CLI does not say.
/// </summary>
public sealed record AgentRunEnd(AttemptEndClass Class, string? LimitType = null, DateTimeOffset? ResetsAt = null, AttemptBudgetKind? Budget = null)
{
    /// <summary>
    /// The class the status alone implies, for an outcome no adapter classified: a success completed, a cancellation was
    /// cancelled and a limit reached was a budget (which one, unknown). A failure and a rate limit are not recognised
    /// (<see cref="AttemptEndClass.Other"/>): the status still says what it said.
    /// </summary>
    public static AgentRunEnd Implied(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Succeeded => new(AttemptEndClass.Completed),
        AgentRunStatus.Cancelled => new(AttemptEndClass.Cancelled),
        AgentRunStatus.LimitReached => new(AttemptEndClass.AttemptBudget),
        AgentRunStatus.Failed or AgentRunStatus.RateLimited => new(AttemptEndClass.Other),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown agent run status."),
    };
}

/// <summary>
/// Final result of a run. Status rules shared by every provider:
/// <list type="bullet">
/// <item><c>Cancelled</c>: the caller cancelled or discarded the run.</item>
/// <item><c>LimitReached</c>: a budget ended the run (turns, cost or time, including the runner timeout).</item>
/// <item><c>RateLimited</c>: the provider reported a quota or rate limit (a <c>RateLimitSignaled</c> event accompanies it).</item>
/// <item><c>Failed</c>: anything else that did not complete the turn.</item>
/// <item>Structured output: when <see cref="AgentRunRequest.OutputJsonSchema"/> was requested and no valid JSON came
/// back, the status is <c>Failed</c>, <c>FinalText</c> is kept and <c>Error</c> explains why. On
/// success <c>StructuredOutputJson</c> holds the JSON. Providers whose <c>SupportsStructuredOutput</c> is false
/// can only succeed if the model answered with valid JSON by itself.</item>
/// </list>
/// </summary>
/// <param name="End">
/// The class of the end (docs/16 D-41), from the adapter that recognised it in a captured message of its CLI; null when it
/// did not classify the end (an adapter of the earlier phases, a double). <see cref="Classified"/> reads it with its fallback.
/// </param>
public sealed record AgentRunOutcome(
    AgentRunStatus Status,
    int? ExitCode,
    string? FinalText,
    string? StructuredOutputJson,
    string? SessionId,
    string? Error,
    AgentRunEnd? End = null)
{
    /// <summary>The class of the end: the adapter's, or else the one the status implies (<see cref="AgentRunEnd.Implied"/>).</summary>
    public AgentRunEnd Classified() => End ?? AgentRunEnd.Implied(Status);
}

/// <summary>Normalised event stream. Each adapter maps its native events (stream-json, exec --json...) to these.</summary>
public abstract record AgentEvent(DateTimeOffset At);

public sealed record SessionStarted(DateTimeOffset At, string SessionId, string? Model) : AgentEvent(At);

public sealed record MessageEmitted(DateTimeOffset At, string Text, bool IsPartial) : AgentEvent(At);

public sealed record ToolInvoked(DateTimeOffset At, string Tool, string Summary) : AgentEvent(At);

public sealed record ToolCompleted(DateTimeOffset At, string Tool, bool Succeeded) : AgentEvent(At);

public sealed record PermissionRequested(DateTimeOffset At, string Tool, string Description) : AgentEvent(At);

public sealed record UsageReported(DateTimeOffset At, TokenUsage Usage) : AgentEvent(At);

/// <summary>
/// The context occupancy of one step (docs/14 D-09): the prompt of that model call, input plus cache read plus cache
/// created, with the window of the model that served it. One per step: an adapter deduplicates the events of a step by
/// its message id. The window may be an estimate until the run ends (D-10), and is missing when nothing is known yet.
/// </summary>
public sealed record ContextObserved(DateTimeOffset At, string? Model, MetricValue<long> PromptTokens, MetricValue<long> ContextWindow) : AgentEvent(At);

/// <summary>The CLI compacted the conversation (<paramref name="Trigger"/>: <c>auto</c> or <c>manual</c>), with the tokens it had before.</summary>
public sealed record CompactionObserved(DateTimeOffset At, string Trigger, MetricValue<long> TokensBefore) : AgentEvent(At);

/// <summary>
/// The provider reported a limit (<paramref name="Status"/> as the CLI says it). <paramref name="LimitType"/> is the window
/// the limit is about, as the CLI names it (Claude's <c>rateLimitType</c>, docs/16 D-36): a five-hour and a weekly window are
/// told apart by it. Null when the CLI does not say (<c>codex exec</c>, Gemini).
/// </summary>
public sealed record RateLimitSignaled(DateTimeOffset At, string Status, DateTimeOffset? ResetsAt, string? LimitType = null) : AgentEvent(At);

/// <summary>A subscription usage window as reported by the provider (e.g. Claude's five-hour and seven-day windows).</summary>
public sealed record QuotaWindow(string Name, MetricValue<double> Utilization, DateTimeOffset? ResetsAt);

/// <summary>Subscription quota exposed by the provider itself; never computed or guessed by Agent Studio.</summary>
public sealed record QuotaReported(DateTimeOffset At, IReadOnlyList<QuotaWindow> Windows) : AgentEvent(At);

public sealed record RunFinished(DateTimeOffset At, AgentRunOutcome Outcome) : AgentEvent(At);

public interface IAgentRun : IAsyncDisposable
{
    string RunId { get; }

    /// <summary>Event stream of the run. Implementations must complete it when the run ends.</summary>
    IAsyncEnumerable<AgentEvent> Events { get; }

    Task<AgentRunOutcome> Completion { get; }

    Task CancelAsync();
}

/// <summary>Adapter that runs an agent through an official CLI (or, optionally, an API).</summary>
public interface IAgentProvider
{
    ProviderId Provider { get; }

    VendorId Vendor { get; }

    ProviderCapabilities Capabilities { get; }

    Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken);
}
