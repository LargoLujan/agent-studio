using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.Orchestration.PlanFixtures;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration;

/// <summary>
/// Properties of replaying a run from its events, over random plans and random runs (failures, retries, interruptions,
/// approvals, cancellations and conditions included). Seeded <see cref="Random"/> loops; each property also checks that
/// the generator reached the cases that matter.
/// </summary>
public sealed class RunReplayPropertyTests
{
    private const int Runs = 200;

    private sealed class Coverage
    {
        public int Skips { get; set; }

        public int Retries { get; set; }

        public int Blocks { get; set; }

        public int Cancellations { get; set; }

        public int Approvals { get; set; }

        public int YesDecisions { get; set; }

        public int NoDecisions { get; set; }

        public int Unfinished { get; set; }

        public int Finished { get; set; }
    }

    /// <summary>A buildable random plan: edges only go from later tasks to earlier ones, and branch edges leave conditions.</summary>
    private static PlanDefinition RandomPlan(Random random)
    {
        var count = random.Next(2, 11);
        var tasks = new List<TaskSpec>();
        for (var i = 0; i < count; i++)
        {
            var kind = random.Next(100) switch
            {
                < 40 => WorkTaskKind.AgentWork,
                < 60 => WorkTaskKind.Condition,
                < 70 => WorkTaskKind.ToolRun,
                < 80 => WorkTaskKind.HumanApproval,
                < 90 => WorkTaskKind.Integration,
                _ => WorkTaskKind.Review,
            };
            var dependencies = new List<DependencySpec>();
            for (var j = 0; j < i; j++)
            {
                if (random.Next(100) < 35)
                {
                    dependencies.Add(new DependencySpec(tasks[j].Id, tasks[j].Kind == WorkTaskKind.Condition ? ConditionBranches.For(random.Next(2) == 0) : null));
                }
            }

            tasks.Add(Spec($"T{i}", kind) with
            {
                DependsOn = dependencies,
                Join = random.Next(100) < 25 ? JoinPolicy.AnyCompleted : JoinPolicy.AllCompleted,
                MaxRetries = kind is WorkTaskKind.Review or WorkTaskKind.HumanApproval ? 0 : random.Next(0, 3),
            });
        }

        return PlanDefinition.Create("random", tasks, [], PlanSource.User, null);
    }

    /// <summary>Drives a run with random actions until nothing more can happen (or a step budget runs out).</summary>
    private static LiveRun RandomRun(PlanDefinition plan, Random random, Coverage coverage)
    {
        var run = new LiveRun(plan, T0);
        for (var step = 0; step < 300; step++)
        {
            coverage.Skips += run.Propagate().Count;
            var ready = run.Graph.ReadyTasks().Select(t => t.Id).ToHashSet();
            var actions = new List<Action>();
            foreach (var task in run.Graph.Tasks)
            {
                var id = task.Id.Value;
                switch (task.Status)
                {
                    case WorkTaskStatus.Pending when ready.Contains(task.Id):
                        actions.Add(() => run.Start(id));
                        actions.Add(() => run.Start(id));
                        if (task.Kind == WorkTaskKind.HumanApproval)
                        {
                            actions.Add(() => { run.AwaitApproval(id); coverage.Approvals++; });
                        }

                        if (random.Next(100) < 4)
                        {
                            actions.Add(() => { run.Cancel(id); coverage.Cancellations++; });
                        }

                        if (random.Next(100) < 6)
                        {
                            actions.Add(() => { run.Block(id); coverage.Blocks++; });
                        }

                        break;
                    case WorkTaskStatus.Running when task.Kind == WorkTaskKind.Condition:
                        actions.Add(() =>
                        {
                            var yes = random.Next(2) == 0;
                            run.Decide(id, ConditionBranches.For(yes));
                            if (yes)
                            {
                                coverage.YesDecisions++;
                            }
                            else
                            {
                                coverage.NoDecisions++;
                            }
                        });
                        break;
                    case WorkTaskStatus.Running:
                        actions.Add(() => run.Complete(id));
                        actions.Add(() => run.Complete(id));
                        if (random.Next(100) < 25)
                        {
                            actions.Add(() => run.Fail(id));
                        }

                        if (random.Next(100) < 8)
                        {
                            actions.Add(() => { run.Block(id); coverage.Blocks++; });
                        }

                        break;
                    case WorkTaskStatus.AwaitingApproval:
                        actions.Add(() => run.Complete(id));
                        actions.Add(() => run.Fail(id));
                        break;
                    case WorkTaskStatus.Blocked:
                        actions.Add(() => run.Unblock(id));
                        break;
                    case WorkTaskStatus.Failed when !task.RetriesExhausted:
                        actions.Add(() => { run.Retry(id); coverage.Retries++; });
                        break;
                }
            }

            if (actions.Count == 0)
            {
                break;
            }

            actions[random.Next(actions.Count)]();
        }

        var unfinished = run.Graph.Tasks.Any(t => !WorkTaskTransitions.IsTerminal(t.Status));
        coverage.Unfinished += unfinished ? 1 : 0;
        coverage.Finished += unfinished ? 0 : 1;
        return run;
    }

    [Fact]
    public void Replaying_the_log_at_any_operation_boundary_gives_the_live_state_of_that_moment()
    {
        var random = new Random(26101506);
        var coverage = new Coverage();
        var comparisons = 0;
        for (var i = 0; i < Runs; i++)
        {
            var run = RandomRun(RandomPlan(random), random, coverage);

            foreach (var (eventCount, signature) in run.Snapshots)
            {
                var replay = run.Replay(run.Events.Take(eventCount));

                Assert.True(replay.Problems.Count == 0, $"run {i}, after {eventCount} events: {string.Join("; ", replay.Problems)}");
                Assert.Equal(signature, LiveRun.Signature(replay.Graph));
                comparisons++;
            }
        }

        Assert.True(comparisons > 2000, $"Only {comparisons} comparisons were made.");
        Assert.True(coverage.Skips > 20, "Runs rarely skipped anything.");
        Assert.True(coverage.Retries > 20 && coverage.Blocks > 20 && coverage.Cancellations > 5 && coverage.Approvals > 5, "Runs rarely retried, blocked, cancelled or asked for approval.");
        Assert.True(coverage.YesDecisions > 20 && coverage.NoDecisions > 20, "Both branches must be decided.");
        Assert.True(coverage.Unfinished > 20 && coverage.Finished > 20, "Some runs must end finished and some stuck.");
    }

    [Fact]
    public void Removing_the_start_of_a_task_that_continued_afterwards_is_always_detected()
    {
        var random = new Random(26101507);
        var coverage = new Coverage();
        var tried = 0;
        for (var i = 0; i < Runs; i++)
        {
            var run = RandomRun(RandomPlan(random), random, coverage);
            var events = run.Events.ToList();
            for (var index = 0; index < events.Count; index++)
            {
                if (events[index] is not TaskTransitioned { To: WorkTaskStatus.Running, From: WorkTaskStatus.Pending } start)
                {
                    continue;
                }

                var continues = events.Skip(index + 1).OfType<TaskTransitioned>().Any(e => e.Task == start.Task && e.From == WorkTaskStatus.Running);
                if (!continues)
                {
                    continue;
                }

                var corrupted = events.Where((_, k) => k != index).ToList();
                Assert.NotEmpty(run.Replay(corrupted).Problems);
                tried++;
            }
        }

        Assert.True(tried > 200, $"Only {tried} corruptions were tried.");
    }

    [Fact]
    public void Repeating_any_transition_is_always_detected()
    {
        var random = new Random(26101508);
        var coverage = new Coverage();
        var tried = 0;
        for (var i = 0; i < Runs; i++)
        {
            var run = RandomRun(RandomPlan(random), random, coverage);
            var events = run.Events.ToList();
            for (var index = 0; index < events.Count; index++)
            {
                if (events[index] is not TaskTransitioned { To: not WorkTaskStatus.Skipped } transition)
                {
                    continue;
                }

                var corrupted = events.ToList();
                corrupted.Insert(index + 1, transition);
                Assert.NotEmpty(run.Replay(corrupted).Problems);
                tried++;
            }
        }

        Assert.True(tried > 500, $"Only {tried} corruptions were tried.");
    }

    [Fact]
    public void Starting_a_task_before_its_prerequisites_are_satisfied_is_always_detected()
    {
        var random = new Random(26101509);
        var coverage = new Coverage();
        var tried = 0;
        for (var i = 0; i < Runs; i++)
        {
            var run = RandomRun(RandomPlan(random), random, coverage);

            // At the very beginning of the log, start every task that has prerequisites: none of them is ready.
            foreach (var task in run.Graph.Tasks.Where(t => run.Graph.EdgesOf(t.Id).Count > 0))
            {
                var forged = new List<RunEvent> { new TaskTransitioned(T0, task.Id, WorkTaskStatus.Pending, WorkTaskStatus.Running, "forged", []) };
                var problem = Assert.Single(run.Replay(forged).Problems);
                Assert.Contains(task.Id.Value, problem, StringComparison.Ordinal);
                tried++;
            }
        }

        Assert.True(tried > 400, $"Only {tried} corruptions were tried.");
    }
}
