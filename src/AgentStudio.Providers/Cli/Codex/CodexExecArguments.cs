using AgentStudio.Application.Providers;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli.Codex;

/// <summary>
/// Translates an <see cref="AgentRunRequest"/> into <c>codex exec</c> arguments. Pure. The prompt never appears in the
/// arguments: it goes through standard input (<c>-</c> is always the last argument).
/// </summary>
/// <remarks>
/// Verified with <c>codex exec --help</c> and <c>codex exec resume --help</c> (0.160.0). <c>resume</c> does not accept
/// <c>-s</c>, <c>-C</c> or <c>--add-dir</c>, but <c>exec</c> options placed before the subcommand are accepted
/// (<c>codex exec -s read-only resume &lt;id&gt; -</c> parses), so all of them go before <c>resume</c>.
/// <c>danger-full-access</c> and <c>--dangerously-bypass-approvals-and-sandbox</c> are never produced.
/// <para>
/// A run that does not persist its session (<see cref="AgentRunRequest.PersistSession"/> false, every turn of a debate,
/// docs/15 D-16) is a new thread with <c>--ephemeral -c history.persistence=none</c> where <c>resume</c> would go; together
/// with a session to resume it is refused. The judge keeps its thread for its correction (<c>exec resume</c>).
/// </para>
/// <para>
/// The repository is untrusted. Codex applies a project's <c>.codex/</c> layer (config.toml with <c>mcp_servers</c>,
/// whose commands start outside the sandbox; hooks; rules) when the user's config.toml trusts the project, and a git
/// worktree inherits the trust of its main checkout. Every launch therefore starts with <see cref="Isolation"/>:
/// constant flags only, verified with 0.160.0 (source of <c>codex-rs/config/src/loader/mod.rs</c> and real runs).
/// </para>
/// </remarks>
public static class CodexExecArguments
{
    public const string SandboxReadOnly = "read-only";
    public const string SandboxWorkspaceWrite = "workspace-write";

    /// <summary>The key of the configuration that takes the effort of the model (<c>-c model_reasoning_effort=…</c>).</summary>
    public const string ReasoningEffortKey = "model_reasoning_effort";

    /// <summary>Placed right after <c>exec --json</c>, so it also applies to <c>resume</c>.</summary>
    private static readonly string[] Isolation =
    [
        // No $CODEX_HOME/config.toml: without its [projects] trust table no project .codex/ layer is enabled.
        // Auth still comes from CODEX_HOME.
        "--ignore-user-config",
        // No user or project execpolicy .rules (they can approve commands outside the sandbox).
        "--ignore-rules",
        // No lifecycle hooks (user, project or plugin).
        "--disable", "hooks",
        // The Windows backend comes from the user config that is now ignored; without it there is no sandbox at all.
        // Unquoted on purpose: codex takes a value that is not TOML literally, and a quote would not pass a codex.cmd shim.
        "-c", "windows.sandbox=unelevated",
    ];

    /// <summary>
    /// A turn that is not saved (docs/15 D-16), where <c>resume</c> would go. <c>--ephemeral</c>: «Run without persisting
    /// session files to disk» (<c>codex exec --help</c> 0.160.0); <c>history.persistence=none</c>, the history of messages
    /// (<c>history.jsonl</c>), which <c>--ephemeral</c> does not cover, in case <c>exec</c> writes it. Constant text, unquoted
    /// like the isolation block.
    /// </summary>
    private static readonly string[] Ephemeral = ["--ephemeral", "-c", "history.persistence=none"];

    /// <param name="request">The run to translate.</param>
    /// <param name="schemaFilePath">Temporary file holding <see cref="AgentRunRequest.OutputJsonSchema"/>; required when a schema is requested.</param>
    /// <param name="isRepository">When false, <c>--skip-git-repo-check</c> is added.</param>
    /// <exception cref="ArgumentException">
    /// The request does not persist its session and resumes one (<see cref="AgentRunRequest.PersistSession"/> false with
    /// <see cref="AgentRunRequest.ResumeSessionId"/>): a thread without its files cannot be resumed.
    /// </exception>
    public static IReadOnlyList<string> Build(AgentRunRequest request, string? schemaFilePath, bool isRepository)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.PersistSession && !string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            throw new ArgumentException("A run that does not persist its session cannot resume one: --ephemeral and resume exclude each other (docs/15 D-16).", nameof(request));
        }

        if (request.OutputJsonSchema is not null && string.IsNullOrWhiteSpace(schemaFilePath))
        {
            throw new ArgumentException("A schema file path is required when an output schema is requested.", nameof(schemaFilePath));
        }

        var permissions = request.Permissions;
        var args = new List<string> { "exec", "--json" };
        args.AddRange(Isolation);

        if (permissions.AllowFileWrites)
        {
            args.AddRange(["-s", SandboxWorkspaceWrite]);

            // Key of the [sandbox_workspace_write] table; it only matters when the sandbox can write.
            args.AddRange(["-c", "sandbox_workspace_write.network_access=" + (permissions.AllowNetwork ? "true" : "false")]);
            foreach (var directory in permissions.AdditionalDirectories.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                args.AddRange(["--add-dir", CliArgumentGuard.Directory(directory, nameof(request))]);
            }
        }
        else
        {
            args.AddRange(["-s", SandboxReadOnly]);
        }

        args.AddRange(["-C", CliArgumentGuard.Directory(request.WorkingDirectory, nameof(request))]);
        if (!isRepository)
        {
            args.Add("--skip-git-repo-check");
        }

        if (!string.IsNullOrWhiteSpace(request.Agent.Model.Model))
        {
            args.AddRange(["-m", CliArgumentGuard.Model(request.Agent.Model.Model, nameof(request))]);
        }

        // Phase 13 (docs/17 D-25): the effort of the agent, only when it is set and Codex takes it (minimal to xhigh), as a
        // token of the closed list and never free text. Unquoted like the isolation block: a bare word is not TOML, so codex
        // takes it literally. Any other (a template edited by hand: the domain refuses it, agent.effort-not-for-provider) is not
        // passed, and the CLI uses its own default.
        if (request.Agent.Effort is { } effort && ReasoningEfforts.Accepts(ProviderId.CodexCli, effort))
        {
            args.AddRange(["-c", ReasoningEffortKey + "=" + ReasoningEfforts.Token(effort)]);
        }

        if (request.OutputJsonSchema is not null)
        {
            args.AddRange(["--output-schema", schemaFilePath!]);
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            args.AddRange(["resume", CliArgumentGuard.SessionId(request.ResumeSessionId, nameof(request))]);
        }
        else if (!request.PersistSession)
        {
            args.AddRange(Ephemeral);
        }

        args.Add("-");
        return args;
    }

    /// <summary>
    /// Text written to standard input. Codex has no flag for extra instructions that is not free text in an argument
    /// (<c>-c developer_instructions=...</c>), so the agent instructions precede the task inside delimiters.
    /// </summary>
    public static string BuildStandardInput(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instructions = request.Agent.SystemInstructions;
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return request.Prompt;
        }

        return $"<agent_instructions>\n{instructions.Trim()}\n</agent_instructions>\n\n<task>\n{request.Prompt}\n</task>\n";
    }

    /// <summary>True when <paramref name="directory"/> or one of its parents has a version-control marker entry.</summary>
    public static bool IsRepository(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return true;
            }
        }

        return false;
    }
}
