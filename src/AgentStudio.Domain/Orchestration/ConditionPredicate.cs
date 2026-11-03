using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Work;

namespace AgentStudio.Domain.Orchestration;

/// <summary>The two outcomes of a condition node; they are the branch names the edges leaving it wait for.</summary>
public static class ConditionBranches
{
    public const string Yes = "yes";
    public const string No = "no";

    public static IReadOnlyList<string> All { get; } = [Yes, No];

    public static string For(bool outcome) => outcome ? Yes : No;
}

/// <summary>
/// What a condition node asks. The set is closed on purpose: the system evaluates predicates over facts it captured
/// itself, so a plan can never smuggle in an expression, a command or a script. Only code in this assembly can add one.
/// </summary>
public abstract record ConditionPredicate
{
    private protected ConditionPredicate()
    {
    }

    /// <summary>The task whose facts the predicate reads; it must be an ancestor of the condition.</summary>
    public TaskId ReferencedTask => this switch
    {
        TaskSucceeded p => p.Task,
        TestsPassed p => p.Task,
        OutputEquals p => p.Task,
        PathsChanged p => p.Task,
        _ => throw new InvalidOperationException($"Unknown predicate type {GetType().Name}."),
    };
}

/// <summary>True when the task completed.</summary>
public sealed record TaskSucceeded(TaskId Task) : ConditionPredicate;

/// <summary>True when the latest system-captured test run of the task succeeded (at least one test passed, none failed).</summary>
public sealed record TestsPassed(TaskId Task) : ConditionPredicate;

/// <summary>True when the task reported an output with exactly this key and value.</summary>
public sealed record OutputEquals(TaskId Task, string Key, string Value) : ConditionPredicate;

/// <summary>True when the task changed any path that starts with the prefix (empty prefix: any path).</summary>
public sealed record PathsChanged(TaskId Task, string PathPrefix) : ConditionPredicate;

/// <summary>What the system knows about a task when it evaluates a condition.</summary>
public sealed record TaskFacts(
    TaskId Task,
    WorkTaskStatus Status,
    TestRunSummary? LatestTests,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyList<string> ChangedPaths);

/// <summary>Evaluates the closed set of predicates over facts. Pure and deterministic.</summary>
public static class ConditionEvaluator
{
    public static bool Evaluate(ConditionPredicate predicate, IReadOnlyDictionary<TaskId, TaskFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.TryGetValue(predicate.ReferencedTask, out var about))
        {
            throw new DomainRuleViolationException(
                "condition.facts-missing",
                $"There are no facts about task '{predicate.ReferencedTask}', so the condition cannot be evaluated.");
        }

        return predicate switch
        {
            TaskSucceeded => about.Status == WorkTaskStatus.Completed,
            TestsPassed => about.LatestTests is { Succeeded: true },
            OutputEquals p => about.Outputs.TryGetValue(p.Key, out var value) && string.Equals(value, p.Value, StringComparison.Ordinal),
            PathsChanged p => about.ChangedPaths.Any(path => path.Replace('\\', '/').StartsWith(p.PathPrefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)),
            _ => throw new InvalidOperationException($"Unknown predicate type {predicate.GetType().Name}."),
        };
    }
}
