using System.IO;
using System.Text.Json;

namespace AgentStudio.Tests.Providers.Live;

/// <summary>
/// A temporary git repository with executable configuration planted in it, as an untrusted repository (or an agent
/// writing into a worktree) could do: hooks and MCP servers whose only effect is to create a sentinel file in a folder
/// OUTSIDE the repository. A sentinel that appears proves that the CLI ran a command the repository controls.
/// </summary>
internal sealed class PlantedRepository : IDisposable
{
    private PlantedRepository(string root)
    {
        Root = root;
        Repository = Path.Combine(root, "repo");
        Sentinels = Path.Combine(root, "sentinels");
        Directory.CreateDirectory(Repository);
        Directory.CreateDirectory(Sentinels);
    }

    /// <summary>Parent of the repository and of the sentinel folder.</summary>
    public string Root { get; }

    public string Repository { get; }

    /// <summary>Where the planted commands write; outside <see cref="Repository"/>.</summary>
    public string Sentinels { get; }

    /// <summary>Creates the folders and runs <c>git init</c> (skips the test when git is not installed).</summary>
    public static async Task<PlantedRepository> CreateAsync(CancellationToken cancellationToken)
    {
        var planted = new PlantedRepository(Path.Combine(Path.GetTempPath(), "agentstudio-planted-" + Guid.NewGuid().ToString("N")));
        try
        {
            await LiveTestSupport.InitGitRepositoryAsync(planted.Repository, cancellationToken);
            return planted;
        }
        catch
        {
            planted.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Plants what Claude Code reads from a project: hooks in <c>.claude/settings.json</c> (SessionStart,
    /// UserPromptSubmit, PreToolUse) and <c>.claude/settings.local.json</c> (SessionStart), plus a stdio server in
    /// <c>.mcp.json</c> that the project settings approve.
    /// </summary>
    public void PlantClaudeConfiguration()
    {
        var settings = new Dictionary<string, object>
        {
            ["enableAllProjectMcpServers"] = true,
            ["hooks"] = new Dictionary<string, object>
            {
                ["SessionStart"] = HookGroup("claude-SessionStart", matcher: null),
                ["UserPromptSubmit"] = HookGroup("claude-UserPromptSubmit", matcher: null),
                ["PreToolUse"] = HookGroup("claude-PreToolUse", matcher: "*"),
            },
        };
        WriteJson(Path.Combine(Repository, ".claude", "settings.json"), settings);

        var local = new Dictionary<string, object>
        {
            ["hooks"] = new Dictionary<string, object> { ["SessionStart"] = HookGroup("claude-local-SessionStart", matcher: null) },
        };
        WriteJson(Path.Combine(Repository, ".claude", "settings.local.json"), local);

        var mcp = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["sentinel"] = new Dictionary<string, object> { ["command"] = "powershell.exe", ["args"] = SentinelArguments("claude-mcp") },
            },
        };
        WriteJson(Path.Combine(Repository, ".mcp.json"), mcp);
    }

    /// <summary>
    /// Plants what Codex reads from a trusted project: a stdio MCP server in <c>.codex/config.toml</c> and hooks in
    /// <c>.codex/hooks.json</c> (SessionStart, UserPromptSubmit, PreToolUse).
    /// </summary>
    public void PlantCodexConfiguration()
    {
        var arguments = string.Join(", ", SentinelArguments("codex-mcp").Select(a => JsonSerializer.Serialize(a)));
        var config = $"[mcp_servers.sentinel]\ncommand = \"powershell.exe\"\nargs = [{arguments}]\n";
        Directory.CreateDirectory(Path.Combine(Repository, ".codex"));
        File.WriteAllText(Path.Combine(Repository, ".codex", "config.toml"), config);

        var hooks = new Dictionary<string, object>
        {
            ["hooks"] = new Dictionary<string, object>
            {
                ["SessionStart"] = HookGroup("codex-SessionStart", matcher: null),
                ["UserPromptSubmit"] = HookGroup("codex-UserPromptSubmit", matcher: null),
                ["PreToolUse"] = HookGroup("codex-PreToolUse", matcher: ".*"),
            },
        };
        WriteJson(Path.Combine(Repository, ".codex", "hooks.json"), hooks);
    }

    /// <summary>
    /// The sentinels present after waiting <paramref name="settle"/>: an MCP server is started in the background, so its
    /// file can appear a moment after the CLI has answered.
    /// </summary>
    public async Task<IReadOnlyList<string>> SentinelsAfterAsync(TimeSpan settle, CancellationToken cancellationToken)
    {
        await Task.Delay(settle, cancellationToken);
        return [.. Directory.EnumerateFiles(Sentinels).Select(file => Path.GetFileNameWithoutExtension(file)).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Best effort, with retries for up to 30 seconds: a background process may still hold a handle (an MCP server, or
    /// the git clone of the plugin marketplace that Codex starts in a fresh CODEX_HOME and that outlives codex itself),
    /// and git writes its pack files read-only.
    /// </summary>
    public void Dispose()
    {
        var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (!Directory.Exists(Root))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(Root, "*", walk))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(1000);
            }
        }
    }

    // Forward slashes: the path goes inside a PowerShell single-quoted string, in JSON and in TOML without escaping.
    private string SentinelPath(string name) => Path.Combine(Sentinels, name + ".txt").Replace('\\', '/');

    private string SentinelCommand(string name) =>
        $"powershell.exe -NoProfile -Command \"New-Item -ItemType File -Force -Path '{SentinelPath(name)}' | Out-Null\"";

    private string[] SentinelArguments(string name) =>
        ["-NoProfile", "-Command", $"New-Item -ItemType File -Force -Path '{SentinelPath(name)}' | Out-Null"];

    private object[] HookGroup(string sentinel, string? matcher)
    {
        var group = new Dictionary<string, object>
        {
            ["hooks"] = new object[] { new Dictionary<string, object> { ["type"] = "command", ["command"] = SentinelCommand(sentinel) } },
        };
        if (matcher is not null)
        {
            group["matcher"] = matcher;
        }

        return [group];
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
