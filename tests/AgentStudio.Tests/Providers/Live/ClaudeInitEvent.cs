using System.Text.Json;

namespace AgentStudio.Tests.Providers.Live;

/// <summary>A plugin as listed by the <c>system/init</c> event of Claude Code.</summary>
internal sealed record ClaudePlugin(string Name, string? Path, string? Source);

/// <summary>An MCP server as listed by the <c>system/init</c> event of Claude Code (<c>source</c> is, e.g., <c>claudeai</c>).</summary>
internal sealed record ClaudeMcpServer(string Name, string? Status, string? Source);

/// <summary>
/// The parts of Claude Code's <c>system/init</c> stream-json event that tell what the session can see. The adapter's
/// <c>SessionStarted</c> does not carry them, so tests read the raw line (see <see cref="RecordingProcessRunner"/>).
/// </summary>
internal sealed record ClaudeInitEvent(
    string? SessionId,
    string? Model,
    IReadOnlyList<string> Skills,
    IReadOnlyList<ClaudePlugin> Plugins,
    IReadOnlyList<ClaudeMcpServer> McpServers)
{
    /// <summary>
    /// True for a <c>system</c> line about a hook (<c>hook_started</c>, <c>hook_progress</c>, <c>hook_response</c>):
    /// Claude Code always streams them for SessionStart hooks, so a run without any of them ran no SessionStart hook.
    /// </summary>
    public static bool IsHookEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Text(root, "type") == "system"
                && Text(root, "subtype") is { } subtype
                && subtype.StartsWith("hook_", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The first <c>system/init</c> event among the stdout lines, or null when there is none.</summary>
    public static ClaudeInitEvent? Find(IEnumerable<string> standardOutputLines)
    {
        ArgumentNullException.ThrowIfNull(standardOutputLines);
        foreach (var line in standardOutputLines)
        {
            if (TryParse(line, out var init))
            {
                return init;
            }
        }

        return null;
    }

    public static bool TryParse(string line, out ClaudeInitEvent init)
    {
        init = new ClaudeInitEvent(null, null, [], [], []);
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != "system" || Text(root, "subtype") != "init")
            {
                return false;
            }

            init = new ClaudeInitEvent(Text(root, "session_id"), Text(root, "model"), ReadSkills(root), ReadPlugins(root), ReadMcpServers(root));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when a skill called <paramref name="name"/> is listed, plain or namespaced as <c>plugin:name</c>.</summary>
    public bool ListsSkill(string name) =>
        Skills.Any(s => string.Equals(s, name, StringComparison.Ordinal) || s.EndsWith(":" + name, StringComparison.Ordinal));

    private static List<string> ReadSkills(JsonElement root) =>
        root.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array
            ? [.. skills.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!)]
            : [];

    private static List<ClaudePlugin> ReadPlugins(JsonElement root) =>
        root.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array
            ? [.. plugins.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object && Text(p, "name") is not null)
                .Select(p => new ClaudePlugin(Text(p, "name")!, Text(p, "path"), Text(p, "source")))]
            : [];

    private static List<ClaudeMcpServer> ReadMcpServers(JsonElement root) =>
        root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array
            ? [.. servers.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.Object && Text(s, "name") is not null)
                .Select(s => new ClaudeMcpServer(Text(s, "name")!, Text(s, "status"), Text(s, "source")))]
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
