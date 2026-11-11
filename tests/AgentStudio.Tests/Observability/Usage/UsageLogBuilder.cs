using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Usage;

/// <summary>
/// Builds a decision log of agent attempts the way the recorders write it (docs/04 section 16.2): an
/// <c>AgentRunStarted</c> before each launch and an <c>AgentRunFinished</c> at its end, version 1 or 2, chained by hash.
/// The clock only moves forward.
/// </summary>
internal sealed class UsageLogBuilder
{
    public const string ProjectId = "6f1c2b0e-0000-4000-8000-000000000001";

    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    private readonly DecisionLog _log = new();

    public UsageLogBuilder(DateTimeOffset? start = null)
    {
        Now = start ?? T0;
    }

    public DateTimeOffset Now { get; private set; }

    public IReadOnlyList<DecisionEntry> Entries => _log.Entries;

    public UsageLogBuilder At(DateTimeOffset instant)
    {
        Now = instant;
        return this;
    }

    public UsageLogBuilder Advance(TimeSpan by)
    {
        Now += by;
        return this;
    }

    public UsageLogBuilder Start(
        string run,
        string? task,
        int attempt,
        string agent = "ejecutor",
        ProviderId? provider = null,
        string? model = "claude-sonnet-5-5",
        AgentRunPurpose? purpose = AgentRunPurpose.Executor,
        int version = 2,
        string workingDirectory = "C:\\worktrees\\t1",
        ReasoningEffort? effort = null)
    {
        var on = provider ?? ProviderId.ClaudeCode;
        var payload = new AgentRunStartedPayload(
            task is null ? null : new TaskId(task),
            attempt,
            new AgentId(agent),
            on,
            VendorOf(on),
            model,
            workingDirectory,
            new EvidenceId("ev-prompt-" + attempt),
            HashA,
            [],
            version >= AgentRunStartedPayload.PurposeVersion ? purpose : null,
            Effort: effort)
        {
            RunId = run,
            V = version,
        };
        return Append(DecisionKind.AgentRunStarted, $"Arranca {agent}", payload);
    }

    public UsageLogBuilder Finish(
        string run,
        string? task,
        int attempt,
        AttemptOutcome status = AttemptOutcome.Succeeded,
        string? session = null,
        UsageSnapshot? usage = null,
        DateTimeOffset? launchedAt = null,
        string? reportedModel = null,
        string? resumed = null,
        ContextPeakSnapshot? peak = null,
        int? compactions = null,
        int version = 2,
        string? error = null)
    {
        var payload = new AgentRunFinishedPayload(
            task is null ? null : new TaskId(task),
            attempt,
            status,
            status == AttemptOutcome.Succeeded ? 0 : null,
            session,
            error,
            usage,
            null,
            version >= AgentRunFinishedPayload.UsageVersion ? launchedAt : null,
            version >= AgentRunFinishedPayload.UsageVersion ? reportedModel : null,
            version >= AgentRunFinishedPayload.UsageVersion ? resumed : null,
            version >= AgentRunFinishedPayload.UsageVersion ? peak : null,
            version >= AgentRunFinishedPayload.UsageVersion ? compactions : null)
        {
            RunId = run,
            V = version,
        };
        return Append(DecisionKind.AgentRunFinished, $"Termina {run}: {status}", payload);
    }

    /// <summary>An entry that is not about an agent attempt, as real logs have many.</summary>
    public UsageLogBuilder Noise(string run)
    {
        var payload = new TaskStatusChangedPayload(new TaskId("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "start", null, [], 1) { RunId = run };
        return Append(DecisionKind.TaskStatusChanged, "T1 en curso", payload);
    }

    public static VendorId VendorOf(ProviderId provider) =>
        provider == ProviderId.CodexCli ? VendorId.OpenAI
        : provider == ProviderId.GeminiCli ? VendorId.Google
        : VendorId.Anthropic;

    private UsageLogBuilder Append(DecisionKind kind, string summary, DecisionPayload payload)
    {
        _log.Append(Now, SystemActor, kind, summary, DecisionPayloadSerializer.Serialize(payload));
        Now += TimeSpan.FromSeconds(1);
        return this;
    }
}

/// <summary>Usage snapshots as the parsers of each CLI and each version of <c>AgentRunFinished</c> record them.</summary>
internal static class Snapshots
{
    public const string ClaudeUsage = "claude result.usage";

    public const string ClaudeCost = "estimación del CLI";

    public static MetricSnapshot Reported(long value, string source = ClaudeUsage) => MetricSnapshot.WithOrigin(MetricValue<long>.Exact(value, source));

    public static MetricSnapshot Cost(decimal value) => MetricSnapshot.WithOrigin(MetricValue<decimal>.Estimated(value, ClaudeCost));

    public static MetricSnapshot NotExposed(string reason) => MetricSnapshot.WithOrigin(MetricValue<long>.NotExposed(reason));

    public static MetricSnapshot NotExposedCost(string reason) => MetricSnapshot.WithOrigin(MetricValue<decimal>.NotExposed(reason));

    /// <summary>Claude, version 2: the input is the whole prompt (fresh input, cache read and cache written).</summary>
    public static UsageSnapshot Claude(long input, long read, long write, long output, decimal cost, params ModelUsageSnapshot[] perModel) =>
        new(
            Reported(input),
            Reported(output),
            Reported(read),
            NotExposed("claude result.usage no da la ventana"),
            Cost(cost),
            Reported(write),
            NotExposed("claude no informa del razonamiento"),
            perModel);

    /// <summary>Claude, version 1: five figures without origin (Phase 3).</summary>
    public static UsageSnapshot ClaudeV1(long input, long read, long output, decimal cost) =>
        new(
            MetricSnapshot.From(MetricValue<long>.Exact(input, ClaudeUsage)),
            MetricSnapshot.From(MetricValue<long>.Exact(output, ClaudeUsage)),
            MetricSnapshot.From(MetricValue<long>.Exact(read, ClaudeUsage)),
            MetricSnapshot.From(MetricValue<long>.Exact(200_000, "claude result.modelUsage")),
            MetricSnapshot.From(MetricValue<decimal>.Estimated(cost, ClaudeCost)));

    /// <summary>Codex, version 2: tokens of the thread, no cost.</summary>
    public static UsageSnapshot Codex(long input, long cached, long write, long output, long reasoning) =>
        new(
            Reported(input, "codex turn.completed.usage.input_tokens"),
            Reported(output, "codex turn.completed.usage.output_tokens"),
            Reported(cached, "codex turn.completed.usage.cached_input_tokens"),
            NotExposed("codex exec --json does not report the context window"),
            NotExposedCost("codex exec --json does not report cost"),
            Reported(write, "codex turn.completed.usage.cache_write_input_tokens"),
            Reported(reasoning, "codex turn.completed.usage.reasoning_output_tokens"),
            []);

    /// <summary>Codex, version 1 (Phase 3): three exact token figures; all zero when no usage notification came.</summary>
    public static UsageSnapshot CodexV1(long input, long cached, long output) =>
        new(
            MetricSnapshot.From(MetricValue<long>.Exact(input, "codex turn.completed.usage.input_tokens")),
            MetricSnapshot.From(MetricValue<long>.Exact(output, "codex turn.completed.usage.output_tokens")),
            MetricSnapshot.From(MetricValue<long>.Exact(cached, "codex turn.completed.usage.cached_input_tokens")),
            MetricSnapshot.From(MetricValue<long>.Unavailable("codex exec --json does not report the context window")),
            MetricSnapshot.From(MetricValue<decimal>.Unavailable("codex exec --json does not report cost")));

    public static ModelUsageSnapshot Model(string model, long input, long output, long read, long write, decimal cost, long window = 1_000_000, string? basis = "list") =>
        new(
            model,
            Reported(input, "claude result.modelUsage"),
            Reported(output, "claude result.modelUsage"),
            Reported(read, "claude result.modelUsage"),
            Reported(write, "claude result.modelUsage"),
            Cost(cost),
            Reported(window, "claude result.modelUsage"),
            basis);

    public static ContextPeakSnapshot Peak(string? model, long used, long window) =>
        new(model, Reported(used, "claude assistant.message.usage"), Reported(window, "claude result.modelUsage"));
}
