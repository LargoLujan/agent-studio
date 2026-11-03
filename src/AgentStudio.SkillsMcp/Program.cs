using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using ModelContextProtocol.Server;

namespace AgentStudio.SkillsMcp;

/// <summary>
/// The snapshots one launch of the server serves (docs/14 D-30; docs/17 D-06): the absolute path of the skills catalog
/// (<c>--snapshot</c>), of the history of the project (<c>--history</c>), or both. At least one is there.
/// </summary>
internal sealed record ServerArguments(string? SkillsSnapshot, string? HistorySnapshot);

/// <summary>
/// Entry point of the local, read-only MCP server of Agent Studio (docs/14 D-30, ADR-0024; docs/17 D-06, ADR-0047). A CLI
/// launches it over stdio from a read-only run with the snapshots it serves: <c>--snapshot &lt;absolute path&gt;</c> for
/// the skills catalog of the assisted recommendation, <c>--history &lt;absolute path&gt;</c> for the trusted history of
/// the project the planner searches, or both; Agent Studio never runs it in-process. It reads them once and serves them
/// until the client closes its stdin. Stdout is the protocol, so nothing else is ever written there: diagnostics go to
/// stderr. There is no host: no configuration file, environment section or user secret is read, no logger is added, no
/// socket is opened and no model is loaded.
/// </summary>
internal static class Program
{
    /// <summary>The client closed the session.</summary>
    internal const int Completed = 0;

    /// <summary>The command line is not one or both of <c>--snapshot &lt;absolute path&gt;</c> and <c>--history &lt;absolute path&gt;</c>.</summary>
    internal const int UsageError = 2;

    /// <summary>A snapshot could not be read or is not one.</summary>
    internal const int SnapshotError = 3;

    private const string Usage =
        $"Usage: AgentStudio.SkillsMcp [{SkillsMcpNames.SnapshotArgument} <absolute path of the skills snapshot>] "
        + $"[{ProjectHistoryNames.SnapshotArgument} <absolute path of the history snapshot>], at least one of them, each once.";

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var arguments, out var problem))
        {
            await Console.Error.WriteLineAsync(problem).ConfigureAwait(false);
            return UsageError;
        }

        McpServerOptions options;
        try
        {
            var skills = arguments.SkillsSnapshot is { } skillsPath ? SnapshotFile.Read(skillsPath) : null;
            var history = arguments.HistorySnapshot is { } historyPath ? HistorySnapshotFile.Read(historyPath) : null;
            options = SkillsMcpServer.Options(skills, history);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            await Console.Error.WriteLineAsync("AgentStudio.SkillsMcp: a snapshot cannot be served: " + ex.Message).ConfigureAwait(false);
            return SnapshotError;
        }

        var transport = new StdioServerTransport(options);
        await using (transport.ConfigureAwait(false))
        {
            var server = McpServer.Create(transport, options);
            await using (server.ConfigureAwait(false))
            {
                await server.RunAsync().ConfigureAwait(false);
            }
        }

        return Completed;
    }

    /// <summary>
    /// True when <paramref name="args"/> is <c>--snapshot &lt;absolute path&gt;</c>, <c>--history &lt;absolute path&gt;</c>
    /// or both, in either order and each once; nothing else is accepted.
    /// </summary>
    internal static bool TryParseArguments(string[] args, out ServerArguments arguments, out string problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        arguments = new ServerArguments(null, null);
        problem = Usage;
        if (args.Length is not (2 or 4))
        {
            return false;
        }

        string? skills = null;
        string? history = null;
        for (var i = 0; i < args.Length; i += 2)
        {
            var (name, value) = (args[i], args[i + 1]);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (string.Equals(name, SkillsMcpNames.SnapshotArgument, StringComparison.Ordinal) && skills is null)
            {
                skills = value;
            }
            else if (string.Equals(name, ProjectHistoryNames.SnapshotArgument, StringComparison.Ordinal) && history is null)
            {
                history = value;
            }
            else
            {
                return false;
            }

            if (!Path.IsPathFullyQualified(value))
            {
                problem = $"The path after {name} must be absolute ({name} <absolute path>).";
                return false;
            }
        }

        arguments = new ServerArguments(skills, history);
        problem = string.Empty;
        return true;
    }
}
