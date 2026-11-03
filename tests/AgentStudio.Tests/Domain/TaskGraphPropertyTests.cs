using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

/// <summary>
/// Properties of the task graph over random graphs. The repository convention applies: loops over a seeded
/// <see cref="Random"/>, no property-testing library. Each property also proves that its generator reached the
/// interesting cases, so it cannot pass vacuously.
/// </summary>
public sealed class TaskGraphPropertyTests
{
    private const int Graphs = 200;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private sealed record Built(TaskGraph Graph, IReadOnlyList<TaskId> Ids, int RejectedCycles, int BranchEdges);

    private sealed class Stats
    {
        public int Started { get; set; }

        public int Skipped { get; set; }

        public int YesDecisions { get; set; }

        public int NoDecisions { get; set; }

        public int AnyJoinsThatMergedADeadEdge { get; set; }

        public int Failures { get; set; }

        public int Cancellations { get; set; }
    }

    private static Built RandomGraph(Random random, bool conditions, bool joins, int maxRetries = 2)
    {
        var size = random.Next(2, 13);
        var graph = new TaskGraph();
        var ids = new List<TaskId>();
        for (var i = 0; i < size; i++)
        {
            var kind = conditions && random.Next(4) == 0 ? WorkTaskKind.Condition : WorkTaskKind.AgentWork;
            var join = joins && random.Next(3) == 0 ? JoinPolicy.AnyCompleted : JoinPolicy.AllCompleted;
            var task = NewTask($"t{i}", kind, random.Next(0, maxRetries + 1), join: join);
            graph.Add(task);
            ids.Add(task.Id);
        }

        var rejected = 0;
        var branchEdges = 0;
        for (var attempt = 0; attempt < size * 3; attempt++)
        {
            var dependent = ids[random.Next(size)];
            var prerequisite = ids[random.Next(size)];
            string? branch = graph[prerequisite].Kind == WorkTaskKind.Condition ? (random.Next(2) == 0 ? "yes" : "no") : null;
            try
            {
                graph.AddDependency(dependent, prerequisite, branch);
                if (branch is not null)
                {
                    branchEdges++;
                }
            }
            catch (DependencyCycleException)
            {
                rejected++;
            }
            catch (DomainRuleViolationException ex) when (ex.Rule == "graph.dependency-branch-conflict")
            {
                // The same pair was drawn twice with different branches: the first edge stays.
            }
        }

        return new Built(graph, ids, rejected, branchEdges);
    }

    // ---- an independent statement of "may this task start?" ----

    private static bool Satisfied(TaskGraph graph, DependencyEdge e) =>
        graph[e.Prerequisite].Status == WorkTaskStatus.Completed
        && (e.Branch is null || e.Branch == graph.TakenBranch(e.Prerequisite));

    private static bool Settled(TaskGraph graph, DependencyEdge e) =>
        graph[e.Prerequisite].Status is WorkTaskStatus.Completed or WorkTaskStatus.Skipped;

    private static bool MayStart(TaskGraph graph, WorkTask task)
    {
        var edges = graph.EdgesOf(task.Id);
        if (edges.Count == 0)
        {
            return true;
        }

        return task.Join == JoinPolicy.AllCompleted
            ? edges.All(e => Satisfied(graph, e))
            : edges.All(e => Settled(graph, e)) && edges.Any(e => Satisfied(graph, e));
    }

    /// <summary>
    /// Runs the graph the way the engine will: skip what is doomed, start a random ready task, finish it. Every start
    /// is checked against <see cref="MayStart"/>.
    /// </summary>
    private static Stats Simulate(TaskGraph graph, Random random, bool failures)
    {
        var stats = new Stats();
        var clock = T0;
        for (var step = 0; step < 2000; step++)
        {
            clock = clock.AddSeconds(1);
            stats.Skipped += graph.PropagateSkips(clock).Count;
            var ready = graph.ReadyTasks();
            if (ready.Count == 0)
            {
                return stats;
            }

            var task = ready[random.Next(ready.Count)];
            Assert.True(MayStart(graph, task), $"{task.Id} became ready before its edges were satisfied.");
            if (task.Join == JoinPolicy.AnyCompleted && graph.EdgesOf(task.Id).Any(e => graph[e.Prerequisite].Status == WorkTaskStatus.Skipped
                    || (e.Branch is not null && graph.TakenBranch(e.Prerequisite) != e.Branch)))
            {
                stats.AnyJoinsThatMergedADeadEdge++;
            }

            task.TransitionTo(WorkTaskStatus.Running, clock, "start");
            stats.Started++;
            if (task.Kind == WorkTaskKind.Condition)
            {
                var yes = random.Next(2) == 0;
                graph.CompleteCondition(task.Id, yes ? "yes" : "no", clock, "decided");
                if (yes)
                {
                    stats.YesDecisions++;
                }
                else
                {
                    stats.NoDecisions++;
                }
            }
            else if (failures && random.Next(100) < 20)
            {
                task.TransitionTo(WorkTaskStatus.Failed, clock, "boom");
                stats.Failures++;
                if (!task.RetriesExhausted)
                {
                    task.Retry(clock, "again");
                }
            }
            else
            {
                task.Complete([Eid("e")], clock, "done");
            }

            if (failures && random.Next(100) < 4)
            {
                var pending = graph.Tasks.Where(t => t.Status == WorkTaskStatus.Pending).ToList();
                if (pending.Count > 0)
                {
                    pending[random.Next(pending.Count)].TransitionTo(WorkTaskStatus.Cancelled, clock, "user cancelled");
                    stats.Cancellations++;
                }
            }
        }

        throw new InvalidOperationException("The simulation did not terminate.");
    }

    // ---- properties ----

    [Fact]
    public void Without_failures_exactly_the_live_tasks_complete_and_the_rest_are_skipped()
    {
        var random = new Random(20261015);
        var totals = new Stats();
        var graphsWithSkips = 0;
        for (var i = 0; i < Graphs; i++)
        {
            var built = RandomGraph(random, conditions: true, joins: true);
            var graph = built.Graph;
            var stats = Simulate(graph, random, failures: false);

            // The oracle: a task is live when its join policy is satisfied by live prerequisites on the taken branches.
            var live = new Dictionary<TaskId, bool>();
            foreach (var id in graph.TopologicalOrder())
            {
                var edges = graph.EdgesOf(id);
                bool Alive(DependencyEdge e) => live[e.Prerequisite] && (e.Branch is null || graph.TakenBranch(e.Prerequisite) == e.Branch);
                live[id] = edges.Count == 0 || (graph[id].Join == JoinPolicy.AllCompleted ? edges.All(Alive) : edges.Any(Alive));
            }

            foreach (var task in graph.Tasks)
            {
                Assert.Equal(live[task.Id] ? WorkTaskStatus.Completed : WorkTaskStatus.Skipped, task.Status);
            }

            Assert.Empty(graph.ReadyTasks());
            Assert.Empty(graph.PropagateSkips(T0.AddDays(1)));
            var report = graph.Analyze(T0.AddDays(1), Hour);
            Assert.True(report.IsHealthy, $"graph {i}: a finished graph must be healthy.");
            Assert.False(report.IsDeadlocked);

            totals.Started += stats.Started;
            totals.Skipped += stats.Skipped;
            totals.YesDecisions += stats.YesDecisions;
            totals.NoDecisions += stats.NoDecisions;
            totals.AnyJoinsThatMergedADeadEdge += stats.AnyJoinsThatMergedADeadEdge;
            graphsWithSkips += stats.Skipped > 0 ? 1 : 0;
        }

        Assert.True(graphsWithSkips > 20, "The generator rarely skipped anything, so the property would be vacuous.");
        Assert.True(totals.YesDecisions > 20 && totals.NoDecisions > 20, "Both branches of the conditions must be taken.");
        Assert.True(totals.AnyJoinsThatMergedADeadEdge > 5, "No AnyCompleted join ever merged a dead edge.");
    }

    [Fact]
    public void With_failures_no_task_starts_early_and_every_unfinished_task_is_explained_by_Analyze()
    {
        var random = new Random(8675309);
        var graphsLeftUnfinished = 0;
        var graphsFinished = 0;
        var totals = new Stats();
        for (var i = 0; i < Graphs; i++)
        {
            var graph = RandomGraph(random, conditions: true, joins: true).Graph;
            var stats = Simulate(graph, random, failures: true);
            totals.Failures += stats.Failures;
            totals.Cancellations += stats.Cancellations;

            var now = T0.AddDays(1);
            var report = graph.Analyze(now, Hour);
            var unfinished = graph.Tasks.Where(t => !WorkTaskTransitions.IsTerminal(t.Status)).Select(t => t.Id).ToList();

            // Nothing is running or retriable here, so unfinished work is a deadlock, and Analyze must say why.
            Assert.Equal(unfinished.Count > 0, report.IsDeadlocked);
            foreach (var id in unfinished)
            {
                Assert.True(
                    report.RetryExhaustedTasks.Contains(id) || report.UnreachableTasks.Contains(id),
                    $"graph {i}: {id} ({graph[id].Status}) is unfinished but Analyze does not explain it.");
            }

            Assert.All(report.UnreachableTasks, id => Assert.Contains(id, unfinished));
            Assert.Empty(report.StalledTasks);
            graphsLeftUnfinished += unfinished.Count > 0 ? 1 : 0;
            graphsFinished += unfinished.Count == 0 ? 1 : 0;
        }

        Assert.True(graphsLeftUnfinished > 20, "Failures rarely left work unfinished, so the explanation is not exercised.");
        Assert.True(graphsFinished > 20, "Failures always left work unfinished.");
        Assert.True(totals.Failures > 50 && totals.Cancellations > 5);
    }

    [Fact]
    public void Branch_edges_never_create_cycles_and_every_rejection_names_a_real_closed_path()
    {
        var random = new Random(424242);
        var rejected = 0;
        var branchEdges = 0;
        for (var i = 0; i < Graphs; i++)
        {
            var size = random.Next(3, 13);
            var graph = new TaskGraph();
            var ids = Enumerable.Range(0, size).Select(n => $"t{n}").ToArray();
            foreach (var id in ids)
            {
                graph.Add(NewTask(id, random.Next(3) == 0 ? WorkTaskKind.Condition : WorkTaskKind.AgentWork));
            }

            var accepted = new HashSet<(TaskId Dependent, TaskId Prerequisite)>();
            for (var attempt = 0; attempt < size * 4; attempt++)
            {
                var dependent = Tid(ids[random.Next(size)]);
                var prerequisite = Tid(ids[random.Next(size)]);
                string? branch = graph[prerequisite].Kind == WorkTaskKind.Condition ? (random.Next(2) == 0 ? "yes" : "no") : null;
                try
                {
                    graph.AddDependency(dependent, prerequisite, branch);
                    accepted.Add((dependent, prerequisite));
                    branchEdges += branch is null ? 0 : 1;
                }
                catch (DependencyCycleException ex)
                {
                    rejected++;
                    Assert.Equal(ex.Cycle[0], ex.Cycle[^1]);
                    Assert.Equal(dependent, ex.Cycle[0]);
                    Assert.Equal(prerequisite, ex.Cycle[1]);
                    for (var n = 1; n < ex.Cycle.Count - 1; n++)
                    {
                        Assert.Contains((ex.Cycle[n], ex.Cycle[n + 1]), accepted);
                    }
                }
                catch (DomainRuleViolationException ex) when (ex.Rule == "graph.dependency-branch-conflict")
                {
                    // The same pair was drawn twice with different branches: the first edge stays.
                }
            }

            var order = graph.TopologicalOrder();
            Assert.Equal(size, order.Distinct().Count());
            foreach (var (dependent, prerequisite) in accepted)
            {
                Assert.True(order.ToList().IndexOf(prerequisite) < order.ToList().IndexOf(dependent), $"graph {i}: {prerequisite} must precede {dependent}");
            }
        }

        Assert.True(rejected > 0, "The generator never produced a cycle.");
        Assert.True(branchEdges > 100, "The generator rarely produced branch edges.");
    }

    [Fact]
    public void PropagateSkips_reaches_a_fixpoint_and_Analyze_never_changes_the_graph()
    {
        var random = new Random(31337);
        var analyzed = 0;
        var skipsApplied = 0;
        for (var i = 0; i < Graphs; i++)
        {
            var graph = RandomGraph(random, conditions: true, joins: true).Graph;

            // Stop the simulation half-way: decide some conditions and finish some tasks, then compare.
            var steps = random.Next(0, 6);
            for (var s = 0; s < steps; s++)
            {
                graph.PropagateSkips(T0);
                var ready = graph.ReadyTasks();
                if (ready.Count == 0)
                {
                    break;
                }

                var task = ready[random.Next(ready.Count)];
                task.TransitionTo(WorkTaskStatus.Running, T0, "start");
                if (task.Kind == WorkTaskKind.Condition)
                {
                    graph.CompleteCondition(task.Id, random.Next(2) == 0 ? "yes" : "no", T0, "decided");
                }
                else
                {
                    task.Complete([Eid("e")], T0, "done");
                }
            }

            var before = graph.Tasks.Select(t => (t.Id, t.Status, t.History.Count)).ToList();
            var report = graph.Analyze(T0, Hour);
            Assert.Equal(before, graph.Tasks.Select(t => (t.Id, t.Status, t.History.Count)).ToList());
            analyzed++;

            // What Analyze predicted is what PropagateSkips then does: the doomed tasks are not unfinished work, and the
            // report computed before the skips equals the one computed after them.
            var skippedNow = graph.PropagateSkips(T0);
            Assert.Empty(graph.PropagateSkips(T0));
            Assert.All(skippedNow, id => Assert.DoesNotContain(id, report.UnreachableTasks));
            Assert.All(skippedNow, id => Assert.Equal(WorkTaskStatus.Skipped, graph[id].Status));
            var after = graph.Analyze(T0, Hour);
            Assert.Equal(report.UnreachableTasks, after.UnreachableTasks);
            Assert.Equal(report.RetryExhaustedTasks, after.RetryExhaustedTasks);
            Assert.Equal(report.StalledTasks, after.StalledTasks);
            Assert.Equal(report.IsDeadlocked, after.IsDeadlocked);
            skipsApplied += skippedNow.Count;
        }

        Assert.Equal(Graphs, analyzed);
        Assert.True(skipsApplied > 20, "The generator rarely left a skip pending, so the prediction is not exercised.");
    }

    // ---- graphs without conditions behave exactly as before Phase 3 ----

    private static void DriveRandomly(WorkTask task, Random random)
    {
        switch (random.Next(9))
        {
            case 1:
                task.TransitionTo(WorkTaskStatus.Running, T0, "go");
                break;
            case 2:
                task.TransitionTo(WorkTaskStatus.Blocked, T0, "wait");
                break;
            case 3:
                task.TransitionTo(WorkTaskStatus.AwaitingApproval, T0, "ask");
                break;
            case 4:
                task.TransitionTo(WorkTaskStatus.Running, T0, "go");
                task.Complete([Eid("e")], T0, "done");
                break;
            case 5:
                task.TransitionTo(WorkTaskStatus.Running, T0, "go");
                task.TransitionTo(WorkTaskStatus.Failed, T0, "boom");
                break;
            case 6:
                task.TransitionTo(WorkTaskStatus.Running, T0, "go");
                task.TransitionTo(WorkTaskStatus.Failed, T0, "boom");
                while (!task.RetriesExhausted)
                {
                    task.Retry(T0, "again");
                    task.TransitionTo(WorkTaskStatus.Running, T0, "go");
                    task.TransitionTo(WorkTaskStatus.Failed, T0, "boom");
                }

                break;
            case 7:
                task.TransitionTo(WorkTaskStatus.Cancelled, T0, "cancel");
                break;
            case 8:
                task.TransitionTo(WorkTaskStatus.Running, T0, "go");
                task.RecordActivity(T0.AddMinutes(random.Next(0, 30)));
                break;
            default:
                break; // stays Pending
        }
    }

    // The implementations below are the ones TaskGraph had before Phase 3, written against its public surface.
    private static List<TaskId> LegacyReady(TaskGraph graph) => graph.Tasks
        .Where(t => t.Status == WorkTaskStatus.Pending && graph.PrerequisitesOf(t.Id).All(p => graph[p].Status == WorkTaskStatus.Completed))
        .Select(t => t.Id)
        .ToList();

    private static bool LegacyTerminal(WorkTaskStatus status) => status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled;

    private static bool LegacyDependsOnAny(TaskGraph graph, TaskId task, HashSet<TaskId> targets)
    {
        var visited = new HashSet<TaskId>();
        var stack = new Stack<TaskId>(graph.PrerequisitesOf(task));
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (targets.Contains(current))
            {
                return true;
            }

            if (visited.Add(current))
            {
                foreach (var next in graph.PrerequisitesOf(current))
                {
                    stack.Push(next);
                }
            }
        }

        return false;
    }

    private static GraphHealthReport LegacyAnalyze(TaskGraph graph, DateTimeOffset now, TimeSpan stallThreshold)
    {
        var tasks = graph.Tasks.ToList();
        var stalled = tasks.Where(t => t.Status == WorkTaskStatus.Running && now - t.LastActivityAt > stallThreshold).Select(t => t.Id).ToList();
        var deadEnds = tasks
            .Where(t => t.Status == WorkTaskStatus.Cancelled || (t.Status == WorkTaskStatus.Failed && t.RetriesExhausted))
            .Select(t => t.Id)
            .ToHashSet();
        var unreachable = tasks
            .Where(t => !LegacyTerminal(t.Status) && !deadEnds.Contains(t.Id) && LegacyDependsOnAny(graph, t.Id, deadEnds))
            .Select(t => t.Id)
            .ToList();
        var retryExhausted = tasks.Where(t => t.Status == WorkTaskStatus.Failed && t.RetriesExhausted).Select(t => t.Id).ToList();
        var unfinished = tasks.Where(t => !LegacyTerminal(t.Status)).ToList();
        var canProgress = unfinished.Any(t => t.Status is WorkTaskStatus.Running or WorkTaskStatus.AwaitingApproval)
            || LegacyReady(graph).Count > 0
            || unfinished.Any(t => t.Status == WorkTaskStatus.Failed && !t.RetriesExhausted && !unreachable.Contains(t.Id));
        return new GraphHealthReport(stalled, unreachable, retryExhausted, unfinished.Count > 0 && !canProgress);
    }

    [Fact]
    public void Graphs_without_conditions_give_the_same_ready_tasks_and_health_as_before_Phase_3()
    {
        var random = new Random(1789);
        var withUnreachable = 0;
        var deadlocked = 0;
        var withStalled = 0;
        var withReady = 0;
        for (var i = 0; i < 400; i++)
        {
            var graph = RandomGraph(random, conditions: false, joins: false, maxRetries: 2).Graph;
            foreach (var task in graph.Tasks)
            {
                DriveRandomly(task, random);
            }

            var now = T0.AddMinutes(random.Next(0, 40));
            var stall = TimeSpan.FromMinutes(random.Next(1, 20));
            var expected = LegacyAnalyze(graph, now, stall);
            var actual = graph.Analyze(now, stall);

            Assert.Equal(LegacyReady(graph), graph.ReadyTasks().Select(t => t.Id));
            Assert.Equal(expected.StalledTasks, actual.StalledTasks);
            Assert.Equal(expected.UnreachableTasks, actual.UnreachableTasks);
            Assert.Equal(expected.RetryExhaustedTasks, actual.RetryExhaustedTasks);
            Assert.Equal(expected.IsDeadlocked, actual.IsDeadlocked);
            Assert.Empty(graph.PropagateSkips(now)); // without branch edges nothing is ever skipped

            withUnreachable += expected.UnreachableTasks.Count > 0 ? 1 : 0;
            deadlocked += expected.IsDeadlocked ? 1 : 0;
            withStalled += expected.StalledTasks.Count > 0 ? 1 : 0;
            withReady += LegacyReady(graph).Count > 0 ? 1 : 0;
        }

        Assert.True(withUnreachable > 20 && deadlocked > 20 && withStalled > 20 && withReady > 20, "The generator did not reach every health case.");
    }
}
