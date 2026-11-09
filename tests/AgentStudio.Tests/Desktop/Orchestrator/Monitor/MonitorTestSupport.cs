using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Monitor;

/// <summary>
/// A <see cref="FakeRun"/> that takes answers: it records them (or throws what the test sets), as the engine does with
/// <c>AnswerAsync</c>. Everything else is the fake run's.
/// </summary>
internal sealed class AnsweringRun(FakeRun inner) : IOrchestrationRun
{
    public FakeRun Inner { get; } = inner;

    public List<QuestionAnswer> Answers { get; } = [];

    /// <summary>What <see cref="AnswerAsync"/> throws, when the test wants the engine to refuse.</summary>
    public Exception? Refusal { get; set; }

    public RunId Id => Inner.Id;

    public IAsyncEnumerable<OrchestrationEvent> Events => Inner.Events;

    public Task<RunResult> Completion => Inner.Completion;

    public RunSnapshot Snapshot() => Inner.Snapshot();

    public Task PauseAsync() => Inner.PauseAsync();

    public Task ResumeDispatchAsync() => Inner.ResumeDispatchAsync();

    public Task RetryBlockedAsync(TaskId task) => Inner.RetryBlockedAsync(task);

    public Task CancelAsync(string reason) => Inner.CancelAsync(reason);

    public Task AnswerAsync(QuestionAnswer answer)
    {
        if (Refusal is { } refusal)
        {
            return Task.FromException(refusal);
        }

        Answers.Add(answer);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}

/// <summary>What the tests of the monitor of Phase 13 share: runs with agents, the events of a step, questions and an agent definition.</summary>
internal static class MonitorTestSupport
{
    public static TaskView View(
        string id,
        string? agent,
        WorkTaskStatus status = WorkTaskStatus.Pending,
        string? block = null,
        PendingQuestion? question = null,
        WorkTaskKind kind = WorkTaskKind.AgentWork,
        int attempt = 1) =>
        new(new TaskId(id), $"Tarea {id}", kind, status, agent is null ? null : new AgentId(agent), attempt, RetryCount: 0, WorktreePath: null, Ref: null, block, [], question);

    /// <summary>A monitor on a queue (nothing is applied until <see cref="Pump"/>) and a clock the test moves, attached to a run with these tasks.</summary>
    public static (RunMonitorViewModel Monitor, FakeRun Run, QueueSynchronizationContext Ui, FakeTimeProvider Time) Open(params TaskView[] tasks) =>
        Open(RunPhase.Running, null, tasks);

    public static (RunMonitorViewModel Monitor, FakeRun Run, QueueSynchronizationContext Ui, FakeTimeProvider Time) Open(ProjectContext project, params TaskView[] tasks) =>
        Open(RunPhase.Running, project, tasks);

    public static (RunMonitorViewModel Monitor, FakeRun Run, QueueSynchronizationContext Ui, FakeTimeProvider Time) Open(RunPhase phase, ProjectContext? project, params TaskView[] tasks)
    {
        var ui = new QueueSynchronizationContext();
        var time = new FakeTimeProvider(T0);
        var monitor = new RunMonitorViewModel(time, ui);
        var run = new FakeRun(FakeRun.SnapshotOf(phase, tasks));
        monitor.Attach(run, project);
        return (monitor, run, ui, time);
    }

    /// <summary>Waits until the monitor has read <paramref name="count"/> events and then applies them on the queue, as the UI thread would.</summary>
    public static void Pump(QueueSynchronizationContext ui, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ui.Pending < count && DateTime.UtcNow < deadline)
        {
            Thread.Yield();
        }

        Assert.True(ui.Pending >= count, "The monitor did not read the events in time.");
        ui.RunAll();
    }

    /// <summary>Moves the clock past the grouping interval and runs what its timer posted.</summary>
    public static void Settle(FakeTimeProvider time, QueueSynchronizationContext ui)
    {
        time.Advance(TimeSpan.FromSeconds(1));
        ui.RunAll();
    }

    public static AgentActivityEvent Step(string task, long tokens, long window = 200_000, string model = "claude-sonnet-5-5") =>
        new(T0, Tid(task), new ContextObserved(T0, model, MetricValue<long>.Exact(tokens, "the CLI"), MetricValue<long>.Exact(window, "the CLI")));

    public static AgentActivityEvent Usage(string task, long input, long output, long cached = 0) =>
        new(T0, Tid(task), new UsageReported(
            T0,
            new TokenUsage(
                MetricValue<long>.Exact(input, "the CLI"),
                MetricValue<long>.Exact(output, "the CLI"),
                MetricValue<long>.Exact(cached, "the CLI"),
                MetricValue<long>.Exact(200_000, "the CLI"),
                MetricValue<decimal>.Unavailable("not reported"))));

    public static AgentActivityEvent EndOfAttempt(string task) =>
        new(T0, Tid(task), new RunFinished(T0, new AgentRunOutcome(AgentRunStatus.Succeeded, 0, null, null, "s1", null)));

    public static TaskStatusChangedEvent Move(string task, WorkTaskStatus from, WorkTaskStatus to, string reason = "") =>
        new(T0, Tid(task), from, to, reason);

    public static AgentQuestion Question(bool allowOther = true, bool recommend = true) => new(
        "¿Guardo las sesiones en SQLite o en ficheros JSON?",
        "Las dos cumplen RQ-02; cambia cómo se migran los datos.",
        [
            new QuestionOption("a", "SQLite", "Una base de datos local.", QuestionEffort.Small, recommend),
            new QuestionOption("b", "Ficheros JSON", "Un fichero por sesión.", QuestionEffort.Medium, false),
        ],
        allowOther);

    public static OptionEstimateSnapshot Estimate(string option, decimal? low, decimal? high, int references) => new(
        option,
        new MetricSnapshot(low, low is null ? MetricProvenance.Unavailable : MetricProvenance.Estimated, low is null ? "unknown: fewer than three references" : "p25 of 6 similar attempts", low is null ? MetricOrigin.Missing : MetricOrigin.EstimatedBySystem),
        new MetricSnapshot(high, high is null ? MetricProvenance.Unavailable : MetricProvenance.Estimated, high is null ? "unknown: fewer than three references" : "p75 of 6 similar attempts", high is null ? MetricOrigin.Missing : MetricOrigin.EstimatedBySystem),
        references);

    public static PendingQuestion Pending(string task = "T1", string agent = "exec1", int attempt = 1, bool allowOther = true, params OptionEstimateSnapshot[] estimates) =>
        new($"{task}-q1", new TaskId(task), new AgentId(agent), attempt, Question(allowOther), new EvidenceId("EV-1"), estimates, T0);

    public static PendingQuestion PendingOfOrchestrator(int number = 1) =>
        new($"plan-q{number}", null, new AgentId("orchestrator"), 1, Question(), new EvidenceId("EV-" + number), [], T0);

    public static AgentDefinition AgentDef(string id, string name, ProviderId? provider = null, string? model = "sonnet") => new(
        new AgentId(id),
        name,
        AgentRole.Executor,
        new ModelBinding(provider ?? ProviderId.ClaudeCode, VendorId.Anthropic, model),
        string.Empty,
        [],
        [],
        [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(10)),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));
}
