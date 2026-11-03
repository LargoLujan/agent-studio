using AgentStudio.Application.Skills;
using ModelContextProtocol.Server;

namespace AgentStudio.SkillsMcp;

/// <summary>
/// Entry point of the local, read-only MCP server of the assisted skill recommendation (docs/14 D-30, ADR-0024). Claude
/// Code launches it over stdio from an auxiliary read-only run with one argument pair, <c>--snapshot &lt;absolute
/// path&gt;</c>; Agent Studio never runs it in-process. It reads that snapshot once and serves it until the client closes
/// its stdin. Stdout is the protocol, so nothing else is ever written there: diagnostics go to stderr. There is no host:
/// no configuration file, environment section or user secret is read, no logger is added and no socket is opened.
/// </summary>
internal static class Program
{
    /// <summary>The client closed the session.</summary>
    internal const int Completed = 0;

    /// <summary>The command line is not <c>--snapshot &lt;absolute path&gt;</c>.</summary>
    internal const int UsageError = 2;

    /// <summary>The snapshot could not be read or is not one.</summary>
    internal const int SnapshotError = 3;

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var snapshotPath, out var problem))
        {
            await Console.Error.WriteLineAsync(problem).ConfigureAwait(false);
            return UsageError;
        }

        McpServerOptions options;
        try
        {
            options = SkillsMcpServer.Options(SnapshotFile.Read(snapshotPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            await Console.Error.WriteLineAsync("AgentStudio.SkillsMcp: the skills snapshot cannot be served: " + ex.Message).ConfigureAwait(false);
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

    /// <summary>True when <paramref name="args"/> is exactly <c>--snapshot &lt;absolute path&gt;</c>.</summary>
    internal static bool TryParseArguments(string[] args, out string snapshotPath, out string problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        snapshotPath = string.Empty;
        problem = $"Usage: AgentStudio.SkillsMcp {SkillsMcpNames.SnapshotArgument} <absolute path of the skills snapshot>";
        if (args.Length != 2 || !string.Equals(args[0], SkillsMcpNames.SnapshotArgument, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[1]))
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(args[1]))
        {
            problem = $"The snapshot path must be absolute ({SkillsMcpNames.SnapshotArgument} <absolute path>).";
            return false;
        }

        snapshotPath = args[1];
        problem = string.Empty;
        return true;
    }
}
