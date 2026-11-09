using System.Buffers;
using System.Globalization;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli.Claude;

/// <summary>
/// Builds the <c>claude -p</c> argument list from a run request. Pure: the prompt never appears here (it travels
/// through stdin) and the permission mode is always explicit.
/// </summary>
/// <remarks>
/// <para>
/// Available tools (ADR-0018, «Medición»): <c>--tools</c> lists exactly the built-in tools the profile needs, and Claude
/// Code withholds every other one (Monitor, Workflow, the Cron tools, Artifact...), each of which could run a command,
/// start a background job or schedule work outside the path rules. <c>Skill</c> exists only with a skills folder, whose
/// skill headers the materializer rebuilt so that none grants tools. The rules below are the second line.
/// </para>
/// <para>
/// Permission semantics (ADR-0018, «Medición»). Every launch runs in <see cref="PermissionMode"/>: whatever no rule allows
/// is denied, and the file tools only reach what the path rules name. A file tool is never pre-approved bare (nor as
/// <c>Tool()</c> or <c>Tool(*)</c>, which Claude Code reads as bare): a whole-tool allow rule overrides the tool's own
/// path check, and with bare <c>Write</c> Claude Code 2.1.290 wrote outside its worktree. Instead:
/// </para>
/// <list type="bullet">
/// <item><c>Read(&lt;directory&gt;/**)</c> for the working directory, each additional directory and the skills folder.
/// Read rules govern Read, Grep and Glob (one permission in Claude Code); inside its working directories (the working
/// directory and each <c>--add-dir</c>) Claude Code reads anyway.</item>
/// <item><c>Edit(&lt;working directory&gt;/**)</c> when <see cref="PermissionProfile.AllowFileWrites"/>: Edit rules govern
/// Write, Edit and NotebookEdit (2.1.290 consults no <c>Write(...)</c> rule). An additional directory is read, never
/// written.</item>
/// </list>
/// <para>
/// A rule names its folder from the root of its drive, the way Claude Code documents absolute paths on Windows:
/// <c>//c/Users/.../wt/T1/**</c>. Its root does not follow the session if the session changes directory, and <c>../</c>
/// from it never matches. A folder that cannot be written that way (a drive root, a UNC or device path, glob or escape
/// characters, control characters, a segment ending in a space or a dot) is rejected, and so is a file tool of the profile
/// that names its own path: only the launch decides where the file tools reach.
/// </para>
/// <para>
/// <see cref="PermissionProfile.AllowShell"/> adds <c>Bash</c> and <see cref="PermissionProfile.AllowNetwork"/> adds
/// <c>WebFetch</c> and <c>WebSearch</c> to <c>--allowedTools</c>; a switch that is off adds the tool to
/// <c>--disallowedTools</c>, as does a profile that may not write (<c>Write</c>, <c>Edit</c>). Every launch denies
/// <c>NotebookEdit</c> and <see cref="OutsideTools"/>. Deny rules win over allow rules. Session ids, model names and
/// directories are validated with <see cref="CliArgumentGuard"/> and rejected with an <see cref="ArgumentException"/>.
/// </para>
/// <c>claude --help</c> (2.1.290) documents <c>--json-schema</c> as inline JSON only (no file path), so the schema
/// travels in argv and is limited to <see cref="MaxJsonSchemaBytes"/> bytes.
/// <para>
/// The repository is untrusted and <c>-p</c> skips the workspace trust dialog, so nothing it controls may run: every
/// launch passes an empty <c>--setting-sources</c> (no <c>.claude/settings.json</c> or <c>settings.local.json</c> with
/// their hooks, <c>env</c> block and helper commands, no <c>.mcp.json</c>, CLAUDE.md, project skills, commands or
/// agents, and nothing from the user's profile either), <c>--strict-mcp-config</c> (MCP servers only from
/// <c>--mcp-config</c>) and, from the provider, <c>--settings</c> with an app-controlled file holding
/// <see cref="IsolationSettingsJson"/>. Verified with 2.1.290: <c>claude --init-only</c> and real runs.
/// </para>
/// <para>
/// Local MCP servers (docs/14 D-30): only a launch that does not write, has no shell and has no network may get them
/// (<see cref="AgentRunRequest.McpServers"/>; the assisted skill recommendation), and anything else fails closed with an
/// <see cref="ArgumentException"/>, so an executor never gets one and the measured launch
/// (<see cref="WriteConfinementLaunch"/>) does not change. Their app-controlled file goes in <c>--mcp-config</c> right
/// after <c>--strict-mcp-config</c>, which stays: the session loads those servers and nothing else. The allow rules each
/// server carries (<see cref="McpServerSpec.AllowedTools"/>) go to <c>--allowedTools</c> as they are, and each must be
/// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> of that server, never a built-in tool or another server's; in
/// <see cref="PermissionMode"/> any other tool is denied. <c>--tools</c> only narrows the built-in tools, so they do not go
/// there.
/// </para>
/// <para>
/// Sessions that are not saved (docs/15 D-16): a request with <see cref="AgentRunRequest.PersistSession"/> false, every turn
/// of a debate, is a new session with <c>--no-session-persistence</c> right before its <c>--session-id</c> («sessions will
/// not be saved to disk and cannot be resumed», <c>claude --help</c> 2.1.290). Together with
/// <see cref="AgentRunRequest.ResumeSessionId"/> it is refused with an <see cref="ArgumentException"/>: the two exclude each
/// other. The judge keeps its session for its correction. A writing executor never asks for a turn that is not saved, so
/// the measured launch (<see cref="WriteConfinementLaunch"/>) does not change.
/// </para>
/// </remarks>
public static class ClaudeCodeArguments
{
    /// <summary>Maximum UTF-8 size of the inline <c>--json-schema</c> value.</summary>
    public const int MaxJsonSchemaBytes = 16 * 1024;

    /// <summary>
    /// Content of the app-controlled <c>--settings</c> file: no hook runs whatever else gets loaded (command-line
    /// settings take precedence over project and local settings; only managed policy is above them).
    /// </summary>
    public const string IsolationSettingsJson = """{"disableAllHooks":true}""";

    /// <summary>
    /// The permission mode of every launch: in headless <c>-p</c> it denies whatever no rule allows. Never
    /// <c>acceptEdits</c> (it accepts any edit inside the working directories without a rule), <c>plan</c> (another model,
    /// more cost) or <c>bypassPermissions</c>.
    /// </summary>
    public const string PermissionMode = "dontAsk";

    /// <summary>
    /// Built-in tools of Claude Code 2.1.290 that act outside the working directory without any path rule, denied in
    /// every launch: <c>EnterWorktree</c> creates a git worktree and a branch in the repository and moves the session
    /// there (its own check allows it when no path is given), <c>ExitWorktree</c> leaves it, and <c>Agent</c> (also named
    /// <c>Task</c>) starts a subagent that may declare another directory or permission mode.
    /// </summary>
    public static IReadOnlyList<string> OutsideTools { get; } = ["EnterWorktree", "ExitWorktree", "Agent", "Task"];

    /// <summary>The tool through which Claude Code returns the answer <c>--json-schema</c> asks for.</summary>
    public const string StructuredOutputTool = "StructuredOutput";

    /// <summary>The tool through which Claude Code invokes a skill.</summary>
    public const string SkillTool = "Skill";

    /// <summary>
    /// The file tools of Claude Code 2.1.290 and the names it still accepts for them: never passed bare, they reach what
    /// the path rules name (Read rules: Read, Grep, Glob; Edit rules: Write, Edit, NotebookEdit).
    /// </summary>
    private static readonly HashSet<string> FileTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Read", "Grep", "Glob", "Edit", "Write", "NotebookEdit", "MultiEdit", "NotebookRead", "LS",
    };

    /// <summary>
    /// Characters a folder of a path rule may not hold: glob characters of the gitignore patterns Claude Code matches
    /// (<c>* ? [ ] { }</c>), the parentheses and backslash its rule parser reads, the leading <c>!</c> and <c>#</c> of a
    /// pattern, and the ones Claude Code itself escapes in a path rule (<c>| + ^ $</c>).
    /// </summary>
    private const string RuleMetacharacterList = "*?[]{}()\\!#|+^$";

    private static readonly SearchValues<char> RuleMetacharacters = SearchValues.Create(RuleMetacharacterList);

    /// <summary>The flags (each with its value) that only say what to run, not what the session may do.</summary>
    private static readonly HashSet<string> RunOnlyFlags = new(StringComparer.Ordinal)
    {
        "--output-format", "--model", "--effort", "--max-turns", "--max-budget-usd", "--json-schema", "--session-id", "--resume", "--append-system-prompt-file",
    };

    /// <summary>
    /// The fingerprint of the launch a writing executor with <see cref="MeasuredWriteConfinement.Executor"/> gets (with a
    /// schema for its report, as the engine always asks), made by <see cref="Build"/> itself: every argument in order, each
    /// between brackets, but the ones that only say what to run
    /// (<c>--verbose</c> and <see cref="RunOnlyFlags"/>), with the settings file written as its content
    /// (<see cref="IsolationSettingsJson"/>) and the folder of the working directory as <c>&lt;worktree&gt;</c>.
    /// <see cref="SupportedVersions.WriteConfinementMeasured(ProviderId)"/> lists a version as confined
    /// only while this is the launch it was measured with, so a change to what the session may do turns the relaxation of
    /// ADR-0018 off until the new launch is measured.
    /// </summary>
    public static string WriteConfinementLaunch { get; } = DescribeWriteConfinementLaunch();

    /// <param name="request">The run to translate.</param>
    /// <param name="newSessionId">Session id for a new conversation (ignored when resuming).</param>
    /// <param name="instructionsFile">Temporary file with the system instructions, or null.</param>
    /// <param name="settingsFile">Temporary file with <see cref="IsolationSettingsJson"/>; the provider always passes it.</param>
    /// <param name="mcpConfigFile">Temporary file with <see cref="McpConfigJson"/> of the request's servers; needed when it has any.</param>
    public static IReadOnlyList<string> Build(AgentRunRequest request, string newSessionId, string? instructionsFile, string? settingsFile = null, string? mcpConfigFile = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSessionId);
        if (!request.PersistSession && !string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            // «Sessions will not be saved to disk and cannot be resumed»: a turn that is not saved is always a new session.
            throw new ArgumentException("A run that does not persist its session cannot resume one: --no-session-persistence and --resume exclude each other (docs/15 D-16).", nameof(request));
        }

        var permissions = request.Permissions;
        var budget = request.Agent.Budget;
        var mcpTools = McpToolRules(request, mcpConfigFile);

        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            // No setting source at all: neither the repository (project, local) nor the user's profile is read.
            // Subscription auth does not depend on it.
            "--setting-sources", string.Empty,
            // MCP servers only from --mcp-config: no .mcp.json, user servers or claude.ai connectors.
            "--strict-mcp-config",
        };

        if (mcpTools.Count > 0)
        {
            args.Add("--mcp-config");
            args.Add(CliArgumentGuard.Directory(mcpConfigFile, nameof(mcpConfigFile)));
        }

        if (!string.IsNullOrWhiteSpace(settingsFile))
        {
            args.Add("--settings");
            args.Add(settingsFile);
        }

        args.Add("--permission-mode");
        args.Add(PermissionMode);

        // The file tools reach only what these rules name; the profile's own file tools are never passed bare.
        var allowed = new List<string> { PathRule("Read", request.WorkingDirectory, nameof(request)) };
        allowed.AddRange(permissions.AdditionalDirectories.Select(directory => PathRule("Read", directory, nameof(request))));
        if (!string.IsNullOrWhiteSpace(request.SkillsDirectory))
        {
            // The skill references the model reads live in the session plugin folder, outside the working directory.
            allowed.Add(PathRule("Read", request.SkillsDirectory, nameof(request)));
        }

        if (permissions.AllowFileWrites)
        {
            allowed.Add(PathRule("Edit", request.WorkingDirectory, nameof(request)));
        }

        foreach (var tool in permissions.AllowedTools)
        {
            if (!IsFileTool(tool))
            {
                allowed.Add(tool);
            }
            else if (tool.Contains('(', StringComparison.Ordinal))
            {
                throw new ArgumentException($"The file tool rule '{tool}' names its own path: the launch scopes the file tools to the working directory.", nameof(request));
            }
        }

        // The tools of the local MCP servers, by their exact rule; never in --tools, which only narrows the built-in ones.
        allowed.AddRange(mcpTools);

        var denied = new List<string>();
        if (permissions.AllowShell)
        {
            allowed.Add("Bash");
        }
        else
        {
            denied.Add("Bash");
        }

        if (permissions.AllowNetwork)
        {
            allowed.AddRange(["WebFetch", "WebSearch"]);
        }
        else
        {
            denied.AddRange(["WebFetch", "WebSearch"]);
        }

        if (!permissions.AllowFileWrites)
        {
            denied.AddRange(["Write", "Edit"]);
        }

        // NotebookEdit was never measured, even though Edit rules govern it too.
        denied.Add("NotebookEdit");
        denied.AddRange(OutsideTools);
        denied.AddRange(permissions.DeniedTools);
        AddToolList(args, "--tools", AvailableTools(request, denied));
        AddToolList(args, "--allowedTools", allowed);
        AddToolList(args, "--disallowedTools", denied);

        foreach (var directory in permissions.AdditionalDirectories)
        {
            args.Add("--add-dir");
            args.Add(CliArgumentGuard.Directory(directory, nameof(request)));
        }

        if (!string.IsNullOrWhiteSpace(request.Agent.Model.Model))
        {
            args.Add("--model");
            args.Add(CliArgumentGuard.Model(request.Agent.Model.Model, nameof(request)));
        }

        // Phase 13 (docs/17 D-25): the effort of the agent, from the closed list, only when it is set and Claude Code takes it
        // (claude --help 2.1.290: low, medium, high, xhigh, max). Any other (a template edited by hand: the domain refuses it,
        // agent.effort-not-for-provider) is not passed, and the CLI uses its own default.
        if (request.Agent.Effort is { } effort && ReasoningEfforts.Accepts(ProviderId.ClaudeCode, effort))
        {
            args.Add("--effort");
            args.Add(ReasoningEfforts.Token(effort));
        }

        args.Add("--max-turns");
        args.Add(budget.MaxTurns.ToString(CultureInfo.InvariantCulture));

        if (budget.MaxCostUsd is { } maxCost)
        {
            args.Add("--max-budget-usd");
            args.Add(maxCost.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(request.OutputJsonSchema))
        {
            // Re-serialized compact: no CR/LF or indentation reaches the command line (control characters are rejected there).
            string compact;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(request.OutputJsonSchema);
                compact = System.Text.Json.JsonSerializer.Serialize(document.RootElement);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ArgumentException("The output JSON schema is not valid JSON: " + ex.Message, nameof(request), ex);
            }

            if (System.Text.Encoding.UTF8.GetByteCount(compact) > MaxJsonSchemaBytes)
            {
                throw new ArgumentException($"The output JSON schema exceeds {MaxJsonSchemaBytes} bytes; Claude Code only accepts it inline in the command line.", nameof(request));
            }

            args.Add("--json-schema");
            args.Add(compact);
        }

        if (!string.IsNullOrWhiteSpace(request.SkillsDirectory))
        {
            args.Add("--plugin-dir");
            args.Add(CliArgumentGuard.Directory(request.SkillsDirectory, nameof(request)));
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            args.Add("--resume");
            args.Add(CliArgumentGuard.SessionId(request.ResumeSessionId, nameof(request)));
        }
        else
        {
            if (!request.PersistSession)
            {
                // A turn of a debate (docs/15 D-16): a new session that Claude Code neither saves nor can resume, so what the
                // participant knows is entirely in its prompt. It keeps its own id, the one the session announces.
                args.Add("--no-session-persistence");
            }

            args.Add("--session-id");
            args.Add(newSessionId);
        }

        if (!string.IsNullOrWhiteSpace(instructionsFile))
        {
            args.Add("--append-system-prompt-file");
            args.Add(instructionsFile);
        }

        return args;
    }

    /// <summary>
    /// The content of the <c>--mcp-config</c> file for <paramref name="servers"/> (docs/14 D-30): each one by its name, a
    /// stdio server with its command and its arguments, and nothing else (no environment, no URL).
    /// </summary>
    /// <exception cref="ArgumentException">A name is not a plain token or repeats, or a command is not an absolute path.</exception>
    public static string McpConfigJson(IReadOnlyList<McpServerSpec> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var config = new System.Text.Json.Nodes.JsonObject();
        foreach (var server in servers)
        {
            ArgumentNullException.ThrowIfNull(server);
            if (!IsMcpToken(server.Name) || !names.Add(server.Name))
            {
                throw new ArgumentException($"The MCP server name '{server.Name}' is not a plain token, or it repeats.", nameof(servers));
            }

            if (string.IsNullOrWhiteSpace(server.Command)
                || server.Command[0] == '-'
                || server.Command.Any(char.IsControl)
                || !Path.IsPathFullyQualified(server.Command))
            {
                throw new ArgumentException($"The command of the MCP server '{server.Name}' must be an absolute path.", nameof(servers));
            }

            if (server.Arguments.Any(a => a is null || a.Any(char.IsControl)))
            {
                throw new ArgumentException($"An argument of the MCP server '{server.Name}' holds a control character.", nameof(servers));
            }

            config[server.Name] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "stdio",
                ["command"] = server.Command,
                ["args"] = new System.Text.Json.Nodes.JsonArray([.. server.Arguments.Select(a => (System.Text.Json.Nodes.JsonNode?)a)]),
            };
        }

        var root = new System.Text.Json.Nodes.JsonObject { ["mcpServers"] = config };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// The allow rules of the tools of the request's MCP servers; none without servers. Only a launch that does not write,
    /// has no shell and no network may have them, and it needs their config file.
    /// </summary>
    private static List<string> McpToolRules(AgentRunRequest request, string? mcpConfigFile)
    {
        if (request.McpServers is not { Count: > 0 } servers)
        {
            return [];
        }

        var permissions = request.Permissions;
        if (permissions.AllowFileWrites || permissions.AllowShell || permissions.AllowNetwork)
        {
            throw new ArgumentException("Local MCP servers are only accepted for a read-only launch: without file writes, shell or network (docs/14 D-30).", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(mcpConfigFile))
        {
            throw new ArgumentException("The MCP servers of the request need their --mcp-config file.", nameof(mcpConfigFile));
        }

        _ = McpConfigJson(servers); // the same checks the file passed
        var rules = new List<string>();
        foreach (var server in servers)
        {
            // The spec carries the allow rules of its tools; each must name a tool of that server, so a server can never
            // pre-approve a built-in tool (Bash, Write...) or the tool of another server.
            var prefix = $"mcp__{server.Name}__";
            foreach (var rule in server.AllowedTools)
            {
                if (rule is null || !rule.StartsWith(prefix, StringComparison.Ordinal) || !IsMcpToken(rule[prefix.Length..]))
                {
                    throw new ArgumentException($"The allow rule '{rule}' of the MCP server '{server.Name}' must be {prefix}<tool>: a tool of that server, a plain token.", nameof(request));
                }

                rules.Add(rule);
            }
        }

        return rules;
    }

    /// <summary>A server or tool name: letters, digits, '_' and '-', not starting with '-', at most 64 characters.</summary>
    private static bool IsMcpToken(string? name) =>
        name is { Length: > 0 and <= 64 } && name[0] != '-' && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    /// <summary>
    /// <c>Tool(//d/folder/**)</c>: the rule of Claude Code for <paramref name="directory"/> and everything below it, from
    /// the root of its drive and with forward slashes, the form Claude Code documents for absolute paths on Windows.
    /// </summary>
    /// <exception cref="ArgumentException">The folder is not an absolute path on a drive that a rule can name exactly.</exception>
    private static string PathRule(string tool, string? directory, string paramName)
    {
        var full = Path.GetFullPath(CliArgumentGuard.Directory(directory, paramName));
        if (full.Length < 4 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
        {
            throw new ArgumentException($"The folder '{directory}' cannot be a path rule: it must be a folder on a drive (X:\\...), not a drive root, a UNC or a device path.", paramName);
        }

        var segments = full[3..].TrimEnd('\\').Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || segment[^1] is ' ' or '.'
                || segment.AsSpan().IndexOfAny(RuleMetacharacters) >= 0
                || segment.Any(char.IsControl))
            {
                throw new ArgumentException($"The folder '{directory}' cannot be a path rule: no segment may be empty, end in a space or a dot, or hold a control character or any of {RuleMetacharacterList}.", paramName);
            }
        }

        return $"{tool}(//{char.ToLowerInvariant(full[0])}/{string.Join('/', segments)}/**)";
    }

    private static string DescribeWriteConfinementLaunch()
    {
        const string Worktree = @"C:\worktree";
        const string WorktreeRule = "//c/worktree";
        const string Skills = @"C:\skills";
        const string SkillsRule = "//c/skills";
        var agent = new AgentDefinition(
            new AgentId("confinement"),
            "confinement",
            AgentRole.Executor,
            new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic),
            string.Empty,
            [],
            [],
            [],
            new ExecutionBudget(1, TimeSpan.FromMinutes(1)),
            1,
            MemoryPolicy.None,
            new ReviewPolicy(false, false));
        // With a schema, as the executor always asks for its report (it makes StructuredOutput available), and with skills,
        // the widest launch (Skill, the plugin folder and its Read rule): a run without skills gets strictly less.
        var request = new AgentRunRequest(agent, string.Empty, Worktree, MeasuredWriteConfinement.Executor, OutputJsonSchema: """{"type":"object"}""", SkillsDirectory: Skills);
        var args = Build(request, "session", null, "settings.json");

        var described = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            if (argument == "--verbose")
            {
                continue;
            }

            if (RunOnlyFlags.Contains(argument))
            {
                i++;
                continue;
            }

            described.Add(argument);
            if (argument == "--settings")
            {
                described.Add(IsolationSettingsJson);
                i++;
            }
        }

        static string Placeholders(string argument) => argument == Skills
            ? "<skills>"
            : argument.Replace(WorktreeRule, "<worktree>", StringComparison.Ordinal).Replace(SkillsRule, "<skills>", StringComparison.Ordinal);
        return string.Join(' ', described.Select(a => "[" + Placeholders(a) + "]"));
    }

    /// <summary>
    /// The built-in tools the session may have at all (<c>--tools</c>; Claude Code 2.1.290 turns every other built-in
    /// tool into a deny rule, and withholds PowerShell too): Read, Grep and Glob always; Edit and Write when the profile
    /// writes; Bash with the shell; WebFetch and WebSearch with the network; <see cref="StructuredOutputTool"/> when a
    /// schema is asked for; <c>Skill</c> when the launch has a skills folder (the materializer makes one only with at
    /// least one skill, and rebuilds every skill header so that none grants tools: while a skill is active, the tools its
    /// <c>allowed-tools</c> names would become allow rules); and any other tool the profile lists, unless the launch
    /// denies the whole tool.
    /// </summary>
    private static List<string> AvailableTools(AgentRunRequest request, IEnumerable<string> denied)
    {
        var permissions = request.Permissions;
        var tools = new List<string> { "Read", "Grep", "Glob" };
        if (permissions.AllowFileWrites)
        {
            tools.AddRange(["Edit", "Write"]);
        }

        if (permissions.AllowShell)
        {
            tools.Add("Bash");
        }

        if (permissions.AllowNetwork)
        {
            tools.AddRange(["WebFetch", "WebSearch"]);
        }

        if (!string.IsNullOrWhiteSpace(request.OutputJsonSchema))
        {
            tools.Add(StructuredOutputTool);
        }

        if (!string.IsNullOrWhiteSpace(request.SkillsDirectory))
        {
            tools.Add(SkillTool);
        }

        foreach (var name in permissions.AllowedTools.Where(t => !IsFileTool(t)).Select(NameOf))
        {
            // 2.1.290 reads preset:default in --tools as every built-in tool.
            if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase) || name.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The tool '{name}' names a preset of Claude Code: it would make every built-in tool available.", nameof(request));
            }

            // Skill goes by the skills folder alone: listed without one, there is nothing to invoke.
            if (!string.Equals(name, SkillTool, StringComparison.OrdinalIgnoreCase))
            {
                tools.Add(name);
            }
        }

        var wholeToolDenies = denied.Where(d => !d.Contains('(', StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. tools.Where(t => !wholeToolDenies.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string NameOf(string tool)
    {
        var open = tool.IndexOf('(', StringComparison.Ordinal);
        return open < 0 ? tool : tool[..open];
    }

    private static bool IsFileTool(string tool) => FileTools.Contains(NameOf(tool));

    // One space-separated argument, as the real captures used: the flag is variadic and would swallow later values.
    private static void AddToolList(List<string> args, string flag, IReadOnlyList<string> tools)
    {
        var distinct = tools.Where(t => !string.IsNullOrEmpty(t)).Select(t => CliArgumentGuard.ToolName(t, flag)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return;
        }

        args.Add(flag);
        args.Add(string.Join(' ', distinct));
    }
}
