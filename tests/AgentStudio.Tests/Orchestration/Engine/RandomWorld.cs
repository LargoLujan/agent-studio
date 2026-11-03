using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// A random plan that the validator accepts, and a deterministic script for everything that happens in it (agent
/// outcomes per attempt, verification, tools, conflicts, verdicts, approvals). Everything is decided up front from the
/// seed, keyed by task and attempt, so the same world behaves the same however the work interleaves.
/// </summary>
internal sealed class RandomWorld
{
    internal enum AgentOutcome
    {
        Complete,
        Fail,
        OutOfScope,
        Blocked,
        LaunchFailure,
    }

    private readonly Dictionary<(string Task, int Attempt), AgentOutcome> _agents = [];
    private readonly Dictionary<(string Task, int Attempt), string> _flags = [];
    private readonly HashSet<(string Task, int Attempt)> _verificationFails = [];
    private readonly HashSet<(string Task, int Attempt)> _toolFails = [];
    private readonly HashSet<(string Task, int Attempt)> _conflicts = [];
    private readonly Dictionary<string, ReviewDisposition> _reviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _approvals = new(StringComparer.Ordinal);

    private RandomWorld(PlanDefinition plan, int claudeLimit, int codexLimit, RunOptions options)
    {
        Plan = plan;
        ClaudeLimit = claudeLimit;
        CodexLimit = codexLimit;
        Options = options;
    }

    public PlanDefinition Plan { get; }

    public int ClaudeLimit { get; }

    public int CodexLimit { get; }

    public RunOptions Options { get; }

    public IReadOnlyDictionary<TaskId, TaskSpec> Specs => Plan.Tasks.ToDictionary(t => t.Id);

    /// <summary>The shape of the plan, for failure messages: kind, workspace, join and dependencies (with branches) of each task.</summary>
    public string Describe() => string.Join(
        "\n",
        Plan.Tasks.Select(t => $"  {t.Id} {t.Kind} {t.Workspace} {t.Join} retries={t.MaxRetries} agent={t.Agent?.Value ?? "-"} cond={t.Condition?.ToString() ?? "-"} deps=[{string.Join(", ", t.DependsOn.Select(d => d.Branch is null ? d.Prerequisite.Value : $"{d.Prerequisite}:{d.Branch}"))}]"))
        + $"\n  limits claude={ClaudeLimit} codex={CodexLimit} parallel={Options.MaxParallelTasks} agentRuns={Options.MaxAgentRuns}";

    public static RandomWorld Generate(Random random)
    {
        var count = random.Next(2, 9);
        var kinds = new WorkTaskKind[count];
        var workspaces = new WorkspaceMode[count];
        var prerequisites = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            kinds[i] = i == 0 ? WorkTaskKind.AgentWork : random.Next(100) switch
            {
                < 40 => WorkTaskKind.AgentWork,
                < 60 => WorkTaskKind.Condition,
                < 68 => WorkTaskKind.ToolRun,
                < 76 => WorkTaskKind.HumanApproval,
                < 86 => WorkTaskKind.Integration,
                _ => WorkTaskKind.Review,
            };
            workspaces[i] = random.Next(100) switch
            {
                < 60 => WorkspaceMode.Write,
                < 80 => WorkspaceMode.None,
                _ => WorkspaceMode.ReadOnly,
            };
            prerequisites[i] = [];
            for (var j = 0; j < i; j++)
            {
                if (random.Next(100) < (kinds[j] == WorkTaskKind.Condition ? 65 : 40))
                {
                    prerequisites[i].Add(j);
                }
            }

            if (i > 0 && prerequisites[i].Count == 0 && random.Next(100) < 70)
            {
                prerequisites[i].Add(random.Next(i));
            }

            if (kinds[i] == WorkTaskKind.Condition && prerequisites[i].Count == 0)
            {
                kinds[i] = WorkTaskKind.AgentWork; // a condition reads one of its ancestors
            }
        }

        // Fan-in (docs/13 section 4.6): a task that builds on the code of two or more producers becomes their integration.
        for (var i = 0; i < count; i++)
        {
            if (NeedsCodeBase(kinds[i], workspaces[i]) && Frontier(i, kinds, workspaces, prerequisites).Count >= 2)
            {
                kinds[i] = WorkTaskKind.Integration;
            }
        }

        // The integrations of a plan form a chain (they share the run's branch): each one waits for the one before it.
        var previous = -1;
        for (var i = 0; i < count; i++)
        {
            if (kinds[i] != WorkTaskKind.Integration)
            {
                continue;
            }

            if (previous >= 0 && !Ancestors(i, prerequisites).Contains(previous))
            {
                prerequisites[i].Add(previous);
            }

            previous = i;
        }

        var hasReview = kinds.Contains(WorkTaskKind.Review);
        string[] executors = hasReview ? ["ejecutor", "ejecutor2"] : ["ejecutor", "ejecutor2", "ejecutorCodex"];
        var specs = new List<TaskSpec>();
        for (var i = 0; i < count; i++)
        {
            var id = $"T{i}";
            var dependencies = prerequisites[i]
                .Select(j => new DependencySpec(new TaskId($"T{j}"), kinds[j] == WorkTaskKind.Condition ? ConditionBranches.For(random.Next(2) == 0) : null))
                .ToList();
            var join = dependencies.Count >= 2 && random.Next(100) < 25 ? JoinPolicy.AnyCompleted : JoinPolicy.AllCompleted;
            var spec = kinds[i] == WorkTaskKind.AgentWork
                ? Work(id, executors[random.Next(executors.Length)], workspaces[i]) with
                {
                    MaxRetries = random.Next(0, 3),
                    VerificationSteps = workspaces[i] == WorkspaceMode.Write && random.Next(100) < 50 ? ["build", "test"] : [],
                }
                : Node(id, kinds[i]) with
                {
                    Covers = [],
                    MaxRetries = kinds[i] is WorkTaskKind.Review or WorkTaskKind.HumanApproval ? 0 : random.Next(0, 3),
                };
            if (kinds[i] == WorkTaskKind.Condition)
            {
                var ancestors = Ancestors(i, prerequisites).ToList();
                var workers = ancestors.Where(a => kinds[a] == WorkTaskKind.AgentWork).ToList();
                ConditionPredicate predicate = workers.Count > 0 && random.Next(100) < 75
                    ? new OutputEquals(new TaskId($"T{workers[random.Next(workers.Count)]}"), "flag", "1")
                    : new TaskSucceeded(new TaskId($"T{ancestors[random.Next(ancestors.Count)]}"));
                spec = spec with { Condition = predicate };
            }

            specs.Add(spec with { DependsOn = dependencies, Join = join });
        }

        var plan = PlanDefinition.Create("Plan aleatorio", specs, [], PlanSource.OrchestratorAgent, new AgentId("orquestador"));
        var violations = PlanValidator.Validate(plan, Context());
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("The generator made an invalid plan: " + string.Join("; ", violations.Select(v => $"[{v.Code}] {v.Message}")));
        }

        var world = new RandomWorld(
            plan,
            random.Next(1, 4),
            random.Next(1, 4),
            new RunOptions(MaxParallelTasks: random.Next(1, 5), MaxAgentRuns: random.Next(4, 14)));
        world.Script(random);
        return world;
    }

    public void Wire(EngineHarness harness)
    {
        harness.Claude.Fallback = AgentScriptFor;
        harness.Codex.Fallback = AgentScriptFor;
        harness.Verification.Script = (scope, step) => step.Id switch
        {
            "lint" => _toolFails.Contains((scope.Task!.Value.Value, scope.Attempt!.Value)) ? StepResult.Fail : null,
            "test" => _verificationFails.Contains((scope.Task!.Value.Value, scope.Attempt!.Value)) ? StepResult.Fail : null,
            _ => null,
        };
        harness.Integrator.ForceConflict = request => _conflicts.Contains((request.Task.Value, request.Attempt)) ? ["src/conflicto.cs"] : null;
        harness.Review.Script = request => _reviews.GetValueOrDefault(request.ReviewTask.Value, ReviewDisposition.Approved);
        harness.Approvals.Approve = request => _approvals.GetValueOrDefault(request.TaskId!, true);
    }

    private AgentScript AgentScriptFor(AgentRunRequest request, int attempt)
    {
        var task = request.TaskId!;
        var spec = Specs[new TaskId(task)];
        var flag = _flags.GetValueOrDefault((task, attempt), "1");
        var report = AgentScript.Report(outputs: [("flag", flag)]);
        return _agents.GetValueOrDefault((task, attempt), AgentOutcome.Complete) switch
        {
            AgentOutcome.Fail => AgentScript.Failing(),
            AgentOutcome.OutOfScope => new AgentScript { Files = new Dictionary<string, string> { [$"fuera/{task}.txt"] = "fuera" }, StructuredOutput = report },
            AgentOutcome.Blocked => new AgentScript { StructuredOutput = AgentScript.Report(status: "blocked", openIssues: ["duda"]) },
            AgentOutcome.LaunchFailure => new AgentScript { ThrowOnStart = new InvalidOperationException("CLI ausente") },
            _ => new AgentScript
            {
                Files = spec.Workspace == WorkspaceMode.Write
                    ? new Dictionary<string, string> { [$"src/{task}/intento{attempt}.txt"] = flag }
                    : new Dictionary<string, string>(),
                StructuredOutput = report,
            },
        };
    }

    private void Script(Random random)
    {
        foreach (var spec in Plan.Tasks)
        {
            var task = spec.Id.Value;
            for (var attempt = 1; attempt <= spec.MaxRetries + 3; attempt++)
            {
                _flags[(task, attempt)] = random.Next(2) == 0 ? "1" : "0";
                switch (spec.Kind)
                {
                    case WorkTaskKind.AgentWork:
                        var roll = random.Next(100);
                        _agents[(task, attempt)] = attempt == 1
                            ? roll switch
                            {
                                < 62 => AgentOutcome.Complete,
                                < 80 => AgentOutcome.Fail,
                                < 85 when spec.Workspace != WorkspaceMode.None => AgentOutcome.OutOfScope,
                                < 90 => AgentOutcome.Blocked,
                                < 95 => AgentOutcome.LaunchFailure,
                                _ => AgentOutcome.Complete,
                            }
                            : roll switch
                            {
                                < 72 => AgentOutcome.Complete,
                                < 94 => AgentOutcome.Fail,
                                _ when spec.Workspace != WorkspaceMode.None => AgentOutcome.OutOfScope,
                                _ => AgentOutcome.Complete,
                            };
                        if (spec.VerificationSteps.Count > 0 && random.Next(100) < 15)
                        {
                            _verificationFails.Add((task, attempt));
                        }

                        break;
                    case WorkTaskKind.ToolRun when random.Next(100) < 20:
                        _toolFails.Add((task, attempt));
                        break;
                    case WorkTaskKind.Integration when random.Next(100) < 10:
                        _conflicts.Add((task, attempt));
                        break;
                }
            }

            if (spec.Kind == WorkTaskKind.Review)
            {
                _reviews[task] = random.Next(100) switch
                {
                    < 70 => ReviewDisposition.Approved,
                    < 80 => ReviewDisposition.ChangesRequested,
                    < 85 => ReviewDisposition.Rejected,
                    _ => ReviewDisposition.NeedsHuman,
                };
            }

            _approvals[task] = random.Next(100) < 80;
        }
    }

    private static bool ProducesCode(WorkTaskKind kind, WorkspaceMode workspace) =>
        kind == WorkTaskKind.Integration || (kind == WorkTaskKind.AgentWork && workspace == WorkspaceMode.Write);

    private static bool NeedsCodeBase(WorkTaskKind kind, WorkspaceMode workspace) =>
        kind is WorkTaskKind.Review or WorkTaskKind.ToolRun || (kind == WorkTaskKind.AgentWork && workspace != WorkspaceMode.None);

    private static IEnumerable<int> Ancestors(int task, List<int>[] prerequisites)
    {
        var found = new HashSet<int>();
        var stack = new Stack<int>(prerequisites[task]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (found.Add(current))
            {
                foreach (var next in prerequisites[current])
                {
                    stack.Push(next);
                }
            }
        }

        return found.Order();
    }

    /// <summary>The maximal producers of code among the ancestors, looking through the tasks that produce none (as the validator does).</summary>
    private static IReadOnlyList<int> Frontier(int task, WorkTaskKind[] kinds, WorkspaceMode[] workspaces, List<int>[] prerequisites)
    {
        var frontier = new HashSet<int>();
        var visited = new HashSet<int>();
        var stack = new Stack<int>(prerequisites[task]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (ProducesCode(kinds[current], workspaces[current]))
            {
                frontier.Add(current);
            }
            else
            {
                foreach (var next in prerequisites[current])
                {
                    stack.Push(next);
                }
            }
        }

        return [.. frontier.Where(x => !frontier.Any(y => y != x && Ancestors(y, prerequisites).Contains(x)))];
    }
}
