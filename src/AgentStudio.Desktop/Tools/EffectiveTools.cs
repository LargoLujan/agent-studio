using System.IO;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;

namespace AgentStudio.Desktop.Tools;

/// <summary>
/// What an agent may use in a task, in one of the two modes the engine launches it in: the tools the launch lists
/// (<c>--tools</c>), those it allows and denies (Claude Code), or the sandbox it runs in (Codex). It is read from the arguments of
/// the launch itself (<see cref="EffectiveTools.For"/>), never from a copy of its rules.
/// </summary>
/// <param name="Available">The built-in tools the session has at all (<c>--tools</c>), by name.</param>
/// <param name="Allowed">The tools the launch allows, by name (a rule of a path is its tool).</param>
/// <param name="Denied">The tools the launch denies, by name.</param>
/// <param name="Sandbox">Codex: <c>read-only</c> or <c>workspace-write</c>; null for a program with no sandbox switch.</param>
/// <param name="Network">Codex: whether the sandbox lets the network through; null when the launch does not say.</param>
/// <param name="Arguments">The arguments of the launch, as the adapter builds them for a placeholder folder.</param>
public sealed record AgentToolSet(
    AgentId Agent,
    ProviderId Provider,
    bool WritesFiles,
    IReadOnlyList<string> Available,
    IReadOnlyList<string> Allowed,
    IReadOnlyList<string> Denied,
    string? Sandbox,
    bool? Network,
    IReadOnlyList<string> Arguments)
{
    /// <summary>What a list of these says to UI Automation: the agent, its program and the tools of its launch.</summary>
    public override string ToString() => $"{Agent} · {Provider.Value} · {string.Join(' ', Available)}";
}

/// <summary>
/// «Herramientas» is computed as the real launch (docs/17 D-46, F13-18): the permissions of the executor are those the engine gives
/// it (<see cref="ProfileOf"/>), and what the agent may use is read from the arguments <see cref="ClaudeCodeArguments.Build"/> and
/// <see cref="CodexExecArguments.Build"/> produce for them. If the launch changes, the view changes with it: nothing is copied.
/// </summary>
public static class EffectiveTools
{
    /// <summary>The folder the preview launches in: the launch only needs an absolute path, and nothing is written there.</summary>
    private static readonly string PreviewFolder = Path.Combine(Path.GetTempPath(), "agent-studio-tools-preview");

    /// <summary>A session id for the preview: the launch only needs one that is valid.</summary>
    private const string PreviewSession = "preview-session";

    /// <summary>The schema of the report of an executor, as a placeholder: the launch adds its own tool for the answer when a schema is asked for.</summary>
    private const string PreviewSchema = "{}";

    /// <summary>
    /// The permissions the engine gives an executor in a task that writes or one that only reads: the engine's own rule
    /// (<see cref="AgentPermissions.For"/>), not a copy of it.
    /// </summary>
    public static PermissionProfile ProfileOf(AgentDefinition agent, bool writes) => AgentPermissions.For(writes, agent);

    /// <summary>The request the engine would launch for <paramref name="agent"/> in a task that writes or one that only reads.</summary>
    public static AgentRunRequest RequestFor(AgentDefinition agent, bool writes) =>
        new(agent, string.Empty, PreviewFolder, ProfileOf(agent, writes), OutputJsonSchema: PreviewSchema);

    /// <summary>What <paramref name="agent"/> may use, read from the arguments of its launch.</summary>
    public static AgentToolSet For(AgentDefinition agent, bool writes)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var request = RequestFor(agent, writes);
        var provider = agent.Model.Provider;
        if (provider == ProviderId.ClaudeCode)
        {
            var args = ClaudeCodeArguments.Build(request, PreviewSession, instructionsFile: null);
            return new AgentToolSet(agent.Id, provider, writes, Names(Value(args, "--tools")), Names(Value(args, "--allowedTools")), Names(Value(args, "--disallowedTools")), null, null, args);
        }

        if (provider == ProviderId.CodexCli)
        {
            var args = CodexExecArguments.Build(request, "schema.json", isRepository: true);
            var network = args.FirstOrDefault(a => a.StartsWith("sandbox_workspace_write.network_access=", StringComparison.Ordinal));
            return new AgentToolSet(agent.Id, provider, writes, [], [], [], Value(args, "-s"), network is null ? null : network.EndsWith("true", StringComparison.Ordinal), args);
        }

        // A program without a list of tools or a sandbox switch (Gemini): nothing is computed that the launch does not say.
        return new AgentToolSet(agent.Id, provider, writes, [], [], [], null, null, []);
    }

    /// <summary>The value after <paramref name="flag"/> in a list of arguments; null when the flag is not there.</summary>
    private static string? Value(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>The names of the tools in a space-separated list of rules: <c>Read(//c/p/**)</c> is <c>Read</c>.</summary>
    public static IReadOnlyList<string> Names(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return [];
        }

        var names = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= list.Length; i++)
        {
            var end = i == list.Length;
            if (!end && list[i] == '(')
            {
                depth++;
            }
            else if (!end && list[i] == ')')
            {
                depth--;
            }

            if (end || (list[i] == ' ' && depth == 0))
            {
                var rule = list[start..i];
                start = i + 1;
                var open = rule.IndexOf('(', StringComparison.Ordinal);
                var name = open < 0 ? rule : rule[..open];
                if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }
}
