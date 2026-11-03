using System.Text;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Providers.Cli.Claude;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Claude;

public sealed class ClaudeCodeArgumentsTests
{
    private const string Session = "11111111-1111-1111-1111-111111111111";

    /// <summary>The path rules of the default working directory of <see cref="ClaudeTestSupport.Request"/> (<c>C:/work</c>).</summary>
    private const string ReadWork = "Read(//c/work/**)";

    private const string EditWork = "Edit(//c/work/**)";

    /// <summary>What every launch denies whatever the profile: the tools that act outside the working directory.</summary>
    private const string OutsideTools = "EnterWorktree ExitWorktree Agent Task";

    /// <summary>The file tools of Claude Code 2.1.290 and the names it still accepts for them.</summary>
    private static readonly string[] FileTools = ["Read", "Grep", "Glob", "Edit", "Write", "NotebookEdit", "MultiEdit", "NotebookRead", "LS"];

    private static string ValueOf(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"missing {flag}");
        return args[index + 1];
    }

    private static string ValueOfOrEmpty(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        return index < 0 ? string.Empty : args[index + 1];
    }

    /// <summary>
    /// How Claude Code 2.1.290 splits one <c>--allowedTools</c> or <c>--disallowedTools</c> value into rules (its
    /// <c>of</c>): at spaces and commas outside parentheses, so a path with spaces stays one rule.
    /// </summary>
    private static List<string> SplitLikeClaude(string value)
    {
        var rules = new List<string>();
        var current = new StringBuilder();
        var inside = false;
        void Flush()
        {
            var rule = current.ToString().Trim();
            if (rule.Length > 0)
            {
                rules.Add(rule);
            }

            current.Clear();
        }

        foreach (var c in value)
        {
            switch (c)
            {
                case '(':
                    inside = true;
                    current.Append(c);
                    break;
                case ')':
                    inside = false;
                    current.Append(c);
                    break;
                case ',' or ' ' when !inside:
                    Flush();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush();
        return rules;
    }

    private static string ToolName(string rule) => rule.IndexOf('(', StringComparison.Ordinal) is var open and >= 0 ? rule[..open] : rule;

    [Fact]
    public void Base_arguments_are_headless_isolated_and_explicit()
    {
        var args = ClaudeCodeArguments.Build(Request(), Session, null);

        Assert.Equal("-p", args[0]);
        Assert.Equal("stream-json", ValueOf(args, "--output-format"));
        Assert.Contains("--verbose", args);
        Assert.Equal(string.Empty, ValueOf(args, "--setting-sources"));
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("haiku", ValueOf(args, "--model"));
        Assert.Equal("4", ValueOf(args, "--max-turns"));
        Assert.Equal(Session, ValueOf(args, "--session-id"));
        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("--max-budget-usd", args);
        Assert.DoesNotContain("--json-schema", args);
        Assert.DoesNotContain("--plugin-dir", args);
        Assert.DoesNotContain("--append-system-prompt-file", args);
        Assert.DoesNotContain("--settings", args);
    }

    public static TheoryData<string> RequestShapes =>
        ["read-only", "writes", "shell-and-network", "resume", "schema", "skills", "instructions", "directories", "budget"];

    private static (AgentRunRequest Request, string? Instructions) Shape(string shape) => shape switch
    {
        "read-only" => (Request(Permissions(allowed: ["Read"])), null),
        "writes" => (Request(Permissions(writes: true)), null),
        "shell-and-network" => (Request(Permissions(writes: true, shell: true, network: true)), null),
        "resume" => (Request(resume: "11111111-2222-3333-4444-555555555555"), null),
        "schema" => (Request(schema: """{"type":"object"}"""), null),
        "skills" => (Request(skills: "C:/sessions/claude-plugin-1"), null),
        "instructions" => (Request(instructions: "Eres el planificador."), "C:/runs/1/instructions.md"),
        "directories" => (Request(Permissions(directories: ["C:/a", "C:/b"])), null),
        "budget" => (Request(budget: new ExecutionBudget(9, TimeSpan.FromMinutes(1), MaxCostUsd: 1m)), null),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory]
    [MemberData(nameof(RequestShapes))]
    public void No_request_makes_claude_read_repository_or_user_settings(string shape)
    {
        var (request, instructions) = Shape(shape);

        var args = ClaudeCodeArguments.Build(request, Session, instructions, "C:/runs/1/settings.json");

        // The repository (project, local) and the user profile are never a setting source: --setting-sources is
        // passed exactly once, with an empty list, and MCP servers only come from --mcp-config (none).
        Assert.Single(args, a => a == "--setting-sources");
        Assert.Equal(string.Empty, ValueOf(args, "--setting-sources"));
        Assert.Single(args, a => a == "--strict-mcp-config");
        Assert.DoesNotContain("--mcp-config", args);
        Assert.Equal("C:/runs/1/settings.json", ValueOf(args, "--settings"));
        Assert.DoesNotContain(args, a => a.Contains("project", StringComparison.Ordinal) || a == "local" || a == "user");
    }

    [Fact]
    public void Isolation_flags_come_before_the_variadic_tool_lists()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(allowed: ["Read"])), Session, null, "C:/runs/1/settings.json").ToList();
        var allowed = args.IndexOf("--allowedTools");
        var denied = args.IndexOf("--disallowedTools");

        // --allowedTools and --disallowedTools are variadic: a flag placed after them could be read as a tool name.
        Assert.True(allowed > 0 && denied > 0);
        foreach (var flag in new[] { "--setting-sources", "--strict-mcp-config", "--settings", "--permission-mode" })
        {
            var index = args.IndexOf(flag);
            Assert.True(index > 0 && index < allowed && index < denied, $"{flag} must come before the tool lists");
        }
    }

    [Theory]
    [InlineData("--setting-sources")]
    [InlineData("--settings")]
    [InlineData("--mcp-config")]
    public void Request_values_cannot_add_a_setting_or_mcp_flag(string value)
    {
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(Permissions(allowed: [value])), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(Permissions(denied: [value])), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(Permissions(directories: [value])), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(model: value), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(skills: value), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(resume: value), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(workingDirectory: value), Session, null));
    }

    // ---- permissions: dontAsk and path rules (ADR-0018, «Medición») --------------------------------------------------

    [Theory]
    [MemberData(nameof(RequestShapes))]
    public void Every_launch_runs_in_dontAsk_and_never_in_acceptEdits_bypass_or_plan(string shape)
    {
        var (request, instructions) = Shape(shape);

        var args = ClaudeCodeArguments.Build(request, Session, instructions);

        // dontAsk denies whatever no rule allows: the path rules are then the whole grant. acceptEdits would accept any
        // edit inside the working directories without a rule, plan changes the model, and bypass skips every check.
        Assert.Equal("dontAsk", ValueOf(args, "--permission-mode"));
        Assert.Single(args, a => a == "--permission-mode");
        Assert.DoesNotContain(args, a => a is "acceptEdits" or "bypassPermissions" or "plan" or "auto");
        Assert.DoesNotContain(args, a => a.StartsWith("--dangerously", StringComparison.Ordinal) || a.StartsWith("--allow-dangerously", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_file_tool_is_ever_allowed_without_a_path(bool writes)
    {
        // A whole-tool rule (a bare name, Tool() or Tool(*)) overrides the tool's own path check: with bare Write, Claude
        // Code 2.1.290 wrote outside its worktree.
        var args = ClaudeCodeArguments.Build(Request(Permissions(writes: writes, allowed: FileTools)), Session, null);

        var rules = SplitLikeClaude(ValueOf(args, "--allowedTools"));
        Assert.All(
            rules.Where(r => FileTools.Contains(ToolName(r), StringComparer.OrdinalIgnoreCase)),
            r => Assert.Matches(@"\A(Read|Edit)\(//[a-z]/[^()*]+/\*\*\)\z", r));
    }

    [Fact]
    public void A_profile_that_writes_may_edit_only_its_working_directory_and_reads_it()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(writes: true, allowed: ["Read", "Grep", "Glob", "Edit", "Write"])), Session, null);

        // Edit rules govern Write, Edit and NotebookEdit; Read rules govern Read, Grep and Glob.
        Assert.Equal([ReadWork, EditWork], SplitLikeClaude(ValueOf(args, "--allowedTools")));
        Assert.Equal(["Bash", "WebFetch", "WebSearch", "NotebookEdit", .. OutsideTools.Split(' ')], SplitLikeClaude(ValueOf(args, "--disallowedTools")));
    }

    [Fact]
    public void A_read_only_profile_gets_no_edit_rule_and_the_write_tools_denied()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(allowed: ["Read", "Grep", "Glob"])), Session, null);

        Assert.Equal([ReadWork], SplitLikeClaude(ValueOf(args, "--allowedTools")));
        Assert.Equal(["Bash", "WebFetch", "WebSearch", "Write", "Edit", "NotebookEdit", .. OutsideTools.Split(' ')], SplitLikeClaude(ValueOf(args, "--disallowedTools")));
    }

    [Fact]
    public void Reads_reach_the_working_directory_the_added_directories_and_the_skills_and_nothing_else()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(writes: true, directories: ["C:/a", "C:/b"]), skills: "C:/skills/claude-plugin-1"), Session, null);

        // An added directory is read, never written: only the working directory gets an Edit rule.
        Assert.Equal(
            [ReadWork, "Read(//c/a/**)", "Read(//c/b/**)", "Read(//c/skills/claude-plugin-1/**)", EditWork],
            SplitLikeClaude(ValueOf(args, "--allowedTools")));
        Assert.Equal(2, args.Count(a => a == "--add-dir"));
    }

    [Fact]
    public void A_working_directory_with_spaces_and_accents_stays_one_rule_for_the_CLI()
    {
        const string Worktree = @"C:\Users\ana\AppData\Local\Temp\as git tést 0123\wt\T1-ab";

        var args = ClaudeCodeArguments.Build(Request(Permissions(writes: true), workingDirectory: Worktree), Session, null);

        Assert.Equal(
            ["Read(//c/Users/ana/AppData/Local/Temp/as git tést 0123/wt/T1-ab/**)", "Edit(//c/Users/ana/AppData/Local/Temp/as git tést 0123/wt/T1-ab/**)"],
            SplitLikeClaude(ValueOf(args, "--allowedTools")));
    }

    [Theory]
    [InlineData(@"C:\work")]
    [InlineData("C:/work")]
    [InlineData(@"c:\work\")]
    [InlineData(@"C:\work\.\src\..")]
    [InlineData(@"C:\otra\..\work")]
    [InlineData(@"C:\work.")] // Windows drops a trailing dot: the folder the process works in is C:\work.
    public void A_working_directory_is_written_from_the_drive_with_forward_slashes_and_no_dot_segments(string workingDirectory)
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(writes: true), workingDirectory: workingDirectory), Session, null);

        Assert.Equal([ReadWork, EditWork], SplitLikeClaude(ValueOf(args, "--allowedTools")));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\servidor\recurso\wt")]
    [InlineData(@"\\?\C:\wt")]
    [InlineData(@"\\.\C:\wt")]
    [InlineData("work")]
    [InlineData(@"C:\a*b")]
    [InlineData(@"C:\a?b")]
    [InlineData(@"C:\a[1]")]
    [InlineData(@"C:\a(1)")]
    [InlineData(@"C:\a{1}")]
    [InlineData(@"C:\a!b")]
    [InlineData(@"C:\a#b")]
    [InlineData(@"C:\a+b")]
    [InlineData(@"C:\a^b")]
    [InlineData(@"C:\a$b")]
    [InlineData("C:\\a\u0007b")]
    [InlineData("C:\\a\u007fb")]
    [InlineData("C:\\a\u0085b")]
    [InlineData(@"C:\a \b")]
    public void A_directory_that_cannot_be_written_as_a_path_rule_is_rejected(string directory)
    {
        // Glob and escape characters would widen or break the rule; a drive root, a UNC or a device path has no rule of
        // its own. Nothing is launched with a grant nobody can read for sure.
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(Permissions(writes: true), workingDirectory: directory), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(Permissions(directories: [directory])), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(skills: directory), Session, null));
    }

    [Theory]
    [InlineData("Edit(//c/**)")]
    [InlineData("Write(C:/Users/**)")]
    [InlineData("Read(~/**)")]
    [InlineData("Grep(*)")]
    [InlineData("Edit()")]
    [InlineData("NotebookEdit(./**)")]
    public void A_file_tool_rule_that_names_its_own_path_is_rejected(string rule)
    {
        // Only the launch decides where the file tools reach; a roster entry cannot widen (or silently narrow) it.
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(Permissions(writes: true, allowed: [rule])), Session, null));
    }

    [Theory]
    [MemberData(nameof(RequestShapes))]
    public void NotebookEdit_and_the_tools_that_act_outside_the_working_directory_are_denied_in_every_launch(string shape)
    {
        var (request, instructions) = Shape(shape);

        var denied = SplitLikeClaude(ValueOf(ClaudeCodeArguments.Build(request, Session, instructions), "--disallowedTools"));

        // EnterWorktree creates a git worktree and a branch in the repository and moves the session there (its own check
        // allows it), and a subagent may run in another directory or mode.
        Assert.Contains("NotebookEdit", denied);
        Assert.Subset(denied.ToHashSet(StringComparer.Ordinal), OutsideTools.Split(' ').ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Fully_open_profile_pre_approves_shell_and_network_besides_the_path_rules()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(writes: true, shell: true, network: true)), Session, null);

        Assert.Equal("dontAsk", ValueOf(args, "--permission-mode"));
        // dontAsk denies tools that are not pre-approved, so "allow" must be explicit.
        Assert.Equal([ReadWork, EditWork, "Bash", "WebFetch", "WebSearch"], SplitLikeClaude(ValueOf(args, "--allowedTools")));
        Assert.Equal(["NotebookEdit", .. OutsideTools.Split(' ')], SplitLikeClaude(ValueOf(args, "--disallowedTools")));
    }

    [Fact]
    public void Allow_shell_alone_pre_approves_only_bash_besides_reading_the_working_directory()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(shell: true)), Session, null);

        Assert.Equal([ReadWork, "Bash"], SplitLikeClaude(ValueOf(args, "--allowedTools")));
        Assert.Equal(["WebFetch", "WebSearch", "Write", "Edit", "NotebookEdit", .. OutsideTools.Split(' ')], SplitLikeClaude(ValueOf(args, "--disallowedTools")));
    }

    [Fact]
    public void Allow_network_alone_pre_approves_only_web_tools_besides_reading_the_working_directory()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(network: true)), Session, null);

        Assert.Equal([ReadWork, "WebFetch", "WebSearch"], SplitLikeClaude(ValueOf(args, "--allowedTools")));
        Assert.Equal(["Bash", "Write", "Edit", "NotebookEdit", .. OutsideTools.Split(' ')], SplitLikeClaude(ValueOf(args, "--disallowedTools")));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void Each_switch_set_to_false_denies_its_tools(bool writes, bool shell, bool network)
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(writes, shell, network)), Session, null);

        var denied = SplitLikeClaude(ValueOfOrEmpty(args, "--disallowedTools"));
        var allowed = SplitLikeClaude(ValueOfOrEmpty(args, "--allowedTools"));
        Assert.Equal(shell, !denied.Contains("Bash"));
        Assert.Equal(network, !denied.Contains("WebFetch"));
        Assert.Equal(writes, !denied.Contains("Write"));
        Assert.Equal(writes, allowed.Contains(EditWork));
    }

    [Fact]
    public void Profile_allowed_and_denied_tools_are_appended()
    {
        var profile = Permissions(shell: true, network: true, writes: true, allowed: ["Read", "Write", "Skill"], denied: ["Bash(rm:*)"]);
        var args = ClaudeCodeArguments.Build(Request(profile), Session, null);

        Assert.Equal([ReadWork, EditWork, "Skill", "Bash", "WebFetch", "WebSearch"], SplitLikeClaude(ValueOf(args, "--allowedTools")));
        Assert.Equal(["NotebookEdit", .. OutsideTools.Split(' '), "Bash(rm:*)"], SplitLikeClaude(ValueOf(args, "--disallowedTools")));
    }

    // ---- the tools that exist at all (--tools) ----------------------------------------------------------------------

    public static TheoryData<string, string> AvailableTools => new()
    {
        { "read-only", "Read Grep Glob" },
        { "writes", "Read Grep Glob Edit Write" },
        { "writes-and-shell", "Read Grep Glob Edit Write Bash" },
        { "network", "Read Grep Glob WebFetch WebSearch" },
        { "schema", "Read Grep Glob StructuredOutput" },
        { "skills", "Read Grep Glob Skill" },
        { "skills-listed", "Read Grep Glob Skill" },
        { "nothing-listed", "Read Grep Glob" },
    };

    [Theory]
    [MemberData(nameof(AvailableTools))]
    public void Only_the_tools_the_profile_needs_exist_in_the_session(string shape, string tools)
    {
        // Every other built-in tool (Monitor, Workflow, the Cron tools, Artifact, Skill...) is withheld by --tools: one
        // that runs a command, starts a background job or schedules work would act outside the path rules.
        var request = shape switch
        {
            "read-only" => Request(Permissions(allowed: ["Read", "Grep", "Glob"])),
            "writes" => Request(Permissions(writes: true, allowed: ["Read", "Grep", "Glob", "Edit", "Write"])),
            "writes-and-shell" => Request(Permissions(writes: true, shell: true, allowed: ["Read", "Edit", "Write", "Bash"])),
            "network" => Request(Permissions(network: true)),
            "schema" => Request(schema: """{"type":"object"}"""),
            // Skill exists only with a skills folder (the materializer gives none without a skill), whose skill files
            // grant nothing: the materializer rebuilds their headers.
            "skills" => Request(skills: "C:/skills/claude-plugin-1"),
            "skills-listed" => Request(Permissions(allowed: ["Read", "Skill"]), skills: "C:/skills/claude-plugin-1"),
            "nothing-listed" => Request(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var args = ClaudeCodeArguments.Build(request, Session, null);

        Assert.Single(args, a => a == "--tools");
        Assert.Equal(tools, ValueOf(args, "--tools"));
    }

    [Theory]
    [InlineData("TodoWrite", "Read Grep Glob TodoWrite")]
    [InlineData("Bash(git log:*)", "Read Grep Glob")]
    [InlineData("WebFetch", "Read Grep Glob")]
    [InlineData("EnterWorktree", "Read Grep Glob")]
    [InlineData("Agent", "Read Grep Glob")]
    [InlineData("Skill", "Read Grep Glob")]
    public void A_tool_the_profile_lists_exists_unless_the_launch_denies_it(string tool, string tools)
    {
        // Shell and network go by their switches, the tools that act outside the working directory are always denied,
        // and Skill goes by the skills folder: without one there is no skill to invoke.
        var args = ClaudeCodeArguments.Build(Request(Permissions(allowed: ["Read", tool])), Session, null);

        Assert.Equal(tools, ValueOf(args, "--tools"));
    }

    [Theory]
    [InlineData("preset:default")]
    [InlineData("Preset:Default")]
    [InlineData("default")]
    public void A_profile_tool_that_would_name_a_preset_is_rejected(string tool)
    {
        // Claude Code 2.1.290 reads preset:default in --tools as every built-in tool: Monitor, Workflow and the rest back.
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(Permissions(allowed: ["Read", tool])), Session, null));
    }

    [Fact]
    public void A_tool_the_profile_denies_is_not_made_available()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(allowed: ["TodoWrite"], denied: ["TodoWrite"])), Session, null);

        Assert.Equal("Read Grep Glob", ValueOf(args, "--tools"));
    }

    [Fact]
    public void The_tool_list_comes_before_the_rule_lists_and_after_the_permission_mode()
    {
        var args = ClaudeCodeArguments.Build(Request(Permissions(writes: true)), Session, null).ToList();

        Assert.True(args.IndexOf("--permission-mode") < args.IndexOf("--tools"));
        Assert.True(args.IndexOf("--tools") < args.IndexOf("--allowedTools"));
        Assert.True(args.IndexOf("--tools") < args.IndexOf("--disallowedTools"));
    }

    [Fact]
    public void The_launch_whose_confinement_was_measured_is_what_the_measured_executor_gets()
    {
        // Build itself makes the fingerprint, so changing a permission flag changes it and turns the relaxation of
        // ADR-0018 off until the new launch is measured.
        // The executor always asks for its report with a schema, and the widest launch has skills: the measured one has both.
        var executor = Request(MeasuredWriteConfinement.Executor, workingDirectory: @"C:\Users\ana\wt\T1", schema: """{"type":"object"}""", skills: @"C:\Users\ana\skills\claude-plugin-1");
        var args = ClaudeCodeArguments.Build(executor, Session, null, "C:/runs/1/settings.json");
        var launch = ClaudeCodeArguments.WriteConfinementLaunch;
        string Placeholders(string value) => value
            .Replace("//c/Users/ana/wt/T1", "<worktree>", StringComparison.Ordinal)
            .Replace("//c/Users/ana/skills/claude-plugin-1", "<skills>", StringComparison.Ordinal)
            .Replace(@"C:\Users\ana\skills\claude-plugin-1", "<skills>", StringComparison.Ordinal);

        Assert.StartsWith("[-p] ", launch, StringComparison.Ordinal);
        Assert.Contains("[--strict-mcp-config]", launch, StringComparison.Ordinal);
        Assert.Contains($"[--settings] [{ClaudeCodeArguments.IsolationSettingsJson}]", launch, StringComparison.Ordinal);
        Assert.Contains("[--tools] [Read Grep Glob Edit Write StructuredOutput Skill]", launch, StringComparison.Ordinal);
        Assert.Contains("[--allowedTools] [Read(<worktree>/**) Read(<skills>/**) Edit(<worktree>/**)]", launch, StringComparison.Ordinal);
        Assert.Contains("[--plugin-dir] [<skills>]", launch, StringComparison.Ordinal);
        foreach (var flag in new[] { "--setting-sources", "--permission-mode", "--tools", "--allowedTools", "--disallowedTools", "--plugin-dir" })
        {
            Assert.Contains($"[{flag}] [{Placeholders(ValueOf(args, flag))}]", launch, StringComparison.Ordinal);
        }

        // What to run (model, turns, schema, session, instructions) is not part of it.
        Assert.DoesNotContain("--model", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("--max-turns", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("--session-id", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("--json-schema", launch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_measured_launch_is_still_the_one_2_1_290_was_measured_with()
    {
        // Hand-copied from the measurement (ADR-0018, fourth measurement): MCP servers (docs/14 D-30) change nothing here.
        Assert.Equal(
            """[-p] [--setting-sources] [] [--strict-mcp-config] [--settings] [{"disableAllHooks":true}] [--permission-mode] [dontAsk] [--tools] [Read Grep Glob Edit Write StructuredOutput Skill] [--allowedTools] [Read(<worktree>/**) Read(<skills>/**) Edit(<worktree>/**)] [--disallowedTools] [Bash WebFetch WebSearch NotebookEdit EnterWorktree ExitWorktree Agent Task] [--plugin-dir] [<skills>]""",
            ClaudeCodeArguments.WriteConfinementLaunch);
    }

    // ---- turns without session persistence (docs/15 D-16) ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(RequestShapes))]
    public void A_session_is_saved_unless_the_request_says_it_must_not_be(string shape)
    {
        var (request, instructions) = Shape(shape);

        var args = ClaudeCodeArguments.Build(request, Session, instructions);

        Assert.True(request.PersistSession);
        Assert.DoesNotContain("--no-session-persistence", args);
    }

    [Fact]
    public void A_turn_that_must_not_be_saved_is_a_new_session_with_no_session_persistence()
    {
        var args = ClaudeCodeArguments.Build(Request() with { PersistSession = false }, Session, null).ToList();

        // A new session with its own id, which Claude Code does not save and cannot resume; never a resumed one.
        Assert.Single(args, a => a == "--no-session-persistence");
        Assert.Equal(Session, ValueOf(args, "--session-id"));
        Assert.DoesNotContain("--resume", args);
        Assert.Equal(args.IndexOf("--session-id") - 1, args.IndexOf("--no-session-persistence"));
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    [InlineData("--dangerously-skip-permissions")]
    public void A_turn_that_must_not_be_saved_and_resumes_a_session_is_refused(string resume)
    {
        // Not saving and resuming exclude each other: «sessions will not be saved to disk and cannot be resumed».
        var request = Request(resume: resume) with { PersistSession = false };

        var refused = Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(request, Session, null));

        Assert.Contains("--no-session-persistence", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_turn_of_a_debate_participant_reads_only_its_copy_and_its_turn_folder_and_is_not_saved()
    {
        // D-09: the fixed read-only profile of a participant, the copy of the commit as working directory and the folder of
        // its turn (debate.json) as an additional directory, its contribution through a schema, and no persistence (D-16).
        const string Copy = @"C:\Users\ana\AppData\Local\AgentStudio\debates\0a1b2c3d\a1b2c3\snapshot";
        const string Turn = @"C:\Users\ana\AppData\Local\AgentStudio\debates\0a1b2c3d\a1b2c3\turns\r1-arquitecto-1";
        var request = Request(
            Permissions(allowed: ["Read", "Grep", "Glob"], directories: [Turn]),
            budget: new ExecutionBudget(12, TimeSpan.FromMinutes(5), MaxCostUsd: 0.5m),
            schema: """{"type":"object"}""",
            workingDirectory: Copy) with { PersistSession = false };

        var args = ClaudeCodeArguments.Build(request, Session, "C:/runs/1/instructions.md", "C:/runs/1/settings.json");

        Assert.Equal(
            [
                "-p", "--output-format", "stream-json", "--verbose", "--setting-sources", string.Empty, "--strict-mcp-config",
                "--settings", "C:/runs/1/settings.json", "--permission-mode", "dontAsk",
                "--tools", "Read Grep Glob StructuredOutput",
                "--allowedTools", "Read(//c/Users/ana/AppData/Local/AgentStudio/debates/0a1b2c3d/a1b2c3/snapshot/**) Read(//c/Users/ana/AppData/Local/AgentStudio/debates/0a1b2c3d/a1b2c3/turns/r1-arquitecto-1/**)",
                "--disallowedTools", "Bash WebFetch WebSearch Write Edit NotebookEdit EnterWorktree ExitWorktree Agent Task",
                "--add-dir", Turn,
                "--model", "haiku", "--max-turns", "12", "--max-budget-usd", "0.5",
                "--json-schema", """{"type":"object"}""",
                "--no-session-persistence", "--session-id", Session,
                "--append-system-prompt-file", "C:/runs/1/instructions.md",
            ],
            args);
    }

    [Fact]
    public void The_measured_launch_of_a_writing_executor_never_has_a_turn_without_persistence()
    {
        // A writing executor never asks for a turn that is not saved: only the read-only participants of a debate do. The
        // measured launch is built from the executor's request, so it does not change (the test above pins its text).
        var executor = Request(MeasuredWriteConfinement.Executor, workingDirectory: @"C:\Users\ana\wt\T1", schema: """{"type":"object"}""", skills: @"C:\Users\ana\skills\claude-plugin-1");

        Assert.True(executor.PersistSession);
        Assert.DoesNotContain("--no-session-persistence", ClaudeCodeArguments.Build(executor, Session, null, "C:/runs/1/settings.json"));
        Assert.DoesNotContain("no-session-persistence", ClaudeCodeArguments.WriteConfinementLaunch, StringComparison.Ordinal);
    }

    // ---- local MCP servers (docs/14 D-30) --------------------------------------------------------------------------

    /// <summary>As the assisted recommendation (WP4) builds it: the spec carries the allow rules of its tools.</summary>
    private static readonly McpServerSpec SkillsServer = new(
        "agentstudio",
        @"C:\Program Files\AgentStudio\AgentStudio.SkillsMcp.exe",
        ["--snapshot", @"C:\runs\aux\snapshot.json"],
        ["mcp__agentstudio__search_skills", "mcp__agentstudio__recommend_skills"]);

    private const string McpConfig = @"C:\runs\aux\mcp.json";

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Mcp_servers_are_refused_for_a_launch_that_writes_has_a_shell_or_has_network(bool writes, bool shell, bool network)
    {
        var request = Request(Permissions(writes: writes, shell: shell, network: network)) with { McpServers = [SkillsServer] };

        var refused = Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(request, Session, null, "C:/runs/1/settings.json", McpConfig));
        Assert.Contains("read-only", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_measured_executor_can_never_get_an_mcp_server()
    {
        var executor = Request(MeasuredWriteConfinement.Executor, schema: """{"type":"object"}""") with { McpServers = [SkillsServer] };

        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(executor, Session, null, "C:/runs/1/settings.json", McpConfig));
    }

    [Fact]
    public void A_read_only_launch_loads_only_the_given_servers_and_preapproves_exactly_their_tools()
    {
        var request = Request(Permissions(allowed: ["Read", "Grep", "Glob"]), schema: """{"type":"object"}""") with { McpServers = [SkillsServer] };

        var args = ClaudeCodeArguments.Build(request, Session, null, "C:/runs/1/settings.json", McpConfig).ToList();

        // --strict-mcp-config stays: with --mcp-config only these servers load, not .mcp.json nor the claude.ai connectors.
        var strict = args.IndexOf("--strict-mcp-config");
        Assert.Equal(["--strict-mcp-config", "--mcp-config", McpConfig], args.Skip(strict).Take(3));
        Assert.Single(args, a => a == "--mcp-config");
        Assert.True(args.IndexOf("--mcp-config") < args.IndexOf("--allowedTools"), "a variadic flag must not swallow the tool lists");
        var allowed = SplitLikeClaude(ValueOf(args, "--allowedTools"));
        Assert.Equal(["mcp__agentstudio__search_skills", "mcp__agentstudio__recommend_skills"], allowed.Where(a => a.StartsWith("mcp__", StringComparison.Ordinal)));

        // --tools only narrows the built-in tools: the server's tools are not there, and nothing else is added.
        Assert.Equal("Read Grep Glob StructuredOutput", ValueOf(args, "--tools"));
        Assert.Contains("Write", SplitLikeClaude(ValueOf(args, "--disallowedTools")));
    }

    [Fact]
    public void Mcp_servers_without_their_config_file_are_refused()
    {
        var request = Request() with { McpServers = [SkillsServer] };

        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(request, Session, null, "C:/runs/1/settings.json"));
    }

    [Theory]
    [InlineData("agent studio", "mcp__agent studio__search_skills")]
    [InlineData("-x", "mcp__-x__search_skills")]
    [InlineData("agentstudio", "mcp__agentstudio__search skills")]
    [InlineData("agentstudio", "mcp__agentstudio__a(b)")]
    [InlineData("agentstudio", "mcp__agentstudio__")]
    [InlineData("agentstudio", "")]
    public void A_server_name_or_allow_rule_that_is_not_a_plain_token_is_refused(string server, string rule)
    {
        var request = Request() with { McpServers = [SkillsServer with { Name = server, AllowedTools = [rule] }] };

        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(request, Session, null, "C:/runs/1/settings.json", McpConfig));
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("Write")]
    [InlineData("search_skills")]
    [InlineData("mcp__otro__search_skills")]
    [InlineData("mcp__agentstudio")]
    public void A_server_can_only_preapprove_tools_of_its_own_never_a_built_in_tool_or_another_servers(string rule)
    {
        var request = Request() with { McpServers = [SkillsServer with { AllowedTools = [rule] }] };

        var refused = Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(request, Session, null, "C:/runs/1/settings.json", McpConfig));
        Assert.Contains("mcp__agentstudio__", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_config_file_of_the_servers_names_each_one_with_its_command_and_arguments_and_nothing_else()
    {
        var json = ClaudeCodeArguments.McpConfigJson([SkillsServer]);

        Assert.Equal(
            """{"mcpServers":{"agentstudio":{"type":"stdio","command":"C:\\Program Files\\AgentStudio\\AgentStudio.SkillsMcp.exe","args":["--snapshot","C:\\runs\\aux\\snapshot.json"]}}}""",
            json);
    }

    [Fact]
    public void A_server_command_that_is_not_an_absolute_path_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.McpConfigJson([SkillsServer with { Command = "AgentStudio.SkillsMcp.exe" }]));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.McpConfigJson([SkillsServer, SkillsServer]));
    }

    // ---- the rest of the launch ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("-x")]
    [InlineData("a b")]
    public void Resume_with_an_unsafe_session_id_is_rejected(string id)
    {
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(resume: id), Session, null));
    }

    [Fact]
    public void Resume_with_a_valid_uuid_is_passed()
    {
        var args = ClaudeCodeArguments.Build(Request(resume: "11111111-2222-3333-4444-555555555555"), Session, null);

        Assert.Equal("11111111-2222-3333-4444-555555555555", ValueOf(args, "--resume"));
    }

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("haiku --foo")]
    [InlineData("-m")]
    public void Unsafe_model_names_are_rejected(string model)
    {
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(model: model), Session, null));
    }

    [Theory]
    [InlineData("haiku")]
    [InlineData("claude-haiku-4-5-20251001")]
    [InlineData("opus[1m]")]
    public void Real_model_names_are_accepted(string model)
    {
        Assert.Equal(model, ValueOf(ClaudeCodeArguments.Build(Request(model: model), Session, null), "--model"));
    }

    [Theory]
    [InlineData("--evil")]
    [InlineData("relative/dir")]
    [InlineData("-C:/x")]
    public void Directories_must_be_absolute_and_not_look_like_flags(string directory)
    {
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(Permissions(directories: [directory])), Session, null));
        Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(skills: directory), Session, null));
    }

    [Fact]
    public void A_schema_over_the_size_limit_is_rejected_with_a_clear_error()
    {
        var schema = "{\"description\":\"" + new string('x', ClaudeCodeArguments.MaxJsonSchemaBytes) + "\"}";

        var error = Assert.Throws<ArgumentException>(() => ClaudeCodeArguments.Build(Request(schema: schema), Session, null));

        Assert.Contains("exceeds", error.Message);
    }

    [Fact]
    public void Additional_directories_budget_schema_skills_and_instructions_are_translated()
    {
        var budget = new ExecutionBudget(7, TimeSpan.FromMinutes(1), MaxCostUsd: 0.5m);
        var request = Request(
            Permissions(directories: ["C:/a", "C:/b"]),
            budget: budget,
            schema: """{"type":"object"}""",
            skills: "C:/skills");
        var args = ClaudeCodeArguments.Build(request, Session, "C:/tmp/sys.md");

        var list = args.ToList();
        Assert.Equal(2, list.Count(a => a == "--add-dir"));
        Assert.Equal("C:/a", args[list.IndexOf("--add-dir") + 1]);
        Assert.Equal("7", ValueOf(args, "--max-turns"));
        Assert.Equal("0.5", ValueOf(args, "--max-budget-usd"));
        Assert.Equal("""{"type":"object"}""", ValueOf(args, "--json-schema"));
        Assert.Equal("C:/skills", ValueOf(args, "--plugin-dir"));
        Assert.Equal("C:/tmp/sys.md", ValueOf(args, "--append-system-prompt-file"));
    }

    [Fact]
    public void Resume_replaces_the_new_session_id()
    {
        var args = ClaudeCodeArguments.Build(Request(resume: "abc-123"), Session, null);

        Assert.Equal("abc-123", ValueOf(args, "--resume"));
        Assert.DoesNotContain("--session-id", args);
    }

    [Fact]
    public void Model_is_omitted_when_the_agent_has_none()
    {
        var args = ClaudeCodeArguments.Build(Request(model: null), Session, null);

        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void Prompt_never_appears_in_the_arguments()
    {
        const string Prompt = "SECRETO-PROMPT con \"comillas\" & metacaracteres | > <";
        var args = ClaudeCodeArguments.Build(Request(prompt: Prompt, instructions: "SECRETO-INSTRUCCIONES"), Session, "C:/tmp/sys.md");

        Assert.DoesNotContain(args, a => a.Contains("SECRETO", StringComparison.Ordinal));
    }
}
