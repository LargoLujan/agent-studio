using System.Diagnostics.CodeAnalysis;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Application.Graph;

/// <summary>
/// The protocol between the host of Auto Graph and the analysis process <c>AgentStudio.GraphWorker</c> (docs/20 D-18, ADR-0062), shared by both
/// sides, as <c>SkillsMcpNames</c> is for the MCP server. The host launches the process at low integrity over the copy of a commit with
/// <c>--root</c> (the folder of the copy) and <c>--commit</c> (its full id), and reads the model from the standard output. The process writes
/// nothing else there: the model is one JSON document (<see cref="CodeGraphJson"/>) or nothing. Diagnostics and progress go to the standard
/// error. The standard output of the process is data the app does not trust: it is read with the strict schema and the caps of
/// <see cref="CodeGraphJson"/>.
/// </summary>
/// <remarks>
/// Progress: before it opens each file, the process writes one line <c>progress:&lt;relative path&gt;</c> to the standard error and flushes it. If the
/// process dies (an exhausted stack cannot be caught), the host knows which file it was reading and launches it again with <c>--skip</c>
/// for that file; that file is then a <c>graph.file-skipped</c> gap and its project partial. At most <see cref="MaxRelaunches"/> times; after that
/// the graph is partial with what there is.
/// </remarks>
public static class GraphWorkerProtocol
{
    /// <summary>The folder of the copy of the commit, absolute: the only folder the process opens files in.</summary>
    public const string RootArgument = "--root";

    /// <summary>The full id of the commit the copy holds: the model says which commit it describes.</summary>
    public const string CommitArgument = "--commit";

    /// <summary>A file, relative to the root, that an earlier launch died on; may be repeated.</summary>
    public const string SkipArgument = "--skip";

    /// <summary>What a progress line starts with.</summary>
    public const string ProgressPrefix = "progress:";

    /// <summary>The version of the format of the standard output (<see cref="CodeGraphJson"/>).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The times the host launches the process again after it died on a file (docs/20 D-18).</summary>
    public const int MaxRelaunches = 3;

    /// <summary>Most files that a launch is told to skip: one per relaunch, with room to spare.</summary>
    public const int MaxSkipped = 16;

    /// <summary>The longest the process may run before the host kills it (docs/20 D-18).</summary>
    public static readonly TimeSpan MaxRuntime = TimeSpan.FromSeconds(120);

    /// <summary>The most the host reads from the standard output of the process, in bytes; a longer output is a failed analysis.</summary>
    public const int MaxOutputBytes = 32 * 1024 * 1024;

    /// <summary>The line of a file the process starts on, for the standard error.</summary>
    public static string ProgressLine(string relativePath) => ProgressPrefix + relativePath;

    /// <summary>The file a progress line names, or false when the line is not a progress line or names a path that is not safe.</summary>
    public static bool TryParseProgress(string? line, [NotNullWhen(true)] out string? relativePath)
    {
        relativePath = null;
        if (line is null || !line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var path = line[ProgressPrefix.Length..];
        if (!RepositoryPaths.IsSafeRelative(path))
        {
            return false;
        }

        relativePath = path;
        return true;
    }
}

/// <summary>How the analysis process ends. Any other code is a crash.</summary>
public static class GraphWorkerExitCodes
{
    /// <summary>The model is on the standard output.</summary>
    public const int Completed = 0;

    /// <summary>The command line is not the protocol.</summary>
    public const int UsageError = 2;

    /// <summary>The root is not a folder.</summary>
    public const int RootError = 3;

    /// <summary>The analysis is not available in this build of the process (the skeleton of the P0 ends here; WP-G replaces it).</summary>
    public const int AnalysisUnavailable = 4;

    /// <summary>The analysis failed; the standard error has the reason, with no path and no text of the repository.</summary>
    public const int Failed = 5;
}

/// <summary>
/// What the host asks of the analysis process: the root of the copy, the commit it holds and the files to skip. It is valid by construction, and
/// it is both written (<see cref="ToArguments"/>) and read (<see cref="TryParse"/>) here, so the host and the process cannot disagree.
/// </summary>
public sealed record GraphWorkerRequest
{
    public GraphWorkerRequest(string root, string commit, IReadOnlyList<string>? skipped = null)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Any(char.IsControl) || !Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The root of the copy is an absolute path.", nameof(root));
        }

        if (!TraceRef.IsFullCommitId(commit))
        {
            throw new ArgumentException("The commit is a full id (40 or 64 lowercase hexadecimal characters).", nameof(commit));
        }

        skipped ??= [];
        if (skipped.Count > GraphWorkerProtocol.MaxSkipped || skipped.Any(p => !RepositoryPaths.IsSafeRelative(p)))
        {
            throw new ArgumentException($"At most {GraphWorkerProtocol.MaxSkipped} files are skipped, each relative to the root and never leading out of it.", nameof(skipped));
        }

        Root = root;
        Commit = commit;
        Skipped = [.. skipped];
    }

    public string Root { get; }

    public string Commit { get; }

    public IReadOnlyList<string> Skipped { get; }

    /// <summary>The arguments of the process, each one an element: nothing is joined into a command line.</summary>
    public IReadOnlyList<string> ToArguments()
    {
        List<string> arguments = [GraphWorkerProtocol.RootArgument, Root, GraphWorkerProtocol.CommitArgument, Commit];
        foreach (var path in Skipped)
        {
            arguments.Add(GraphWorkerProtocol.SkipArgument);
            arguments.Add(path);
        }

        return arguments;
    }

    /// <summary>Reads the arguments the process got: exactly the protocol, each option with its value, <c>--root</c> and <c>--commit</c> once and in any order.</summary>
    public static bool TryParse(IReadOnlyList<string> arguments, [NotNullWhen(true)] out GraphWorkerRequest? request)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        string? root = null;
        string? commit = null;
        var skipped = new List<string>();
        if (arguments.Count % 2 != 0)
        {
            return false;
        }

        for (var i = 0; i < arguments.Count; i += 2)
        {
            var value = arguments[i + 1];
            switch (arguments[i])
            {
                case GraphWorkerProtocol.RootArgument when root is null:
                    root = value;
                    break;
                case GraphWorkerProtocol.CommitArgument when commit is null:
                    commit = value;
                    break;
                case GraphWorkerProtocol.SkipArgument:
                    skipped.Add(value);
                    break;
                default:
                    return false;
            }
        }

        if (root is null || commit is null)
        {
            return false;
        }

        try
        {
            request = new GraphWorkerRequest(root, commit, skipped);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool Equals(GraphWorkerRequest? other) =>
        other is not null && Root == other.Root && Commit == other.Commit && Skipped.SequenceEqual(other.Skipped);

    public override int GetHashCode() => HashCode.Combine(Root, Commit, Skipped.Count);
}
