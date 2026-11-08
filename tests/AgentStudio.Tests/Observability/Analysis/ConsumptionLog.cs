using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Observability.Usage;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Analysis;

/// <summary>
/// A prepared log of one run for the analysis of consumption (docs/17 D-34): its start, its agent attempts (start, wait in the
/// queue, launch, end with usage of version 3), the transitions of its tasks, its evidence, corrections and its end, chained by
/// hash as the recorders write them. The clock only moves forward.
/// </summary>
internal sealed class ConsumptionLog
{
    public const string ProjectId = UsageLogBuilder.ProjectId;

    public static readonly RunId Run = new("20261107-a1b2c3");

    public const string PlanHash = "plan-hash";

    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    private readonly DecisionLog _log = new();

    public DateTimeOffset Now { get; private set; } = T0;

    public IReadOnlyList<DecisionEntry> Entries => _log.Entries;

    public ConsumptionLog Started(string planHash = PlanHash, string? run = null, IReadOnlyList<RosterEntry>? roster = null) =>
        Append(DecisionKind.RunStarted, new RunStartedPayload(planHash, HashA, "base", "main", HashB, roster ?? [], null) { RunId = run ?? Run.Value, V = 1 });

    /// <summary>One attempt of an agent: started, waiting <paramref name="queue"/> in the queue, launched, and ended after <paramref name="duration"/>.</summary>
    public ConsumptionLog Attempt(
        string? task,
        int attempt,
        string agent = "ejecutor",
        AgentRunPurpose purpose = AgentRunPurpose.Executor,
        ProviderId? provider = null,
        string? model = "claude-sonnet-5-5",
        AttemptOutcome status = AttemptOutcome.Succeeded,
        long input = 10_000,
        long read = 0,
        long output = 100,
        TimeSpan? queue = null,
        TimeSpan? duration = null,
        int omitted = 0,
        IReadOnlyList<ContextPartSnapshot>? parts = null,
        (long Used, long Window)? peak = null,
        int? compactions = 0,
        AttemptEndClass? end = null,
        string? session = null,
        string? resumed = null,
        string? run = null,
        string? error = null)
    {
        var on = provider ?? ProviderId.ClaudeCode;
        var runId = run ?? Run.Value;
        var taskId = task is null ? (TaskId?)null : new TaskId(task);
        var pack = omitted > 0 ? new ContextPackReference(HashB, ["RQ-1"], [.. Enumerable.Range(1, omitted).Select(i => "mem-" + i)], 1000) : null;
        Append(DecisionKind.AgentRunStarted, new AgentRunStartedPayload(
            taskId, attempt, new AgentId(agent), on, UsageLogBuilder.VendorOf(on), model, "wt", new EvidenceId($"ev-prompt-{task}-{attempt}"), HashA, [], purpose, pack, parts)
        {
            RunId = runId,
            V = parts is null ? AgentRunStartedPayload.PurposeVersion : AgentRunStartedPayload.ContextPartsVersion,
        });

        Now += queue ?? TimeSpan.FromSeconds(1);
        var launched = Now;
        Now += duration ?? TimeSpan.FromMinutes(10);
        var usage = on == ProviderId.CodexCli ? Snapshots.Codex(input, read, 0, output, 0) : Snapshots.Claude(input, read, 0, output, 0.1m);
        var peakSnapshot = peak is { } p ? new ContextPeakSnapshot(model, Snapshots.Reported(p.Used, "claude assistant.message.usage"), Snapshots.Reported(p.Window, "claude result.modelUsage")) : null;
        var finalEnd = end is { } cls ? new AttemptEndSnapshot(cls) : AttemptEndSnapshot.ImpliedBy(status);
        return Append(DecisionKind.AgentRunFinished, new AgentRunFinishedPayload(
            taskId, attempt, status, 0, session ?? $"s-{task}-{attempt}", error, usage, null, launched, model, resumed, peakSnapshot, on == ProviderId.CodexCli ? null : compactions, 3, finalEnd)
        {
            RunId = runId,
            V = AgentRunFinishedPayload.EndClassVersion,
        });
    }

    public ConsumptionLog Status(string task, WorkTaskStatus to, int attempt = 1) =>
        Append(DecisionKind.TaskStatusChanged, new TaskStatusChangedPayload(new TaskId(task), WorkTaskStatus.Running, to, "x", null, [], attempt) { RunId = Run.Value });

    public ConsumptionLog Tests(string task, int attempt, int failed, int passed, string title = "Pruebas (test)") =>
        Append(DecisionKind.EvidenceRegistered, new EvidenceRegisteredPayload(new EvidenceId($"ev-test-{task}-{attempt}-{Entries.Count}"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, HashB, new TaskId(task), attempt, title, "agent-studio", Now, new TestRunSnapshot(passed, failed, 0, failed > 0 ? 1 : 0)) { RunId = Run.Value, V = EvidenceRegisteredPayload.MetadataVersion });

    public ConsumptionLog Evidence(string id, EvidenceKind kind, string task, int attempt, string title, string sha256) =>
        Append(DecisionKind.EvidenceRegistered, new EvidenceRegisteredPayload(new EvidenceId(id), kind, EvidenceOrigin.CapturedBySystem, sha256, new TaskId(task), attempt, title, "agent-studio", Now) { RunId = Run.Value, V = EvidenceRegisteredPayload.MetadataVersion });

    public ConsumptionLog PlanRevised(string? run = null, string planHash = PlanHash) =>
        Append(DecisionKind.PlanRevised, new PlanRevisedPayload(planHash, "first-plan", "x") { RunId = run ?? Run.Value });

    public ConsumptionLog Verdict(string id, string? supersedes) =>
        Append(DecisionKind.VerdictIssued, new VerdictIssuedPayload(id, new TaskId("R1"), VerdictDecision.Approved, true, [], HashA, HashB, supersedes, null) { RunId = Run.Value });

    public ConsumptionLog Finished(RunPhase phase) =>
        Append(DecisionKind.RunFinished, new RunFinishedPayload(phase, "fin", null, null) { RunId = Run.Value });

    private ConsumptionLog Append(DecisionKind kind, DecisionPayload payload)
    {
        _log.Append(Now, SystemActor, kind, kind.ToString(), DecisionPayloadSerializer.Serialize(payload));
        Now += TimeSpan.FromSeconds(1);
        return this;
    }
}
