namespace AgentStudio.Application.Graph;

/// <summary>
/// The codes of the gaps of the model of Auto Graph (docs/20 D-16 to D-18): what the reading of a node did not cover. The analysis process writes them,
/// the desktop paints them from the code (ADR-0054), and none of them quotes text of the repository.
/// </summary>
public static class GraphGapCodes
{
    /// <summary>What MSBuild would resolve was not evaluated: a value with <c>$(…)</c>, an element with a <c>Condition</c>, a wildcard, an <c>Import</c> or a <c>Compile</c> item.</summary>
    public const string Unevaluated = "graph.unevaluated";

    /// <summary>A <c>Directory.Build.props</c> or <c>.targets</c> above the project exists: it is noted and never applied.</summary>
    public const string BuildProps = "graph.build-props";

    /// <summary>A path of the repository was absolute or led out of the copy: nothing was opened.</summary>
    public const string OutsideRoot = "graph.outside-root";

    /// <summary>A path of the repository is not a path that can be read safely (a device name, a character that is not allowed).</summary>
    public const string InvalidPath = "graph.invalid-path";

    /// <summary>A link or a junction was found on the way: it is not followed.</summary>
    public const string Link = "graph.link";

    /// <summary>A file that was listed or referenced is not in the copy.</summary>
    public const string Missing = "graph.missing";

    /// <summary>A file that was too long, took too long, nested too deep, failed to parse or killed an earlier launch: it was not read.</summary>
    public const string FileSkipped = "graph.file-skipped";

    /// <summary>A cap of the analysis was reached: what comes after it was not read.</summary>
    public const string Limit = "graph.limit";

    /// <summary>The same project is listed twice in the solution: only the first was kept.</summary>
    public const string Duplicate = "graph.duplicate";

    /// <summary>More than one solution file is at the root: only the first was read.</summary>
    public const string IgnoredSolution = "graph.ignored-solution";

    /// <summary>The codes in the order the screen lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Unevaluated, BuildProps, OutsideRoot, InvalidPath, Link, Missing, FileSkipped, Limit, Duplicate, IgnoredSolution];
}
