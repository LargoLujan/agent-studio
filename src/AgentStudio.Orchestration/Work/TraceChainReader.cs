using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// Walks the chain of a node (docs/16 F9-4, D-29): requirement, milestone, task, attempt, change (the commit and the diff), verification, verdict and
/// delivery, from the verified log in both directions. It reads only; a log that does not verify throws <see cref="DecisionLogIntegrityException"/>.
/// The chain is the same whichever node it starts from (a requirement, a task, a commit, a test run, a verdict or the delivery).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What it joins.</b> The facts of the log (the milestone and its runs and follow-ups, the requirements of its set and those a task covers, the attempts of a
/// task, the evidence of an attempt with the commit it verified, the valid verdicts, the delivery of a run) and the explicit links in force (a commit of the person that
/// fulfils a requirement…). Nothing inferred is in the chain: the suggestions are asked for from <see cref="ITraceSuggester"/>, and so <see cref="TraceChainStep.Suggested"/>
/// is never set here.</item>
/// <item><b>Which way it walks.</b> From the node it goes towards the delivery only through nodes of a later stage, and towards the requirement only through nodes of an
/// earlier one; so the chain of a task is its own, not that of every task of its run. A milestone met on the way is where that way ends. The run is a hub between the
/// milestone and its tasks and is not a step. The verdicts and the delivery hang off the tasks they judged and delivered (see below): a chain that reaches a task has them, and a
/// chain that starts at a verdict or the delivery has the work of its tasks (attempts, changes and verifications). A verdict counts only when it is valid in the log and in the
/// store, with the same decision.</item>
/// <item><b>A verdict and a delivery are shared nodes</b>: every task they hang off, with its requirements and its work, is in the chain from any of them, so the chain is the
/// same from every node it contains. A verdict that judged R1 and R2 puts them in one chain; one that judged only R1 leaves R2 out of it, unless the two were delivered together.
/// The unit is the task: a task that covers R1 and R2 carries the verdict that judged either of them (the board, per requirement, says it more finely).</item>
/// <item><b>Which tasks a verdict hangs from</b> is the rule the board and the participation use too (<see cref="VerdictReach"/>): a verdict judges a task only if it assessed a
/// requirement the task covers (all of its scope if it assessed none), and so a chain from a requirement the verdict did not assess does not have it.</item>
/// <item><b>Several verdicts</b> over the same work (a retry after a rejection, a revision, a follow-up run) are all shown, in the order of the log, each with its
/// <see cref="TraceChainStep.Standing"/>: the last one that judged a requirement (in the last run that started that covers it) is in force, an earlier one is superseded, and a
/// person's challenge leaves it challenged or, once closed with another decision, revised (docs/15 D-38 to D-40, as on the board). A verdict that is not in force is never shown
/// as the one that stands.</item>
/// <item><b>Which tasks a delivery delivered.</b> The integration task is recorded when the engine accepted its integration (the branch of the run, not a hidden candidate
/// ref), with a commit and no conflict. It merged the code of the completed tasks that produce code and that it depends on, looking through those that produce none, and of
/// what they were built on; a task that failed or was skipped, or one that is not a prerequisite of the integration, was not merged.</item>
/// <item><b>The steps</b> come in the order of the stages, each node once, with the title the log or the plan gives (the statement of a requirement, the name of a
/// milestone, the title of a task).</item>
/// </list>
/// </remarks>
public sealed class TraceChainReader(IDecisionRecorder recorder, IRequirementStore requirements, IPlanStore plans, IVerdictStore verdicts) : ITraceChainReader
{
    // Positions on the way from requirement to delivery; the run is a hub between the milestone and its tasks.
    private const int RequirementAt = 0;
    private const int MilestoneAt = 2;
    private const int RunAt = 3;
    private const int TaskAt = 4;
    private const int AttemptAt = 6;
    private const int ChangeAt = 8;
    private const int VerificationAt = 10;
    private const int VerdictAt = 12;
    private const int DeliveryAt = 14;

    public async Task<TraceChain> ChainAsync(ProjectContext project, TraceRef start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(start);
        var state = await TraceLinkService.ReadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        var graph = await BuildAsync(project.ProjectId, state, cancellationToken).ConfigureAwait(false);

        if (graph.At(start) is null)
        {
            return new TraceChain(start, []);
        }

        var up = Walk(graph, start, upstream: true);
        var down = Walk(graph, start, upstream: false);
        var core = up.Append(start).Concat(down).ToList();
        var startAt = graph.At(start)!.Value;

        // What hangs off the tasks of the chain: their verdicts and their delivery; and, from a verdict or the delivery, the work of those tasks. A verdict and a delivery
        // are shared nodes: the other tasks they hang off (and with them their requirements and work) are in the chain too, so the chain is the same from every node it holds.
        var extra = new List<TraceRef>();
        var pending = new Queue<TraceRef>(core.Where(n => graph.At(n) == TaskAt));
        var inChain = new HashSet<TraceRef>(pending);
        while (pending.Count > 0)
        {
            var task = pending.Dequeue();
            if (startAt >= VerdictAt && core.Contains(task))
            {
                extra.AddRange(Walk(graph, task, upstream: false).Where(n => graph.At(n) is { } at && at < VerdictAt));
            }

            foreach (var hanging in graph.Neighbours(task).Where(n => graph.At(n) is { } at && at >= VerdictAt))
            {
                extra.Add(hanging);
                foreach (var other in graph.Neighbours(hanging).Where(n => graph.At(n) == TaskAt))
                {
                    if (inChain.Add(other))
                    {
                        extra.Add(other);
                        extra.AddRange(Walk(graph, other, upstream: true));
                        extra.AddRange(Walk(graph, other, upstream: false).Where(n => graph.At(n) is { } at && at < VerdictAt));
                        pending.Enqueue(other);
                    }
                }
            }
        }

        // The change of an attempt is its diff and its commit, on the same stage: one does not reach the other, nor the verification of the other; the attempt above them does.
        foreach (var attempt in up.Where(n => graph.At(n) == AttemptAt))
        {
            extra.AddRange(graph.Neighbours(attempt).Where(n => graph.At(n) is ChangeAt or VerificationAt));
        }

        var steps = new List<(int At, int Order, TraceRef Node)>();
        var order = 0;
        foreach (var node in Enumerable.Reverse(up).Append(start).Concat(down).Concat(extra).Distinct())
        {
            if (graph.At(node) is { } at && at != RunAt)
            {
                steps.Add((at, order++, node));
            }
        }

        return new TraceChain(
            start,
            [.. steps.OrderBy(s => s.At).ThenBy(s => graph.Sequence.GetValueOrDefault(s.Node)).ThenBy(s => s.Node.Kind).ThenBy(s => s.Node.Id, StringComparer.Ordinal).Select(s => new TraceChainStep(StageOf(s.At), s.Node, graph.Titles.GetValueOrDefault(s.Node), false, graph.Standings.TryGetValue(s.Node, out var standing) ? standing : null))]);
    }

    /// <summary>The nodes reached from <paramref name="start"/> through nodes of an earlier (or later) stage, breadth first, without the start.</summary>
    private static List<TraceRef> Walk(ChainGraph graph, TraceRef start, bool upstream)
    {
        var seen = new HashSet<TraceRef> { start };
        var queue = new Queue<TraceRef>([start]);
        var reached = new List<TraceRef>();
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            // A milestone met on the way is where it ends; so is a requirement.
            if (!node.Equals(start) && (node.Kind == TraceNodeKind.Milestone || graph.At(node) == RequirementAt))
            {
                continue;
            }

            var here = graph.At(node)!.Value;
            foreach (var next in graph.Neighbours(node))
            {
                if (graph.At(next) is not { } there || !(upstream ? there < here : there > here) || !seen.Add(next))
                {
                    continue;
                }

                reached.Add(next);
                queue.Enqueue(next);
            }
        }

        return reached;
    }

    private static TraceChainStage StageOf(int at) => at switch
    {
        RequirementAt => TraceChainStage.Requirement,
        MilestoneAt => TraceChainStage.Milestone,
        TaskAt => TraceChainStage.Task,
        AttemptAt => TraceChainStage.Attempt,
        ChangeAt => TraceChainStage.Change,
        VerificationAt => TraceChainStage.Verification,
        VerdictAt => TraceChainStage.Verdict,
        _ => TraceChainStage.Delivery,
    };

    private async Task<ChainGraph> BuildAsync(string projectId, TraceState state, CancellationToken cancellationToken)
    {
        var graph = new ChainGraph();
        var entries = state.Entries;
        var parsed = WorkLog.Parse(entries);
        var payloads = parsed.Where(x => x.Payload is not null).Select(x => (x.Entry, Payload: x.Payload!)).ToList();
        var storedVerdicts = (await verdicts.ListAsync(projectId, null, cancellationToken).ConfigureAwait(false)).GroupBy(v => v.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // Milestones: their name, the requirements of the set they work against now, and their runs.
        foreach (var opened in payloads.Select(p => p.Payload).OfType<MilestoneOpenedPayload>())
        {
            var milestone = TraceRef.Milestone(opened.RunId);
            graph.Add(milestone, MilestoneAt, opened.Name);
            var current = payloads.Select(p => p.Payload).OfType<MilestoneRequirementsRevisedPayload>().LastOrDefault(r => r.RunId == opened.RunId)?.RequirementSetHash ?? opened.RequirementSetHash;
            await AddRequirementsAsync(graph, projectId, current, milestone, cancellationToken).ConfigureAwait(false);
            foreach (var run in WorkLog.RunsOfMilestone(parsed, opened.RunId))
            {
                graph.Edge(milestone, TraceRef.Run(run), RunAt);
            }
        }

        // Runs: their tasks and what the tasks cover.
        var tasksOfRun = new Dictionary<string, List<PlannedTask>>(StringComparer.Ordinal);
        var prerequisites = new Dictionary<(string Run, string Task), string[]>();
        var planCache = new Dictionary<string, AgentStudio.Domain.Orchestration.PlanDefinition?>(StringComparer.Ordinal);
        foreach (var started in payloads.Select(p => p.Payload).OfType<RunStartedPayload>().DistinctBy(p => p.RunId))
        {
            var run = TraceRef.Run(started.RunId);
            graph.Add(run, RunAt, null);
            if (!planCache.TryGetValue(started.PlanHash, out var plan))
            {
                planCache[started.PlanHash] = plan = await plans.FindAsync(projectId, started.PlanHash, cancellationToken).ConfigureAwait(false);
            }

            var tasks = tasksOfRun[started.RunId] = [];
            foreach (var spec in plan?.Tasks ?? [])
            {
                var task = TraceRef.Task(started.RunId, spec.Id.Value);
                tasks.Add(new PlannedTask(task, spec.Id, spec.Covers, spec.Kind == AgentStudio.Domain.Work.WorkTaskKind.Review, RunContext.ProducesCode(spec)));

                prerequisites[(started.RunId, spec.Id.Value)] = [.. spec.DependsOn.Select(d => d.Prerequisite.Value)];

                graph.Edge(run, task, TaskAt, spec.Title);
                foreach (var covered in spec.Covers)
                {
                    graph.Edge(task, TraceRef.Requirement(covered.Value), RequirementAt);
                }
            }
        }

        // Which tasks a delivery delivered: those that finished completed and, when the log records the integrations of tasks of the run, were integrated.
        var completed = new HashSet<(string Run, string Task)>();
        var skipped = new HashSet<(string Run, string Task)>();
        var integrated = new HashSet<(string Run, string Task)>();
        var integrates = new HashSet<string>(StringComparer.Ordinal);

        // What each delivery delivered, as the log stood when it was made: a task completed or integrated afterwards is not part of it.
        var deliveredBy = new Dictionary<long, HashSet<string>>();
        var hidden = AgentStudio.Orchestration.Integration.IntegrationNames.HiddenRefPrefix;
        foreach (var (entry, payload) in payloads)
        {
            switch (payload)
            {
                case IntegrationPerformedPayload { IsDelivery: true } delivery when tasksOfRun.TryGetValue(delivery.RunId, out var plannedTasks):
                    deliveredBy[entry.Sequence] = [.. plannedTasks.Select(t => t.Id.Value).Where(id => completed.Contains((delivery.RunId, id)) && (!integrates.Contains(delivery.RunId) || integrated.Contains((delivery.RunId, id))))];
                    break;
                case TaskStatusChangedPayload change:
                    if (change.To == AgentStudio.Domain.Work.WorkTaskStatus.Completed)
                    {
                        completed.Add((change.RunId, change.Task.Value));
                    }
                    else
                    {
                        completed.Remove((change.RunId, change.Task.Value));
                    }

                    if (change.To == AgentStudio.Domain.Work.WorkTaskStatus.Skipped)
                    {
                        skipped.Add((change.RunId, change.Task.Value));
                    }
                    else
                    {
                        skipped.Remove((change.RunId, change.Task.Value));
                    }

                    break;
                case IntegrationPerformedPayload { IsDelivery: false, Task: { } integratedTask } integration:
                    integrates.Add(integration.RunId);
                    // The engine records an integration it did not accept (a failed one, or one whose evidence it could not anchor) on the hidden candidate ref of the task,
                    // never on the branch of the run, and one that left no commit or conflicted merged nothing.
                    if (integration.ResultCommit is not null && integration.ConflictPaths.Count == 0 && !integration.TargetRef.StartsWith(hidden, StringComparison.Ordinal))
                    {
                        // The integration task (I1) merges the code of the completed tasks it depends on, through the tasks that produce none (a condition, an approval, a
                        // review), and with each of them the code it was built on. A task that did not complete merged nothing, and what is reachable only through it neither;
                        // a task that is not a prerequisite of the integration, however near, is not merged by it.
                        var toVisit = new Stack<string>([integratedTask.Value]);
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        while (toVisit.Count > 0)
                        {
                            var id = toVisit.Pop();
                            if (!seen.Add(id) || skipped.Contains((integration.RunId, id)) || !prerequisites.TryGetValue((integration.RunId, id), out var before))
                            {
                                continue;
                            }

                            // The integration task itself records before it completes. Any other task that produces code merged only if it completed; one that does not
                            // (a condition, an approval, a review) is looked through and merged nothing.
                            var isIntegration = id == integratedTask.Value;
                            var producesCode = tasksOfRun.TryGetValue(integration.RunId, out var planned) && planned.Any(t => t.Id.Value == id && t.ProducesCode);
                            if (!isIntegration && producesCode && !completed.Contains((integration.RunId, id)))
                            {
                                continue;
                            }

                            if (isIntegration || producesCode)
                            {
                                integrated.Add((integration.RunId, id));
                            }

                            foreach (var prerequisite in before)
                            {
                                toVisit.Push(prerequisite);
                            }
                        }
                    }

                    break;
            }
        }

        var last = new Dictionary<string, (int Run, int Sequence, string Verdict)>(StringComparer.Ordinal);
        var verdictStanding = new Dictionary<TraceRef, VerdictStanding>();

        // The verdicts of a revision (VerdictReach.Revises) and the challenged verdict each one belongs to: they never compete to be the one in force.
        var revisionOf = new Dictionary<TraceRef, StoredVerdict>();
        var sequence = 0;
        var runOrder = payloads.Select(p => p.Payload).OfType<RunStartedPayload>().Select(p => p.RunId).Distinct(StringComparer.Ordinal).Select((run, index) => (run, index)).ToDictionary(x => x.run, x => x.index, StringComparer.Ordinal);
        foreach (var (entry, payload) in payloads)
        {
            switch (payload)
            {
                case AgentRunStartedPayload { Task: { } task } launch when tasksOfRun.ContainsKey(launch.RunId):
                    graph.Edge(TraceRef.Task(launch.RunId, task.Value), TraceRef.Attempt(launch.RunId, task.Value, launch.Attempt), AttemptAt);
                    break;
                case EvidenceRegisteredPayload { Task: { } task, Attempt: { } attempt } evidence when tasksOfRun.ContainsKey(evidence.RunId):
                    var attemptNode = TraceRef.Attempt(evidence.RunId, task.Value, attempt);
                    if (evidence.Kind == EvidenceKind.CodeDiff)
                    {
                        graph.Edge(attemptNode, TraceRef.Evidence(evidence.Evidence.Value), ChangeAt);
                    }
                    else if (evidence.Kind is EvidenceKind.TestRun or EvidenceKind.BuildLog)
                    {
                        var verification = TraceRef.Evidence(evidence.Evidence.Value);
                        graph.Edge(attemptNode, verification, VerificationAt);
                        if (TraceRef.IsFullCommitId(evidence.SubjectCommit))
                        {
                            // The commit it verified: the change of the attempt, which the verification comes after.
                            graph.Edge(attemptNode, TraceRef.Commit(evidence.SubjectCommit!), ChangeAt);
                            graph.Edge(TraceRef.Commit(evidence.SubjectCommit!), verification, VerificationAt);
                        }
                    }

                    break;
                case VerdictIssuedPayload { Valid: true } verdict when tasksOfRun.TryGetValue(verdict.RunId, out var judged)
                    && storedVerdicts.TryGetValue(verdict.VerdictId, out var stored) && stored.Valid && stored.Verdict.Decision == verdict.Decision:
                    // The same rule as the board and the participation (VerdictReach): a verdict hangs from a task only if it judged it, and so only from the requirements
                    // it assessed. The verdict of a run is issued on its review task (VerdictKeeper), which covers nothing: it judged the work of the run.
                    var onOrdinaryTask = verdict.Task is { } issuedOn && judged.Any(t => t.Id == issuedOn && !t.IsReview);
                    var verdictNode = TraceRef.Verdict(verdict.VerdictId);
                    var hung = false;
                    var revises = VerdictReach.Revises(parsed, stored, storedVerdicts);
                    sequence++;
                    foreach (var task in judged.Where(t => VerdictReach.Judges(stored.Verdict, verdict.Task, onOrdinaryTask, t.Id, t.Covers)))
                    {
                        graph.Edge(task.Node, verdictNode, VerdictAt);

                        // The last verdict of the last run that judged a requirement is the one in force for it (a task that covers none, for the task).
                        IEnumerable<string> keys = revises is not null ? []
                            : stored.Verdict.Assessments.Count == 0
                                ? (task.Covers.Count == 0 ? [TaskKey(task.Node)] : task.Covers.Select(c => c.Value))
                                : stored.Verdict.Assessments.Where(a => task.Covers.Contains(a.Requirement)).Select(a => a.Requirement.Value);
                        foreach (var key in keys)
                        {
                            if (!last.TryGetValue(key, out var known) || (runOrder[verdict.RunId], sequence).CompareTo((known.Run, known.Sequence)) > 0)
                            {
                                last[key] = (runOrder[verdict.RunId], sequence, verdict.VerdictId);
                            }
                        }

                        hung = true;
                    }

                    if (hung)
                    {
                        verdictStanding[verdictNode] = VerdictReach.Standing(parsed, stored, storedVerdicts);
                        if (revises is not null)
                        {
                            revisionOf[verdictNode] = revises;
                        }

                        graph.Sequence[verdictNode] = sequence;
                    }

                    break;
                case IntegrationPerformedPayload { IsDelivery: true, ResultCommit: { } commit, ConflictPaths.Count: 0 } delivery when tasksOfRun.TryGetValue(delivery.RunId, out var delivered) && TraceRef.IsFullCommitId(commit):
                    var deliveryNode = TraceRef.Commit(commit);
                    graph.Place(deliveryNode, DeliveryAt);
                    foreach (var task in delivered.Where(t => deliveredBy.TryGetValue(entry.Sequence, out var ids) && ids.Contains(t.Id.Value)))
                    {
                        graph.Edge(task.Node, deliveryNode, DeliveryAt);
                    }

                    break;
            }
        }

        // Several verdicts can judge the same work (a retry after a rejection, a revision, a follow-up run): as on the board, the last verdict that judged a requirement decides
        // it, and where several runs cover it the last run that started (and a later run that covers it with no verdict leaves the earlier runs' verdicts superseded). One that
        // decides nowhere was superseded. A verdict a person challenged is shown as such while it is the last, and one whose revision settled another decision as revised, never as
        // the one that stands.
        var lastCoveringRun = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (run, planned) in tasksOfRun)
        {
            foreach (var requirement in planned.SelectMany(t => t.Covers).Select(c => c.Value))
            {
                lastCoveringRun[requirement] = Math.Max(lastCoveringRun.GetValueOrDefault(requirement, -1), runOrder.GetValueOrDefault(run, -1));
            }
        }

        var inForce = last.Where(k => !lastCoveringRun.TryGetValue(k.Key, out var covering) || k.Value.Run >= covering).Select(k => k.Value.Verdict).ToHashSet(StringComparer.Ordinal);
        foreach (var (node, standing) in verdictStanding)
        {
            if (revisionOf.TryGetValue(node, out var revised))
            {
                // A verdict of a revision stands as the verdict it revises does: in review while that one is, never the one in force by itself.
                graph.Standings[node] = verdictStanding.ContainsKey(TraceRef.Verdict(revised.Id)) && !inForce.Contains(revised.Id) ? TraceVerdictStanding.Superseded
                    : VerdictReach.Standing(parsed, revised, storedVerdicts).Challenged ? TraceVerdictStanding.Challenged
                    : TraceVerdictStanding.InForce;
                continue;
            }

            graph.Standings[node] = !inForce.Contains(node.Id)
                ? TraceVerdictStanding.Superseded
                : standing.Challenged ? TraceVerdictStanding.Challenged : standing.Revised ? TraceVerdictStanding.Revised : TraceVerdictStanding.InForce;
        }

        // The explicit links in force, between nodes that have a place on the chain.
        foreach (var link in state.Links.Active)
        {
            var from = Placed(graph, link.From);
            var to = Placed(graph, link.To);
            if (from is not null && to is not null)
            {
                graph.Edge(link.From, link.To, null);
            }
        }

        return graph;
    }

    private async Task AddRequirementsAsync(ChainGraph graph, string projectId, string setHash, TraceRef milestone, CancellationToken cancellationToken)
    {
        if (await requirements.FindAsync(projectId, setHash, cancellationToken).ConfigureAwait(false) is not { } set)
        {
            return;
        }

        foreach (var requirement in set.Requirements)
        {
            var node = TraceRef.Requirement(requirement.Id.Value);
            graph.Add(node, RequirementAt, requirement.Statement);
            graph.Edge(milestone, node, null);
        }
    }

    /// <summary>The place of a node of an explicit link that the facts did not place: backlog items are requirements, a commit is a change (or a delivery if it is one), and what is not on the chain has none.</summary>
    private static int? Placed(ChainGraph graph, TraceRef node)
    {
        if (graph.At(node) is { } known)
        {
            return known;
        }

        int? at = node.Kind switch
        {
            TraceNodeKind.Requirement or TraceNodeKind.BacklogItem => RequirementAt,
            TraceNodeKind.Commit => ChangeAt,
            _ => null,
        };
        if (at is { } place)
        {
            graph.Add(node, place, null);
        }

        return at;
    }

    private static string TaskKey(TraceRef task) => "task:" + task.Id;

    /// <summary>A task of the plan of a run: its node, what it covers and whether it is the review task (which covers nothing) or produces code.</summary>
    private sealed record PlannedTask(TraceRef Node, TaskId Id, IReadOnlyList<RequirementId> Covers, bool IsReview, bool ProducesCode);

    /// <summary>The nodes with their place on the chain and their title, and the edges between them (read in both directions).</summary>
    private sealed class ChainGraph
    {
        private readonly Dictionary<TraceRef, int> _at = [];
        private readonly Dictionary<TraceRef, List<TraceRef>> _edges = [];

        public Dictionary<TraceRef, string?> Titles { get; } = [];

        /// <summary>Where each verdict of the chain stands (docs/15 D-40): in force, superseded by a later one, or challenged.</summary>
        public Dictionary<TraceRef, TraceVerdictStanding> Standings { get; } = [];

        /// <summary>The place of each verdict in the log, so that verdicts come in the order they were issued and not by id.</summary>
        public Dictionary<TraceRef, int> Sequence { get; } = [];

        public int? At(TraceRef node) => _at.TryGetValue(node, out var at) ? at : null;

        public void Add(TraceRef node, int at, string? title)
        {
            _at.TryAdd(node, at);
            if (title is not null)
            {
                Titles.TryAdd(node, title);
            }
        }

        /// <summary>An edge; <paramref name="toAt"/> places <paramref name="to"/> when it is not placed yet (null: it must be).</summary>
        public void Edge(TraceRef from, TraceRef to, int? toAt, string? toTitle = null)
        {
            if (toAt is { } place)
            {
                Add(to, place, toTitle);
            }

            if (!_at.ContainsKey(from))
            {
                Add(from, DefaultAt(from), null);
            }

            Link(from, to);
            Link(to, from);
        }

        /// <summary>Places a node, over the place it had: a commit the delivery left is a delivery even if a verification named it first.</summary>
        public void Place(TraceRef node, int at) => _at[node] = at;

        private static int DefaultAt(TraceRef node) => node.Kind switch
        {
            TraceNodeKind.Requirement or TraceNodeKind.BacklogItem => RequirementAt,
            TraceNodeKind.Milestone => MilestoneAt,
            TraceNodeKind.Run => RunAt,
            TraceNodeKind.Task => TaskAt,
            TraceNodeKind.Attempt => AttemptAt,
            TraceNodeKind.Commit => ChangeAt,
            _ => throw new InvalidOperationException($"{node} has no place on the chain."),
        };

        public IReadOnlyList<TraceRef> Neighbours(TraceRef node) => _edges.TryGetValue(node, out var list) ? list : [];

        private void Link(TraceRef a, TraceRef b)
        {
            if (!_edges.TryGetValue(a, out var list))
            {
                _edges[a] = list = [];
            }

            if (!list.Contains(b))
            {
                list.Add(b);
            }
        }
    }
}
