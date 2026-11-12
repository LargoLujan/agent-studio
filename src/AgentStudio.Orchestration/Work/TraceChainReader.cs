using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// Walks the chain of a node (docs/16 F9-4, D-29): requirement, milestone, task, attempt, change (the commit and the diff), verification, verdict and
/// delivery, from the verified log in both directions. It reads only; a log that does not verify throws <see cref="DecisionLogIntegrityException"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What it joins.</b> The facts of the log (the milestone and its runs and follow-ups, the requirements of its set and those a task covers, the attempts of a
/// task, the evidence of an attempt with the commit it verified, the valid verdicts, the delivery of a run) and the explicit links in force (a commit of the person that
/// fulfils a requirement…). Nothing inferred is in the chain: the suggestions are asked for from <see cref="ITraceSuggester"/>, and so <see cref="TraceChainStep.Suggested"/>
/// is never set here.</item>
/// <item><b>Which way it walks.</b> From the node it goes towards the delivery only through nodes of a later stage, and towards the requirement only through nodes of an
/// earlier one; so the chain of a task is its own, not that of every task of its run. A milestone met on the way is where that way ends. The run is a hub between the
/// milestone and its tasks and is not a step.</item>
/// <item><b>The steps</b> come in the order of the stages, each node once, with the title the log or the plan gives (the statement of a requirement, the name of a
/// milestone, the title of a task).</item>
/// </list>
/// </remarks>
public sealed class TraceChainReader(IDecisionRecorder recorder, IRequirementStore requirements, IPlanStore plans) : ITraceChainReader
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

        var steps = new List<(int At, int Order, TraceRef Node)>();
        var order = 0;
        foreach (var node in Enumerable.Reverse(Walk(graph, start, upstream: true)).Append(start).Concat(Walk(graph, start, upstream: false)))
        {
            if (graph.At(node) is { } at && at != RunAt)
            {
                steps.Add((at, order++, node));
            }
        }

        return new TraceChain(
            start,
            [.. steps.OrderBy(s => s.At).ThenBy(s => s.Order).Select(s => new TraceChainStep(StageOf(s.At), s.Node, graph.Titles.GetValueOrDefault(s.Node), false))]);
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
        var payloads = entries.Select(e => (Entry: e, Payload: RunLog.Payload(e))).Where(x => x.Payload is not null).Select(x => (x.Entry, Payload: x.Payload!)).ToList();

        // Milestones: their name, the requirements of the set they work against now, and their runs.
        foreach (var opened in payloads.Select(p => p.Payload).OfType<MilestoneOpenedPayload>())
        {
            var milestone = TraceRef.Milestone(opened.RunId);
            graph.Add(milestone, MilestoneAt, opened.Name);
            var current = payloads.Select(p => p.Payload).OfType<MilestoneRequirementsRevisedPayload>().LastOrDefault(r => r.RunId == opened.RunId)?.RequirementSetHash ?? opened.RequirementSetHash;
            await AddRequirementsAsync(graph, projectId, current, milestone, cancellationToken).ConfigureAwait(false);
            foreach (var run in WorkLog.RunsOfMilestone(entries, opened.RunId))
            {
                graph.Edge(milestone, TraceRef.Run(run), RunAt);
            }
        }

        // Runs: their tasks and what the tasks cover.
        var tasksOfRun = new Dictionary<string, List<TraceRef>>(StringComparer.Ordinal);
        foreach (var started in payloads.Select(p => p.Payload).OfType<RunStartedPayload>().DistinctBy(p => p.RunId))
        {
            var run = TraceRef.Run(started.RunId);
            graph.Add(run, RunAt, null);
            var plan = await plans.FindAsync(projectId, started.PlanHash, cancellationToken).ConfigureAwait(false);
            var tasks = tasksOfRun[started.RunId] = [];
            foreach (var spec in plan?.Tasks ?? [])
            {
                var task = TraceRef.Task(started.RunId, spec.Id.Value);
                tasks.Add(task);
                graph.Edge(run, task, TaskAt, spec.Title);
                foreach (var covered in spec.Covers)
                {
                    graph.Edge(task, TraceRef.Requirement(covered.Value), RequirementAt);
                }
            }
        }

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
                case VerdictIssuedPayload { Valid: true } verdict when tasksOfRun.TryGetValue(verdict.RunId, out var judged):
                    foreach (var task in verdict.Task is { } one ? judged.Where(t => t == TraceRef.Task(verdict.RunId, one.Value)) : judged)
                    {
                        graph.Edge(task, TraceRef.Verdict(verdict.VerdictId), VerdictAt);
                    }

                    break;
                case IntegrationPerformedPayload { IsDelivery: true, ResultCommit: { } commit, ConflictPaths.Count: 0 } delivery when tasksOfRun.TryGetValue(delivery.RunId, out var delivered) && TraceRef.IsFullCommitId(commit):
                    var deliveryNode = TraceRef.Commit(commit);
                    graph.Place(deliveryNode, DeliveryAt);
                    foreach (var task in delivered)
                    {
                        graph.Edge(task, deliveryNode, DeliveryAt);
                    }

                    break;
            }
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

    /// <summary>The nodes with their place on the chain and their title, and the edges between them (read in both directions).</summary>
    private sealed class ChainGraph
    {
        private readonly Dictionary<TraceRef, int> _at = [];
        private readonly Dictionary<TraceRef, List<TraceRef>> _edges = [];

        public Dictionary<TraceRef, string?> Titles { get; } = [];

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
