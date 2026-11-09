using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// What the engine lets an executor do in a task (docs/17 D-46, F13-18): the one rule, public so that the screens that say what an agent
/// may use («Herramientas») read it from here and never copy it. <see cref="Workers"/> launches with it.
/// </summary>
public static class AgentPermissions
{
    /// <summary>
    /// The permissions of an executor in a task that writes (<paramref name="writes"/>) or one that only reads: it writes only in its worktree
    /// and inside its scope, has a shell only when its template lists <c>Bash</c> and the task writes, and gets no network; the file tools
    /// that write are left out of a task that reads.
    /// </summary>
    public static PermissionProfile For(bool writes, AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var shell = writes && agent.AllowedTools.Contains("Bash", StringComparer.OrdinalIgnoreCase);
        var tools = agent.AllowedTools
            .Where(t => !string.Equals(t, "Bash", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t, "WebFetch", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t, "WebSearch", StringComparison.OrdinalIgnoreCase))
            .Where(t => writes || !(string.Equals(t, "Write", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Edit", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new PermissionProfile(writes, shell, false, tools, [], []);
    }
}
