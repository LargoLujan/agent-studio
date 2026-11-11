using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Domain.Metrics;

/// <summary>
/// What an attempt was for, the first axis of the activity of the consumption (docs/16 D-34). A deterministic classification,
/// documented and never asked of an agent. The names are written to exports, so new ones are appended.
/// </summary>
public enum ActivityKind
{
    /// <summary>The planner and its correction.</summary>
    Coordination,

    /// <summary>The participants of a debate and the recommendation of skills.</summary>
    Analysis,

    /// <summary>An executor whose diff touches code.</summary>
    Implementation,

    /// <summary>An executor whose diff touches only the paths of the tests.</summary>
    Tests,

    /// <summary>An executor whose diff touches only the documentation.</summary>
    Documentation,

    /// <summary>The judge, the audits and the challenges.</summary>
    Review,

    /// <summary>A follow-up run opened after a conflict of integration.</summary>
    Conflicts,
}

/// <summary>The second axis (docs/16 D-34): whether the attempt is the first of its task or a retry.</summary>
public enum AttemptOrder
{
    First,
    Retry,
}

/// <summary>
/// The third axis (docs/16 D-34): what became of the work. <see cref="Kept"/> reached a delivered integration;
/// <see cref="Discarded"/> is a failed or cancelled task or a run that was not delivered; <see cref="Pending"/> is an attempt
/// of a run still going, which has neither yet (docs/16 does not name it: an attempt in flight needs a value too).
/// </summary>
public enum AttemptFate
{
    Kept,
    Discarded,
    Pending,
}

/// <summary>
/// The value of an attempt on each of the three axes of the activity (docs/16 D-34, F9-6): one on each, so an attempt
/// counts once in each axis and the groups of an axis add up to the total. <paramref name="HasCommit"/> says whether the attempt
/// left a commit, which the destination tells apart («descartado, con o sin commit»).
/// </summary>
public sealed record ActivityClass(ActivityKind Kind, AttemptOrder Order, AttemptFate Fate, bool HasCommit);

/// <summary>The rule of the first axis for an executor: from the paths its diff touches (docs/16 D-34).</summary>
public static class ActivityPaths
{
    /// <summary>The folders of the tests when the verification profile names none (<c>tests/</c>).</summary>
    public static readonly IReadOnlyList<string> DefaultTestPaths = ["tests/"];

    private static readonly IReadOnlyList<string> DocumentationPaths = ["docs/"];

    /// <summary>
    /// What an executor did, by the paths its diff touches: <see cref="ActivityKind.Tests"/> when all of them are test paths,
    /// <see cref="ActivityKind.Documentation"/> when all of them are <c>docs/</c> or <c>.md</c> files, and
    /// <see cref="ActivityKind.Implementation"/> as soon as one is neither (code). A diff of tests and documentation only counts
    /// as tests; one with no path (the attempt changed nothing) as implementation, which is what an executor does.
    /// </summary>
    /// <param name="changedPaths">The paths the diff changed, relative to the repository (<see cref="WriteScope"/> rules).</param>
    /// <param name="testPaths">The folders and files of the tests: those of the verification profile, or <see cref="DefaultTestPaths"/> when null or empty.</param>
    public static ActivityKind OfDiff(IReadOnlyList<string> changedPaths, IReadOnlyList<string>? testPaths = null)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        if (changedPaths.Count == 0)
        {
            return ActivityKind.Implementation;
        }

        IReadOnlyList<string> tests = testPaths is { Count: > 0 } ? testPaths : DefaultTestPaths;
        var anyTest = false;
        foreach (var path in changedPaths)
        {
            if (WriteScope.Contains(tests, path))
            {
                anyTest = true;
            }
            else if (!IsDocumentation(path))
            {
                return ActivityKind.Implementation;
            }
        }

        return anyTest ? ActivityKind.Tests : ActivityKind.Documentation;
    }

    private static bool IsDocumentation(string path) =>
        WriteScope.Contains(DocumentationPaths, path) || (path is not null && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
}
