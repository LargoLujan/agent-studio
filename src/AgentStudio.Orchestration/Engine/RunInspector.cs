using System.Runtime.CompilerServices;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// Reads a run back from its verified log and the stores, to look at it (<see cref="IRunInspector"/>): the graph is replayed
/// through the domain (<see cref="RunStateReplayer"/>) and the engine's memory of the run (attempts, agent runs, blocks and
/// verdict) is rebuilt as a resume rebuilds it (<see cref="RunResumer.Ledger"/>), so the snapshot is the one the live run
/// published when it stopped. Unlike a resume, nothing is checked against the header to go on, nothing is recovered and
/// nothing is recorded: a log the domain does not replay to its end is shown up to its first problem, which is said.
/// </summary>
internal sealed class RunInspector(EnginePorts ports)
{
    /// <summary>What an inspected run says when it did not finish: the words of the list of runs (<see cref="Orchestrator.ListRunsAsync"/>).</summary>
    public const string InterruptedSummary = "Interrumpida: no terminó y no hay ningún proceso detrás. Se puede reanudar.";

    /// <summary>The reason a task that was running when the app stopped is shown with: the one a resume records for it.</summary>
    public const string InterruptedReason = "Interrumpida: la aplicación se cerró mientras se trabajaba en ella.";

    public async Task<IOrchestrationRun> InspectAsync(ProjectContext project, RunId run, IReadOnlyList<DecisionEntry> log, CancellationToken cancellationToken)
    {
        var record = await ports.Runs.FindAsync(project.ProjectId, run, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run {run} is not registered in project {project.ProjectId}.");
        var entries = RunLog.Of(log, run.Value);
        var started = entries.Select(e => e.Payload).OfType<RunStartedPayload>().FirstOrDefault()
            ?? throw new InvalidOperationException($"Run {run} never started.");
        var plan = await ports.Plans.FindAsync(project.ProjectId, started.PlanHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The plan {started.PlanHash} that run {run} started with is not stored.");
        var requirements = await ports.Requirements.FindAsync(project.ProjectId, started.RequirementSetHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The requirement set {started.RequirementSetHash} that run {run} started with is not stored.");
        var evidence = (await ports.Evidence.ListByRunAsync(project.ProjectId, run.Value, cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.Id);

        var replay = RunStateReplayer.Replay(plan, record.CreatedAt, RunResumer.Events(entries), started.PlanHash);
        var graph = replay.Graph;
        var context = new RunContext(project, run, plan, requirements, record.Verification, record.Roster, record.Options, record.BaseCommit, record.CreatedAt);
        var problems = new List<string>(replay.Problems);
        var ledger = LedgerOf(entries, context, graph, problems);

        // The health as the log left it: measured at its last entry, so a task that was running when the app stopped is not
        // taken for one abandoned since.
        var lastAt = entries.Count > 0 ? entries[^1].Entry.RecordedAt : record.CreatedAt;
        var health = graph.Analyze(lastAt, context.StallThreshold);
        var finished = entries.Select(e => e.Payload).OfType<RunFinishedPayload>().LastOrDefault();
        var phase = finished?.Phase ?? RunPhase.Interrupted;
        var result = finished is null
            ? new RunResult(run, phase, health, null, ledger.VerdictId, InterruptedSummary, LoggedTexts.RunSummary(InterruptedSummary))
            : new RunResult(run, phase, health, finished.IntegrationCommit, finished.VerdictId, finished.Summary, LoggedTexts.RunSummary(finished.Summary));

        // A run that did not finish has nothing running behind it: what was running was interrupted, as a resume records it.
        var interrupted = finished is null
            ? graph.Tasks.Where(t => t.Status == WorkTaskStatus.Running).Select(t => t.Id).ToHashSet()
            : [];
        var snapshot = new RunSnapshot(
            run,
            phase,
            [.. graph.Tasks.Select(t => new TaskView(
                t.Id,
                t.Title,
                t.Kind,
                interrupted.Contains(t.Id) ? WorkTaskStatus.Blocked : t.Status,
                t.AssignedTo,
                ledger.Attempts.GetValueOrDefault(t.Id),
                t.RetryCount,
                ledger.Worktrees.GetValueOrDefault(t.Id)?.Path,
                ledger.Worktrees.GetValueOrDefault(t.Id)?.RefName,
                interrupted.Contains(t.Id) ? BlockReason.Interrupted.ToString() : ledger.Blocks.GetValueOrDefault(t.Id)?.ToString(),
                [.. t.CompletionEvidence]))],
            health,
            ledger.AgentRunsUsed,
            record.Options.MaxAgentRuns);

        var history = History(entries, evidence, context);
        history.AddRange(interrupted.Select(task => new TaskStatusChangedEvent(lastAt, task, WorkTaskStatus.Running, WorkTaskStatus.Blocked, InterruptedReason, LoggedTexts.TaskReason(InterruptedReason))));
        if (problems.Count > 0)
        {
            history.Add(new RunPhaseChangedEvent(
                lastAt,
                phase,
                $"El registro de esta ejecución no se puede reproducir entero ({string.Join(" ", problems)}): se muestra hasta ese punto."));
        }

        return new InspectedRun(snapshot, history, result);
    }

    /// <summary>
    /// The engine's memory of the run, as a resume rebuilds it, from the entries about tasks the plan has: a log that names
    /// other tasks is shown up to its problem, never refused for it. What cannot be rebuilt leaves the attempts and blocks
    /// unknown, and the problem is said.
    /// </summary>
    private static RunLedger LedgerOf(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries, RunContext context, TaskGraph graph, List<string> problems)
    {
        var known = entries.Where(e => TaskOf(e.Payload) is not { } task || context.Specs.ContainsKey(task)).ToList();
        if (known.Count < entries.Count && problems.Count == 0)
        {
            problems.Add("El registro nombra tareas que no están en el plan de la ejecución.");
        }

        try
        {
            return RunResumer.Ledger(known, context, graph);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            problems.Add($"No se pudieron reconstruir los intentos ni los bloqueos: {ex.Message}");
            return new RunLedger();
        }
    }

    /// <summary>The task an entry is about, for the entries the engine's memory of a run reads per task; null otherwise.</summary>
    private static TaskId? TaskOf(DecisionPayload payload) => payload switch
    {
        TaskStatusChangedPayload change => change.Task,
        AgentRunStartedPayload started => started.Task,
        AgentRunFinishedPayload ended => ended.Task,
        AccessCheckedPayload check => check.Task,
        WorktreeCreatedPayload worktree => worktree.Task,
        _ => null,
    };

    /// <summary>
    /// The entries of the run as the monitor's events, in log order: each transition with its reason, each evidence the log
    /// registered with its task (as the store lists it for the run), each pause and resume, and each delivery. A task the plan
    /// does not have is left out: the run is shown with the tasks it was approved with.
    /// </summary>
    private static List<OrchestrationEvent> History(IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries, Dictionary<EvidenceId, EvidenceRecord> evidence, RunContext context)
    {
        var history = new List<OrchestrationEvent>();
        foreach (var (entry, payload) in entries)
        {
            switch (payload)
            {
                case TaskStatusChangedPayload change when context.Specs.ContainsKey(change.Task):
                    history.Add(new TaskStatusChangedEvent(entry.RecordedAt, change.Task, change.From, change.To, change.Reason, LoggedTexts.TaskReason(change.Reason)));
                    break;
                case EvidenceRegisteredPayload registered when evidence.TryGetValue(registered.Evidence, out var row):
                    var owner = registered.Task is { } task && context.Specs.ContainsKey(task) ? task : (TaskId?)null;
                    history.Add(new EvidenceCapturedEvent(entry.RecordedAt, owner, row));
                    break;
                case RunDispatchChangedPayload dispatch:
                    history.Add(new RunPhaseChangedEvent(
                        entry.RecordedAt,
                        dispatch.Change == RunDispatchChange.Paused ? RunPhase.Paused : RunPhase.Running,
                        Dispatch(dispatch),
                        LoggedTexts.Dispatch(
                            dispatch.Change == RunDispatchChange.Paused,
                            dispatch.DecidedBy,
                            string.Equals(dispatch.DecidedBy, OrchestrationRun.DecidedByUser, StringComparison.Ordinal))));
                    break;
                case RunResumedPayload resumed:
                    history.Add(new RunPhaseChangedEvent(entry.RecordedAt, RunPhase.Running, RunResumer.Notice(resumed.Cause), RunResumer.NoticeText(resumed.Cause)));
                    break;
                case IntegrationPerformedPayload { IsDelivery: true } delivered:
                    history.Add(new RunDeliveredEvent(entry.RecordedAt, delivered.TargetRef, delivered.ResultCommit));
                    break;
            }
        }

        return history;
    }

    /// <summary>A pause or a resume of the dispatch, in Spanish: the person's own («la pausaste tú»), or whoever else's.</summary>
    private static string Dispatch(RunDispatchChangedPayload dispatch)
    {
        var paused = dispatch.Change == RunDispatchChange.Paused;
        return string.Equals(dispatch.DecidedBy, OrchestrationRun.DecidedByUser, StringComparison.Ordinal)
            ? paused ? "La pausaste tú." : "La reanudaste tú."
            : paused ? $"La pausó {dispatch.DecidedBy}." : $"La reanudó {dispatch.DecidedBy}.";
    }

    /// <summary>An inspected run: its history already happened, its end is known, and it takes no command.</summary>
    private sealed class InspectedRun(RunSnapshot snapshot, IReadOnlyList<OrchestrationEvent> history, RunResult result) : IOrchestrationRun
    {
        private const string NotRunning = "The run is only being inspected: it does not run in this process, so it takes no command. Resume it from the list of runs to run it again.";

        public RunId Id => snapshot.Id;

        public IAsyncEnumerable<OrchestrationEvent> Events => Replay(CancellationToken.None);

        public Task<RunResult> Completion { get; } = Task.FromResult(result);

        public RunSnapshot Snapshot() => snapshot;

        public Task PauseAsync() => Task.FromException(new InvalidOperationException(NotRunning));

        public Task ResumeDispatchAsync() => Task.FromException(new InvalidOperationException(NotRunning));

        public Task RetryBlockedAsync(TaskId task) => Task.FromException(new InvalidOperationException(NotRunning));

        public Task CancelAsync(string reason) => Task.FromException(new InvalidOperationException(NotRunning));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async IAsyncEnumerable<OrchestrationEvent> Replay([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var item in history)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
