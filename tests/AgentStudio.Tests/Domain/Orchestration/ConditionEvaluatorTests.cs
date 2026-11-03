using System.Reflection;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration;

public sealed class ConditionEvaluatorTests
{
    private static TaskFacts Facts(
        string task,
        WorkTaskStatus status = WorkTaskStatus.Completed,
        TestRunSummary? tests = null,
        IEnumerable<(string Key, string Value)>? outputs = null,
        IEnumerable<string>? paths = null) =>
        new(Tid(task), status, tests, (outputs ?? []).ToDictionary(o => o.Key, o => o.Value), [.. paths ?? []]);

    private static IReadOnlyDictionary<TaskId, TaskFacts> World(params TaskFacts[] facts) => facts.ToDictionary(f => f.Task);

    // ---- TaskSucceeded ----

    [Theory]
    [InlineData(WorkTaskStatus.Completed, true)]
    [InlineData(WorkTaskStatus.Pending, false)]
    [InlineData(WorkTaskStatus.Running, false)]
    [InlineData(WorkTaskStatus.Blocked, false)]
    [InlineData(WorkTaskStatus.AwaitingApproval, false)]
    [InlineData(WorkTaskStatus.Failed, false)]
    [InlineData(WorkTaskStatus.Cancelled, false)]
    [InlineData(WorkTaskStatus.Skipped, false)]
    public void TaskSucceeded_is_true_only_for_a_completed_task(WorkTaskStatus status, bool expected)
    {
        var result = ConditionEvaluator.Evaluate(new TaskSucceeded(Tid("a")), World(Facts("a", status)));

        Assert.Equal(expected, result);
    }

    // ---- TestsPassed ----

    [Fact]
    public void TestsPassed_follows_the_latest_system_captured_test_run()
    {
        Assert.True(ConditionEvaluator.Evaluate(new TestsPassed(Tid("a")), World(Facts("a", tests: Green))));
        Assert.False(ConditionEvaluator.Evaluate(new TestsPassed(Tid("a")), World(Facts("a", tests: Red))));
    }

    [Fact]
    public void TestsPassed_is_false_without_a_test_run_and_when_no_test_ran()
    {
        Assert.False(ConditionEvaluator.Evaluate(new TestsPassed(Tid("a")), World(Facts("a"))));
        Assert.False(ConditionEvaluator.Evaluate(new TestsPassed(Tid("a")), World(Facts("a", tests: new TestRunSummary(0, 0, 0, 0)))));
        Assert.False(ConditionEvaluator.Evaluate(new TestsPassed(Tid("a")), World(Facts("a", tests: new TestRunSummary(5, 0, 0, 1)))));
    }

    // ---- OutputEquals ----

    [Theory]
    [InlineData("status", "ok", true)]
    [InlineData("status", "OK", false)]
    [InlineData("status", "ok ", false)]
    [InlineData("Status", "ok", false)]
    [InlineData("other", "ok", false)]
    [InlineData("status", "", false)]
    public void OutputEquals_compares_the_key_and_the_value_exactly(string key, string value, bool expected)
    {
        var world = World(Facts("a", outputs: [("status", "ok")]));

        Assert.Equal(expected, ConditionEvaluator.Evaluate(new OutputEquals(Tid("a"), key, value), world));
    }

    [Fact]
    public void OutputEquals_matches_an_empty_value_when_the_task_reported_one()
    {
        var world = World(Facts("a", outputs: [("note", string.Empty)]));

        Assert.True(ConditionEvaluator.Evaluate(new OutputEquals(Tid("a"), "note", string.Empty), world));
    }

    // ---- PathsChanged ----

    [Theory]
    [InlineData("src/Calc/", true)]
    [InlineData("src/", true)]
    [InlineData("SRC/calc/", true)]
    [InlineData("src\\Calc\\", true)]
    [InlineData("src/Calc/Multiply.cs", true)]
    [InlineData("tests/", true)]
    [InlineData("docs/", false)]
    [InlineData("src/Other/", false)]
    [InlineData("", true)]
    public void PathsChanged_is_true_when_any_changed_path_starts_with_the_prefix(string prefix, bool expected)
    {
        var world = World(Facts("a", paths: ["src/Calc/Multiply.cs", "tests/Calc.Tests/MultiplyTests.cs"]));

        Assert.Equal(expected, ConditionEvaluator.Evaluate(new PathsChanged(Tid("a"), prefix), world));
    }

    [Fact]
    public void PathsChanged_is_false_when_nothing_changed()
    {
        Assert.False(ConditionEvaluator.Evaluate(new PathsChanged(Tid("a"), "src/"), World(Facts("a"))));
        Assert.False(ConditionEvaluator.Evaluate(new PathsChanged(Tid("a"), string.Empty), World(Facts("a"))));
    }

    [Fact]
    public void PathsChanged_reads_changed_paths_written_with_backslashes()
    {
        var world = World(Facts("a", paths: ["src\\Calc\\Multiply.cs"]));

        Assert.True(ConditionEvaluator.Evaluate(new PathsChanged(Tid("a"), "src/Calc/"), world));
    }

    // ---- general ----

    [Fact]
    public void Facts_about_other_tasks_are_ignored()
    {
        var world = World(Facts("a", WorkTaskStatus.Failed), Facts("b"));

        Assert.True(ConditionEvaluator.Evaluate(new TaskSucceeded(Tid("b")), world));
        Assert.False(ConditionEvaluator.Evaluate(new TaskSucceeded(Tid("a")), world));
    }

    [Fact]
    public void A_predicate_about_a_task_without_facts_is_a_programming_error_not_a_quiet_no()
    {
        var world = World(Facts("a"));

        foreach (ConditionPredicate predicate in new ConditionPredicate[]
                 {
                     new TaskSucceeded(Tid("ghost")),
                     new TestsPassed(Tid("ghost")),
                     new OutputEquals(Tid("ghost"), "k", "v"),
                     new PathsChanged(Tid("ghost"), "src/"),
                 })
        {
            Assert.Equal("condition.facts-missing", Violation(() => ConditionEvaluator.Evaluate(predicate, world)).Rule);
        }
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ConditionEvaluator.Evaluate(null!, World()));
        Assert.Throws<ArgumentNullException>(() => ConditionEvaluator.Evaluate(new TaskSucceeded(Tid("a")), null!));
    }

    [Fact]
    public void Each_predicate_names_the_task_it_reads()
    {
        Assert.Equal(Tid("t1"), new TaskSucceeded(Tid("t1")).ReferencedTask);
        Assert.Equal(Tid("t2"), new TestsPassed(Tid("t2")).ReferencedTask);
        Assert.Equal(Tid("t3"), new OutputEquals(Tid("t3"), "k", "v").ReferencedTask);
        Assert.Equal(Tid("t4"), new PathsChanged(Tid("t4"), "src/").ReferencedTask);
    }

    [Fact]
    public void Predicates_compare_by_value()
    {
        Assert.Equal(new OutputEquals(Tid("a"), "k", "v"), new OutputEquals(Tid("a"), "k", "v"));
        Assert.NotEqual<ConditionPredicate>(new TaskSucceeded(Tid("a")), new TestsPassed(Tid("a")));
    }

    [Fact]
    public void The_set_of_predicates_is_closed_and_every_member_is_known_to_these_tests()
    {
        // Adding a predicate must make this test fail until the evaluator, the hash, the validator and these tests know it.
        var concrete = typeof(ConditionPredicate).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ConditionPredicate)))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["OutputEquals", "PathsChanged", "TaskSucceeded", "TestsPassed"], concrete);

        // The only way to create a new kind of predicate is the parameterless constructor, and it is private protected.
        Assert.Empty(typeof(ConditionPredicate).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        var parameterless = typeof(ConditionPredicate)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 0);
        Assert.True(parameterless.IsFamilyAndAssembly, "Only code in the Domain assembly may derive from ConditionPredicate.");
    }

    [Fact]
    public void The_branch_names_are_yes_and_no()
    {
        Assert.Equal("yes", ConditionBranches.Yes);
        Assert.Equal("no", ConditionBranches.No);
        Assert.Equal(["yes", "no"], ConditionBranches.All);
        Assert.Equal(ConditionBranches.Yes, ConditionBranches.For(true));
        Assert.Equal(ConditionBranches.No, ConditionBranches.For(false));
    }
}
