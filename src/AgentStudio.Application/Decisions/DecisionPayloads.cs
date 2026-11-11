using System.Globalization;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Domain.Work;

namespace AgentStudio.Application.Decisions;

// The payloads of the decision log in Phase 3 (docs/13 sections 3.2 and 3.7), Phase 4 (docs/14 section 5.2) and Phase 8
// (docs/16 D-13, D-16 and D-41). They are written once, in one fixed format (see DecisionPayloadSerializer), and the log
// hashes their text: to change a payload, add a field and raise V, never rename or remove one. Payloads never carry prompts
// or agent output: they carry the id and the SHA-256 of the evidence.

/// <summary>
/// Base of every payload. <see cref="V"/> is the version of the payload's shape; <see cref="RunId"/> ties the entry to
/// the orchestration run it is about, which is what lets a run be replayed from the log.
/// </summary>
public abstract record DecisionPayload
{
    /// <summary>
    /// The run id of an entry that belongs to no run (docs/14 D-19): memory entries and their reviews, and the
    /// acceptance of a history as a new origin. Replay and the judge's dossier select entries by run, so they never see
    /// these, and no run can have this id.
    /// </summary>
    public const string NoRun = "";

    public int V { get; init; } = 1;

    public required string RunId { get; init; }
}

/// <summary>An agent of the roster of a run, as the judge-independence check needs it.</summary>
public sealed record RosterEntry(AgentId Agent, AgentRole Role, ProviderId Provider, VendorId Vendor);

/// <summary>
/// One writer at medium integrity of the plan a run was approved with (ADR-0018, decision 3), as <c>RunStarted</c>
/// version 3 anchors it: the task, why it writes at medium integrity (the name of the engine's reason: <c>Shell</c>,
/// <c>CodexWrites</c>, <c>UnmeasuredCli</c>, <c>UnmeasuredClaudeVersion</c>, <c>ToolsBeyondMeasuredProfile</c>,
/// <c>UnknownAgent</c>, <c>UnknownStep</c> or <c>StepWithoutIsolation</c>) and, when they are part of the reason, the
/// agent, the CLI, the tools beyond the measured profile and the step. It does not depend on how the warning words it.
/// </summary>
public sealed record MediumIntegrityWriterEntry(
    TaskId Task,
    string Reason,
    AgentId? Agent = null,
    ProviderId? Provider = null,
    IReadOnlyList<string>? Tools = null,
    string? Step = null);

/// <summary>
/// A run starts. The roster is recorded so the vendors that produced the work can be computed from the log alone.
/// Version 2 adds <paramref name="HeaderHash"/>, the canonical SHA-256 of the whole header the run was stored with
/// (<see cref="RunHeaderHash"/>): roster with instructions, tools and budgets, options, base commit and the hashes of the
/// plan, the requirements and the verification profile. Resuming recomputes it from the stored header.
/// Version 3 (G-02) adds what the user approved about writers at medium integrity (ADR-0018, decision 3): the warning
/// shown next to «Aprobar plan» (<paramref name="ApprovedWarning"/>, null when there was none) and each writer it names
/// (<paramref name="ApprovedWriters"/>, empty when there was none). Resuming reads the writers again and goes on only
/// with these. A version 2 start anchors neither, which is not the same as anchoring none (<see cref="AnchorsApprovedWriters"/>).
/// <paramref name="PlannedIn"/> (V43, no new version: it is left out when null, so every entry already written reads and is
/// written back byte for byte) is the run in which the orchestrator planned, when it is not this one: the plan was edited
/// after the orchestrator proposed it, or its proposal had started a run already, and the run got an id of its own. The
/// questions the orchestrator asked before it planned, and their answers, are in the entries of that run: the start and a
/// resume read them from there.
/// </summary>
public sealed record RunStartedPayload(
    string PlanHash,
    string RequirementSetHash,
    string BaseCommit,
    string? BaseRef,
    string VerificationProfileHash,
    IReadOnlyList<RosterEntry> Roster,
    string? Follows,
    string? HeaderHash = null,
    string? ApprovedWarning = null,
    IReadOnlyList<MediumIntegrityWriterEntry>? ApprovedWriters = null,
    string? PlannedIn = null) : DecisionPayload
{
    /// <summary>The first version that anchors the hash of the whole run header.</summary>
    public const int HeaderVersion = 2;

    /// <summary>The first version that anchors the writers at medium integrity the plan was approved with, and its warning.</summary>
    public const int WritersVersion = 3;

    /// <summary>
    /// Whether this start says which writers at medium integrity were approved (version 3 on), even none. A method, not a
    /// property: the serializer would write a property into the payload, and the text of a payload is part of its hash.
    /// </summary>
    public bool AnchorsApprovedWriters() => V >= WritersVersion;
}

public sealed record RunFinishedPayload(RunPhase Phase, string Summary, string? IntegrationCommit, string? VerdictId) : DecisionPayload;

public sealed record PlanCreatedPayload(string PlanHash, PlanSource Source, AgentId? Author, int TaskCount, EvidenceId? RawPlanEvidence) : DecisionPayload;

/// <summary>The one correction round of the planner: the plan that replaced <paramref name="PreviousPlanHash"/>.</summary>
public sealed record PlanRevisedPayload(string PlanHash, string PreviousPlanHash, string Reason) : DecisionPayload;

public sealed record TaskCreatedPayload(TaskId Task, string Title, WorkTaskKind Kind, JoinPolicy Join, int MaxRetries) : DecisionPayload;

public sealed record TaskAssignedPayload(TaskId Task, AgentId Agent) : DecisionPayload;

/// <summary>
/// A transition of a task. This is the entry replay reads: <paramref name="Evidence"/> completes the task and
/// <paramref name="BlockReason"/> says why a task is blocked (Interrupted, ProviderUnavailable, RateLimitedUntil, AwaitingHuman).
/// </summary>
public sealed record TaskStatusChangedPayload(
    TaskId Task,
    WorkTaskStatus From,
    WorkTaskStatus To,
    string Reason,
    string? BlockReason,
    IReadOnlyList<EvidenceId> Evidence,
    int Attempt) : DecisionPayload;

/// <summary>What the executor was told, by reference: the prompt is evidence, not payload.</summary>
public sealed record InstructionsIssuedPayload(
    TaskId Task,
    int Attempt,
    EvidenceId PromptEvidenceId,
    string PromptSha256,
    IReadOnlyList<RequirementId> Covers,
    IReadOnlyList<string> WriteScope,
    IReadOnlyList<SkillId> Skills) : DecisionPayload;

/// <summary>What an agent run is for (docs/14 D-08). The usage panel groups by it; the auxiliary ones run outside an orchestration.</summary>
public enum AgentRunPurpose
{
    /// <summary>The orchestrator planning a run, or its one correction.</summary>
    Planner,

    /// <summary>An executor working on a task.</summary>
    Executor,

    /// <summary>The independent judge reviewing a run, or its one correction.</summary>
    Judge,

    /// <summary>The assisted skill recommendation (D-29), an auxiliary run.</summary>
    SkillRecommendation,

    /// <summary>The narrative daily summary by an agent (D-31), an auxiliary run.</summary>
    DailySummary,

    // Phase 5 (docs/15 D-44), appended: the stored starts name their purpose and nothing may shift the values before.

    /// <summary>A turn of a participant of a debate (task <c>r&lt;round&gt;-&lt;agent&gt;</c>, in the run id of the debate).</summary>
    DebateParticipant,

    /// <summary>The judge of a debate, or its one correction (task <c>juez-&lt;agent&gt;</c>, attempts 1 and 2).</summary>
    DebateJudge,

    /// <summary>The judge of an audit of the whole project, or its one correction (in the run id of the audit).</summary>
    ProjectAudit,

    // Phase 13 (docs/17 §8.2), appended: auxiliary runs, each with the person's OK and the estimate in front.

    /// <summary>«Revisar el orden con el orquestador» (D-20): the orchestrator, read-only, on a plan the person changed.</summary>
    OrderReview,

    /// <summary>The analysis of consumption with an agent (D-35): read-only, of the vendor that did not produce the work.</summary>
    ConsumptionAnalysis,

    /// <summary>The chat of preparation with the orchestrator (D-44): read-only, in the band of preparation.</summary>
    PreparationChat,

    // Phase 14 (docs/17 §8.2), appended: auxiliary runs again, each with the person's OK and the estimate in front.

    /// <summary>The agent of the help (D-56): read-only, in an empty folder, with the card of what it is sent in front of the person.</summary>
    Help,

    /// <summary>The part of a document for clients written by an agent (D-59): the summary or an explanation of use, read-only, marked in the document.</summary>
    ClientDocumentDraft,
}

/// <summary>
/// What project memory an agent was given (docs/14 D-23 and D-24), by reference: the SHA-256 of the
/// <c>&lt;project_memory&gt;</c> block, the ids of the entries in it, those left out for size, and its characters. The
/// text of the block is in the evidence of the prompt.
/// </summary>
public sealed record ContextPackReference(string Sha256, IReadOnlyList<string> Entries, IReadOnlyList<string> Omitted, int Characters);

/// <summary>
/// The parts of a prompt before the agent starts (docs/17 D-13), each a segment of the bar of context by parts. Recorded by
/// name: new ones are appended.
/// </summary>
public enum ContextPartKind
{
    /// <summary>The instructions of the protocol (ADR-0013): the same for every task of a role.</summary>
    Protocol,

    /// <summary>The frozen requirements the task covers.</summary>
    Requirements,

    /// <summary>The brief of the project (<c>&lt;project_brief&gt;</c>, D-10).</summary>
    ProjectBrief,

    /// <summary>The instructions of the agent (<c>&lt;agent_instructions&gt;</c>, D-42).</summary>
    AgentInstructions,

    /// <summary>The pack of project memory (<c>&lt;project_memory&gt;</c>).</summary>
    Memory,

    /// <summary>What comes from earlier tasks (<c>&lt;upstream_output&gt;</c>).</summary>
    Upstream,

    /// <summary>The previous attempt or the handoff of a renewed session (ADR-0035).</summary>
    PreviousAttempt,

    /// <summary>The question of the agent and the person's answer (<c>&lt;human_answer&gt;</c>, D-30).</summary>
    HumanAnswer,

    /// <summary>What presenting the skills costs: their names and descriptions.</summary>
    Skills,
}

/// <summary>
/// One part of a prompt before the agent starts (docs/17 D-13), in characters: Agent Studio knows them before it launches,
/// and a bar shows them estimated (characters divided by 4, docs/14 D-24). What the CLI then reports beyond their sum is the
/// conversation and the tools. No text: only its size.
/// </summary>
public sealed record ContextPartSnapshot(ContextPartKind Kind, int Characters);

/// <summary>
/// Recorded before the agent is launched. <paramref name="Task"/> is null for the planner, the judge and the auxiliary
/// runs. Version 2 (docs/14 section 5.2) adds what the run is for (<paramref name="Purpose"/>) and, when the agent was
/// given project memory, the pack it was given (<paramref name="ContextPack"/>; null when it got none). Version 3 (docs/17
/// D-13, Phase 13) adds the parts of the prompt before the agent starts (<paramref name="ContextParts"/>), written only when
/// there are: a start of version 2 is read and written back byte for byte. Still in version 3 (docs/18 P47, ADR-0050), the
/// reasoning effort the agent was set with (<paramref name="Effort"/>) is written only when it is set, by its token
/// (<see cref="ReasoningEfforts.Token"/>), so a start of version 2 or 3 written without it is read and written back byte for
/// byte too, and the estimator can tell an attempt at <c>low</c> from one at <c>high</c> of the same model.
/// </summary>
public sealed record AgentRunStartedPayload(
    TaskId? Task,
    int Attempt,
    AgentId Agent,
    ProviderId Provider,
    VendorId Vendor,
    string? Model,
    string WorkingDirectory,
    EvidenceId PromptEvidenceId,
    string PromptSha256,
    IReadOnlyList<SkillId> Skills,
    AgentRunPurpose? Purpose = null,
    ContextPackReference? ContextPack = null,
    IReadOnlyList<ContextPartSnapshot>? ContextParts = null,
    ReasoningEffort? Effort = null) : DecisionPayload
{
    /// <summary>The first version that records the purpose of the run and the memory the agent was given.</summary>
    public const int PurposeVersion = 2;

    /// <summary>
    /// The first version that records the parts of the prompt before the agent starts (docs/17 D-13) and, since docs/18 P47, the
    /// reasoning effort of the agent. It keeps everything version 2 records. A start of this version carries the parts, the effort
    /// or both, each only when there is one.
    /// </summary>
    public const int ContextPartsVersion = 3;

    /// <summary>Whether this start records its purpose and memory (version 2 on). A method, so the serializer never writes it.</summary>
    public bool RecordsPurpose() => V >= PurposeVersion;

    /// <summary>
    /// Whether this start is of the version that can record the parts of its prompt (version 3 on). It does not mean there are
    /// parts: a version 3 start may carry only the effort, so a reader checks <c>ContextParts is { Count: &gt; 0 }</c> before using
    /// them. A method, so the serializer never writes it.
    /// </summary>
    public bool RecordsContextParts() => V >= ContextPartsVersion;
}

/// <summary>How an agent attempt ended. <c>Interrupted</c> is added by recovery for an attempt the app never saw finish.</summary>
public enum AttemptOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    LimitReached,
    RateLimited,
    Interrupted,
}

public static class AttemptOutcomes
{
    public static AttemptOutcome From(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Succeeded => AttemptOutcome.Succeeded,
        AgentRunStatus.Failed => AttemptOutcome.Failed,
        AgentRunStatus.Cancelled => AttemptOutcome.Cancelled,
        AgentRunStatus.LimitReached => AttemptOutcome.LimitReached,
        AgentRunStatus.RateLimited => AttemptOutcome.RateLimited,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown agent run status."),
    };
}

/// <summary>
/// A measured or estimated number with where it comes from; a number that is not available is never zero. From
/// version 2 of <c>AgentRunFinished</c> it also says who gave it (<paramref name="Origin"/>, docs/14 D-03); a version 1
/// snapshot has none, and its origin is read from its provenance (<see cref="ResolvedOrigin"/>).
/// </summary>
public sealed record MetricSnapshot(decimal? Value, MetricProvenance Provenance, string Source, MetricOrigin? Origin = null)
{
    /// <summary>The figure as version 1 records it: value, provenance and source, without its origin.</summary>
    public static MetricSnapshot From(MetricValue<long> metric) => new(metric.Value, metric.Provenance, metric.Source);

    /// <summary>The figure as version 1 records it: value, provenance and source, without its origin.</summary>
    public static MetricSnapshot From(MetricValue<decimal> metric) => new(metric.Value, metric.Provenance, metric.Source);

    /// <summary>The figure as version 2 records it, with its origin.</summary>
    public static MetricSnapshot WithOrigin(MetricValue<long> metric) => new(metric.Value, metric.Provenance, metric.Source, metric.Origin);

    /// <summary>The figure as version 2 records it, with its origin.</summary>
    public static MetricSnapshot WithOrigin(MetricValue<decimal> metric) => new(metric.Value, metric.Provenance, metric.Source, metric.Origin);

    /// <summary>
    /// Who gave the figure. A version 1 snapshot came from a parser through the creators whose meaning did not change in
    /// Phase 4: an exact figure was reported by the CLI, an estimate was the CLI's, and an unavailable one was not
    /// exposed. A method, not a property: the serializer would write a property into the payload, whose text is hashed.
    /// </summary>
    public MetricOrigin ResolvedOrigin() => Origin ?? Provenance switch
    {
        MetricProvenance.Exact => MetricOrigin.ReportedByProvider,
        MetricProvenance.Estimated => MetricOrigin.EstimatedByProvider,
        _ => MetricOrigin.NotExposed,
    };

    /// <summary>The figure as a whole-number metric, with its origin.</summary>
    /// <exception cref="ArgumentException">The snapshot contradicts itself (a value its origin cannot have, a provenance that is not its origin's, a fraction).</exception>
    /// <exception cref="OverflowException">The value does not fit in a <see cref="long"/>.</exception>
    public MetricValue<long> ToInt64()
    {
        var origin = Checked();
        if (Value is { } value && decimal.Truncate(value) != value)
        {
            throw new ArgumentException($"The figure '{Source}' is {value.ToString(CultureInfo.InvariantCulture)}, not a whole number.", nameof(Value));
        }

        return MetricValue<long>.Of(origin, Value is { } whole ? decimal.ToInt64(whole) : null, Source);
    }

    /// <summary>The figure as a decimal metric (a cost), with its origin.</summary>
    /// <exception cref="ArgumentException">The snapshot contradicts itself.</exception>
    public MetricValue<decimal> ToDecimal() => MetricValue<decimal>.Of(Checked(), Value, Source);

    private MetricOrigin Checked()
    {
        var origin = ResolvedOrigin();
        if (origin.ToProvenance() != Provenance)
        {
            throw new ArgumentException($"The figure '{Source}' says {Provenance} but its origin is {origin}.", nameof(Origin));
        }

        return origin;
    }
}

/// <summary>
/// The usage of one model in an attempt (Claude <c>modelUsage</c>, docs/14 D-04), as version 2 of <c>AgentRunFinished</c>
/// records it: every figure with its origin and the CLI's cost basis.
/// </summary>
public sealed record ModelUsageSnapshot(
    string Model,
    MetricSnapshot InputTokens,
    MetricSnapshot OutputTokens,
    MetricSnapshot CacheReadInputTokens,
    MetricSnapshot CacheWriteInputTokens,
    MetricSnapshot CostUsd,
    MetricSnapshot ContextWindow,
    string? CostBasis)
{
    public static ModelUsageSnapshot From(ModelUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new ModelUsageSnapshot(
            usage.Model,
            MetricSnapshot.WithOrigin(usage.InputTokens),
            MetricSnapshot.WithOrigin(usage.OutputTokens),
            MetricSnapshot.WithOrigin(usage.CacheReadInputTokens),
            MetricSnapshot.WithOrigin(usage.CacheWriteInputTokens),
            MetricSnapshot.WithOrigin(usage.CostUsd),
            MetricSnapshot.WithOrigin(usage.ContextWindow),
            usage.CostBasis);
    }
}

/// <summary>
/// The usage of an attempt as the provider reported it, each figure with its provenance. Version 1 records the five
/// figures (<see cref="From"/>); version 2 (<see cref="WithBreakdown"/>) records them with their origin and adds the cache
/// writes, the reasoning tokens and the breakdown per model (empty when the CLI gives none). The CLIs report totals
/// (docs/14 D-02): the usage of a resumed session includes what earlier attempts of it spent.
/// </summary>
public sealed record UsageSnapshot(
    MetricSnapshot InputTokens,
    MetricSnapshot OutputTokens,
    MetricSnapshot CachedInputTokens,
    MetricSnapshot ContextWindow,
    MetricSnapshot CostUsd,
    MetricSnapshot? CacheWriteInputTokens = null,
    MetricSnapshot? ReasoningOutputTokens = null,
    IReadOnlyList<ModelUsageSnapshot>? PerModel = null)
{
    /// <summary>The usage as version 1 records it: the five figures without their origin. Phase 3 writers use it.</summary>
    public static UsageSnapshot From(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new UsageSnapshot(
            MetricSnapshot.From(usage.InputTokens),
            MetricSnapshot.From(usage.OutputTokens),
            MetricSnapshot.From(usage.CachedInputTokens),
            MetricSnapshot.From(usage.ContextWindow),
            MetricSnapshot.From(usage.CostUsd));
    }

    /// <summary>The usage as version 2 records it: every figure with its origin, the cache writes, the reasoning and the breakdown per model.</summary>
    public static UsageSnapshot WithBreakdown(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new UsageSnapshot(
            MetricSnapshot.WithOrigin(usage.InputTokens),
            MetricSnapshot.WithOrigin(usage.OutputTokens),
            MetricSnapshot.WithOrigin(usage.CachedInputTokens),
            MetricSnapshot.WithOrigin(usage.ContextWindow),
            MetricSnapshot.WithOrigin(usage.CostUsd),
            MetricSnapshot.WithOrigin(usage.CacheWriteInputTokens),
            MetricSnapshot.WithOrigin(usage.ReasoningOutputTokens),
            [.. usage.PerModel.Select(ModelUsageSnapshot.From)]);
    }
}

/// <summary>
/// The step of an attempt with the largest context occupancy (docs/14 D-09 and D-12): the model of that step, the tokens
/// its prompt used (<see cref="ContextObserved.PromptTokens"/>; a count, never the prompt) and the window of that model,
/// each with its origin.
/// </summary>
public sealed record ContextPeakSnapshot(string? Model, MetricSnapshot UsedTokens, MetricSnapshot ContextWindow)
{
    public static ContextPeakSnapshot From(ContextObserved step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new ContextPeakSnapshot(step.Model, MetricSnapshot.WithOrigin(step.PromptTokens), MetricSnapshot.WithOrigin(step.ContextWindow));
    }

    /// <summary>
    /// The step with the window of its model as the breakdown of the run reports it (Claude's <c>modelUsage</c> comes when
    /// the run ends, after the step was observed without it); the step's own window when the breakdown has none for it.
    /// </summary>
    public static ContextPeakSnapshot From(ContextObserved step, TokenUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(step);
        var window = WindowOf(step.Model, usage) ?? step.ContextWindow;
        return new ContextPeakSnapshot(step.Model, MetricSnapshot.WithOrigin(step.PromptTokens), MetricSnapshot.WithOrigin(window));
    }

    private static MetricValue<long>? WindowOf(string? model, TokenUsage? usage)
    {
        if (model is null || usage is null)
        {
            return null;
        }

        var match = usage.PerModel.FirstOrDefault(m => m.Model == model)
            ?? usage.PerModel.FirstOrDefault(m => m.Model.StartsWith(model, StringComparison.Ordinal) || model.StartsWith(m.Model, StringComparison.Ordinal));
        return match is { ContextWindow.HasValue: true } ? match.ContextWindow : null;
    }
}

/// <summary>
/// How an attempt ended, classified, as version 3 of <c>AgentRunFinished</c> records it (docs/16 D-41): the class, and for a
/// window quota the window that ran out as the CLI names it and its reset, or for a budget which one. Each is left out when
/// unknown. It carries no text: <paramref name="LimitType"/> is a token of the CLI (Claude's <c>rateLimitType</c>).
/// </summary>
public sealed record AttemptEndSnapshot(AttemptEndClass Class, string? LimitType = null, DateTimeOffset? ResetsAt = null, AttemptBudgetKind? Budget = null)
{
    /// <summary>The end as an adapter classified it (<see cref="AgentRunOutcome.Classified"/>).</summary>
    public static AttemptEndSnapshot From(AgentRunEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        return new AttemptEndSnapshot(end.Class, end.LimitType, end.ResetsAt, end.Budget);
    }

    /// <summary>
    /// The class an end recorded without one (versions 1 and 2) implies from its status alone: a success completed, a
    /// cancellation was cancelled, a limit reached was a budget (which one, unknown) and an interrupted attempt was
    /// interrupted. A failure and a rate limit are not recognised (<see cref="AttemptEndClass.Other"/>): a rate limit of
    /// version 2 could have been a window or a throttling, and its status still says it was a limit.
    /// </summary>
    public static AttemptEndSnapshot ImpliedBy(AttemptOutcome status) => status switch
    {
        AttemptOutcome.Succeeded => new(AttemptEndClass.Completed),
        AttemptOutcome.Cancelled => new(AttemptEndClass.Cancelled),
        AttemptOutcome.LimitReached => new(AttemptEndClass.AttemptBudget),
        AttemptOutcome.Interrupted => new(AttemptEndClass.Interrupted),
        AttemptOutcome.Failed or AttemptOutcome.RateLimited => new(AttemptEndClass.Other),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown attempt outcome."),
    };

    /// <summary>The end as the engine reads it.</summary>
    public AgentRunEnd ToEnd() => new(Class, LimitType, ResetsAt, Budget);
}

/// <summary>
/// How an attempt ended. Version 2 (docs/14 section 5.2) records the usage with origins and breakdown
/// (<see cref="UsageSnapshot.WithBreakdown"/>) and adds: when the process was launched (<paramref name="LaunchedAt"/>,
/// after any wait in the provider's queue, so the duration is the run's), the model the CLI reported
/// (<paramref name="ReportedModel"/>), the session this attempt resumed (<paramref name="ResumedSession"/>, so the
/// attempts that share a session can be told apart, D-02), the step with the largest context
/// (<paramref name="ContextPeak"/>), the compactions (<paramref name="Compactions"/>; null when the CLI does not expose
/// them) and the turns (<paramref name="Turns"/>). Version 3 (docs/16 D-41) adds the class of the end
/// (<paramref name="End"/>), which decides what the engine does next: wait for a window, renew the session, block until
/// the CLI is signed in again... An end of version 1 or 2 has none, and is read with the one its status implies
/// (<see cref="ResolvedEnd"/>).
/// </summary>
public sealed record AgentRunFinishedPayload(
    TaskId? Task,
    int Attempt,
    AttemptOutcome Status,
    int? ExitCode,
    string? SessionId,
    string? Error,
    UsageSnapshot? Usage,
    EvidenceId? ReportEvidenceId,
    DateTimeOffset? LaunchedAt = null,
    string? ReportedModel = null,
    string? ResumedSession = null,
    ContextPeakSnapshot? ContextPeak = null,
    int? Compactions = null,
    int? Turns = null,
    AttemptEndSnapshot? End = null) : DecisionPayload
{
    /// <summary>The first version that records the usage with origins and breakdown, the launch time, the reported model, the resumed session, the context peak and the compactions.</summary>
    public const int UsageVersion = 2;

    /// <summary>The first version that records the class of the end (docs/16 D-41). It keeps everything version 2 records.</summary>
    public const int EndClassVersion = 3;

    /// <summary>Whether this end records the usage of version 2 (version 3 does too). A method, so the serializer never writes it.</summary>
    public bool RecordsUsageWithOrigins() => V >= UsageVersion;

    /// <summary>Whether this end records its class (version 3 on). A method, so the serializer never writes it.</summary>
    public bool RecordsEndClass() => V >= EndClassVersion;

    /// <summary>The class of the end: the recorded one, or for an end recorded without one the one its status implies (<see cref="AttemptEndSnapshot.ImpliedBy"/>).</summary>
    public AttemptEndSnapshot ResolvedEnd() => End ?? AttemptEndSnapshot.ImpliedBy(Status);

    /// <summary>
    /// The receipt of the journal entry of this launch (docs/17 D-02, addendum of 2026-11-03): the entry and the SHA-256 of
    /// the exact text the journal wrote for it (<see cref="AgentRunOutcome.Journal"/>). The derivation of the journal as memory
    /// gives <c>System</c> only to the entry with that text. Null when the journal wrote none: an end of before Phase 12, a
    /// launch that could not start, a journal without receipts. Written only when there is one, so no stored payload changes.
    /// </summary>
    public JournalReceipt? Journal { get; init; }
}

/// <summary>The counters of a test run as a payload carries them: only what was measured, never a computed verdict.</summary>
public sealed record TestRunSnapshot(int Passed, int Failed, int Skipped, int ExitCode)
{
    public static TestRunSnapshot? From(TestRunSummary? summary) =>
        summary is null ? null : new TestRunSnapshot(summary.Passed, summary.Failed, summary.Skipped, summary.ExitCode);

    public TestRunSummary ToSummary() => new(Passed, Failed, Skipped, ExitCode);
}

/// <summary>
/// A piece of evidence of the run, announced in the log. Version 1 anchors its id, kind, origin and content hash.
/// Version 2 also anchors the metadata that decides an approval: <paramref name="Title"/>, <paramref name="ProducedBy"/>,
/// <paramref name="CapturedAt"/>, the counters of a test run (<paramref name="TestRun"/>) and the commit it verified
/// (<paramref name="SubjectCommit"/>). The store's row of a version-1 entry carries those fields unanchored.
/// </summary>
public sealed record EvidenceRegisteredPayload(
    EvidenceId Evidence,
    EvidenceKind Kind,
    EvidenceOrigin Origin,
    string Sha256,
    TaskId? Task,
    int? Attempt,
    string? Title = null,
    string? ProducedBy = null,
    DateTimeOffset? CapturedAt = null,
    TestRunSnapshot? TestRun = null,
    string? SubjectCommit = null) : DecisionPayload
{
    /// <summary>The first version that anchors the metadata of the evidence, not only its content.</summary>
    public const int MetadataVersion = 2;

    /// <summary>The entry the engine records for evidence it captured: version 2, with the record's metadata.</summary>
    public static EvidenceRegisteredPayload Anchoring(EvidenceRecord record, string runId, TaskId? task, int? attempt)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new EvidenceRegisteredPayload(
            record.Id,
            record.Kind,
            record.Origin,
            record.ContentSha256,
            task,
            attempt,
            record.Title,
            record.ProducedBy,
            record.CapturedAt,
            TestRunSnapshot.From(record.TestRun),
            record.SubjectCommit)
        {
            V = MetadataVersion,
            RunId = runId,
        };
    }

    /// <summary>Whether this entry anchors the metadata of the evidence (version 2 or later), not only its content.</summary>
    public bool AnchorsMetadata() => V >= MetadataVersion;

    /// <summary>
    /// How the store's row of this evidence differs from what this entry anchored, one line per field, naming the field
    /// (<c>subjectCommit</c>, <c>testRun</c>...). Every version anchors the hash, the kind and the origin; version 2 also
    /// the title, the author, the capture time (compared as an instant), the test counters and the commit. Empty when
    /// the row agrees with the entry.
    /// </summary>
    public IReadOnlyList<string> Disagreements(EvidenceRecord stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var fields = new List<string>();
        void Compare(string field, bool same, string? store, string? log)
        {
            if (!same)
            {
                fields.Add($"{field}: the store holds {store ?? "nothing"}, the log anchored {log ?? "nothing"}");
            }
        }

        static string? Quote(string? text) => text is null ? null : $"'{text}'";
        static string? Counters(TestRunSnapshot? tests) =>
            tests is null ? null : string.Create(CultureInfo.InvariantCulture, $"passed={tests.Passed} failed={tests.Failed} skipped={tests.Skipped} exit={tests.ExitCode}");

        Compare("sha256", string.Equals(Sha256, stored.ContentSha256, StringComparison.Ordinal), stored.ContentSha256, Sha256);
        Compare("kind", Kind == stored.Kind, stored.Kind.ToString(), Kind.ToString());
        Compare("origin", Origin == stored.Origin, stored.Origin.ToString(), Origin.ToString());
        if (AnchorsMetadata())
        {
            var tests = TestRunSnapshot.From(stored.TestRun);
            Compare("title", string.Equals(Title, stored.Title, StringComparison.Ordinal), Quote(stored.Title), Quote(Title));
            Compare("producedBy", string.Equals(ProducedBy, stored.ProducedBy, StringComparison.Ordinal), Quote(stored.ProducedBy), Quote(ProducedBy));
            Compare("capturedAt", CapturedAt == stored.CapturedAt, stored.CapturedAt.ToString("O", CultureInfo.InvariantCulture), CapturedAt?.ToString("O", CultureInfo.InvariantCulture));
            Compare("testRun", tests == TestRun, Counters(tests), Counters(TestRun));
            Compare("subjectCommit", string.Equals(SubjectCommit, stored.SubjectCommit, StringComparison.Ordinal), Quote(stored.SubjectCommit), Quote(SubjectCommit));
        }

        return fields;
    }

    /// <summary>
    /// The evidence exactly as this version-2 entry anchored it. The requirements it claims to cover are only a hint for
    /// the judge and are not anchored: they come from <paramref name="covers"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry anchors no metadata (version 1), or lacks part of it.</exception>
    public EvidenceRecord ToRecord(IReadOnlyList<RequirementId>? covers)
    {
        if (!AnchorsMetadata() || Title is null || ProducedBy is null || CapturedAt is not { } capturedAt)
        {
            throw new InvalidOperationException($"The EvidenceRegistered entry of evidence '{Evidence}' (version {V}) does not anchor its title, author and capture time.");
        }

        return new EvidenceRecord(Evidence, Kind, Origin, Title, ProducedBy, capturedAt, Sha256, TestRun?.ToSummary(), covers, SubjectCommit);
    }
}

public sealed record WorktreeCreatedPayload(TaskId? Task, string Name, string Path, string RefName, string BaseCommit) : DecisionPayload;

public sealed record WorktreeRemovedPayload(string Name, string Path, bool DiscardedChanges) : DecisionPayload;

/// <summary>A merge into the integration branch or, with <paramref name="IsDelivery"/>, into the user's branch.</summary>
public sealed record IntegrationPerformedPayload(
    TaskId? Task,
    string TargetRef,
    IReadOnlyList<string> Sources,
    string? ResultCommit,
    IReadOnlyList<string> ConflictPaths,
    bool IsDelivery) : DecisionPayload;

/// <summary>One fact a condition was decided on, such as <c>T3.status = Completed</c>.</summary>
public sealed record ConditionFact(string Name, string Value);

/// <summary>A condition was evaluated by the system over facts it captured: replay uses <paramref name="Branch"/>.</summary>
public sealed record ConditionEvaluatedPayload(TaskId Task, string Predicate, bool Result, string Branch, IReadOnlyList<ConditionFact> Facts) : DecisionPayload;

/// <summary>Recorded before the judge runs, so the review can check the log first.</summary>
public sealed record ReviewRequestedPayload(TaskId Task, AgentId Judge) : DecisionPayload;

public sealed record VerdictIssuedPayload(
    string VerdictId,
    TaskId? Task,
    VerdictDecision Decision,
    bool Valid,
    IReadOnlyList<VerdictViolationCode> Violations,
    string RequirementSetHash,
    string DecisionLogHeadHash,
    string? Supersedes,
    string? VendorNote) : DecisionPayload;

public sealed record ApprovalRequestedPayload(string Action, string Description, RiskLevel Risk, TaskId? Task, IReadOnlyList<string> AffectedPaths) : DecisionPayload;

/// <summary>
/// The answer to an approval request; the kind of the entry (granted or denied) says which, and <paramref name="Approved"/>
/// repeats it. Version 2 (docs/16 D-13, F8-11) adds how the person who answered was confirmed (<paramref name="Verification"/>):
/// the dialog alone, Windows Hello after it, or «sin Hello: no disponible». An answer of version 1 was given in the dialog.
/// </summary>
public sealed record ApprovalAnsweredPayload(
    string Action,
    bool Approved,
    string DecidedBy,
    string? Comment,
    TaskId? Task,
    ApprovalVerification? Verification = null) : DecisionPayload
{
    /// <summary>The first version that records how the person was confirmed.</summary>
    public const int VerificationVersion = 2;

    /// <summary>Whether this answer records how the person was confirmed (version 2 on). A method, so the serializer never writes it.</summary>
    public bool RecordsVerification() => V >= VerificationVersion;

    /// <summary>How the person was confirmed: the recorded method, or the dialog for an answer of version 1.</summary>
    public ApprovalVerification ResolvedVerification() => Verification ?? ApprovalVerification.Dialog;
}

/// <summary>A problem found in a run (abandoned, unreachable, retries exhausted, deadlock, broken log...). Raised once when it appears.</summary>
public sealed record AuditAlertPayload(string Code, IReadOnlyList<TaskId> Tasks, string Message) : DecisionPayload;

/// <summary>
/// A memory entry is announced before its row is stored (docs/14 D-19): its id, category and origin, the canonical
/// SHA-256 of its category, topic and content (<see cref="MemoryContent.Sha256"/>), the source it cites (a run and an
/// entry of the log, or an evidence and its SHA-256) and the entry it supersedes. It carries no text: recall only returns
/// rows announced here with the same hash. It belongs to no run (<see cref="DecisionPayload.NoRun"/>).
/// </summary>
public sealed record MemoryRecordedPayload(string Entry, MemoryKind Kind, MemoryOrigin Origin, string Sha256, string Source, string? Supersedes) : DecisionPayload
{
    /// <summary>The announcement of an entry, outside every run.</summary>
    public static MemoryRecordedPayload Announcing(string entry, MemoryKind kind, MemoryOrigin origin, string sha256, string source, string? supersedes = null) =>
        new(entry, kind, origin, sha256, source, supersedes) { RunId = NoRun };
}

/// <summary>A person accepted, rejected or retracted a memory entry (docs/14 D-19 and D-20). It belongs to no run.</summary>
public sealed record MemoryReviewedPayload(string Entry, MemoryReview Decision, string DecidedBy, string? Comment) : DecisionPayload
{
    /// <summary>The review of an entry, outside every run.</summary>
    public static MemoryReviewedPayload Deciding(string entry, MemoryReview decision, string decidedBy, string? comment) =>
        new(entry, decision, decidedBy, comment) { RunId = NoRun };
}

/// <summary>
/// A person trusted the journal of the project or of one day of it, or withdrew that trust (docs/17 D-02, «Confiar en la
/// bitácora de este proyecto»): <paramref name="Day"/> is the day for <see cref="JournalTrustScope.Day"/> and null for the
/// whole project. It covers the journal entries Agent Studio did not write; it belongs to no run.
/// </summary>
public sealed record JournalTrustChangedPayload(JournalTrustScope Scope, DateOnly? Day, bool Trusted, string DecidedBy, string? Comment) : DecisionPayload
{
    /// <summary>The decision of a person about the trust of the journal, outside every run.</summary>
    /// <exception cref="ArgumentException">A day for the project, none for a day, or no one decided.</exception>
    public static JournalTrustChangedPayload Deciding(JournalTrustScope scope, DateOnly? day, bool trusted, string decidedBy, string? comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope of a decision about the journal.");
        }

        if ((scope == JournalTrustScope.Day) != day.HasValue)
        {
            throw new ArgumentException("A decision about one day of the journal names its day, and one about the whole project names none.", nameof(day));
        }

        return new JournalTrustChangedPayload(scope, day, trusted, decidedBy, comment) { RunId = NoRun };
    }
}

/// <summary>A file an export of a document for clients wrote (docs/17 D-60): its format, its name (never a path: the folder is the person's), its SHA-256 and its size.</summary>
public sealed record ClientDocumentFileSnapshot(ClientDocumentFormat Format, string FileName, string Sha256, long Bytes);

/// <summary>A part of the document an agent wrote (docs/17 D-59): the section it is in, the run that wrote it (no text) and whether the person reviewed it.</summary>
public sealed record ClientDocumentAgentPartSnapshot(ClientDocumentSectionKind Section, RunId Run, bool ReviewedByPerson);

/// <summary>
/// A person approved and exported a document for clients (docs/17 D-60, ADR-0057). Each export is a version and the earlier one stays.
/// The payload records what was exported without any of its text: the SHA-256 of the model (<see cref="ClientDocument.Sha256"/>), the
/// evidence of the Markdown the system generated (kept whichever formats were chosen) with its SHA-256, the name and SHA-256 of each
/// file, and what the list of what goes out said: how many secrets were redacted, how many paths of the machine were removed (their
/// text is never written to the log), the parts agents wrote with their run and whether the person reviewed them, how many risks go
/// and whether the consumption does. It belongs to no run (<see cref="DecisionPayload.NoRun"/>): D-60 exports outside every run.
/// </summary>
/// <param name="Version">The version of the document, from 1: one more than the last export of the same milestone.</param>
/// <param name="Milestone">The name of the milestone it is for, or null for a delivery without one.</param>
/// <param name="Commit">The commit of the cover, or null.</param>
/// <param name="Language">The language the document was exported in (<see cref="Localization.LanguageTags"/>).</param>
/// <param name="ModelSha256">The SHA-256 of the model the files were written from.</param>
/// <param name="MarkdownEvidence">The evidence of the generated Markdown.</param>
/// <param name="MarkdownSha256">Its SHA-256.</param>
/// <param name="Files">The files written, one per format.</param>
/// <param name="SecretsRedacted">How many secrets the redactor took out.</param>
/// <param name="PathsRemoved">How many paths of the machine were removed.</param>
/// <param name="RisksIncluded">How many risks go in the section of risks and pending.</param>
/// <param name="IncludesConsumption">Whether the person ticked the consumption.</param>
/// <param name="AgentParts">The parts agents wrote; empty when none.</param>
/// <param name="DecidedBy">Who approved the export.</param>
public sealed record ClientDocumentExportedPayload(
    int Version,
    string? Milestone,
    string? Commit,
    string Language,
    string ModelSha256,
    EvidenceId MarkdownEvidence,
    string MarkdownSha256,
    IReadOnlyList<ClientDocumentFileSnapshot> Files,
    int SecretsRedacted,
    int PathsRemoved,
    int RisksIncluded,
    bool IncludesConsumption,
    IReadOnlyList<ClientDocumentAgentPartSnapshot> AgentParts,
    string DecidedBy) : DecisionPayload
{
    /// <summary>
    /// The export as the log records it, outside every run.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A version below 1, a commit that is not one, a language that is not a name, a hash that is not a SHA-256, no file, two files of one
    /// format or with one name, a name that is not a plain file name or does not end with the extension of its format, a size below 1,
    /// a count below 0, or no one decided.
    /// </exception>
    public static ClientDocumentExportedPayload Exporting(
        int version,
        string? milestone,
        string? commit,
        string language,
        string modelSha256,
        EvidenceId markdownEvidence,
        string markdownSha256,
        IReadOnlyList<ClientDocumentFileSnapshot> files,
        int secretsRedacted,
        int pathsRemoved,
        int risksIncluded,
        bool includesConsumption,
        IReadOnlyList<ClientDocumentAgentPartSnapshot> agentParts,
        string decidedBy)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(agentParts);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (commit is not null && !ClientDocumentRules.IsCommit(commit))
        {
            throw new ArgumentException("The commit of the cover is 7 to 64 lowercase hex characters.", nameof(commit));
        }

        // The name of the milestone is text the log keeps: it is held to the rules of the rest of the text of the document.
        if (milestone is not null && (string.IsNullOrWhiteSpace(milestone) || LocalPaths.Contains(milestone) || PlainText.HasHiddenCharacter(milestone, allowLineBreaks: false)))
        {
            throw new ArgumentException("The milestone has a name, with no path of the machine and no hidden character.", nameof(milestone));
        }

        if (!Localization.LanguageTags.IsValid(language))
        {
            throw new ArgumentException("The language is the name of a culture, such as es-ES or en.", nameof(language));
        }

        if (!ContentHash.IsSha256Hex(modelSha256) || !ContentHash.IsSha256Hex(markdownSha256))
        {
            throw new ArgumentException("The model and the Markdown are named by their SHA-256 (64 lowercase hex characters).", nameof(modelSha256));
        }

        if (files.Count == 0)
        {
            throw new ArgumentException("An export writes at least one file.", nameof(files));
        }

        foreach (var file in files)
        {
            if (!Enum.IsDefined(file.Format)
                || !ClientDocumentFiles.IsPlainFileName(file.FileName)
                || !file.FileName.EndsWith(ClientDocumentFormats.Extension(file.Format), StringComparison.Ordinal))
            {
                throw new ArgumentException("Each file has a plain file name that ends with the extension of its format.", nameof(files));
            }

            if (!ContentHash.IsSha256Hex(file.Sha256) || file.Bytes < 1)
            {
                throw new ArgumentException("Each file has its SHA-256 and a size of at least one byte.", nameof(files));
            }
        }

        if (files.Select(f => f.Format).Distinct().Count() != files.Count || files.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
        {
            throw new ArgumentException("An export writes one file per format, each with its own name.", nameof(files));
        }

        if (secretsRedacted < 0 || pathsRemoved < 0 || risksIncluded < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secretsRedacted), "The counts of what goes out cannot be negative.");
        }

        return new ClientDocumentExportedPayload(
            version, milestone, commit, language, modelSha256, markdownEvidence, markdownSha256, files, secretsRedacted, pathsRemoved, risksIncluded, includesConsumption, agentParts, decidedBy)
        {
            RunId = NoRun,
        };
    }

    /// <summary>
    /// The export as the log records it, from the request the person approved and what the exporter did: the cover and the list of what
    /// goes out of the build, the files and the evidence of the result.
    /// </summary>
    /// <exception cref="ArgumentException">The version of the result is not the one of the cover, or <see cref="Exporting"/> refuses a value.</exception>
    public static ClientDocumentExportedPayload Of(ClientDocumentExportRequest request, ClientDocumentExportResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var cover = request.Build.Document.Cover;
        if (result.Version != cover.Version)
        {
            throw new ArgumentException("The version exported is the one of the cover.", nameof(result));
        }

        var disclosure = request.Build.Disclosure;
        return Exporting(
            result.Version,
            cover.Milestone,
            cover.Commit,
            request.Language,
            request.Build.Document.Sha256(),
            result.MarkdownEvidence,
            result.MarkdownSha256,
            [.. result.Files.Select(f => new ClientDocumentFileSnapshot(f.Format, f.FileName, f.Sha256, f.Bytes))],
            disclosure.SecretsRedacted,
            disclosure.PathsRemoved.Count,
            disclosure.RisksIncluded,
            disclosure.IncludesConsumption,
            [.. ClientDocumentDisclosure.AgentPartsOf(request.Build.Document).Select(p => new ClientDocumentAgentPartSnapshot(p.Section, p.Run, p.ReviewedByPerson))],
            request.DecidedBy);
    }
}

/// <summary>What was chosen when the app was left with a run going (docs/16 D-16, F8-9).</summary>
public enum RunExitChoice
{
    /// <summary>
    /// «Pausar y salir cuando terminen los intentos en curso»: nothing new is dispatched, and the app waits in the tray for
    /// the attempts going to end before it exits. The person paused the run: it never resumes by itself.
    /// </summary>
    PauseAndExitWhenIdle,

    /// <summary>«Salir ahora y dejarlas interrumpidas»: the app exits at once and the attempts going are interrupted. The person left the run: it never resumes by itself.</summary>
    ExitNow,

    /// <summary>
    /// Windows ended the session (signing out, shutting down or restarting): it counts as exiting now, but nobody chose it,
    /// so a run whose header has the consent resumes by itself (D-39), as after any close nobody asked for.
    /// </summary>
    SessionEnded,
}

/// <summary>
/// The choice made when the app was left with this run going (docs/16 D-16): one entry in the log of each run it affected
/// (<see cref="DecisionPayload.RunId"/>: the run, or the debate or the audit), recorded before the app exits.
/// <paramref name="DecidedBy"/> is who chose: the person at the desktop, or the system for
/// <see cref="RunExitChoice.SessionEnded"/>. Cancelling the exit records nothing, since nothing changes. A run with no such
/// entry that did not finish was cut by a close nobody asked for (the app died, or Windows gave no time to write it).
/// </summary>
public sealed record RunExitChosenPayload(RunExitChoice Choice, string DecidedBy) : DecisionPayload
{
    /// <summary>
    /// Whether a run whose header has the consent may resume by itself after this choice (D-39): only after an exit nobody
    /// chose. What the person chose is what they wanted. A method, so the serializer never writes it.
    /// </summary>
    public bool AllowsAutomaticResume() => Choice == RunExitChoice.SessionEnded;
}

/// <summary>How a person changed the dispatch of a run (docs/16 D-39).</summary>
public enum RunDispatchChange
{
    /// <summary>Nothing new is dispatched; the attempts going go on. The person paused the run: it never resumes by itself.</summary>
    Paused,

    /// <summary>The dispatch goes on again.</summary>
    Resumed,
}

/// <summary>
/// A person paused or resumed the dispatch of this run (docs/16 D-39), from the monitor or anywhere else the app offers it,
/// such as the tray: <paramref name="DecidedBy"/> is who, and <paramref name="At"/> when, as the app saw it. The pause lived
/// only in memory before Phase 8; this entry is what tells, after a close nobody asked for, that the person paused the run,
/// so it does not resume by itself whatever its consent says. The pause of the managed exit is
/// <see cref="RunExitChosenPayload"/>, and putting a blocked task back to pending is its own <c>TaskStatusChanged</c>.
/// </summary>
public sealed record RunDispatchChangedPayload(RunDispatchChange Change, string DecidedBy, DateTimeOffset At) : DecisionPayload
{
    /// <summary>
    /// Whether, as the last change of the dispatch of its run, it lets the run resume by itself (D-39): a resume lets the
    /// consent of the header decide again; a pause never does. A method, so the serializer never writes it.
    /// </summary>
    public bool AllowsAutomaticResume() => Change == RunDispatchChange.Resumed;
}

/// <summary>
/// What a check before going on after a wait asked (docs/16 D-38, F8-3): only the sources that are allowed, none of which runs
/// a turn of the model.
/// </summary>
public enum AccessCheckSource
{
    /// <summary>The session probe of the CLI of the provider: <c>claude auth status</c> or <c>codex login status</c>.</summary>
    SessionProbe,

    /// <summary>The read of the Codex quota through its app-server (<c>account/rateLimits/read</c>), only with the user's opt-in (ADR-0020).</summary>
    QuotaRead,
}

/// <summary>What a check before going on said (docs/16 D-38, F8-3).</summary>
public enum AccessCheckResult
{
    /// <summary>The CLI is signed in (a session probe), or the quota allows ordinary use (a quota read): the task may go on.</summary>
    Ready,

    /// <summary>The CLI has no session, or it expired: the task is blocked with the command that fixes it, with no retries.</summary>
    SignedOut,

    /// <summary>A quota read says a window is still reached; <see cref="AccessCheckedPayload.ResetsAt"/> is its reset when the read gives one.</summary>
    StillLimited,

    /// <summary>The quota was not read: the user did not opt in, so nothing was launched.</summary>
    NotOptedIn,

    /// <summary>The provider does not expose the quota there («no expuesto»: Codex with an API key).</summary>
    NotExposed,

    /// <summary>The check gave no answer (the CLI is missing, the probe failed or timed out): «sin dato».</summary>
    Missing,
}

/// <summary>
/// One check before going on after a wait for a limit (docs/16 D-38, F8-3), recorded before the task is launched again: the
/// task it was for (null when it was for the provider, before any of its tasks), the provider, its source, its result and
/// when it was made (<paramref name="CheckedAt"/>). A check with two sources (the session, then the quota of Codex) is two
/// entries. <paramref name="ResetsAt"/> is the reset a quota read gave for a window still reached. Nothing the CLI printed
/// is recorded: a session probe can print the account.
/// </summary>
/// <remarks>
/// A session probe gives <see cref="AccessCheckResult.Ready"/>, <see cref="AccessCheckResult.SignedOut"/> or
/// <see cref="AccessCheckResult.Missing"/>; a quota read, any but <see cref="AccessCheckResult.SignedOut"/>. In Claude there
/// is no read of the quota without a turn: the attempt itself is the check, and its end says whether it is still limited.
/// </remarks>
public sealed record AccessCheckedPayload(
    TaskId? Task,
    ProviderId Provider,
    AccessCheckSource Source,
    AccessCheckResult Result,
    DateTimeOffset CheckedAt,
    DateTimeOffset? ResetsAt = null) : DecisionPayload;

/// <summary>Why a run was resumed after a restart (docs/16 D-39, F8-4).</summary>
public enum RunResumeCause
{
    /// <summary>A person asked for it.</summary>
    UserRequested,

    /// <summary>By the consent of its header: when the app closed, the run was waiting for the quota of a provider.</summary>
    ConsentAfterQuotaWait,

    /// <summary>By the consent of its header: a close nobody asked for cut it (the app died, or nothing was recorded on leaving).</summary>
    ConsentAfterUnrequestedClose,

    /// <summary>By the consent of its header: Windows ended the session (<see cref="RunExitChoice.SessionEnded"/>).</summary>
    ConsentAfterSessionEnded,
}

/// <summary>
/// The run was resumed after a restart (docs/16 D-39, F8-4), recorded before anything is launched again, also when no task of
/// it was interrupted (a run that was only waiting for quota): why (<paramref name="Cause"/>), who or what decided it
/// (<paramref name="DecidedBy"/>: the person, or the system by the consent), when (<paramref name="At"/>) and, for a resume
/// by consent, the hash of the header that carried <c>AutoResumeConsent</c> (<paramref name="ConsentHeaderHash"/>, the one
/// <c>RunStarted</c> anchored). A resume a person asked for names no header.
/// </summary>
public sealed record RunResumedPayload(RunResumeCause Cause, string DecidedBy, DateTimeOffset At, string? ConsentHeaderHash = null) : DecisionPayload
{
    /// <summary>Whether the run resumed by itself, by the consent of its header. A method, so the serializer never writes it.</summary>
    public bool ByConsent() => Cause != RunResumeCause.UserRequested;
}
