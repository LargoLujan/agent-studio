using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.Orchestration.PlanFixtures;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration;

public sealed class PlanDefinitionTests
{
    private static string HashOf(IReadOnlyList<TaskSpec> tasks) => Plan(tasks).ContentHash;

    // ---- Create ----

    [Fact]
    public void Create_keeps_the_content_and_orders_the_tasks_by_id()
    {
        var plan = Plan(ValidTasks().Reverse().ToList());

        Assert.Equal("Add Calc.Multiply with tests", plan.Summary);
        Assert.Equal(["The build may be slow"], plan.Risks);
        Assert.Equal(PlanSource.OrchestratorAgent, plan.Source);
        Assert.Equal(Aid("orquestador"), plan.Author);
        Assert.Equal(["T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8"], plan.Tasks.Select(t => t.Id.Value));
    }

    [Fact]
    public void Create_orders_the_inner_sets_of_each_task()
    {
        var tasks = Replace("T3", t => t with { DependsOn = [Dep("T2"), Dep("T1")] });
        tasks = [.. tasks.Select(t => t.Id == Tid("T1") ? t with { WriteScope = ["tests/Calc.Tests/", "src/Calc/"], Covers = [Rid("R2"), Rid("R1")] } : t)];

        var t1 = Plan(tasks).Tasks.Single(t => t.Id == Tid("T1"));
        var t3 = Plan(tasks).Tasks.Single(t => t.Id == Tid("T3"));

        Assert.Equal(["src/Calc/", "tests/Calc.Tests/"], t1.WriteScope);
        Assert.Equal([Rid("R1"), Rid("R2")], t1.Covers);
        Assert.Equal([Tid("T1"), Tid("T2")], t3.DependsOn.Select(d => d.Prerequisite));
    }

    [Fact]
    public void Create_keeps_the_order_of_the_verification_steps_because_they_run_in_that_order()
    {
        var plan = Plan(Replace("T1", t => t with { VerificationSteps = ["test", "build"] }));

        Assert.Equal(["test", "build"], plan.Tasks[0].VerificationSteps);
    }

    [Fact]
    public void Create_rejects_null_arguments_and_null_elements()
    {
        Assert.Throws<ArgumentNullException>(() => PlanDefinition.Create(null!, [], [], PlanSource.User, null));
        Assert.Throws<ArgumentNullException>(() => PlanDefinition.Create("s", null!, [], PlanSource.User, null));
        Assert.Throws<ArgumentNullException>(() => PlanDefinition.Create("s", [], null!, PlanSource.User, null));
        Assert.ThrowsAny<ArgumentException>(() => PlanDefinition.Create("s", [null!], [], PlanSource.User, null));
        Assert.ThrowsAny<ArgumentException>(() => PlanDefinition.Create("s", [], [null!], PlanSource.User, null));
        Assert.ThrowsAny<ArgumentException>(() => PlanDefinition.Create("s", [Spec("a") with { DependsOn = null! }], [], PlanSource.User, null));
    }

    [Fact]
    public void Create_accepts_an_invalid_plan_so_that_the_validator_can_explain_it()
    {
        var plan = PlanDefinition.Create(string.Empty, [Spec("1 bad id"), Spec("1 bad id")], [], PlanSource.User, null);

        Assert.Equal(2, plan.Tasks.Count);
        Assert.Matches("^[0-9a-f]{64}$", plan.ContentHash);
    }

    [Fact]
    public void Create_does_not_alias_the_lists_it_receives()
    {
        var tasks = ValidTasks().ToList();
        var plan = Plan(tasks);
        var before = plan.ContentHash;

        tasks.Clear();

        Assert.Equal(8, plan.Tasks.Count);
        Assert.Equal(before, plan.ContentHash);
    }

    // ---- ContentHash ----

    [Fact]
    public void The_hash_is_64_lowercase_hex_and_stable()
    {
        var hash = Plan().ContentHash;

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, Plan().ContentHash);
    }

    [Fact]
    public void The_hash_does_not_depend_on_the_order_of_the_tasks()
    {
        Assert.Equal(HashOf(ValidTasks()), HashOf(ValidTasks().Reverse().ToList()));
    }

    [Fact]
    public void The_hash_does_not_depend_on_the_order_of_dependencies_covers_scopes_or_risks()
    {
        var shuffled = ValidTasks()
            .Select(t => t with
            {
                DependsOn = [.. t.DependsOn.Reverse()],
                Covers = [.. t.Covers.Reverse()],
                WriteScope = [.. t.WriteScope.Reverse()],
            })
            .ToList();
        var twoRisks = PlanDefinition.Create("s", ValidTasks(), ["a", "b"], PlanSource.User, null);
        var swapped = PlanDefinition.Create("s", ValidTasks(), ["b", "a"], PlanSource.User, null);

        Assert.Equal(HashOf(ValidTasks()), HashOf(shuffled));
        Assert.Equal(twoRisks.ContentHash, swapped.ContentHash);
    }

    [Fact]
    public void The_hash_is_about_the_content_not_about_who_wrote_it()
    {
        var byAgent = Plan(source: PlanSource.OrchestratorAgent, author: "orquestador");
        var byUser = Plan(source: PlanSource.User, author: null);

        Assert.Equal(byAgent.ContentHash, byUser.ContentHash);
    }

    [Fact]
    public void Random_input_orders_always_give_the_same_hash()
    {
        var random = new Random(26101502);
        var expected = HashOf(ValidTasks());
        for (var i = 0; i < 100; i++)
        {
            var shuffled = ValidTasks()
                .OrderBy(_ => random.Next())
                .Select(t => t with
                {
                    DependsOn = [.. t.DependsOn.OrderBy(_ => random.Next())],
                    Covers = [.. t.Covers.OrderBy(_ => random.Next())],
                    WriteScope = [.. t.WriteScope.OrderBy(_ => random.Next())],
                })
                .ToList();
            Assert.Equal(expected, HashOf(shuffled));
        }
    }

    [Fact]
    public void Creating_a_plan_from_the_content_of_another_gives_the_same_hash()
    {
        var plan = Plan(ValidTasks().Reverse().ToList());

        var again = PlanDefinition.Create(plan.Summary, plan.Tasks, plan.Risks, plan.Source, plan.Author);

        Assert.Equal(plan.ContentHash, again.ContentHash);
        Assert.Equal(plan.Tasks.Select(t => t.Id), again.Tasks.Select(t => t.Id));
    }

    public static TheoryData<string> Mutations() =>
    [
        "title", "kind", "agent", "agent-removed", "objective", "instructions", "dependency-added", "dependency-removed", "dependency-target",
        "dependency-branch", "join", "covers", "write-scope", "verification-step", "verification-order", "condition-type",
        "condition-task", "condition-removed", "tool", "retries", "timeout", "workspace", "task-id", "task-removed", "task-added",
        "summary", "risk",
    ];

    [Theory]
    [MemberData(nameof(Mutations))]
    public void The_hash_changes_when_any_field_changes(string mutation)
    {
        var baseline = ValidTasks().ToList();
        var original = HashOf(baseline);
        IReadOnlyList<TaskSpec> changed = mutation switch
        {
            "title" => Replace("T1", t => t with { Title = "Other title" }),
            "kind" => Replace("T1", t => t with { Kind = WorkTaskKind.ToolRun }),
            "agent" => Replace("T1", t => t with { Agent = Aid("ejecutor2") }),
            "agent-removed" => Replace("T1", t => t with { Agent = null }),
            "objective" => Replace("T1", t => t with { Objective = "Other objective" }),
            "instructions" => Replace("T1", t => t with { Instructions = "Also do this" }),
            "dependency-added" => Replace("T2", t => t with { DependsOn = [Dep("T1")] }),
            "dependency-removed" => Replace("T3", t => t with { DependsOn = [Dep("T1")] }),
            "dependency-target" => Replace("T3", t => t with { DependsOn = [Dep("T1"), Dep("T6")] }),
            "dependency-branch" => Replace("T5", t => t with { DependsOn = [Dep("T4", "no")] }),
            "join" => Replace("T7", t => t with { Join = JoinPolicy.AllCompleted }),
            "covers" => Replace("T1", t => t with { Covers = [Rid("R1"), Rid("R2")] }),
            "write-scope" => Replace("T1", t => t with { WriteScope = ["src/Calc/"] }),
            "verification-step" => Replace("T1", t => t with { VerificationSteps = ["build"] }),
            "verification-order" => Replace("T1", t => t with { VerificationSteps = ["test", "build"] }),
            "condition-type" => Replace("T4", t => t with { Condition = new TaskSucceeded(Tid("T3")) }),
            "condition-task" => Replace("T4", t => t with { Condition = new TestsPassed(Tid("T1")) }),
            "condition-removed" => Replace("T4", t => t with { Condition = null }),
            "tool" => Replace("T8", t => t with { ToolId = "format" }),
            "retries" => Replace("T1", t => t with { MaxRetries = 2 }),
            "timeout" => Replace("T1", t => t with { Timeout = TimeSpan.FromMinutes(5) }),
            "workspace" => Replace("T1", t => t with { Workspace = WorkspaceMode.ReadOnly }),
            "task-id" => Replace("T8", t => t with { Id = Tid("T9") }),
            "task-removed" => [.. baseline.Take(7)],
            "task-added" => [.. baseline, Spec("T9")],
            _ => baseline,
        };
        var plan = mutation switch
        {
            "summary" => PlanDefinition.Create("Another summary", baseline, ["The build may be slow"], PlanSource.OrchestratorAgent, Aid("orquestador")),
            "risk" => PlanDefinition.Create("Add Calc.Multiply with tests", baseline, ["The build may be slow", "New risk"], PlanSource.OrchestratorAgent, Aid("orquestador")),
            _ => Plan(changed),
        };

        Assert.NotEqual(original, plan.ContentHash);
    }

    [Fact]
    public void Different_predicates_over_the_same_text_do_not_collide()
    {
        var a = Plan(Replace("T4", t => t with { Condition = new OutputEquals(Tid("T3"), "ab", "c") }));
        var b = Plan(Replace("T4", t => t with { Condition = new OutputEquals(Tid("T3"), "a", "bc") }));
        var c = Plan(Replace("T4", t => t with { Condition = new PathsChanged(Tid("T3"), "ab") }));

        Assert.NotEqual(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
    }

    [Fact]
    public void Text_moved_between_adjacent_fields_does_not_collide()
    {
        var a = Plan(Replace("T1", t => t with { Title = "ab", Objective = "c" }));
        var b = Plan(Replace("T1", t => t with { Title = "a", Objective = "bc" }));

        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    // ---- BuildGraph ----

    [Fact]
    public void BuildGraph_creates_one_task_per_spec_with_its_kind_agent_retries_and_join()
    {
        var graph = Plan().BuildGraph(T0.AddHours(1));

        Assert.Equal(["T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8"], graph.Tasks.Select(t => t.Id.Value));
        var review = graph[Tid("T7")];
        Assert.Equal(WorkTaskKind.Review, review.Kind);
        Assert.Equal(Aid("juez"), review.AssignedTo);
        Assert.Equal(0, review.MaxRetries);
        Assert.Equal(JoinPolicy.AnyCompleted, review.Join);
        Assert.Equal(JoinPolicy.AllCompleted, graph[Tid("T1")].Join);
        Assert.Equal(1, graph[Tid("T1")].MaxRetries);
        Assert.All(graph.Tasks, t => Assert.Equal(WorkTaskStatus.Pending, t.Status));
        Assert.All(graph.Tasks, t => Assert.Equal(T0.AddHours(1), t.CreatedAt));
    }

    [Fact]
    public void BuildGraph_creates_the_dependencies_with_their_branches()
    {
        var graph = Plan().BuildGraph(T0);

        Assert.Equal([new DependencyEdge(Tid("T4"), "yes")], graph.EdgesOf(Tid("T5")));
        Assert.Equal([new DependencyEdge(Tid("T4"), "no")], graph.EdgesOf(Tid("T6")));
        Assert.Equal([Tid("T1"), Tid("T2")], graph.EdgesOf(Tid("T3")).Select(e => e.Prerequisite));
        Assert.Equal(["T1", "T2"], graph.ReadyTasks().Select(t => t.Id.Value));
    }

    [Fact]
    public void BuildGraph_gives_the_same_graph_whatever_the_input_order()
    {
        var forward = Plan().BuildGraph(T0);
        var backward = Plan(ValidTasks().Reverse().ToList()).BuildGraph(T0);

        Assert.Equal(forward.TopologicalOrder(), backward.TopologicalOrder());
        Assert.Equal(forward.Tasks.Select(t => t.Id), backward.Tasks.Select(t => t.Id));
    }

    [Fact]
    public void BuildGraph_throws_a_cycle_exception_with_its_path()
    {
        var plan = Plan(Replace("T1", t => t with { DependsOn = [Dep("T3")] }));

        var ex = Assert.Throws<DependencyCycleException>(() => plan.BuildGraph(T0));

        Assert.Equal(ex.Cycle[0], ex.Cycle[^1]);
        Assert.True(ex.Cycle.Count >= 3);
    }

    [Fact]
    public void BuildGraph_reports_an_unknown_dependency_as_a_rule_violation()
    {
        var plan = Plan(Replace("T8", t => t with { DependsOn = [Dep("ghost")] }));

        var violation = Violation(() => plan.BuildGraph(T0));

        Assert.Equal("plan.unknown-dependency", violation.Rule);
        Assert.Contains("ghost", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGraph_refuses_duplicate_ids()
    {
        var plan = Plan([Spec("T1"), Spec("T1")]);

        Assert.Equal("graph.duplicate-task", Violation(() => plan.BuildGraph(T0)).Rule);
    }

    [Fact]
    public void BuildGraph_builds_a_fresh_graph_each_time()
    {
        var plan = Plan();
        var first = plan.BuildGraph(T0);
        first[Tid("T1")].TransitionTo(WorkTaskStatus.Running, T0, "go");

        Assert.Equal(WorkTaskStatus.Pending, plan.BuildGraph(T0)[Tid("T1")].Status);
    }
}
