using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Work;

namespace AgentStudio.Observability.Usage;

// Phase 9 (docs/16 D-34, ADR-0040): the milestone, the activity and the wait of an attempt. They are not in AgentRunStarted or
// AgentRunFinished: they come from the rest of the same verified log (the links of the milestones, the starts and ends of the runs, the
// status of the tasks, the integrations and the instructions), read in the same pass, so there is still no table and no extra reading.
public sealed partial class UsageProjector
{
    /// <summary>The prefix of the block reason of a task that waits for its quota window (<c>BlockReason.ToString</c>; it also starts the estimated form).</summary>
    private const string RateLimitedPrefix = "RateLimitedUntil";

    /// <summary>The kinds the activity needs besides the two of the attempts.</summary>
    private static bool IsActivityKind(DecisionKind kind) =>
        kind is DecisionKind.RunStarted or DecisionKind.RunFinished or DecisionKind.TaskStatusChanged or DecisionKind.IntegrationPerformed
            or DecisionKind.InstructionsIssued or DecisionKind.MilestoneLinked;

    /// <summary>What one pass over the log collects for the milestone, the activity and the waits.</summary>
    private sealed class LogFacts
    {
        private readonly Dictionary<string, RunFacts> _runs = new(StringComparer.Ordinal);
        private readonly List<MilestoneLink> _links = [];
        private readonly List<(string, string)> _follows = [];
        private readonly Dictionary<(string Run, string Task), (DateTimeOffset Since, int Attempt)> _waiting = [];
        private readonly Dictionary<(string Run, string Task, int Attempt), long> _waited = [];
        private readonly HashSet<(string Run, string Task, int Attempt)> _open = [];

        public void Add(DecisionEntry entry, DecisionPayload payload)
        {
            switch (payload)
            {
                case MilestoneLinkedPayload linked:
                    _links.Add(new MilestoneLink(linked.RunId, linked.Kind, linked.Target));
                    break;

                case RunStartedPayload started:
                    var begun = RunOf(started.RunId);
                    begun.Started = true;
                    begun.Follows = string.IsNullOrEmpty(started.Follows) ? null : started.Follows;
                    if (!string.IsNullOrEmpty(started.Follows))
                    {
                        _follows.Add((started.RunId, started.Follows));
                    }

                    break;

                case RunFinishedPayload finished:
                    RunOf(finished.RunId).Phase = finished.Phase;
                    break;

                case IntegrationPerformedPayload integration:
                    var run = RunOf(integration.RunId);
                    if (integration.IsDelivery)
                    {
                        run.Delivered = true;
                    }
                    else if (integration.ConflictPaths.Count > 0)
                    {
                        run.HadConflict = true;
                    }
                    else if (integration.ResultCommit is not null && integration.Task is { } merged)
                    {
                        run.TaskOf(merged.Value).Merged = true;
                    }

                    break;

                case InstructionsIssuedPayload instructions:
                    RunOf(instructions.RunId).TaskOf(instructions.Task.Value).Scope = instructions.WriteScope;
                    break;

                case TaskStatusChangedPayload change:
                    var changed = RunOf(change.RunId).TaskOf(change.Task.Value);
                    changed.Status = change.To;
                    if (change.To == WorkTaskStatus.Completed)
                    {
                        changed.CompletedAttempt = change.Attempt;
                    }

                    TrackQuotaWait(entry, change);
                    break;
            }
        }

        /// <summary>
        /// A task blocked by <c>RateLimitedUntil</c> waits from that entry until it leaves the block; the wait is the attempt's that hit the
        /// limit. A wait that never ends is open. Blocks of another kind (a person, the app stopping) are not quota.
        /// </summary>
        private void TrackQuotaWait(DecisionEntry entry, TaskStatusChangedPayload change)
        {
            var key = (change.RunId, change.Task.Value);
            if (_waiting.Remove(key, out var wait))
            {
                var attempt = (change.RunId, change.Task.Value, wait.Attempt);
                _waited[attempt] = _waited.GetValueOrDefault(attempt) + Math.Max(0, (long)(entry.RecordedAt - wait.Since).TotalMilliseconds);
                _open.Remove(attempt);
            }

            if (change.To == WorkTaskStatus.Blocked && change.BlockReason?.StartsWith(RateLimitedPrefix, StringComparison.Ordinal) == true)
            {
                _waiting[key] = (entry.RecordedAt, change.Attempt);
                _open.Add((change.RunId, change.Task.Value, change.Attempt));
            }
        }

        private RunFacts RunOf(string run)
        {
            if (!_runs.TryGetValue(run, out var facts))
            {
                _runs[run] = facts = new RunFacts();
            }

            return facts;
        }

        /// <summary>Gives every attempt its milestones, its activity and its wait for quota.</summary>
        public void Apply(List<UsageAttempt> attempts)
        {
            var membership = MilestoneMembership.Resolve(_links, _follows);
            for (var i = 0; i < attempts.Count; i++)
            {
                var attempt = attempts[i];
                var run = _runs.GetValueOrDefault(attempt.RunId);
                RunFacts? orchestration = run is { Started: true } ? run : null;
                var task = attempt.TaskId is { } id ? run?.Tasks.GetValueOrDefault(id) : null;
                var input = new ActivityInput(
                    attempt.Purpose,
                    attempt.TaskId is not null,
                    attempt.Attempt,
                    attempt.Outcome,
                    orchestration is null ? null : new ActivityRunFacts(StateOf(orchestration), orchestration.Follows is { } followed && _runs.GetValueOrDefault(followed) is { HadConflict: true }),
                    task is null ? null : new ActivityTaskFacts(task.Status, task.Scope, task.Merged, task.CompletedAttempt));
                var key = (attempt.RunId, attempt.TaskId ?? string.Empty, attempt.Attempt);
                attempts[i] = attempt with
                {
                    Milestones = membership.GetValueOrDefault(attempt.RunId) ?? [],
                    Activity = ActivityClassifier.Classify(input),
                    QuotaWaitMilliseconds = _waited.TryGetValue(key, out var waited) ? waited : null,
                    OpenQuotaWait = _open.Contains(key),
                };
            }
        }

        private static ActivityRunState StateOf(RunFacts run) =>
            run.Delivered ? ActivityRunState.Delivered
            : run.Phase is RunPhase.Failed or RunPhase.Cancelled or RunPhase.Blocked ? ActivityRunState.Dead
            : run.Phase == RunPhase.Succeeded ? ActivityRunState.Undelivered
            : ActivityRunState.Open;
    }

    private sealed class RunFacts
    {
        public string? Follows { get; set; }

        public RunPhase? Phase { get; set; }

        public bool Delivered { get; set; }

        public bool HadConflict { get; set; }

        public Dictionary<string, TaskFacts> Tasks { get; } = new(StringComparer.Ordinal);

        /// <summary>Whether a <c>RunStarted</c> was read: the unit is an orchestration run, not a debate, an audit or an auxiliary run.</summary>
        public bool Started { get; set; }

        public TaskFacts TaskOf(string task)
        {
            if (!Tasks.TryGetValue(task, out var facts))
            {
                Tasks[task] = facts = new TaskFacts();
            }

            return facts;
        }
    }

    private sealed class TaskFacts
    {
        public WorkTaskStatus? Status { get; set; }

        public IReadOnlyList<string>? Scope { get; set; }

        public bool Merged { get; set; }

        public int? CompletedAttempt { get; set; }
    }
}
